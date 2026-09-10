using System.Text.Json;

namespace HexLive.AgentHost;

public sealed partial class AgentHostRuntime
{
    private volatile bool _executingPlan;
    private int _planNeedsDecision;
    private volatile string _lastExecutionAttempt = "";

    private async Task ValidateExecutionPlanAsync(McpClient mcp, int npcId,
        CompanionDecision decision, CancellationToken token)
    {
        if (decision.ExecutionPlanUpdate is not { Operation: "replace" } update) return;
        var tools = await mcp.ReadToolCatalogAsync(token).ConfigureAwait(false);
        var names = tools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        if (!names.Contains("execute_agent_command") || !names.Contains("read_agent_command"))
            throw new AgentActionValidationException("ServerExecutionPlanUnavailable");
        foreach (var step in update.Steps)
            await ValidateActionAsync(mcp, npcId, new() { Tool = step.Tool, Arguments = step.Arguments }, token).ConfigureAwait(false);
    }

    private async Task TryStartExecutionPlanAsync(McpClient mcp, int npcId, CancellationToken token)
    {
        if (!_actionTask.IsCompleted || _executingPlan) return;
        var archive = await _memory.SnapshotAsync(token).ConfigureAwait(false);
        var plan = archive.ExecutionPlan;
        var world = Volatile.Read(ref _historyWorld);
        if (plan == null || world == null || plan.WorldKey != world.WorldKey || plan.NpcId != npcId ||
            archive.Objective?.Status != "active" || archive.Objective.Revision != plan.ObjectiveRevision ||
            plan.Status is "completed" or "canceled" || plan.Command?.Status == "failed" ||
            (plan.Status == "paused" && plan.Command == null) ||
            _lastExecutionAttempt == plan.Id + ":" + plan.Revision) return;
        _lastExecutionAttempt = plan.Id + ":" + plan.Revision;
        _actionStop?.Dispose();
        _actionStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _executingPlan = true;
        _actionTask = RunExecutionPlanAsync(mcp, npcId, plan.Id, world, _actionStop.Token);
    }

