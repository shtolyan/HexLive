#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §150: binds the procedural minimap to its compact, collapsible HUD card.
    /// The distant world map is deliberately NOT UI: HexWorldRenderer swaps
    /// its 3D renderers for world-space sprites while the same camera remains
    /// free to orbit and tilt. Map orders still enter the simulation queue.
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
        private SimulationInputAdapter? _input;
        private RtsCameraController? _cameraController;
        private Camera? _camera;
        private readonly TacticalMapFrame _frame = new();
        private readonly Dictionary<int, RememberedMarker> _rememberedMarkers = new();
        private readonly HashSet<int> _visibleMarkerIds = new();
        private readonly List<int> _staleMarkerIds = new();
        private int _lastTick = int.MinValue;
        private int _lastSeed = int.MinValue;
        private float _nextPresentationRefreshAt;
        private bool _miniCollapsed;

        public static bool PointerOverMap { get; private set; }

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

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

            var template = Resources.Load<VisualTreeAsset>(PanelResource);
            var sheet = Resources.Load<StyleSheet>(StyleResource);
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
                _frame.People.Clear();
                _frame.CameraFootprint.Clear();
                RefreshPeople(snapshot, runner);
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

            RefreshRememberedMarkers(snapshot);
            RefreshPeople(snapshot, runner);
            RefreshCameraFootprint();
            _miniMap.SetFrame(_frame);
        }

        private void RefreshRememberedMarkers(WorldSnapshot snapshot)
        {
            _visibleMarkerIds.Clear();
            for (var i = 0; i < snapshot.Objects.Count; i++)
            {
                var worldObject = snapshot.Objects[i];
                if (!_frame.VisibleTiles.Contains(worldObject.Tile) ||
                    !TacticalMapPalette.TryClassify(worldObject.DefinitionId, out var kind))
                {
                    continue;
                }

                var id = worldObject.Id.Value;
                _visibleMarkerIds.Add(id);
                _rememberedMarkers[id] = new RememberedMarker(worldObject.Tile, kind);
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

            // Aggregate identical tile/kind markers. A palm grove remains one
            // readable glyph instead of turning the small map into confetti.
            var emitted = new HashSet<MarkerTileKey>();
            foreach (var pair in _rememberedMarkers)
            {
                var marker = pair.Value;
                var key = new MarkerTileKey(marker.Tile, marker.Kind);
                if (!emitted.Add(key))
                {
                    continue;
                }

                _frame.Markers.Add(new TacticalMapMarker(
                    marker.Tile, marker.Kind, _frame.VisibleTiles.Contains(marker.Tile)));
            }
        }

        private void RefreshPeople(WorldSnapshot snapshot, SimulationRunnerBehaviour runner)
        {
            for (var i = 0; i < snapshot.Npcs.Count; i++)
            {
                var npc = snapshot.Npcs[i];
                var owned = runner.CanControlNpc(npc.Id);
                var visible = _worldRenderer != null &&
                    _worldRenderer.IsNpcPickable(npc.Id.Value, npc.Tile, owned);
                if (!visible)
                {
                    continue;
                }

                _frame.People.Add(new TacticalMapPerson(
                    npc.Position,
                    owned,
                    npc.IsHostileToColony,
                    NpcSelection.Contains(npc.Id.Value)));
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
            var corners = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(Screen.width, 0f),
                new Vector2(Screen.width, Screen.height),
                new Vector2(0f, Screen.height)
            };

            for (var i = 0; i < corners.Length; i++)
            {
                var ray = camera.ScreenPointToRay(corners[i]);
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
            public readonly TacticalMapMarkerKind Kind;

            public RememberedMarker(TileCoord tile, TacticalMapMarkerKind kind)
            {
                Tile = tile;
                Kind = kind;
            }
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
