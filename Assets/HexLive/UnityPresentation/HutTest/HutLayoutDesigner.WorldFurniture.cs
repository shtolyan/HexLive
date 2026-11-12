#nullable enable
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Localization;
using UnityEngine;

namespace HexLive.UnityPresentation.HutTest
{
    /// <summary>
    /// §120.13: мебель в режиме стройки живого мира. Вкладка «Мебель» мирового
    /// редактора работает с настоящими объектами мира: площадки и построенная
    /// мебель проецируются в локальный черновик, а каждый жест (поставить,
    /// перетащить, повернуть, снять) уходит одной из трёх команд симуляции.
    ///
    /// Правило одно для всего каталога — точка навигационной сетки лагеря,
    /// авторский след, шесть поворотов; пол отличается только высотой.
    /// </summary>
    public sealed partial class HutLayoutDesigner
    {
        private const float WorldSyncInterval = 0.3f;
        private const float PendingFurnitureSeconds = 3f;

        private WorldSnapshot? _worldSnapshot;
        private float _nextWorldSync;
        private List<JunctionSnapshot>? _indexedJunctions;
        private readonly Dictionary<JunctionKey, JunctionSnapshot> _junctionsByKey = new();
        private readonly Dictionary<int, JunctionSnapshot> _junctionsById = new();
        private readonly Dictionary<TileCoord, TileSnapshot> _worldTiles = new();
        private readonly HashSet<TileCoord> _campTiles = new();
        // id предмета черновика → ObjectId мира; «ждёт подтверждения сервером»;
        // «это ещё площадка, а не готовый предмет».
        private readonly Dictionary<string, int> _furnitureObjects = new();
        private readonly Dictionary<string, float> _pendingFurniture = new();
        private readonly HashSet<string> _siteFurniture = new();
        private readonly Dictionary<string, int> _modernStock = new();
        private int _ghostYawStep;
        private int _gridSignature;

        private static readonly HashSet<int> HiddenWorldObjects = new();

        /// <summary>Живой вид этого объекта временно подменён превью режима
        /// стройки (площадка показана готовой, предмет тащат или его новое
        /// место ещё не подтвердил сервер).</summary>
        public static bool IsWorldObjectHidden(int objectId) =>
            DirectWorldEditorOpen && HiddenWorldObjects.Contains(objectId);

        /// <summary>Закрыть мировой режим стройки (клавиша B, кнопка HUD).</summary>
        public static void CloseWorldEditor()
        {
            foreach (var designer in FindObjectsByType<HutLayoutDesigner>(FindObjectsSortMode.None))
                if (designer.WorldMode) designer.CloseWorldMode();
        }

        private bool WorldFurniture => DirectWorldConstruction;

        private void ConfigureWorldPreview()
        {
            if (!WorldFurniture || _preview == null) return;
            _preview.SurfaceHeight = SurfaceLocalY;
            _preview.WorldFurnitureGrid = true;
            // Перетаскиваемый предмет превью рисует как «изменённый» (ghost).
            _preview.FurnitureVisible = item =>
                _siteFurniture.Contains(item.Id) || _pendingFurniture.ContainsKey(item.Id) ||
                !_furnitureObjects.ContainsKey(item.Id) || item.Id == _rotationPreviewId;
        }

        private void ResetWorldFurniture()
        {
            _furnitureObjects.Clear();
            _pendingFurniture.Clear();
            _siteFurniture.Clear();
            HiddenWorldObjects.Clear();
            _gridSignature = 0;
            _nextWorldSync = 0f;
        }

        // ─────────────────────────────────────────────────────────────────
        // Мир → черновик.
        // ─────────────────────────────────────────────────────────────────

