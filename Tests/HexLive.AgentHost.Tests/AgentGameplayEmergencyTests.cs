using System.Text.Json;
using HexLive.Server;
using HexLive.Server.Mcp;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed partial class AgentExecutionRuntimeTests
{
    [Test]
    public async Task GameplayEmergencyAdmissionRefusalPreservesGoalAndProvidesNextDecisionFeedback()
    {
        using var host = Host();
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options();
        using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, _) = await Install(runtime, mcp);
        var before = await store.SnapshotAsync(default);
        await store.CommitTurnAsync(world with { ObjectiveRevision = before.Objective!.Revision,
            ExecutionPlanRevision = before.ExecutionPlan!.Revision, ExecutionPlanId = before.ExecutionPlan.Id },
            "pause-for-emergency", "heartbeat", new CompanionDecision
            { ObjectiveUpdate = new() { Operation = "pause", Reason = "Emergency fixture" } }, default);

        // A healthy native body rejects dressing as NotNeeded. The production
        // action boundary must report this refusal, not terminate the scenario.
        await RunGameplayEmergencyAsync(runtime, mcp, new CompanionAction
        { Tool = "self_action", Arguments = JsonSerializer.SerializeToElement(new { kind = "TreatSelf" }) },
            CancellationToken.None, "rejected-treatment");
        Assert.That(GameplayActionFeedback(runtime), Does.Contain("result=NotNeeded"));
        var after = await store.SnapshotAsync(default);
        Assert.That(after.Objective!.Status, Is.EqualTo("paused"));
        Assert.That(after.Objective.Text, Is.EqualTo(before.Objective.Text));
        Assert.That(after.Objective.StartedRevision, Is.EqualTo(before.Objective.StartedRevision));
        Assert.That(providers.Calls, Is.Zero);
    }
}
