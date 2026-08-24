using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime.Blueprints
{
    /// <summary>
    /// §120.10 direct-world architecture. A free piece owns itself: the same
    /// top-level WorldObject is its construction site, saved LEGO identity and
    /// render anchor. No BuildingBlueprintDraft, BlueprintId or footprint owner
    /// is created in authoritative world state.
    /// </summary>
    public static class FreeArchitectureRules
    {
        public const int MaxGestureElements = 256;

        private static readonly string[] EnvelopeIds =
        {
            "architecture.wall.wood",
            "architecture.window.wood",
            "architecture.door.wood"
        };

        public static bool IsFreePiece(WorldObjectState piece) =>
            piece != null && piece.ArchitectureOwnerId == piece.Id &&
            piece.ArchitectureElements.Count == 1;

        public static bool IsFreePiece(ObjectSnapshot piece) =>
            piece != null && piece.ArchitectureOwnerObjectId == piece.Id.Value &&
            piece.ArchitectureElements.Count == 1;

        public static bool TryDecode(
            string definitionId, string slotKey, out BlueprintElementData element)
        {
            element = null;
            if (string.IsNullOrEmpty(definitionId) || string.IsNullOrEmpty(slotKey)) return false;
            var kind = KindForDefinition(definitionId);
            if (kind is null) return false;

            var decoded = new BlueprintElementData
            {
                Id = slotKey,
                Kind = kind.Value,
                Origin = BlueprintElementOrigin.Manual
            };
            switch (kind.Value)
            {
                case BlueprintElementKind.Support:
                    if (!slotKey.StartsWith("support:", StringComparison.Ordinal) ||
                        !TryNode(slotKey.AsSpan(8), out var node)) return false;
                    decoded.Node = node;
                    break;
                case BlueprintElementKind.FloorSector:
                    if (!slotKey.StartsWith("floor:", StringComparison.Ordinal) ||
                        !TrySector(slotKey.AsSpan(6), out var floorHex, out var floorSector)) return false;
                    decoded.FloorSector = new FloorSectorKey(floorHex, floorSector);
                    break;
                case BlueprintElementKind.RoofSector:
                    if (!slotKey.StartsWith("roof:", StringComparison.Ordinal) ||
                        !TrySector(slotKey.AsSpan(5), out var roofHex, out var roofSector)) return false;
                    decoded.RoofSector = new RoofSectorKey(roofHex, roofSector);
                    break;
                default:
                    if (!slotKey.StartsWith("bay:", StringComparison.Ordinal) ||
                        !TrySegment(slotKey.AsSpan(4), out var segment)) return false;
                    decoded.Segment = segment;
                    break;
            }

            element = decoded;
            return true;
        }

        public static bool Apply(
            WorldState world,
            IReadOnlyList<FreeArchitecturePlacementData> placements,
            IReadOnlyList<string> removedSlotKeys,
            out string error)
        {
            error = string.Empty;
            placements ??= Array.Empty<FreeArchitecturePlacementData>();
            removedSlotKeys ??= Array.Empty<string>();
            if (placements.Count + removedSlotKeys.Count == 0 ||
                placements.Count + removedSlotKeys.Count > MaxGestureElements)
            {
                error = "InvalidArchitectureGesture";
                return false;
            }

            var freeBySlot = FreePieces(world)
                .ToDictionary(piece => piece.ArchitectureElements[0].SlotKey, StringComparer.Ordinal);
            var removals = new HashSet<string>(removedSlotKeys, StringComparer.Ordinal);
            if (removals.Any(slot => !freeBySlot.ContainsKey(slot)))
            {
                error = "ArchitectureElementMissing";
                return false;
            }

            var placementBySlot = new Dictionary<string, FreeArchitecturePlacementData>(StringComparer.Ordinal);
            var affectedTiles = new HashSet<TileCoord>();
            foreach (var placement in placements)
            {
                if (placement == null || !ValidatePlacement(world, placement))
                {
                    error = "InvalidArchitecturePlacement";
                    return false;
                }
                if (!placementBySlot.TryAdd(placement.SlotKey, placement))
                {
                    error = "DuplicateArchitectureSlot";
                    return false;
                }
            }

            // Existing direct pieces may be replaced in-place (wall -> opening)
            // by the same slot. Every other occupied free slot is a conflict.
            foreach (var pair in placementBySlot)
            {
                if (freeBySlot.ContainsKey(pair.Key)) removals.Add(pair.Key);
            }

            foreach (var slot in removals)
            {
                if (!freeBySlot.TryGetValue(slot, out var piece)) continue;
                affectedTiles.Add(piece.Tile);
                ForgetPiece(world, piece.Id);
                WorldObjectMutations.DespawnObject(world, piece.Id);
            }

            foreach (var placement in placementBySlot.Values)
            {
                affectedTiles.Add(placement.AnchorTile);
                Spawn(world, placement);
            }

            RefreshAll(world, affectedTiles);
            return true;
        }

        public static void SyncProgress(WorldState world, WorldObjectState piece)
        {
            if (!IsFreePiece(piece)) return;
            var state = piece.ArchitectureElements[0];
            state.DeliveredSticks = Count(piece, ContentIds.Stick);
            state.DeliveredBoards = Count(piece, ContentIds.Board);
            state.DeliveredRope = Count(piece, ContentIds.Rope);
            state.DeliveredLeaves = Count(piece, ContentIds.PalmLeaf);
            if (state.DeliveredSticks >= state.RequiredSticks &&
                state.DeliveredBoards >= state.RequiredBoards &&
                state.DeliveredRope >= state.RequiredRope &&
                state.DeliveredLeaves >= state.RequiredLeaves)
            {
                state.WorkDone = state.WorkRequired;
            }
            RefreshBuildability(world);
            RepairTopology(world);
            RefreshTileFlags(world, new[] { piece.Tile });
        }

        public static void Complete(WorldState world, WorldObjectState piece)
        {
            if (!IsFreePiece(piece)) return;
            SyncProgress(world, piece);
            piece.BuildProduct = string.Empty;
            RepairTopology(world);
            RefreshTileFlags(world, new[] { piece.Tile });
        }

        public static void RefreshAll(WorldState world)
        {
            RefreshAll(world, FreePieces(world).Select(piece => piece.Tile));
        }

        private static void RefreshAll(WorldState world, IEnumerable<TileCoord> affectedTiles)
        {
            RefreshGeometryAndBills(world);
            RefreshBuildability(world);
            RepairTopology(world);
            RefreshTileFlags(world, affectedTiles);
        }

        public static IEnumerable<WorldObjectState> FreePieces(WorldState world) =>
            world.Entities.Objects.Values.Where(IsFreePiece);

        private static WorldObjectState Spawn(
            WorldState world, FreeArchitecturePlacementData placement)
        {
            var tile = world.Tiles.Items[placement.AnchorTile];
            var anchorId = StructurePlacement.CenterJunction(world, placement.AnchorTile)
                           ?? tile.Junctions.First();
            var fragment = world.Junctions.Items[anchorId].Fragment;
            var definitionId = DefinitionForKind(placement.Kind);
            var piece = WorldObjectMutations.SpawnObject(
                world, definitionId, fragment, placement.AnchorTile, anchorId);
            piece.ArchitectureOwnerId = piece.Id;
            piece.BuildProduct = definitionId;
            piece.RotationDegrees = 0f;
            piece.ArchitectureElements.Add(new ArchitectureElementState
            {
                ElementId = piece.Id.Value,
                DefinitionId = definitionId,
                SlotKey = placement.SlotKey,
                Layer = PlacementLayer.Architecture,
                Buildable = placement.Kind != BlueprintElementKind.RoofSector
            });
            Bootstrap.BuildingBootstrap.RememberPlanSite(world, piece);
            return piece;
        }

        private static void ForgetPiece(WorldState world, ObjectId pieceId)
        {
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (!npc.Memory.KnownObjects.Remove(pieceId)) continue;
                npc.Memory.Version++;
            }
        }

        private static bool ValidatePlacement(
            WorldState world, FreeArchitecturePlacementData placement)
        {
            if (!world.Tiles.Items.TryGetValue(placement.AnchorTile, out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Water) ||
                tile.Flags.HasFlag(TileFlags.Blocked)) return false;

            var element = placement.ToElement("validate");
            return placement.Kind switch
            {
                BlueprintElementKind.Support =>
                    placement.AnchorTile == HexSpatialMath.WorldToTile(BlueprintGeometry.ToWorld(element.Node)),
                BlueprintElementKind.FloorSector =>
                    element.FloorSector.Hex == placement.AnchorTile,
                BlueprintElementKind.RoofSector =>
                    element.RoofSector.Hex == placement.AnchorTile,
                BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door =>
                    BlueprintGeometry.IsUnitSegment(element.Segment) &&
                    placement.AnchorTile == SegmentTile(element.Segment) &&
                    (placement.Kind != BlueprintElementKind.Door ||
                     BlueprintGeometry.TryDoorPortal(element.Segment, out _)),
                _ => false
            };
        }

        private static TileCoord SegmentTile(BuildSegmentKey segment)
        {
            var a = BlueprintGeometry.ToWorld(segment.A);
            var b = BlueprintGeometry.ToWorld(segment.B);
            return HexSpatialMath.WorldToTile(new Float2(
                (a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f));
        }

        private static void RefreshGeometryAndBills(WorldState world)
        {
            var pieces = FreePieces(world).ToArray();
            if (pieces.Length == 0) return;
            var draft = new BuildingBlueprintDraft
            {
                BlueprintId = "free_world_projection",
                HasAnchor = true,
                AnchorQ = 0,
                AnchorR = 0
            };
            var bySlot = new Dictionary<string, WorldObjectState>(StringComparer.Ordinal);
            foreach (var piece in pieces)
            {
                var state = piece.ArchitectureElements[0];
                if (!TryDecode(state.DefinitionId, state.SlotKey, out var element)) continue;
                draft.Elements.Add(element);
                bySlot[state.SlotKey] = piece;
            }

            draft.Normalize();
            foreach (var module in BlueprintBuildingPlan.Modules(draft))
            {
                if (!bySlot.TryGetValue(module.Key, out var piece)) continue;
                var state = piece.ArchitectureElements[0];
                var anchor = HexSpatialMath.TileToWorld(piece.Tile);
                state.DefinitionId = DefinitionForKind(module.Kind);
                state.SlotIndex = module.Index;
                state.LocalX = module.LocalX - anchor.X;
                state.LocalZ = module.LocalZ - anchor.Y;
                state.LocalYaw = module.LocalYaw;
                state.RequiredSticks = module.Sticks;
                state.RequiredBoards = module.Boards;
                state.RequiredRope = module.Rope;
                state.RequiredLeaves = module.Leaves;
                piece.DefinitionId = state.DefinitionId;
                if (!string.IsNullOrEmpty(piece.BuildProduct)) piece.BuildProduct = state.DefinitionId;
                piece.BillSticks = module.Sticks;
                piece.BillBoards = module.Boards;
                piece.BillRope = module.Rope;
                piece.BillLeaves = module.Leaves;
                state.DeliveredSticks = Count(piece, ContentIds.Stick);
                state.DeliveredBoards = Count(piece, ContentIds.Board);
                state.DeliveredRope = Count(piece, ContentIds.Rope);
                state.DeliveredLeaves = Count(piece, ContentIds.PalmLeaf);
                if (state.DeliveredSticks >= state.RequiredSticks &&
                    state.DeliveredBoards >= state.RequiredBoards &&
                    state.DeliveredRope >= state.RequiredRope &&
                    state.DeliveredLeaves >= state.RequiredLeaves)
                    state.WorkDone = state.WorkRequired;
            }
        }

        private static void RefreshBuildability(WorldState world)
        {
            var pieces = FreePieces(world).ToArray();
            var supports = pieces
                .Where(piece => piece.DefinitionId == "architecture.support.wood" &&
                                piece.ArchitectureElements[0].Complete)
                .Select(piece => piece.ArchitectureElements[0].SlotKey)
                .Select(slot => TryDecode("architecture.support.wood", slot, out var element)
                    ? element.Node
                    : default)
                .ToHashSet();
            foreach (var piece in pieces)
            {
                var state = piece.ArchitectureElements[0];
                if (state.DefinitionId != "architecture.roof.palm")
                {
                    state.Buildable = true;
                    continue;
                }
                if (!TryDecode(state.DefinitionId, state.SlotKey, out var roof))
                {
                    state.Buildable = false;
                    continue;
                }
                state.Buildable = BlueprintGeometry.RoofSupports(roof.RoofSector)
                    .All(supports.Contains);
            }
        }

        private static void RepairTopology(WorldState world)
        {
            var pieces = FreePieces(world).ToArray();
            var index = new Dictionary<JunctionKey, JunctionId>();
            foreach (var junction in world.Junctions.Items.Values)
            {
                var point = junction.WorldPosition;
                var key = new JunctionKey(
                    (int)MathF.Round(point.X / (HexSpatialMath.Sqrt3 * 0.1875f)),
                    (int)MathF.Round(point.Y / 0.1875f));
                index[key] = junction.Id;
            }

            var changed = false;
            foreach (var piece in pieces)
            {
                changed |= WorldObjectMutations.ReleaseOwnedBlocking(world, piece);
                if (piece.DefinitionId != DoorTopology.DoorDefinitionId) continue;
                foreach (var id in piece.Junctions)
                {
                    if (!world.Junctions.Items.TryGetValue(id, out var junction) || !junction.Door) continue;
                    junction.Door = false;
                    changed = true;
                }
            }

            var portals = new HashSet<JunctionId>();
            foreach (var piece in pieces.Where(piece => piece.DefinitionId == DoorTopology.DoorDefinitionId))
            {
                var state = piece.ArchitectureElements[0];
                if (!TryDecode(state.DefinitionId, state.SlotKey, out var door) ||
                    !BlueprintGeometry.TryDoorPortal(door.Segment, out var portalKey) ||
                    !index.TryGetValue(portalKey, out var portalId)) continue;
                piece.Junctions.Clear();
                piece.Junctions.Add(portalId);
                portals.Add(portalId);
                var portal = world.Junctions.Items[portalId];
                WorldObjectMutations.ClearBlockingOwnershipAt(world, portalId);
                portal.Door = state.DeliveredTotal > 0;
                changed = true;
            }

            foreach (var piece in pieces)
            {
                var state = piece.ArchitectureElements[0];
                if (Array.IndexOf(EnvelopeIds, state.DefinitionId) < 0 ||
                    state.DeliveredTotal <= 0 ||
                    !TryDecode(state.DefinitionId, state.SlotKey, out var envelope)) continue;
                foreach (var key in BlueprintGeometry.SegmentJunctions(envelope.Segment))
                {
                    if (!index.TryGetValue(key, out var id) || portals.Contains(id) ||
                        !world.Junctions.Items.TryGetValue(id, out var junction)) continue;
                    if (!piece.BlockedJunctions.Contains(id)) piece.BlockedJunctions.Add(id);
                    if (!junction.Blocked)
                    {
                        junction.Blocked = true;
                        changed = true;
                    }
                }
            }
            if (changed) world.TopologyVersion++;
        }

        private static void RefreshTileFlags(
            WorldState world, IEnumerable<TileCoord> affectedTiles)
        {
            var pieces = FreePieces(world).ToArray();
            var touched = affectedTiles.Distinct().ToArray();
            var architecturalOwners = world.Entities.Objects.Values.Where(owner =>
                !IsFreePiece(owner) &&
                (BuildSiteMath.IsArchitecturalBuilding(owner.BuildProduct) ||
                 BuildingRules.IsCompletedBuilding(owner))).ToArray();
            foreach (var tileCoord in touched)
            {
                if (!world.Tiles.Items.TryGetValue(tileCoord, out var tile)) continue;
                var completedFloors = pieces.Count(piece =>
                    piece.DefinitionId == "architecture.floor.board" && piece.Tile == tileCoord &&
                    piece.ArchitectureElements[0].Complete);
                var completedRoofs = pieces.Count(piece =>
                    piece.DefinitionId == "architecture.roof.palm" && piece.Tile == tileCoord &&
                    piece.ArchitectureElements[0].Complete);
                var planFloor = architecturalOwners.Any(owner =>
                    Bootstrap.BuildingBootstrap.FootprintTiles(world, owner).Contains(tileCoord) &&
                    BuildingRules.FloorComplete(world, owner));
                var completedPlan = architecturalOwners.Any(owner =>
                    BuildingRules.IsCompletedBuilding(owner) &&
                    Bootstrap.BuildingBootstrap.FootprintTiles(world, owner).Contains(tileCoord));
                SetFlag(tile, TileFlags.HasFloor, completedFloors >= 6 || planFloor);
                SetFlag(tile, TileFlags.Roofed, completedRoofs >= 6 || completedPlan);
            }
        }

        private static void SetFlag(Tile tile, TileFlags flag, bool enabled)
        {
            if (enabled) tile.Flags |= flag;
            else tile.Flags &= ~flag;
        }

        private static int Count(WorldObjectState piece, string definitionId) =>
            piece.Contents.Count(item => item.DefinitionId == definitionId);

        private static BlueprintElementKind? KindForDefinition(string definitionId) => definitionId switch
        {
            "architecture.support.wood" => BlueprintElementKind.Support,
            "architecture.floor.board" => BlueprintElementKind.FloorSector,
            "architecture.wall.wood" => BlueprintElementKind.Wall,
            "architecture.window.wood" => BlueprintElementKind.Window,
            "architecture.door.wood" => BlueprintElementKind.Door,
            "architecture.roof.palm" => BlueprintElementKind.RoofSector,
            _ => null
        };

        private static string DefinitionForKind(BlueprintElementKind kind) => kind switch
        {
            BlueprintElementKind.Support => "architecture.support.wood",
            BlueprintElementKind.FloorSector => "architecture.floor.board",
            BlueprintElementKind.Window => "architecture.window.wood",
            BlueprintElementKind.Door => "architecture.door.wood",
            BlueprintElementKind.RoofSector => "architecture.roof.palm",
            _ => "architecture.wall.wood"
        };

        private static bool TryNode(ReadOnlySpan<char> value, out HexBuildNodeKey node)
        {
            node = default;
            var comma = value.IndexOf(',');
            return comma > 0 && int.TryParse(value[..comma], out var q) &&
                   int.TryParse(value[(comma + 1)..], out var r) &&
                   SetNode(q, r, out node);
        }

        private static bool SetNode(int q, int r, out HexBuildNodeKey node)
        {
            node = new HexBuildNodeKey(q, r);
            return true;
        }

        private static bool TrySegment(ReadOnlySpan<char> value, out BuildSegmentKey segment)
        {
            segment = default;
            var separator = value.IndexOf('>');
            if (separator <= 0 || !TryNode(value[..separator], out var a) ||
                !TryNode(value[(separator + 1)..], out var b)) return false;
            segment = new BuildSegmentKey(a, b);
            return BlueprintGeometry.IsUnitSegment(segment);
        }

        private static bool TrySector(
            ReadOnlySpan<char> value, out TileCoord hex, out int sector)
        {
            hex = default;
            sector = 0;
            var colon = value.IndexOf(':');
            var comma = value.IndexOf(',');
            if (comma <= 0 || colon <= comma ||
                !int.TryParse(value[..comma], out var q) ||
                !int.TryParse(value[(comma + 1)..colon], out var r) ||
                !int.TryParse(value[(colon + 1)..], out sector) || sector < 0 || sector > 5)
                return false;
            hex = new TileCoord(q, r);
            return true;
        }
    }
}
