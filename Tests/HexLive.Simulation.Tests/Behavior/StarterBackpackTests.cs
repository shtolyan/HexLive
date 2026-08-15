using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§133.2 r2: starter backpacks use the dedicated layer and add nine slots.</summary>
public sealed class StarterBackpackTests
{
    [Test]
    public void EveryCatalogBackpackUsesTheBagsLayerAndNineSlots()
    {
        var world = TestWorld.CreateWorld(13321);
        var defaults = GarmentLibrary.Defaults.Where(IsBackpack).ToArray();
        var active = GarmentLibrary.Active.Where(IsBackpack).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(defaults, Has.Length.EqualTo(5),
                "The code fallback catalog must know all five shipped backpacks.");
            Assert.That(active, Has.Length.EqualTo(5),
                "The live/SimData catalog must know all five shipped backpacks.");
            Assert.That(defaults.All(IsCorrectBackpack), Is.True);
            Assert.That(active.All(IsCorrectBackpack), Is.True);
            Assert.That(active.All(pack =>
                    world.Content.ObjectDefinitions[pack.Id].Layer == WearLayer.Bags &&
                    world.Content.ObjectDefinitions[pack.Id].InventoryCapacity == 9),
                Is.True,
                "Runtime object definitions must preserve the catalog layer/capacity.");
        });
    }

    [Test]
    public void EveryStartingColonistWearsOneOwnedNineSlotBackpack()
    {
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 1; seed <= 40; seed++)
        {
            var world = TestWorld.CreateWorld(seed);
            foreach (var npc in world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony))
            {
                var wornPacks = npc.WornItems.Where(item =>
                    item.DefinitionId.StartsWith("gear.backpack_", StringComparison.Ordinal))
                    .ToArray();
                var carry = InventoryLayoutBuilder.Build(world, npc).Containers.Single(container =>
                    container.Kind == InventoryContainerKind.Carry);
                if (wornPacks.Length == 1) selectedIds.Add(wornPacks[0].DefinitionId);

                Assert.Multiple(() =>
                {
                    Assert.That(wornPacks, Has.Length.EqualTo(1),
                        $"seed={seed} npc={npc.Id.Value}: expected one starter backpack.");
                    Assert.That(wornPacks[0].OwnerId, Is.EqualTo(npc.Id.Value),
                        "A starter backpack is personal property from tick zero.");
                    Assert.That(carry.OwnerItemDefinitionId, Is.EqualTo(wornPacks[0].DefinitionId));
                    Assert.That(carry.BodyAnchor, Is.EqualTo(InventoryBodyAnchor.Back));
                    Assert.That(carry.BackpackCapacity, Is.EqualTo(9));
                });
            }
        }

        Assert.That(selectedIds, Is.EquivalentTo(
            GarmentLibrary.Active.Where(IsBackpack).Select(pack => pack.Id)),
            "The seeded per-NPC draw must make every shipped backpack reachable.");
    }

    [Test]
    public void SameSeedAndNpcReceiveTheSameRandomBackpack()
    {
        var first = TestWorld.CreateWorld(13322);
        var second = TestWorld.CreateWorld(13322);

        static string BackpackOf(NPCState npc) => npc.WornItems.Single(item =>
            item.DefinitionId.StartsWith("gear.backpack_", StringComparison.Ordinal)).DefinitionId;

        var firstByNpc = first.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .ToDictionary(npc => npc.Id.Value, BackpackOf);
        var secondByNpc = second.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .ToDictionary(npc => npc.Id.Value, BackpackOf);

        Assert.That(secondByNpc, Is.EqualTo(firstByNpc));
    }

    private static bool IsBackpack(GarmentParams garment) =>
        garment != null &&
        garment.Id.StartsWith("gear.backpack_", StringComparison.Ordinal);

    private static bool IsCorrectBackpack(GarmentParams garment) =>
        garment.Layer == WearLayer.Bags &&
        garment.Category == GarmentCategory.Bag &&
        garment.Capacity == 9;
}

}
