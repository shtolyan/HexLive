using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class FlashlightTests
{
    [Test]
    public void InventoryToggleTargetsThePhysicalSlotAndOnlyPoweredBatteriesDrain()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var actor = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        actor.Inventory.Items.Clear();
        var spare = new ItemInstance(ContentIds.Flashlight);
        var selected = new ItemInstance(ContentIds.Flashlight) { Durability = .5f };
        actor.Inventory.Items.Add(spare);
        actor.Inventory.Items.Add(selected);
        var order = new ManageInventoryCommand(actor.Id, FlashlightAt(1), InventoryAction.ToggleFlashlight);
        using var commandBytes = new MemoryStream();
        using (var writer = new BinaryWriter(commandBytes, System.Text.Encoding.UTF8, leaveOpen: true))
            SimulationCommandCodec.Write(writer, order);
        commandBytes.Position = 0;
        using var reader = new BinaryReader(commandBytes);
        var accepted = engine.ApplyManualCommand(SimulationCommandCodec.Read(reader));
        Assert.That(accepted.Accepted, Is.True, accepted.Reason);
        Assert.That(selected.ResourceAmount, Is.EqualTo(1f));
        Assert.That(spare.ResourceAmount, Is.Zero);
        Assert.That(ActionItemResolver.HeldId(world, actor), Is.EqualTo(ContentIds.Flashlight));

        new FireSystem().Run(world);
        Assert.That(selected.Durability, Is.LessThan(.5f));
        Assert.That(spare.Durability, Is.EqualTo(1f));
        Assert.That(engine.ApplyManualCommand(order).Accepted, Is.True);
        var switchedOffCharge = selected.Durability;
        new FireSystem().Run(world);
        Assert.That(selected.Durability, Is.EqualTo(switchedOffCharge));
        Assert.That(ActionItemResolver.HeldId(world, actor), Is.Empty);

        selected.Durability = 0f;
        Assert.That(engine.ApplyManualCommand(order).Reason, Is.EqualTo("BatteryEmpty"));
        Assert.That(actor.Inventory.Items[1], Is.SameAs(selected));
    }

    [Test]
    public void OrdinaryDropAndGroundOrdersPreserveSwitchAndBatteryOnPickup()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        foreach (var person in world.Entities.Npcs.Values)
            engine.ApplyManualCommand(new SetManualControlCommand(person.Id, true));
        engine.Step();
        var actor = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        actor.Needs.Hunger = actor.Needs.Thirst = 0f;
        actor.Inventory.Items.Clear();
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.Flashlight) { ResourceAmount = 1f, Durability = .6f });

        var drop = engine.ApplyManualCommand(new ManageInventoryCommand(actor.Id, FlashlightAt(0), InventoryAction.Drop));
        Assert.That(drop.Accepted, Is.True, drop.Reason);
        var placed = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.Flashlight);
        Assert.That(FlashlightMath.IsOn(placed.ResourceAmount, placed.Durability), Is.True);
        Assert.That(placed.Durability, Is.EqualTo(.6f));

        var off = engine.ApplyManualCommand(new InteractCommand(actor.Id, placed.Id,
            InteractionType.SwitchFlashlight, FlashlightMath.SwitchOffInteractionId));
        Assert.That(off.Accepted, Is.True, off.Reason);
        for (var i = 0; i < 400 && placed.ResourceAmount != 0f; i++) engine.Step();
        Assert.That(placed.ResourceAmount, Is.Zero);
        Assert.That(placed.IsOccupied, Is.False);

        var on = engine.ApplyManualCommand(new InteractCommand(actor.Id, placed.Id,
            InteractionType.SwitchFlashlight, FlashlightMath.SwitchOnInteractionId));
        Assert.That(on.Accepted, Is.True, on.Reason);
        for (var i = 0; i < 400 && placed.ResourceAmount == 0f; i++) engine.Step();
        Assert.That(FlashlightMath.IsOn(placed.ResourceAmount, placed.Durability), Is.True);
        Assert.That(placed.IsOccupied, Is.False);

        var pickup = engine.ApplyManualCommand(new InteractCommand(actor.Id, placed.Id, InteractionType.PickUp));
        Assert.That(pickup.Accepted, Is.True, pickup.Reason);
        for (var i = 0; i < 400 && actor.Inventory.Items.Count == 0; i++) engine.Step();
        var picked = actor.Inventory.Items.Single(item => item.DefinitionId == ContentIds.Flashlight);
        Assert.That(world.Entities.Objects.ContainsKey(placed.Id), Is.False);
        Assert.That(FlashlightMath.IsOn(picked.ResourceAmount, picked.Durability), Is.True);
        Assert.That(picked.Durability, Is.InRange(.49f, .6f));
    }

    [Test]
    public void PoweredGroundAndCarriedFlashlightsSurviveSaveAndWireAndLeaveShellsAtZero()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        actor.Inventory.Items.Clear();
        const float charge = .0001f;
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.Flashlight) { ResourceAmount = 1f, Durability = charge });
        var lampAnchor = world.Tiles.Items[actor.Tile].Junctions.First(id => !world.Junctions.Items[id].Blocked);
        var lamp = WorldObjectMutations.SpawnObject(world, ContentIds.Flashlight, actor.Fragment, actor.Tile, lampAnchor);
        lamp.ResourceAmount = 1f;
        lamp.Durability = charge;

        using var save = new MemoryStream();
        using (var writer = new BinaryWriter(save, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);
        save.Position = 0;
        var restored = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(save, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(restored, reader);
        var restoredLamp = restored.Entities.Objects[lamp.Id];
        var restoredItem = restored.Entities.Npcs[actor.Id].Inventory.Items.Single();
        Assert.That(restoredLamp.Durability, Is.EqualTo(charge));
        Assert.That(restoredItem.Durability, Is.EqualTo(charge));
        Assert.That(restoredLamp.ResourceAmount, Is.EqualTo(1f));
        Assert.That(restoredItem.ResourceAmount, Is.EqualTo(1f));

        using var wire = new MemoryStream();
        using (var writer = new BinaryWriter(wire, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSnapshotCodec.Write(WorldSnapshotExporter.Export(restored), writer, includeDebugDetails: false);
        wire.Position = 0;
        using var wireReader = new BinaryReader(wire);
        var remote = new WorldSnapshot();
        WorldSnapshotCodec.Read(wireReader, remote);
        Assert.That(remote.Objects.Single(obj => obj.Id == lamp.Id).ResourceAmount, Is.EqualTo(1f));
        var remoteActor = remote.Npcs.Single(person => person.Id == actor.Id);
        Assert.That(remoteActor.HeldItemId, Is.EqualTo(ContentIds.Flashlight));
        Assert.That(remoteActor.InventoryContainers.SelectMany(container => container.Slots)
            .Single(slot => slot.ItemDefinitionId == ContentIds.Flashlight).ResourceAmount, Is.EqualTo(1f));

        new FireSystem().Run(restored);
        Assert.That(restoredLamp.ResourceAmount, Is.Zero);
        Assert.That(restoredLamp.Durability, Is.Zero);
        Assert.That(restoredItem.ResourceAmount, Is.Zero);
        Assert.That(restoredItem.Durability, Is.Zero);
        Assert.That(restored.Entities.Objects[lamp.Id], Is.SameAs(restoredLamp));
        Assert.That(restored.Entities.Npcs[actor.Id].Inventory.Items.Single(), Is.SameAs(restoredItem));
        Assert.That(GearCatalog.For(ContentIds.Flashlight).Capabilities & GearCapability.Ignite,
            Is.EqualTo(GearCapability.None));
    }

    private static InventoryItemRef FlashlightAt(int index) =>
        new(InventoryItemSource.Carried, index, ContentIds.Flashlight);
}