        private void SyncWorld(bool force = false)
        {
            if (!WorldFurniture || _runner == null) return;
            if (!force && Time.unscaledTime < _nextWorldSync) return;
            _nextWorldSync = Time.unscaledTime + WorldSyncInterval;
            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null) return;
            _worldSnapshot = snapshot;
            IndexTopology(snapshot);
            RefreshCamp(snapshot);
            var stockChanged = RefreshModernStock(snapshot);
            // Под рукой игрока черновик не трогаем: жест закончится — догоним.
            if (_gestureActive || _rotationPreview != null) return;
            var changed = ReconcileFurniture(snapshot);
            RefreshWorldGrid();
            if (stockChanged) RebuildCatalogUi();
            if (!changed) return;
            RebuildPreview();
            RefreshUiState();
        }

        private void IndexTopology(WorldSnapshot snapshot)
        {
            _worldTiles.Clear();
            foreach (var tile in snapshot.Tiles) _worldTiles[tile.Coord] = tile;
            // Записи топологии переиспользуются, их флаги обновляются на месте:
            // индекс строится один раз на топологию, не на каждый снапшот.
            if (ReferenceEquals(_indexedJunctions, snapshot.Junctions) && _junctionsById.Count > 0) return;
            _indexedJunctions = snapshot.Junctions;
            _junctionsByKey.Clear();
            _junctionsById.Clear();
            foreach (var junction in snapshot.Junctions)
            {
                _junctionsById[junction.Id.Value] = junction;
                var key = FurniturePlacement.KeyOf(junction.WorldPosition);
                if (!_junctionsByKey.TryGetValue(key, out var existing) || junction.Id.Value < existing.Id.Value)
                    _junctionsByKey[key] = junction;
            }
        }

        private void RefreshCamp(WorldSnapshot snapshot)
        {
            _campTiles.Clear();
            var faction = PlayerCampView.Of(_runner, snapshot);
            var home = snapshot.CampHomes.FirstOrDefault(candidate => candidate.Faction == faction);
            if (home == null) return;
            var radius = Spec72.MaxCampRadiusTiles;
            for (var dq = -radius; dq <= radius; dq++)
            for (var dr = Mathf.Max(-radius, -dq - radius); dr <= Mathf.Min(radius, -dq + radius); dr++)
            {
                var tile = new TileCoord(home.Tile.Q + dq, home.Tile.R + dr);
                if (_worldTiles.TryGetValue(tile, out var ground) && ground.Walkable && !ground.Water &&
                    !PortalWorld.IsApartmentTile(tile))
                    _campTiles.Add(tile);
            }
        }

        private bool RefreshModernStock(WorldSnapshot snapshot)
        {
            var changed = false;
            var faction = PlayerCampView.Of(_runner, snapshot);
            foreach (var entry in BuildCatalogDefinition.ForCategory(
                         BuildCatalogMode.Furniture, BuildCatalogCategories.Modern))
            {
                var count = PortalWorld.AvailableFurnitureCount(snapshot, faction, entry.DefinitionId);
                if (ModernStock(entry.DefinitionId) == count) continue;
                _modernStock[entry.DefinitionId] = count;
                changed = true;
            }

            if (changed && _activeCatalogEntry != null &&
                PortalWorld.IsFurniture(_activeCatalogEntry.DefinitionId) &&
                ModernStock(_activeCatalogEntry.DefinitionId) <= 0)
            {
                _activeCatalogEntry = null;
                _tool = Tool.None;
            }

            return changed;
        }

        private int ModernStock(string definitionId) =>
            _modernStock.TryGetValue(definitionId, out var count) ? count : 0;

