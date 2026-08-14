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

        public static BlueprintCommandResult CreateRoom(
            BuildingBlueprintDraft draft, IEnumerable<FloorSectorKey> sectors)
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
                EnsureRoomSupports(working, roomId);
                return true;
            }, "Комната создана.");
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

                working.Elements.RemoveAll(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId);
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
                EnsureRoomSupports(working, roomId);
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
                    var remaining = working.Elements.Where(candidate =>
                        candidate.Kind == BlueprintElementKind.FloorSector && candidate.RoomId == roomId)
                        .Select(candidate => candidate.FloorSector).ToArray();
                    if (remaining.Length > 0 && !BlueprintGeometry.IsConnected(remaining)) return false;
                    RebuildRoomBoundary(working, roomId);
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
                return true;
            }, "Комната удалена.");
        }

        internal static void ReplaceDraft(BuildingBlueprintDraft target, BuildingBlueprintDraft source)
        {
            target.Version = source.Version;
            target.BlueprintId = source.BlueprintId;
            target.NextElementId = source.NextElementId;
            target.NextRoomId = source.NextRoomId;
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

        /// <summary>
        /// Every hex a room covers gets a corner post on all six of its corners.
        /// A roof sector needs three of the six corners of ITS OWN hex, and hexes
        /// gained by stretching a room only share two corners with the hex the
        /// room started on — so without this the player sees supports standing
        /// right there and still cannot roof the new part, forever. Corner nodes
        /// are shared, so one post serves every hex that touches it and the
        /// beams of neighbouring sectors land on the same node.
        /// </summary>
        private static void EnsureRoomSupports(BuildingBlueprintDraft draft, int roomId)
        {
            var hexes = new HashSet<TileCoord>(draft.Elements
                .Where(element => element.Kind == BlueprintElementKind.FloorSector &&
                                  element.RoomId == roomId)
                .Select(element => element.FloorSector.Hex));
            var existing = new HashSet<HexBuildNodeKey>(draft.Elements
                .Where(element => element.Kind == BlueprintElementKind.Support)
                .Select(element => element.Node));
            foreach (var hex in hexes)
            {
                for (var corner = 0; corner < 6; corner++)
                {
                    var node = BlueprintGeometry.HexCorner(hex, corner);
                    if (!existing.Add(node)) continue;
                    draft.Elements.Add(new BlueprintElementData
                    {
                        Id = draft.AllocateElementId(),
                        Kind = BlueprintElementKind.Support,
                        Origin = BlueprintElementOrigin.RoomBoundary,
                        RoomId = roomId,
                        Node = node
                    });
                }
            }
        }

        private static void RebuildRoomBoundary(BuildingBlueprintDraft draft, int roomId)
        {
            draft.Elements.RemoveAll(element =>
                element.Origin == BlueprintElementOrigin.RoomBoundary && element.RoomId == roomId &&
                element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door);
            var sectors = draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId)
                .Select(element => element.FloorSector).ToArray();
            foreach (var segment in BlueprintGeometry.BoundaryOf(sectors))
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

        public BlueprintCommandResult Execute(
            BuildingBlueprintDraft draft, Func<BuildingBlueprintDraft, BlueprintCommandResult> gesture)
        {
            var before = draft.Clone();
            var result = gesture(draft);
            if (!result.Succeeded) return result;
            _undo.Push(before);
            _redo.Clear();
            return result;
        }

        public bool Undo(BuildingBlueprintDraft draft)
        {
            if (!CanUndo) return false;
            _redo.Push(draft.Clone());
            BlueprintEditorCommands.ReplaceDraft(draft, _undo.Pop());
            return true;
        }

        public bool Redo(BuildingBlueprintDraft draft)
        {
            if (!CanRedo) return false;
            _undo.Push(draft.Clone());
            BlueprintEditorCommands.ReplaceDraft(draft, _redo.Pop());
            return true;
        }
    }
}
