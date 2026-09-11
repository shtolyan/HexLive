namespace HexLive.AgentHost;

public sealed partial class MashaMemoryStore
{
    private bool _executionPersistenceFaulted;

    // Called before any side effects and again inside the same commit as AppliedTurnIds.
    private AgentExecutionPlan? ProjectExecutionPlan(MashaWorldHandle world,
        CompanionDecision decision, AgentObjective? objective)
    {
        var current = _archive.ExecutionPlan;
        var update = decision.ExecutionPlanUpdate;
        if (_executionPersistenceFaulted && (update != null || decision.Action != null))
            throw new IOException("ExecutionPersistenceUnavailable");
        if ((update != null || decision.Action != null || decision.ObjectiveUpdate != null) &&
            world.ExecutionPlanRevision is { } expected &&
            (expected != (current?.Revision ?? 0) || world.ExecutionPlanId != (current?.Id ?? "")))
            throw new AgentObjectiveConflictException();
        if (update != null && (world.ExecutionPlanRevision == null || world.ExecutionPlanId == null))
            throw new AgentObjectiveConflictException();
        if (AgentExecutionPlanPolicy.CompletesWithPendingWork(decision, current))
            throw new InvalidDataException("ExecutionPlanNotCompleted");

        if (current is { Status: "active" or "paused" } && decision.ObjectiveUpdate is { } goalUpdate)
        {
            current = goalUpdate.Operation switch
            {
                "set" or "clear" or "complete" => AgentExecutionPlanPolicy.Cancel(current, current.Revision),
                "pause" when current.Status == "active" => AgentExecutionPlanPolicy.Pause(current, current.Revision, "ObjectivePaused"),
                _ => current
            };
            if (goalUpdate.Operation is "pause" or "resume")
                current = current with { ObjectiveRevision = objective!.Revision };
        }
        if (update == null)
        {
            if (decision.Action != null && current is { Status: "active" })
                throw new InvalidDataException("ExplicitExecutionPlanUpdateRequired");
            return current;
        }
        AgentExecutionPlanPolicy.ValidateUpdate(update);
        var episode = RequireEpisode(world.EpisodeId);
        if (update.Operation == "replace")
        {
            if (decision.Action != null || objective == null ||
                current is { Command: { Status: not "failed" }, Status: not "canceled" })
                throw new InvalidDataException("ExecutionPlanReplacementBlocked");
            return AgentExecutionPlanPolicy.Create(objective, episode.WorldKey, episode.AvatarNpcId, update.Steps);
        }
        if (current == null) throw new InvalidDataException("ExecutionPlanMissing");
        return update.Operation switch
        {
            "pause" when current.Status == "active" => AgentExecutionPlanPolicy.Pause(current, current.Revision, update.Reason),
            "pause" when current.Status == "paused" => current,
            "resume" when objective?.Status == "active" && decision.Action == null =>
                AgentExecutionPlanPolicy.Resume(current, current.Revision, episode.WorldKey, episode.AvatarNpcId, objective.Revision),
            "cancel" when current.Status == "canceled" => current,
            "cancel" => AgentExecutionPlanPolicy.Cancel(current, current.Revision),
            _ => throw new InvalidDataException("InvalidExecutionPlanTransition")
        };
    }

    public Task<AgentExecutionPlan> PrepareExecutionStepAsync(string planId, long revision,
        MashaWorldHandle world, CancellationToken token, long sequence = 0) => MutateExecutionPlanAsync(planId, revision, plan =>
    {
        var episode = RequireEpisode(world.EpisodeId);
        if (_archive.Objective is not { Status: "active" } goal)
            throw new InvalidOperationException("ExecutionObjectiveInactive");
        var next = AgentExecutionPlanPolicy.Prepare(plan, revision, episode.WorldKey, episode.AvatarNpcId, goal.Revision);
        return next with { Command = next.Command! with { Sequence = sequence } };
    }, token);

    public Task<AgentExecutionPlan> PauseExecutionPlanAsync(string planId, long revision, string reason,
        CancellationToken token) => MutateExecutionPlanAsync(planId, revision,
            plan => AgentExecutionPlanPolicy.Pause(plan, revision, reason), token);

    public Task<AgentExecutionPlan> BranchExecutionPlanAsync(string planId, long revision,
        CancellationToken token) => MutateExecutionPlanAsync(planId, revision,
            plan => AgentExecutionPlanPolicy.Branch(plan, revision), token);

    public Task<AgentExecutionPlan> ObserveExecutionStepAsync(string planId, long revision,
        AgentExecutionReceipt receipt, CancellationToken token) => MutateExecutionPlanAsync(planId, revision,
            plan =>
            {
                var next = AgentExecutionPlanPolicy.Observe(plan, revision, receipt);
                if (receipt.Outcome == "completed" && plan.Reason == "CommandOutcomeUnknown" &&
                    next.Status == "paused" && next.Command == null &&
                    _archive.Objective is { Status: "active" } objective &&
                    objective.WorldKey == next.WorldKey && objective.AvatarNpcId == next.NpcId &&
                    objective.Revision == next.ObjectiveRevision)
                    next = AgentExecutionPlanPolicy.Resume(next, next.Revision, next.WorldKey, next.NpcId, objective.Revision);
                return next;
            }, token);

    public Task<AgentExecutionPlan> RecoverExecutionPlanAsync(string planId, long revision,
        CancellationToken token) => MutateExecutionPlanAsync(planId, revision, AgentExecutionPlanPolicy.Recover, token);

    private async Task<AgentExecutionPlan> MutateExecutionPlanAsync(string planId, long revision,
        Func<AgentExecutionPlan, AgentExecutionPlan> change, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_executionPersistenceFaulted) throw new IOException("ExecutionPersistenceUnavailable");
            var current = _archive.ExecutionPlan;
            if (current == null || current.Id != planId || current.Revision != revision)
                throw new AgentObjectiveConflictException();
            var next = change(current);
            if (!ReferenceEquals(next, current))
            {
                if (current.Command is { } completed && next.Command == null &&
                    (next.Cursor > current.Cursor || next.Iteration > current.Iteration))
                {
                    // Only the command-specific completed receipt makes this transition.
                    // A condition branch has no command and must not invent completed work.
                    _archive.ExecutionProgress.Add(new(current.WorldKey, current.NpcId, current.Id,
                        completed.Id, completed.Sequence, current.Steps[current.Cursor] with
                            { Arguments = current.Steps[current.Cursor].Arguments.Clone() }, DateTimeOffset.UtcNow));
                    if (_archive.ExecutionProgress.Count > 64)
                        _archive.ExecutionProgress.RemoveRange(0, _archive.ExecutionProgress.Count - 64);
                }
                _archive.ExecutionPlan = next;
                try { await SaveAsync(token).ConfigureAwait(false); }
                catch
                {
                    // The atomic rename may have succeeded before a derived view failed.
                    // Never roll back to a dispatchable in-memory state or send without a successful save.
                    _executionPersistenceFaulted = true;
                    throw;
                }
            }
            return CloneExecutionPlan(next);
        }
        finally { _gate.Release(); }
    }

    private static AgentExecutionPlan CloneExecutionPlan(AgentExecutionPlan plan) => plan with
        { Steps = plan.Steps.Select(step => step with { Arguments = step.Arguments.Clone() }).ToArray() };
}
