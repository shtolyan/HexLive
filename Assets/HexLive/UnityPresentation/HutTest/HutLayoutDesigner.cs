#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.HutTest.BlueprintEditor;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Views;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.HutTest
{
    /// <summary>
    /// Sims-style multi-hex constructor. The editor owns gestures and UI only;
    /// every mutation goes through BlueprintEditorCommands and the preview
    /// reads the resulting production-ready integer blueprint.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HutLayoutDesigner : MonoBehaviour
    {
        /// <summary>
        /// §120.8: конструктор, открытый ИЗ ИГРЫ на выбранном гексе. Черчение
        /// то же самое; отличия — якорь превью (гекс игрока вместо HutAssembly
        /// сцены), никакой принудительной паузы (серверный паритет §120.7),
        /// свой автосейв-черновик и кнопка «Построить», отдающая драфт наружу.
        /// Config кладётся статически ДО AddComponent: Awake строит UI сразу.
        /// </summary>
        public sealed class WorldPlacementConfig
        {
            public Vector3 AnchorWorldPosition;
            public Simulation.Common.TileCoord AnchorTile;
            public Action<BuildingBlueprintDraft> OnBuild;
            public Action OnClosed;
        }

        public static WorldPlacementConfig PendingWorldPlacement;

        /// <summary>Открыт ли конструктор поверх игрового мира — кнопка
        /// «Строить» BuildModePanel прячется, пока игрок чертит.</summary>
        public static bool WorldEditorOpen { get; private set; }

        private WorldPlacementConfig _worldPlacement;
        private bool WorldMode => _worldPlacement != null;

        private const string PanelResource = "HexLive/UI/HutConstructor/HutConstructorPanel";
        private const string WorldDraftId = "game_project_autosave";
        // ⚠️ Не совмещать путь со стилем: у .uxml при импорте появляется свой
        // inline-StyleSheet-сабассет, и Resources.Load<StyleSheet> по общему
        // пути отдаёт ЕГО — пустой. Панель тогда рисуется голыми кнопками во
        // весь экран. Поэтому таблица стилей живёт под собственным именем.
        private const string StyleResource = "HexLive/UI/HutConstructor/HutConstructorStyles";
        private const string DraftId = "hut_constructor_autosave";
        private const float PickRadius = 0.34f;
        private readonly BlueprintCommandHistory _history = new();
        private readonly BlueprintDraftStore _store = new();
        private readonly HashSet<string> _selectedIds = new();

        private UIDocument? _document;
        private VisualElement? _panel;
        private Label? _statusLabel;
        private Label? _selectionLabel;
        private Label? _selectionDescription;
        private Label? _availabilityLabel;
        private Label? _categoryTitle;
        private VisualElement? _categoryList;
        private VisualElement? _catalogList;
        private SimulationRunnerBehaviour? _runner;
        private Camera? _camera;
        private RtsCameraController? _rtsCamera;
        private bool _worldSelectionSuppressed;
        private Transform? _source;
        private BlueprintPreviewRenderer? _preview;
        private Transform? _handles;
        private BuildingBlueprintDraft _draft = null!;
        private BlueprintEditorMode _mode = BlueprintEditorMode.Furniture;
        private BuildCatalogMode _catalogMode = BuildCatalogMode.Furniture;
        private Tool _tool = Tool.None;
        private string _activeCategoryId = BuildCatalogCategories.Comfort;
        private BuildCatalogEntryDefinition? _activeCatalogEntry;
        private TileCoord? _outdoorGridTile;
        private string _selectedId = string.Empty;
        private int _selectedRoomId;
        private bool _gestureActive;
        private HexBuildNodeKey _wallStart;
        private FloorSectorKey _roomStart;
        private string _dragFurnitureId = string.Empty;
        private string _dragOpeningId = string.Empty;
        private BlueprintRoomResizeHandle? _roomResizeHandle;
        private BuildingBlueprintDraft? _gesturePreview;
        private BuildingBlueprintDraft? _rotationPreview;
        private string _rotationPreviewId = string.Empty;
        private float _rotationHandleRadius = 0.48f;
        private Material? _rotationButtonMaterial;
        private Material? _rotationGlyphMaterial;
        private bool _initialized;
        private float _autosaveDeadline;
        private string _statusKey = "blueprint.status.ready";

        private enum Tool
        {
            None,
            Room,
            Wall,
            Window,
            Door,
            Support,
            Floor,
            Roof,
            Furniture
        }

        private void Awake()
        {
            _worldPlacement = PendingWorldPlacement;
            PendingWorldPlacement = null;
            if (WorldMode) WorldEditorOpen = true;
            _document = GetComponent<UIDocument>();
            ConfigurePanelSettings();
            BuildUi();
            Loc.LanguageChanged += ApplyLanguage;
        }

        private void Start()
        {
            _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            _camera = Camera.main;
            SuppressWorldSelection();
            var draftId = WorldMode ? WorldDraftId : DraftId;
            if (!_store.TryLoad(draftId, out _draft, out var error))
            {
                // Игровой проект начинается с ЧИСТОГО листа: игрок чертит свой
                // дом, а не редактирует эталон dev-сцены.
                _draft = WorldMode ? new BuildingBlueprintDraft() : BuiltInBuildingBlueprints.Hut1Hex();
                _draft.BlueprintId = draftId;
                if (!string.IsNullOrEmpty(error)) Debug.LogWarning($"[BlueprintEditor] {error}", this);
            }
            ApplyLanguage();
        }

        private void OnDestroy()
        {
            RestoreWorldSelection();
            Loc.LanguageChanged -= ApplyLanguage;
            if (_rotationButtonMaterial != null) Destroy(_rotationButtonMaterial);
            if (_rotationGlyphMaterial != null) Destroy(_rotationGlyphMaterial);
            if (WorldMode)
            {
                // Превью — отдельный корневой GO; в игре за собой прибираем.
                WorldEditorOpen = false;
                if (_preview != null) Destroy(_preview.gameObject);
                if (_handles != null) Destroy(_handles.gameObject);
                _worldPlacement.OnClosed?.Invoke();
            }
        }

        private void Update()
        {
            SuppressWorldSelection();
            if (!_initialized) InitializePreview();
            if (!_initialized || _preview == null || _camera == null) return;
            // Dev-сцена конструктора держит мир на паузе; ИГРОВОЙ режим — нет
            // (§120.7: на сервере часы операторские, паузу игрок ставит сам).
            if (!WorldMode && _runner != null && !_runner.IsPaused) _runner.TogglePause();
            HandleKeyboard();
            HandlePointer();
            _preview.UpdateCutaway(_camera);
            UpdateRotationHandlePresentation();
            if (_autosaveDeadline > 0f && Time.unscaledTime >= _autosaveDeadline)
            {
                _autosaveDeadline = 0f;
                SaveDraft(false);
            }
        }

        private void OnDisable()
        {
            RestoreWorldSelection();
        }

        private void OnEnable()
        {
            SuppressWorldSelection();
        }

        private void SuppressWorldSelection()
        {
            if (_worldSelectionSuppressed && _rtsCamera != null) return;
            _worldSelectionSuppressed = false;
            var mainCamera = Camera.main;
            _rtsCamera = mainCamera != null ? mainCamera.GetComponent<RtsCameraController>() : null;
            if (_rtsCamera == null) _rtsCamera = FindAnyObjectByType<RtsCameraController>();
            if (_rtsCamera == null) return;
            _rtsCamera.SetSelectionInputSuppressed(this, true);
            _worldSelectionSuppressed = true;
        }

        private void RestoreWorldSelection()
        {
            if (_worldSelectionSuppressed && _rtsCamera != null)
            {
                _rtsCamera.SetSelectionInputSuppressed(this, false);
            }
            _worldSelectionSuppressed = false;
            _rtsCamera = null;
        }

        private void InitializePreview()
        {
            if (WorldMode)
            {
                // §120.8: превью встаёт на выбранный игроком гекс; чужие
                // renderer'ы не трогаем — вокруг живой мир, не тест-стенд.
                if (_draft == null) return;
                var worldRoot = new GameObject("Blueprint constructor preview");
                worldRoot.transform.SetPositionAndRotation(
                    _worldPlacement.AnchorWorldPosition, Quaternion.identity);
                _preview = worldRoot.AddComponent<BlueprintPreviewRenderer>();
                RebuildPreview();
                _initialized = true;
                return;
            }

            foreach (var hut in FindObjectsByType<HutAssembly>(FindObjectsSortMode.None))
            {
                if (hut.name.Contains("Blueprint", StringComparison.OrdinalIgnoreCase)) continue;
                _source = hut.transform;
                break;
            }
            if (_source == null || _draft == null) return;

            foreach (var renderer in _source.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            foreach (var view in WorldObjectView.All)
            {
                if (view == null || Vector3.Distance(view.transform.position, _source.position) >= 2f) continue;
                foreach (var renderer in view.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            }

            var root = new GameObject("Blueprint constructor preview");
            root.transform.SetPositionAndRotation(_source.position, _source.rotation);
            _preview = root.AddComponent<BlueprintPreviewRenderer>();
            RebuildPreview();
            _initialized = true;
        }

        private void ConfigurePanelSettings()
        {
            if (_document == null) return;
            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings == null) return;
            var settings = Instantiate(baseSettings);
            settings.name = "HutConstructorPanelSettings";
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.match = 1f;
            settings.sortingOrder = 175;
            _document.panelSettings = settings;
        }

        private void BuildUi()
        {
            if (_document == null) return;
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.pickingMode = PickingMode.Ignore;
            var tree = Resources.Load<VisualTreeAsset>(PanelResource);
            var sheet = Resources.Load<StyleSheet>(StyleResource);
            if (tree == null || sheet == null)
            {
                Debug.LogError("[BlueprintEditor] UI Toolkit resources are missing.", this);
                return;
            }
            tree.CloneTree(root);
            root.styleSheets.Add(sheet);
            // Обёртка UXML — полноэкранная и по умолчанию pickable: на высоком
            // sortingOrder она съедает клики по нижележащим панелям (ростер,
            // панель персонажа). Кликается только сам лоток.
            var wrapper = root.Q(className: "hut-constructor-root");
            if (wrapper != null) wrapper.pickingMode = PickingMode.Ignore;
            _panel = root.Q("constructor-panel");
            if (_panel != null) _panel.pickingMode = PickingMode.Position;
            _statusLabel = root.Q<Label>("status-label");
            _selectionLabel = root.Q<Label>("selection-label");
            _selectionDescription = root.Q<Label>("selection-description");
            _availabilityLabel = root.Q<Label>("availability-label");
            _categoryTitle = root.Q<Label>("category-title");
            _categoryList = root.Q("category-list");
            _catalogList = root.Q("catalog-list");

            root.Q<Button>("mode-construction").clicked += () => SetCatalogMode(BuildCatalogMode.Construction);
            root.Q<Button>("mode-furniture").clicked += () => SetCatalogMode(BuildCatalogMode.Furniture);
            root.Q<Button>("delete-selection").clicked += DeleteSelected;
            root.Q<Button>("finish-selection").clicked += FinishSelection;
            root.Q<Button>("undo").clicked += Undo;
            root.Q<Button>("redo").clicked += Redo;
            if (WorldMode)
            {
                // §120.8: в игре черновик и так автосейвится; две кнопки — это
                // «Выйти» (закрыть без стройки) и «Построить» (отдать драфт).
                root.Q<Button>("save").clicked += CloseWorldMode;
                root.Q<Button>("export").clicked += BuildAndClose;
            }
            else
            {
                root.Q<Button>("save").clicked += () => SaveDraft(true);
                root.Q<Button>("export").clicked += ExportDraft;
            }

            RebuildCatalogUi();
            RefreshUiState();
        }

        /// <summary>Валидный чертёж уходит наружу (BuildModePanel шлёт команду
        /// разметки), конструктор закрывается. Невалидный остаётся на экране с
        /// причиной в статусе — молча терять работу игрока нельзя.</summary>
        private void BuildAndClose()
        {
            if (_worldPlacement?.OnBuild == null) return;
            SaveDraft(false);
            var validation = BlueprintValidator.Validate(_draft);
            var hasFloor = _draft.Elements.Any(e => e.Kind == BlueprintElementKind.FloorSector);
            var hasDoor = _draft.Elements.Any(e => e.Kind == BlueprintElementKind.Door);
            if (!validation.IsValid || !hasFloor || !hasDoor)
            {
                SetStatus(!hasFloor
                    ? Loc.Get("blueprint.build.needs_floor")
                    : !hasDoor
                        ? Loc.Get("blueprint.build.needs_door")
                        : validation.Issues[0].Message);
                return;
            }

            var draft = _draft.Clone();
            _worldPlacement.OnBuild(draft);
            CloseWorldMode();
        }

        private void CloseWorldMode()
        {
            if (!WorldMode) return;
            SaveDraft(false);
            Destroy(gameObject);
        }

        private void HandleKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            var command = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ||
                          keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed;
            if (command && keyboard.zKey.wasPressedThisFrame)
            {
                if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) Redo();
                else Undo();
            }
            else if (keyboard.qKey.wasPressedThisFrame) RotateSelected(-1);
            else if (keyboard.eKey.wasPressedThisFrame) RotateSelected(1);
            else if (keyboard.deleteKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame) DeleteSelected();
            else if (keyboard.escapeKey.wasPressedThisFrame) CancelGesture();
        }

        private void HandlePointer()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (mouse.rightButton.wasPressedThisFrame)
            {
                CancelGesture();
                return;
            }
            var screen = mouse.position.ReadValue();
            if (PointerOverPanel(screen)) return;
            if (!TryLocalPoint(screen, out var local)) return;

            if (!_gestureActive && _activeCatalogEntry != null) UpdateCatalogGhost(local);
            if (mouse.leftButton.wasPressedThisFrame) PointerDown(local, screen);
            if (_gestureActive && mouse.leftButton.isPressed) UpdateGesturePreview(local);
            if (_gestureActive && mouse.leftButton.wasReleasedThisFrame) PointerUp(local);
        }

        private void PointerDown(Vector3 local, Vector2 screen)
        {
            if (TryHandleClick(screen)) return;
            switch (_tool)
            {
                case Tool.Wall:
                    _wallStart = NearestBuildNode(local);
                    _gestureActive = true;
                    break;
                case Tool.Room:
                    _roomStart = NearestSector(local);
                    _gestureActive = true;
                    break;
                case Tool.None:
                    ClearRotationPreview();
                    var keyboard = Keyboard.current;
                    var additive = keyboard != null &&
                        (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
                    SelectNearest(local, additive);
                    if (_selectedIds.Count <= 1 && _mode == BlueprintEditorMode.Furniture &&
                        _draft.Furniture.Any(item => item.Id == _selectedId))
                    {
                        _dragFurnitureId = _selectedId;
                        _gestureActive = true;
                    }
                    else if (_mode == BlueprintEditorMode.Architecture &&
                             _draft.Elements.Any(element => element.Id == _selectedId &&
                                 element.Kind is BlueprintElementKind.Window or BlueprintElementKind.Door))
                    {
                        _dragOpeningId = _selectedId;
                        _gestureActive = true;
                    }
                    break;
                default:
                    ExecuteSingleClick(local);
                    break;
            }
        }

        private void UpdateGesturePreview(Vector3 local)
        {
            if (_preview == null) return;
            _gesturePreview = _draft.Clone();
            BlueprintCommandResult result;
            if (_roomResizeHandle != null)
            {
                result = BlueprintEditorCommands.ResizeRoom(_gesturePreview,
                    _roomResizeHandle.RoomId, ResizeSectors(_roomResizeHandle, local));
            }
            else if (_tool == Tool.Wall)
            {
                var target = SnapLineEnd(_wallStart, NearestBuildNode(local));
                result = BlueprintEditorCommands.DrawWall(_gesturePreview, _wallStart, target);
            }
            else if (_tool == Tool.Room)
            {
                var end = NearestSector(local);
                result = BlueprintEditorCommands.CreateRoom(_gesturePreview,
                    RoomDragSectors(_roomStart, end, WholeHexTarget(local, end.Hex)));
            }
            else if (!string.IsNullOrEmpty(_dragFurnitureId))
            {
                var (tile, slot) = NearestJunction(local);
                result = BlueprintEditorCommands.Move(_gesturePreview, _dragFurnitureId, tile, slot);
            }
            else if (!string.IsNullOrEmpty(_dragOpeningId))
            {
                result = BlueprintEditorCommands.MoveOpening(
                    _gesturePreview, _dragOpeningId, NearestSegment(local));
            }
            else return;

            ShowCommandPreview(result);
            if (_tool == Tool.Wall)
            {
                var target = SnapLineEnd(_wallStart, NearestBuildNode(local));
                _preview.ShowWallGuide(_wallStart, target,
                    result.Succeeded || WallLineAlreadyCompatible(_wallStart, target));
                if (BlueprintGeometry.TryLine(_wallStart, target, out _, out var sectionCount))
                    SetStatus($"{sectionCount} × 0,5 = {sectionCount * BlueprintGeometry.BuildStep:0.0} wu");
            }
        }

        private void PointerUp(Vector3 local)
        {
            BlueprintCommandResult? result = null;
            if (_roomResizeHandle != null)
            {
                var handle = _roomResizeHandle;
                result = Execute(working => BlueprintEditorCommands.ResizeRoom(
                    working, handle.RoomId, ResizeSectors(handle, local)));
            }
            else if (_tool == Tool.Wall)
            {
                var target = SnapLineEnd(_wallStart, NearestBuildNode(local));
                result = Execute(working => BlueprintEditorCommands.DrawWall(working, _wallStart, target));
            }
            else if (_tool == Tool.Room)
            {
                var end = NearestSector(local);
                var sectors = RoomDragSectors(_roomStart, end, WholeHexTarget(local, end.Hex));
                result = Execute(working => BlueprintEditorCommands.CreateRoom(working, sectors));
            }
            else if (!string.IsNullOrEmpty(_dragFurnitureId))
            {
                var id = _dragFurnitureId;
                var (tile, slot) = NearestJunction(local);
                result = Execute(working => BlueprintEditorCommands.Move(working, id, tile, slot));
            }
            else if (!string.IsNullOrEmpty(_dragOpeningId))
            {
                var id = _dragOpeningId;
                result = Execute(working => BlueprintEditorCommands.MoveOpening(
                    working, id, NearestSegment(local)));
            }
            _gestureActive = false;
            _dragFurnitureId = string.Empty;
            _dragOpeningId = string.Empty;
            _roomResizeHandle = null;
            _gesturePreview = null;
            RebuildPreview();
            if (result != null) SetStatus(result.Message);
        }

        private void ExecuteSingleClick(Vector3 local)
        {
            BlueprintCommandResult? result = null;
            if (_tool is Tool.Window or Tool.Door)
            {
                var segment = NearestSegment(local);
                result = Execute(working => BlueprintEditorCommands.PlaceOpening(working, segment,
                    _tool == Tool.Door ? BlueprintElementKind.Door : BlueprintElementKind.Window));
            }
            else if (_tool == Tool.Support)
            {
                var node = NearestBuildNode(local);
                result = Execute(working => BlueprintEditorCommands.AddSupport(working, node));
            }
            else if (_tool == Tool.Floor)
            {
                var sector = NearestSector(local);
                result = Execute(working => BlueprintEditorCommands.AddFloorSector(working, sector));
            }
            else if (_tool == Tool.Roof)
            {
                // One click roofs the whole hex. The dome is still six sector
                // elements — that is what the save format, Indoor and staged
                // construction speak — but the player never leaves a hex half
                // covered, and nothing is ever needed at the hex centre.
                var floor = NearestSector(local);
                result = Execute(working => BlueprintEditorCommands.RoofHex(working, floor.Hex));
            }
            else if (_tool == Tool.Furniture && _activeCatalogEntry != null)
            {
                var (tile, slot) = NearestJunction(local);
                var definition = _activeCatalogEntry.DefinitionId;
                result = Execute(working => BlueprintEditorCommands.PlaceFurniture(working, definition, tile, slot));
                if (result.Succeeded)
                {
                    _selectedId = _draft.Furniture.Last().Id;
                    _selectedIds.Clear();
                    _selectedIds.Add(_selectedId);
                    _activeCatalogEntry = null;
                    _tool = Tool.None;
                    _outdoorGridTile = null;
                    RebuildCatalogUi();
                    RebuildPreview();
                }
            }
            if (result != null)
            {
                if (!result.Succeeded) ShowCommandPreview(result);
                SetStatus(result.Message);
            }
        }

        private void UpdateCatalogGhost(Vector3 local)
        {
            if (_activeCatalogEntry == null || _preview == null || _gestureActive) return;
            // Editor commands commit successful candidates into the draft they
            // receive. A hover preview must therefore operate on a disposable
            // clone; passing _draft here would silently place one item per
            // frame before the player ever clicked.
            var previewDraft = _draft.Clone();
            BlueprintCommandResult? result = null;
            switch (_activeCatalogEntry.PlacementKind)
            {
                case BuildCatalogPlacementKind.FloorRegion:
                    result = BlueprintEditorCommands.AddFloorSector(previewDraft, NearestSector(local));
                    break;
                case BuildCatalogPlacementKind.WallLine:
                    var wallSegment = NearestSegment(local);
                    result = BlueprintEditorCommands.DrawWall(previewDraft, wallSegment.A, wallSegment.B);
                    break;
                case BuildCatalogPlacementKind.Window:
                    result = BlueprintEditorCommands.PlaceOpening(
                        previewDraft, NearestSegment(local), BlueprintElementKind.Window);
                    break;
                case BuildCatalogPlacementKind.Door:
                    result = BlueprintEditorCommands.PlaceOpening(
                        previewDraft, NearestSegment(local), BlueprintElementKind.Door);
                    break;
                case BuildCatalogPlacementKind.Support:
                    result = BlueprintEditorCommands.AddSupport(previewDraft, NearestBuildNode(local));
                    break;
                case BuildCatalogPlacementKind.Roof:
                    result = BlueprintEditorCommands.RoofHex(previewDraft, NearestSector(local).Hex);
                    break;
                case BuildCatalogPlacementKind.Furniture:
                    var (tile, slot) = NearestJunction(local);
                    _outdoorGridTile = _activeCatalogEntry.CategoryId == BuildCatalogCategories.Outdoor
                        ? tile
                        : null;
                    result = BlueprintEditorCommands.PlaceFurniture(
                        previewDraft, _activeCatalogEntry.DefinitionId, tile, slot);
                    break;
            }

            if (result != null) ShowCommandPreview(result);
        }

        private BlueprintCommandResult Execute(Func<BuildingBlueprintDraft, BlueprintCommandResult> gesture)
        {
            ClearRotationPreview();
            var result = _history.Execute(_draft, gesture);
            if (result.Succeeded)
            {
                _autosaveDeadline = Time.unscaledTime + 0.35f;
                RebuildPreview();
            }
            RefreshUiState();
            return result;
        }

        private void SelectNearest(Vector3 local, bool additive)
        {
            ClearRotationPreview();
            var candidateId = string.Empty;
            var candidateRoomId = 0;
            var best = PickRadius * PickRadius;
            if (_catalogMode == BuildCatalogMode.Furniture)
            {
                foreach (var item in _draft.Furniture)
                {
                    var point = BlueprintGeometry.JunctionToWorld(item.PrimaryJunction);
                    var sq = Sqr(local.x - point.X, local.z - point.Y);
                    if (sq >= best) continue;
                    best = sq;
                    candidateId = item.Id;
                }
            }
            else
            {
                foreach (var element in _draft.Elements.Where(ElementVisibleToCurrentToolspace))
                {
                    var point = ElementCenter(element);
                    var sq = Sqr(local.x - point.x, local.z - point.z);
                    if (sq >= best) continue;
                    best = sq;
                    candidateId = element.Id;
                    candidateRoomId = element.Kind == BlueprintElementKind.FloorSector ? element.RoomId : 0;
                }
            }
            if (!additive || candidateRoomId > 0)
            {
                _selectedIds.Clear();
                _selectedRoomId = 0;
            }
            if (!string.IsNullOrEmpty(candidateId))
            {
                _selectedId = candidateId;
                _selectedIds.Add(candidateId);
                _selectedRoomId = candidateRoomId;
            }
            else if (!additive)
            {
                _selectedId = string.Empty;
            }
            RebuildPreview();
            RefreshUiState();
        }

        private bool ElementVisibleToCurrentToolspace(BlueprintElementData element) => _mode switch
        {
            BlueprintEditorMode.Rooms => element.Kind == BlueprintElementKind.FloorSector,
            BlueprintEditorMode.Roof => element.Kind is BlueprintElementKind.RoofSector or BlueprintElementKind.Support,
            BlueprintEditorMode.Architecture => element.Kind is BlueprintElementKind.Wall or
                BlueprintElementKind.Window or BlueprintElementKind.Door or BlueprintElementKind.Support,
            _ => false
        };

        private void RotateSelected(int delta)
        {
            if (_selectedIds.Count > 1 || !_draft.Furniture.Any(item => item.Id == _selectedId)) return;
            if (_rotationPreview == null || _rotationPreviewId != _selectedId)
            {
                _rotationPreview = _draft.Clone();
                _rotationPreviewId = _selectedId;
            }

            var previewResult = BlueprintEditorCommands.Rotate(_rotationPreview, _selectedId, delta);
            if (previewResult.Candidate == null)
            {
                SetStatus(previewResult.Message);
                return;
            }

            _rotationPreview = previewResult.Candidate;
            var previewItem = _rotationPreview.Furniture.First(item => item.Id == _selectedId);
            var committedItem = _draft.Furniture.First(item => item.Id == _selectedId);
            if (!previewResult.Validation.IsValid)
            {
                ShowCommandPreview(previewResult);
                RebuildHandles(_rotationPreview);
                RefreshUiState();
                SetStatus(previewResult.Message);
                return;
            }

            var totalDelta = previewItem.YawStep - committedItem.YawStep;
            ClearRotationPreview();
            if (BlueprintGeometry.NormalizeSector(totalDelta) == 0)
            {
                RebuildPreview();
                RefreshUiState();
                SetStatus("blueprint.status.cancelled", true);
                return;
            }

            var result = Execute(working => BlueprintEditorCommands.Rotate(working, _selectedId, totalDelta));
            SetStatus(result.Message);
        }

        private void DeleteSelected()
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            ClearRotationPreview();
            var result = _selectedRoomId > 0
                ? Execute(working => BlueprintEditorCommands.DeleteRoom(working, _selectedRoomId))
                : _selectedIds.Count > 1
                    ? Execute(working => BlueprintEditorCommands.DeleteMany(working, _selectedIds))
                    : Execute(working => BlueprintEditorCommands.Delete(working, _selectedId));
            if (result.Succeeded)
            {
                _selectedId = string.Empty;
                _selectedRoomId = 0;
                _selectedIds.Clear();
            }
            SetStatus(result.Message);
        }

        private void Undo()
        {
            ClearRotationPreview();
            if (!_history.Undo(_draft)) return;
            _selectedId = string.Empty;
            _selectedIds.Clear();
            _autosaveDeadline = Time.unscaledTime + 0.2f;
            RebuildPreview();
            RefreshUiState();
            SetStatus("blueprint.status.undo", true);
        }

        private void Redo()
        {
            ClearRotationPreview();
            if (!_history.Redo(_draft)) return;
            _selectedId = string.Empty;
            _selectedIds.Clear();
            _autosaveDeadline = Time.unscaledTime + 0.2f;
            RebuildPreview();
            RefreshUiState();
            SetStatus("blueprint.status.redo", true);
        }

        private void CancelGesture()
        {
            _gestureActive = false;
            _dragFurnitureId = string.Empty;
            _dragOpeningId = string.Empty;
            _roomResizeHandle = null;
            _gesturePreview = null;
            _activeCatalogEntry = null;
            _outdoorGridTile = null;
            _tool = Tool.None;
            ClearRotationPreview();
            RebuildCatalogUi();
            RebuildPreview();
            SetStatus("blueprint.status.cancelled", true);
        }

        private void SaveDraft(bool announce)
        {
            try
            {
                var path = _store.Save(_draft);
                if (announce)
                {
                    Debug.Log($"[BlueprintEditor][SAVED] {path}", this);
                    SetStatus("blueprint.status.saved", true);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                SetStatus("blueprint.status.save_failed", true);
            }
        }

        private void ExportDraft()
        {
            var json = BuildingBlueprintJson.Serialize(_draft);
            GUIUtility.systemCopyBuffer = json;
            SaveDraft(false);
            Debug.Log($"[BlueprintEditor][EXPORT]\n{json}", this);
            SetStatus("blueprint.status.exported", true);
        }

        private void RebuildPreview()
        {
            if (_preview == null) return;
            _preview.Rebuild(_draft, _mode, _selectedId, selectedIds: _selectedIds,
                extraFurnitureGridTiles: OutdoorGridTiles());
            RebuildHandles();
        }

        private void ShowCommandPreview(BlueprintCommandResult result)
        {
            if (_preview == null) return;
            var candidate = result.Candidate ?? _draft;
            var changed = ChangedIds(_draft, candidate);
            if (result.Candidate != null && !string.IsNullOrEmpty(_dragFurnitureId)) changed.Add(_dragFurnitureId);
            if (result.Candidate != null && !string.IsNullOrEmpty(_dragOpeningId)) changed.Add(_dragOpeningId);
            var junctionConflicts = FurnitureConflicts(candidate);
            var buildConflicts = MissingRoofSupports(candidate);
            var invalid = result.Candidate != null && !result.Validation.IsValid;
            _preview.Rebuild(candidate, _mode, _selectedId,
                junctionConflicts, buildConflicts, changed, invalid, _selectedIds,
                OutdoorGridTiles());
        }

        private IEnumerable<TileCoord> OutdoorGridTiles()
        {
            if (_catalogMode == BuildCatalogMode.Furniture &&
                _activeCategoryId == BuildCatalogCategories.Outdoor &&
                _outdoorGridTile is { } tile)
                yield return tile;
        }

        private static HashSet<string> ChangedIds(
            BuildingBlueprintDraft before, BuildingBlueprintDraft after)
        {
            var changed = new HashSet<string>();
            var beforeElements = before.Elements.ToDictionary(element => element.Id);
            foreach (var element in after.Elements)
            {
                if (!beforeElements.TryGetValue(element.Id, out var old) ||
                    old.Kind != element.Kind || old.Origin != element.Origin || old.RoomId != element.RoomId ||
                    old.Node != element.Node || old.Segment != element.Segment ||
                    old.FloorSector != element.FloorSector || old.RoofSector != element.RoofSector)
                    changed.Add(element.Id);
            }
            var beforeFurniture = before.Furniture.ToDictionary(item => item.Id);
            foreach (var item in after.Furniture)
            {
                if (!beforeFurniture.TryGetValue(item.Id, out var old) ||
                    old.DefinitionId != item.DefinitionId || old.TileQ != item.TileQ || old.TileR != item.TileR ||
                    old.JunctionSlot != item.JunctionSlot || old.YawStep != item.YawStep)
                    changed.Add(item.Id);
            }
            return changed;
        }

        private static JunctionKey[] FurnitureConflicts(BuildingBlueprintDraft draft) =>
            draft.Furniture.SelectMany(item => BlueprintFurnitureFootprints.OccupiedJunctions(item))
                .GroupBy(key => key).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();

        private static HexBuildNodeKey[] MissingRoofSupports(BuildingBlueprintDraft draft)
        {
            var supports = draft.Elements.Where(element => element.Kind == BlueprintElementKind.Support)
                .Select(element => element.Node).ToHashSet();
            return draft.Elements.Where(element => element.Kind == BlueprintElementKind.RoofSector)
                .Select(element => element.RoofSector.Hex)
                .Distinct()
                .SelectMany(hex =>
                {
                    var candidates = BlueprintGeometry.RoofSupports(new RoofSectorKey(hex, 0));
                    return candidates.Count(supports.Contains) >= BlueprintGeometry.RequiredRoofSupportCount
                        ? Array.Empty<HexBuildNodeKey>()
                        : candidates.Where(node => !supports.Contains(node));
                })
                .Distinct()
                .ToArray();
        }

        private void RebuildHandles(BuildingBlueprintDraft? displayDraft = null)
        {
            if (_preview == null) return;
            if (_handles != null)
            {
                _handles.gameObject.SetActive(false);
                Destroy(_handles.gameObject);
            }
            if (string.IsNullOrEmpty(_selectedId)) return;
            var handleDraft = displayDraft ?? _draft;
            var selectedFurniture = handleDraft.Furniture.FirstOrDefault(item => item.Id == _selectedId);
            if (selectedFurniture == null)
            {
                if (_selectedRoomId > 0) BuildRoomResizeHandles();
                return;
            }
            var footprint = BlueprintFurnitureFootprints.OccupiedJunctions(selectedFurniture)
                .Select(BlueprintGeometry.JunctionToWorld).ToArray();
            var centre = footprint.Aggregate(Vector2.zero,
                (sum, point) => sum + new Vector2(point.X, point.Y)) / Mathf.Max(1, footprint.Length);
            _rotationHandleRadius = Mathf.Max(0.48f, footprint.Max(point =>
                Vector2.Distance(centre, new Vector2(point.X, point.Y))) + 0.26f);
            _handles = new GameObject("Selection handles").transform;
            _handles.SetParent(_preview.transform, false);
            _handles.localPosition = new Vector3(centre.x, HutAssembly.FloorSurfaceLift + 0.08f, centre.y);
            AddHandle("drag-handle", Vector3.zero, new Color(1f, 0.63f, 0.20f), 0.11f);
            AddRotationButton("rotate-left-handle", -1);
            AddRotationButton("rotate-right-handle", 1);
            UpdateRotationHandlePresentation();
        }

        private void BuildRoomResizeHandles()
        {
            if (_preview == null) return;
            var floors = _draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == _selectedRoomId)
                .Select(element => element.FloorSector).ToArray();
            if (floors.Length == 0) return;
            var floorSet = floors.ToHashSet();
            var boundary = BlueprintGeometry.BoundaryOf(floors);
            var centroid = floors.Select(sector => SectorCenter(sector.Hex, sector.Sector))
                .Aggregate(Vector3.zero, (sum, point) => sum + point) / floors.Length;
            _handles = new GameObject("Room resize handles").transform;
            _handles.SetParent(_preview.transform, false);

            foreach (var side in boundary.GroupBy(LineKey))
            {
                var segments = side.ToArray();
                var owners = floors.Where(sector =>
                    BlueprintGeometry.SectorBoundary(sector).Any(segments.Contains)).Distinct().ToArray();
                var candidates = new HashSet<FloorSectorKey>();
                foreach (var owner in owners)
                {
                    for (var q = owner.Hex.Q - 1; q <= owner.Hex.Q + 1; q++)
                    for (var r = owner.Hex.R - 1; r <= owner.Hex.R + 1; r++)
                    for (var sector = 0; sector < 6; sector++)
                    {
                        var candidate = new FloorSectorKey(new TileCoord(q, r), sector);
                        if (floorSet.Contains(candidate)) continue;
                        if (BlueprintGeometry.SectorBoundary(candidate).Any(segments.Contains))
                            candidates.Add(candidate);
                    }
                }

                var midpoint = segments.Select(segment =>
                {
                    var a = BlueprintGeometry.ToWorld(segment.A);
                    var b = BlueprintGeometry.ToWorld(segment.B);
                    return new Vector3((a.X + b.X) * 0.5f, 0f, (a.Y + b.Y) * 0.5f);
                }).Aggregate(Vector3.zero, (sum, point) => sum + point) / segments.Length;
                var outward = midpoint - centroid;
                outward.y = 0f;
                if (outward.sqrMagnitude < 0.001f) continue;
                outward.Normalize();

                var go = new GameObject($"floor-resize-arrow-{side.Key}");
                go.name = $"room-resize-{side.Key}";
                go.transform.SetParent(_handles, false);
                go.transform.localPosition = midpoint + outward * 0.14f + Vector3.up *
                    (HutAssembly.FloorSurfaceLift + 0.07f);
                go.transform.localRotation = Quaternion.LookRotation(outward, Vector3.up);
                AddResizeArrowArt(go.transform);
                var handle = go.AddComponent<BlueprintRoomResizeHandle>();
                handle.RoomId = _selectedRoomId;
                handle.Outward = outward;
                handle.AddSectors = candidates.ToArray();
                handle.RemoveSectors = owners;
            }
        }

        private void AddResizeArrowArt(Transform parent)
        {
            var stemObject = new GameObject("arrow-stem");
            stemObject.transform.SetParent(parent, false);
            var stem = stemObject.AddComponent<LineRenderer>();
            stem.useWorldSpace = false;
            stem.positionCount = 2;
            stem.SetPosition(0, new Vector3(0f, 0f, -0.17f));
            stem.SetPosition(1, new Vector3(0f, 0f, 0.17f));
            stem.startWidth = 0.052f;
            stem.endWidth = 0.052f;
            stem.sharedMaterial = RotationButtonMaterial;
            stem.startColor = stem.endColor = new Color(1f, 0.63f, 0.20f, 0.98f);

            var headObject = new GameObject("arrow-head");
            headObject.transform.SetParent(parent, false);
            var head = headObject.AddComponent<LineRenderer>();
            head.useWorldSpace = false;
            head.positionCount = 3;
            head.SetPosition(0, new Vector3(-0.10f, 0f, 0.07f));
            head.SetPosition(1, new Vector3(0f, 0f, 0.19f));
            head.SetPosition(2, new Vector3(0.10f, 0f, 0.07f));
            head.startWidth = 0.052f;
            head.endWidth = 0.052f;
            head.sharedMaterial = RotationButtonMaterial;
            head.startColor = head.endColor = new Color(1f, 0.63f, 0.20f, 0.98f);
        }

        private IReadOnlyList<FloorSectorKey> ResizeSectors(BlueprintRoomResizeHandle handle, Vector3 local)
        {
            var floors = _draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == handle.RoomId)
                .Select(element => element.FloorSector).ToHashSet();
            var fromHandle = local - handle.transform.localPosition;
            if (Vector3.Dot(fromHandle, handle.Outward) >= 0f)
                floors.UnionWith(handle.AddSectors);
            else
                floors.ExceptWith(handle.RemoveSectors);
            return floors.ToArray();
        }

        private static string LineKey(BuildSegmentKey segment)
        {
            if (segment.A.Q == segment.B.Q) return $"q{segment.A.Q}";
            if (segment.A.R == segment.B.R) return $"r{segment.A.R}";
            return $"s{segment.A.Q + segment.A.R}";
        }

        private void AddHandle(string name, Vector3 localPosition, Color color, float scale)
        {
            if (_handles == null) return;
            var handle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            handle.name = name;
            handle.transform.SetParent(_handles, false);
            handle.transform.localPosition = localPosition;
            handle.transform.localScale = Vector3.one * scale;
            handle.GetComponent<Renderer>().material.color = color;
        }

        private void AddRotationButton(string name, int direction)
        {
            if (_handles == null) return;
            var button = new GameObject(name);
            button.transform.SetParent(_handles, false);
            button.AddComponent<BlueprintRotationHandle>().Direction = direction;

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "button";
            disc.transform.SetParent(button.transform, false);
            disc.transform.localScale = new Vector3(0.13f, 0.018f, 0.13f);
            disc.GetComponent<Renderer>().sharedMaterial = RotationButtonMaterial;
            Destroy(disc.GetComponent<Collider>());

            var arcObject = new GameObject("curved-arrow");
            arcObject.transform.SetParent(button.transform, false);
            var line = arcObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = 11;
            line.startWidth = 0.018f;
            line.endWidth = 0.018f;
            line.sharedMaterial = RotationGlyphMaterial;
            line.startColor = line.endColor = new Color(0.10f, 0.14f, 0.16f);
            for (var index = 0; index < line.positionCount; index++)
            {
                var t = index / (line.positionCount - 1f);
                var degrees = direction > 0 ? Mathf.Lerp(145f, -75f, t) : Mathf.Lerp(35f, 255f, t);
                var radians = degrees * Mathf.Deg2Rad;
                line.SetPosition(index, new Vector3(
                    Mathf.Cos(radians) * 0.073f, 0.031f, Mathf.Sin(radians) * 0.073f));
            }

            var end = line.GetPosition(line.positionCount - 1);
            var previous = line.GetPosition(line.positionCount - 2);
            var tangent = (end - previous).normalized;
            var normal = new Vector3(-tangent.z, 0f, tangent.x);
            var head = new GameObject("arrow-head");
            head.transform.SetParent(button.transform, false);
            var headLine = head.AddComponent<LineRenderer>();
            headLine.useWorldSpace = false;
            headLine.positionCount = 3;
            headLine.startWidth = headLine.endWidth = 0.018f;
            headLine.sharedMaterial = RotationGlyphMaterial;
            headLine.startColor = headLine.endColor = new Color(0.10f, 0.14f, 0.16f);
            var back = end - tangent * 0.037f;
            headLine.SetPosition(0, back + normal * 0.022f);
            headLine.SetPosition(1, end);
            headLine.SetPosition(2, back - normal * 0.022f);
        }

        private void UpdateRotationHandlePresentation()
        {
            if (_camera == null || _preview == null || _handles == null) return;
            var right = _preview.transform.InverseTransformDirection(_camera.transform.right);
            right.y = 0f;
            if (right.sqrMagnitude < 0.001f) right = Vector3.right;
            else right.Normalize();
            var forward = Vector3.Cross(right, Vector3.up).normalized;
            foreach (var handle in _handles.GetComponentsInChildren<BlueprintRotationHandle>())
            {
                handle.transform.localPosition = right * (_rotationHandleRadius * handle.Direction);
                handle.transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);
            }
        }

        private bool TryHandleClick(Vector2 screen)
        {
            if (_camera == null || _handles == null) return false;
            foreach (var roomHandle in _handles.GetComponentsInChildren<BlueprintRoomResizeHandle>())
            {
                var markerScreen = _camera.WorldToScreenPoint(roomHandle.transform.position);
                if ((new Vector2(markerScreen.x, markerScreen.y) - screen).sqrMagnitude > 34f * 34f) continue;
                _roomResizeHandle = roomHandle;
                _gestureActive = true;
                return true;
            }
            foreach (var marker in _handles.GetComponentsInChildren<Transform>())
            {
                var name = marker.name;
                if (name == "drag-handle" && _selectedIds.Count <= 1 &&
                    _draft.Furniture.Any(item => item.Id == _selectedId))
                {
                    var dragScreen = _camera.WorldToScreenPoint(marker.position);
                    if ((new Vector2(dragScreen.x, dragScreen.y) - screen).sqrMagnitude <= 28f * 28f)
                    {
                        _dragFurnitureId = _selectedId;
                        _gestureActive = true;
                        return true;
                    }
                }
            }
            foreach (var handle in _handles.GetComponentsInChildren<BlueprintRotationHandle>())
            {
                var markerScreen = _camera.WorldToScreenPoint(handle.transform.position);
                if (markerScreen.z <= 0f ||
                    (new Vector2(markerScreen.x, markerScreen.y) - screen).sqrMagnitude > 30f * 30f) continue;
                RotateSelected(handle.Direction);
                return true;
            }
            return false;
        }

        private bool PointerOverPanel(Vector2 screen)
        {
            if (_document?.rootVisualElement.panel == null || _panel == null) return false;
            var panelPosition = RuntimePanelUtils.ScreenToPanel(
                _document.rootVisualElement.panel, new Vector2(screen.x, Screen.height - screen.y));
            return _panel.worldBound.Contains(panelPosition);
        }

        private bool TryLocalPoint(Vector2 screen, out Vector3 local)
        {
            local = default;
            if (_camera == null || _preview == null) return false;
            var plane = new Plane(Vector3.up,
                _preview.transform.position + Vector3.up * HutAssembly.FloorSurfaceLift);
            var ray = _camera.ScreenPointToRay(screen);
            if (!plane.Raycast(ray, out var distance)) return false;
            local = _preview.transform.InverseTransformPoint(ray.GetPoint(distance));
            return true;
        }

        private void SetCatalogMode(BuildCatalogMode mode)
        {
            _catalogMode = mode;
            _activeCatalogEntry = null;
            _outdoorGridTile = null;
            _tool = Tool.None;
            _activeCategoryId = BuildCatalogDefinition.ForMode(mode)
                .Select(entry => entry.CategoryId).FirstOrDefault() ?? string.Empty;
            _mode = mode == BuildCatalogMode.Furniture
                ? BlueprintEditorMode.Furniture
                : PreviewModeForCategory(_activeCategoryId);
            CancelGesture();
            RebuildCatalogUi();
            RefreshUiState();
        }

        private void SetCategory(string categoryId)
        {
            _activeCategoryId = categoryId;
            _activeCatalogEntry = null;
            _outdoorGridTile = null;
            _tool = Tool.None;
            _mode = _catalogMode == BuildCatalogMode.Furniture
                ? BlueprintEditorMode.Furniture
                : PreviewModeForCategory(categoryId);
            CancelGesture();
            RebuildCatalogUi();
            RefreshUiState();
        }

        private void SelectCatalogEntry(BuildCatalogEntryDefinition entry)
        {
            CancelGesture();
            _activeCatalogEntry = entry;
            _activeCategoryId = entry.CategoryId;
            _catalogMode = entry.Mode;
            _tool = ToolFor(entry.PlacementKind);
            _mode = entry.Mode == BuildCatalogMode.Furniture
                ? BlueprintEditorMode.Furniture
                : PreviewModeForCategory(entry.CategoryId);
            _selectedId = string.Empty;
            _selectedRoomId = 0;
            _selectedIds.Clear();
            RebuildCatalogUi();
            RebuildPreview();
            RefreshUiState();
        }

        private static BlueprintEditorMode PreviewModeForCategory(string categoryId) => categoryId switch
        {
            BuildCatalogCategories.Floor => BlueprintEditorMode.Rooms,
            BuildCatalogCategories.Roof => BlueprintEditorMode.Roof,
            _ => BlueprintEditorMode.Architecture
        };

        private static Tool ToolFor(BuildCatalogPlacementKind kind) => kind switch
        {
            BuildCatalogPlacementKind.FloorRegion => Tool.Room,
            BuildCatalogPlacementKind.WallLine => Tool.Wall,
            BuildCatalogPlacementKind.Window => Tool.Window,
            BuildCatalogPlacementKind.Door => Tool.Door,
            BuildCatalogPlacementKind.Support => Tool.Support,
            BuildCatalogPlacementKind.Roof => Tool.Roof,
            BuildCatalogPlacementKind.Furniture => Tool.Furniture,
            _ => Tool.None
        };

        private void FinishSelection()
        {
            CancelGesture();
            _activeCatalogEntry = null;
            _outdoorGridTile = null;
            _tool = Tool.None;
            _selectedId = string.Empty;
            _selectedRoomId = 0;
            _selectedIds.Clear();
            RebuildCatalogUi();
            RebuildPreview();
            RefreshUiState();
        }

        private void RebuildCatalogUi()
        {
            if (_categoryList == null || _catalogList == null) return;
            var entries = BuildCatalogDefinition.ForMode(_catalogMode).ToArray();
            var categories = entries.Select(entry => entry.CategoryId).Distinct().ToArray();
            if (categories.Length > 0 && !categories.Contains(_activeCategoryId))
                _activeCategoryId = categories[0];

            _categoryList.Clear();
            foreach (var categoryId in categories)
            {
                var captured = categoryId;
                var button = new Button(() => SetCategory(captured))
                {
                    text = Loc.Get(CategoryTerm(captured))
                };
                button.AddToClassList("category-button");
                SetActive(button, captured == _activeCategoryId);
                _categoryList.Add(button);
            }

            _catalogList.Clear();
            foreach (var entry in entries.Where(entry => entry.CategoryId == _activeCategoryId))
            {
                var captured = entry;
                var card = new Button(() => SelectCatalogEntry(captured));
                card.AddToClassList("catalog-card");
                SetActive(card, ReferenceEquals(_activeCatalogEntry, entry) ||
                                _activeCatalogEntry?.DefinitionId == entry.DefinitionId);

                card.Add(BuildCardThumb(captured));
                var name = new Label(Loc.Get(entry.NameTerm));
                name.AddToClassList("catalog-name");
                card.Add(name);
                var description = new Label(Loc.Get(entry.DescriptionTerm));
                description.AddToClassList("catalog-description");
                card.Add(description);
                _catalogList.Add(card);
            }

            if (_categoryTitle != null)
                _categoryTitle.text = Loc.Get(CategoryTerm(_activeCategoryId));
        }

        /// <summary>
        /// Превью карточки — как в Sims: картинка предмета, а до её появления
        /// (или при её отсутствии) глиф на тёмной подложке. Иконки едут тем же
        /// путём, что у инвентаря (<see cref="ItemIcons"/>, Addressables
        /// «icon/&lt;definitionId&gt;»): нарисованная для вещи иконка появится
        /// в каталоге сама, без правок здесь. Load не блокирует и грузит в
        /// фоне, поэтому карточка недолго опрашивает кэш.
        /// </summary>
        private static VisualElement BuildCardThumb(BuildCatalogEntryDefinition entry)
        {
            var thumb = new VisualElement();
            thumb.AddToClassList("catalog-thumb");
            thumb.pickingMode = PickingMode.Ignore;

            var glyph = new Label(entry.FallbackGlyph);
            glyph.AddToClassList("catalog-thumb-glyph");
            glyph.pickingMode = PickingMode.Ignore;
            thumb.Add(glyph);

            var image = new Image { scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("catalog-thumb-image");
            image.pickingMode = PickingMode.Ignore;
            image.style.display = DisplayStyle.None;
            thumb.Add(image);

            void Apply(Sprite sprite)
            {
                image.sprite = sprite;
                image.style.display = DisplayStyle.Flex;
                glyph.style.display = DisplayStyle.None;
            }

            var ready = ItemIcons.Load(entry.DefinitionId);
            if (ready != null)
            {
                Apply(ready);
                return thumb;
            }

            // Промахи ItemIcons кэширует, так что опрос дешёвый; предел нужен
            // лишь затем, чтобы карточка вещи БЕЗ иконки не опрашивала вечно.
            var attempts = 0;
            IVisualElementScheduledItem? poll = null;
            poll = image.schedule.Execute(() =>
            {
                var sprite = ItemIcons.Load(entry.DefinitionId);
                if (sprite != null) Apply(sprite);
                if (sprite != null || ++attempts > 20) poll?.Pause();
            }).Every(300);
            return thumb;
        }

        private static string CategoryTerm(string categoryId) => categoryId switch
        {
            BuildCatalogCategories.Floor => "blueprint.category.floor",
            BuildCatalogCategories.Walls => "blueprint.category.walls",
            BuildCatalogCategories.Openings => "blueprint.category.openings",
            BuildCatalogCategories.Roof => "blueprint.category.roof",
            BuildCatalogCategories.Comfort => "blueprint.category.comfort",
            BuildCatalogCategories.Heating => "blueprint.category.heating",
            BuildCatalogCategories.Storage => "blueprint.category.storage",
            BuildCatalogCategories.Workstations => "blueprint.category.workstations",
            BuildCatalogCategories.Outdoor => "blueprint.category.outdoor",
            _ => "blueprint.category.other"
        };

        private void RefreshUiState()
        {
            if (_document == null) return;
            var root = _document.rootVisualElement;
            SetActive(root.Q("mode-construction"), _catalogMode == BuildCatalogMode.Construction);
            SetActive(root.Q("mode-furniture"), _catalogMode == BuildCatalogMode.Furniture);

            root.Q<Button>("undo").SetEnabled(_history.CanUndo);
            root.Q<Button>("redo").SetEnabled(_history.CanRedo);
            var displayedDraft = _rotationPreviewId == _selectedId ? _rotationPreview : _draft;
            var selected = displayedDraft?.Furniture.FirstOrDefault(item => item.Id == _selectedId);
            var catalogEntry = _activeCatalogEntry;
            if (catalogEntry == null && selected != null)
                BuildCatalogDefinition.TryGet(selected.DefinitionId, out catalogEntry);

            if (_selectionLabel != null)
            {
                _selectionLabel.text = _selectedIds.Count > 1
                    ? $"{Loc.Get("blueprint.selection")}: {_selectedIds.Count}"
                    : catalogEntry != null
                        ? Loc.Get(catalogEntry.NameTerm) +
                          (selected != null ? $" · {selected.YawStep * 60}°" : string.Empty)
                        : _selectedRoomId > 0
                            ? $"{Loc.Get("blueprint.room")} #{_selectedRoomId}"
                            : Loc.Get("blueprint.selection.none");
            }
            if (_selectionDescription != null)
            {
                _selectionDescription.text = catalogEntry != null
                    ? Loc.Get(catalogEntry.DescriptionTerm)
                    : _selectedRoomId > 0
                        ? Loc.Get(BlueprintValidator.IsIndoorRoom(_draft, _selectedRoomId)
                            ? "blueprint.indoor.ready"
                            : "blueprint.indoor.incomplete")
                        : Loc.Get("blueprint.catalog.help");
            }
            if (_availabilityLabel != null)
            {
                var waitingForFloor = selected != null && catalogEntry?.RequiresCompletedFloor == true &&
                                      !FurnitureHasFloorSupport(displayedDraft!, selected);
                _availabilityLabel.text = Loc.Get(waitingForFloor
                    ? "blueprint.availability.floor"
                    : "blueprint.availability.ready");
                _availabilityLabel.EnableInClassList("waiting", waitingForFloor);
            }
            root.Q<Button>("delete-selection").SetEnabled(!string.IsNullOrEmpty(_selectedId));
            root.Q<Button>("finish-selection").SetEnabled(
                _activeCatalogEntry != null || !string.IsNullOrEmpty(_selectedId));
        }

        private static bool FurnitureHasFloorSupport(
            BuildingBlueprintDraft draft, FurniturePlacementData item)
        {
            var floors = draft.Elements.Where(element => element.Kind == BlueprintElementKind.FloorSector)
                .Select(element => element.FloorSector).ToArray();
            return BlueprintFurnitureFootprints.OccupiedJunctions(item)
                .All(junction => BlueprintGeometry.IsSupportedByFloor(junction, floors));
        }

        private void ClearRotationPreview()
        {
            _rotationPreview = null;
            _rotationPreviewId = string.Empty;
        }

        private Material RotationButtonMaterial => _rotationButtonMaterial ??= HandleMaterial(
            "BlueprintRotationButton", new Color(1f, 0.63f, 0.20f, 0.98f));

        private Material RotationGlyphMaterial => _rotationGlyphMaterial ??= HandleMaterial(
            "BlueprintRotationGlyph", new Color(0.10f, 0.14f, 0.16f, 1f));

        private static Material HandleMaterial(string name, Color color)
        {
            var shader = Shader.Find("HexLive/BlueprintOverlay")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            var material = new Material(shader) { name = name, color = color, renderQueue = 5000 };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }

        private void ApplyLanguage()
        {
            if (_document == null) return;
            var root = _document.rootVisualElement;
            Text<Label>(root, "title-label", "blueprint.title");
            Text<Button>(root, "mode-construction", "blueprint.mode.construction");
            Text<Button>(root, "mode-furniture", "blueprint.mode.furniture");
            Text<Label>(root, "selection-title", "blueprint.selection.title");
            Text<Button>(root, "delete-selection", "blueprint.action.delete");
            Text<Button>(root, "finish-selection", "blueprint.action.finish");
            // Undo/redo — узкие кнопки-глифы; слово живёт в подсказке, иначе
            // «Отменить» вылезает из 36px и наезжает на соседей.
            Tooltip<Button>(root, "undo", "blueprint.action.undo");
            Tooltip<Button>(root, "redo", "blueprint.action.redo");
            Text<Button>(root, "save", WorldMode ? "blueprint.action.exit" : "blueprint.action.save");
            Text<Button>(root, "export", WorldMode ? "blueprint.action.build" : "blueprint.action.export");
            Text<Label>(root, "shortcut-label", "blueprint.shortcuts");
            RebuildCatalogUi();
            SetStatus(_statusKey, true);
            RefreshUiState();
        }

        private void SetStatus(string value, bool localizationKey = false)
        {
            if (localizationKey) _statusKey = value;
            else _statusKey = string.Empty;
            if (_statusLabel != null) _statusLabel.text = localizationKey ? Loc.Get(value) : value;
        }

        private static void Text<T>(VisualElement root, string name, string key) where T : TextElement
        {
            var element = root.Q<T>(name);
            if (element != null) element.text = Loc.Get(key);
        }

        private static void Tooltip<T>(VisualElement root, string name, string key) where T : VisualElement
        {
            var element = root.Q<T>(name);
            if (element != null) element.tooltip = Loc.Get(key);
        }

        private static void SetActive(VisualElement? element, bool active)
        {
            if (element == null) return;
            if (active) element.AddToClassList("active");
            else element.RemoveFromClassList("active");
        }

        private static HexBuildNodeKey NearestBuildNode(Vector3 point)
        {
            var q = 2f * point.x / (BlueprintGeometry.BuildStep * HexSpatialMath.Sqrt3);
            var r = point.z / BlueprintGeometry.BuildStep - q * 0.5f;
            return RoundAxial(q, r);
        }

        private static HexBuildNodeKey RoundAxial(float q, float r)
        {
            var x = q;
            var z = r;
            var y = -x - z;
            var rx = Mathf.RoundToInt(x);
            var ry = Mathf.RoundToInt(y);
            var rz = Mathf.RoundToInt(z);
            var dx = Mathf.Abs(rx - x);
            var dy = Mathf.Abs(ry - y);
            var dz = Mathf.Abs(rz - z);
            if (dx > dy && dx > dz) rx = -ry - rz;
            else if (dy > dz) ry = -rx - rz;
            else rz = -rx - ry;
            return new HexBuildNodeKey(rx, rz);
        }

        private static HexBuildNodeKey SnapLineEnd(HexBuildNodeKey start, HexBuildNodeKey target)
        {
            var dq = target.Q - start.Q;
            var dr = target.R - start.R;
            var candidates = new[]
            {
                new HexBuildNodeKey(0, dr),
                new HexBuildNodeKey(dq, 0),
                new HexBuildNodeKey(dq, -dq)
            };
            return candidates.Select(candidate => new
                {
                    Node = start + candidate,
                    Error = Sqr(target.Q - start.Q - candidate.Q, target.R - start.R - candidate.R)
                })
                .OrderBy(candidate => candidate.Error).First().Node;
        }

        private bool WallLineAlreadyCompatible(HexBuildNodeKey start, HexBuildNodeKey end)
        {
            if (!BlueprintGeometry.TryLine(start, end, out _, out _)) return false;
            var occupied = _draft.Elements.Where(element =>
                    element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door)
                .Select(element => element.Segment).ToHashSet();
            return BlueprintGeometry.SplitLine(start, end).All(occupied.Contains);
        }

        private static FloorSectorKey NearestSector(Vector3 local)
        {
            var tile = WorldToTile(local.x, local.z);
            var center = HexSpatialMath.TileToWorld(tile);
            var angle = Mathf.Atan2(local.z - center.Y, local.x - center.X) * Mathf.Rad2Deg;
            var cornerAngle = 90f;
            var sector = Mathf.FloorToInt(Mathf.Repeat(cornerAngle - angle, 360f) / 60f);
            return new FloorSectorKey(tile, sector);
        }

        private static TileCoord WorldToTile(float x, float z)
        {
            var q = (HexSpatialMath.Sqrt3 / 3f * x - z / 3f) / HexSpatialMath.HexRadius;
            var r = (2f / 3f * z) / HexSpatialMath.HexRadius;
            var rounded = RoundAxial(q, r);
            return new TileCoord(rounded.Q, rounded.R);
        }

        private static (TileCoord tile, int slot) NearestJunction(Vector3 local)
        {
            var tile = WorldToTile(local.x, local.z);
            var center = HexSpatialMath.TileToWorld(tile);
            var bestSlot = 0;
            var bestSq = float.MaxValue;
            foreach (var node in HexPointLayout.GetInteriorTemplates())
            {
                var sq = Sqr(local.x - center.X - node.Offset.X, local.z - center.Y - node.Offset.Y);
                if (sq >= bestSq) continue;
                bestSq = sq;
                bestSlot = node.Slot;
            }
            return (tile, bestSlot);
        }

        private static BuildSegmentKey NearestSegment(Vector3 local)
        {
            var node = NearestBuildNode(local);
            BuildSegmentKey best = default;
            var bestSq = float.MaxValue;
            foreach (var direction in BlueprintGeometry.NeighborDirections)
            {
                var segment = new BuildSegmentKey(node, node + direction);
                var a = BlueprintGeometry.ToWorld(segment.A);
                var b = BlueprintGeometry.ToWorld(segment.B);
                var sq = PointSegmentDistanceSquared(new Vector2(local.x, local.z),
                    new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
                if (sq >= bestSq) continue;
                bestSq = sq;
                best = segment;
            }
            return best;
        }

        private static IReadOnlyList<FloorSectorKey> RoomDragSectors(
            FloorSectorKey start, FloorSectorKey end, bool wholeEndHex)
        {
            if (start.Hex == end.Hex)
            {
                if (wholeEndHex)
                    return Enumerable.Range(0, 6)
                        .Select(sector => new FloorSectorKey(start.Hex, sector)).ToArray();
                var clockwise = (end.Sector - start.Sector + 6) % 6;
                var counter = (start.Sector - end.Sector + 6) % 6;
                var direction = clockwise <= counter ? 1 : -1;
                var count = Math.Min(clockwise, counter) + 1;
                return Enumerable.Range(0, count)
                    .Select(index => new FloorSectorKey(start.Hex, start.Sector + direction * index)).ToArray();
            }

            var qMin = Math.Min(start.Hex.Q, end.Hex.Q);
            var qMax = Math.Max(start.Hex.Q, end.Hex.Q);
            var rMin = Math.Min(start.Hex.R, end.Hex.R);
            var rMax = Math.Max(start.Hex.R, end.Hex.R);
            var sectors = new List<FloorSectorKey>();
            for (var q = qMin; q <= qMax; q++)
            for (var r = rMin; r <= rMax; r++)
            for (var sector = 0; sector < 6; sector++)
                sectors.Add(new FloorSectorKey(new TileCoord(q, r), sector));
            if (!wholeEndHex)
            {
                var startWorld = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(start.Hex));
                var endWorld = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(end.Hex));
                var towardStart = new Vector2(startWorld.X - endWorld.X, startWorld.Y - endWorld.Y).normalized;
                var kept = Enumerable.Range(0, 6).Select(sector =>
                {
                    var center = SectorCenter(end.Hex, sector);
                    var hexCenter = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(end.Hex));
                    var direction = new Vector2(center.x - hexCenter.X, center.z - hexCenter.Y).normalized;
                    return (sector, dot: Vector2.Dot(direction, towardStart));
                }).OrderByDescending(candidate => candidate.dot).Take(3)
                  .Select(candidate => candidate.sector).ToHashSet();
                sectors.RemoveAll(sector => sector.Hex == end.Hex && !kept.Contains(sector.Sector));
            }
            return sectors;
        }

        private static bool WholeHexTarget(Vector3 local, TileCoord hex)
        {
            var center = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(hex));
            return Sqr(local.x - center.X, local.z - center.Y) <= 0.30f * 0.30f;
        }

        private Vector3 ElementCenter(BlueprintElementData element)
        {
            switch (element.Kind)
            {
                case BlueprintElementKind.Support:
                    var node = BlueprintGeometry.ToWorld(element.Node);
                    return new Vector3(node.X, 0f, node.Y);
                case BlueprintElementKind.FloorSector:
                    return SectorCenter(element.FloorSector.Hex, element.FloorSector.Sector);
                case BlueprintElementKind.RoofSector:
                    return SectorCenter(element.RoofSector.Hex, element.RoofSector.Sector);
                default:
                    var a = BlueprintGeometry.ToWorld(element.Segment.A);
                    var b = BlueprintGeometry.ToWorld(element.Segment.B);
                    return new Vector3((a.X + b.X) * 0.5f, 0f, (a.Y + b.Y) * 0.5f);
            }
        }

        private static Vector3 SectorCenter(TileCoord hex, int sector)
        {
            var center = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(hex));
            var a = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(hex, sector));
            var b = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(hex, sector + 1));
            return new Vector3((center.X + a.X + b.X) / 3f, 0f, (center.Y + a.Y + b.Y) / 3f);
        }

        private static float PointSegmentDistanceSquared(Vector2 point, Vector2 a, Vector2 b)
        {
            var delta = b - a;
            var denominator = delta.sqrMagnitude;
            if (denominator < 0.0001f) return (point - a).sqrMagnitude;
            var t = Mathf.Clamp01(Vector2.Dot(point - a, delta) / denominator);
            return (point - (a + delta * t)).sqrMagnitude;
        }

        private static float Sqr(float x, float y) => x * x + y * y;
    }

    public sealed class BlueprintRotationHandle : MonoBehaviour
    {
        public int Direction;
    }

    public sealed class BlueprintRoomResizeHandle : MonoBehaviour
    {
        public int RoomId;
        public Vector3 Outward;
        public FloorSectorKey[] AddSectors = Array.Empty<FloorSectorKey>();
        public FloorSectorKey[] RemoveSectors = Array.Empty<FloorSectorKey>();
    }
}
