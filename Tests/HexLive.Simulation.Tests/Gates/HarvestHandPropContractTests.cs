using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§44 / §84: manual harvest exports the blade selected by its target.</summary>
public sealed class HarvestHandPropContractTests
{
    [TestCase("plant.yucca")]
    [TestCase("herb.bush")]
    public void ManualBladeHarvestExportsKnifeEvenWhenGoalIsPlayerOrder(string definitionId)
    {
        var world = TestWorld.CreateWorld(249);
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        var here = npc.CurrentJunction ?? world.Junctions.Items.Keys.First();
        var tile = world.Junctions.Items[here].Tiles[0];
        var target = WorldObjectMutations.SpawnObject(
            world, definitionId, new FragmentId(1), tile, here);
        npc.Execution.CurrentInteraction = InteractionType.Harvest;
        npc.Execution.TargetObject = target.Id;

        var snapshot = WorldSnapshotExporter.Export(world).Npcs.Single(candidate =>
            candidate.Id.Value == npc.Id.Value);

        Assert.That(snapshot.HeldItemId, Is.EqualTo(ContentIds.Knife));
    }

    [Test]
    public void HerbHarvestRemainsLegalWithoutBladeAndExportsEmptyHand()
    {
        var world = TestWorld.CreateWorld(2491);
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        var here = npc.CurrentJunction ?? world.Junctions.Items.Keys.First();
        var tile = world.Junctions.Items[here].Tiles[0];
        var target = WorldObjectMutations.SpawnObject(
            world, "herb.bush", new FragmentId(1), tile, here);
        npc.Execution.CurrentInteraction = InteractionType.Harvest;
        npc.Execution.TargetObject = target.Id;

        var snapshot = WorldSnapshotExporter.Export(world).Npcs.Single(candidate =>
            candidate.Id.Value == npc.Id.Value);

        Assert.That(snapshot.HeldItemId, Is.Empty);
    }

    [Test]
    public void ManualCoconutProcessExportsKnifeEvenWhenGoalIsPlayerOrder()
    {
        var world = TestWorld.CreateWorld(272);
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        var here = npc.CurrentJunction ?? world.Junctions.Items.Keys.First();
        var tile = world.Junctions.Items[here].Tiles[0];
        var coconut = WorldObjectMutations.SpawnObject(
            world, ContentIds.Coconut, new FragmentId(1), tile, here);
        npc.Execution.CurrentInteraction = InteractionType.Process;
        npc.Execution.TargetObject = coconut.Id;

        var snapshot = WorldSnapshotExporter.Export(world).Npcs.Single(candidate =>
            candidate.Id.Value == npc.Id.Value);

        Assert.That(snapshot.HeldItemId, Is.EqualTo(ContentIds.Knife));
    }
}
