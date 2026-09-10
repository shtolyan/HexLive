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
                current is { Command: not null, Status: not "canceled" })
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
        MashaWorldHandle world, CancellationToken token) => MutateExecutionPlanAsync(planId, revision, plan =>
    {
        var episode = RequireEpisode(world.EpisodeId);
        if (_archive.Objective is not { Status: "active" } goal)
            throw new InvalidOperationException("ExecutionObjectiveInactive");
        return AgentExecutionPlanPolicy.Prepare(plan, revision, episode.WorldKey, episode.AvatarNpcId, goal.Revision);
    }, token);

    public Task<AgentExecutionPlan> ObserveExecutionStepAsync(string planId, long revision,
        AgentExecutionReceipt receipt, CancellationToken token) => MutateExecutionPlanAsync(planId, revision,
            plan => AgentExecutionPlanPolicy.Observe(plan, revision, receipt), token);

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
