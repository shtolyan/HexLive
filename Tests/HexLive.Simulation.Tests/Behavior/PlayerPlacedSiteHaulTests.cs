using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// §54.23 (баг #766): сундук и воткнутый факел, поставленные игроком из
/// режима стройки, колония обязана достроить сама. На живом сейве факел
/// ждал ОДНУ палку, три палки лежали в лагере, а все восемь колонисток
/// ходили с рюкзаком, забитым до последней ячейки, и поднять палку не
/// могла ни одна.
/// </summary>
public sealed class PlayerPlacedSiteHaulTests
{
    [OneTimeSetUp]
    public void LoadShippedContent() => SimDataFile.Require(RepoPaths.SimData);

    [Test]
    public void ColonyFinishesAPlayerPlacedChestAndTorchFromGroundMaterials()
    {
        var engine = Arena(out var world, out var colonists);
        var anchor = colonists[0];
        var torch = PlaceSite(engine, world, anchor, ContentIds.Torch, 0);
        var chest = PlaceSite(engine, world, anchor, ContentIds.StorageChest, 1);
        Assert.That(torch.Variant, Is.EqualTo("planted"));

        var pile = MaterialTile(world, anchor);
        Spawn(world, ContentIds.Stick, pile, StorageChest.BillSticks + 1);
        Spawn(world, ContentIds.Rope, pile, StorageChest.BillRope + 1);
        Spawn(world, ContentIds.Board, pile, StorageChest.BillBoards);
        Spawn(world, ContentIds.Cloth, pile, 1);

        Run(engine, world, colonists, 8000, torch, chest);

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(torch.Id), Is.False,
                "torch site never raised: " + Describe(world, torch, colonists));
            Assert.That(world.Entities.Objects.ContainsKey(chest.Id), Is.False,
                "chest site never raised: " + Describe(world, chest, colonists));
        });
    }

    [Test]
    public void FullPacksLeaveARedundantToolToCarryTheTorchsLastStick()
    {
        var engine = Arena(out var world, out var colonists);
        var anchor = colonists[0];
        var torch = PlaceSite(engine, world, anchor, ContentIds.Torch, 0);
        // As on the live save: rope and cloth already delivered, one stick short.
        torch.Contents.Add(new ItemInstance(ContentIds.Rope));
        torch.Contents.Add(new ItemInstance(ContentIds.Cloth));
        Assert.That(BuildSiteMath.Remaining(torch, ContentIds.Stick), Is.EqualTo(1));
        Spawn(world, ContentIds.Stick, MaterialTile(world, anchor), 3);

        foreach (var npc in colonists) FillPack(npc, redundantKnife: true);
        Assert.That(colonists.All(n => n.Inventory.UsedSlots == n.Inventory.Capacity), Is.True);

        Run(engine, world, colonists, 4000, torch);

        Assert.That(world.Entities.Objects.ContainsKey(torch.Id), Is.False,
            "a full-pack colony never carried the last stick: " + Describe(world, torch, colonists));
        Assert.That(world.Entities.Objects.Values.Any(o =>
            o.DefinitionId == ContentIds.Torch && o.Variant == "planted"), Is.True);
        Assert.That(colonists.All(n => n.Inventory.Items.Any(i => i.DefinitionId == "tool.machete")), Is.True,
            "the useful tool must never be the one left behind");
    }

    [Test]
    public void OnlyARedundantToolMakesRoomAndOnlyForWhatTheSiteStillLacks()
    {
        var engine = Arena(out var world, out var colonists);
        var npc = colonists[0];
        var torch = PlaceSite(engine, world, npc, ContentIds.Torch, 0);
        torch.Contents.Add(new ItemInstance(ContentIds.Rope));
        torch.Contents.Add(new ItemInstance(ContentIds.Cloth));
        // Perception must see the fresh site; it is the only one in the arena.
        // The colony may stake its own rack meanwhile — keep the arena to the torch.
        for (var i = 0; i < 40 && DecisionSystem.FindBuildSite(npc, world)?.Id != torch.Id; i++)
        {
            foreach (var other in world.Entities.Objects.Values
                         .Where(o => BuildSiteMath.IsSite(o) && o.Id != torch.Id).Select(o => o.Id).ToList())
                WorldObjectMutations.DespawnObject(world, other);
            engine.Step();
        }
        Assert.That(DecisionSystem.FindBuildSite(npc, world)?.Id, Is.EqualTo(torch.Id));

        FillPack(npc, redundantKnife: true);
        Assert.Multiple(() =>
        {
            Assert.That(InventoryMath.IsRedundantTool(npc, npc.Inventory.Items.First(i => i.DefinitionId == "tool.knife")), Is.True);
            Assert.That(InventoryMath.IsRedundantTool(npc, npc.Inventory.Items.First(i => i.DefinitionId == "tool.machete")), Is.False);
            Assert.That(InventoryMath.IsRedundantTool(npc, npc.Inventory.Items.First(i => i.DefinitionId == ContentIds.Bottle)), Is.False,
                "a second bottle is water, not a spare tool");
            Assert.That(InventoryMath.CanMakeRoomForGoal(world, npc, GoalType.GatherWood, ContentIds.Stick), Is.True);
            Assert.That(InventoryMath.CanMakeRoomForGoal(world, npc, GoalType.GatherWood, ContentIds.Board), Is.False,
                "the torch bills no boards");
        });

        torch.Contents.Add(new ItemInstance(ContentIds.Stick));
        Assert.That(BuildSiteMath.Remaining(torch, ContentIds.Stick), Is.Zero);
        Assert.That(InventoryMath.CanMakeRoomForGoal(world, npc, GoalType.GatherWood, ContentIds.Stick), Is.False,
            "the site is no longer short of a stick");
        torch.Contents.RemoveAt(torch.Contents.Count - 1);

        FillPack(npc, redundantKnife: false);
        Assert.That(InventoryMath.CanMakeRoomForGoal(world, npc, GoalType.GatherWood, ContentIds.Stick), Is.False,
            "without a spare tool a full pack stays full");
    }

    // ── arena ──────────────────────────────────────────────────────────────

    private static SimulationEngine Arena(out WorldState world, out List<NPCState> colonists)
    {
        var engine = TestWorld.CreateEngine(766);
        world = engine.World;
        world.Mobs.Clear();
        foreach (var site in world.Entities.Objects.Values.Where(BuildSiteMath.IsSite).Select(o => o.Id).ToList())
            WorldObjectMutations.DespawnObject(world, site);
        colonists = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).ToList();
        Assert.That(colonists, Is.Not.Empty);
        engine.Step();
        foreach (var npc in colonists) Rest(npc);
        return engine;
    }

    private static void Rest(NPCState npc)
    {
        npc.Needs.Hunger = npc.Needs.Thirst = 0f;
        npc.Needs.Energy = npc.Needs.Stamina = 1f;
        npc.Memory.Dangers.Clear();
    }

    private static WorldObjectState PlaceSite(
        SimulationEngine engine, WorldState world, NPCState npc, string catalogId, int nth)
    {
        var anchor = world.Junctions.Items.Values
            .Where(j => j.Tiles.Count == 1 && j.Id != npc.CurrentJunction && j.Fragment == npc.Fragment &&
                HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) is >= 1 and <= 3 &&
                SpatialQueries.IsJunctionFree(world, j.Id) &&
                FurniturePlacement.Validate(world, catalogId, j, 0) is null &&
                Connectivity.Reachable(world, npc.CurrentJunction!.Value, j.Id))
            .OrderBy(j => j.Id.Value).Skip(nth * 7).First();
        var key = FurniturePlacement.KeyOf(anchor.WorldPosition);
        // The exact command the build menu sends.
        var admission = engine.ApplyManualCommand(new PlaceFurnitureCommand(catalogId, key.XKey, key.YKey, 0));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var site = FurniturePlacement.FindAt(world, catalogId, key);
        Assert.That(site, Is.Not.Null);
        Assert.That(site.DefinitionId, Is.EqualTo(ContentIds.BuildSite));
        return site;
    }

    private static TileCoord MaterialTile(WorldState world, NPCState npc) =>
        world.Tiles.Items.Keys
            .Where(t => HexSpatialMath.HexDistance(npc.Tile, t) == 2 &&
                StructurePlacement.CenterJunction(world, t) is { } j &&
                Connectivity.Reachable(world, npc.CurrentJunction!.Value, j))
            .OrderBy(t => t.Q).ThenBy(t => t.R).First();

    private static void Spawn(WorldState world, string definitionId, TileCoord tile, int count)
    {
        var junction = StructurePlacement.CenterJunction(world, tile)!.Value;
        for (var i = 0; i < count; i++)
            WorldObjectMutations.SpawnObject(
                world, definitionId, world.Junctions.Items[junction].Fragment, tile, junction);
    }

    /// <summary>A machete (the tool she keeps), optionally a knife the machete
    /// fully covers, then water bottles up to the last cell.</summary>
    private static void FillPack(NPCState npc, bool redundantKnife)
    {
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance("tool.machete"));
        if (redundantKnife) npc.Inventory.Items.Add(new ItemInstance("tool.knife"));
        while (npc.Inventory.UsedSlots < npc.Inventory.Capacity)
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
    }

    private static void Run(SimulationEngine engine, WorldState world, List<NPCState> colonists,
        int ticks, params WorldObjectState[] sites)
    {
        for (var tick = 0; tick < ticks && sites.Any(s => world.Entities.Objects.ContainsKey(s.Id)); tick++)
        {
            engine.Step();
            if (tick % 200 == 0)
                foreach (var npc in colonists) Rest(npc);
        }
    }

    private static string Describe(WorldState world, WorldObjectState site, List<NPCState> colonists) =>
        string.Join(" ", BuildSiteMath.MaterialsFor(site).Where(m => BuildSiteMath.Bill(site, m) > 0)
            .Select(m => $"{m}:{BuildSiteMath.Delivered(site, m)}/{BuildSiteMath.Bill(site, m)}")) +
        " | " + string.Join(" ; ", colonists.Select(n =>
            $"{n.Id.Value} goal={n.Mind.CurrentGoal} site={DecisionSystem.FindBuildSite(n, world)?.BuildProduct} " +
            $"pack={n.Inventory.UsedSlots}/{n.Inventory.Capacity} [{string.Join(",", n.Inventory.Items.Select(i => i.DefinitionId))}]"));
}
