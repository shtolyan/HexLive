using System.IO;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §35.4 r3 (bug #750): «крыша на каждой комнате должна работать по-своему,
/// внутри гексагона, а не внутри всего строения». A two-hex house, one open
/// region (no partition), only hex A's six roof sectors finished: rain stops
/// on A and still soaks B, long before the whole house completes.
/// </summary>
public sealed class PerHexRoofShelterTests
{
    private const string Roof = "architecture.roof.palm";

    [Test]
    public void FinishedRoofHexStaysDryWhileTheOpenNeighbourHexGetsWet_Bug750()
    {
        var fixture = Build(75001);
        var world = fixture.World;

        Assert.That(Flags(world, fixture.A).HasFlag(TileFlags.Roofed), Is.True,
            "Шестой сектор крыши гекса A обязан сразу поставить Roofed этому гексу.");
        Assert.That(Flags(world, fixture.B).HasFlag(TileFlags.Roofed), Is.False);
        Assert.That(Flags(world, fixture.A).HasFlag(TileFlags.Indoor), Is.False,
            "Санктуарий остаётся контрактом целого дома — дом не достроен.");
        Assert.That(ShelterMath.IsUnderRoof(world, fixture.A), Is.True);
        Assert.That(ShelterMath.IsUnderRoof(world, fixture.B), Is.False);

        AssertRain(world, fixture.A, fixture.B);
    }

    [Test]
    public void PerHexRoofSurvivesSaveLoadAndIsRebuiltForAPreFixBlob_Bug750()
    {
        var fixture = Build(75002);
        var world = fixture.World;

        var loaded = RoundTrip(world, 75002);
        Assert.That(Flags(loaded, fixture.A).HasFlag(TileFlags.Roofed), Is.True);
        Assert.That(Flags(loaded, fixture.B).HasFlag(TileFlags.Roofed), Is.False);
        AssertRain(loaded, fixture.A, fixture.B);

        // A blob written by 0.1.120: the roof modules are finished, but the
        // flag was only ever set at CompleteHut. Load must derive it again.
        world.Tiles.Items[fixture.A].Flags &= ~TileFlags.Roofed;
        var legacy = RoundTrip(world, 75002);
        Assert.That(Flags(legacy, fixture.A).HasFlag(TileFlags.Roofed), Is.True,
            "Загрузка старого сейва обязана восстановить крышу гекса по готовым секторам.");
        Assert.That(Flags(legacy, fixture.B).HasFlag(TileFlags.Roofed), Is.False);
        AssertRain(legacy, fixture.A, fixture.B);
    }

    private sealed class Fixture
    {
        public WorldState World;
        public TileCoord A;
        public TileCoord B;
    }

    private static Fixture Build(int seed)
    {
        var world = TestWorld.CreateWorld(seed);
        var draft = TwoHexDraft();
        var blueprintId = world.NextPlayerBlueprintId++;
        world.PlayerBlueprints[blueprintId] = draft;
        var anchor = world.Tiles.Items.Keys
            .OrderBy(tile => tile.Q).ThenBy(tile => tile.R)
            .First(tile => BuildingBootstrap.FootprintTiles(draft, tile, 0f)
                               .All(footprint => BuildingBootstrap.CanPlaceHut(world, footprint)) &&
                           StructurePlacement.CenterJunction(world, tile) is not null);
        var site = BuildingBootstrap.CreatePlayerBlueprintSite(world, anchor, 0f, blueprintId);
        Assert.That(site, Is.Not.Null);

        var elements = BuildingRules.Elements(world, site).ToArray();
        var roofHexes = elements.Where(e => e.DefinitionId == Roof)
            .GroupBy(e => HexSpatialMath.WorldToTile(BuildWorkPoint.ModulePosition(world, site, e)))
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.SlotKey, System.StringComparer.Ordinal).ToArray());
        Assert.That(roofHexes, Has.Count.EqualTo(2), "фикстура: два гекса по шесть секторов");
        Assert.That(roofHexes.Values.All(group => group.Length == 6), Is.True);
        var a = roofHexes.Keys.OrderBy(t => t.Q).ThenBy(t => t.R).First();
        var b = roofHexes.Keys.Single(t => !t.Equals(a));

        // Everything but hex B's roof is up; hex A's last roof sector is one
        // unit short — the builder's next gesture is what finishes the hex.
        var last = roofHexes[a][5];
        foreach (var element in elements)
        {
            if (element.DefinitionId == Roof && roofHexes[b].Contains(element)) continue;
            element.DeliveredSticks = element.RequiredSticks;
            element.DeliveredBoards = element.RequiredBoards;
            element.DeliveredRope = element.RequiredRope;
            element.DeliveredLeaves = element.RequiredLeaves;
            element.WorkDone = element.WorkRequired;
        }

        string material;
        if (last.RequiredRope > 0) { last.DeliveredRope--; material = ContentIds.Rope; }
        else if (last.RequiredLeaves > 0) { last.DeliveredLeaves--; material = ContentIds.PalmLeaf; }
        else if (last.RequiredBoards > 0) { last.DeliveredBoards--; material = ContentIds.Board; }
        else { last.DeliveredSticks--; material = ContentIds.Stick; }
        last.WorkDone = 0;
        Assert.That(last.Complete, Is.False);
        Assert.That(Flags(world, a).HasFlag(TileFlags.Roofed), Is.False);

        Assert.That(BuildingRules.AssignDeliveredMaterial(world, site, material, staged: true), Is.True);
        Assert.That(last.Complete, Is.True, "фикстура: доставка обязана закрыть именно этот сектор");
        Assert.That(roofHexes[b].Any(e => e.Complete), Is.False);
        Assert.That(BuildingRules.IsCompletedBuilding(site), Is.False, "дом в целом не достроен");
        return new Fixture { World = world, A = a, B = b };
    }

    private static BuildingBlueprintDraft TwoHexDraft()
    {
        var a = TileCoord.Zero;
        var direction = HexDirection.All[0];
        var b = new TileCoord(direction.DQ, direction.DR);
        var draft = new BuildingBlueprintDraft { BlueprintId = "test_two_hex_750" };
        var floors = Enumerable.Range(0, 6).SelectMany(sector => new[]
        {
            new FloorSectorKey(a, sector), new FloorSectorKey(b, sector)
        }).ToArray();
        Assert.That(BlueprintEditorCommands.CreateRoom(draft, floors).Succeeded, Is.True);
        var doorPlaced = false;
        for (var edge = 0; edge < 6 && !doorPlaced; edge++)
            doorPlaced = BlueprintEditorCommands.PlaceOpening(
                draft, BuiltInBuildingBlueprints.EdgeSegments(a, edge)[1], BlueprintElementKind.Door).Succeeded;
        Assert.That(doorPlaced, Is.True);
        foreach (var hex in new[] { a, b })
        {
            for (var corner = 0; corner < 6; corner++)
                BlueprintEditorCommands.AddSupport(draft, BlueprintGeometry.HexCorner(hex, corner));
            Assert.That(BlueprintEditorCommands.RoofHex(draft, hex).Succeeded, Is.True);
        }
        draft.Normalize();
        return draft;
    }

    private static void AssertRain(WorldState world, TileCoord roofed, TileCoord open)
    {
        var npc = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        world.Environment.IsRaining = true;

        npc.Tile = roofed;
        npc.BodyWetness = 0f;
        new MoistureSystem().Run(world);
        Assert.That(npc.BodyWetness, Is.Zero, "под готовой крышей своего гекса она сухая");

        npc.Tile = open;
        new MoistureSystem().Run(world);
        Assert.That(npc.BodyWetness, Is.EqualTo(1f), "в соседней комнате без крыши она мокнет");
    }

    private static TileFlags Flags(WorldState world, TileCoord tile) => world.Tiles.Items[tile].Flags;

    private static WorldState RoundTrip(WorldState world, int seed)
    {
        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);
        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }
}

}
