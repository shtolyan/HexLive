using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class GiftGarmentChoiceTests
{
    private static (WorldState world, NPCState giver, NPCState receiver) Scene()
    {
        var world = TestWorld.CreateWorld(); var pair = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        foreach (var npc in pair) { npc.Inventory.Items.Clear(); npc.WornItems.Clear(); npc.Mind.ManualControl = false; npc.Mind.OutfitLocked = false; npc.Execution.Status = ExecutionStatus.None; npc.Plan.Status = PlanStatus.Completed; npc.Memory.Dangers.Clear(); npc.Movement.IsMoving = false; npc.Mind.WakeGraceUntilTick = 0; }
        foreach (var id in new[] { "test.gift_a", "test.gift_b" })
            world.Content.ObjectDefinitions[id] = new ObjectDefinition { Id = id, Layer = WearLayer.Wear, Covers = { BodyPart.Torso }, InventoryCapacity = 3 };
        return (world, pair[0], pair[1]);
    }

    [Test]
    public void ConditionDominatesLargeTasteDifferenceAndTasteBreaksEqualCondition()
    {
        var (world, giver, receiver) = Scene();
        var a = new ItemInstance("test.gift_a") { Durability = .1f }; var b = new ItemInstance("test.gift_b") { Durability = 1f };
        Assert.That(GiftGarmentChoice.Score(receiver.Id.Value, b), Is.GreaterThan(GiftGarmentChoice.Score(receiver.Id.Value, a)));
        a.Durability = b.Durability;
        Assert.That(GiftGarmentChoice.Score(receiver.Id.Value, a) > GiftGarmentChoice.Score(receiver.Id.Value, b),
            Is.EqualTo(ItemAffinity.For(receiver.Id.Value, a.DefinitionId) > ItemAffinity.For(receiver.Id.Value, b.DefinitionId)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GiftTransfersOwnershipEvenWhenRecipientCannotChooseClothing(bool sleeping)
    {
        var (world, giver, receiver) = Scene();
        receiver.Mind.ManualControl = !sleeping;
        if (sleeping) receiver.Execution.CurrentInteraction = InteractionType.Sleep;
        var gift = new ItemInstance("test.gift_a") { OwnerId = giver.Id.Value, Durability = .83f };
        giver.Inventory.Items.Add(gift); var itemRef = new InventoryItemRef(InventoryItemSource.Carried, 0, gift.DefinitionId);
        Assert.That(PlayerInventoryTransferMath.FitsAfter(world, giver, receiver, itemRef, 1, gift: true), Is.True);
        PlayerInventoryTransferMath.MoveResolved(world, giver, receiver, itemRef, new[] { gift }, new ItemInstance[0], gift: true);
        Assert.That(gift.OwnerId, Is.EqualTo(receiver.Id.Value)); Assert.That(receiver.Inventory.Items[0], Is.SameAs(gift));
        Assert.That(receiver.WornItems, Is.Empty); Assert.That(world.Events.Items.Any(e => e.Type == "ItemStolen"), Is.False);
        receiver.Mind.ManualControl = false; receiver.Execution.CurrentInteraction = null;
        world.Environment.GlobalTemperature = 20f;
        receiver.Needs.Hunger = receiver.Needs.Thirst = 0f;
        OutfitPreference.TryWearCarriedFavorite(world, receiver, 20f);
        Assert.That(receiver.WornItems.Any(i => ReferenceEquals(i, gift)), Is.True);
    }

    [Test]
    public void BetterGiftIsWornAndInferiorGiftRemainsOwnedInInventory()
    {
        var (world, giver, receiver) = Scene();
        var worn = new ItemInstance("test.gift_a") { Durability = .1f, OwnerId = receiver.Id.Value }; receiver.WornItems.Add(worn); EquipmentMath.Recalculate(world, receiver);
        var better = new ItemInstance("test.gift_b") { Durability = 1f, OwnerId = giver.Id.Value }; giver.Inventory.Items.Add(better);
        var itemRef = new InventoryItemRef(InventoryItemSource.Carried, 0, better.DefinitionId);
        Assert.That(PlayerInventoryTransferMath.FitsAfter(world, giver, receiver, itemRef, 1, gift: true), Is.True);
        PlayerInventoryTransferMath.MoveResolved(world, giver, receiver, itemRef, new[] { better }, new ItemInstance[0], gift: true);
        Assert.That(receiver.WornItems.Any(i => ReferenceEquals(i, better)), Is.True);
        Assert.That(better.OwnerId, Is.EqualTo(receiver.Id.Value));
        var worse = new ItemInstance("test.gift_a") { Durability = .01f }; giver.Inventory.Items.Add(worse);
        PlayerInventoryTransferMath.MoveResolved(world, giver, receiver, new InventoryItemRef(InventoryItemSource.Carried, 0, worse.DefinitionId), new[] { worse }, new ItemInstance[0], gift: true);
        Assert.That(receiver.Inventory.Items.Any(i => ReferenceEquals(i, worse)), Is.True);
        Assert.That(worse.OwnerId, Is.EqualTo(receiver.Id.Value));
        Assert.That(receiver.WornItems.Any(i => ReferenceEquals(i, better)), Is.True);
    }

    [Test]
    public void FailedCapacityProjectionNeverTransfersOwnership()
    {
        var (world, giver, receiver) = Scene(); receiver.Mind.OutfitLocked = true;
        for (var i = 0; i < 100; i++) receiver.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        var gift = new ItemInstance("test.gift_a") { OwnerId = giver.Id.Value }; giver.Inventory.Items.Add(gift);
        Assert.That(PlayerInventoryTransferMath.FitsAfter(world, giver, receiver,
            new InventoryItemRef(InventoryItemSource.Carried, 0, gift.DefinitionId), 1, gift: true), Is.False);
        Assert.That(gift.OwnerId, Is.EqualTo(giver.Id.Value)); Assert.That(giver.Inventory.Items[0], Is.SameAs(gift));
    }
}
}
