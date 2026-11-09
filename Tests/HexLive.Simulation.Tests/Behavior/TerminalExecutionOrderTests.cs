using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;
public sealed class TerminalExecutionOrderTests
{
    [TestCase(ExecutionStatus.Completed)]
    [TestCase(ExecutionStatus.Failed)]
    public void TheNextOrdinaryPickupStartsAfterTerminalPlanting(ExecutionStatus previous)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        foreach (var colonist in world.Entities.Npcs.Values)
            engine.ApplyManualCommand(new SetManualControlCommand(colonist.Id, true));
        engine.Step();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Clear();
        npc.Needs.Stamina = 1f;
        npc.Execution.Status = previous;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = world.Tick - 15;
        npc.Execution.EndTick = world.Tick - 1;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        var anchor = SpatialQueries.GetPassableNeighbors(world, npc.CurrentJunction.Value)
            .First(j => world.Entities.Objects.Values.All(o => !o.Junctions.Contains(j)));
        var tile = world.Junctions.Items[anchor].Tiles[0];
        var item = WorldObjectMutations.SpawnObject(world, ContentIds.Stick, npc.Fragment, tile, anchor);
        var accepted = engine.ApplyManualCommand(new InteractCommand(npc.Id, item.Id, InteractionType.PickUp));
        Assert.That(accepted.Accepted, Is.True, accepted.Reason);
        for (var i=0; i<400 && world.Entities.Objects.ContainsKey(item.Id); i++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(item.Id), Is.False);
        Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.Stick), Is.True);
    }
}