    private async Task RunExecutionPlanAsync(McpClient mcp, int npcId, string planId,
        MashaWorldHandle world, CancellationToken token)
    {
        bool acquired = false;
        string turn = planId;
        try
        {
            var initial = (await _memory.SnapshotAsync(token).ConfigureAwait(false)).ExecutionPlan;
            if (initial?.Id != planId) return;
            if (initial.Status == "active")
            {
                await mcp.CallToolAsync("acquire_npc_control", new { npcId, ttlSeconds = 45 }, token).ConfigureAwait(false);
                acquired = true;
            }
            _diagnostics.Record("plan.started", planId);
            while (!token.IsCancellationRequested)
            {
                var plan = (await _memory.SnapshotAsync(token).ConfigureAwait(false)).ExecutionPlan;
                if (plan == null || plan.Id != planId || plan.Status is "completed" or "canceled" || plan.Command?.Status == "failed") break;
                if (plan.Status == "paused" && plan.Command == null) break;
                var step = plan.Steps[plan.Cursor];
                turn = planId + ":" + step.Id;
                if (plan.Command == null)
                {
                    var before = await mcp.CallToolAsync("describe_colonist", new { npcId }, token).ConfigureAwait(false);
                    _diagnostics.Record("step.before", turn, tool: step.Tool, observation: AgentDiagnosticObservation.From(before));
                    if (step.Condition is { } condition && !AgentExecutionPlanPolicy.ConditionSatisfied(condition, before))
                    {
                        plan = await _memory.BranchExecutionPlanAsync(plan.Id, plan.Revision, token).ConfigureAwait(false);
                        _diagnostics.Record("step.condition", turn, tool: step.Tool, result: plan.Reason);
                        if (plan.Status != "active") break;
                        continue;
                    }
                    var action = new CompanionAction { Tool = step.Tool, Arguments = step.Arguments };
                    var arguments = await ValidateActionAsync(mcp, npcId, action, token).ConfigureAwait(false);
                    var head = await mcp.CallToolAsync("read_agent_command", new { npcId, sequence = 0, commandId = "" }, token).ConfigureAwait(false);
                    var sequence = checked(head.GetProperty("highestSequence").GetInt64() + 1);
                    plan = await _memory.PrepareExecutionStepAsync(plan.Id, plan.Revision, world, token, sequence).ConfigureAwait(false);
                    _diagnostics.Record("step.sending", turn, tool: step.Tool);
                    var receipt = await mcp.CallToolAsync("execute_agent_command", new
                        { npcId, sequence, commandId = plan.Command!.Id, tool = step.Tool, arguments }, token).ConfigureAwait(false);
                    plan = await ApplyExecutionReceiptAsync(plan, receipt, token).ConfigureAwait(false);
                }
                var renew = DateTimeOffset.UtcNow.AddSeconds(9);
                while (plan.Command is { Status: "sending" or "accepted" or "unknown" } command && !token.IsCancellationRequested)
                {
                    if (command.Sequence <= 0) throw new InvalidDataException("ExecutionSequenceMissing");
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    var current = (await _memory.SnapshotAsync(token).ConfigureAwait(false)).ExecutionPlan;
                    if (current == null || current.Id != planId || current.Status == "canceled") return;
                    plan = current;
                    var receipt = await mcp.CallToolAsync("read_agent_command",
                        new { npcId, sequence = command.Sequence, commandId = command.Id }, token).ConfigureAwait(false);
                    plan = await ApplyExecutionReceiptAsync(plan, receipt, token).ConfigureAwait(false);
                    if (plan.Command?.Status == "unknown") break; // Ask for a decision; never guess or replay.
                    if (acquired && DateTimeOffset.UtcNow >= renew)
                    {
                        await mcp.CallToolAsync("acquire_npc_control", new { npcId, ttlSeconds = 45 }, token).ConfigureAwait(false);
                        renew = DateTimeOffset.UtcNow.AddSeconds(9);
                    }
                }
                _diagnostics.Record("step.observed", turn, tool: step.Tool, result: plan.Command?.Status ?? "completed");
                var after = await mcp.CallToolAsync("describe_colonist", new { npcId }, token).ConfigureAwait(false);
                _diagnostics.Record("step.after", turn, tool: step.Tool, observation: AgentDiagnosticObservation.From(after));
                if (plan.Status != "active") break;
            }
        }
        catch (Exception ex)
        {
            var code = ex is OperationCanceledException ? "Cancelled" :
                ex is AgentActionValidationException validation ? validation.ReasonCode : "ExecutionPlanInterrupted";
            _diagnostics.Record("plan.interrupted", turn, result: code);
            // The send might have reached the server. Preserve the same ID/number for reconciliation.
            try
            {
                var plan = (await _memory.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ExecutionPlan;
                if (plan?.Id == planId && plan.Status is "active" or "paused")
                {
                    if (plan.Command != null)
                        await _memory.ObserveExecutionStepAsync(plan.Id, plan.Revision,
                            new(plan.Command.Id, "unknown"), CancellationToken.None).ConfigureAwait(false);
                    else if (plan.Status == "active")
                        await _memory.PauseExecutionPlanAsync(plan.Id, plan.Revision, code, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { /* Persist failure already blocks dispatch in this store. */ }
        }
        finally
        {
            if (acquired && !_handoffActionLease && mcp.HasEstablishedSession)
                try { await mcp.CallToolAsync("release_control", new { npcId }, CancellationToken.None).ConfigureAwait(false); } catch { }
            _executingPlan = false;
            var needsDecision = true;
            try
            {
                var saved = (await _memory.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ExecutionPlan;
                needsDecision = saved?.Id == planId && saved.Status != "canceled";
                if (saved?.Id == planId && saved.Status == "paused") _lastExecutionAttempt = saved.Id + ":" + saved.Revision;
            }
            catch { }
            if (needsDecision) Interlocked.Exchange(ref _planNeedsDecision, 1);
        }
    }

    private async Task<AgentExecutionPlan> ApplyExecutionReceiptAsync(AgentExecutionPlan plan, JsonElement response, CancellationToken token)
    {
        var command = plan.Command ?? throw new InvalidOperationException("ExecutionCommandMissing");
        if (response.GetProperty("sequence").GetInt64() != command.Sequence || response.GetProperty("commandId").GetString() != command.Id)
            throw new InvalidDataException("ExecutionReceiptMismatch");
        var outcome = response.GetProperty("outcome").GetString() ?? "unknown";
        return await _memory.ObserveExecutionStepAsync(plan.Id, plan.Revision, new(command.Id, outcome), token).ConfigureAwait(false);
    }
}