        /// <summary>Сверить мебель черновика с миром. Мир — истина; исключение —
        /// предмет, чью команду сервер ещё не подтвердил (он «ждёт»).</summary>
        private bool ReconcileFurniture(WorldSnapshot snapshot)
        {
            var now = Time.unscaledTime;
            var changed = false;
            var alive = new HashSet<int>();
            foreach (var obj in snapshot.Objects) alive.Add(obj.Id.Value);
            // Площадка, ставшая готовым предметом, — НОВЫЙ объект мира на той же
            // точке: отвязать старый id, чтобы предмет черновика подхватил новый.
            foreach (var pair in _furnitureObjects.ToArray())
                if (!alive.Contains(pair.Value)) _furnitureObjects.Remove(pair.Key);

            var byObject = new Dictionary<int, string>();
            foreach (var pair in _furnitureObjects) byObject[pair.Value] = pair.Key;
            var items = _draft.Furniture.ToDictionary(item => item.Id);
            var appeared = new List<FurniturePlacementData>();
            var vanished = new HashSet<string>();
            HiddenWorldObjects.Clear();
            _siteFurniture.Clear();

            foreach (var obj in snapshot.Objects)
            {
                var catalog = FurniturePlacement.CatalogIdOf(obj);
                if (catalog == null || obj.Junctions.Count == 0 ||
                    !_junctionsById.TryGetValue(obj.Junctions[0].Value, out var anchor)) continue;
                var key = FurniturePlacement.KeyOf(anchor.WorldPosition);
                if (!TryPlacement(key, out var tile, out var slot) || PortalWorld.IsApartmentTile(tile)) continue;
                var yaw = FurniturePlacement.YawStepOf(obj.RotationDegrees);

                FurniturePlacementData? item = null;
                if (byObject.TryGetValue(obj.Id.Value, out var itemId)) items.TryGetValue(itemId, out item);
                if (item == null)
                {
                    item = _draft.Furniture.FirstOrDefault(candidate =>
                        !_furnitureObjects.ContainsKey(candidate.Id) &&
                        candidate.DefinitionId == catalog && candidate.PrimaryJunction.Equals(key));
                    if (item == null)
                    {
                        item = new FurniturePlacementData
                        {
                            Id = _draft.AllocateFurnitureId(), DefinitionId = catalog,
                            TileQ = tile.Q, TileR = tile.R, JunctionSlot = slot, YawStep = yaw
                        };
                        _draft.Furniture.Add(item);
                        items[item.Id] = item;
                        appeared.Add(item.Clone());
                        changed = true;
                    }

                    _furnitureObjects[item.Id] = obj.Id.Value;
                }

                var matches = item.DefinitionId == catalog && item.TileQ == tile.Q && item.TileR == tile.R &&
                              item.JunctionSlot == slot && item.YawStep == yaw;
                if (_pendingFurniture.TryGetValue(item.Id, out var deadline) && (matches || now >= deadline))
                {
                    _pendingFurniture.Remove(item.Id);
                    changed = true;
                }

                if (!matches && !_pendingFurniture.ContainsKey(item.Id))
                {
                    item.DefinitionId = catalog;
                    item.TileQ = tile.Q;
                    item.TileR = tile.R;
                    item.JunctionSlot = slot;
                    item.YawStep = yaw;
                    changed = true;
                }

                var site = obj.DefinitionId == ContentIds.BuildSite;
                if (site) _siteFurniture.Add(item.Id);
                if (site || _pendingFurniture.ContainsKey(item.Id)) HiddenWorldObjects.Add(obj.Id.Value);
            }

            // Предмет черновика без объекта мира: либо ещё едет (ждём), либо его
            // больше нет — отклонён, достроен в другое, унесён в рюкзаке.
            foreach (var item in _draft.Furniture.ToArray())
            {
                if (_furnitureObjects.ContainsKey(item.Id)) continue;
                if (_pendingFurniture.TryGetValue(item.Id, out var deadline) && now < deadline) continue;
                _pendingFurniture.Remove(item.Id);
                _draft.Furniture.Remove(item);
                vanished.Add(item.Id);
                if (_selectedId == item.Id)
                {
                    _selectedId = string.Empty;
                    _selectedIds.Remove(item.Id);
                }

                changed = true;
            }

            if (appeared.Count > 0 || vanished.Count > 0)
            {
                _history.Rewrite(step =>
                {
                    step.Furniture.RemoveAll(item => vanished.Contains(item.Id));
                    foreach (var item in appeared)
                        if (step.Furniture.All(existing => existing.Id != item.Id))
                            step.Furniture.Add(item.Clone());
                    step.Normalize();
                });
            }

            if (changed) _draft.Normalize();
            return changed;
        }

