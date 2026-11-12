using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Blueprints
{
    /// <summary>
    /// §120.13: правки живого мира — растягивание комнаты не сносит её проёмы и
    /// уже настеленный пол; «чертёж» кладётся в мир как набор обычных деталей.
    /// </summary>
    public sealed class WorldEditingTests
    {
        private static FloorSectorKey[] Hex(int q, int r) =>
            Enumerable.Range(0, 6).Select(sector => new FloorSectorKey(new TileCoord(q, r), sector)).ToArray();

        [Test]
        public void StretchingARoomKeepsItsDoorWindowAndExistingFloorBoards()
        {
            var draft = new BuildingBlueprintDraft();
            Assert.That(BlueprintEditorCommands.CreateRoom(draft, Hex(0, 0)).Succeeded, Is.True);
            var roomId = draft.Elements.First(e => e.Kind == BlueprintElementKind.FloorSector).RoomId;
            var boundary = BlueprintGeometry.BoundaryOf(Hex(0, 0));
            var grown = Hex(0, 0).Concat(Hex(1, 0)).ToArray();
            var grownBoundary = BlueprintGeometry.BoundaryOf(grown).ToHashSet();
            // Проёмы — на стороне, которая останется наружной после растягивания.
            var doorSegment = boundary.First(segment =>
                grownBoundary.Contains(segment) && BlueprintGeometry.TryDoorPortal(segment, out _));
            var windowSegment = boundary.First(segment =>
                grownBoundary.Contains(segment) && segment != doorSegment);
            Assert.That(BlueprintEditorCommands.PlaceOpening(
                draft, doorSegment, BlueprintElementKind.Door).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.PlaceOpening(
                draft, windowSegment, BlueprintElementKind.Window).Succeeded, Is.True);
            var doorId = draft.Elements.Single(e => e.Kind == BlueprintElementKind.Door).Id;
            var windowId = draft.Elements.Single(e => e.Kind == BlueprintElementKind.Window).Id;
            var floorIds = draft.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector)
                .ToDictionary(e => e.FloorSector, e => e.Id);

            var result = BlueprintEditorCommands.ResizeRoom(draft, roomId, grown);

            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(draft.Elements.Single(e => e.Kind == BlueprintElementKind.Door).Id, Is.EqualTo(doorId),
                "The door stays the same piece — in the live world it is already built.");
            Assert.That(draft.Elements.Single(e => e.Kind == BlueprintElementKind.Window).Id, Is.EqualTo(windowId));
            foreach (var pair in floorIds)
                Assert.That(draft.Elements.Single(e => e.Kind == BlueprintElementKind.FloorSector &&
                    e.FloorSector == pair.Key).Id, Is.EqualTo(pair.Value), "Old boards are not re-laid.");
            var envelope = draft.Elements.Where(e => e.Kind is BlueprintElementKind.Wall or
                BlueprintElementKind.Window or BlueprintElementKind.Door).Select(e => e.Segment).ToHashSet();
            Assert.That(envelope.SetEquals(grownBoundary), Is.True,
                "The contour is exactly the new outline: the wall between the two hexes is gone.");
        }

        [Test]
        public void WorldFloorsBecomeRoomsAndOnlyAClosedContourFollowsTheRoom()
        {
            var draft = new BuildingBlueprintDraft { GroundFurniture = true };
            // Дом: пол и замкнутый контур, как их отдаёт проекция мира (всё «вручную»).
            foreach (var sector in Hex(0, 0))
                Assert.That(BlueprintEditorCommands.AddFloorSector(draft, sector).Succeeded, Is.True);
            foreach (var segment in BlueprintGeometry.BoundaryOf(Hex(0, 0)))
                Assert.That(BlueprintEditorCommands.DrawWall(draft, segment.A, segment.B).Succeeded, Is.True);
            // Терраса: пол без стен, далеко в стороне.
            foreach (var sector in Hex(5, 0))
                Assert.That(BlueprintEditorCommands.AddFloorSector(draft, sector).Succeeded, Is.True);

            BlueprintEditorCommands.AssignRoomsToLooseFloors(draft);
            draft.Normalize();

            var house = draft.Elements.First(e => e.Kind == BlueprintElementKind.FloorSector &&
                e.FloorSector.Hex.Equals(new TileCoord(0, 0))).RoomId;
            var terrace = draft.Elements.First(e => e.Kind == BlueprintElementKind.FloorSector &&
                e.FloorSector.Hex.Equals(new TileCoord(5, 0))).RoomId;
            Assert.That(house, Is.GreaterThan(0));
            Assert.That(terrace, Is.GreaterThan(0));
            Assert.That(terrace, Is.Not.EqualTo(house));
            Assert.That(draft.OpenRooms, Does.Contain(terrace));
            Assert.That(draft.OpenRooms, Does.Not.Contain(house));
            Assert.That(draft.Elements.Where(e => e.Kind == BlueprintElementKind.Wall)
                .All(e => e.Origin == BlueprintElementOrigin.RoomBoundary && e.RoomId == house), Is.True);
            Assert.That(BlueprintValidator.Validate(draft).IsValid, Is.True);

            // Растянутая терраса стен не отращивает.
            var walls = draft.Elements.Count(e => e.Kind == BlueprintElementKind.Wall);
            Assert.That(BlueprintEditorCommands.ResizeRoom(
                draft, terrace, Hex(5, 0).Concat(Hex(6, 0))).Succeeded, Is.True);
            Assert.That(draft.Elements.Count(e => e.Kind == BlueprintElementKind.Wall), Is.EqualTo(walls));
        }

        [TestCase(0, 0, 0)]
        [TestCase(7, -3, 2)]
        [TestCase(-4, 9, 5)]
        public void APlanIsStampedIntoTheWorldAsOrdinaryPieces(int q, int r, int yawStep)
        {
            var plan = CommittedBuildingPlans.PlayerHut;
            var draft = new BuildingBlueprintDraft { GroundFurniture = true };

            var result = BlueprintEditorCommands.StampPlan(draft, plan, new TileCoord(q, r), yawStep);

            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(draft.Elements.Count, Is.EqualTo(plan.Elements.Count));
            Assert.That(draft.Furniture.Count, Is.EqualTo(plan.Furniture.Count));
            Assert.That(draft.Elements.Count(e => e.Kind == BlueprintElementKind.Door),
                Is.EqualTo(plan.Elements.Count(e => e.Kind == BlueprintElementKind.Door)));
            Assert.That(draft.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector)
                .All(e => e.RoomId > 0), Is.True, "Stamped floors are rooms and can be stretched at once.");
            // Мебель набора по-прежнему стоит на его полу — поворот перенёс всё согласованно.
            var floors = draft.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector)
                .Select(e => e.FloorSector).ToArray();
            foreach (var item in draft.Furniture)
                foreach (var junction in BlueprintFurnitureFootprints.OccupiedJunctions(item))
                    Assert.That(BlueprintGeometry.IsSupportedByFloor(junction, floors), Is.True,
                        $"{item.DefinitionId} at {junction}");

            // Второй такой же набор на то же место не встаёт и ничего не меняет.
            var again = BlueprintEditorCommands.StampPlan(draft, plan, new TileCoord(q, r), yawStep);
            Assert.That(again.Succeeded, Is.False);
            Assert.That(draft.Elements.Count, Is.EqualTo(plan.Elements.Count));
        }
    }
}
