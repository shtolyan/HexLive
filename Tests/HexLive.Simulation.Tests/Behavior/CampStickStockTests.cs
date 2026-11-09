using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class CampStickStockTests
{
    [TestCase(36, true)]
    [TestCase(37, false)]
    [TestCase(40, false)]
    public void NewProductionFitsInsideTwoGroundStacks(int stock, bool allowed)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var resident in world.Entities.Npcs.Values) resident.Inventory.Items.Clear();
        foreach (var stick in world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Stick).ToArray())
            WorldObjectMutations.DespawnObject(world, stick.Id);
        for (var i=0; i<stock; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        Assert.That(CampStickStock.Limit, Is.EqualTo(40));
        Assert.That(CampStickStock.CanProduce(world, npc, 4), Is.EqualTo(allowed));
    }

    [Test]
    public void OtherWorkersPromisedOutputCountsButAnotherCampsPocketsDoNot()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var resident in world.Entities.Npcs.Values) resident.Inventory.Items.Clear();
        foreach (var stick in world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Stick).ToArray())
            WorldObjectMutations.DespawnObject(world, stick.Id);
        var other = world.Entities.Npcs.Values.First(n => n.Faction == npc.Faction && n.Id != npc.Id);
        for (var i=0; i<33; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        other.Plan.Status = PlanStatus.Active;
        other.Plan.Goal = GoalType.SplitLog;
        Assert.That(CampStickStock.CanProduce(world, npc, 4), Is.False);
        other.Plan.Status = PlanStatus.Completed;
        var foreign = world.Entities.Npcs.Values.First(n => n.Faction != npc.Faction);
        for (var i=0; i<40; i++) foreign.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        Assert.That(CampStickStock.CanProduce(world, npc, 4), Is.True);
    }
    [TestCase(36, true)]
    [TestCase(40, false)]
    public void TheRealDecisionAuctionStopsSplittingWhenCampAlreadyHasEnough(int stock, bool allowed)
    {
        var engine = TestWorld.CreateEngine(); engine.Step();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var resident in world.Entities.Npcs.Values) resident.Inventory.Items.Clear();
        foreach (var stick in world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Stick).ToArray())
            WorldObjectMutations.DespawnObject(world, stick.Id);
        var neighbor = world.Entities.Npcs.Values.First(n => n.Faction == npc.Faction && n.Id != npc.Id);
        for (var j=0; j<stock; j++) neighbor.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Axe));
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;
        npc.Mind.ManualControl = false;
        npc.Needs.Hunger = npc.Needs.Thirst = 0f;
        npc.Needs.Stamina = npc.Needs.Energy = 1f;
        npc.Perception.Objects.Clear();
        var log = WorldObjectMutations.SpawnObject(world, ContentIds.Log,
            npc.Fragment, npc.Tile, npc.CurrentJunction.Value);
        npc.Perception.Objects.Add(new PerceivedObject { Id = log.Id, DefinitionId = log.DefinitionId,
            Tile = log.Tile, IsReachable = true, AvailableInteractions = { InteractionType.Process } });
        var fire = world.Entities.Objects.Values.First(o =>
            world.Content.ObjectDefinitions[o.DefinitionId].HasTag("Campfire") &&
            ColonyQueries.InCamp(world, o.Tile, npc.Faction));
        fire.ResourceAmount = 1;
        npc.Perception.Objects.Add(new PerceivedObject { Id = fire.Id, DefinitionId = fire.DefinitionId,
            Tile = fire.Tile, IsReachable = true });
        new DecisionSystem().Run(world);
        Assert.That(npc.Mind.LastScores.Single(x => x.Goal == GoalType.SplitLog).FinalScore > 0,
            Is.EqualTo(allowed));
    }

}