        private static bool TryPlacement(JunctionKey key, out TileCoord tile, out int slot) =>
            BlueprintEditorCommands.TryInteriorSlot(key, out tile, out slot);

        // ─────────────────────────────────────────────────────────────────
        // Черновик → мир.
        // ─────────────────────────────────────────────────────────────────

        private void DispatchFurnitureDelta(BuildingBlueprintDraft before, BuildingBlueprintDraft after)
        {
            if (!WorldFurniture || _runner == null) return;
            var old = before.Furniture.ToDictionary(item => item.Id);
            var next = after.Furniture.ToDictionary(item => item.Id);
            var deadline = Time.unscaledTime + PendingFurnitureSeconds;
            foreach (var pair in old)
            {
                if (next.ContainsKey(pair.Key)) continue;
                var at = pair.Value.PrimaryJunction;
                _runner.EnqueueCommand(new RemoveFurnitureCommand(pair.Value.DefinitionId, at.XKey, at.YKey));
                _furnitureObjects.Remove(pair.Key);
                _pendingFurniture.Remove(pair.Key);
            }

            foreach (var pair in next)
            {
                var to = pair.Value.PrimaryJunction;
                if (!old.TryGetValue(pair.Key, out var previous))
                {
                    _runner.EnqueueCommand(new PlaceFurnitureCommand(
                        pair.Value.DefinitionId, to.XKey, to.YKey, pair.Value.YawStep));
                    _pendingFurniture[pair.Key] = deadline;
                    continue;
                }

                if (previous.TileQ == pair.Value.TileQ && previous.TileR == pair.Value.TileR &&
                    previous.JunctionSlot == pair.Value.JunctionSlot && previous.YawStep == pair.Value.YawStep)
                    continue;
                var from = previous.PrimaryJunction;
                _runner.EnqueueCommand(new MoveFurnitureCommand(
                    pair.Value.DefinitionId, from.XKey, from.YKey, to.XKey, to.YKey, pair.Value.YawStep));
                _pendingFurniture[pair.Key] = deadline;
                if (_furnitureObjects.TryGetValue(pair.Key, out var objectId)) HiddenWorldObjects.Add(objectId);
            }

            _nextWorldSync = Time.unscaledTime + 0.05f;
        }

        /// <summary>Пока предмет тащат или крутят, его живой вид спрятан: игрок
        /// видит один предмет — тот, что под курсором.</summary>
        private void UpdateGestureHiding()
        {
            if (!WorldFurniture) return;
            // Живой вид прячется, только когда призрак уже уехал с места:
            // простой клик-выбор не должен мигать предметом.
            if (_dragMoved && !string.IsNullOrEmpty(_dragFurnitureId) &&
                _furnitureObjects.TryGetValue(_dragFurnitureId, out var dragged))
                HiddenWorldObjects.Add(dragged);
            if (!string.IsNullOrEmpty(_rotationPreviewId) &&
                _furnitureObjects.TryGetValue(_rotationPreviewId, out var rotated))
                HiddenWorldObjects.Add(rotated);
        }

        private bool IsFinishedWorldFurniture(string itemId) =>
            WorldFurniture && _furnitureObjects.ContainsKey(itemId) && !_siteFurniture.Contains(itemId);

        // ─────────────────────────────────────────────────────────────────
        // Клиентская оценка места (истину решает FurniturePlacement в симе).
        // ─────────────────────────────────────────────────────────────────

