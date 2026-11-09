using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class PropertyUseTheftTests
{
    [TestCase(InteractionType.Drink)]
    [TestCase(InteractionType.Process)]
    [TestCase(InteractionType.PickUp)]
    [TestCase(InteractionType.Harvest)]
    public void DifferentSuccessfulWorldActionsAllReportOneTheft(InteractionType type)
    {
        var world = TestWorld.CreateWorld(); var actor = world.Entities.Npcs.Values.First();
        var owner = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
        actor.Plan.Goal = GoalType.PlayerOrder; actor.Mind.ManualControl = true;
        var id = type == InteractionType.Drink ? ContentIds.CoconutPierced : ContentIds.Stick;
        var obj = WorldObjectMutations.SpawnObject(world, id, actor.Fragment, actor.Tile, world.Tiles.Items[actor.Tile].Junctions[0]);
        obj.Owner = owner.Id; obj.ResourceAmount = 2f;
        var def = world.Content.ObjectDefinitions[id];
        var interaction = new InteractionDefinition { Id = "test", Type = type };
        ExecutionSystem.ApplyInteractionCompletion(world, actor, obj, def, interaction, "");
        Assert.That(world.Events.Items.Count(e => e.Type == "ItemStolen"), Is.EqualTo(1));
    }

    [Test]
    public void RejectedRefillCreatesNoTheftAndSharedOwnCampStationCreatesNone()
    {
        var world = TestWorld.CreateWorld(); var actor = world.Entities.Npcs.Values.First();
        var owner = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
        var fire = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Campfire);
        fire.Owner = owner.Id;
        Assert.That(ExecutionSystem.ApplyInteractionCompletion(world, actor, fire,
            world.Content.ObjectDefinitions[fire.DefinitionId], new InteractionDefinition { Type = InteractionType.FillBottle }, ""), Is.False);
        Assert.That(world.Events.Items.Count(e => e.Type == "ItemStolen"), Is.Zero);
        fire.Owner = null; fire.OwnerFaction = actor.Faction;
        ExecutionSystem.ApplyInteractionCompletion(world, actor, fire,
            world.Content.ObjectDefinitions[fire.DefinitionId], new InteractionDefinition { Type = InteractionType.Sit }, "");
        Assert.That(world.Events.Items.Count(e => e.Type == "ItemStolen"), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ContainerPickupIncludesNestedOwnershipAndNeverPenalizesRecoveryOfOwnItem(bool ownItem)
    {
        var world = TestWorld.CreateWorld(); var actor = world.Entities.Npcs.Values.First();
        var owner = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
        var bag = world.Entities.Objects.Values.First(); bag.Owner = ownItem ? owner.Id : actor.Id;
        var item = new ItemInstance(ContentIds.Bottle) { OwnerId = ownItem ? actor.Id.Value : owner.Id.Value, ResourceAmount = 4.05f, WaterKind = WaterKind.Boiled };
        bag.Contents.Add(item);
        ContainerLootMath.TakeFromContainer(world, bag, actor, new[] { item }, Array.Empty<ObjectId>());
        Assert.That(world.Events.Items.Count(e => e.Type == "ItemStolen"), Is.EqualTo(ownItem ? 0 : 1));
        Assert.That(actor.Inventory.Items.Any(i => ReferenceEquals(i, item)), Is.True);
        Assert.That(item.ResourceAmount, Is.EqualTo(4.05f));
    }

    [Test]
    public void PersonalTheftPredicateSeparatesVoluntaryGiftsFromUnauthorizedTake()
    {
        var world = TestWorld.CreateWorld(); var actor = world.Entities.Npcs.Values.First();
        var owner = world.Entities.Npcs.Values.First(n => n.Id != actor.Id); owner.Faction = actor.Faction;
        Assert.That(TheftMath.IsTakingTheft(world, actor, owner, new ItemInstance(ContentIds.Stick)), Is.True);
        Assert.That(TheftMath.IsTakingTheft(world, actor, owner, new ItemInstance(ContentIds.Stick) { OwnerId = actor.Id.Value }), Is.False);
    }
}
}
