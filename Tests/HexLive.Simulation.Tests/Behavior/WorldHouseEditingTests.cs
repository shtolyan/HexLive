using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;
public sealed class WorldHouseEditingTests
{
    [TestCase(0)] [TestCase(60)] [TestCase(120)] [TestCase(180)] [TestCase(240)] [TestCase(300)]
    public void WorldToolEditsExistingHouseAtItsActualSixWayPosition(float yaw)
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(o.BuildProduct));
        hut.RotationDegrees = yaw;
        HexLive.Simulation.Bootstrap.BuildingBootstrap.RepairPlanTopology(world, hut);
        BuildingBlueprintJson.TryDeserialize(BuildingRules.EditablePlanJsonFor(world, hut), out var draft, out _);
        var authored = draft.Elements.First(e => e.Kind == BlueprintElementKind.Wall);
        var slot = BlueprintBuildingPlan.SlotKey(authored);
        var physical = BuildingRules.ArchitectureObjects(world, hut).Single(o => o.ArchitectureElements[0].SlotKey == slot);
        var id = physical.Id;
        var worldElement = ArchitectureWorldEditing.ToWorld(authored, draft, hut.Tile, yaw);
        worldElement.Kind = BlueprintElementKind.Window;
        var placement = FreeArchitecturePlacementData.FromElement(worldElement);
        var count = BuildingRules.ArchitectureObjects(world, hut).Count();
        var admission = engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(new[] { placement }, Array.Empty<string>()));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(BuildingRules.ArchitectureObjects(world, hut).Count(), Is.EqualTo(count));
        Assert.That(FreeArchitectureRules.IsDemolitionSite(physical), Is.True);
        Assert.That(physical.ArchitectureElements[0].ReplacementDefinitionId, Is.EqualTo("architecture.window.wood"));
        Assert.That(FreeArchitectureRules.FreePieces(world).Any(o => o.ArchitectureElements[0].SlotKey == placement.SlotKey), Is.False,
            "Editing a house must not create a coincident independent duplicate wall.");
        Assert.That(world.Entities.Objects[id].DefinitionId, Is.EqualTo("architecture.wall.wood"));
        FreeArchitectureRules.CompleteDemolition(world, physical);
        Assert.That(BuildingRules.ArchitectureObjects(world, hut).Single(o => o.ArchitectureElements[0].SlotKey == slot).DefinitionId,
            Is.EqualTo("architecture.window.wood"));
    }

    [Test]
    public void ForeignExistingHouseBlocksWholeMixedGestureBeforeAnyMutation()
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World; world.FactionHomes.Clear();
        var hut = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(o.BuildProduct));
        hut.OwnerFaction = Faction.Colony2;
        BuildingBlueprintJson.TryDeserialize(BuildingRules.EditablePlanJsonFor(world, hut), out var draft, out _);
        var authored = draft.Elements.First(e => e.Kind == BlueprintElementKind.Wall);
        var global = ArchitectureWorldEditing.ToWorld(authored, draft, hut.Tile, hut.RotationDegrees);
        var key = BlueprintBuildingPlan.SlotKey(global);
        var count = world.Entities.Objects.Count; var json = BuildingBlueprintJson.Serialize(draft, false);
        var tile = world.Tiles.Items.Values.First(t => t.Flags.HasFlag(TileFlags.Walkable) && !t.Flags.HasFlag(TileFlags.Water) && !t.Flags.HasFlag(TileFlags.Blocked)).Coord;
        var free = FreeArchitecturePlacementData.FromElement(new BlueprintElementData { Kind = BlueprintElementKind.FloorSector, FloorSector = new FloorSectorKey(tile, 0) });
        var accepted = FreeArchitectureRules.Apply(world, new[] { free }, new[] { key }, out var error, new[] { Faction.Colony });
        Assert.That(accepted, Is.False); Assert.That(error, Is.EqualTo(ConstructionAccess.ForeignReason));
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(count));
        Assert.That(BuildingRules.EditablePlanJsonFor(world, hut), Is.EqualTo(json));
        Assert.That(BuildingRules.Elements(world, hut).Any(e => e.DemolitionPlanned), Is.False);
    }

    [Test]
    public void CuttingOneFloorBridgeKeepsBothSurvivingPartsAndTheirElementIds()
    {
        var plan = new BuildingBlueprintDraft();
        BlueprintEditorCommands.CreateRoom(plan, Enumerable.Range(0, 3).Select(i => new FloorSectorKey(TileCoord.Zero, i)), openFloor: true);
        var floors = plan.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector).ToArray();
        var bridge = floors.Single(e => e.FloorSector.Sector == 1);
        var result = BlueprintEditorCommands.Delete(plan, bridge.Id);
        Assert.That(result.Succeeded, Is.True, result.Message);
        Assert.That(plan.Elements.Select(e => e.Id), Is.EquivalentTo(floors.Where(e => e != bridge).Select(e => e.Id)));
        Assert.That(plan.Elements.Select(e => e.RoomId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(BlueprintValidator.Validate(plan).IsValid, Is.True);
        Assert.That(plan.OpenRooms, Is.EquivalentTo(plan.Elements.Select(e => e.RoomId)));
    }

    [Test]
    public void UndoCanRestoreAWholePhysicalFloorTileBeforeTeardownCompletes()
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World;
        var tile = world.Tiles.Items.Values.First(t => HexLive.Simulation.Bootstrap.BuildingBootstrap.CanPlaceHut(world, t.Coord)).Coord;
        var anchor = StructurePlacement.CenterJunction(world, tile).Value;
        var hut = WorldObjectMutations.SpawnObject(world, ContentIds.HutPlan, world.Junctions.Items[anchor].Fragment, tile, anchor);
        var original = BuiltInBuildingBlueprints.Hut1Hex(); original.Furniture.Clear();
        hut.BlueprintId = world.NextPlayerBlueprintId++; world.PlayerBlueprints[hut.BlueprintId] = original;
        BuildingRules.EnsureHutElements(world, hut, completed: true);
        HexLive.Simulation.Bootstrap.BuildingBootstrap.CompleteHut(world, hut);
        var revision = original.Clone(); revision.Elements.RemoveAll(e => e.Kind == BlueprintElementKind.FloorSector);
        Assert.That(engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(revision, false))).Accepted, Is.True);
        var floors = BuildingRules.ArchitectureObjects(world, hut).Where(o => o.DefinitionId == "architecture.floor.board").ToArray();
        Assert.That(floors.Length, Is.EqualTo(6));
        Assert.That(floors.All(FreeArchitectureRules.IsDemolitionSite), Is.True);
        var undo = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(original, false)));
        Assert.That(undo.Accepted, Is.True, "Restore the still-owned physical footprint: " + undo.Reason);
        Assert.That(floors.All(o => world.Entities.Objects.ContainsKey(o.Id) && !FreeArchitectureRules.IsDemolitionSite(o)), Is.True);
        Assert.That(world.Tiles.Items[tile].Flags.HasFlag(TileFlags.HasFloor), Is.True);
        Assert.That(hut.BuildProduct, Is.Empty);
    }

    [Test]
    public void FiveOldFloorSectorsPlusOneIndependentSectorMakeOnePhysicalFloor()
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World;
        // A real completed constructor house without furniture isolates the
        // physical floor union from the separate furniture-support gate.
        var tileState = world.Tiles.Items.Values.First(t => HexLive.Simulation.Bootstrap.BuildingBootstrap.CanPlaceHut(world, t.Coord));
        var anchor = StructurePlacement.CenterJunction(world, tileState.Coord).Value;
        var hut = WorldObjectMutations.SpawnObject(world, ContentIds.HutPlan, world.Junctions.Items[anchor].Fragment, tileState.Coord, anchor);
        var plan = BuiltInBuildingBlueprints.Hut1Hex(); plan.Furniture.Clear();
        hut.BlueprintId = world.NextPlayerBlueprintId++; world.PlayerBlueprints[hut.BlueprintId] = plan;
        BuildingRules.EnsureHutElements(world, hut, completed: true);
        HexLive.Simulation.Bootstrap.BuildingBootstrap.CompleteHut(world, hut);
        var removed = plan.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector).First(e => {
            var candidate = plan.Clone(); candidate.Elements.RemoveAll(x => x.Id == e.Id);
            return BlueprintValidator.Validate(candidate).IsValid;
        });
        var global = ArchitectureWorldEditing.ToWorld(removed, plan, hut.Tile, hut.RotationDegrees);
        var tile = global.FloorSector.Hex;
        var slot = BlueprintBuildingPlan.SlotKey(removed);
        var old = BuildingRules.ArchitectureObjects(world, hut).Single(o => o.ArchitectureElements[0].SlotKey == slot);
        plan.Elements.RemoveAll(e => e.Id == removed.Id);
        Assert.That(engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(plan, false))).Accepted, Is.True);
        Assert.That(world.Tiles.Items[tile].Flags.HasFlag(TileFlags.HasFloor), Is.True, "Paid old floor remains physical until work.");
        FreeArchitectureRules.CompleteDemolition(world, old);
        Assert.That(world.Tiles.Items[tile].Flags.HasFlag(TileFlags.HasFloor), Is.False);
        var placement = FreeArchitecturePlacementData.FromElement(global);
        var admission = engine.ApplyManualCommand(new ApplyFreeArchitectureCommand(new[] { placement }, Array.Empty<string>()));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var independent = FreeArchitectureRules.FreePieces(world).Single(o => o.ArchitectureElements[0].SlotKey == placement.SlotKey);
        foreach (var material in BuildSiteMath.AllMaterials)
            for (var i = 0; i < BuildSiteMath.Bill(independent, material); i++) independent.Contents.Add(new ItemInstance(material));
        while (BuildSiteMath.TryInstallOne(world, independent)) { }
        FreeArchitectureRules.Complete(world, independent);
        Assert.That(world.Tiles.Items[tile].Flags.HasFlag(TileFlags.HasFloor), Is.True);
        Assert.That(BuildingRules.ArchitectureObjects(world, hut).Count(o => o.DefinitionId == "architecture.floor.board"),
            Is.EqualTo(plan.Elements.Count(e => e.Kind == BlueprintElementKind.FloorSector)));
    }

    [TestCase(0)] [TestCase(60)] [TestCase(120)] [TestCase(180)] [TestCase(240)] [TestCase(300)]
    public void ProjectedFloorAndRoofVerticesMatchActualIntegerRotation(float yaw)
    {
        var plan = BuiltInBuildingBlueprints.Hut1Hex(); var target = new TileCoord(-6, -26);
        var origin = BlueprintGeometry.HexCenter(BlueprintBuildingPlan.AnchorTile(plan));
        var dest = BlueprintGeometry.HexCenter(target);
        foreach (var authored in plan.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector || e.Kind == BlueprintElementKind.RoofSector))
        {
            var transformed = ArchitectureWorldEditing.ToWorld(authored, plan, target, yaw);
            var sector = authored.Kind == BlueprintElementKind.FloorSector ? authored.FloorSector : new FloorSectorKey(authored.RoofSector.Hex, authored.RoofSector.Sector);
            var actual = transformed.Kind == BlueprintElementKind.FloorSector ? transformed.FloorSector : new FloorSectorKey(transformed.RoofSector.Hex, transformed.RoofSector.Sector);
            // Compare canonical lattice vertices, including both clockwise sector corners.
            var nodes = new[] { BlueprintGeometry.HexCenter(sector.Hex), BlueprintGeometry.HexCorner(sector.Hex, sector.Sector), BlueprintGeometry.HexCorner(sector.Hex, sector.Sector + 1) };
            var projected = nodes.Select(n => dest + BlueprintGeometry.RotateNode(new HexBuildNodeKey(n.Q - origin.Q, n.R - origin.R), (int)(yaw / 60))).ToArray();
            var actualNodes = new[] { BlueprintGeometry.HexCenter(actual.Hex), BlueprintGeometry.HexCorner(actual.Hex, actual.Sector), BlueprintGeometry.HexCorner(actual.Hex, actual.Sector + 1) };
            Assert.That(actualNodes, Is.EquivalentTo(projected));
        }
    }
}
