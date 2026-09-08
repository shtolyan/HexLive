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

/// <summary>§123/§128 / bug #215: inventory authority is independent of AI/manual mode.</summary>
public sealed class InventoryControlModeTests
{
    [TestCase(false, "carried")]
    [TestCase(true, "carried")]
    [TestCase(false, "worn")]
    [TestCase(true, "worn")]
    [TestCase(false, "container")]
    [TestCase(true, "container")]
    public void TakeAndWearDoesNotNeedTemporaryInventorySpace(bool manual, string origin)
    {
        var engine = TestWorld.CreateEngine(36701);
        var world = engine.World;
        var npc = Colonist(world);
        var other = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony && n.Id != npc.Id);
        foreach (var n in world.Entities.Npcs.Values)
        {
            n.Mind.ManualControl = true;
            n.Needs.Hunger = 0f;
            n.Needs.Thirst = 0f;
        }
        npc.Mind.ManualControl = manual;
        npc.Mind.OutfitLocked = false;
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        other.Inventory.Items.Clear();
        other.WornItems.Clear();
        other.Mind.FaintedUntilTick = world.Tick + 10000;
        PlaceAdjacent(world, npc, other);
        const string oldId = "test.quick_wear.old";
        const string newId = "test.quick_wear.new";
        foreach (var id in new[] { oldId, newId })
        {
            var definition = new ObjectDefinition { Id = id, DisplayName = id, Layer = WearLayer.Wear };
            definition.Covers.Add(BodyPart.Torso);
            world.Content.ObjectDefinitions[id] = definition;
        }
        var old = new ItemInstance(oldId) { Dirtiness = .2f };
        var incoming = new ItemInstance(newId) { Dirtiness = .7f, OwnerId = other.Id.Value };
        npc.WornItems.Add(old);
        EquipmentMath.RecalculateCapacity(world, npc);
        while (npc.Inventory.UsedSlots < npc.Inventory.Capacity)
        {
            var id = "test.quick_wear.cargo." + npc.Inventory.Items.Count;
            world.Content.ObjectDefinitions[id] = new ObjectDefinition { Id = id, DisplayName = id };
            npc.Inventory.Items.Add(new ItemInstance(id));
        }
        var cargo = npc.Inventory.Items.ToArray();
        ManualCommandAdmission admission;
        if (origin == "container")
        {
            var anchor = world.Junctions.Items[npc.CurrentJunction!.Value].Neighbors
                .Select(id => world.Junctions.Items[id])
                .First(j => SpatialQueries.IsJunctionFree(world, j.Id));
            var container = WorldObjectMutations.SpawnObject(world, ContentIds.HumanRemains,
                anchor.Fragment, anchor.Tiles[0], anchor.Id);
            container.Contents.Add(incoming);
            admission = engine.ApplyManualCommand(new TransferContainerCommand(
                npc.Id, container.Id, 0, newId, 1, InventoryTransferDirection.TakeAndWear));
        }
        else
        {
            (origin == "worn" ? other.WornItems : other.Inventory.Items).Add(incoming);
            EquipmentMath.RecalculateCapacity(world, other);
            admission = engine.ApplyManualCommand(new TransferInventoryCommand(npc.Id, other.Id,
                new InventoryItemRef(origin == "worn" ? InventoryItemSource.Worn : InventoryItemSource.Carried,
                    0, newId), 1, InventoryTransferDirection.TakeAndWear));
        }
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        StepUntil(engine, () => npc.WornItems.Any(i => ReferenceEquals(i, incoming)));
        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.ManualControl, Is.EqualTo(manual));
            Assert.That(npc.WornItems.Single(), Is.SameAs(incoming));
            Assert.That(incoming.Dirtiness, Is.EqualTo(.7f).Within(.001f),
                "The full simulation tick adds normal wear dirt after the transfer.");
            Assert.That(npc.Inventory.Items, Is.EquivalentTo(cargo));
            Assert.That(npc.Inventory.UsedSlots, Is.LessThanOrEqualTo(npc.Inventory.Capacity));
            Assert.That(world.Entities.Objects.Values.Single(o => o.DefinitionId == oldId).Dirtiness,
                Is.EqualTo(.2f));
            Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, incoming)), Is.False);
            Assert.That(other.WornItems.Any(i => ReferenceEquals(i, incoming)), Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OutfitLockAndOwnInventoryActionsWorkInBothControlModes_Bug215(
        bool manualControl)
    {
        var engine = TestWorld.CreateEngine(215001);
        var world = engine.World;
        var npc = Colonist(world);
        npc.Mind.ManualControl = manualControl;
        npc.Mind.CurrentGoal = GoalType.GetFood;
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.LeatherPants));
        npc.Inventory.Items.Add(new ItemInstance("tool.hammer"));
        EquipmentMath.RecalculateCapacity(world, npc);

        var lockOn = engine.ApplyManualCommand(new SetOutfitLockCommand(npc.Id, true));
        var lockOff = engine.ApplyManualCommand(new SetOutfitLockCommand(npc.Id, false));
        var wear = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(
                InventoryItemSource.Carried, 0, ContentIds.LeatherPants),
            InventoryAction.Wear));
        var stow = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(
                InventoryItemSource.Worn, 0, ContentIds.LeatherPants),
            InventoryAction.Stow));
        var drop = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, "tool.hammer"),
            InventoryAction.Drop));

        Assert.Multiple(() =>
        {
            Assert.That(lockOn.Accepted, Is.True);
            Assert.That(lockOff.Accepted, Is.True);
            Assert.That(wear.Accepted, Is.True);
            Assert.That(stow.Accepted, Is.True);
            Assert.That(drop.Accepted, Is.True);
            Assert.That(npc.Mind.ManualControl, Is.EqualTo(manualControl));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.GetFood),
                "Immediate inventory management must not replace the current AI/manual goal.");
            Assert.That(npc.WornItems, Is.Empty);
            Assert.That(npc.Inventory.Items.Any(item =>
                item.DefinitionId == ContentIds.LeatherPants), Is.True);
        });
    }

    [Test]
    public void PersonAndContainerTransfersAreAcceptedInAiModeWithoutTakingManualControl_Bug215()
    {
        var engine = TestWorld.CreateEngine(215002);
        var world = engine.World;
        var looter = Colonist(world);
        var other = world.Entities.Npcs.Values.First(npc =>
            npc.Faction == Faction.Colony && npc.Id != looter.Id);
        looter.Mind.ManualControl = false;
        looter.Mind.ManualControlLeaseRenewedAtSeconds = null;
        looter.Needs.Hunger = 0f;
        looter.Needs.Thirst = 0f;
        looter.Inventory.Items.Clear();
        looter.WornItems.Clear();
        other.Inventory.Items.Clear();
        other.WornItems.Clear();
        other.Mind.FaintedUntilTick = world.Tick + 10000;
        EquipmentMath.RecalculateCapacity(world, looter);
        EquipmentMath.RecalculateCapacity(world, other);
        PlaceAdjacent(world, looter, other);

        var hammer = new ItemInstance("tool.hammer");
        other.Inventory.Items.Add(hammer);
        var personAdmission = engine.ApplyManualCommand(new TransferInventoryCommand(
            looter.Id, other.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, hammer.DefinitionId),
            1, InventoryTransferDirection.Take));
        StepUntil(engine, () => looter.Inventory.Items.Contains(hammer));

        var objectAnchor = world.Junctions.Items[looter.CurrentJunction!.Value].Neighbors
            .Select(id => world.Junctions.Items[id])
            .First(junction => SpatialQueries.IsJunctionFree(world, junction.Id));
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, objectAnchor.Fragment,
            objectAnchor.Tiles[0], objectAnchor.Id);
        var stone = new ItemInstance(ContentIds.Stone);
        remains.Contents.Add(stone);
        var containerAdmission = engine.ApplyManualCommand(new TransferContainerCommand(
            looter.Id, remains.Id, 0, stone.DefinitionId, 1,
            InventoryTransferDirection.Take));
        StepUntil(engine, () => looter.Inventory.Items.Contains(stone));

        Assert.Multiple(() =>
        {
            Assert.That(personAdmission.Accepted, Is.True, personAdmission.Reason);
            Assert.That(containerAdmission.Accepted, Is.True, containerAdmission.Reason);
            Assert.That(looter.Mind.ManualControl, Is.False,
                "Inventory transfers must not flip an AI-controlled NPC to manual mode.");
            Assert.That(looter.Mind.ManualControlLeaseRenewedAtSeconds, Is.Null,
                "An AI-mode inventory transfer must not create a dormant manual lease.");
            Assert.That(looter.Inventory.Items.Contains(hammer), Is.True);
            Assert.That(looter.Inventory.Items.Contains(stone), Is.True);
        });
    }

    [Test]
    public void ForeignNpcStillCannotReceiveInventoryCommands_Bug215()
    {
        var engine = TestWorld.CreateEngine(215003);
        var foreign = engine.World.Entities.Npcs.Values.First(npc =>
            npc.Faction != Faction.Colony);

        var lockResult = engine.ApplyManualCommand(
            new SetOutfitLockCommand(foreign.Id, true));
        var manageResult = engine.ApplyManualCommand(new ManageInventoryCommand(
            foreign.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, "tool.hammer"),
            InventoryAction.Drop));

        Assert.Multiple(() =>
        {
            Assert.That(lockResult.Accepted, Is.False);
            Assert.That(lockResult.Reason, Is.EqualTo("NotOwned"));
            Assert.That(manageResult.Accepted, Is.False);
            Assert.That(manageResult.Reason, Is.EqualTo("NotOwned"));
        });
    }

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc =>
            npc.Faction == Faction.Colony && npc.Health > 0f);

    private static void StepUntil(SimulationEngine engine, System.Func<bool> done)
    {
        for (var i = 0; i < 600 && !done(); i++)
        {
            engine.Step();
        }
        Assert.That(done(), Is.True, "Inventory transfer did not complete in 600 ticks.");
    }

    private static void PlaceAdjacent(
        WorldState world, NPCState looter, NPCState other)
    {
        var from = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && SpatialQueries.IsJunctionFree(world, junction.Id) &&
            junction.Neighbors.Any(id => SpatialQueries.IsJunctionFree(world, id)));
        var to = world.Junctions.Items[from.Neighbors.First(id =>
            SpatialQueries.IsJunctionFree(world, id))];
        PlaceAt(world, looter, from);
        PlaceAt(world, other, to);
    }

    private static void PlaceAt(
        WorldState world, NPCState person, Junction destination)
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
