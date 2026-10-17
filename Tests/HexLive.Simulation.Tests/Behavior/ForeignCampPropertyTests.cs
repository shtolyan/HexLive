using System.Linq;
using System.Reflection;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class ForeignCampPropertyTests
{
    private static (WorldState world, NPCState actor, NPCState host) Camp()
    {
        var world = TestWorld.CreateWorld();
        world.Mode = GameMode.HugeIsland;
        var actors = world.Entities.Npcs.Values.Take(2).ToArray();
        var actor = actors[0];
        var host = actors[1];
        actor.Faction = Faction.Colony;
        host.Faction = Faction.Colony2;
        world.FactionHomes.Clear();
        world.FactionHomes[host.Faction] = actor.Tile;
        actor.Perception.Objects.Clear();
        host.Perception.Agents.Add(new PerceivedAgent { Id = actor.Id });
        actor.CurrentJunction = world.Tiles.Items[actor.Tile].Junctions
            .First(j => !world.Junctions.Items[j].Blocked);
        return (world, actor, host);
    }

    private static WorldObjectState Object(WorldState world, NPCState actor, string id)
    {
        var obj = WorldObjectMutations.SpawnObject(world, id, actor.Fragment,
            actor.Tile, actor.CurrentJunction.Value);
        var seen = new PerceivedObject { Id = obj.Id, DefinitionId = id, Tile = obj.Tile,
            IsReachable = true, Distance = 1f };
        seen.AvailableInteractions.Add(id == ContentIds.DryingRack
            ? InteractionType.Hang : InteractionType.PickUp);
        actor.Perception.Objects.Add(seen);
        return obj;
    }

    [Test]
    public void NeutralVisitorDoesNotBidForHostsRackOrSupplies()
    {
        var (world, actor, host) = Camp();
        Object(world, actor, ContentIds.DryingRack);
        Object(world, actor, ContentIds.Stone);
        Assert.Multiple(() =>
        {
            Assert.That(FactionRelations.AreNeutral(world, actor, host), Is.True);
            Assert.That(PlanningSystem.HasDryingDestination(world, actor), Is.False);
            Assert.That(PlanningSystem.HasObjectCandidateForGoal(world, actor, GoalType.GatherStone), Is.False);
            Assert.That(DecisionSystem.HasReachableWithTag(actor, world, "Stone"), Is.False);
        });
    }

    [Test]
    public void OldPickupPlanCannotStealAndReduceHostsAffinity()
    {
        var (world, actor, host) = Camp();
        var stone = Object(world, actor, ContentIds.Stone);
        actor.Plan.Goal = GoalType.GatherStone;
        var def = world.Content.ObjectDefinitions[stone.DefinitionId];
        var picked = (bool)typeof(ExecutionSystem).GetMethod("CompletePickUp",
            BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                new object[] { world, actor, stone, def,
                    def.Interactions.First(i => i.Type == InteractionType.PickUp), "" });
        Assert.Multiple(() =>
        {
            Assert.That(picked, Is.False);
            Assert.That(world.Entities.Objects.ContainsKey(stone.Id), Is.True);
            Assert.That(host.Social.GetOrCreate(actor.Id).Affinity, Is.Zero);
            Assert.That(world.Events.Items.Any(e => e.Type == "ItemStolen"), Is.False);
        });
    }

    [Test]
    public void VisitorDoesNotUseNearbyRackForOwnLaundryOrDumpCarriedItems()
    {
        var (world, actor, _) = Camp();
        var rack = Object(world, actor, ContentIds.DryingRack);
        var garment = new ItemInstance("clothing.panties") { OwnerId = actor.Id.Value };
        var count = world.Entities.Objects.Count;
        Assert.Multiple(() =>
        {
            Assert.That(StowMath.StationBesideNpc(world, actor), Is.Null);
            Assert.That(ExecutionSystem.StowGarmentWithContents(world, actor, garment, rack.Id), Is.Null);
            Assert.That(InventoryMath.TryDropAutomatic(world, actor, new ItemInstance(ContentIds.Stick)), Is.Null);
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(count));
        });
    }

    [Test]
    public void OwnershipChangedDuringWalkAbortsBeforeStationResentment()
    {
        var (world, actor, host) = Camp();
        var rack = Object(world, actor, ContentIds.DryingRack);
        rack.IsOccupied = true;
        rack.CurrentUser = host.Id;
        actor.Mind.CurrentGoal = GoalType.DryClothes;
        actor.Plan.Goal = GoalType.DryClothes;
        actor.Plan.Status = PlanStatus.Active;
        actor.Plan.TargetObjectId = rack.Id;
        actor.Plan.TargetJunctionId = actor.CurrentJunction;
        actor.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Interact,
            TargetObject = rack.Id, Interaction = InteractionType.Hang });
        new ExecutionSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(actor.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
            Assert.That(actor.Social.GetOrCreate(host.Id).Affinity, Is.Zero);
            Assert.That(rack.CurrentUser, Is.EqualTo(host.Id), "Do not release another actor's claim.");
            Assert.That(world.Events.Items.Any(e => e.Type == "InteractionBlocked"), Is.False);
        });
    }

    [Test]
    public void OwnClothesCanBeRecoveredButNeutralHostsClothesCannot()
    {
        var (world, actor, host) = Camp();
        var own = Object(world, actor, "clothing.panties");
        own.Owner = actor.Id;
        var theirs = Object(world, actor, "clothing.panties");
        theirs.Owner = host.Id;
        Assert.That(CampPropertyMath.CanUse(world, actor, own), Is.True);
        Assert.That(CampPropertyMath.CanUse(world, actor, theirs), Is.False);
        world.FactionHomes.Clear();
        Assert.That(CampPropertyMath.CanUse(world, actor, theirs), Is.False,
            "A live owner's property remains theirs outside their camp.");
    }

    [Test]
    public void ExplicitPlayerPickupStillAppliesTheRealTheftPenalty()
    {
        var (world, actor, host) = Camp();
        var stone = Object(world, actor, ContentIds.Stone);
        actor.Plan.Goal = GoalType.PlayerOrder;
        var def = world.Content.ObjectDefinitions[stone.DefinitionId];
        var picked = (bool)typeof(ExecutionSystem).GetMethod("CompletePickUp",
            BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                new object[] { world, actor, stone, def,
                    def.Interactions.First(i => i.Type == InteractionType.PickUp), "" });
        Assert.That(picked, Is.True);
        Assert.That(host.Social.GetOrCreate(actor.Id).Affinity,
            Is.EqualTo(-TheftMath.WitnessAffinityHit).Within(0.00001f));
    }

    [Test]
    public void HostileLootAndOwnCampActivitiesRemainAvailable()
    {
        var (world, actor, host) = Camp();
        var stone = Object(world, actor, ContentIds.Stone);
        stone.Owner = host.Id;
        actor.Social.GetOrCreate(host.Id).Affinity = -1f;
        Assert.That(CampPropertyMath.CanUse(world, actor, stone), Is.True,
            "Personal hatred is the existing solo-camp hostility rule.");
        world.FactionHomes.Clear();
        world.FactionHomes[actor.Faction] = actor.Tile;
        stone.Owner = null;
        var rack = Object(world, actor, ContentIds.DryingRack);
        Assert.That(PlanningSystem.HasDryingDestination(world, actor), Is.True);
        Assert.That(PlanningSystem.HasObjectCandidateForGoal(world, actor, GoalType.GatherStone), Is.True);
        Assert.That(CampPropertyMath.CanDeposit(world, actor, actor.Tile), Is.True);
    }

    [Test]
    public void InterruptedHeldGarmentAndItsPocketsAreRetainedWithoutLitter()
    {
        var (world, actor, _) = Camp();
        var garment = new ItemInstance("clothing.panties") { OwnerId = actor.Id.Value,
            Dirtiness = 0.25f, Wetness = 0.8f };
        var pocket = new ItemInstance(ContentIds.Stone) { Durability = 0.7f };
        actor.Mind.CurrentGoal = GoalType.StowClothes;
        actor.Plan.Goal = GoalType.StowClothes;
        actor.Plan.Status = PlanStatus.Active;
        actor.Execution.HeldGarment = garment;
        actor.Execution.HeldGarmentContents.Add(pocket);
        var count = world.Entities.Objects.Count;
        Assert.That(PlanInterruption.TryAbort(world, actor,
            InterruptionCause.ExecutionFailure, "foreign station"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(count));
            Assert.That(actor.Inventory.Items.Any(i => ReferenceEquals(i, garment)), Is.True);
            Assert.That(actor.Inventory.Items.Any(i => ReferenceEquals(i, pocket)), Is.True);
            Assert.That(garment.Wetness, Is.EqualTo(0.8f));
        });
    }

    [Test]
    public void NeutralCampBoundaryAndRememberedSuppliesUseFiveHexRadius()
    {
        var (world, actor, _) = Camp();
        var home = actor.Tile;
        var remembered = new PerceivedObject { Id = new ObjectId(int.MaxValue),
            DefinitionId = ContentIds.Stone, Tile = new TileCoord(home.Q + 5, home.R),
            FromMemory = true };
        Assert.That(CampPropertyMath.CanUse(world, actor, remembered), Is.False);
        remembered.Tile = new TileCoord(home.Q + 6, home.R);
        Assert.That(CampPropertyMath.CanUse(world, actor, remembered), Is.True);
        Assert.That(actor.Social.Relationships, Is.Empty,
            "Read-only admission must not create relationships.");
    }
}
}
