using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class AdminWorldCommandTests
{
    [TestCase(BodyPart.ArmL)] [TestCase(BodyPart.ArmR)]
    [TestCase(BodyPart.LegL)] [TestCase(BodyPart.LegR)]
    public void RestoreLimb_ClearsAmputationAndLocalTrauma_AndSurvivesSave(BodyPart part)
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First();
        npc.Body.Sever(part); npc.Body.Condition(part).CriticalTrauma = .4f;
        npc.Body.Parts[BodyPart.Torso] = .6f; npc.Needs.Blood = .4f;
        var command = new AdminCommand { NpcId = npc.Id.Value, Kind = "restore_limb", Target = part.ToString() };
        Assert.That(AdminWorldCommands.Execute(world, command).Accepted, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(npc.Body.IsSevered(part), Is.False);
            Assert.That(npc.Body.Parts[part], Is.EqualTo(1));
            Assert.That(npc.Body.Condition(part).CriticalTrauma, Is.Zero);
            Assert.That(npc.Body.Parts[BodyPart.Torso], Is.EqualTo(.6f));
            Assert.That(npc.Needs.Blood, Is.EqualTo(.4f));
        });
        using var blob = new MemoryStream();
        using (var w = new BinaryWriter(blob, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, w);
        blob.Position = 0; var restored = TestWorld.CreateWorld();
        using (var r = new BinaryReader(blob, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(restored, r);
        Assert.That(restored.Entities.Npcs[npc.Id].Body.IsSevered(part), Is.False);
    }
    [Test]
    public void Heal_IsNotRegeneration()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First(); npc.Body.Sever(BodyPart.LegL);
        Assert.That(AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "heal" }).Accepted, Is.True);
        Assert.That(npc.Body.IsSevered(BodyPart.LegL), Is.True); Assert.That(npc.Body.Parts[BodyPart.LegL], Is.Zero);
    }
    [Test]
    public void BestProsthetic_UsesFunctionAndDurability_AndRepeatDoesNotCreateItem()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First(); npc.Body.Sever(BodyPart.LegL);
        var command = new AdminCommand { NpcId = npc.Id.Value, Kind = "fit_prosthetic", Target = "LegL", DefinitionId = "best" };
        var before = npc.Inventory.Items.Count;
        Assert.That(AdminWorldCommands.Execute(world, command).Accepted, Is.True);
        Assert.That(npc.Body.Condition(BodyPart.LegL).Prosthetic.DefinitionId, Is.EqualTo(ContentIds.MechanicalLeg));
        Assert.That(npc.Body.IsSevered(BodyPart.LegL), Is.True);
        Assert.That(npc.Body.CanJump, Is.True);
        Assert.That(AdminWorldCommands.Execute(world, command).Accepted, Is.True);
        Assert.That(npc.Inventory.Items.Count, Is.EqualTo(before));
    }
    [Test]
    public void WrongProstheticAndAmbiguousLimb_AreAtomicRefusals()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First();
        npc.Body.Sever(BodyPart.LegL); npc.Body.Sever(BodyPart.ArmR);
        Assert.That(AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "fit_prosthetic" }).Reason, Is.EqualTo("AmbiguousLimb"));
        Assert.That(AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "fit_prosthetic", Target = "LegL", DefinitionId = ContentIds.MechanicalArm }).Reason,
            Is.EqualTo("IncompatibleProsthetic"));
        Assert.That(npc.Body.Condition(BodyPart.LegL).Prosthetic, Is.Null);
    }
    [Test]
    public void FullInventory_RestoringWithExistingDeviceDoesNotLoseDevice()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First();
        npc.Body.Sever(BodyPart.LegL); npc.Inventory.Items.Clear(); npc.Inventory.Capacity = 0;
        var device = AdminProsthetics.Create(world, BodyPart.LegL, ContentIds.WoodenLeg);
        npc.Body.Condition(BodyPart.LegL).Prosthetic = device;
        Assert.That(AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "restore_limb", Target = "LegL" }).Reason, Is.EqualTo("InventoryFull"));
        Assert.That(npc.Body.IsSevered(BodyPart.LegL), Is.True);
        Assert.That(npc.Body.Condition(BodyPart.LegL).Prosthetic, Is.SameAs(device));
    }
    [Test]
    public void AllNeeds_RespectsOppositeScaleDirections_AndInvalidStatDoesNotMutate()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First();
        AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "set_need", Target = "all" });
        Assert.Multiple(() => { Assert.That(npc.Needs.Hunger, Is.Zero); Assert.That(npc.Needs.Thirst, Is.Zero); Assert.That(npc.Needs.Energy, Is.EqualTo(1)); });
        var strength = npc.Attributes.Strength;
        Assert.That(AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "set_attribute", Target = "Strength", Value = float.NaN }).Accepted, Is.False);
        Assert.That(npc.Attributes.Strength, Is.EqualTo(strength));
    }
    [Test]
    public void RandomGarment_GiveAndEquipUseSameCatalogAndPreserveDisplacedItems()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        npc.Inventory.Items.Clear(); npc.Inventory.Capacity = 100;
        var command = new AdminCommand { NpcId = npc.Id.Value, Kind = "give_garment", Category = "skirt", DefinitionId = "random", OperationId = Guid.NewGuid().ToString("N") };
        var result = AdminWorldCommands.Execute(world, command);
        Assert.That(result.Accepted, Is.True);
        Assert.That(result.DefinitionId, Is.Not.Empty);
        Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == result.DefinitionId), Is.True);
        npc.Inventory.Items.Clear(); npc.WornItems.Clear();
        command.Kind = "equip_garment";
        result = AdminWorldCommands.Execute(world, command);
        Assert.That(result.Accepted, Is.True);
        Assert.That(npc.WornItems.Single().DefinitionId, Is.EqualTo(result.DefinitionId));
        var worn = npc.WornItems.Single();
        result = AdminWorldCommands.Execute(world, command);
        Assert.That(result.Accepted, Is.True);
        Assert.That(npc.Inventory.Items.Any(i => ReferenceEquals(i, worn)), Is.True);
    }
    [Test]
    public void GarmentRefusalDoesNotChangeWardrobeOrInventory()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First();
        var before = npc.WornItems.ToArray(); var inventory = npc.Inventory.Items.ToArray();
        var result = AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "equip_garment", DefinitionId = "does.not.exist" });
        Assert.That(result.Accepted, Is.False);
        Assert.That(npc.WornItems, Is.EqualTo(before)); Assert.That(npc.Inventory.Items, Is.EqualTo(inventory));
        npc.Inventory.Items.Clear(); npc.Inventory.Capacity = 0;
        result = AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "give_garment", Category = "skirt", DefinitionId = "random" });
        Assert.That(result.Accepted, Is.False); Assert.That(npc.Inventory.Items, Is.Empty);
    }

    [Test]
    public void EquippingIntoAnOverfullInventoryIsAtomic()
    {
        var world = TestWorld.CreateWorld(); var npc = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        var before = npc.WornItems.ToArray(); npc.Inventory.Capacity = 0;
        for (var i = 0; i < 1000; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.MechanicalLeg));
        var inventory = npc.Inventory.Items.ToArray();
        var result = AdminWorldCommands.Execute(world, new AdminCommand { NpcId = npc.Id.Value, Kind = "equip_garment", Category = "skirt", DefinitionId = "random" });
        Assert.That(result.Reason, Is.EqualTo("InventoryFull"));
        Assert.That(npc.WornItems.Count, Is.EqualTo(before.Length));
        for (var i = 0; i < before.Length; i++) Assert.That(npc.WornItems[i], Is.SameAs(before[i]));
        Assert.That(npc.Inventory.Items, Is.EqualTo(inventory));
    }

    [Test]
    public void CameraSpawnUsesTheExactFreeGroundPoint_AndBlockedAreaDoesNotSpawn()
    {
        var world = TestWorld.CreateWorld(); var source = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        var tile = world.Tiles.Items[source.Tile];
        var point = tile.Junctions.Select(id => world.Junctions.Items[id]).First(j => !j.Blocked &&
            HexLive.Simulation.Spatial.SpatialQueries.IsJunctionFree(world, j.Id) &&
            !HexLive.Simulation.Spatial.SpatialQueries.IsAllWaterJunction(world, j.Id));
        var command = new AdminCommand { Kind = "spawn_npc", DefinitionId = "colonist", Target = source.Faction.ToString(),
            Location = "camera", TileQ = source.Tile.Q, TileR = source.Tile.R, GroundX = point.WorldPosition.X, GroundZ = point.WorldPosition.Y };
        var result = AdminWorldCommands.Execute(world, command);
        Assert.That(result.Accepted, Is.True);
        Assert.That(world.Entities.Npcs[new HexLive.Simulation.Common.EntityId(result.EntityId)].Position, Is.EqualTo(point.WorldPosition));
        foreach (var junction in world.Junctions.Items.Values) junction.Blocked = true;
        var count = world.Entities.Npcs.Count;
        result = AdminWorldCommands.Execute(world, command);
        Assert.That(result.Reason, Is.EqualTo("NoSpawnLocation"));
        Assert.That(world.Entities.Npcs.Count, Is.EqualTo(count));
    }

    [Test]
    public void AdminWire_StrictUtf8AndBoundedRoundTrip()
    {
        var frame = AdminWire.Encode("{\"text\":\"Верни ей руку\"}");
        Assert.That(frame[0], Is.EqualTo((byte)FrameKind.AdminInput));
        Assert.That(AdminWire.Decode(frame.Skip(1).ToArray()), Does.Contain("Верни"));
        Assert.Throws<InvalidDataException>(() => AdminWire.Decode(new byte[] { 0xff }));
        Assert.Throws<InvalidDataException>(() => AdminWire.Encode(new string('x', AdminWire.MaxBytes + 1)));
    }
}
