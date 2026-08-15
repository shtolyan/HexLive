using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§54.10 r2: raw and cooked meat form separate eight-piece stacks.</summary>
public sealed class MeatStackingTests
{
    [Test]
    public void RawAndCookedMeatStackSeparatelyByEight()
    {
        var world = TestWorld.CreateWorld(54101);
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 4;

        for (var i = 0; i < InventoryState.MeatStackSize + 1; i++)
        {
            npc.Inventory.Items.Add(ContentIds.MeatRaw);
            npc.Inventory.Items.Add(ContentIds.MeatCooked);
        }

        var slots = InventoryLayoutBuilder.Build(world, npc)
            .Containers.SelectMany(container => container.Slots).ToArray();
        var raw = slots.Where(slot => slot.ItemDefinitionId == ContentIds.MeatRaw)
            .Select(slot => slot.StackCount).ToArray();
        var cooked = slots.Where(slot => slot.ItemDefinitionId == ContentIds.MeatCooked)
            .Select(slot => slot.StackCount).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(InventoryState.IsStackable(ContentIds.MeatRaw), Is.True);
            Assert.That(InventoryState.IsStackable(ContentIds.MeatCooked), Is.True);
            Assert.That(InventoryState.StackSizeFor(ContentIds.MeatRaw), Is.EqualTo(8));
            Assert.That(InventoryState.StackSizeFor(ContentIds.MeatCooked), Is.EqualTo(8));
            Assert.That(raw, Is.EqualTo(new[] { 8, 1 }));
            Assert.That(cooked, Is.EqualTo(new[] { 8, 1 }));
            Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(4));
        });
    }

    [Test]
    public void FullRawStackCannotAcceptCookedMeatAndViceVersa()
    {
        var world = TestWorld.CreateWorld(54102);
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 1;

        for (var i = 0; i < 7; i++)
        {
            npc.Inventory.Items.Add(ContentIds.MeatRaw);
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.HasSpace, Is.False,
                "A partially filled stack still occupies its one physical slot.");
            Assert.That(InventoryMath.FitsWithoutEviction(
                world, npc, ContentIds.MeatRaw), Is.True,
                "The eighth raw portion must fill the existing raw stack.");
            Assert.That(InventoryMath.FitsWithoutEviction(
                world, npc, ContentIds.MeatCooked), Is.False,
                "Cooked meat must not merge into a raw-meat stack.");
        });

        npc.Inventory.Items.Clear();
        for (var i = 0; i < 7; i++)
        {
            npc.Inventory.Items.Add(ContentIds.MeatCooked);
        }

        Assert.Multiple(() =>
        {
            Assert.That(InventoryMath.FitsWithoutEviction(
                world, npc, ContentIds.MeatCooked), Is.True,
                "The eighth cooked portion must fill the existing cooked stack.");
            Assert.That(InventoryMath.FitsWithoutEviction(
                world, npc, ContentIds.MeatRaw), Is.False,
                "Raw meat must not merge into a cooked-meat stack.");
        });
    }
}

}
