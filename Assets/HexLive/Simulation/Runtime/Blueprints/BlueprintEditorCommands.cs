using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime.Blueprints
{
    public sealed class BlueprintCommandResult
    {
        internal BlueprintCommandResult(
            bool changed,
            BlueprintValidationResult validation,
            string message,
            BuildingBlueprintDraft candidate = null)
        {
            Changed = changed;
            Validation = validation;
            Message = message ?? string.Empty;
            Candidate = candidate;
        }

        public bool Changed { get; }
        public bool Succeeded => Changed && Validation.IsValid;
        public BlueprintValidationResult Validation { get; }
        public string Message { get; }

        /// <summary>
        /// Candidate produced by the gesture before validation. Invalid
        /// candidates are intentionally exposed read-only to presentation so
        /// it can draw a red ghost without ever mutating the live draft.
        /// </summary>
        public BuildingBlueprintDraft Candidate { get; }
    }

    /// <summary>
    /// Transaction boundary for every editor gesture. Callers never mutate a
    /// draft list directly: a complete gesture either validates and replaces
    /// the draft, or leaves it byte-for-byte unchanged.
    /// </summary>
    public static class BlueprintEditorCommands
    {
        public static BlueprintCommandResult DrawWall(
            BuildingBlueprintDraft draft,
            HexBuildNodeKey start,
            HexBuildNodeKey end,
            BlueprintElementOrigin origin = BlueprintElementOrigin.Manual,
            int roomId = 0)
        {
            return Transact(draft, working =>
            {
                if (!BlueprintGeometry.TryLine(start, end, out _, out _)) return false;
                var changed = false;
                foreach (var segment in BlueprintGeometry.SplitLine(start, end))
                {
                    if (FindEnvelope(working, segment) != null) continue;
                    working.Elements.Add(NewSegment(
                        working, BlueprintElementKind.Wall, segment, origin, roomId));
                    changed = true;
                }
                return changed;
            }, "Стена создана.");
        }

        public static BlueprintCommandResult AddSupport(
            BuildingBlueprintDraft draft, HexBuildNodeKey node)
        {
            return Transact(draft, working =>
            {
                if (working.Elements.Any(element =>
                        element.Kind == BlueprintElementKind.Support && element.Node == node)) return false;
                working.Elements.Add(new BlueprintElementData
                {
                    Id = working.AllocateElementId(),
                    Kind = BlueprintElementKind.Support,
                    Origin = BlueprintElementOrigin.Manual,
                    Node = node
                });
                return true;
            }, "Опорная балка создана.");
        }

        public static BlueprintCommandResult AddFloorSector(
            BuildingBlueprintDraft draft, FloorSectorKey sector, int roomId = 0)
        {
            return Transact(draft, working =>
            {
                if (working.Elements.Any(element =>
                        element.Kind == BlueprintElementKind.FloorSector && element.FloorSector == sector)) return false;
                working.Elements.Add(new BlueprintElementData
                {
                    Id = working.AllocateElementId(),
                    Kind = BlueprintElementKind.FloorSector,
                    Origin = BlueprintElementOrigin.Manual,
                    RoomId = roomId,
                    FloorSector = sector
                });
                return true;
            }, "Сектор пола создан.");
        }

        public static BlueprintCommandResult AddRoofSector(
            BuildingBlueprintDraft draft, RoofSectorKey sector)
        {
            return Transact(draft, working =>
            {
                if (working.Elements.Any(element =>
                        element.Kind == BlueprintElementKind.RoofSector && element.RoofSector == sector)) return false;
                working.Elements.Add(new BlueprintElementData
                {
                    Id = working.AllocateElementId(),
                    Kind = BlueprintElementKind.RoofSector,
                    Origin = BlueprintElementOrigin.Manual,
                    RoofSector = sector
                });
                return true;
            }, "Сектор крыши создан.");
        }

        /// <summary>
        /// Roofs a whole hex in one gesture: all six sectors in one transaction,
        /// all or nothing. The dome is still six sector elements, because that is
        /// what the save format, the Indoor rule, staged construction and the
        /// per-sector support check already speak — but the player places it as
        /// one roof and never ends up with a half-covered hex. Nothing stands at
        /// the hex centre: six panels rise from their own outer edges and meet
        /// there, so the apex is a joint, not a column (Spec 120).
        /// </summary>
        public static BlueprintCommandResult RoofHex(BuildingBlueprintDraft draft, TileCoord hex)
        {
            return Transact(draft, working =>
            {
                var existing = new HashSet<RoofSectorKey>(working.Elements
                    .Where(element => element.Kind == BlueprintElementKind.RoofSector)
                    .Select(element => element.RoofSector));
                var added = false;
                for (var sector = 0; sector < 6; sector++)
                {
                    var key = new RoofSectorKey(hex, sector);
                    if (!existing.Add(key)) continue;
                    working.Elements.Add(new BlueprintElementData
                    {
                        Id = working.AllocateElementId(),
                        Kind = BlueprintElementKind.RoofSector,
                        Origin = BlueprintElementOrigin.Manual,
                        RoofSector = key
                    });
                    added = true;
                }
                return added;
            }, "Крыша гекса построена.");
        }

        public static BlueprintCommandResult CreateRoom(
            BuildingBlueprintDraft draft, IEnumerable<FloorSectorKey> sectors,
            bool openFloor = false)
        {
            var selected = sectors?.Distinct().ToArray() ?? Array.Empty<FloorSectorKey>();
            return Transact(draft, working =>
            {
                if (selected.Length == 0 || !BlueprintGeometry.IsConnected(selected)) return false;
                var occupied = new HashSet<FloorSectorKey>(working.Elements
                    .Where(element => element.Kind == BlueprintElementKind.FloorSector)
                    .Select(element => element.FloorSector));
                if (selected.Any(occupied.Contains)) return false;

                var roomId = working.AllocateRoomId();
                // §120.8: «Пол без стен» — настил без автоконтура. Стены к нему
                // при желании чертятся вручную обычным инструментом.
                if (openFloor) working.OpenRooms.Add(roomId);
                foreach (var sector in selected)
                {
                    working.Elements.Add(new BlueprintElementData
                    {
                        Id = working.AllocateElementId(),
                        Kind = BlueprintElementKind.FloorSector,
                        Origin = BlueprintElementOrigin.Manual,
                        RoomId = roomId,
                        FloorSector = sector
                    });
                }
                RebuildRoomBoundary(working, roomId);
                return true;
            }, openFloor ? "Пол настелен." : "Комната создана.");
        }

        public static BlueprintCommandResult ResizeRoom(
            BuildingBlueprintDraft draft, int roomId, IEnumerable<FloorSectorKey> sectors)
        {
            var selected = sectors?.Distinct().ToArray() ?? Array.Empty<FloorSectorKey>();
            return Transact(draft, working =>
            {
                if (roomId <= 0 || selected.Length == 0 || !BlueprintGeometry.IsConnected(selected)) return false;
                var roomFloors = working.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId).ToArray();
                if (roomFloors.Length == 0) return false;

                var otherFloors = new HashSet<FloorSectorKey>(working.Elements.Where(element =>
                        element.Kind == BlueprintElementKind.FloorSector && element.RoomId != roomId)
                    .Select(element => element.FloorSector));
                if (selected.Any(otherFloors.Contains)) return false;

                var selectedSet = new HashSet<FloorSectorKey>(selected);
                var removed = roomFloors.Select(element => element.FloorSector)
                    .Where(sector => !selectedSet.Contains(sector)).ToArray();
                if (WouldRemoveFurnitureSupport(working, removed) ||
                    WouldOrphanManualElement(working, removed)) return false;

                // Сохранившийся сектор остаётся ТЕМ ЖЕ элементом: в мировом
                // режиме (§120.13) это уже настеленная доска, и пересоздание
                // её под новым id означало бы снос и новую стройку.
                var removedSet = new HashSet<FloorSectorKey>(removed);
                working.Elements.RemoveAll(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId &&
                    removedSet.Contains(element.FloorSector));
                var kept = new HashSet<FloorSectorKey>(working.Elements.Where(element =>
                        element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId)
                    .Select(element => element.FloorSector));
                foreach (var sector in selected)
                {
                    if (kept.Contains(sector)) continue;
                    working.Elements.Add(new BlueprintElementData
                    {
                        Id = working.AllocateElementId(),
                        Kind = BlueprintElementKind.FloorSector,
                        Origin = BlueprintElementOrigin.Manual,
                        RoomId = roomId,
                        FloorSector = sector
                    });
                }
                RebuildRoomBoundary(working, roomId);
                return true;
            }, "Размер комнаты изменён.");
        }

        public static BlueprintCommandResult PlaceOpening(
            BuildingBlueprintDraft draft,
            BuildSegmentKey segment,
            BlueprintElementKind openingKind)
        {
            if (openingKind != BlueprintElementKind.Window && openingKind != BlueprintElementKind.Door)
                return Failed("Проём должен быть окном или дверью.");
            return Transact(draft, working =>
            {
                var existing = FindEnvelope(working, segment);
                if (existing == null) return false;
                if (openingKind == BlueprintElementKind.Door &&
                    !BlueprintGeometry.TryDoorPortal(segment, out _)) return false;
                if (existing.Kind == openingKind) return false;
                existing.Kind = openingKind;
                return true;
            }, openingKind == BlueprintElementKind.Door ? "Дверь установлена." : "Окно установлено.");
        }

        public static BlueprintCommandResult MoveOpening(
            BuildingBlueprintDraft draft, string elementId, BuildSegmentKey target)
        {
            return Transact(draft, working =>
            {
                var opening = working.Elements.FirstOrDefault(element => element.Id == elementId &&
                    element.Kind is BlueprintElementKind.Window or BlueprintElementKind.Door);
                if (opening == null || FindEnvelope(working, target) is not { } targetWall ||
                    targetWall.Id == opening.Id) return false;
                if (opening.Kind == BlueprintElementKind.Door &&
                    !BlueprintGeometry.TryDoorPortal(target, out _)) return false;

                var oldKind = opening.Kind;
                opening.Kind = BlueprintElementKind.Wall;
                targetWall.Kind = oldKind;
                return true;
            }, "Проём перемещён.");
        }

        public static BlueprintCommandResult PlaceFurniture(
            BuildingBlueprintDraft draft,
            string definitionId,
            TileCoord tile,
            int junctionSlot,
            int yawStep = 0)
        {
            return Transact(draft, working =>
            {
                if (string.IsNullOrWhiteSpace(definitionId) || !ValidJunctionSlot(junctionSlot)) return false;
                working.Furniture.Add(new FurniturePlacementData
                {
                    Id = working.AllocateFurnitureId(),
                    DefinitionId = definitionId,
                    TileQ = tile.Q,
                    TileR = tile.R,
                    JunctionSlot = junctionSlot,
                    YawStep = BlueprintGeometry.NormalizeSector(yawStep)
                });
                return true;
            }, "Мебель установлена.");
        }

        public static BlueprintCommandResult Move(
            BuildingBlueprintDraft draft, string furnitureId, TileCoord tile, int junctionSlot)
        {
            return Transact(draft, working =>
            {
                if (!ValidJunctionSlot(junctionSlot)) return false;
                var item = working.Furniture.FirstOrDefault(candidate => candidate.Id == furnitureId);
                if (item == null ||
                    item.TileQ == tile.Q && item.TileR == tile.R && item.JunctionSlot == junctionSlot) return false;
                item.TileQ = tile.Q;
                item.TileR = tile.R;
                item.JunctionSlot = junctionSlot;
                return true;
            }, "Предмет перемещён.");
        }

        public static BlueprintCommandResult Rotate(
            BuildingBlueprintDraft draft, string furnitureId, int deltaSteps)
        {
            return Transact(draft, working =>
            {
                var item = working.Furniture.FirstOrDefault(candidate => candidate.Id == furnitureId);
                if (item == null || deltaSteps % 6 == 0) return false;
                item.YawStep = BlueprintGeometry.NormalizeSector(item.YawStep + deltaSteps);
                return true;
            }, "Предмет повёрнут.");
        }

        public static BlueprintCommandResult Delete(BuildingBlueprintDraft draft, string id)
        {
            return Transact(draft, working =>
            {
                var furniture = working.Furniture.RemoveAll(item => item.Id == id);
                if (furniture > 0) return true;
                var element = working.Elements.FirstOrDefault(candidate => candidate.Id == id);
                if (element == null) return false;
                var roomId = element.RoomId;
                working.Elements.Remove(element);
                if (element.Kind == BlueprintElementKind.FloorSector && roomId > 0)
                {
                    var firstNewRoom = working.NextRoomId;
                    NormalizeRoomConnectivity(working);
                    RebuildRoomBoundary(working, roomId);
                    for (var id = firstNewRoom; id < working.NextRoomId; id++) RebuildRoomBoundary(working, id);
                }
                return true;
            }, "Элемент удалён.");
        }

        public static BlueprintCommandResult DeleteMany(
            BuildingBlueprintDraft draft, IEnumerable<string> ids)
        {
            var selected = ids?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray() ??
                Array.Empty<string>();
            return Transact(draft, working =>
            {
                if (selected.Length == 0) return false;
                foreach (var id in selected)
                {
                    var result = Delete(working, id);
                    if (!result.Succeeded) return false;
                }
                return true;
            }, "Выбранные элементы удалены.");
        }

        public static BlueprintCommandResult DeleteRoom(BuildingBlueprintDraft draft, int roomId)
        {
            return Transact(draft, working =>
            {
                if (roomId <= 0) return false;
                var floors = working.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId).ToArray();
                if (floors.Length == 0) return false;
                var removed = floors.Select(element => element.FloorSector).ToArray();
                if (WouldRemoveFurnitureSupport(working, removed) ||
                    WouldOrphanManualElement(working, removed)) return false;
                working.Elements.RemoveAll(element =>
                    element.RoomId == roomId &&
                    (element.Kind == BlueprintElementKind.FloorSector ||
                     element.Origin == BlueprintElementOrigin.RoomBoundary));
                working.OpenRooms.Remove(roomId);
                return true;
            }, "Комната удалена.");
        }

        /// <summary>
        /// §120.13: «чертёж» — это просто набор, который кладут в мир. Все детали
        /// и мебель плана переносятся в черновик на гекс <paramref name="tile"/>
        /// с поворотом на <paramref name="yawStep"/> шагов по 60° и дальше живут
        /// как обычные стены, пол, крыша и мебель: их двигают, растягивают и
        /// сносят поштучно. Жест атомарен: занятый слот отклоняет его целиком.
        /// </summary>
        public static BlueprintCommandResult StampPlan(
            BuildingBlueprintDraft draft, BuildingBlueprintDraft plan, TileCoord tile, int yawStep)
        {
            if (plan == null) return Failed("Набор отсутствует.");
            var steps = BlueprintGeometry.NormalizeSector(yawStep);
            return Transact(draft, working =>
            {
                var occupied = new HashSet<string>(
                    working.Elements.Select(BlueprintBuildingPlan.SlotKey), StringComparer.Ordinal);
                foreach (var authored in plan.Elements)
                {
                    var element = ArchitectureWorldEditing.ToWorld(authored, plan, tile, steps * 60f);
                    if (!occupied.Add(BlueprintBuildingPlan.SlotKey(element))) return false;
                    element.Id = working.AllocateElementId();
                    working.Elements.Add(element);
                }

                var planAnchor = BlueprintBuildingPlan.AnchorTile(plan);
                var origin = HexLive.Simulation.Spatial.HexPointLayout.GetJunctionKeyPair(planAnchor, default);
                var destination = HexLive.Simulation.Spatial.HexPointLayout.GetJunctionKeyPair(tile, default);
                foreach (var placement in plan.Furniture)
                {
                    var local = placement.PrimaryJunction;
                    var offset = BlueprintGeometry.RotateJunctionOffset(
                        new JunctionKey(local.XKey - origin.xKey, local.YKey - origin.yKey), steps);
                    var key = new JunctionKey(destination.xKey + offset.XKey, destination.yKey + offset.YKey);
                    if (!TryInteriorSlot(key, out var hex, out var slot)) return false;
                    working.Furniture.Add(new FurniturePlacementData
                    {
                        Id = working.AllocateFurnitureId(),
                        DefinitionId = placement.DefinitionId,
                        TileQ = hex.Q,
                        TileR = hex.R,
                        JunctionSlot = slot,
                        YawStep = BlueprintGeometry.NormalizeSector(placement.YawStep + steps)
                    });
                }

                AssignRoomsToLooseFloors(working);
                return plan.Elements.Count + plan.Furniture.Count > 0;
            }, "Набор размещён.");
        }

        /// <summary>Якорь мебели — одна из внутренних точек гекса под ней.</summary>
        public static bool TryInteriorSlot(JunctionKey key, out TileCoord tile, out int slot)
        {
            tile = HexLive.Simulation.Spatial.HexSpatialMath.WorldToTile(BlueprintGeometry.JunctionToWorld(key));
            foreach (var template in HexLive.Simulation.Spatial.HexPointLayout.GetInteriorTemplates())
            {
                var pair = HexLive.Simulation.Spatial.HexPointLayout.GetJunctionKeyPair(tile, template.SubAxial);
                if (pair.xKey != key.XKey || pair.yKey != key.YKey) continue;
                slot = template.Slot;
                return true;
            }

            slot = -1;
            return false;
        }

        /// <summary>
        /// §120.13: комнаты для пола без комнаты — проекция живого мира и
        /// только что положенный набор. Комната — связный настил. Если ВЕСЬ её
        /// контур закрыт стенами/окнами/дверями, контур становится
        /// автоматическим и следует за растягиванием комнаты; иначе это
        /// открытый настил (терраса), и стены вокруг остаются ручными.
        /// </summary>
        public static void AssignRoomsToLooseFloors(BuildingBlueprintDraft draft)
        {
            if (draft == null) return;
            var remaining = new Dictionary<FloorSectorKey, BlueprintElementData>();
            foreach (var element in draft.Elements)
                if (element.Kind == BlueprintElementKind.FloorSector && element.RoomId <= 0)
                    remaining[element.FloorSector] = element;
            if (remaining.Count == 0) return;
            var envelope = new Dictionary<BuildSegmentKey, BlueprintElementData>();
            foreach (var element in draft.Elements)
                if (element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or
                    BlueprintElementKind.Door)
                    envelope[element.Segment] = element;

            while (remaining.Count > 0)
            {
                var roomId = draft.AllocateRoomId();
                var component = new List<FloorSectorKey>();
                var queue = new Queue<FloorSectorKey>();
                queue.Enqueue(remaining.Keys.OrderBy(key => key).First());
                while (queue.Count > 0)
                {
                    var key = queue.Dequeue();
                    if (!remaining.TryGetValue(key, out var floor)) continue;
                    remaining.Remove(key);
                    floor.RoomId = roomId;
                    component.Add(key);
                    foreach (var next in remaining.Keys
                                 .Where(candidate => BlueprintGeometry.AreSectorsAdjacent(key, candidate))
                                 .OrderBy(candidate => candidate).ToArray())
                        queue.Enqueue(next);
                }

                var boundary = BlueprintGeometry.BoundaryOf(component);
                if (boundary.Count > 0 && boundary.All(envelope.ContainsKey))
                {
                    foreach (var segment in boundary)
                    {
                        envelope[segment].Origin = BlueprintElementOrigin.RoomBoundary;
                        envelope[segment].RoomId = roomId;
                    }
                }
                else
                {
                    draft.OpenRooms.Add(roomId);
                }
            }
        }

        internal static void ReplaceDraft(BuildingBlueprintDraft target, BuildingBlueprintDraft source)
        {
            target.Version = source.Version;
            target.BlueprintId = source.BlueprintId;
            target.NextElementId = source.NextElementId;
            target.NextRoomId = source.NextRoomId;
            target.OpenRooms = new List<int>(source.OpenRooms);
            target.GroundFurniture = source.GroundFurniture;
            target.Elements = source.Elements.Select(element => element.Clone()).ToList();
            target.Furniture = source.Furniture.Select(item => item.Clone()).ToList();
            target.Normalize();
        }

        private static BlueprintCommandResult Transact(
            BuildingBlueprintDraft draft,
            Func<BuildingBlueprintDraft, bool> mutation,
            string successMessage)
        {
            if (draft == null) return Failed("Чертёж отсутствует.");
            var working = draft.Clone();
            if (!mutation(working)) return Failed("Жест не изменил чертёж.");
            working.Normalize();
            var validation = BlueprintValidator.Validate(working);
            if (!validation.IsValid)
                return new BlueprintCommandResult(false, validation, validation.Issues[0].Message, working);
            ReplaceDraft(draft, working);
            return new BlueprintCommandResult(true, validation, successMessage, working);
        }

        private static BlueprintCommandResult Failed(string message) =>
            new BlueprintCommandResult(false, new BlueprintValidationResult(), message);

        private static BlueprintElementData FindEnvelope(BuildingBlueprintDraft draft, BuildSegmentKey segment) =>
            draft.Elements.FirstOrDefault(element =>
                element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door &&
                element.Segment == segment);

        private static BlueprintElementData NewSegment(
            BuildingBlueprintDraft draft,
            BlueprintElementKind kind,
            BuildSegmentKey segment,
            BlueprintElementOrigin origin,
            int roomId) => new BlueprintElementData
        {
            Id = draft.AllocateElementId(),
            Kind = kind,
            Origin = origin,
            RoomId = roomId,
            Segment = segment
        };

        /// <summary>Relabels disconnected floor components after a cut. Only
        /// private working drafts are passed here; walls are never removed by
        /// this bookkeeping, and each surviving floor retains its element Id.</summary>
        public static void NormalizeRoomConnectivity(BuildingBlueprintDraft draft)
        {
            foreach (var room in draft.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector && e.RoomId > 0)
                         .GroupBy(e => e.RoomId).ToArray())
            {
                var remaining = room.ToDictionary(e => e.FloorSector);
                var first = true;
                while (remaining.Count > 0)
                {
                    var id = first ? room.Key : draft.AllocateRoomId();
                    if (!first && draft.OpenRooms.Contains(room.Key)) draft.OpenRooms.Add(id);
                    first = false;
                    var start = remaining.Keys.OrderBy(key => key).First();
                    var queue = new Queue<FloorSectorKey>(); queue.Enqueue(start);
                    while (queue.Count > 0)
                    {
                        var key = queue.Dequeue();
                        if (!remaining.TryGetValue(key, out var floor)) continue;
                        remaining.Remove(key); floor.RoomId = id;
                        foreach (var next in remaining.Keys.Where(k => BlueprintGeometry.AreSectorsAdjacent(key, k)).OrderBy(k => k).ToArray())
                            queue.Enqueue(next);
                    }
                }
            }
        }

        private static void RebuildRoomBoundary(BuildingBlueprintDraft draft, int roomId)
        {
            var sectors = draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId)
                .Select(element => element.FloorSector).ToArray();
            // §120.8: открытый настил границу не отращивает — только снимает
            // свою прежнюю, если комнату перевели в открытые.
            var open = draft.OpenRooms.Contains(roomId);
            // §120.13: секция автоконтура, оставшаяся на границе, СОХРАНЯЕТСЯ
            // вместе со своим видом. Прежде растягивание комнаты сносило весь
            // контур и отращивало сплошные стены — окна и дверь комнаты молча
            // исчезали, а в мировом режиме это снос уже построенной двери.
            var boundary = open
                ? new HashSet<BuildSegmentKey>()
                : new HashSet<BuildSegmentKey>(BlueprintGeometry.BoundaryOf(sectors));
            draft.Elements.RemoveAll(element =>
                element.Origin == BlueprintElementOrigin.RoomBoundary && element.RoomId == roomId &&
                element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door &&
                !boundary.Contains(element.Segment));
            if (open) return;
            foreach (var segment in boundary)
            {
                if (FindEnvelope(draft, segment) != null) continue;
                draft.Elements.Add(NewSegment(draft, BlueprintElementKind.Wall, segment,
                    BlueprintElementOrigin.RoomBoundary, roomId));
            }
        }

        private static bool WouldRemoveFurnitureSupport(
            BuildingBlueprintDraft draft, IReadOnlyCollection<FloorSectorKey> removed)
        {
            if (removed.Count == 0) return false;
            var remaining = draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && !removed.Contains(element.FloorSector))
                .Select(element => element.FloorSector).ToArray();
            return draft.Furniture.Any(item => BlueprintFurnitureFootprints.OccupiedJunctions(item)
                .Any(junction => !BlueprintGeometry.IsSupportedByFloor(junction, remaining)));
        }

        private static bool WouldOrphanManualElement(
            BuildingBlueprintDraft draft, IReadOnlyCollection<FloorSectorKey> removed)
        {
            if (removed.Count == 0) return false;
            var removedBoundary = new HashSet<BuildSegmentKey>(removed.SelectMany(BlueprintGeometry.SectorBoundary));
            if (draft.Elements.Any(element => element.Origin == BlueprintElementOrigin.Manual &&
                    element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door &&
                    removedBoundary.Contains(element.Segment))) return true;
            return draft.Elements.Any(element => element.Kind == BlueprintElementKind.RoofSector &&
                removed.Contains(new FloorSectorKey(element.RoofSector.Hex, element.RoofSector.Sector)));
        }

        private static bool ValidJunctionSlot(int slot) =>
            slot >= 0 && slot < HexLive.Simulation.Spatial.HexPointLayout.GetInteriorTemplates().Count;
    }

    public sealed class BlueprintCommandHistory
    {
        private readonly Stack<BuildingBlueprintDraft> _undo = new Stack<BuildingBlueprintDraft>();
        private readonly Stack<BuildingBlueprintDraft> _redo = new Stack<BuildingBlueprintDraft>();

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public void Clear() { _undo.Clear(); _redo.Clear(); }

        /// <summary>§120.13: мир изменился НЕ жестом игрока (колония разметила
        /// кровать, площадка достроилась). Та же поправка вносится во все
        /// сохранённые шаги, иначе Undo «вернул бы» мир без этого предмета — то
        /// есть снёс бы чужую площадку.</summary>
        public void Rewrite(Action<BuildingBlueprintDraft> change)
        {
            if (change == null) return;
            foreach (var draft in _undo) change(draft);
            foreach (var draft in _redo) change(draft);
        }

        public BlueprintCommandResult Execute(
            BuildingBlueprintDraft draft, Func<BuildingBlueprintDraft, BlueprintCommandResult> gesture,
            Func<BuildingBlueprintDraft, BuildingBlueprintDraft, bool> canApply = null,
            string deniedMessage = "")
        {
            var before = draft.Clone();
            var working = canApply == null ? draft : draft.Clone();
            var result = gesture(working);
            if (!result.Succeeded) return result;
            if (canApply != null && !canApply(before, working))
                return new BlueprintCommandResult(false, new BlueprintValidationResult(), deniedMessage, working);
            if (canApply != null) BlueprintEditorCommands.ReplaceDraft(draft, working);
            _undo.Push(before);
            _redo.Clear();
            return result;
        }

        public bool Undo(BuildingBlueprintDraft draft,
            Func<BuildingBlueprintDraft, BuildingBlueprintDraft, bool> canApply = null)
        {
            if (!CanUndo) return false;
            if (canApply != null && !canApply(draft, _undo.Peek())) return false;
            _redo.Push(draft.Clone());
            BlueprintEditorCommands.ReplaceDraft(draft, _undo.Pop());
            return true;
        }

        public bool Redo(BuildingBlueprintDraft draft,
            Func<BuildingBlueprintDraft, BuildingBlueprintDraft, bool> canApply = null)
        {
            if (!CanRedo) return false;
            if (canApply != null && !canApply(draft, _redo.Peek())) return false;
            _undo.Push(draft.Clone());
            BlueprintEditorCommands.ReplaceDraft(draft, _redo.Pop());
            return true;
        }
    }
}