        private static HashSet<JunctionKey> WallKeys(
            BuildingBlueprintDraft draft, out HashSet<JunctionKey> portals)
        {
            var walls = new HashSet<JunctionKey>();
            portals = new HashSet<JunctionKey>();
            foreach (var element in draft.Elements)
            {
                if (element.Kind is not (BlueprintElementKind.Wall or BlueprintElementKind.Window or
                    BlueprintElementKind.Door)) continue;
                foreach (var key in BlueprintGeometry.SegmentJunctions(element.Segment)) walls.Add(key);
                if (element.Kind == BlueprintElementKind.Door &&
                    BlueprintGeometry.TryDoorPortal(element.Segment, out var portal))
                    portals.Add(portal);
            }

            return walls;
        }

        private bool CanEditWorldTile(TileCoord tile) =>
            _worldPlacement?.CanEditTile == null || _worldPlacement.CanEditTile(tile);

        private bool PointUsable(JunctionKey key, HashSet<JunctionKey> walls, HashSet<JunctionKey> portals)
        {
            if (!_junctionsByKey.TryGetValue(key, out var junction) || junction.IsSwimmable ||
                portals.Contains(key)) return false;
            var dry = false;
            foreach (var tile in junction.Tiles)
            {
                if (!CanEditWorldTile(tile)) return false;
                dry |= _worldTiles.TryGetValue(tile, out var ground) &&
                       ground.Walkable && !ground.Water && !ground.Blocked;
            }

            return dry && (!junction.Blocked || walls.Contains(key));
        }

        private float OwnBlockingRadius(string catalogId)
        {
            var radius = 0f;
            foreach (var local in BlueprintFurnitureFootprints.LocalOffsets(catalogId))
            {
                var point = BlueprintGeometry.JunctionToWorld(local);
                radius = Mathf.Max(radius, Mathf.Sqrt(point.X * point.X + point.Y * point.Y));
            }

            var product = catalogId == ContentIds.FurnitureHearth ? ContentIds.Campfire : catalogId;
            if (_runner != null && _runner.TryGetObjectDefinition(product, out var definition) &&
                definition != null)
                radius = Mathf.Max(radius, definition.ObstacleRadius);
            return radius + 0.02f;
        }

        /// <summary>Точки следа изменённой мебели, на которые в мире встать
        /// нельзя: обрыв, вода, дерево, чужая мебель, дверной проём, чужой или
        /// не свой лагерь, кончившийся запас современной мебели.</summary>
        private JunctionKey[] WorldFurnitureConflicts(BuildingBlueprintDraft candidate)
        {
            if (!WorldFurniture || _worldSnapshot == null) return System.Array.Empty<JunctionKey>();
            var committed = _draft.Furniture.ToDictionary(item => item.Id);
            HashSet<JunctionKey>? walls = null;
            HashSet<JunctionKey>? portals = null;
            var conflicts = new List<JunctionKey>();
            foreach (var item in candidate.Furniture)
            {
                committed.TryGetValue(item.Id, out var old);
                if (old != null && old.TileQ == item.TileQ && old.TileR == item.TileR &&
                    old.JunctionSlot == item.JunctionSlot && old.YawStep == item.YawStep) continue;
                walls ??= WallKeys(candidate, out portals);
                var anchorKey = item.PrimaryJunction;
                if (old == null && PortalWorld.IsFurniture(item.DefinitionId) &&
                    ModernStock(item.DefinitionId) <= 0)
                    conflicts.Add(anchorKey);
                if (_campTiles.Count > 0 && !_campTiles.Contains(item.Tile)) conflicts.Add(anchorKey);

                // Чужой след: площадки не блокируют навигацию, пока строятся,
                // поэтому пересечение с ними видно только по черновику.
                var others = new HashSet<JunctionKey>();
                foreach (var other in candidate.Furniture)
                    if (other.Id != item.Id)
                        foreach (var key in BlueprintFurnitureFootprints.OccupiedJunctions(other))
                            others.Add(key);
                foreach (var key in BlueprintFurnitureFootprints.OccupiedJunctions(item))
                    if (others.Contains(key)) conflicts.Add(key);

                // Собственная блокировка готового предмета не мешает ему же.
                Float2? ownCentre = old != null && IsFinishedWorldFurniture(item.Id)
                    ? BlueprintGeometry.JunctionToWorld(old.PrimaryJunction)
                    : null;
                var ownRadius = ownCentre != null ? OwnBlockingRadius(item.DefinitionId) : 0f;
                foreach (var key in BlueprintFurnitureFootprints.OccupiedJunctions(item))
                {
                    if (PointUsable(key, walls, portals!)) continue;
                    if (ownCentre is { } centre && _junctionsByKey.TryGetValue(key, out var own) &&
                        own.Blocked && !own.IsSwimmable && !portals!.Contains(key))
                    {
                        var point = BlueprintGeometry.JunctionToWorld(key);
                        var dx = point.X - centre.X;
                        var dy = point.Y - centre.Y;
                        if (dx * dx + dy * dy <= ownRadius * ownRadius) continue;
                    }

                    conflicts.Add(key);
                }
            }

            return conflicts.ToArray();
        }

