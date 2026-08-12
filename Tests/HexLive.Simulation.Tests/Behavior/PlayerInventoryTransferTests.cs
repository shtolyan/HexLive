using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§128: player-directed, two-way looting of an unconscious person.</summary>
public sealed class PlayerInventoryTransferTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void CanTakeExactItemFromAlliedOrForeignUnconsciousPerson(bool allied)
    {
        var (engine, looter, other) = Scene(allied);
        var item = new ItemInstance("tool.hammer")
        {
            Wetness = 0.73f,
            Durability = 0.42f,
            Dirtiness = 0.31f,
            Bloodiness = 0.19f,
            ResourceAmount = 2f
        };
        other.Inventory.Items.Add(item);

        Transfer(engine, looter, other, InventoryTransferDirection.Take,
            InventoryItemSource.Carried, 0, item.DefinitionId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, item)), Is.True);
            Assert.That(looter.Inventory.Items.Single(i =>
                i.DefinitionId == item.DefinitionId), Is.SameAs(item),
                "Transfer must preserve the physical ItemInstance and all runtime state.");
            Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, item)), Is.False);
            Assert.That(looter.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        });
    }

    [Test]
    public void CanGiveOwnItemToAlliedUnconsciousPerson()
    {
        var (engine, looter, other) = Scene(allied: true);
        var gift = new ItemInstance("tool.hammer") { Durability = 0.27f };
        looter.Inventory.Items.Add(gift);

        Transfer(engine, looter, other, InventoryTransferDirection.Give,
            InventoryItemSource.Carried, 0, gift.DefinitionId, 1);

        Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, gift)), Is.True);
        Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, gift)), Is.False);
    }

    [Test]
    public void WholeVisibleResourceStackMovesByItsPhysicalSourceIndex()
    {
        var (engine, looter, other) = Scene(allied: false);
        var first = new ItemInstance("resource.stick");
        var separator = new ItemInstance("tool.hammer");
        var second = new ItemInstance("resource.stick");
        var third = new ItemInstance("resource.stick");
        other.Inventory.Items.Add(first);
        other.Inventory.Items.Add(separator);
        other.Inventory.Items.Add(second);
        other.Inventory.Items.Add(third);

        var layout = InventoryLayoutBuilder.Build(engine.World, other);
        var stack = layout.Containers.SelectMany(c => c.Slots).Single(s =>
            s.ItemDefinitionId == "resource.stick");
        Assert.That(stack.SourceIndex, Is.Zero);
        Assert.That(stack.StackCount, Is.EqualTo(3));

        Transfer(engine, looter, other, InventoryTransferDirection.Take,
            InventoryItemSource.Carried, stack.SourceIndex,
            stack.ItemDefinitionId, stack.StackCount);

        Assert.Multiple(() =>
        {
            Assert.That(looter.Inventory.Items.Where(i =>
                i.DefinitionId == "resource.stick"),
                Is.EquivalentTo(new[] { first, second, third }));
            Assert.That(other.Inventory.Items, Is.EqualTo(new[] { separator }));
        });
    }

    [Test]
    public void LayoutPublishesPhysicalStartsForLaterStackChunksAndWornOwners()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        var stackSize = InventoryState.StackSizeFor("resource.stick");
        for (var i = 0; i < stackSize; i++)
            npc.Inventory.Items.Add(new ItemInstance("resource.stick"));
        npc.Inventory.Items.Add(new ItemInstance("tool.hammer"));
        npc.Inventory.Items.Add(new ItemInstance("resource.stick"));
        npc.Inventory.Items.Add(new ItemInstance("resource.stick"));

        const string garmentId = "test.loot.owner_index";
        var garment = new ObjectDefinition
        {
            Id = garmentId,
            DisplayName = garmentId,
            Layer = WearLayer.Wear,
            InventoryCapacity = 2
        };
        garment.Covers.Add(BodyPart.Torso);
        world.Content.ObjectDefinitions[garmentId] = garment;
        npc.WornItems.Add(new ItemInstance(garmentId));
        npc.WornItems.Add(new ItemInstance("legHolster_2204"));
        EquipmentMath.RecalculateCapacity(world, npc);

        var layout = InventoryLayoutBuilder.Build(world, npc);
        var stacks = layout.Containers.SelectMany(c => c.Slots)
            .Where(s => s.ItemDefinitionId == "resource.stick").ToArray();
        var garmentContainer = layout.Containers.Single(c =>
            c.Kind == InventoryContainerKind.Garment &&
            c.OwnerItemDefinitionId == garmentId);
        var holsterContainer = layout.Containers.Single(c =>
            c.Kind == InventoryContainerKind.Holster);

        Assert.Multiple(() =>
        {
            Assert.That(stacks.Select(s => s.SourceIndex),
                Is.EqualTo(new[] { 0, stackSize + 1 }));
            Assert.That(stacks.Select(s => s.StackCount),
                Is.EqualTo(new[] { stackSize, 2 }));
            Assert.That(garmentContainer.OwnerSourceIndex, Is.Zero);
            Assert.That(holsterContainer.OwnerSourceIndex, Is.EqualTo(1));
        });
    }

    [Test]
    public void FilledPocketGarmentMovesAsOneWornBundleWithItsDisplayedContents()
    {
        var (engine, looter, other) = Scene(allied: false);
        const string garmentId = "test.loot.pocket_jacket";
        var definition = new ObjectDefinition
        {
            Id = garmentId,
            DisplayName = garmentId,
            Layer = WearLayer.Wear,
            InventoryCapacity = 2
        };
        definition.Covers.Add(BodyPart.Torso);
        engine.World.Content.ObjectDefinitions[garmentId] = definition;
        var jacket = new ItemInstance(garmentId);
        other.WornItems.Add(jacket);
        EquipmentMath.RecalculateCapacity(engine.World, other);
        var leftPocket = new ItemInstance("tool.hammer") { Durability = 0.37f };
        var rightPocket = new ItemInstance("resource.stone") { Wetness = 0.62f };
        other.Inventory.Items.Add(leftPocket);
        other.Inventory.Items.Add(rightPocket);

        Transfer(engine, looter, other, InventoryTransferDirection.Take,
            InventoryItemSource.Worn, 0, garmentId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(other.WornItems.Any(i => ReferenceEquals(i, jacket)), Is.False);
            Assert.That(looter.WornItems.Any(i => ReferenceEquals(i, jacket)), Is.True,
                "The jacket must occupy a free body slot, not another pocket.");
            Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, jacket)), Is.False);
            Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, leftPocket)), Is.True);
            Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, rightPocket)), Is.True);
            Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, leftPocket)), Is.False);
            Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, rightPocket)), Is.False);
        });
    }

    [Test]
    public void FilledGarmentCannotReplaceAnOccupiedWearSlot()
    {
        var (engine, looter, other) = Scene(allied: false);
        const string incomingId = "test.loot.incoming_jacket";
        const string occupiedId = "test.loot.occupied_jacket";
        AddGarment(engine.World, incomingId, 2, BodyPart.Torso);
        AddGarment(engine.World, occupiedId, 1, BodyPart.Torso);
        var incoming = new ItemInstance(incomingId);
        var occupied = new ItemInstance(occupiedId);
        var pocketItem = new ItemInstance("tool.hammer");
        other.WornItems.Add(incoming);
        other.Inventory.Items.Add(pocketItem);
        looter.WornItems.Add(occupied);
        EquipmentMath.RecalculateCapacity(engine.World, other);
        EquipmentMath.RecalculateCapacity(engine.World, looter);

        Transfer(engine, looter, other, InventoryTransferDirection.Take,
            InventoryItemSource.Worn, 0, incomingId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(other.WornItems.Any(i => ReferenceEquals(i, incoming)), Is.True);
            Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, pocketItem)), Is.True);
            Assert.That(looter.WornItems.Any(i => ReferenceEquals(i, occupied)), Is.True);
            Assert.That(looter.WornItems.Any(i => ReferenceEquals(i, incoming)), Is.False,
                "A bundle may only enter a genuinely free wear slot.");
        });
    }

    [Test]
    public void CanGiveAWholeFilledGarmentAndItRemainsWorn()
    {
        var (engine, looter, other) = Scene(allied: true);
        const string garmentId = "test.loot.given_jacket";
        AddGarment(engine.World, garmentId, 1, BodyPart.Torso);
        var jacket = new ItemInstance(garmentId);
        var pocketItem = new ItemInstance("tool.hammer");
        looter.WornItems.Add(jacket);
        looter.Inventory.Items.Add(pocketItem);
        EquipmentMath.RecalculateCapacity(engine.World, looter);

        Transfer(engine, looter, other, InventoryTransferDirection.Give,
            InventoryItemSource.Worn, 0, garmentId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(other.WornItems.Any(i => ReferenceEquals(i, jacket)), Is.True);
            Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, pocketItem)), Is.True);
            Assert.That(looter.WornItems.Any(i => ReferenceEquals(i, jacket)), Is.False);
            Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, pocketItem)), Is.False);
        });
    }

    [Test]
    public void BackpackBundleMovesOnlyItsBonusTailNotBodyCarryOrHands()
    {
        var (engine, looter, other) = Scene(allied: false);
        const string backpackId = "gear.backpack_osiris";
        var backpack = new ItemInstance(backpackId);
        other.WornItems.Add(backpack);
        EquipmentMath.RecalculateCapacity(engine.World, other);
        var carry = InventoryLayoutBuilder.Build(engine.World, other).Containers.Single(c =>
            c.Kind == InventoryContainerKind.Carry);
        var bodyItems = new ItemInstance[carry.BaseCapacity + carry.StrengthBonus];
        var backpackItems = new ItemInstance[carry.BackpackCapacity];
        for (var i = 0; i < bodyItems.Length; i++)
        {
            bodyItems[i] = AddPlainItem(engine.World, "test.loot.body_carry." + i);
            other.Inventory.Items.Add(bodyItems[i]);
        }
        for (var i = 0; i < backpackItems.Length; i++)
        {
            backpackItems[i] = AddPlainItem(engine.World, "test.loot.backpack." + i);
            other.Inventory.Items.Add(backpackItems[i]);
        }

        Transfer(engine, looter, other, InventoryTransferDirection.Take,
            InventoryItemSource.Worn, 0, backpackId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(looter.WornItems.Any(i => ReferenceEquals(i, backpack)), Is.True);
            Assert.That(backpackItems.All(item => looter.Inventory.Items.Any(candidate =>
                ReferenceEquals(candidate, item))), Is.True);
            Assert.That(bodyItems.All(item => other.Inventory.Items.Any(candidate =>
                ReferenceEquals(candidate, item))), Is.True,
                "Merged body-carry cells must remain with the original body.");
            Assert.That(bodyItems.Any(item => looter.Inventory.Items.Any(candidate =>
                ReferenceEquals(candidate, item))), Is.False);
        });
    }

    [Test]
    public void FilledGarmentCannotBeStowedInsideAnotherPocket()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        const string garmentId = "test.loot.no_nested_jacket";
        AddGarment(world, garmentId, 2, BodyPart.Torso);
        npc.WornItems.Add(new ItemInstance(garmentId));
        npc.Inventory.Items.Add(new ItemInstance("tool.hammer"));
        EquipmentMath.RecalculateCapacity(world, npc);
        var reference = new InventoryItemRef(
            InventoryItemSource.Worn, 0, garmentId);

        Assert.That(PlayerInventoryMath.FitsAfter(
            world, npc, reference, InventoryAction.Stow), Is.False,
            "A jacket with a real item in its derived pocket cannot be nested.");

        npc.Inventory.Items.Clear();
        Assert.That(PlayerInventoryMath.FitsAfter(
            world, npc, reference, InventoryAction.Stow), Is.True,
            "An empty garment may still be folded into ordinary free carry space.");
    }

    [Test]
    public void BottleCarriesItsNpcBackedWaterStateAndCannotMergeWithAnotherBottle()
    {
        var (engine, looter, other) = Scene(allied: false);
        var bottle = new ItemInstance(ContentIds.Bottle);
        other.Inventory.Items.Add(bottle);
        other.BottleWater = WaterKind.Boiled;
        other.BottleCharges = 3;

        Transfer(engine, looter, other, InventoryTransferDirection.Take,
            InventoryItemSource.Carried, 0, ContentIds.Bottle, 1);

        Assert.Multiple(() =>
        {
            Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, bottle)), Is.True);
            Assert.That(looter.BottleWater, Is.EqualTo(WaterKind.Boiled));
            Assert.That(looter.BottleCharges, Is.EqualTo(3));
            Assert.That(other.BottleWater, Is.EqualTo(WaterKind.None));
            Assert.That(other.BottleCharges, Is.Zero);
        });

        other.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        Transfer(engine, looter, other, InventoryTransferDirection.Give,
            InventoryItemSource.Carried, 0, ContentIds.Bottle, 1);

        Assert.Multiple(() =>
        {
            Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, bottle)), Is.True,
                "The legacy per-NPC water store cannot safely represent two bottles.");
            Assert.That(looter.BottleCharges, Is.EqualTo(3));
        });
    }

    [Test]
    public void ConsciousTargetRejectsTransferWithoutMutation()
    {
        var (engine, looter, other) = Scene(allied: false);
        var item = new ItemInstance("tool.hammer");
        other.Inventory.Items.Add(item);
        other.Mind.FaintedUntilTick = 0;

        Transfer(engine, looter, other, InventoryTransferDirection.Take,
            InventoryItemSource.Carried, 0, item.DefinitionId, 1);

        Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, item)), Is.True);
        Assert.That(looter.Inventory.Items.Any(i => ReferenceEquals(i, item)), Is.False);
    }

    [Test]
    public void DistantTransferUsesTheOrdinaryPersonApproachBeforeMovingTheItem()
    {
        var (engine, looter, other) = Scene(allied: false);
        var world = engine.World;
        var destination = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0 && SpatialQueries.IsJunctionFree(world, j.Id) &&
            HexSpatialMath.HexDistance(looter.Tile, j.Tiles[0]) is >= 3 and <= 5 &&
            Connectivity.Reachable(world, looter.CurrentJunction!.Value, j.Id));
        PlaceAt(world, other, destination);
        var item = new ItemInstance("tool.hammer");
        other.Inventory.Items.Add(item);

        engine.Commands.Enqueue(new TransferInventoryCommand(
            looter.Id, other.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, item.DefinitionId),
            1, InventoryTransferDirection.Take));
        engine.Step();
        for (var i = 1; i < 600 &&
             !looter.Inventory.Items.Any(candidate => ReferenceEquals(candidate, item)); i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(looter.Inventory.Items.Any(candidate =>
                ReferenceEquals(candidate, item)), Is.True,
                "The player command never completed after its approach walk.");
            Assert.That(other.Inventory.Items.Any(candidate =>
                ReferenceEquals(candidate, item)), Is.False);
            Assert.That(looter.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        });
    }

    private static void AddGarment(
        WorldState world, string id, int capacity, BodyPart bodyPart)
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

    private static ItemInstance AddPlainItem(WorldState world, string id)
    {
        world.Content.ObjectDefinitions[id] = new ObjectDefinition
            { Id = id, DisplayName = id };
        return new ItemInstance(id);
    }

    private static (SimulationEngine engine, NPCState looter, NPCState other) Scene(bool allied)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var looter = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var other = allied
            ? world.Entities.Npcs.Values.First(n =>
                n.Faction == Faction.Colony && !n.Id.Equals(looter.Id))
            : world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);

        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Mind.ManualControl = true;
        }
        looter.Inventory.Items.Clear();
        looter.WornItems.Clear();
        other.Inventory.Items.Clear();
        other.WornItems.Clear();
        EquipmentMath.RecalculateCapacity(world, looter);
        EquipmentMath.RecalculateCapacity(world, other);
        PlaceAdjacent(world, looter, other);
        other.Mind.FaintedUntilTick = world.Tick + 10000;
        Assert.That(other.IsUnconscious(world.Tick), Is.True);
        return (engine, looter, other);
    }

    private static void Transfer(
        SimulationEngine engine,
        NPCState looter,
        NPCState other,
        InventoryTransferDirection direction,
        InventoryItemSource source,
        int index,
        string definitionId,
        int count)
    {
        engine.Commands.Enqueue(new TransferInventoryCommand(
            looter.Id, other.Id,
            new InventoryItemRef(source, index, definitionId), count, direction));
        engine.Step();
    }

    private static void PlaceAdjacent(WorldState world, NPCState looter, NPCState other)
    {
        var from = world.Junctions.Items.Values.First(j =>
            !j.Blocked && SpatialQueries.IsJunctionFree(world, j.Id) &&
            j.Neighbors.Any(id => SpatialQueries.IsJunctionFree(world, id)));
        var to = world.Junctions.Items[from.Neighbors.First(id =>
            SpatialQueries.IsJunctionFree(world, id))];
        PlaceAt(world, looter, from);
        PlaceAt(world, other, to);
    }

    private static void PlaceAt(WorldState world, NPCState person, Junction destination)
    {
        if (person.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, person.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, person.Id);
        }

        var oldTile = person.Tile;
        person.CurrentJunction = destination.Id;
        person.Position = destination.WorldPosition;
        person.Tile = destination.Tiles[0];
        person.Fragment = destination.Fragment;
        SpatialMutations.MoveEntityToTile(world, person.Id, oldTile, person.Tile);
        SpatialMutations.OccupyJunction(world, destination.Id, person.Id);
    }
}

}
