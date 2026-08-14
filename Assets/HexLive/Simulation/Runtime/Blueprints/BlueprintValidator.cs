using System.Collections.Generic;
using System.Linq;

namespace HexLive.Simulation.Runtime.Blueprints
{
    public static class BlueprintValidator
    {
        public static BlueprintValidationResult Validate(BuildingBlueprintDraft draft)
        {
            var result = new BlueprintValidationResult();
            if (draft == null)
            {
                result.Add("draft.null", "Чертёж отсутствует.");
                return result;
            }
            if (draft.Version != BuildingBlueprintDraft.CurrentVersion)
                result.Add("draft.version", $"Версия {draft.Version} не поддерживается.");

            ValidateIds(draft, result);
            ValidateSegments(draft, result);
            ValidateFloors(draft, result);
            ValidateRoofs(draft, result);
            ValidateFurniture(draft, result);
            return result;
        }

        public static bool IsIndoorRoom(BuildingBlueprintDraft draft, int roomId)
        {
            var floors = draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId)
                .Select(element => element.FloorSector).ToArray();
            if (floors.Length == 0 || !BlueprintGeometry.IsConnected(floors)) return false;
            return CalculateRoomRegions(draft, floors).All(region => IsIndoorRegion(draft, region));
        }

        /// <summary>
        /// Connected floor components after completed wall lines are removed
        /// from the adjacency graph. A closed manual partition therefore
        /// creates a genuine room without rewriting authored floor ids.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<FloorSectorKey>> CalculateRoomRegions(
            BuildingBlueprintDraft draft) => CalculateRoomRegions(draft,
                draft.Elements.Where(element => element.Kind == BlueprintElementKind.FloorSector)
                    .Select(element => element.FloorSector));

        private static IReadOnlyList<IReadOnlyList<FloorSectorKey>> CalculateRoomRegions(
            BuildingBlueprintDraft draft, IEnumerable<FloorSectorKey> source)
        {
            var remaining = source.Distinct().ToHashSet();
            var envelope = draft.Elements.Where(IsEnvelope).Select(element => element.Segment).ToHashSet();
            var regions = new List<IReadOnlyList<FloorSectorKey>>();
            while (remaining.Count > 0)
            {
                var region = new List<FloorSectorKey>();
                var queue = new Queue<FloorSectorKey>();
                var first = remaining.First();
                remaining.Remove(first);
                queue.Enqueue(first);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    region.Add(current);
                    foreach (var candidate in remaining.ToArray())
                    {
                        var shared = BlueprintGeometry.SectorBoundary(current)
                            .Intersect(BlueprintGeometry.SectorBoundary(candidate)).ToArray();
                        if (shared.Length != BlueprintGeometry.SectionsPerHexEdge ||
                            shared.All(envelope.Contains)) continue;
                        remaining.Remove(candidate);
                        queue.Enqueue(candidate);
                    }
                }
                region.Sort();
                regions.Add(region);
            }
            return regions;
        }

        private static bool IsIndoorRegion(
            BuildingBlueprintDraft draft, IReadOnlyList<FloorSectorKey> floors)
        {
            var roofs = new HashSet<RoofSectorKey>(draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.RoofSector)
                .Select(element => element.RoofSector));
            if (floors.Any(floor => !roofs.Contains(new RoofSectorKey(floor.Hex, floor.Sector)))) return false;
            var envelope = new HashSet<BuildSegmentKey>(draft.Elements.Where(IsEnvelope)
                .Select(element => element.Segment));
            return BlueprintGeometry.BoundaryOf(floors).All(envelope.Contains);
        }

        private static void ValidateIds(BuildingBlueprintDraft draft, BlueprintValidationResult result)
        {
            var ids = new HashSet<string>();
            foreach (var element in draft.Elements)
            {
                if (string.IsNullOrWhiteSpace(element.Id) || !ids.Add(element.Id))
                    result.Add("element.id", "Идентификатор элемента пуст или повторяется.", element.Id);
            }
            foreach (var item in draft.Furniture)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
                    result.Add("furniture.id", "Идентификатор мебели пуст или повторяется.", item.Id);
            }
        }

        private static void ValidateSegments(BuildingBlueprintDraft draft, BlueprintValidationResult result)
        {
            var used = new Dictionary<BuildSegmentKey, BlueprintElementData>();
            foreach (var element in draft.Elements.Where(IsEnvelope))
            {
                if (!BlueprintGeometry.IsUnitSegment(element.Segment))
                    result.Add("segment.length", "Стеновой элемент должен занимать ровно 0,5 wu.", element.Id);
                if (used.TryGetValue(element.Segment, out var previous))
                    result.Add("segment.duplicate", $"Сегмент уже занят элементом {previous.Id}.", element.Id);
                else
                    used[element.Segment] = element;
                if (element.Kind == BlueprintElementKind.Door &&
                    !BlueprintGeometry.TryDoorPortal(element.Segment, out _))
                    result.Add("door.portal", "Центр двери не совпадает с единственным junction-порталом.", element.Id);
            }
        }

        private static void ValidateFloors(BuildingBlueprintDraft draft, BlueprintValidationResult result)
        {
            var occupied = new HashSet<FloorSectorKey>();
            foreach (var floor in draft.Elements.Where(element => element.Kind == BlueprintElementKind.FloorSector))
            {
                if (!occupied.Add(floor.FloorSector))
                    result.Add("floor.duplicate", "Сектор пола занят дважды.", floor.Id);
            }
            foreach (var room in draft.Elements.Where(element =>
                         element.Kind == BlueprintElementKind.FloorSector && element.RoomId > 0)
                     .GroupBy(element => element.RoomId))
            {
                if (!BlueprintGeometry.IsConnected(room.Select(element => element.FloorSector)))
                    result.Add("room.disconnected", $"Комната {room.Key} разорвана на части.");
            }
        }

        private static void ValidateRoofs(BuildingBlueprintDraft draft, BlueprintValidationResult result)
        {
            var supports = new HashSet<HexBuildNodeKey>(draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.Support)
                .Select(element => element.Node));
            var roofs = new HashSet<RoofSectorKey>();
            foreach (var roof in draft.Elements.Where(element => element.Kind == BlueprintElementKind.RoofSector))
            {
                if (!roofs.Add(roof.RoofSector))
                    result.Add("roof.duplicate", "Сектор крыши занят дважды.", roof.Id);
                var missing = BlueprintGeometry.RoofSupports(roof.RoofSector).Count(node => !supports.Contains(node));
                if (missing > 0)
                    result.Add("roof.support", $"Сектору крыши не хватает опор: {missing} из 3.", roof.Id);
            }
        }

        private static void ValidateFurniture(BuildingBlueprintDraft draft, BlueprintValidationResult result)
        {
            var occupied = new Dictionary<JunctionKey, string>();
            var floors = draft.Elements.Where(element => element.Kind == BlueprintElementKind.FloorSector)
                .Select(element => element.FloorSector).ToArray();
            foreach (var item in draft.Furniture)
            {
                if (item.JunctionSlot < 0 ||
                    item.JunctionSlot >= HexLive.Simulation.Spatial.HexPointLayout.GetInteriorTemplates().Count)
                {
                    result.Add("furniture.junction", "Опорный junction мебели не существует.", item.Id);
                    continue;
                }
                if (item.YawStep < 0 || item.YawStep > 5)
                    result.Add("furniture.yaw", "Поворот мебели должен быть одним из шести шагов.", item.Id);
                foreach (var junction in BlueprintFurnitureFootprints.OccupiedJunctions(item))
                {
                    if (floors.Length > 0 && !BlueprintGeometry.IsSupportedByFloor(junction, floors))
                        result.Add("furniture.floor", $"Footprint выходит за построенный пол в junction {junction}.", item.Id);
                    if (occupied.TryGetValue(junction, out var previous))
                        result.Add("furniture.overlap", $"Footprint пересекается с {previous} в junction {junction}.", item.Id);
                    else
                        occupied[junction] = item.Id;
                }
            }
        }

        private static bool IsEnvelope(BlueprintElementData element) =>
            element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door;
    }
}
