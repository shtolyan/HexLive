using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§146.11: HugeIsland world with one maxed, armored player starter.</summary>
public sealed class ManiacWorldGenGateTests
{
    private const int Seed = 12345;

    [Test]
    public void PlayerStarterHasTenOfTenAttributesMacheteAndMaximumArmorKit()
    {
        var world = Build();
        var player = world.Entities.Npcs[new EntityId(1)];
        string[] expectedArmor =
        {
            "clothing.cap_riot",
            "clothing.corset_anarchy",
            "clothing.armguards_fighter",
            "clothing.pants_biker",
            "clothing.vest_stars",
            "clothing.greaves_tod",
        };

        Assert.Multiple(() =>
        {
            Assert.That(world.Mode, Is.EqualTo(GameMode.Maniac));
            Assert.That(world.Tiles.Items, Has.Count.EqualTo(102 * 80));
            Assert.That(world.FactionHomes, Has.Count.EqualTo(7),
                "Six women camps plus the central Outsiders camp.");
            Assert.That(player.Faction, Is.EqualTo(Faction.Colony));
            Assert.That(player.Sex, Is.EqualTo(GarmentSex.Female));
            foreach (var kind in AttributeSet.All)
            {
                Assert.That(player.Attributes.Get(kind), Is.EqualTo(1f),
                    $"{kind} must display as 10/10.");
            }

            Assert.That(player.Inventory.Items.Count(item =>
                item.DefinitionId == GearCatalog.Machete), Is.EqualTo(1));
            Assert.That(player.Inventory.Items.Single(item =>
                item.DefinitionId == GearCatalog.Machete).OwnerId,
                Is.EqualTo(player.Id.Value));
            Assert.That(SimBalance.BestMeleeWeapon(
                player.Inventory.Items, player.Body.WeaponHands),
                Is.EqualTo(GearCatalog.Machete),
                "Combat presentation must put the machete in her hands.");
            Assert.That(expectedArmor.All(id => player.WornItems.Contains(id)), Is.True);
            Assert.That(player.WornItems, Has.Count.EqualTo(9),
                "Two underwear pieces, backpack and six authored armor pieces.");
            Assert.That(player.EquippedArmor, Is.EqualTo(0.22f));
            Assert.That(EquipmentMath.ArmorForPart(world, player, BodyPart.Head),
                Is.EqualTo(0.01f));
            Assert.That(EquipmentMath.ArmorForPart(world, player, BodyPart.Torso),
                Is.EqualTo(0.22f));
            Assert.That(EquipmentMath.ArmorForPart(world, player, BodyPart.Pelvis),
                Is.EqualTo(0.09f));
            Assert.That(EquipmentMath.ArmorForPart(world, player, BodyPart.ArmL),
                Is.EqualTo(0.15f));
            Assert.That(EquipmentMath.ArmorForPart(world, player, BodyPart.ArmR),
                Is.EqualTo(0.15f));
            Assert.That(EquipmentMath.ArmorForPart(world, player, BodyPart.LegL),
                Is.EqualTo(0.18f));
            Assert.That(EquipmentMath.ArmorForPart(world, player, BodyPart.LegR),
                Is.EqualTo(0.18f));
        });
    }

    [Test]
    public void OtherCampStartersKeepHugeIslandMinimalKitAndNoTools()
    {
        var world = Build();
        var otherWomen = world.Entities.Npcs.Values.Where(npc =>
            npc.Faction is Faction.Colony2 or Faction.Colony3 or Faction.Colony4 or
                Faction.Colony5 or Faction.Colony6).ToArray();

        Assert.That(otherWomen, Has.Length.EqualTo(5));
        foreach (var npc in otherWomen)
        {
            Assert.Multiple(() =>
            {
                // §55.4 (bug #317): минимальный комплект = личная пустая бутылка.
                Assert.That(npc.Inventory.Items.Select(i => i.DefinitionId),
                    Is.EqualTo(new[] { GearCatalog.Bottle }), $"NPC {npc.Id.Value}");
                Assert.That(npc.WornItems, Has.Count.EqualTo(3), $"NPC {npc.Id.Value}");
                Assert.That(npc.WornItems.Count(item => Garment(item.DefinitionId).Layer ==
                    WearLayer.Underwear), Is.EqualTo(2), $"NPC {npc.Id.Value}");
                Assert.That(npc.WornItems.Count(item => Garment(item.DefinitionId).Layer ==
                    WearLayer.Bags), Is.EqualTo(1), $"NPC {npc.Id.Value}");
            });
        }

        Assert.That(world.Entities.Npcs.Values.Sum(npc => npc.Inventory.Items.Count(item =>
            item.DefinitionId == GearCatalog.Machete)), Is.EqualTo(2),
            "One machete belongs to the player and one to opening Kshishtof.");
        Assert.That(world.Entities.Objects.Values.Any(obj =>
            obj.DefinitionId == GearCatalog.Machete), Is.False,
            "Maniac does not add a loose world machete.");
    }

    [Test]
    public void SaveRoundTripPreservesManiacModeAndStarterProfile()
    {
        var world = Build();
        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = Build();
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var player = loaded.Entities.Npcs[new EntityId(1)];
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Mode, Is.EqualTo(GameMode.Maniac));
            Assert.That(AttributeSet.All.All(kind =>
                player.Attributes.Get(kind) == 1f), Is.True);
            Assert.That(player.Inventory.Items.Any(item =>
                item.DefinitionId == GearCatalog.Machete), Is.True);
            Assert.That(player.WornItems.Any(item =>
                item.DefinitionId == "clothing.vest_stars"), Is.True);
        });
    }

    private static WorldState Build() => new WorldStateFactory().Create(
        PrototypeWorldDefinitionFactory.Create(Seed, GameMode.Maniac));

    private static GarmentParams Garment(string id) =>
        GarmentLibrary.Active.First(garment => garment.Id == id);
}

}
