using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.AI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §120.10: free construction is live world state, not a hidden player
/// blueprint. Every LEGO module owns its own identity, bill and save record.
/// </summary>
public sealed class FreeArchitectureTests
{
    [Test]
    public void WallGestureCreatesIndependentSitesWithoutBlueprint()
    {
        var engine = TestWorld.CreateEngine(12010);
        var world = engine.World;
        var baselineBlueprints = world.PlayerBlueprints.Count;
        var tile = BuildableTile(world);
        var segments = BlueprintGeometry.HexPerimeter(tile).Take(2).ToArray();
        var placements = segments.Select((segment, index) =>
            FreeArchitecturePlacementData.FromElement(new BlueprintElementData
            {
                Id = $"wall_{index}",
                Kind = BlueprintElementKind.Wall,
                Origin = BlueprintElementOrigin.Manual,
                Segment = segment
            })).ToArray();

        var admission = engine.ApplyManualCommand(
            new ApplyFreeArchitectureCommand(placements, new string[0]));

        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(world.PlayerBlueprints, Has.Count.EqualTo(baselineBlueprints));
        var pieces = FreeArchitectureRules.FreePieces(world)
            .Where(piece => placements.Select(item => item.SlotKey)
                .Contains(piece.ArchitectureElements.Single().SlotKey))
            .ToArray();
        Assert.That(pieces, Has.Length.EqualTo(2));
        Assert.That(pieces.Select(piece => piece.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(pieces.All(piece => piece.ArchitectureOwnerId == piece.Id), Is.True);
        Assert.That(pieces.All(piece => piece.BuildProduct == "architecture.wall.wood"), Is.True);
        Assert.That(pieces.All(piece => piece.ArchitectureElements.Single().RequiredTotal > 0), Is.True);
        Assert.That(world.Entities.Npcs.Values.Where(npc => npc.Faction == Faction.Colony)
            .All(npc => pieces.All(piece => npc.Memory.KnownObjects.ContainsKey(piece.Id))), Is.True,
            "Direct sites must enter colony memory so ordinary builders can discover them.");

        var removedId = pieces.Single(piece =>
            piece.ArchitectureElements.Single().SlotKey == placements[0].SlotKey).Id;
        admission = engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            new FreeArchitecturePlacementData[0], new[] { placements[0].SlotKey }));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(world.Entities.Objects.ContainsKey(removedId), Is.False);
        Assert.That(world.Entities.Npcs.Values.All(npc =>
            !npc.Memory.KnownObjects.ContainsKey(removedId)), Is.True);
        Assert.That(FreeArchitectureRules.FreePieces(world).Any(piece =>
            piece.ArchitectureElements.Single().SlotKey == placements[1].SlotKey), Is.True,
            "Removing one wall must not delete a house aggregate or its neighbour.");
    }

    [Test]
    public void RoofWaitsForItsExactSupports()
    {
        var engine = TestWorld.CreateEngine(12011);
        var world = engine.World;
        var tile = BuildableTile(world);
        var roofSector = new RoofSectorKey(tile, 0);
        var supportNodes = BlueprintGeometry.RoofSupports(roofSector);
        var supportPlacements = supportNodes.Select((node, index) =>
            FreeArchitecturePlacementData.FromElement(new BlueprintElementData
            {
                Id = $"support_{index}",
                Kind = BlueprintElementKind.Support,
                Node = node
            })).ToArray();
        var roofPlacement = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        {
            Id = "roof",
            Kind = BlueprintElementKind.RoofSector,
            RoofSector = roofSector
        });

