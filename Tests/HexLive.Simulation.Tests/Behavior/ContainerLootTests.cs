using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>§128.5 r2 / bug #199: remains and wardrobe share container transfer.</summary>
public sealed class ContainerLootTests
{
    [Test]
    public void EmptyHumanRemainsStayAValidTwoWayContainer()
    {
        var world = TestWorld.CreateWorld(128501);
        var npc = Colonist(world);
        var anchor = Junction(world, npc);
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, npc.Fragment, npc.Tile,
            anchor);

        Assert.That(ContainerLootMath.IsLootable(world, remains), Is.True);

        var stone = new ItemInstance(ContentIds.Stone);
        npc.Inventory.Items.Add(stone);
        ContainerLootMath.GiveToContainer(
            world, remains, npc, new[] { stone });
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items, Does.Not.Contain(stone));
            Assert.That(remains.Contents.Any(item => ReferenceEquals(item, stone)), Is.True);
        });
    }

    [Test]
    public void WardrobeRoundTripPreservesPhysicalGarmentAndItsState()
    {
        var world = TestWorld.CreateWorld(128502);
        var npc = Colonist(world);
        var anchor = Junction(world, npc);
        var wardrobe = WorldObjectMutations.SpawnObject(
            world, ContentIds.Wardrobe, npc.Fragment, npc.Tile, anchor);
        wardrobe.RotationDegrees = 120f;
        var garment = WorldObjectMutations.SpawnObject(
            world, ContentIds.LeatherPants, npc.Fragment, npc.Tile, anchor);
        garment.Wetness = 0.7f;
        garment.Durability = 0.6f;
        garment.Dirtiness = 0.5f;
        garment.Bloodiness = 0.4f;
        garment.Owner = npc.Id;
        garment.Contents.Add(new ItemInstance(ContentIds.Stone));

        Assert.That(ContainerLootMath.IsLootable(world, wardrobe), Is.True,
            "An empty wardrobe must remain a valid destination before its first garment.");
        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, wardrobe, cells);
        Assert.That(cells.Select(cell => cell.ItemId), Does.Contain(ContentIds.LeatherPants));
        var slot = cells.FindIndex(cell => cell.ItemId == ContentIds.LeatherPants);
        Assert.That(ContainerLootMath.TryResolve(
            world, wardrobe, slot, ContentIds.LeatherPants, 1,
            out var moving, out var groundSources), Is.True);
        Assert.That(ContainerLootMath.FitsInLooter(world, npc, moving), Is.True);

        ContainerLootMath.TakeFromContainer(
            world, wardrobe, npc, moving, groundSources);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(garment.Id), Is.False);
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.LeatherPants), Is.EqualTo(1));
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.Stone), Is.EqualTo(1));
            var carried = npc.Inventory.Items.Single(item =>
                item.DefinitionId == ContentIds.LeatherPants);
            Assert.That(carried.Wetness, Is.EqualTo(0.7f));
            Assert.That(carried.Durability, Is.EqualTo(0.6f));
            Assert.That(carried.OwnerId, Is.EqualTo(npc.Id.Value));
        });

        var returning = npc.Inventory.Items.Single(item =>
            item.DefinitionId == ContentIds.LeatherPants);
        Assert.That(ContainerLootMath.CanAccept(
            world, wardrobe, new[] { returning }), Is.True);
        ContainerLootMath.GiveToContainer(
            world, wardrobe, npc, new[] { returning });

        var restored = world.Entities.Objects.Values.Single(obj =>
            obj.Id != wardrobe.Id && obj.DefinitionId == ContentIds.LeatherPants &&
            obj.Junctions.Contains(anchor));
        Assert.Multiple(() =>
        {
            Assert.That(restored.RotationDegrees, Is.EqualTo(wardrobe.RotationDegrees));
            Assert.That(restored.Wetness, Is.EqualTo(0.7f));
            Assert.That(restored.Durability, Is.EqualTo(0.6f));
            Assert.That(restored.Owner, Is.EqualTo(npc.Id));
        });
    }

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static HexLive.Simulation.Common.JunctionId Junction(
        WorldState world, NPCState npc) =>
        npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
}