        // ─────────────────────────────────────────────────────────────────
        // Сетка лагеря и высота поверхности.
        // ─────────────────────────────────────────────────────────────────

        private IEnumerable<TileCoord> WorldGridTiles()
        {
            if (_campTiles.Count > 0) return _campTiles;
            // Лагеря нет (свободный мир) — сетка вокруг опорного гекса.
            var result = new List<TileCoord>();
            var origin = _worldPlacement?.AnchorTile ?? default;
            for (var dq = -3; dq <= 3; dq++)
            for (var dr = Mathf.Max(-3, -dq - 3); dr <= Mathf.Min(3, -dq + 3); dr++)
            {
                var tile = new TileCoord(origin.Q + dq, origin.R + dr);
                if (_worldTiles.TryGetValue(tile, out var ground) && ground.Walkable && !ground.Water)
                    result.Add(tile);
            }

            return result;
        }

        /// <summary>Высота поверхности под мировой точкой в координатах превью:
        /// земля своего гекса, а на готовом настиле — выше на высоту пола. Это
        /// единственное отличие «на полу» от «на улице».</summary>
        private float SurfaceLocalY(Float2 point)
        {
            var tile = HexSpatialMath.WorldToTile(point);
            var ground = _worldPlacement?.GroundOffset != null ? _worldPlacement.GroundOffset(tile) : 0f;
            var floor = _worldTiles.TryGetValue(tile, out var snapshot) && snapshot.HasFloor;
            return _worldPlacement?.GroundOffset == null
                ? HutAssembly.FloorSurfaceLift
                : ground + (floor ? HutAssembly.FloorSurfaceLift : 0f);
        }

        private void RefreshWorldGrid()
        {
            if (!WorldFurniture || _preview == null || _catalogMode != BuildCatalogMode.Furniture) return;
            var walls = WallKeys(_draft, out var portals);
            var free = new List<Vector3>();
            var blocked = new List<Vector3>();
            var drawn = new HashSet<JunctionKey>();
            var signature = 17;
            foreach (var tile in WorldGridTiles())
            foreach (var template in HexPointLayout.GetInteriorTemplates()
                         .Concat(HexPointLayout.GetBoundaryTemplates()))
            {
                var pair = HexPointLayout.GetJunctionKeyPair(tile, template.SubAxial);
                var key = new JunctionKey(pair.xKey, pair.yKey);
                if (!drawn.Add(key) || !_junctionsByKey.ContainsKey(key)) continue;
                var usable = PointUsable(key, walls, portals);
                var point = BlueprintGeometry.JunctionToWorld(key);
                var y = SurfaceLocalY(point) + 0.03f;
                (usable ? free : blocked).Add(new Vector3(point.X, y, point.Y));
                unchecked
                {
                    signature = signature * 31 + key.GetHashCode() * (usable ? 3 : 7) +
                                Mathf.RoundToInt(y * 1000f);
                }
            }

            if (signature == _gridSignature) return;
            _gridSignature = signature;
            _preview.SetWorldGrid(free, blocked);
        }

