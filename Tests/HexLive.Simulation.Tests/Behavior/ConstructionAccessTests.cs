using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class ConstructionAccessTests
{
    private static readonly Faction[] Ours = { Faction.Colony };

    [TestCase("furniture")]
    [TestCase("hut")]
    [TestCase("blueprint")]
    [TestCase("free")]
    public void ForeignPlacementIsRejectedWithoutWorldMutation(string kind)
    {
        var engine = TestWorld.CreateEngine();
        var tile = Buildable(engine.World);
        engine.World.FactionHomes[Faction.Colony2] = tile;
        ISimulationCommand command = kind switch
        {
            "furniture" => new PlaceFurnitureSiteCommand(ContentIds.BedBasic, tile, 0),
            "hut" => new PlaceBuildingPlanCommand(tile, 0),
            "blueprint" => new PlaceBuildingBlueprintCommand(
                BuildingBlueprintJson.Serialize(BuiltInBuildingBlueprints.Hut1Hex()), tile, 0),
            _ => new ApplyFreeArchitectureCommand(new[] { Floor(tile) }, Array.Empty<string>())
        };
        RejectUnchanged(engine, command);
    }

    [TestCase("cancel")]
    [TestCase("rotate")]
    [TestCase("revision")]
    [TestCase("build")]
    public void ExistingForeignStructureCannotBeEdited(string operation)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var tile = Buildable(world);
        world.FactionHomes.Clear();
        var actor = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var anchor = StructurePlacement.CenterJunction(world, tile).Value;
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite,
            world.Junctions.Items[anchor].Fragment, tile, anchor);
        site.BuildProduct = ContentIds.BedBasic;
        site.OwnerFaction = Faction.Colony2;
        actor.Mind.ManualControl = true;
        ISimulationCommand command = operation switch
        {
            "cancel" => new CancelBuildSiteCommand(site.Id),
            "rotate" => new RotateBuildSiteCommand(site.Id, 60),
            "revision" => new UpdateBuildingBlueprintCommand(site.Id,
                BuildingBlueprintJson.Serialize(BuiltInBuildingBlueprints.Hut1Hex())),
            _ => new InteractCommand(actor.Id, site.Id, InteractionType.Build)
        };
        RejectUnchanged(engine, command);
    }

    [Test]
    public void MixedFreeGestureAndImplicitReplacementAreAtomic()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.FactionHomes.Clear();
        var tile = Buildable(world);
        var first = Floor(tile);
        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(
            new[] { first }, Array.Empty<string>())).Accepted, Is.True);
        var piece = FreeArchitectureRules.FreePieces(world).Single(p => p.Tile == tile);
        piece.OwnerFaction = Faction.Colony2;
        var next = Floor(new TileCoord(tile.Q + 1, tile.R));
        RejectUnchanged(engine, new ApplyFreeArchitectureCommand(new[] { next }, new[] { first.SlotKey }));
        RejectUnchanged(engine, new ApplyFreeArchitectureCommand(new[] { first }, Array.Empty<string>()));
        RejectUnchanged(engine, new ApplyFreeArchitectureCommand(Array.Empty<FreeArchitecturePlacementData>(), new[] { first.SlotKey }));
    }

    [Test]
    public void SenderAssignmentsCannotBorrowAnotherPlayersCamp()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var actors = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        actors[1].Faction = Faction.Colony2;
        world.PlayerControlledNpcs.Add(actors[1].Id.Value);
        var tile = Buildable(world);
        world.FactionHomes.Clear();
        world.FactionHomes[Faction.Colony2] = tile;
        var command = new ApplyFreeArchitectureCommand(new[] { Floor(tile) }, Array.Empty<string>());
        RejectUnchanged(engine, command, new[] { actors[0].Id.Value }, "ws:player-a");
        Assert.That(engine.ApplyManualCommand(command, "ws:player-b", new[] { actors[1].Id.Value }).Accepted, Is.True);
        Assert.That(FreeArchitectureRules.FreePieces(world).Single(p => p.Tile == tile).OwnerFaction,
            Is.EqualTo(Faction.Colony2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RemoteMissingOrEmptyAssignmentsFailClosed(bool explicitEmpty)
    {
        var engine = TestWorld.CreateEngine();
        var command = new ApplyFreeArchitectureCommand(new[] { Floor(Buildable(engine.World)) }, Array.Empty<string>());
        var result = engine.ApplyManualCommand(command, "ws:outsider", explicitEmpty ? Array.Empty<int>() : null);
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Reason, Is.EqualTo("NotAssigned"));
    }

    [Test]
    public void TerritoryAndSnapshotAgreeIncludingOverlapAndRadiusBoundary()
    {
        var world = TestWorld.CreateWorld();
        world.FactionHomes.Clear();
        world.FactionHomes[Faction.Colony] = new TileCoord(1, 0);
        world.FactionHomes[Faction.Colony2] = TileCoord.Zero;
        var snapshot = new WorldSnapshot();
        foreach (var pair in world.FactionHomes)
            snapshot.CampHomes.Add(new CampHomeSnapshot { Faction = pair.Key, Tile = pair.Value });
        for (var q = -7; q <= 7; q++)
        for (var r = -7; r <= 7; r++)
        {
            var tile = new TileCoord(q, r);
            var expected = HexSpatialMath.HexDistance(TileCoord.Zero, tile) > TheftMath.PrivateGroundRadiusTiles;
            Assert.That(ConstructionAccess.CanEditTile(world, Ours, tile), Is.EqualTo(expected), tile.ToString());
            Assert.That(ConstructionAccess.CanEditTile(snapshot, Ours, tile), Is.EqualTo(expected), tile.ToString());
        }
    }

    [Test]
    public void BoundaryWallAndForgedAnchorCannotBypassForeignGeometry()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.FactionHomes.Clear();
        var tile = Buildable(world);
        var wall = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        { Kind = BlueprintElementKind.Wall, Segment = BlueprintGeometry.HexPerimeter(tile)[0] });
        var touched = ConstructionAccess.ElementTiles(wall.ToElement("wall")).Distinct().ToArray();
        Assert.That(touched.Length, Is.GreaterThan(1), "An edge belongs to both adjacent hexes.");
        var foreign = touched.First(t => t != wall.AnchorTile);
        world.FactionHomes[Faction.Colony2] = foreign;
        RejectUnchanged(engine, new ApplyFreeArchitectureCommand(new[] { wall }, Array.Empty<string>()));
        var forged = new FreeArchitecturePlacementData(wall.Kind, new TileCoord(tile.Q + 100, tile.R),
            wall.Node, wall.Segment, wall.FloorSector, wall.RoofSector);
        RejectUnchanged(engine, new ApplyFreeArchitectureCommand(new[] { forged }, Array.Empty<string>()));
    }

    [Test]
    public void BlueprintChecksAllModulesAndRotatedFootprintNotJustAnchor()
    {
        var world = TestWorld.CreateWorld();
        world.FactionHomes.Clear();
        var plan = BuiltInBuildingBlueprints.Hut1Hex();
        var detached = new TileCoord(12, 0);
        plan.Elements.Add(new BlueprintElementData { Id = "far_support", Kind = BlueprintElementKind.Support,
            Node = BlueprintGeometry.HexCenter(detached) });
        plan.Normalize();
        for (var yaw = 0; yaw < 360; yaw += 60)
        {
            var anchor = new TileCoord(20, 20);
            var affected = ConstructionAccess.PlanTiles(plan, anchor, yaw).Distinct().ToArray();
            var foreign = affected.OrderByDescending(tile => HexSpatialMath.HexDistance(anchor, tile)).First();
            Assert.That(HexSpatialMath.HexDistance(anchor, foreign), Is.GreaterThan(5));
            world.FactionHomes[Faction.Colony2] = foreign;
            Assert.That(ConstructionAccess.Validate(world, new PlaceBuildingBlueprintCommand(
                BuildingBlueprintJson.Serialize(plan), anchor, yaw), Ours, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ConstructionAccess.ForeignReason));
        }
    }

    [TestCase(30f)]
    [TestCase(90f)]
    [TestCase(-30f)]
    [TestCase(390f)]
    public void PermissionGeometryUsesTheSameYawQuantizationAsCreation(float yaw)
    {
        var plan = new BuildingBlueprintDraft { HasAnchor = true, AnchorQ = 0, AnchorR = 0 };
        var remote = new TileCoord(9, 0);
        plan.Elements.Add(new BlueprintElementData { Id = "floor", Kind = BlueprintElementKind.FloorSector,
            FloorSector = new FloorSectorKey(remote, 0) });
        var anchor = new TileCoord(20, 20);
        var expected = BuildingBootstrap.FootprintTiles(plan, anchor,
            StructurePlacement.QuantizeHexSymmetryYaw(yaw));
        Assert.That(expected.All(tile => ConstructionAccess.PlanTiles(plan, anchor, yaw).Contains(tile)), Is.True);
    }

    [Test]
    public void RevisionCannotMaskForeignGeometryBySpoofingItsAnchor()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.FactionHomes.Clear();
        var owner = TestWorld.StartHut(world);
        var previous = BuildingRules.EditablePlanFor(world, owner);
        var stable = BlueprintBuildingPlan.AnchorTile(previous);
        var revision = previous.Clone();
        revision.HasAnchor = true;
        revision.AnchorQ = stable.Q + 7;
        revision.AnchorR = stable.R;
        revision.Elements.Add(new BlueprintElementData { Id = "masked_support", Kind = BlueprintElementKind.Support,
            Node = BlueprintGeometry.HexCenter(new TileCoord(stable.Q + 7, stable.R)) });
        revision.Normalize();
        var foreignNode = BlueprintGeometry.RotateNode(new HexBuildNodeKey(42, -21),
            (int)StructurePlacement.QuantizeHexSymmetryYaw(owner.RotationDegrees) / 60);
        var local = BlueprintGeometry.ToWorld(foreignNode);
        var position = HexSpatialMath.TileToWorld(owner.Tile);
        world.FactionHomes[Faction.Colony2] = HexSpatialMath.WorldToTile(
            new Float2(position.X + local.X, position.Y + local.Y));
        Assert.That(ConstructionAccess.CanEditObject(world, Ours, owner), Is.True,
            "The existing house itself must be editable; only the forged revision reaches foreign land.");
        Assert.That(ConstructionAccess.PlanTiles(revision, owner.Tile, owner.RotationDegrees)
            .All(tile => ConstructionAccess.CanEditTile(world, Ours, tile)), Is.True,
            "Trusting the transmitted anchor would incorrectly admit this revision.");
        RejectUnchanged(engine, new UpdateBuildingBlueprintCommand(owner.Id, BuildingBlueprintJson.Serialize(revision)));
    }

    [Test]
    public void OwnAndUnclaimedConstructionAndHistoryContinueWorking()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var tile = Buildable(world);
        world.FactionHomes.Clear();
        world.FactionHomes[Faction.Colony] = tile;
        var placement = Floor(tile);
        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(new[] { placement }, Array.Empty<string>())).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(Array.Empty<FreeArchitecturePlacementData>(), new[] { placement.SlotKey })).Accepted, Is.True);
        world.FactionHomes.Clear();
        Assert.That(engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(new[] { placement }, Array.Empty<string>())).Accepted, Is.True);

        var draft = new BuildingBlueprintDraft();
        var history = new BlueprintCommandHistory();
        bool allow = false;
        bool Access(BuildingBlueprintDraft before, BuildingBlueprintDraft after) => allow;
        var result = history.Execute(draft, d => BlueprintEditorCommands.AddSupport(d, new HexBuildNodeKey(0, 0)), Access, "denied");
        Assert.That(result.Succeeded, Is.False);
        Assert.That(draft.Elements, Is.Empty);
        Assert.That(history.CanUndo, Is.False);
        allow = true;
        Assert.That(history.Execute(draft, d => BlueprintEditorCommands.AddSupport(d, new HexBuildNodeKey(0, 0)), Access).Succeeded, Is.True);
        allow = false;
        Assert.That(history.Undo(draft, Access), Is.False);
        Assert.That(draft.Elements, Has.Count.EqualTo(1));
        allow = true;
        Assert.That(history.Undo(draft, Access), Is.True);
        allow = false;
        Assert.That(history.Redo(draft, Access), Is.False);
        Assert.That(draft.Elements, Is.Empty);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void FurnitureChecksOccupiedJunctionsNotJustItsHexCenter(int yaw)
    {
        var world = TestWorld.CreateWorld();
        world.FactionHomes.Clear();
        var furniture = Enumerable.Range(0, HexPointLayout.GetInteriorTemplates().Count)
            .Select(slot => new FurniturePlacementData { Id = "edge_bed", DefinitionId = ContentIds.BedBasic,
                JunctionSlot = slot, YawStep = yaw })
            .First(item => BlueprintFurnitureFootprints.OccupiedJunctions(item)
                .Any(key => HexSpatialMath.WorldToTile(BlueprintGeometry.JunctionToWorld(key)) != TileCoord.Zero));
        var outside = BlueprintFurnitureFootprints.OccupiedJunctions(furniture)
            .Select(key => HexSpatialMath.WorldToTile(BlueprintGeometry.JunctionToWorld(key)))
            .First(tile => tile != TileCoord.Zero);
        world.FactionHomes[Faction.Colony2] = new TileCoord(6 * outside.Q, 6 * outside.R);
        Assert.That(ConstructionAccess.CanEditTile(world, Ours, TileCoord.Zero), Is.True);
        var plan = new BuildingBlueprintDraft { HasAnchor = true };
        plan.Furniture.Add(furniture);
        var tiles = ConstructionAccess.PlanTiles(plan, TileCoord.Zero, 0).Distinct().ToArray();
        Assert.That(tiles, Does.Contain(outside));
        Assert.That(tiles.Any(tile => !ConstructionAccess.CanEditTile(world, Ours, tile)), Is.True,
            "A valid anchor does not authorize the occupied part crossing into foreign ground.");
    }

    private static FreeArchitecturePlacementData Floor(TileCoord tile) =>
        FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        { Kind = BlueprintElementKind.FloorSector, FloorSector = new FloorSectorKey(tile, 0) });

    private static TileCoord Buildable(WorldState world) => world.Tiles.Items.Values
        .Where(t => t.Flags.HasFlag(TileFlags.Walkable) && !t.Flags.HasFlag(TileFlags.Water) && !t.Flags.HasFlag(TileFlags.Blocked))
        .OrderBy(t => Math.Abs(t.Coord.Q) + Math.Abs(t.Coord.R)).First().Coord;

    private static byte[] Save(WorldState world)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        return stream.ToArray();
    }

    private static void RejectUnchanged(SimulationEngine engine, ISimulationCommand command,
        IReadOnlyCollection<int> actors = null, string owner = "")
    {
        var before = Save(engine.World);
        var result = engine.ApplyManualCommand(command, owner, actors);
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Reason, Is.EqualTo(ConstructionAccess.ForeignReason));
        Assert.That(Save(engine.World), Is.EqualTo(before), "A rejected edit must preserve the complete serialized world.");
    }
}
}
