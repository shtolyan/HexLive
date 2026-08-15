using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
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

    [Test]
    public void BackpackIsNeverAHeatUndressCandidate()
    {
        var world = TestWorld.CreateWorld(13323);
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var pack = npc.WornItems.Single(item =>
            world.Content.ObjectDefinitions[item.DefinitionId].Layer == WearLayer.Bags);
        npc.WornItems.Clear();
        npc.WornItems.Add(pack);
        npc.Memory.Dangers.Clear();

        Assert.That(DecisionSystem.FindRemovableItem(npc, world), Is.Null,
            "Жара всё ещё считает рюкзак снимаемой одеждой.");
    }

    [Test]
    public void DroppedOwnedBackpackGetsPriorityDressBidAndPlan()
    {
        var engine = TestWorld.CreateEngine(13324);
        for (var i = 0; i < 4; i++) engine.Step();

        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var pack = npc.WornItems.Single(item =>
            world.Content.ObjectDefinitions[item.DefinitionId].Layer == WearLayer.Bags);
        npc.WornItems.Remove(pack);

        Assert.That(npc.CurrentJunction, Is.Not.Null);
        var dropped = WorldObjectMutations.SpawnObject(
            world, pack.DefinitionId, npc.Fragment, npc.Tile, npc.CurrentJunction!.Value);
        dropped.Owner = npc.Id;
        npc.Perception.Objects.Clear();
        var seen = new PerceivedObject
        {
            Id = dropped.Id,
            DefinitionId = dropped.DefinitionId,
            Tile = dropped.Tile,
            Distance = 0.1f,
            IsReachable = true
        };
        seen.AvailableInteractions.Add(InteractionType.Dress);
        npc.Perception.Objects.Add(seen);
        npc.Mind.RedressGarments.Clear();

        new DecisionSystem().Run(world);
        var dress = npc.Mind.LastScores.Single(score => score.Goal == GoalType.Dress);
        Assert.That(dress.FinalScore, Is.GreaterThanOrEqualTo(1.1f),
            "Потерянная вместимость не получила первоочередную ставку Dress.");

        npc.Mind.CurrentGoal = GoalType.Dress;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);

        Assert.That(npc.Plan.Steps.Any(step => step.TargetObject == dropped.Id), Is.True,
            "Нулевое тепло рюкзака всё ещё отсекается как NoWarmthGain.");
    }

    [Test]
    public void LaundryDoesNotRemoveAWornBackpack()
    {
        var engine = TestWorld.CreateEngine(13325);
        for (var i = 0; i < 4; i++) engine.Step();

        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var pack = npc.WornItems.Single(item =>
            world.Content.ObjectDefinitions[item.DefinitionId].Layer == WearLayer.Bags);
        npc.WornItems.Clear();
        pack.Dirtiness = 1f;
        npc.WornItems.Add(pack);
        npc.Perception.Objects.Clear();
        npc.Mind.CurrentGoal = GoalType.WashClothes;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.Status = PlanStatus.None;

        new PlanningSystem().Run(world);

        Assert.That(npc.Plan.TargetItemDefinitionId, Is.Not.EqualTo(pack.DefinitionId),
            "Стирка всё ещё снимает надетый рюкзак; временно снимать его может только купание.");
    }

    private static bool IsBackpack(GarmentParams garment) =>
        garment != null &&
        garment.Id.StartsWith("gear.backpack_", StringComparison.Ordinal);

    private static bool IsCorrectBackpack(GarmentParams garment) =>
        garment.Layer == WearLayer.Bags &&
        garment.Category == GarmentCategory.Bag &&
        garment.Warmth == 0f &&
        garment.Capacity == 9;
}

}
