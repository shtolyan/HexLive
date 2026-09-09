using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class ItemRequestTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void NeutralOwnerGivesOneSpareLighterAtTheExactConsentBoundary(bool manual)
    {
        var (world, requester, owner) = Scene();
        requester.Mind.ManualControl = manual;
        var selected = new ItemInstance(GearCatalog.Lighter) { Durability = 0.42f };
        var spare = new ItemInstance(GearCatalog.Lighter) { Durability = 0.93f };
        owner.Inventory.Items.Add(selected);
        owner.Inventory.Items.Add(spare);

        var admission = ManualCommandExecutor.Apply(world,
            new RequestItemCommand(requester.Id, owner.Id, GearCatalog.Lighter));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Accepted, Is.True,
                "0.35 + 0.25 × one spare + 0 affinity must reach the 0.6 boundary.");
            Assert.That(requester.Inventory.Items.Single(), Is.SameAs(selected));
            Assert.That(owner.Inventory.Items.Single(), Is.SameAs(spare));
            Assert.That(selected.Durability, Is.EqualTo(0.42f));
            Assert.That(world.Events.Items.Any(e => e.EntityId == requester.Id.Value &&
                e.Type == "ItemRequestResult" && e.Message.Contains("Outcome=Transferred")), Is.True);
            Assert.That(world.Events.Items.Any(e => e.Type == "GiftGiven"), Is.True);
        });
    }

    [Test]
    public void RefusalChangesNeitherInventoryNorRelationships()
    {
        var (world, requester, owner) = Scene();
        var selected = new ItemInstance(GearCatalog.Lighter);
        var spare = new ItemInstance(GearCatalog.Lighter);
        owner.Inventory.Items.Add(selected);
        owner.Inventory.Items.Add(spare);
        var ownerRelation = owner.Social.GetOrCreate(requester.Id);
        ownerRelation.Affinity = -1f;
        ownerRelation.Familiarity = 0.31f;
        var requesterRelation = requester.Social.GetOrCreate(owner.Id);
        requesterRelation.Affinity = 0.2f;
        requesterRelation.Familiarity = 0.17f;

        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, selected.DefinitionId),
            Is.EqualTo(ItemRequestOutcome.Refused));

        Assert.Multiple(() =>
        {
            Assert.That(requester.Inventory.Items, Is.Empty);
            Assert.That(owner.Inventory.Items[0], Is.SameAs(selected));
            Assert.That(owner.Inventory.Items[1], Is.SameAs(spare));
            Assert.That(ownerRelation.Affinity, Is.EqualTo(-1f));
            Assert.That(ownerRelation.Familiarity, Is.EqualTo(0.31f));
            Assert.That(requesterRelation.Affinity, Is.EqualTo(0.2f));
            Assert.That(requesterRelation.Familiarity, Is.EqualTo(0.17f));
            Assert.That(world.Events.Items.Any(e => e.Type == "GiftGiven"), Is.False);
        });
    }

    [Test]
    public void TheOnlyToolStaysWithItsOwnerEvenForAFriend()
    {
        var (world, requester, owner) = Scene();
        owner.Social.GetOrCreate(requester.Id).Affinity = 1f;
        var item = new ItemInstance(GearCatalog.Lighter);
        owner.Inventory.Items.Add(item);
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, item.DefinitionId),
            Is.EqualTo(ItemRequestOutcome.NeededByOwner));
        Assert.That(owner.Inventory.Items.Single(), Is.SameAs(item));
        Assert.That(requester.Inventory.Items, Is.Empty);
    }

    [Test]
    public void AStarvingOwnerKeepsFoodAndBorrowedPropertyIsNotGivenAway()
    {
        var (world, requester, owner) = Scene();
        owner.Inventory.Items.Add(ContentIds.CoconutOpen);
        owner.Inventory.Items.Add(ContentIds.CoconutOpen);
        owner.Needs.Hunger = SimBalance.StarvingEnterThreshold;
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, ContentIds.CoconutOpen),
            Is.EqualTo(ItemRequestOutcome.NeededByOwner));
        owner.Inventory.Items.Clear();
        owner.Inventory.Items.Add(new ItemInstance(GearCatalog.Lighter) { OwnerId = 98765 });
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.Refused));
        Assert.That(requester.Inventory.Items, Is.Empty);
    }

    [Test]
    public void WaterAndConditionFollowTheSamePhysicalBottle()
    {
        var (world, requester, owner) = Scene();
        var selected = new ItemInstance(ContentIds.Bottle)
            { Wetness = 0.4f, Dirtiness = 0.23f, Durability = 0.71f };
        var spare = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(selected, WaterKind.Rain, 3);
        BottleInventoryMath.SetContents(spare, WaterKind.Raw, 1);
        owner.Inventory.Items.Add(selected);
        owner.Inventory.Items.Add(spare);
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, ContentIds.Bottle),
            Is.EqualTo(ItemRequestOutcome.Transferred));
        Assert.Multiple(() =>
        {
            Assert.That(requester.Inventory.Items.Single(), Is.SameAs(selected));
            Assert.That(owner.Inventory.Items.Single(), Is.SameAs(spare));
            Assert.That(selected.ResourceAmount, Is.EqualTo(3));
            Assert.That(selected.WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(selected.Durability, Is.EqualTo(0.71f));
            Assert.That(selected.Wetness, Is.EqualTo(0.4f));
            Assert.That(selected.Dirtiness, Is.EqualTo(0.23f));
            Assert.That(spare.ResourceAmount, Is.EqualTo(1));
            Assert.That(spare.WaterKind, Is.EqualTo(WaterKind.Raw));
        });
    }

    [Test]
    public void EmptySpareBottleDoesNotMakeTheOwnersNeededWaterDisposable()
    {
        var (world, requester, owner) = Scene();
        var filled = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(filled, WaterKind.Boiled, 3);
        owner.Inventory.Items.Add(filled);
        owner.Inventory.Items.Add(ContentIds.Bottle);
        owner.Needs.Thirst = SimBalance.StarvingEnterThreshold;
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, ContentIds.Bottle),
            Is.EqualTo(ItemRequestOutcome.NeededByOwner));
        Assert.That(owner.Inventory.Items[0], Is.SameAs(filled));
        Assert.That(filled.ResourceAmount, Is.EqualTo(3));
        Assert.That(requester.Inventory.Items, Is.Empty);
    }

    [Test]
    public void MedicineNeededByTheOwnerIsNotOfferedDespiteASpare()
    {
        var (world, requester, owner) = Scene();
        owner.Inventory.Items.Add(ContentIds.Pill);
        owner.Inventory.Items.Add(ContentIds.Pill);
        owner.Mind.SickUntilTick = world.Tick + 100;
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, ContentIds.Pill),
            Is.EqualTo(ItemRequestOutcome.NeededByOwner));
        Assert.That(owner.Inventory.Items.Count, Is.EqualTo(2));
        Assert.That(requester.Inventory.Items, Is.Empty);
    }

    [Test]
    public void BorrowedItemsAreSkippedAndNeverCountAsSpareProperty()
    {
        var (world, requester, owner) = Scene();
        var borrowed = new ItemInstance(GearCatalog.Lighter) { OwnerId = 98765 };
        var own = new ItemInstance(GearCatalog.Lighter) { OwnerId = owner.Id.Value };
        owner.Inventory.Items.Add(borrowed);
        owner.Inventory.Items.Add(own);
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.NeededByOwner));
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.Transferred));
        Assert.That(requester.Inventory.Items.Single(), Is.SameAs(own));
        Assert.That(own.OwnerId, Is.EqualTo(requester.Id.Value));
        Assert.That(owner.Inventory.Items[0], Is.SameAs(borrowed));
    }

    [Test]
    public void AManuallyControlledOwnerMustDecideForHerself()
    {
        var (world, requester, owner) = Scene();
        owner.Mind.ManualControl = true;
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        var relationship = owner.Social.GetOrCreate(requester.Id);
        var affinity = relationship.Affinity;
        var familiarity = relationship.Familiarity;
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.CannotTalk));
        Assert.Multiple(() =>
        {
            Assert.That(owner.Inventory.Items.Count, Is.EqualTo(2));
            Assert.That(requester.Inventory.Items, Is.Empty);
            Assert.That(relationship.Affinity, Is.EqualTo(affinity));
            Assert.That(relationship.Familiarity, Is.EqualTo(familiarity));
        });
    }

    [Test]
    public void FullReceiverDoesNotLoseOrEvictAnyItem()
    {
        var (world, requester, owner) = Scene();
        for (var i = 0; i < requester.Inventory.Capacity; i++)
            requester.Inventory.Items.Add(ContentIds.Knife);
        var original = requester.Inventory.Items.ToArray();
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        var selected = owner.Inventory.Items[0];
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.NoSpace));
        Assert.That(owner.Inventory.Items[0], Is.SameAs(selected));
        Assert.That(owner.Inventory.Items.Count, Is.EqualTo(2));
        for (var i = 0; i < original.Length; i++)
            Assert.That(requester.Inventory.Items[i], Is.SameAs(original[i]));
    }

    [TestCase("sleep")]
    [TestCase("unconscious")]
    [TestCase("dead")]
    [TestCase("walking")]
    [TestCase("fighting")]
    public void AnOwnerWhoCannotAnswerNeverGrantsConsent(string state)
    {
        var (world, requester, owner) = Scene();
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        switch (state)
        {
            case "sleep": owner.Execution.CurrentInteraction = InteractionType.Sleep; break;
            case "unconscious": owner.Mind.FaintedUntilTick = world.Tick + 100; break;
            case "dead": owner.Health = 0f; break;
            case "walking": owner.Movement.IsMoving = true; break;
            case "fighting": owner.IsFighting = true; break;
        }
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.CannotTalk));
        Assert.That(requester.Inventory.Items, Is.Empty);
        Assert.That(owner.Inventory.Items.Count, Is.EqualTo(2));
    }

    [Test]
    public void MissingAndDistantItemsReturnExplicitResultsWithoutAPhantomTransfer()
    {
        var (world, requester, owner) = Scene();
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.MissingItem));
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        owner.Inventory.Items.Add(GearCatalog.Lighter);
        var far = world.Junctions.Items.Values.First(j => !j.Blocked &&
            !SpatialQueries.IsAllWaterJunction(world, j.Id) &&
            Math.Abs(j.WorldPosition.X - requester.Position.X) > InteractionReach.Aid * 2f);
        Place(world, owner, far);
        Assert.That(ItemRequestMath.Request(world, requester, owner.Id, GearCatalog.Lighter),
            Is.EqualTo(ItemRequestOutcome.TooFar));
        Assert.That(requester.Inventory.Items, Is.Empty);
        Assert.That(owner.Inventory.Items.Count, Is.EqualTo(2));
    }

    private static (WorldState world, NPCState requester, NPCState owner) Scene()
    {
        var world = TestWorld.CreateWorld();
        var people = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        foreach (var npc in people)
        {
            npc.Inventory.Items.Clear();
            npc.WornItems.Clear();
            npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Movement.IsMoving = false;
            EquipmentMath.RecalculateCapacity(world, npc);
        }
        people[1].Social.GetOrCreate(people[0].Id).Affinity = 0f;
        var from = world.Junctions.Items.Values.First(j =>
            SpatialQueries.IsJunctionFree(world, j.Id) && !SpatialQueries.IsAllWaterJunction(world, j.Id) &&
            j.Neighbors.Any(id => SpatialQueries.IsJunctionFree(world, id) &&
                !SpatialQueries.IsAllWaterJunction(world, id) &&
                InteractionReach.CanTouchPersonAcross(world, j.Id, id, InteractionReach.Aid)));
        var to = world.Junctions.Items[from.Neighbors.First(id =>
            SpatialQueries.IsJunctionFree(world, id) && !SpatialQueries.IsAllWaterJunction(world, id) &&
            InteractionReach.CanTouchPersonAcross(world, from.Id, id, InteractionReach.Aid))];
        Place(world, people[0], from);
        Place(world, people[1], to);
        return (world, people[0], people[1]);
    }

    private static void Place(WorldState world, NPCState npc, Junction at)
    {
        if (npc.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, npc.Id);
        }
        var oldTile = npc.Tile;
        npc.CurrentJunction = at.Id;
        npc.Position = at.WorldPosition;
        npc.Tile = at.Tiles[0];
        npc.Fragment = at.Fragment;
        SpatialMutations.MoveEntityToTile(world, npc.Id, oldTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, at.Id, npc.Id);
    }
}

}
