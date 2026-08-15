using System.IO;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Blueprints
{
    [TestFixture]
    public sealed class BlueprintEditorTests
    {
        [Test]
        public void HexPerimeterHasThreeSectionsPerEdgeAndEighteenTotal()
        {
            var perimeter = BlueprintGeometry.HexPerimeter(TileCoord.Zero);
            Assert.That(perimeter, Has.Count.EqualTo(18));
            Assert.That(perimeter.Distinct().Count(), Is.EqualTo(18));
            for (var edge = 0; edge < 6; edge++)
                Assert.That(BuiltInBuildingBlueprints.EdgeSegments(TileCoord.Zero, edge), Has.Length.EqualTo(3));
        }

        [Test]
        public void WallDragSupportsAllThreeAxesInBothDirections()
        {
            var directions = BlueprintGeometry.NeighborDirections;
            Assert.That(directions, Has.Length.EqualTo(6));
            foreach (var direction in directions)
            {
                var draft = new BuildingBlueprintDraft();
                var end = new HexBuildNodeKey(direction.Q * 4, direction.R * 4);
                var result = BlueprintEditorCommands.DrawWall(draft, new HexBuildNodeKey(0, 0), end);
                Assert.That(result.Succeeded, Is.True, result.Message);
                Assert.That(draft.Elements.Count(element => element.Kind == BlueprintElementKind.Wall), Is.EqualTo(4));
            }
        }

        [Test]
        public void AdjacentHexesShareOneCanonicalThreeSegmentEdge()
        {
            var left = BlueprintGeometry.HexPerimeter(TileCoord.Zero).ToHashSet();
            var right = BlueprintGeometry.HexPerimeter(new TileCoord(1, 0)).ToHashSet();
            left.IntersectWith(right);
            Assert.That(left, Has.Count.EqualTo(3));
        }

        [Test]
        public void RoomResizeRemovesOnlyAutomaticBoundaryAndKeepsManualPartition()
        {
            var draft = new BuildingBlueprintDraft();
            var initial = new[]
            {
                new FloorSectorKey(TileCoord.Zero, 0),
                new FloorSectorKey(TileCoord.Zero, 1)
            };
            Assert.That(BlueprintEditorCommands.CreateRoom(draft, initial).Succeeded, Is.True);
            var roomId = draft.Elements.First(element => element.Kind == BlueprintElementKind.FloorSector).RoomId;

            var manualLine = BlueprintGeometry.SplitLine(
                BlueprintGeometry.HexCenter(TileCoord.Zero), BlueprintGeometry.HexCorner(TileCoord.Zero, 1));
            Assert.That(BlueprintEditorCommands.DrawWall(
                draft, BlueprintGeometry.HexCenter(TileCoord.Zero), BlueprintGeometry.HexCorner(TileCoord.Zero, 1)).Succeeded,
                Is.True);
            Assert.That(draft.Elements.Count(element => element.Origin == BlueprintElementOrigin.Manual &&
                manualLine.Contains(element.Segment)), Is.EqualTo(3));

            var disappearingBoundary = BlueprintGeometry.SplitLine(
                BlueprintGeometry.HexCenter(TileCoord.Zero), BlueprintGeometry.HexCorner(TileCoord.Zero, 2));
            Assert.That(draft.Elements.Any(element => element.Origin == BlueprintElementOrigin.RoomBoundary &&
                disappearingBoundary.Contains(element.Segment)), Is.True);

            var expanded = initial.Append(new FloorSectorKey(TileCoord.Zero, 2)).ToArray();
            Assert.That(BlueprintEditorCommands.ResizeRoom(draft, roomId, expanded).Succeeded, Is.True);
            Assert.That(draft.Elements.Count(element => element.Origin == BlueprintElementOrigin.Manual &&
                manualLine.Contains(element.Segment)), Is.EqualTo(3));
            Assert.That(draft.Elements.Any(element => element.Origin == BlueprintElementOrigin.RoomBoundary &&
                disappearingBoundary.Contains(element.Segment)), Is.False);
        }

        [Test]
        public void OpeningReplacesWallAndDoorUsesExactlyMiddlePortalSegment()
        {
            var draft = new BuildingBlueprintDraft();
            var edge = BuiltInBuildingBlueprints.EdgeSegments(TileCoord.Zero, 0);
            Assert.That(BlueprintEditorCommands.DrawWall(
                draft, BlueprintGeometry.HexCorner(TileCoord.Zero, 0), BlueprintGeometry.HexCorner(TileCoord.Zero, 1)).Succeeded,
                Is.True);

            Assert.That(BlueprintEditorCommands.PlaceOpening(
                draft, edge[0], BlueprintElementKind.Door).Succeeded, Is.False);
            Assert.That(BlueprintEditorCommands.PlaceOpening(
                draft, edge[1], BlueprintElementKind.Door).Succeeded, Is.True);
            Assert.That(BlueprintGeometry.TryDoorPortal(edge[1], out var portal), Is.True);
            Assert.That(portal, Is.Not.EqualTo(default(JunctionKey)));
            Assert.That(draft.Elements.Count(element => element.Kind == BlueprintElementKind.Door), Is.EqualTo(1));
            Assert.That(draft.Elements.Count, Is.EqualTo(3));
        }

        [Test]
        public void RoofSectorRequiresBothPostsOfItsOwnEdgeAndNeverTheCentrePost()
        {
            var draft = new BuildingBlueprintDraft();
            var sector = new RoofSectorKey(TileCoord.Zero, 0);
            var candidates = BlueprintGeometry.RoofSupports(sector);
            // A sector spans from its own outer hex edge up to the hex centre,
            // so the two posts of THAT edge carry it. Counting three of the
            // parent hex's six corners left a stretched room unroofable.
            Assert.That(candidates, Has.Count.EqualTo(2));
            Assert.That(candidates, Is.EquivalentTo(new[]
            {
                BlueprintGeometry.HexCorner(TileCoord.Zero, sector.Sector),
                BlueprintGeometry.HexCorner(TileCoord.Zero, sector.Sector + 1)
            }));
            Assert.That(candidates, Does.Not.Contain(BlueprintGeometry.HexCenter(TileCoord.Zero)));
            Assert.That(BlueprintEditorCommands.AddRoofSector(draft, sector).Succeeded, Is.False);
            Assert.That(draft.Elements, Is.Empty);

            Assert.That(BlueprintEditorCommands.AddSupport(
                draft, BlueprintGeometry.HexCenter(TileCoord.Zero)).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.AddSupport(draft, candidates[0]).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.AddRoofSector(draft, sector).Succeeded, Is.False,
                "The legacy centre post must not count as a roof support.");

            Assert.That(BlueprintEditorCommands.AddSupport(draft, candidates[1]).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.AddRoofSector(draft, sector).Succeeded, Is.True);
            var centreId = draft.Elements.Single(element =>
                element.Kind == BlueprintElementKind.Support &&
                element.Node == BlueprintGeometry.HexCenter(TileCoord.Zero)).Id;
            Assert.That(BlueprintEditorCommands.Delete(draft, centreId).Succeeded, Is.True);
            Assert.That(draft.Elements.Any(element => element.Id == centreId), Is.False);
            Assert.That(draft.Elements.Any(element => element.Kind == BlueprintElementKind.RoofSector), Is.True);
        }

        /// <summary>
        /// Furniture may only stand on a built floor, so a draft that is about
        /// to receive furniture starts with one whole hex of it.
        /// </summary>
        private static BuildingBlueprintDraft DraftWithFloor()
        {
            var draft = new BuildingBlueprintDraft();
            var result = BlueprintEditorCommands.CreateRoom(draft, Enumerable.Range(0, 6)
                .Select(index => new FloorSectorKey(TileCoord.Zero, index)));
            Assert.That(result.Succeeded, Is.True, result.Message);
            return draft;
        }

        [Test]
        public void FurnitureNeedsFloorUnderEveryJunctionEvenWhenNothingIsBuilt()
        {
            var empty = new BuildingBlueprintDraft();
            var refused = BlueprintEditorCommands.PlaceFurniture(
                empty, "test.single", TileCoord.Zero, 18);
            Assert.That(refused.Succeeded, Is.False,
                "A bed on bare grass beside the hut is not a placement, it is a bug.");
            Assert.That(refused.Validation.Issues.Any(issue => issue.Code == "furniture.floor"), Is.True);
            Assert.That(empty.Furniture, Is.Empty);

            var floored = DraftWithFloor();
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                floored, "test.single", TileCoord.Zero, 18).Succeeded, Is.True);
        }

        /// <summary>
        /// Every 0.5 wu seam of a closed room carries exactly one post pair.
        /// The section models author one pair each and BuildSegmentKey sorts its
        /// two nodes, so "the pair is on B" put two posts inside each other
        /// wherever the ordering turned over and none at all on the node next
        /// door: 6 doubled and 5 bare joints out of 24 on the player's draft.
        /// </summary>
        [Test]
        public void EverySeamOfAClosedRoomCarriesExactlyOnePost()
        {
            var draft = BuiltInBuildingBlueprints.Hut1Hex();
            var bays = draft.Elements.Where(element =>
                element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window
                    or BlueprintElementKind.Door).ToArray();
            var assigned = BlueprintGeometry.AssignSeamPosts(draft.Elements);

            var seams = bays.SelectMany(bay => new[] { bay.Segment.A, bay.Segment.B }).ToHashSet();
            Assert.That(assigned, Has.Count.EqualTo(bays.Length),
                "Around a closed ring every section carries a pair.");
            foreach (var seam in seams)
            {
                Assert.That(assigned.Values.Count(node => node == seam), Is.EqualTo(1),
                    $"Seam {seam} must carry exactly one post pair.");
            }
            foreach (var bay in bays)
            {
                Assert.That(assigned[bay.Id], Is.EqualTo(bay.Segment.A).Or.EqualTo(bay.Segment.B),
                    "A section can only put its pair on one of its own two nodes.");
            }

            var shuffled = BlueprintGeometry.AssignSeamPosts(
                draft.Elements.OrderByDescending(element => element.Id).ToArray());
            Assert.That(shuffled, Is.EqualTo(assigned),
                "The assignment must not depend on the order elements arrive in.");
        }

        [Test]
        public void OpenWallChainLeavesOnlyItsFirstNodeWithoutAPost()
        {
            var draft = new BuildingBlueprintDraft();
            Assert.That(BlueprintEditorCommands.DrawWall(
                draft, new HexBuildNodeKey(0, 0), new HexBuildNodeKey(0, 4)).Succeeded, Is.True);
            var assigned = BlueprintGeometry.AssignSeamPosts(draft.Elements);

            Assert.That(assigned, Has.Count.EqualTo(4));
            Assert.That(assigned.Values.Distinct().Count(), Is.EqualTo(4),
                "Four sections carry four pairs and never stack two on one seam.");
            var seams = draft.Elements
                .SelectMany(element => new[] { element.Segment.A, element.Segment.B }).ToHashSet();
            var bare = seams.Where(seam => assigned.Values.All(node => node != seam)).ToArray();
            Assert.That(bare, Has.Length.EqualTo(1),
                "An open chain has one more seam than it has sections, so exactly one end is bare.");
        }

        [Test]
        public void FurnitureHasSixYawStepsAndRejectsOccupiedJunction()
        {
            var draft = DraftWithFloor();
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, "test.single", TileCoord.Zero, 18).Succeeded, Is.True);
            var id = draft.Furniture.Single().Id;
            for (var step = 1; step <= 6; step++)
            {
                Assert.That(BlueprintEditorCommands.Rotate(draft, id, 1).Succeeded, Is.True);
                Assert.That(draft.Furniture.Single().YawStep, Is.EqualTo(step % 6));
            }
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, "test.single", TileCoord.Zero, 18).Succeeded, Is.False);
            Assert.That(draft.Furniture, Has.Count.EqualTo(1));
        }

        [Test]
        public void RotationPreviewCanCrossInvalidSixtyAndOneTwentyToCommitOneEighty()
        {
            var draft = DraftWithFloor();
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, "bed.basic", TileCoord.Zero, 18).Succeeded, Is.True);
            var bedId = draft.Furniture.Single().Id;
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, "test.single", TileCoord.Zero, 13).Succeeded, Is.True);
            var committedBefore = BuildingBlueprintJson.Serialize(draft);

            var sixty = BlueprintEditorCommands.Rotate(draft, bedId, 1);
            Assert.That(sixty.Succeeded, Is.False);
            Assert.That(sixty.Candidate, Is.Not.Null);
            Assert.That(sixty.Candidate.Furniture.Single(item => item.Id == bedId).YawStep, Is.EqualTo(1));
            Assert.That(BuildingBlueprintJson.Serialize(draft), Is.EqualTo(committedBefore));

            var oneTwenty = BlueprintEditorCommands.Rotate(sixty.Candidate, bedId, 1);
            Assert.That(oneTwenty.Succeeded, Is.False);
            Assert.That(oneTwenty.Candidate, Is.Not.Null);
            Assert.That(oneTwenty.Candidate.Furniture.Single(item => item.Id == bedId).YawStep, Is.EqualTo(2));
            Assert.That(BuildingBlueprintJson.Serialize(draft), Is.EqualTo(committedBefore));

            var oneEighty = BlueprintEditorCommands.Rotate(oneTwenty.Candidate, bedId, 1);
            Assert.That(oneEighty.Succeeded, Is.True, oneEighty.Message);
            Assert.That(oneEighty.Candidate.Furniture.Single(item => item.Id == bedId).YawStep, Is.EqualTo(3));
            Assert.That(BuildingBlueprintJson.Serialize(draft), Is.EqualTo(committedBefore),
                "Preview candidates must never mutate the committed draft.");

            var history = new BlueprintCommandHistory();
            Assert.That(history.Execute(draft,
                working => BlueprintEditorCommands.Rotate(working, bedId, 3)).Succeeded, Is.True);
            Assert.That(draft.Furniture.Single(item => item.Id == bedId).YawStep, Is.EqualTo(3));
            Assert.That(history.Undo(draft), Is.True);
            Assert.That(draft.Furniture.Single(item => item.Id == bedId).YawStep, Is.Zero,
                "The whole preview sequence commits as one undo gesture.");
        }

        [Test]
        public void ConstructorPanelLeavesFurnitureRotationToWorldHandles()
        {
            var uxml = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "Resources", "HexLive", "UI", "HutConstructor",
                "HutConstructorPanel.uxml"));
            var designer = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "HutTest",
                "HutLayoutDesigner.cs"));

            Assert.That(uxml, Does.Not.Contain("name=\"rotate-left\""));
            Assert.That(uxml, Does.Not.Contain("name=\"rotate-right\""));
            Assert.That(designer, Does.Contain("AddRotationButton(\"rotate-left-handle\", -1)"));
            Assert.That(designer, Does.Contain("AddRotationButton(\"rotate-right-handle\", 1)"));
        }

        [Test]
        public void ConstructorOwnsWorldPointerWithoutRtsSelectionMarquee()
        {
            var designer = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "HutTest",
                "HutLayoutDesigner.cs"));
            var camera = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Input",
                "RtsCameraController.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(designer, Does.Contain("SetSelectionInputSuppressed(this, true)"));
                Assert.That(designer, Does.Contain("SetSelectionInputSuppressed(this, false)"));
                Assert.That(camera, Does.Contain("if (SelectionInputSuppressed)"));
                Assert.That(camera, Does.Contain(
                    "if (SelectionInputSuppressed || !_selectionDragging) return;"));
                Assert.That(camera, Does.Contain("CancelPointerGesture();"));
            });
        }

        [Test]
        public void UndoRedoTreatsWholeWallDragAsOneGesture()
        {
            var draft = new BuildingBlueprintDraft();
            var history = new BlueprintCommandHistory();
            var result = history.Execute(draft, working => BlueprintEditorCommands.DrawWall(
                working, new HexBuildNodeKey(0, 0), new HexBuildNodeKey(0, 5)));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(draft.Elements, Has.Count.EqualTo(5));
            Assert.That(history.Undo(draft), Is.True);
            Assert.That(draft.Elements, Is.Empty);
            Assert.That(history.Redo(draft), Is.True);
            Assert.That(draft.Elements, Has.Count.EqualTo(5));
        }

        [Test]
        public void JsonRoundTripIsCanonicalAndLossless()
        {
            var draft = new BuildingBlueprintDraft { BlueprintId = "round_trip" };
            Assert.That(BlueprintEditorCommands.CreateRoom(draft, Enumerable.Range(0, 6)
                .Select(index => new FloorSectorKey(TileCoord.Zero, index))).Succeeded, Is.True);
            var first = BuildingBlueprintJson.Serialize(draft);
            Assert.That(BuildingBlueprintJson.TryDeserialize(first, out var loaded, out var error), Is.True, error);
            var second = BuildingBlueprintJson.Serialize(loaded);
            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void VersionOneDraftMigratesLegacyCentrePostToPerimeterSupports()
        {
            var legacy = new BuildingBlueprintDraft { BlueprintId = "legacy_centre_support" };
            var sector = new RoofSectorKey(TileCoord.Zero, 0);
            Assert.That(BlueprintEditorCommands.AddSupport(
                legacy, BlueprintGeometry.HexCenter(TileCoord.Zero)).Succeeded, Is.True);
            foreach (var support in BlueprintGeometry.RoofSupports(sector).Take(2))
                Assert.That(BlueprintEditorCommands.AddSupport(legacy, support).Succeeded, Is.True);
            legacy.Elements.Add(new BlueprintElementData
            {
                Id = legacy.AllocateElementId(),
                Kind = BlueprintElementKind.RoofSector,
                Origin = BlueprintElementOrigin.Manual,
                RoofSector = sector
            });

            var json = BuildingBlueprintJson.Serialize(legacy)
                .Replace("\"version\": 2", "\"version\": 1");
            Assert.That(BuildingBlueprintJson.TryDeserialize(json, out var migrated, out var error), Is.True, error);
            Assert.That(migrated.Version, Is.EqualTo(BuildingBlueprintDraft.CurrentVersion));
            Assert.That(migrated.Elements.Any(element =>
                element.Kind == BlueprintElementKind.Support &&
                element.Node == BlueprintGeometry.HexCenter(TileCoord.Zero)), Is.False);
            Assert.That(migrated.Elements.Count(element =>
                element.Kind == BlueprintElementKind.Support &&
                BlueprintGeometry.RoofSupports(sector).Contains(element.Node)),
                Is.EqualTo(BlueprintGeometry.RequiredRoofSupportCount));
        }

        [Test]
        public void BuiltInHutUsesIntegerKeysAndIsValid()
        {
            var draft = BuiltInBuildingBlueprints.Hut1Hex();
            var validation = BlueprintValidator.Validate(draft);
            Assert.That(validation.IsValid, Is.True,
                validation.Issues.Count == 0 ? string.Empty : validation.Issues[0].Message);
            Assert.That(draft.Elements.Count(element => element.Kind == BlueprintElementKind.FloorSector), Is.EqualTo(6));
            Assert.That(draft.Elements.Count(element =>
                element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door),
                Is.EqualTo(18));
            Assert.That(draft.Elements.Count(element => element.Kind == BlueprintElementKind.Door), Is.EqualTo(1));
            Assert.That(draft.Elements.Count(element => element.Kind == BlueprintElementKind.Support), Is.EqualTo(6));
            Assert.That(draft.Elements.Any(element => element.Kind == BlueprintElementKind.Support &&
                element.Node == BlueprintGeometry.HexCenter(TileCoord.Zero)), Is.False);
            var roomId = draft.Elements.First(element => element.Kind == BlueprintElementKind.FloorSector).RoomId;
            Assert.That(BlueprintValidator.IsIndoorRoom(draft, roomId), Is.True);
        }

        [Test]
        public void ThreeSectorsCreateConnectedHalfHexRoom()
        {
            var draft = new BuildingBlueprintDraft();
            var half = new[]
            {
                new FloorSectorKey(TileCoord.Zero, 0),
                new FloorSectorKey(TileCoord.Zero, 1),
                new FloorSectorKey(TileCoord.Zero, 2)
            };
            var result = BlueprintEditorCommands.CreateRoom(draft, half);
            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(draft.Elements.Count(element => element.Kind == BlueprintElementKind.FloorSector), Is.EqualTo(3));
            Assert.That(BlueprintGeometry.IsConnected(half), Is.True);
            Assert.That(BlueprintGeometry.BoundaryOf(half), Has.Count.EqualTo(15));
        }

        [Test]
        public void RoomShrinkCannotRemoveSectorUnderFurniture()
        {
            var draft = new BuildingBlueprintDraft();
            var full = Enumerable.Range(0, 6)
                .Select(index => new FloorSectorKey(TileCoord.Zero, index)).ToArray();
            Assert.That(BlueprintEditorCommands.CreateRoom(draft, full).Succeeded, Is.True);
            var roomId = draft.Elements.First(element => element.Kind == BlueprintElementKind.FloorSector).RoomId;
            var exclusiveSlot = HexPointLayout.GetInteriorTemplates().First(template =>
            {
                var pair = HexPointLayout.GetJunctionKeyPair(TileCoord.Zero, template.SubAxial);
                var junction = new JunctionKey(pair.xKey, pair.yKey);
                return BlueprintGeometry.Contains(full[0], junction) &&
                       full.Skip(1).All(sector => !BlueprintGeometry.Contains(sector, junction));
            }).Slot;
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, "test.single", TileCoord.Zero, exclusiveSlot).Succeeded, Is.True);

            var result = BlueprintEditorCommands.ResizeRoom(draft, roomId, full.Skip(1));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(draft.Elements.Count(element => element.Kind == BlueprintElementKind.FloorSector), Is.EqualTo(6));
        }

        [Test]
        public void InvalidFurnitureMoveExposesRedGhostCandidateButKeepsDraftUnchanged()
        {
            var draft = DraftWithFloor();
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, "test.single", TileCoord.Zero, 18).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, "test.single", TileCoord.Zero, 19).Succeeded, Is.True);
            var moving = draft.Furniture.Last().Id;
            var before = BuildingBlueprintJson.Serialize(draft);

            var result = BlueprintEditorCommands.Move(draft, moving, TileCoord.Zero, 18);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Candidate, Is.Not.Null);
            Assert.That(result.Validation.Issues.Any(issue => issue.Code == "furniture.overlap"), Is.True);
            Assert.That(BuildingBlueprintJson.Serialize(draft), Is.EqualTo(before));
        }

        [Test]
        public void DoorCanMoveOnlyBetweenPortalCapableWallSegments()
        {
            var draft = new BuildingBlueprintDraft();
            var first = BuiltInBuildingBlueprints.EdgeSegments(TileCoord.Zero, 0);
            var second = BuiltInBuildingBlueprints.EdgeSegments(TileCoord.Zero, 3);
            Assert.That(BlueprintEditorCommands.DrawWall(draft,
                BlueprintGeometry.HexCorner(TileCoord.Zero, 0), BlueprintGeometry.HexCorner(TileCoord.Zero, 1)).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.DrawWall(draft,
                BlueprintGeometry.HexCorner(TileCoord.Zero, 3), BlueprintGeometry.HexCorner(TileCoord.Zero, 4)).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.PlaceOpening(
                draft, first[1], BlueprintElementKind.Door).Succeeded, Is.True);
            var doorId = draft.Elements.Single(element => element.Kind == BlueprintElementKind.Door).Id;

            Assert.That(BlueprintEditorCommands.MoveOpening(draft, doorId, second[0]).Succeeded, Is.False);
            Assert.That(BlueprintEditorCommands.MoveOpening(draft, doorId, second[1]).Succeeded, Is.True);
            Assert.That(draft.Elements.Single(element => element.Kind == BlueprintElementKind.Door).Segment,
                Is.EqualTo(second[1]));
        }

        [Test]
        public void JsonRejectsFractionalIntegerKeys()
        {
            var json = BuildingBlueprintJson.Serialize(new BuildingBlueprintDraft())
                .Replace("\"nextRoomId\": 1", "\"nextRoomId\": 1.5");
            Assert.That(BuildingBlueprintJson.TryDeserialize(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("целым числом"));
        }

        [Test]
        public void ClosedManualPartitionSplitsCalculatedRoomGraph()
        {
            var draft = new BuildingBlueprintDraft();
            Assert.That(BlueprintEditorCommands.CreateRoom(draft, Enumerable.Range(0, 6)
                .Select(index => new FloorSectorKey(TileCoord.Zero, index))).Succeeded, Is.True);
            Assert.That(BlueprintValidator.CalculateRoomRegions(draft), Has.Count.EqualTo(1));

            Assert.That(BlueprintEditorCommands.DrawWall(draft,
                BlueprintGeometry.HexCorner(TileCoord.Zero, 0), BlueprintGeometry.HexCenter(TileCoord.Zero)).Succeeded,
                Is.True);
            Assert.That(BlueprintEditorCommands.DrawWall(draft,
                BlueprintGeometry.HexCenter(TileCoord.Zero), BlueprintGeometry.HexCorner(TileCoord.Zero, 3)).Succeeded,
                Is.True);

            var regions = BlueprintValidator.CalculateRoomRegions(draft);
            Assert.That(regions, Has.Count.EqualTo(2));
            Assert.That(regions.All(region => region.Count == 3), Is.True);
        }

        [Test]
        public void HutTestSceneStartsWithBlueprintConstructorEnabled()
        {
            var scene = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "Scenes", "HutTest.unity"));

            Assert.That(scene, Does.Contain("_layoutDesignerMode: 1"),
                "HutTest must add HutLayoutDesigner; otherwise it silently shows the legacy hut without the constructor UI.");
        }
    }
}
