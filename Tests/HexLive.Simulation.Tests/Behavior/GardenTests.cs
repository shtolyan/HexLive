using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class GardenTests
{
    private static HexLive.Simulation.Core.WorldState InitializedWorld()
    {
        var engine = TestWorld.CreateEngine();
        engine.Step();
        return engine.World;
    }
    [TestCase(ContentIds.YuccaSeed, ContentIds.YuccaSprout)]
    [TestCase(ContentIds.HerbSeed, ContentIds.HerbSprout)]
    public void AutonomousPlantingUsesTheOrdinaryTimedSeedExecution(string seedId, string sproutId)
    {
        var world = InitializedWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var seed = new ItemInstance(seedId);
        npc.Inventory.Items.Add(seed);
        npc.Needs.Stamina = 1f;
        npc.Mind.CurrentGoal = GoalType.TendGarden;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        Assert.That(npc.Plan.Steps.Last().Type, Is.EqualTo(PlanStepType.PlantInventorySeed));
        var target = npc.Plan.TargetJunctionId.Value;
        var home = ColonyQueries.Home(world, npc.Faction).Value;
        Assert.That(HexSpatialMath.HexDistance(npc.Plan.TargetTile.Value, home), Is.InRange(2, 4));
        SpatialMutations.FreeJunction(world, npc.CurrentJunction.Value, npc.Id);
        npc.CurrentJunction = target;
        npc.Position = world.Junctions.Items[target].WorldPosition;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);
        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Plant));
        Assert.That(npc.Inventory.Items.Contains(seed), Is.True);
        world.Tick = npc.Execution.EndTick;
        execution.Run(world);
        Assert.That(npc.Inventory.Items.Contains(seed), Is.False);
        Assert.That(world.Entities.Objects.Values.Any(o => o.DefinitionId == sproutId && o.Junctions.Contains(target)), Is.True);
    }

    [TestCase(ExecutionStatus.Completed)]
    [TestCase(ExecutionStatus.Failed)]
    public void PlantingStartsAfterAnEarlierInteractionHasFinished(ExecutionStatus previous)
    {
        var world = InitializedWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));
        npc.Needs.Stamina = 1f;
        npc.Execution.Status = previous;
        npc.Execution.CurrentInteraction = null;
        npc.Mind.CurrentGoal = GoalType.TendGarden;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        var target = npc.Plan.TargetJunctionId.Value;
        SpatialMutations.FreeJunction(world, npc.CurrentJunction.Value, npc.Id);
        npc.CurrentJunction = target;
        npc.Position = world.Junctions.Items[target].WorldPosition;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);
        new ExecutionSystem().Run(world);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Plant));
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
    }

    [Test]
    public void GrowingPlantsAndPendingClaimsPreventOverplanting()
    {
        var world = InitializedWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var crop = PlantGrowthCatalog.All.Single(p => p.SeedDefinitionId == ContentIds.YuccaSeed);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));
        var home = ColonyQueries.Home(world, npc.Faction).Value;
        foreach (var plant in GardenMath.NearbyPlants(world, home).Where(o =>
                     o.DefinitionId == crop.MatureDefinitionId || crop.StageDefinitionIds.Contains(o.DefinitionId)).ToArray())
            WorldObjectMutations.DespawnObject(world, plant.Id);
        for (var i = 0; i < 9; i++)
        {
            Assert.That(GardenMath.TryPickSpot(world, npc, out var point, out var tile), Is.True);
            WorldObjectMutations.SpawnObject(world, crop.StageDefinitionIds[i % 3], point.Fragment, tile, point.Id);
        }
        Assert.That(GardenMath.Count(world, npc.Faction, crop), Is.EqualTo(9));
        npc.Mind.CurrentGoal = GoalType.TendGarden;
        new PlanningSystem().Run(world);
        Assert.That(GardenMath.Count(world, npc.Faction, crop), Is.EqualTo(10));
        var other = world.Entities.Npcs.Values.First(n => n.Faction == npc.Faction && n.Id != npc.Id);
        other.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));
        Assert.That(GardenMath.TryChooseTask(world, other, out var task) && task.Crop == crop, Is.False);
    }

    [Test]
    public void MissingSeedsDoNotCreatePlantsOrAnImpossibleBid()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Clear();
        npc.Perception.Objects.Clear();
        Assert.That(GardenMath.TryChooseTask(world, npc, out _), Is.False);
        Assert.That(PlantGrowthCatalog.All.Single(p => p.SeedDefinitionId == ContentIds.Coconut).GardenTargetCount, Is.Zero);
    }
}
}
