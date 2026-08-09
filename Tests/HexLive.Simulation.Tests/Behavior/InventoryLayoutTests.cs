using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class InventoryLayoutTests
{
    [Test]
    public void NakedBody_HasSeparateHandsAndCarryWithStrengthBreakdown()
    {
        var (world, npc) = CleanNpc();

        var ordinary = InventoryLayoutBuilder.Build(world, npc);
        Assert.That(ordinary.Containers.Count(c => c.Kind == InventoryContainerKind.HandLeft), Is.EqualTo(1));
        Assert.That(ordinary.Containers.Count(c => c.Kind == InventoryContainerKind.HandRight), Is.EqualTo(1));
        var carry = ordinary.Containers.Single(c => c.Kind == InventoryContainerKind.Carry);
        Assert.That(carry.BaseCapacity, Is.EqualTo(SimBalance.BaseCarrySlots));
        Assert.That(carry.StrengthBonus, Is.Zero);
        Assert.That(carry.BackpackCapacity, Is.Zero);

        npc.Attributes.Strength = Spec76.CarrySlotThreshold;
        EquipmentMath.RecalculateCapacity(world, npc);
        var strong = InventoryLayoutBuilder.Build(world, npc);
        carry = strong.Containers.Single(c => c.Kind == InventoryContainerKind.Carry);
        Assert.That(carry.StrengthBonus, Is.EqualTo(1));
        AssertCapacityParity(npc, strong);
    }

    [Test]
    public void Hands_FollowLimbFunctionIncludingProsthesis()
    {
        var (world, npc) = CleanNpc();

        npc.Body.Sever(BodyPart.ArmL);
        EquipmentMath.RecalculateCapacity(world, npc);
        var oneHand = InventoryLayoutBuilder.Build(world, npc);
        Assert.That(oneHand.Containers.Any(c => c.Kind == InventoryContainerKind.HandLeft), Is.False);
        Assert.That(oneHand.Containers.Any(c => c.Kind == InventoryContainerKind.HandRight), Is.True);

        npc.Body.Condition(BodyPart.ArmL).Prosthetic = new ProstheticState
        {
            DefinitionId = ContentIds.WoodenArm,
            Part = BodyPart.ArmL,
            Condition = 1f,
            MaxCondition = 1f,
            Function = 0.6f
        };
        EquipmentMath.RecalculateCapacity(world, npc);
        var prosthetic = InventoryLayoutBuilder.Build(world, npc);
        Assert.That(prosthetic.Containers.Any(c => c.Kind == InventoryContainerKind.HandLeft), Is.True);

        npc.Body.Sever(BodyPart.ArmR);
        npc.Body.Condition(BodyPart.ArmL).Prosthetic = null;
        EquipmentMath.RecalculateCapacity(world, npc);
        var armless = InventoryLayoutBuilder.Build(world, npc);
        Assert.That(armless.Containers.Any(c =>
            c.Kind is InventoryContainerKind.HandLeft or InventoryContainerKind.HandRight), Is.False);
        Assert.That(armless.Containers.Any(c => c.Kind == InventoryContainerKind.Carry), Is.False);
        AssertCapacityParity(npc, armless);
    }

    [Test]
    public void Backpack_ExtendsCarryButDoesNotChangeHandPanels()
    {
        var (world, npc) = CleanNpc();
        var backpackId = "gear.backpack_osiris";
        var backpackCapacity = world.Content.ObjectDefinitions[backpackId].InventoryCapacity;

        npc.WornItems.Add(backpackId);
        EquipmentMath.RecalculateCapacity(world, npc);
        var layout = InventoryLayoutBuilder.Build(world, npc);

        var carry = layout.Containers.Single(c => c.Kind == InventoryContainerKind.Carry);
        Assert.That(carry.OwnerItemDefinitionId, Is.EqualTo(backpackId));
        Assert.That(carry.BackpackCapacity, Is.EqualTo(backpackCapacity));
        Assert.That(carry.Capacity,
            Is.EqualTo(SimBalance.BaseCarrySlots + carry.StrengthBonus + backpackCapacity));
        Assert.That(layout.Containers.Count(c =>
            c.Kind is InventoryContainerKind.HandLeft or InventoryContainerKind.HandRight), Is.EqualTo(2));
        AssertCapacityParity(npc, layout);
    }

    [Test]
    public void Garments_CreateExactContainersInWornOrder()
    {
        var (world, npc) = CleanNpc();
        AddGarment(world, "test.jacket", 3, BodyPart.Torso);
        AddGarment(world, "test.trousers", 2, BodyPart.Pelvis);
        npc.WornItems.Add("test.jacket");
        npc.WornItems.Add("test.trousers");
        EquipmentMath.RecalculateCapacity(world, npc);

        var garments = InventoryLayoutBuilder.Build(world, npc).Containers
            .Where(c => c.Kind == InventoryContainerKind.Garment).ToArray();

        Assert.That(garments.Select(c => c.OwnerItemDefinitionId),
            Is.EqualTo(new[] { "test.jacket", "test.trousers" }));
        Assert.That(garments.Select(c => c.Capacity), Is.EqualTo(new[] { 3, 2 }));
        Assert.That(garments[0].BodyAnchor, Is.EqualTo(InventoryBodyAnchor.Chest));
        Assert.That(garments[1].BodyAnchor, Is.EqualTo(InventoryBodyAnchor.Pelvis));
    }

    [Test]
    public void Holster_TakesOnlyItsTypedFirstTool()
    {
        var (world, npc) = CleanNpc();
        npc.WornItems.Add("legHolster_2204");
        npc.Inventory.Items.Add("tool.axe_stone");
        npc.Inventory.Items.Add("tool.axe_stone");
        npc.Inventory.Items.Add("resource.stone");
        EquipmentMath.RecalculateCapacity(world, npc);

        var layout = InventoryLayoutBuilder.Build(world, npc);
        var holster = layout.Containers.Single(c => c.Kind == InventoryContainerKind.Holster);
        Assert.That(holster.Slots.Single(s =>
            s.AcceptedItemDefinitionId == "tool.axe_stone").ItemDefinitionId,
            Is.EqualTo("tool.axe_stone"));
        Assert.That(holster.Slots.Single(s =>
            s.AcceptedItemDefinitionId == "tool.knife").ItemDefinitionId, Is.Empty);

        var regularAxeCells = layout.Containers
            .Where(IsRegular)
            .SelectMany(c => c.Slots)
            .Count(s => s.ItemDefinitionId == "tool.axe_stone");
        Assert.That(regularAxeCells, Is.EqualTo(1),
            "Only the first axe may ride free; its duplicate consumes a normal cell.");
        AssertCapacityParity(npc, layout);
    }

    [Test]
    public void ResourceStacks_SplitAtCurrentStackSize()
    {
        var (world, npc) = CleanNpc();
        var count = InventoryState.StackSizeFor("resource.stick") + 1;
        for (var i = 0; i < count; i++) npc.Inventory.Items.Add("resource.stick");

        var layout = InventoryLayoutBuilder.Build(world, npc);
        var stacks = layout.Containers.Where(IsRegular).SelectMany(c => c.Slots)
            .Where(s => s.ItemDefinitionId == "resource.stick").ToArray();

        Assert.That(stacks.Select(s => s.StackCount),
            Is.EqualTo(new[] { InventoryState.StackSizeFor("resource.stick"), 1 }));
        Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(2));
        AssertCapacityParity(npc, layout);
    }

    [Test]
    public void FavoriteWeapon_RemainsInARealCellAndAddsNoCapacity()
    {
        var (world, npc) = CleanNpc();
        npc.Inventory.Items.Add("tool.knife");
        EquipmentMath.RecalculateCapacity(world, npc);
        var capacityBefore = npc.Inventory.Capacity;

        var layout = InventoryLayoutBuilder.Build(world, npc);

        Assert.That(layout.FavoriteWeaponId, Is.EqualTo("tool.knife"));
        Assert.That(layout.Containers.SelectMany(c => c.Slots)
            .Count(s => s.ItemDefinitionId == "tool.knife"), Is.EqualTo(1));
        Assert.That(layout.RegularCapacity, Is.EqualTo(capacityBefore));
        AssertCapacityParity(npc, layout);
    }

    [Test]
    public void FavoriteWeapon_ProtectsOneKnifeButNotIdenticalCopies()
    {
        var (world, npc) = CleanNpc();
        var favorite = new ItemInstance(ContentIds.Knife);
        var duplicate = new ItemInstance(ContentIds.Knife);
        npc.Inventory.Items.Add(favorite);
        npc.Inventory.Items.Add(duplicate);

        Assert.That(InventoryMath.LowestImportanceDroppable(world, npc),
            Is.SameAs(duplicate),
            "Only one physical knife is the favorite; copies must not lock the pack.");
    }

    [Test]
    public void MissingTool_ReplacesRedundantKnifeInFullInventory()
    {
        var (world, npc) = CleanNpc();
        npc.Inventory.Capacity = 2;
        npc.Inventory.Items.Add(ContentIds.Knife);
        npc.Inventory.Items.Add(ContentIds.Knife);

        Assert.That(InventoryMath.Importance(world, ContentIds.PickaxeStone),
            Is.LessThan(InventoryMath.Importance(world, ContentIds.Knife)),
            "Regression setup requires the missing tool to lose the ordinary importance comparison.");
        Assert.That(InventoryMath.CanMakeRoomFor(world, npc, ContentIds.PickaxeStone), Is.True,
            "A missing capability must be able to displace redundant gear.");

        Assert.That(InventoryMath.MakeRoomFor(world, npc, ContentIds.PickaxeStone), Is.True);
        Assert.That(npc.Inventory.Items.Count(item => item.DefinitionId == ContentIds.Knife),
            Is.EqualTo(1));
        Assert.That(npc.Inventory.HasSpace, Is.True);
        Assert.That(world.Events.Items.Any(e =>
            e.Type == "InventoryMadeRoom" && e.Message.Contains(ContentIds.PickaxeStone)), Is.True);
    }

    [Test]
    public void SecondHerbLeafFitsTheExistingStackWhenEverySlotIsOccupied()
    {
        var (world, npc) = CleanNpc();
        npc.Inventory.Capacity = 2;
        npc.Inventory.Items.Add(ContentIds.HerbLeaf);
        npc.Inventory.Items.Add(ContentIds.Knife);

        Assert.That(npc.Inventory.HasSpace, Is.False,
            "Regression setup requires both pocket slots to be occupied.");
        Assert.That(InventoryMath.CanMakeRoomFor(world, npc, ContentIds.HerbLeaf), Is.True,
            "The second medicine leaf belongs in the existing resource stack, " +
            "so GatherHerb must not wait for an empty slot.");
    }

    // §75A: статы первичны — любимое оружие выбирает MeleePriority, а не вкус.
    // Мачете (35) обязано побеждать нож (10) у ЛЮБОГО персонажа, каким бы ни
    // был его хеш симпатии; вкус решает только между экземплярами одного класса.
    [Test]
    public void FavoriteWeapon_StatsBeatTaste_MacheteAlwaysOverKnife()
    {
        var pack = new[] { GearCatalog.Knife, GearCatalog.Machete };
        var reversed = new[] { GearCatalog.Machete, GearCatalog.Knife };
        for (var npcId = 1; npcId <= 200; npcId++)
        {
            Assert.That(ItemAffinity.FavoriteWeapon(npcId, pack), Is.EqualTo(GearCatalog.Machete));
            Assert.That(ItemAffinity.FavoriteWeapon(npcId, reversed), Is.EqualTo(GearCatalog.Machete));
        }
    }

    [Test]
    public void MissingTool_DoesNotReplaceSoleFavoriteWeapon()
    {
        var (world, npc) = CleanNpc();
        npc.Inventory.Capacity = 1;
        npc.Inventory.Items.Add(ContentIds.Knife);

        Assert.That(InventoryMath.CanMakeRoomFor(world, npc, ContentIds.PickaxeStone), Is.False,
            "The duplicate exception must not sacrifice the sole favorite weapon.");
    }

    [Test]
    public void RemovingGarment_RebuildsLayoutAndSpillsOnlyCurrentOverflow()
    {
        var (world, npc) = CleanNpc();
        AddGarment(world, "test.pockets", 2, BodyPart.Torso);
        npc.WornItems.Add("test.pockets");
        EquipmentMath.RecalculateCapacity(world, npc);
        for (var i = 0; i < npc.Inventory.Capacity; i++)
        {
            var id = "test.item." + i;
            world.Content.ObjectDefinitions[id] = new ObjectDefinition { Id = id, DisplayName = id };
            npc.Inventory.Items.Add(id);
        }

        Assert.That(InventoryLayoutBuilder.Build(world, npc).Containers
            .Single(c => c.OwnerItemDefinitionId == "test.pockets").Slots.Count, Is.EqualTo(2));

        npc.WornItems.Clear();
        EquipmentMath.RecalculateCapacity(world, npc);
        var overflow = npc.Inventory.UsedSlots - npc.Inventory.Capacity;
        var before = npc.Inventory.Items.Count;
        InventoryMath.SpillOverflow(world, npc);
        var after = InventoryLayoutBuilder.Build(world, npc);

        Assert.That(before - npc.Inventory.Items.Count, Is.EqualTo(overflow));
        Assert.That(after.Containers.Any(c => c.Kind == InventoryContainerKind.Overflow), Is.False);
        AssertCapacityParity(npc, after);
    }

    private static (HexLive.Simulation.Core.WorldState world, NPCState npc) CleanNpc()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        npc.Body.Severed.Clear();
        npc.Body.Parts[BodyPart.ArmL] = 1f;
        npc.Body.Parts[BodyPart.ArmR] = 1f;
        npc.Body.Condition(BodyPart.ArmL).Prosthetic = null;
        npc.Body.Condition(BodyPart.ArmR).Prosthetic = null;
        npc.Attributes.Strength = 0.5f;
        EquipmentMath.RecalculateCapacity(world, npc);
        return (world, npc);
    }

    private static void AddGarment(
        HexLive.Simulation.Core.WorldState world,
        string id,
        int capacity,
        BodyPart bodyPart)
    {
        var definition = new ObjectDefinition
        {
            Id = id,
            DisplayName = id,
            Layer = WearLayer.Wear,
            InventoryCapacity = capacity
        };
        definition.Covers.Add(bodyPart);
        world.Content.ObjectDefinitions[id] = definition;
    }

    private static bool IsRegular(InventoryContainerLayout container) =>
        container.Kind is InventoryContainerKind.HandLeft or
            InventoryContainerKind.HandRight or
            InventoryContainerKind.Carry or
            InventoryContainerKind.Garment;

    private static void AssertCapacityParity(NPCState npc, InventoryLayout layout)
    {
        var regular = layout.Containers.Where(IsRegular).ToArray();
        Assert.That(regular.Sum(c => c.Capacity), Is.EqualTo(npc.Inventory.Capacity));
        Assert.That(regular.SelectMany(c => c.Slots).Count(s => s.StackCount > 0),
            Is.EqualTo(npc.Inventory.UsedSlots));
        Assert.That(layout.RegularCapacity, Is.EqualTo(npc.Inventory.Capacity));
        Assert.That(layout.UsedRegularSlots, Is.EqualTo(npc.Inventory.UsedSlots));
    }
}

}
