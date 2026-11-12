#nullable enable
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.HutTest;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §120.13: вход в режим строительства. Кнопка «Строить» и клавиша B
    /// открывают ОДИН редактор живого мира (<see cref="HutLayoutDesigner"/> в
    /// прямом мировом режиме) с двумя вкладками — «Мебель» и «Строительство».
    ///
    /// Раньше здесь жил второй лоток со своими правилами: мебель ставилась
    /// только в центр гекса, современная — только на готовый пол, дом — отдельной
    /// «площадкой по плану», свой дом — отдельным «проектом», а стены —
    /// третьим режимом «свободного строительства». Теперь сущностей «строение» и
    /// «чертёж» у игрока нет: любая стена, пол и предмет в своём лагере
    /// правятся на месте, одним способом.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed partial class BuildModePanel : MonoBehaviour
    {
        private const string StyleResource = "HexLive/UI/HutConstructor/HutConstructorStyles";

        /// <summary>Открыт ли режим стройки: колышки площадок прячутся (их
        /// подменяет полная модель превью), HUD подсвечивает кнопку.</summary>
        public static bool IsOpen => HutLayoutDesigner.DirectWorldEditorOpen;

        /// <summary>Мир готов принимать строительные команды.</summary>
        public static bool CanOpen { get; private set; }

        private static BuildModePanel? _instance;

        private SimulationRunnerBehaviour? _runner;
        private HexWorldRenderer? _worldRenderer;
        private UIDocument? _document;
        private Button? _openButton;

        private WorldSnapshot? _snapshot;
        private readonly HashSet<Faction> _buildFactions = new();

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        public void SetWorldRenderer(HexWorldRenderer renderer) => _worldRenderer = renderer;

        /// <summary>Кнопка HUD и клавиша B: открыть или закрыть режим.</summary>
        public static void Toggle()
        {
            var panel = _instance;
            if (panel == null) return;
            if (IsOpen) HutLayoutDesigner.CloseWorldEditor();
            else if (CanOpen) panel.Open();
        }

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            ConfigurePanelSettings();
            BuildUi();
            Loc.LanguageChanged += ApplyLanguage;
            _instance = this;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
            Loc.LanguageChanged -= ApplyLanguage;
        }

        private void ConfigurePanelSettings()
        {
            if (_document == null) return;
            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings == null) return;
            var settings = Instantiate(baseSettings);
            settings.name = "BuildModePanelSettings";
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.match = 1f;
            settings.sortingOrder = 172;
            _document.panelSettings = settings;
        }

        private void BuildUi()
        {
            if (_document == null) return;
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            // ⚠️ Корень — полноэкранный: pickable-обёртка на высоком sortingOrder
            // молча съедает клики по ростеру и панели персонажа.
            root.pickingMode = PickingMode.Ignore;
            var sheet = Resources.Load<StyleSheet>(StyleResource);
            if (sheet != null) root.styleSheets.Add(sheet);

            // Плавающая кнопка входа — видна, когда мир готов, а режим закрыт.
            _openButton = new Button(Open);
            _openButton.AddToClassList("header-button");
            _openButton.AddToClassList("accent");
            _openButton.style.position = Position.Absolute;
            _openButton.style.right = 18f;
            _openButton.style.bottom = 16f;
            _openButton.style.height = 40f;
            _openButton.style.minWidth = 108f;
            _openButton.style.fontSize = 13f;
            _openButton.style.display = DisplayStyle.None;
            root.Add(_openButton);
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            if (_openButton != null) _openButton.text = Loc.Get("build.mode.button");
        }

        private void Update()
        {
            var ready = _runner != null && _runner.IsReady && _runner.SupportsNpcCommands;
            CanOpen = ready && !HutLayoutDesigner.WorldEditorOpen;
            if (_openButton != null)
            {
                // §174.7: в веб-клиенте дверь в режим — кнопка «Строить» в HUD.
                _openButton.style.display = CanOpen && !HtmlUi.Active ? DisplayStyle.Flex : DisplayStyle.None;
                // Панель выбранного персонажа занимает весь низ — кнопка
                // отъезжает выше её карточки, чтобы не лечь на «Отношения».
                _openButton.style.bottom = NpcSelection.HasSelection ? 306f : 16f;
            }

            var keyboard = Keyboard.current;
            if (ready && keyboard != null && keyboard.bKey.wasPressedThisFrame) Toggle();
        }

        // ─────────────────────────────────────────────────────────────────
        // Права: чужой лагерь править нельзя (§120.12).
        // ─────────────────────────────────────────────────────────────────

        private void RefreshBuildPermissions()
        {
            _buildFactions.Clear();
            if (_snapshot == null || _runner == null) return;
            foreach (var npc in _snapshot.Npcs)
                if (npc.Health > 0 && npc.TravelLeaderNpcId == null && _runner.IsAssignedNpc(npc.Id))
                    _buildFactions.Add(npc.Faction);
        }

        private bool CanEditTile(TileCoord tile) => _snapshot != null &&
            ConstructionAccess.CanEditTile(_snapshot, _buildFactions, tile);

        private bool CanBuildDraft(BuildingBlueprintDraft draft, TileCoord tile, float rotation)
        {
            _snapshot = _runner?.CreateSnapshot();
            RefreshBuildPermissions();
            return ConstructionAccess.PlanTiles(draft, tile, rotation).All(CanEditTile);
        }

        // ─────────────────────────────────────────────────────────────────
        // Открытие редактора живого мира.
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Редактор открывается абсолютной проекцией живого мира. Гекс под
        /// центром экрана даёт только высоту плоскости курсора для вкладки
        /// «Строительство»; он не якорь и никуда не записывается.
        /// </summary>
        private void Open()
        {
            var runner = _runner;
            var worldRenderer = _worldRenderer;
            if (IsOpen || HutLayoutDesigner.WorldEditorOpen || runner == null || worldRenderer == null ||
                !runner.IsReady || !runner.SupportsNpcCommands) return;

            _snapshot = runner.CreateSnapshot();
            if (_snapshot == null) return;
            RefreshBuildPermissions();

            var reference = ReferenceTile(_snapshot);
            if (reference is not { } referenceTile) return;
            var elevation = worldRenderer.GroundTopY(referenceTile);
            // Sims-style: режим стройки — про мир, не про персонажа. Панель
            // выбранной колонистки закрывается, чтобы не просвечивать под лотком.
            NpcSelection.Clear();

            HutLayoutDesigner.PendingWorldPlacement = new HutLayoutDesigner.WorldPlacementConfig
            {
                AnchorWorldPosition = new Vector3(0f, elevation, 0f),
                AnchorTile = referenceTile,
                DirectWorldConstruction = true,
                ArchitectureRenderContext = BlueprintArchitectureRenderContext.World(_snapshot,
                    tile => worldRenderer.GroundTopY(tile) - elevation),
                InitialDraft = FreeWorldProjection(_snapshot),
                GroundOffset = tile => worldRenderer.GroundTopY(tile) - elevation,
                CanEditTile = CanEditTile,
                CanApplyDraft = (before, after) =>
                {
                    _snapshot = runner.CreateSnapshot();
                    RefreshBuildPermissions();
                    return ConstructionAccess.ChangedElements(before, after)
                        .SelectMany(ConstructionAccess.ElementTiles).All(CanEditTile);
                },
                OnApplyFreeArchitecture = (placements, removals) =>
                    runner.EnqueueCommand(new ApplyFreeArchitectureCommand(placements, removals)),
                OnClosed = null
            };

            var editorRoot = new GameObject("HexLive Build Mode");
            editorRoot.AddComponent<UIDocument>();
            editorRoot.AddComponent<HutLayoutDesigner>();
        }

        /// <summary>Гекс под центром экрана, иначе дом своего лагеря, иначе
        /// любой сухой гекс.</summary>
        private TileCoord? ReferenceTile(WorldSnapshot snapshot)
        {
            var camera = Camera.main;
            if (camera != null)
            {
                var ray = camera.ScreenPointToRay(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
                if (Mathf.Abs(ray.direction.y) > 0.0001f)
                {
                    var bestT = float.PositiveInfinity;
                    TileCoord? best = null;
                    foreach (var tile in snapshot.Tiles)
                    {
                        if (tile.Water || !tile.Walkable) continue;
                        var topY = _worldRenderer != null
                            ? _worldRenderer.GroundTopY(tile.Coord)
                            : Spatial.SimulationUnityMapper.TileHeight + tile.Elevation * 0.55f;
                        var t = (topY - ray.origin.y) / ray.direction.y;
                        if (t <= 0f || t >= bestT) continue;
                        var hit = ray.origin + ray.direction * t;
                        var centre = HexLive.Simulation.Spatial.HexSpatialMath.TileToWorld(tile.Coord);
                        var dx = Mathf.Abs(hit.x - centre.X);
                        var dz = Mathf.Abs(hit.z - centre.Y);
                        var radius = HexLive.Simulation.Spatial.HexSpatialMath.HexRadius;
                        var sqrt3 = HexLive.Simulation.Spatial.HexSpatialMath.Sqrt3;
                        if (dz > radius || sqrt3 * dx + dz > sqrt3 * radius) continue;
                        bestT = t;
                        best = tile.Coord;
                    }

                    if (best != null) return best;
                }
            }

            var faction = PlayerCampView.Of(_runner, snapshot);
            var home = snapshot.CampHomes.FirstOrDefault(candidate => candidate.Faction == faction);
            if (home != null) return home.Tile;
            return snapshot.Tiles.FirstOrDefault(tile => tile.Walkable && !tile.Water && !tile.Blocked)?.Coord;
        }

        /// <summary>
        /// Архитектура живого мира одним черновиком в абсолютной сетке:
        /// свободные детали и детали существующих домов (их собственный
        /// якорь и поворот уже применены). Мебель добавляет сам редактор —
        /// она сверяется с миром постоянно, а не один раз при открытии.
        /// </summary>
        internal static BuildingBlueprintDraft FreeWorldProjection(WorldSnapshot snapshot)
        {
            var draft = new BuildingBlueprintDraft
            {
                BlueprintId = "free_world_projection",
                HasAnchor = true,
                AnchorQ = 0,
                AnchorR = 0,
                GroundFurniture = true
            };
            foreach (var obj in snapshot.Objects.OrderBy(obj => obj.Id.Value))
            {
                if (!FreeArchitectureRules.IsFreePiece(obj)) continue;
                var state = obj.ArchitectureElements[0];
                var plannedDefinition = FreeArchitectureRules.PlannedDefinitionId(obj);
                if (string.IsNullOrEmpty(plannedDefinition)) continue;
                if (!FreeArchitectureRules.TryDecode(
                        plannedDefinition, state.SlotKey, out var element)) continue;
                element.Id = $"free_{obj.Id.Value}";
                element.Origin = BlueprintElementOrigin.Manual;
                draft.Elements.Add(element);
            }
            var occupiedSlots = draft.Elements.Select(BlueprintBuildingPlan.SlotKey).ToHashSet();
            foreach (var owner in snapshot.Objects.OrderBy(obj => obj.Id.Value))
            {
                if (string.IsNullOrEmpty(owner.BuildingBlueprintJson) ||
                    !BuildingBlueprintJson.TryDeserialize(owner.BuildingBlueprintJson, out var plan, out _)) continue;
                foreach (var authored in plan.Elements)
                {
                    var element = ArchitectureWorldEditing.ToWorld(authored, plan, owner.Tile, owner.RotationDegrees);
                    if (!occupiedSlots.Add(BlueprintBuildingPlan.SlotKey(element))) continue;
                    element.Id = $"house_{owner.Id.Value}_{authored.Id}";
                    draft.Elements.Add(element);
                }
            }
            draft.NextElementId = draft.Elements.Count + 1;
            // Комнаты — чтобы пол растягивался за стену, как в Sims.
            BlueprintEditorCommands.AssignRoomsToLooseFloors(draft);
            draft.Normalize();
            return draft;
        }

        /// <summary>Карточка грядки показывает взрослое растение, а не семечко.</summary>
        private static string CatalogIconId(string id) =>
            PlantGrowthCatalog.TryGetBySeed(id, out var plant) ? plant.MatureDefinitionId : id;
    }
}