        /// <summary>Точка земли под курсором с учётом высоты КАЖДОГО гекса (и
        /// настила): одна общая плоскость на склоне уводила бы призрак в сторону
        /// от того места, куда показывает игрок.</summary>
        private bool TryWorldGroundPoint(Vector2 screen, out Vector3 local)
        {
            local = default;
            if (_camera == null || _preview == null || _worldTiles.Count == 0 ||
                _worldPlacement?.GroundOffset == null) return false;
            var ray = _camera.ScreenPointToRay(screen);
            if (Mathf.Abs(ray.direction.y) < 0.0001f) return false;
            var rootY = _preview.transform.position.y;
            var best = float.PositiveInfinity;
            var found = false;
            foreach (var pair in _worldTiles)
            {
                if (pair.Value.Water || !pair.Value.Walkable) continue;
                var topY = rootY + _worldPlacement.GroundOffset(pair.Key) +
                           (pair.Value.HasFloor ? HutAssembly.FloorSurfaceLift : 0f);
                var t = (topY - ray.origin.y) / ray.direction.y;
                if (t <= 0f || t >= best) continue;
                var hit = ray.origin + ray.direction * t;
                var centre = HexSpatialMath.TileToWorld(pair.Key);
                var dx = Mathf.Abs(hit.x - centre.X);
                var dz = Mathf.Abs(hit.z - centre.Y);
                if (dz > HexSpatialMath.HexRadius ||
                    HexSpatialMath.Sqrt3 * dx + dz > HexSpatialMath.Sqrt3 * HexSpatialMath.HexRadius) continue;
                best = t;
                local = _preview.transform.InverseTransformPoint(hit);
                found = true;
            }

            return found;
        }

        // ─────────────────────────────────────────────────────────────────
        // Каталог мирового режима.
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Современная мебель не строится, а берётся из запаса лагеря:
        /// карточка есть, только пока есть что ставить.</summary>
        private IEnumerable<BuildCatalogEntryDefinition> VisibleCatalogEntries()
        {
            foreach (var entry in BuildCatalogDefinition.ForMode(_catalogMode))
            {
                if (!PortalWorld.IsFurniture(entry.DefinitionId) ||
                    WorldFurniture && ModernStock(entry.DefinitionId) > 0)
                    yield return entry;
            }

            // Готовый дом — не «здание» и не «чертёж», а набор деталей, который
            // кладут в мир одним кликом. Только в живом мире.
            if (WorldFurniture && _catalogMode == BuildCatalogMode.Construction)
                yield return HouseStampEntry;
        }

        private static readonly BuildCatalogEntryDefinition HouseStampEntry = new(
            ContentIds.HutPlan, BuildCatalogMode.Construction, BuildCatalogCategories.House,
            BuildCatalogPlacementKind.HouseStamp,
            "blueprint.catalog.hut.name", "blueprint.catalog.hut.description", "⌂", 5);

        private bool TryVisibleEntry(string id, out BuildCatalogEntryDefinition entry)
        {
            foreach (var candidate in VisibleCatalogEntries())
            {
                if (candidate.DefinitionId != id) continue;
                entry = candidate;
                return true;
            }

            entry = null!;
            return false;
        }

        private string CardDescription(BuildCatalogEntryDefinition entry) =>
            PortalWorld.IsFurniture(entry.DefinitionId)
                ? Loc.Get(entry.DescriptionTerm) + " · " +
                  string.Format(Loc.Get("portal.furniture_count"), ModernStock(entry.DefinitionId))
                : Loc.Get(entry.DescriptionTerm);

        /// <summary>Карточка грядки показывает взрослое растение, а не семечко.</summary>
        private static string CardIconId(BuildCatalogEntryDefinition entry) =>
            PlantGrowthCatalog.TryGetBySeed(entry.DefinitionId, out var plant)
                ? plant.MatureDefinitionId
                : entry.DefinitionId;
    }
}
