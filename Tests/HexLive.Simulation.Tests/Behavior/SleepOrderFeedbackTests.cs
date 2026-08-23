using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§49.12 / bug #216: a manual sleep order overrides stale fear and
/// every real refusal names itself immediately.</summary>
public sealed class SleepOrderFeedbackTests
{
    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static void PrepareRestedBody(NPCState npc)
    {
        npc.Needs.Energy = 0.5f;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Mind.AdrenalineUntilTick = 0;
        npc.Perception.Hostiles.Clear();
        npc.Perception.Mobs.Clear();
    }

    [Test]
    public void ManualSleepIgnoresDangerMemoryAfterThreatLeavesPerception()
    {
        var world = TestWorld.CreateWorld(21601);
        var npc = Colonist(world);
        PrepareRestedBody(npc);
        npc.Tile = world.Tiles.Items.Values.First(tile =>
            !tile.Flags.HasFlag(TileFlags.Indoor)).Coord;
        npc.Memory.Dangers.Clear();
        npc.Memory.Dangers.Add(new DangerMemory
        {
            Tile = npc.Tile,
            Tick = world.Tick
        });

        Assert.Multiple(() =>
        {
            Assert.That(
                ExecutionSystem.GetSleepInterruptReason(
                    world, npc, manualOrder: true),
                Is.Null,
                "A direct order must not be cancelled by a threat that is no longer perceived.");
            Assert.That(
                ExecutionSystem.GetSleepInterruptReason(
                    world, npc, manualOrder: false),
                Is.EqualTo("SleepDanger"),
                "Autonomous outdoor caution must retain the existing recent-memory rule.");
        });
    }

    [Test]
    public void VisibleHostileRejectsBedOrderWithTextAndOverheadCue()
    {
        var world = TestWorld.CreateWorld(21602);
        var npc = Colonist(world);
        PrepareRestedBody(npc);
        npc.Mind.ManualControl = true;
        var hostile = world.Entities.Npcs.Values.First(other => other.Id != npc.Id);
        npc.Perception.Hostiles.Add(new PerceivedAgent { Id = hostile.Id });
        var bed = world.Entities.Objects.Values.First(obj =>
            obj.DefinitionId == ContentIds.BedBasic);

        var admission = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, bed.Id, InteractionType.Sleep));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(admission.Reason, Is.EqualTo("SleepDanger"));
            Assert.That(world.Events.Items.Any(e =>
                    e.Type == "ManualOrderRejected" &&
                    e.EntityId == npc.Id.Value &&
                    e.Message.Contains("Reason=SleepDanger")),
                Is.True);
            Assert.That(npc.Execution.LastSocialCueKind,
                Is.EqualTo("SleepRejected:Danger"));
        });
    }

    [TestCase(0.95f, 0f, "SleepHungry", "SleepRejected:Hunger")]
    [TestCase(0f, 0.95f, "SleepThirsty", "SleepRejected:Thirst")]
    public void CriticalNeedRejectsGroundSleepWithSpecificReason(
        float hunger, float thirst, string reason, string cue)
    {
        var world = TestWorld.CreateWorld(21603);
        var npc = Colonist(world);
        PrepareRestedBody(npc);
        npc.Mind.ManualControl = true;
        npc.Needs.Energy = 0.8f;
        npc.Needs.Hunger = hunger;
        npc.Needs.Thirst = thirst;

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.GroundSleep));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(admission.Reason, Is.EqualTo(reason));
            Assert.That(npc.Execution.LastSocialCueKind, Is.EqualTo(cue));
        });
    }
}

}
