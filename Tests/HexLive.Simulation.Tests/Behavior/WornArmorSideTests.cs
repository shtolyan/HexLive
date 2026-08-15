using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class WornArmorSideTests
{
    [TestCase(BodyPart.ArmL, WearSlot.ForearmL, BodyPart.ArmR)]
    [TestCase(BodyPart.ArmR, WearSlot.ForearmR, BodyPart.ArmL)]
    public void OneSidedArmwearProtectsAndWearsOnlyItsPhysicalArm(
        BodyPart protectedArm, WearSlot occupiedSlot, BodyPart bareArm)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear();

        var id = $"test.armor.single.{occupiedSlot}";
        AddArmor(world, id, 0.4f);
        WearSlotCatalog.Register(id, new[] { occupiedSlot });
        var item = new ItemInstance(id);
        npc.WornItems.Add(item);

        Assert.Multiple(() =>
        {
            Assert.That(EquipmentMath.ArmorForPart(world, npc, protectedArm), Is.EqualTo(0.4f));
            Assert.That(EquipmentMath.ArmorForPart(world, npc, bareArm), Is.Zero);
            Assert.That(EquipmentMath.IsPartCovered(world, npc, protectedArm), Is.True);
            Assert.That(EquipmentMath.IsPartCovered(world, npc, bareArm), Is.False);
        });

        EquipmentMath.WearCoveringItems(world, npc, bareArm, 0.25f);
        Assert.That(item.Durability, Is.EqualTo(1f));
        EquipmentMath.WearCoveringItems(world, npc, protectedArm, 0.25f);
        Assert.That(item.Durability, Is.EqualTo(0.75f));
    }

    [Test]
    public void PairedArmwearStillProtectsBothArms()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear();

        const string id = "test.armor.paired.arms";
        AddArmor(world, id, 0.3f);
        WearSlotCatalog.Register(id, new[] { WearSlot.WristL, WearSlot.WristR });
        npc.WornItems.Add(id);

        Assert.Multiple(() =>
        {
            Assert.That(EquipmentMath.ArmorForPart(world, npc, BodyPart.ArmL), Is.EqualTo(0.3f));
            Assert.That(EquipmentMath.ArmorForPart(world, npc, BodyPart.ArmR), Is.EqualTo(0.3f));
        });
    }

    private static void AddArmor(
        HexLive.Simulation.Core.WorldState world, string id, float armor)
    {
        var definition = new ObjectDefinition
        {
            Id = id,
            DisplayName = id,
            Layer = WearLayer.Wear
        };
        definition.Covers.Add(BodyPart.ArmL);
        definition.Covers.Add(BodyPart.ArmR);
        var dress = new InteractionDefinition
        {
            Id = "dress." + id,
            Type = InteractionType.Dress,
            DurationTicks = 8
        };
        dress.Effects.ArmorDelta = armor;
        definition.Interactions.Add(dress);
        world.Content.ObjectDefinitions[id] = definition;
    }
}

}
