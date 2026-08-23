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
