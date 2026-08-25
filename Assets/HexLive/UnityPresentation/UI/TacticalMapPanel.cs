#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §150: binds one visibility-safe contact frame to the compact HUD map.
    /// Distant camera zoom continues to render and pick the real world.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TacticalMapPanel : MonoBehaviour
    {
        private const string PanelResource = "HexLive/UI/TacticalMapPanel";
        private const string StyleResource = "HexLive/UI/TacticalMapPanel";
        private const float PresentationRefreshSeconds = 0.25f;

        [SerializeField] private SimulationRunnerBehaviour? _runner;

        private UIDocument? _document;
        private VisualElement? _root;
        private VisualElement? _miniCard;
        private VisualElement? _miniExpandTab;
        private VisualElement? _miniCollapse;
        private TacticalMapView? _miniMap;
        private HexWorldRenderer? _worldRenderer;
        private NpcPortraitCache? _portraitCache;
        private SimulationInputAdapter? _input;
        private RtsCameraController? _cameraController;
        private Camera? _camera;
        private readonly TacticalMapFrame _frame = new();
        private readonly Dictionary<int, RememberedMarker> _rememberedMarkers = new();
        private readonly HashSet<int> _visibleMarkerIds = new();
        private readonly HashSet<JunctionId> _storedGarmentJunctions = new();
        private readonly HashSet<MarkerTileKey> _emittedMarkerKeys = new();
        private readonly HashSet<MarkerTileDefinitionKey> _emittedItemTypes = new();
        private readonly List<RememberedItemCandidate> _itemCandidates = new();
        private readonly List<int> _staleMarkerIds = new();
        private int _lastTick = int.MinValue;
        private int _lastSeed = int.MinValue;
        private float _nextPresentationRefreshAt;
        private bool _miniCollapsed;

        public static bool PointerOverMap { get; private set; }

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        public void SetPortraitCache(NpcPortraitCache portraitCache) =>
            _portraitCache = portraitCache;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => PointerOverMap = false;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "TacticalMapPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                // Above the world, below the character panel and history.
                settings.sortingOrder = 140;
                _document.panelSettings = settings;
            }

            BuildUi();
        }

        private void OnDisable()
        {
            PointerOverMap = false;
        }

        private void Update()
        {
            ResolveDependencies();
            RefreshMapFrame();
            RefreshPointerState();
        }

        private void BuildUi()
        {
            if (_document == null)
            {
                return;
            }

            var root = _document.rootVisualElement;
            root.Clear();
            root.pickingMode = PickingMode.Ignore;

            var template = HexLive.UnityPresentation.Content.AtomicResources.Load<VisualTreeAsset>(PanelResource);
            var sheet = HexLive.UnityPresentation.Content.AtomicResources.Load<StyleSheet>(StyleResource);
            if (template == null || sheet == null)
            {
                Debug.LogError("§150 Tactical map UXML/USS resources are missing.");
                return;
            }

            template.CloneTree(root);
            _root = root.Q<VisualElement>("tacticalMapRoot");
            _miniCard = root.Q<VisualElement>("miniMapCard");
            _miniExpandTab = root.Q<VisualElement>("miniMapExpandTab");
            _miniCollapse = root.Q<VisualElement>("miniMapCollapse");
            var miniHost = root.Q<VisualElement>("miniMapHost");
            if (_root == null || _miniCard == null || _miniExpandTab == null ||
                _miniCollapse == null || miniHost == null)
            {
                Debug.LogError("§150 Tactical map visual tree is incomplete.");
                return;
            }

            _miniMap = new TacticalMapView();
            _miniMap.AddToClassList("tactical-map-canvas");
            miniHost.Add(_miniMap);

            _miniMap.RegisterCallback<PointerUpEvent>(OnMapPointerUp);
            _miniCollapse.RegisterCallback<PointerDownEvent>(evt =>
            {
                SetMiniCollapsed(true);
                evt.StopPropagation();
            });
            _miniExpandTab.RegisterCallback<PointerDownEvent>(evt =>
            {
                SetMiniCollapsed(false);
                evt.StopPropagation();
            });
            _miniCard.RegisterCallback<PointerEnterEvent>(_ => PointerOverMap = true);
            _miniCard.RegisterCallback<PointerLeaveEvent>(_ => PointerOverMap = false);
            _miniExpandTab.RegisterCallback<PointerEnterEvent>(_ => PointerOverMap = true);
            _miniExpandTab.RegisterCallback<PointerLeaveEvent>(_ => PointerOverMap = false);
            ApplyMiniCollapsed();
        }

        private void ResolveDependencies()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
            }

            if (_portraitCache == null)
            {
                _portraitCache = FindAnyObjectByType<NpcPortraitCache>();
            }

            if (_camera == null)
            {
                _camera = Camera.main;
            }

            if (_input == null && _camera != null)
            {
                _input = _camera.GetComponent<SimulationInputAdapter>();
            }

            if (_cameraController == null && _camera != null)
            {
                _cameraController = _camera.GetComponent<RtsCameraController>();
            }

        }

        private void RefreshMapFrame()
        {
            var runner = _runner;
            if (runner == null || !runner.IsReady || _miniMap == null ||
                _worldRenderer == null || !_worldRenderer.PlayerVisibilityReady)
            {
                return;
            }

            var snapshot = runner.CreateSnapshot();
            if (snapshot == null)
            {
                return;
            }

            if (snapshot.Tick == _lastTick)
            {
                // Selection and the camera keep moving while the simulation is
                // paused. Repaint their state at 4 Hz rather than rebuilding
                // up to 8160 procedural hexes every display frame.
                if (Time.unscaledTime < _nextPresentationRefreshAt)
                {
                    return;
                }

                _nextPresentationRefreshAt = Time.unscaledTime + PresentationRefreshSeconds;
                _frame.Markers.Clear();
                _frame.DroppedItems.Clear();
                _frame.People.Clear();
                _frame.UnknownPeople.Clear();
                _frame.Mobs.Clear();
                _frame.CameraFootprint.Clear();
                EmitRememberedMarkers();
                RefreshPeople(snapshot, runner);
                RefreshMobs(snapshot);
                RefreshCameraFootprint();
                _miniMap.SetFrame(_frame);
                return;
            }

            _lastTick = snapshot.Tick;
            _nextPresentationRefreshAt = Time.unscaledTime + PresentationRefreshSeconds;
            if (_lastSeed != snapshot.Seed)
            {
                _lastSeed = snapshot.Seed;
                _rememberedMarkers.Clear();
            }

            _frame.Clear();
            for (var i = 0; i < snapshot.Tiles.Count; i++)
            {
                var tile = snapshot.Tiles[i];
                var visible = _worldRenderer.IsTileVisibleToPlayer(tile.Coord);
                _frame.AddTile(new TacticalMapTile(
                    tile.Coord, tile.Water, tile.Explored, visible, tile.Elevation));
            }

            RefreshRememberedMarkers(snapshot, runner);
            RefreshPeople(snapshot, runner);
            RefreshMobs(snapshot);
            RefreshCameraFootprint();
            _miniMap.SetFrame(_frame);
        }

        private void RefreshRememberedMarkers(
            WorldSnapshot snapshot, SimulationRunnerBehaviour runner)
        {
            // Garments sharing a rack/wardrobe junction are stored, not lying
            // on the ground. They stay visible in the furniture itself but do
            // not masquerade as a dropped-clothing pile on the minimap.
            _storedGarmentJunctions.Clear();
            for (var i = 0; i < snapshot.Objects.Count; i++)
            {
                var furniture = snapshot.Objects[i];
                if (furniture.Junctions.Count > 0 &&
                    (furniture.DefinitionId == "station.drying_rack" ||
                     furniture.DefinitionId == "furniture.wardrobe"))
                {
                    _storedGarmentJunctions.Add(furniture.Junctions[0]);
                }
            }

            _visibleMarkerIds.Clear();
            for (var i = 0; i < snapshot.Objects.Count; i++)
            {
                var worldObject = snapshot.Objects[i];
                var homeCamp = worldObject.DefinitionId == ContentIds.Campfire ||
                    worldObject.BuildProduct == ContentIds.Campfire;
                var visible = _frame.VisibleTiles.Contains(worldObject.Tile);
                if (!visible && !homeCamp)
                {
                    continue;
                }

                var id = worldObject.Id.Value;
                var portable = !homeCamp && IsPortable(runner, worldObject.DefinitionId);
                var garment = portable && IsClothing(runner, worldObject.DefinitionId);
                if (garment && worldObject.Junctions.Count > 0 &&
                    _storedGarmentJunctions.Contains(worldObject.Junctions[0]))
                {
                    // It was visibly picked up from the ground and hung up.
                    // Forget the old drop even if its former tile is fogged.
                    _visibleMarkerIds.Add(id);
                    _rememberedMarkers.Remove(id);
                    continue;
                }

                TacticalMapMarkerKind kind;
                if (homeCamp)
                {
                    kind = TacticalMapMarkerKind.Camp;
                }
                else if (worldObject.DefinitionId.StartsWith("tree.", StringComparison.Ordinal) ||
                         worldObject.DefinitionId.StartsWith("plant.", StringComparison.Ordinal))
                {
                    kind = TacticalMapMarkerKind.Palm;
                }
                else if (portable)
                {
                    kind = TacticalMapMarkerKind.Item;
                }
                else
                {
                    continue;
                }

                _visibleMarkerIds.Add(id);
                var lit = visible
                    ? worldObject.DefinitionId == ContentIds.Campfire &&
                      worldObject.ResourceAmount > 0f
                    : _rememberedMarkers.TryGetValue(id, out var previous) && previous.Lit;
                _rememberedMarkers[id] = new RememberedMarker(
                    worldObject.Tile,
                    _worldRenderer!.ObjectAnchorPosition(worldObject),
                    kind,
                    worldObject.DefinitionId,
                    Importance(runner, worldObject.DefinitionId),
                    lit,
                    homeCamp);
            }

            // A marker is disproved only by looking at its tile again and not
            // finding that object. Outside live perception it remains the last
            // honest observation rather than reading the authoritative frame.
            _staleMarkerIds.Clear();
            foreach (var pair in _rememberedMarkers)
            {
                if (_frame.VisibleTiles.Contains(pair.Value.Tile) &&
                    !_visibleMarkerIds.Contains(pair.Key))
                {
                    _staleMarkerIds.Add(pair.Key);
                }
            }

            for (var i = 0; i < _staleMarkerIds.Count; i++)
            {
                _rememberedMarkers.Remove(_staleMarkerIds[i]);
            }

            EmitRememberedMarkers();
        }

        private void EmitRememberedMarkers()
        {
            _frame.Markers.Clear();
            _frame.DroppedItems.Clear();

            // Aggregate palms/camps, then retain at most one icon per distinct
            // portable definition. Per tile: <=4 types stay exact; >4 becomes
            // the three most important definitions plus one stack glyph.
            _emittedMarkerKeys.Clear();
            _emittedItemTypes.Clear();
            _itemCandidates.Clear();
            foreach (var pair in _rememberedMarkers)
            {
                var marker = pair.Value;
                var live = _frame.VisibleTiles.Contains(marker.Tile);
                if (marker.Kind == TacticalMapMarkerKind.Item)
                {
                    var typeKey = new MarkerTileDefinitionKey(
                        marker.Tile, marker.DefinitionId);
                    if (_emittedItemTypes.Add(typeKey))
                    {
                        _itemCandidates.Add(new RememberedItemCandidate(
                            pair.Key, marker, live));
                    }
                    continue;
                }

                var key = new MarkerTileKey(marker.Tile, marker.Kind);
                if (!_emittedMarkerKeys.Add(key))
                {
                    continue;
                }

                _frame.Markers.Add(new TacticalMapMarker(
                    marker.Tile,
                    marker.Anchor,
                    marker.Kind,
                    live,
                    marker.Lit,
                    marker.AlwaysKnown));
            }

            _itemCandidates.Sort(CompareItemCandidates);
            var index = 0;
            while (index < _itemCandidates.Count)
            {
                var first = _itemCandidates[index];
                var end = index + 1;
                while (end < _itemCandidates.Count &&
                       _itemCandidates[end].Marker.Tile.Equals(first.Marker.Tile))
                {
                    end++;
                }

                var count = end - index;
                var exactCount = count > 4 ? 3 : count;
                for (var i = 0; i < exactCount; i++)
                {
                    var candidate = _itemCandidates[index + i];
                    var marker = candidate.Marker;
                    _frame.DroppedItems.Add(new TacticalMapDroppedItem(
                        marker.Tile,
                        candidate.ObjectId,
                        marker.DefinitionId,
                        ItemIcons.Load(marker.DefinitionId),
                        candidate.Live,
                        marker.Anchor,
                        marker.Importance));
                }

                if (count > 4)
                {
                    _frame.DroppedItems.Add(new TacticalMapDroppedItem(
                        first.Marker.Tile,
                        int.MaxValue,
                        string.Empty,
                        null,
                        first.Live,
                        first.Marker.Anchor,
                        0,
                        overflow: true));
                }

                index = end;
            }

            _frame.DroppedItems.Sort(CompareDroppedItems);
        }

        private static int CompareItemCandidates(
            RememberedItemCandidate left, RememberedItemCandidate right)
        {
            var q = left.Marker.Tile.Q.CompareTo(right.Marker.Tile.Q);
            if (q != 0) return q;
            var r = left.Marker.Tile.R.CompareTo(right.Marker.Tile.R);
            if (r != 0) return r;
            var importance = right.Marker.Importance.CompareTo(left.Marker.Importance);
            if (importance != 0) return importance;
            var definition = string.CompareOrdinal(
                left.Marker.DefinitionId, right.Marker.DefinitionId);
            return definition != 0 ? definition : left.ObjectId.CompareTo(right.ObjectId);
        }

        private static int CompareDroppedItems(
            TacticalMapDroppedItem left, TacticalMapDroppedItem right)
        {
            var q = left.Tile.Q.CompareTo(right.Tile.Q);
            if (q != 0) return q;
            var r = left.Tile.R.CompareTo(right.Tile.R);
            return r != 0 ? r : left.ObjectId.CompareTo(right.ObjectId);
        }

        private static bool IsClothing(
            SimulationRunnerBehaviour runner, string definitionId)
        {
            if (runner.TryGetObjectDefinition(definitionId, out var definition) &&
                definition != null)
            {
                var category = ItemCatalog.Classify(definition);
                return category == ItemCategory.Clothing || category == ItemCategory.Armor;
            }

            // Safe fallback for a frame received before the content table is
            // ready. Imported garments normally take the definition path.
            return definitionId.StartsWith("clothing.", StringComparison.Ordinal) ||
                definitionId.StartsWith("underwear.", StringComparison.Ordinal) ||
                definitionId.StartsWith("armor.", StringComparison.Ordinal);
        }

        private static bool IsPortable(
            SimulationRunnerBehaviour runner, string definitionId)
        {
            if (runner.TryGetObjectDefinition(definitionId, out var definition) &&
                definition != null)
            {
                if (definition.Layer.HasValue ||
                    ItemCatalog.Classify(definition) != ItemCategory.Misc)
                {
                    return true;
                }

                for (var i = 0; i < definition.Interactions.Count; i++)
                {
                    if (definition.Interactions[i].Type == InteractionType.PickUp)
                    {
                        return true;
                    }
                }
            }

            return definitionId.StartsWith("food.", StringComparison.Ordinal) ||
                definitionId.StartsWith("tool.", StringComparison.Ordinal) ||
                definitionId.StartsWith("item.", StringComparison.Ordinal) ||
                definitionId.StartsWith("resource.", StringComparison.Ordinal) ||
                definitionId.StartsWith("clothing.", StringComparison.Ordinal) ||
                definitionId.StartsWith("underwear.", StringComparison.Ordinal) ||
                definitionId.StartsWith("armor.", StringComparison.Ordinal);
        }

        private static int Importance(
            SimulationRunnerBehaviour runner, string definitionId) =>
            runner.TryGetObjectDefinition(definitionId, out var definition) &&
            definition != null
                ? ItemCatalog.Importance(definition)
                : ItemCatalog.ImportanceById(definitionId);

        private void RefreshPeople(WorldSnapshot snapshot, SimulationRunnerBehaviour runner)
        {
            for (var i = 0; i < snapshot.Npcs.Count; i++)
            {
                AddPerson(snapshot.Npcs[i], runner, dead: false);
            }

            for (var i = 0; i < snapshot.Corpses.Count; i++)
            {
                AddPerson(snapshot.Corpses[i], runner, dead: true);
            }
        }

        private void AddPerson(
            NpcSnapshot npc, SimulationRunnerBehaviour runner, bool dead)
        {
            var owned = runner.CanControlNpc(npc.Id);
            var visible = _worldRenderer != null &&
                _worldRenderer.IsNpcPickable(npc.Id.Value, npc.Tile, owned);
            if (!visible)
            {
                if (!dead && !owned && _worldRenderer != null &&
                    _worldRenderer.TryGetLastSeenNpcTile(npc.Id.Value, out var lastSeen))
                {
                    _frame.UnknownPeople.Add(new TacticalMapUnknownPerson(
                        npc.Id.Value, lastSeen));
                }
                return;
            }

            Texture2D? portrait = null;
            if (_portraitCache != null &&
                !_portraitCache.TryGet(npc.Id.Value, out portrait))
            {
                _portraitCache.RequestNow(npc.Id.Value);
            }

            _frame.People.Add(new TacticalMapPerson(
                npc.Id.Value,
                npc.Position,
                owned,
                npc.IsHostileToColony,
                NpcSelection.Contains(npc.Id.Value),
                portrait,
                dead));
        }

        private void RefreshMobs(WorldSnapshot snapshot)
        {
            for (var i = 0; i < snapshot.Mobs.Count; i++)
            {
                var mob = snapshot.Mobs[i];
                if (mob.Health <= 0f || !_frame.VisibleTiles.Contains(mob.Tile))
                {
                    continue;
                }

                _frame.Mobs.Add(new TacticalMapMob(
                    mob.Id, false, mob.Position, TacticalMapMob.Classify(mob.MobId)));
            }

            for (var i = 0; i < snapshot.Crabs.Count; i++)
            {
                var crab = snapshot.Crabs[i];
                if (_frame.VisibleTiles.Contains(crab.Tile))
                {
                    _frame.Mobs.Add(new TacticalMapMob(
                        crab.Id, true, crab.Position, TacticalMapMobKind.Crab));
                }
            }

            for (var i = 0; i < snapshot.Sharks.Count; i++)
            {
                var shark = snapshot.Sharks[i];
                if (_frame.VisibleTiles.Contains(shark.Tile))
                {
                    _frame.Mobs.Add(new TacticalMapMob(
                        -1, false, shark.Position, TacticalMapMobKind.Shark));
                }
            }
        }

        private void RefreshCameraFootprint()
        {
            var camera = _camera;
            if (camera == null)
            {
                return;
            }

            var ground = new Plane(Vector3.up, new Vector3(0f, SimulationUnityMapper.TileHeight, 0f));
            for (var i = 0; i < 4; i++)
            {
                var corner = i switch
                {
                    0 => new Vector2(0f, 0f),
                    1 => new Vector2(Screen.width, 0f),
                    2 => new Vector2(Screen.width, Screen.height),
                    _ => new Vector2(0f, Screen.height)
                };
                var ray = camera.ScreenPointToRay(corner);
                if (!ground.Raycast(ray, out var distance) || distance <= 0f)
                {
                    _frame.CameraFootprint.Clear();
                    return;
                }

                var hit = ray.GetPoint(distance);
                _frame.CameraFootprint.Add(new Float2(hit.x, hit.z));
            }
        }

        private void SetMiniCollapsed(bool collapsed)
        {
            _miniCollapsed = collapsed;
            PointerOverMap = true;
            ApplyMiniCollapsed();
        }

        private void ApplyMiniCollapsed()
        {
            _miniCard?.EnableInClassList("map-hidden", _miniCollapsed);
            _miniExpandTab?.EnableInClassList("map-hidden", !_miniCollapsed);
        }

        private void RefreshPointerState()
        {
            var mouse = Mouse.current;
            if (mouse == null || _root == null)
            {
                PointerOverMap = false;
                return;
            }

            var panelHeight = _root.layout.height;
            var active = _miniCollapsed ? _miniExpandTab : _miniCard;
            if (active == null || panelHeight < 1f)
            {
                PointerOverMap = false;
                return;
            }

            var scale = Screen.height / panelHeight;
            var pointer = mouse.position.ReadValue();
            var bounds = active.worldBound;
            var left = bounds.xMin * scale;
            var right = bounds.xMax * scale;
            var top = Screen.height - bounds.yMin * scale;
            var bottom = Screen.height - bounds.yMax * scale;
            PointerOverMap = pointer.x >= left && pointer.x <= right &&
                pointer.y >= bottom && pointer.y <= top;
        }

        private void OnMapPointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0 || evt.currentTarget is not TacticalMapView map)
            {
                return;
            }

            evt.StopPropagation();
            var local = new Vector2(evt.localPosition.x, evt.localPosition.y);
            if (map.TryMapToWorld(local, out var point))
            {
                _cameraController?.MoveToMapPoint(point);
            }
        }

        private readonly struct RememberedMarker
        {
            public readonly TileCoord Tile;
            public readonly Float2 Anchor;
            public readonly TacticalMapMarkerKind Kind;
            public readonly string DefinitionId;
            public readonly int Importance;
            public readonly bool Lit;
            public readonly bool AlwaysKnown;

            public RememberedMarker(
                TileCoord tile,
                Float2 anchor,
                TacticalMapMarkerKind kind,
                string definitionId,
                int importance,
                bool lit,
                bool alwaysKnown)
            {
                Tile = tile;
                Anchor = anchor;
                Kind = kind;
                DefinitionId = definitionId;
                Importance = importance;
                Lit = lit;
                AlwaysKnown = alwaysKnown;
            }
        }

        private readonly struct RememberedItemCandidate
        {
            public readonly int ObjectId;
            public readonly RememberedMarker Marker;
            public readonly bool Live;

            public RememberedItemCandidate(
                int objectId, RememberedMarker marker, bool live)
            {
                ObjectId = objectId;
                Marker = marker;
                Live = live;
            }
        }

        private readonly struct MarkerTileDefinitionKey : IEquatable<MarkerTileDefinitionKey>
        {
            private readonly TileCoord _tile;
            private readonly string _definitionId;

            public MarkerTileDefinitionKey(TileCoord tile, string definitionId)
            {
                _tile = tile;
                _definitionId = definitionId;
            }

            public bool Equals(MarkerTileDefinitionKey other) =>
                _tile.Equals(other._tile) &&
                string.Equals(_definitionId, other._definitionId, StringComparison.Ordinal);

            public override bool Equals(object? obj) =>
                obj is MarkerTileDefinitionKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_tile, _definitionId);
        }

        private readonly struct MarkerTileKey : IEquatable<MarkerTileKey>
        {
            private readonly TileCoord _tile;
            private readonly TacticalMapMarkerKind _kind;

            public MarkerTileKey(TileCoord tile, TacticalMapMarkerKind kind)
            {
                _tile = tile;
                _kind = kind;
            }

            public bool Equals(MarkerTileKey other) =>
                _tile.Equals(other._tile) && _kind == other._kind;

            public override bool Equals(object? obj) =>
                obj is MarkerTileKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_tile, (int)_kind);
        }
    }
}