        var admission = engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            supportPlacements.Concat(new[] { roofPlacement }).ToArray(), new string[0]));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var roof = FreeArchitectureRules.FreePieces(world).Single(piece =>
            piece.ArchitectureElements.Single().SlotKey == roofPlacement.SlotKey);
        Assert.That(roof.ArchitectureElements.Single().Buildable, Is.False);

        foreach (var placement in supportPlacements)
        {
            var support = FreeArchitectureRules.FreePieces(world).Single(piece =>
                piece.ArchitectureElements.Single().SlotKey == placement.SlotKey);
            FillBill(support);
            FreeArchitectureRules.Complete(world, support);
        }

        Assert.That(roof.ArchitectureElements.Single().Buildable, Is.True,
            "A roof sector unlocks only after the supports under its own edge are complete.");
        Assert.That(roof.BuildProduct, Is.EqualTo("architecture.roof.palm"),
            "Unlocking a roof schedules that roof; it does not auto-complete it.");
    }

    [Test]
    public void DirectPieceSurvivesSaveLoadWithSelfOwnership()
    {
        const int seed = 12012;
        var engine = TestWorld.CreateEngine(seed);
        var world = engine.World;
        var tile = BuildableTile(world);
        var segment = BlueprintGeometry.HexPerimeter(tile).First();
        var placement = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        {
            Id = "wall",
            Kind = BlueprintElementKind.Wall,
            Segment = segment
        });
        var admission = engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            new[] { placement }, new string[0]));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var original = FreeArchitectureRules.FreePieces(world).Single(piece =>
            piece.ArchitectureElements.Single().SlotKey == placement.SlotKey);

        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(loaded, reader);

        var restored = loaded.Entities.Objects[original.Id];
        Assert.That(restored.ArchitectureOwnerId, Is.EqualTo(restored.Id));
        Assert.That(restored.ArchitectureElements, Has.Count.EqualTo(1));
        Assert.That(restored.ArchitectureElements[0].SlotKey, Is.EqualTo(placement.SlotKey));
        Assert.That(restored.BuildProduct, Is.EqualTo("architecture.wall.wood"));
    }

    [Test]
    public void FreePieceIsAPerceivedBuildTargetAndKeepsItsCanonicalMarkerNodes()
    {
        var engine = TestWorld.CreateEngine(12013);
        var world = engine.World;
        var tile = BuildableTile(world);
        var segment = BlueprintGeometry.HexPerimeter(tile).First();
        var placement = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        {
            Id = "wall",
            Kind = BlueprintElementKind.Wall,
            Segment = segment
        });
        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            new[] { placement }, System.Array.Empty<string>())).Accepted, Is.True);
        var piece = FreeArchitectureRules.FreePieces(world).Single(candidate =>
            candidate.ArchitectureElements.Single().SlotKey == placement.SlotKey);

        engine.Step();

        Assert.That(BuildSiteMath.IsSite(piece), Is.True);
        Assert.That(world.Entities.Npcs.Values.Where(npc => npc.Faction == Faction.Colony)
            .All(npc => npc.Perception.Objects.Any(seen => seen.Id == piece.Id &&
                seen.AvailableInteractions.Contains(InteractionType.Build))), Is.True,
            "A real free LEGO object must expose Build without pretending to be build.site content.");
        Assert.That(BlueprintPlanningMarkers.ForElement(placement.ToElement("marker"))
            .Select(marker => marker.Position), Is.EquivalentTo(new[]
            {
                BlueprintGeometry.ToWorld(segment.A),
                BlueprintGeometry.ToWorld(segment.B)
            }));
    }

    [Test]
    public void DeliveredWallQueuesDemolitionAndReturnsHalfAtItsAnchor()
    {
        var engine = TestWorld.CreateEngine(12014);
        var world = engine.World;
        var tile = BuildableTile(world);
        var segment = BlueprintGeometry.HexPerimeter(tile).First();
        var placement = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        {
            Id = "wall",
            Kind = BlueprintElementKind.Wall,
            Segment = segment
        });
        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            new[] { placement }, System.Array.Empty<string>())).Accepted, Is.True);
        var wall = FreeArchitectureRules.FreePieces(world).Single(candidate =>
            candidate.ArchitectureElements.Single().SlotKey == placement.SlotKey);
        FillBill(wall);
        FreeArchitectureRules.Complete(world, wall);
        var wallId = wall.Id;
        var wallAnchor = wall.Junctions[0];
        var objectsBeforeDemolition = world.Entities.Objects.Keys.ToHashSet();
        var blockedBefore = wall.BlockedJunctions.ToArray();
        var expectedReturns = wall.Contents
            .GroupBy(item => item.DefinitionId)
            .ToDictionary(group => group.Key, group => group.Count() / 2);
        var groundBefore = expectedReturns.ToDictionary(pair => pair.Key, pair =>
            world.Entities.Objects.Values.Count(obj => obj.DefinitionId == pair.Key));

        var admission = engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            System.Array.Empty<FreeArchitecturePlacementData>(), new[] { placement.SlotKey }));

        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(world.Entities.Objects.ContainsKey(wallId), Is.True,
            "Deleting the drawing must not teleport a delivered wall away.");
        Assert.That(FreeArchitectureRules.IsDemolitionSite(wall), Is.True);
        Assert.That(wall.BlockedJunctions, Is.EquivalentTo(blockedBefore),
            "The physical wall keeps topology until teardown finishes.");
        Assert.That(BuildSiteMath.IsSite(wall), Is.True);

        FreeArchitectureRules.CompleteDemolition(world, wall);

        Assert.That(world.Entities.Objects.ContainsKey(wallId), Is.False);
        var recovered = world.Entities.Objects.Values
            .Where(obj => !objectsBeforeDemolition.Contains(obj.Id) &&
                          expectedReturns.ContainsKey(obj.DefinitionId))
            .ToArray();
        Assert.That(recovered.All(obj => obj.Junctions.Contains(wallAnchor)), Is.True,
            "Recovered parts must land at the demolished module, not at the worker's feet.");
        foreach (var pair in expectedReturns)
        {
            Assert.That(world.Entities.Objects.Values.Count(obj => obj.DefinitionId == pair.Key),
                Is.EqualTo(groundBefore[pair.Key] + pair.Value),
                $"Exactly half of {pair.Key} must survive demolition.");
        }
    }

    [Test]
    public void OpeningReplacementWaitsForOldWallDemolitionAndSurvivesSaveLoad()
    {
        const int seed = 12015;
        var engine = TestWorld.CreateEngine(seed);
        var world = engine.World;
        var tile = BuildableTile(world);
        var segment = BlueprintGeometry.HexPerimeter(tile)
            .First(candidate => BlueprintGeometry.TryDoorPortal(candidate, out _));
        var wallPlacement = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        {
            Id = "wall",
            Kind = BlueprintElementKind.Wall,
            Segment = segment
        });
        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            new[] { wallPlacement }, System.Array.Empty<string>())).Accepted, Is.True);
        var wall = FreeArchitectureRules.FreePieces(world).Single(candidate =>
            candidate.ArchitectureElements.Single().SlotKey == wallPlacement.SlotKey);
        FillBill(wall);
        FreeArchitectureRules.Complete(world, wall);
        var originalId = wall.Id;
        var doorPlacement = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        {
            Id = "door",
            Kind = BlueprintElementKind.Door,
            Segment = segment
        });

        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            new[] { doorPlacement }, new[] { wallPlacement.SlotKey })).Accepted, Is.True);
        Assert.That(wall.DefinitionId, Is.EqualTo("architecture.wall.wood"));
        Assert.That(wall.ArchitectureElements[0].DemolitionPlanned, Is.True);
        Assert.That(wall.ArchitectureElements[0].ReplacementDefinitionId,
            Is.EqualTo("architecture.door.wood"));

        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(loaded, reader);
        var restored = loaded.Entities.Objects[originalId];
        Assert.That(restored.ArchitectureElements[0].DemolitionPlanned, Is.True);
        Assert.That(restored.ArchitectureElements[0].ReplacementDefinitionId,
            Is.EqualTo("architecture.door.wood"));

        FreeArchitectureRules.CompleteDemolition(loaded, restored);
        Assert.That(loaded.Entities.Objects[originalId].DefinitionId,
            Is.EqualTo("architecture.door.wood"));
        Assert.That(loaded.Entities.Objects[originalId].BuildProduct,
            Is.EqualTo("architecture.door.wood"));
        Assert.That(loaded.Entities.Objects[originalId].ArchitectureElements[0].DemolitionPlanned,
            Is.False);
    }

    private static TileCoord BuildableTile(WorldState world) =>
        world.Tiles.Items.Values
            .Where(tile => tile.Flags.HasFlag(TileFlags.Walkable) &&
                           !tile.Flags.HasFlag(TileFlags.Water) &&
                           !tile.Flags.HasFlag(TileFlags.Blocked))
            .OrderBy(tile => System.Math.Abs(tile.Coord.Q) + System.Math.Abs(tile.Coord.R))
            .ThenBy(tile => tile.Coord.Q)
            .ThenBy(tile => tile.Coord.R)
            .First().Coord;

    private static void FillBill(WorldObjectState site)
    {
        Add(site, ContentIds.Stick, site.BillSticks);
        Add(site, ContentIds.Board, site.BillBoards);
        Add(site, ContentIds.Rope, site.BillRope);
        Add(site, ContentIds.PalmLeaf, site.BillLeaves);
    }

    private static void Add(WorldObjectState site, string definitionId, int count)
    {
        for (var i = 0; i < count; i++) site.Contents.Add(new ItemInstance(definitionId));
    }
}

}
