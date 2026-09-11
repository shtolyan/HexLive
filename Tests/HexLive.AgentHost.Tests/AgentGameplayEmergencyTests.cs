using System.Text.Json;
using System.Reflection;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed partial class AgentExecutionRuntimeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RestoredPausedQueueAcquiresControlOnlyForItsBoundWorld(bool differentWorld)
    {
        using var host = Host();
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options();
        using var providers = new NoModel();
        var original = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(original, mcp);
        await store.PauseExecutionPlanAsync(plan.Id, plan.Revision, "Emergency", default);
        var restored = new AgentHostRuntime(options, providers);
        typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(restored, differentWorld ? world with { WorldKey = "different-world" } : world);
        var maintain = typeof(AgentHostRuntime).GetMethod("MaintainPlanLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)maintain.Invoke(restored, [mcp, 901, true, CancellationToken.None])!;
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.EqualTo(!differentWorld));
        await (Task)maintain.Invoke(restored, [mcp, 901, false, CancellationToken.None])!;
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.False);
        Assert.That(providers.Calls, Is.Zero);
    }

    [TestCase("pause")]
    [TestCase("clear")]
    public async Task EmergencyQueuePauseKeepsNativeControlUntilTheGoalIsPausedOrCleared(string operation)
    {
        using var host = Host();
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options();
        using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp);
        var before = await store.SnapshotAsync(default);
        world = world with { ObjectiveRevision = before.Objective!.Revision,
            ExecutionPlanRevision = plan.Revision, ExecutionPlanId = plan.Id };
        typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, world);
        await store.CommitTurnAsync(world, "interrupt-queue", "heartbeat", new CompanionDecision
        { ExecutionPlanUpdate = new() { Operation = "pause", Reason = "Emergency" } }, default);
        await RunGameplayEmergencyAsync(runtime, mcp, new CompanionAction
        { Tool = "self_action", Arguments = JsonSerializer.SerializeToElement(new { kind = "TreatSelf" }) },
            CancellationToken.None, "emergency-refusal");
        Assert.That(GameplayActionFeedback(runtime), Does.Contain("result=NotNeeded"));
        var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        host.Read(w => { for (var i = 0; i < 32; i++) engine.Step(); return true; });
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.True,
            "An emergency refusal must not hand the paused queue to native AI.");
        var saved = await store.SnapshotAsync(default);
        Assert.That(saved.Objective!.Status, Is.EqualTo("active"));
        Assert.That(saved.ExecutionPlan!.Status, Is.EqualTo("paused"));
        Assert.That(saved.ExecutionPlan.Cursor, Is.Zero);
        await store.CommitTurnAsync(world with { ExecutionPlanRevision = saved.ExecutionPlan.Revision },
            "player-stops-goal", "voice", new CompanionDecision
            { ObjectiveUpdate = new() { Operation = operation, Reason = "Player request" } }, default);
        await (Task)typeof(AgentHostRuntime).GetMethod("MaintainPlanLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [mcp, 901, true, CancellationToken.None])!;
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.False);
        Assert.That(providers.Calls, Is.Zero);
    }

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
