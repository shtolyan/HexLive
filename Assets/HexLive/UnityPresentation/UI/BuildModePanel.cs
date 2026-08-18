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
using HexLive.UnityPresentation.HutTest;
using HexLive.UnityPresentation.HutTest.BlueprintEditor;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §120.7: игровой режим строительства — Sims-лоток Build/Buy прямо в
    /// обычной игре и билдах. Кнопка «Строить»/клавиша B открывают тот же
    /// UXML/USS-лоток, что у конструктора HutTest; карточка цепляет ghost
    /// ГОТОВОГО изделия к курсору со снапом на центр гекса (§66), клик шлёт
    /// мировую команду (<see cref="PlaceFurnitureSiteCommand"/> /
    /// <see cref="PlaceBuildingPlanCommand"/>) через общую шину §121.9 — и
    /// строят уже сами девушки штатным циклом доставки §120.
    ///
    /// Пока режим открыт, симуляция стоит на паузе, а каждая размеченная
    /// недостроенная площадка показана полностью построенным призраком —
    /// «каким это будет». Выход возвращает штатный рендер: поэтапную сборку
    /// по доставленному и колышки §120.1 на пустых площадках.
    ///
    /// Зелёный/красный ghost — клиентская ОЦЕНКА тех же правил по snapshot;
    /// истину о месте решает исполнитель команды в симуляции.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class BuildModePanel : MonoBehaviour
    {
        private const string PanelResource = "HexLive/UI/HutConstructor/HutConstructorPanel";
        private const string StyleResource = "HexLive/UI/HutConstructor/HutConstructorStyles";
        private const string HutCardId = "building.hut_plan";

        /// <summary>§120.8: карточка «Свой проект» — клик по гексу открывает
        /// конструктор прямо в мире, «Построить» шлёт чертёж командой.</summary>
        private const string ProjectCardId = "custom.project";
        private const float SnapshotInterval = 0.25f;
        private const float OverlayInterval = 1f;

        /// <summary>Колышки §120.1 прячутся, пока открыт режим стройки: их
        /// подменяет полный призрак здания (читает BuildSiteStakeRenderer).</summary>
        public static bool IsOpen { get; private set; }

        private SimulationRunnerBehaviour? _runner;
        private HexWorldRenderer? _worldRenderer;
        private UIDocument? _document;
        private Camera? _camera;
        private RtsCameraController? _rtsCamera;

        private VisualElement? _panel;
        private Button? _openButton;
        private Label? _statusLabel;
        private Label? _categoryTitle;
        private Label? _selectionLabel;
        private Label? _selectionDescription;
        private Label? _availabilityLabel;
        private VisualElement? _categoryList;
        private VisualElement? _catalogList;
        private Button? _deleteButton;

        private BuildCatalogMode _catalogMode = BuildCatalogMode.Furniture;
        private string _activeCategoryId = BuildCatalogCategories.Comfort;
        private string _activeCardId = string.Empty;

        private bool _selectionSuppressed;

        // Выбранная УЖЕ размеченная пустая площадка: повернуть / снять / Esc.
        private int? _selectedSiteId;
        private string _selectedSiteProduct = string.Empty;
        private TileCoord _selectedSiteTile;
        private float _selectedSiteRotation;
        private LineRenderer? _selectionOutline;

        // Стрелки поворота — экранные кнопки, следующие за объектом в мире.
        private Button? _rotateLeft;
        private Button? _rotateRight;

        // Сетка «можно/нельзя»: точка на каждом гексе, пока выбрана карточка.
        private GameObject? _gridRoot;
        private readonly List<(MeshRenderer Renderer, TileCoord Tile)> _gridDots = new();
        private Material? _dotFree;
        private Material? _dotBusy;
        private Mesh? _dotMesh;
        private bool _gridDirty;

        // Ghost под курсором.
        private GameObject? _ghostRoot;
        private LineRenderer? _ghostOutline;
        private int _ghostYawSteps;
        private TileCoord? _ghostTile;
        private bool _ghostValid;

        // «Полностью построено» поверх размеченных площадок.
        private readonly Dictionary<int, GameObject> _siteOverlays = new();
        private float _nextOverlayRefresh;

        // Кэш snapshot для валидации и оверлеев.
        private WorldSnapshot? _snapshot;
        private float _nextSnapshotRefresh;
        private readonly Dictionary<TileCoord, TileSnapshot> _tiles = new();
        private readonly HashSet<TileCoord> _occupiedHexes = new();

        // Проверка «площадка не появилась» → честный «здесь нельзя строить».
        private TileCoord? _pendingTile;
        private float _pendingDeadline;

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        public void SetWorldRenderer(HexWorldRenderer renderer) => _worldRenderer = renderer;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            ConfigurePanelSettings();
            BuildUi();
            Loc.LanguageChanged += ApplyLanguage;
        }

        private void OnDestroy()
        {
            Loc.LanguageChanged -= ApplyLanguage;
            EndBuildMode();
            IsOpen = false;
            if (_outlineMaterial != null) Destroy(_outlineMaterial);
            if (_dotFree != null) Destroy(_dotFree);
            if (_dotBusy != null) Destroy(_dotBusy);
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
            root.pickingMode = PickingMode.Ignore;

            var tree = Resources.Load<VisualTreeAsset>(PanelResource);
            var sheet = Resources.Load<StyleSheet>(StyleResource);
            if (tree == null || sheet == null)
            {
                UnityEngine.Debug.LogError("[BuildMode] UI Toolkit resources are missing.", this);
                return;
            }

            tree.CloneTree(root);
            root.styleSheets.Add(sheet);
            // ⚠️ Обёртка UXML — полноэкранный absolute-элемент с picking по
            // умолчанию. На sortingOrder выше остального UI она молча съедала
            // клики по ростеру и панели персонажа даже с закрытым лотком.
            var wrapper = root.Q(className: "hut-constructor-root");
            if (wrapper != null) wrapper.pickingMode = PickingMode.Ignore;
            _panel = root.Q("constructor-panel");
            if (_panel != null)
            {
                _panel.pickingMode = PickingMode.Position;
                _panel.style.display = DisplayStyle.None;
            }

            _statusLabel = root.Q<Label>("status-label");
            _categoryTitle = root.Q<Label>("category-title");
            _selectionLabel = root.Q<Label>("selection-label");
            _selectionDescription = root.Q<Label>("selection-description");
            _availabilityLabel = root.Q<Label>("availability-label");
            _categoryList = root.Q("category-list");
            _catalogList = root.Q("catalog-list");

            // Игровому режиму не нужны черновичные глаголы конструктора.
            Hide(root, "undo");
            Hide(root, "redo");
            Hide(root, "save");
            Hide(root, "export");
            Hide(root, "shortcut-label");
            _deleteButton = root.Q<Button>("delete-selection");
            if (_deleteButton != null)
            {
                _deleteButton.style.display = DisplayStyle.None;
                _deleteButton.clicked += CancelSelectedSite;
            }

            root.Q<Button>("mode-construction").clicked += () => SetMode(BuildCatalogMode.Construction);
            root.Q<Button>("mode-furniture").clicked += () => SetMode(BuildCatalogMode.Furniture);
            var finish = root.Q<Button>("finish-selection");
            if (finish != null) finish.clicked += Close;

            // Плавающая кнопка входа — видна, когда мир готов, а лоток закрыт.
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

            // Стрелки поворота — следуют за ghost'ом/выбранной площадкой в мире.
            _rotateLeft = MakeRotateArrow(root, "↺", () => Rotate(-1));
            _rotateRight = MakeRotateArrow(root, "↻", () => Rotate(1));

            ApplyLanguage();
        }

        private static Button MakeRotateArrow(
            VisualElement root, string glyph, System.Action onClick)
        {
            var button = new Button(onClick) { text = glyph };
            button.AddToClassList("icon-button");
            button.style.position = Position.Absolute;
            button.style.width = 36f;
            button.style.height = 36f;
            button.style.fontSize = 19f;
            button.style.display = DisplayStyle.None;
            root.Add(button);
            return button;
        }

        private static void Hide(VisualElement root, string name)
        {
            var element = root.Q(name);
            if (element != null) element.style.display = DisplayStyle.None;
        }

        private void ApplyLanguage()
        {
            if (_document == null) return;
            var root = _document.rootVisualElement;
            var title = root.Q<Label>("title-label");
            if (title != null) title.text = Loc.Get("blueprint.title");
            var construction = root.Q<Button>("mode-construction");
            if (construction != null) construction.text = Loc.Get("blueprint.mode.construction");
            var furniture = root.Q<Button>("mode-furniture");
            if (furniture != null) furniture.text = Loc.Get("blueprint.mode.furniture");
            var finish = root.Q<Button>("finish-selection");
            if (finish != null) finish.text = Loc.Get("blueprint.action.finish");
            var deleteButton = root.Q<Button>("delete-selection");
            if (deleteButton != null) deleteButton.text = Loc.Get("blueprint.action.delete");
            var selectionTitle = root.Q<Label>("selection-title");
            if (selectionTitle != null) selectionTitle.text = Loc.Get("blueprint.selection.title");
            if (_openButton != null) _openButton.text = Loc.Get("build.mode.button");
            if (IsOpen) RebuildCatalogUi();
        }

        // ─────────────────────────────────────────────────────────────────
        // Открытие/закрытие режима.
        // ─────────────────────────────────────────────────────────────────

        private void Open()
        {
            if (IsOpen || _runner == null || !_runner.IsReady || !_runner.SupportsNpcCommands)
            {
                return;
            }

            IsOpen = true;
            _camera = Camera.main;
            // Sims-style: режим стройки — про мир, не про персонажа. Панель
            // выбранной колонистки закрывается, чтобы не просвечивать под лотком.
            NpcSelection.Clear();
            SuppressSelection(true);
            // Паузу НЕ ставим: на сервере часы операторские (§83.2), и режим
            // обязан работать одинаково локально и по проводу. Хочет паузу —
            // игрок ставит её сам штатной панелью скорости.

            if (_panel != null) _panel.style.display = DisplayStyle.Flex;
            if (_openButton != null) _openButton.style.display = DisplayStyle.None;
            SetMode(BuildCatalogMode.Furniture);
            SetStatus("build.mode.hint");
            _nextOverlayRefresh = 0f;
            _nextSnapshotRefresh = 0f;
        }

        private void Close()
        {
            if (!IsOpen) return;
            EndBuildMode();
        }

        private void EndBuildMode()
        {
            IsOpen = false;
            ClearGhost();
            ClearOverlays();
            DeselectSite();
            SuppressSelection(false);
            if (_panel != null) _panel.style.display = DisplayStyle.None;
            // Стрелки живут в root, вне _panel, и обновляются только пока
            // IsOpen — без явного скрытия они переживают выход из режима.
            if (_rotateLeft != null) _rotateLeft.style.display = DisplayStyle.None;
            if (_rotateRight != null) _rotateRight.style.display = DisplayStyle.None;
        }

        private void SuppressSelection(bool suppressed)
        {
            if (suppressed == _selectionSuppressed) return;
            if (_rtsCamera == null)
            {
                var mainCamera = Camera.main;
                _rtsCamera = mainCamera != null ? mainCamera.GetComponent<RtsCameraController>() : null;
                if (_rtsCamera == null) _rtsCamera = FindAnyObjectByType<RtsCameraController>();
            }

            if (_rtsCamera == null) return;
            _rtsCamera.SetSelectionInputSuppressed(this, suppressed);
            _selectionSuppressed = suppressed;
        }

        // ─────────────────────────────────────────────────────────────────
        // Каталог.
        // ─────────────────────────────────────────────────────────────────

        private void SetMode(BuildCatalogMode mode)
        {
            _catalogMode = mode;
            _activeCardId = string.Empty;
            ClearGhost();
            _activeCategoryId = mode == BuildCatalogMode.Construction
                ? "house"
                : BuildCatalogDefinition.ForMode(BuildCatalogMode.Furniture)
                    .Select(entry => entry.CategoryId).FirstOrDefault() ?? string.Empty;
            RebuildCatalogUi();
        }

        private void SetCategory(string categoryId)
        {
            _activeCategoryId = categoryId;
            RebuildCatalogUi();
        }

        /// <summary>Игровая вкладка «Строительство» — одна карточка целого дома
        /// по утверждённому плану; свободная поэлементная архитектура остаётся
        /// в конструкторе HutTest (§120.7).</summary>
        private void RebuildCatalogUi()
        {
            if (_categoryList == null || _catalogList == null || _document == null) return;
            var root = _document.rootVisualElement;
            SetActive(root.Q("mode-construction"), _catalogMode == BuildCatalogMode.Construction);
            SetActive(root.Q("mode-furniture"), _catalogMode == BuildCatalogMode.Furniture);

            _categoryList.Clear();
            _catalogList.Clear();

            if (_catalogMode == BuildCatalogMode.Construction)
            {
                AddCategoryButton("house", Loc.Get("blueprint.category.house"));
                if (_categoryTitle != null) _categoryTitle.text = Loc.Get("blueprint.category.house");
                AddCard(HutCardId, "blueprint.catalog.hut.name", "blueprint.catalog.hut.description", "⌂");
                AddCard(ProjectCardId, "blueprint.catalog.project.name",
                    "blueprint.catalog.project.description", "✎");
            }
            else
            {
                var entries = BuildCatalogDefinition.ForMode(BuildCatalogMode.Furniture).ToArray();
                var categories = entries.Select(entry => entry.CategoryId).Distinct().ToArray();
                if (categories.Length > 0 && !categories.Contains(_activeCategoryId))
                    _activeCategoryId = categories[0];
                foreach (var categoryId in categories)
                {
                    AddCategoryButton(categoryId, Loc.Get(CategoryTerm(categoryId)));
                }

                if (_categoryTitle != null)
                    _categoryTitle.text = Loc.Get(CategoryTerm(_activeCategoryId));
                foreach (var entry in entries.Where(entry => entry.CategoryId == _activeCategoryId))
                {
                    AddCard(entry.DefinitionId, entry.NameTerm, entry.DescriptionTerm, entry.FallbackGlyph);
                }
            }

            RefreshSelectionCard();
        }

        private void AddCategoryButton(string categoryId, string text)
        {
            var captured = categoryId;
            var button = new Button(() => SetCategory(captured)) { text = text };
            button.AddToClassList("category-button");
            SetActive(button, captured == _activeCategoryId);
            _categoryList!.Add(button);
        }

        private void AddCard(string cardId, string nameTerm, string descriptionTerm, string glyph)
        {
            var captured = cardId;
            var card = new Button(() => SelectCard(captured));
            card.AddToClassList("catalog-card");
            SetActive(card, captured == _activeCardId);

            var thumb = new VisualElement();
            thumb.AddToClassList("catalog-thumb");
            thumb.pickingMode = PickingMode.Ignore;
            var glyphLabel = new Label(glyph);
            glyphLabel.AddToClassList("catalog-thumb-glyph");
            glyphLabel.pickingMode = PickingMode.Ignore;
            thumb.Add(glyphLabel);
            var image = new Image { scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("catalog-thumb-image");
            image.pickingMode = PickingMode.Ignore;
            image.style.display = DisplayStyle.None;
            thumb.Add(image);
            var icon = ItemIcons.Load(cardId);
            if (icon != null)
            {
                image.sprite = icon;
                image.style.display = DisplayStyle.Flex;
                glyphLabel.style.display = DisplayStyle.None;
            }

            card.Add(thumb);
            var name = new Label(Loc.Get(nameTerm));
            name.AddToClassList("catalog-name");
            card.Add(name);
            var description = new Label(Loc.Get(descriptionTerm));
            description.AddToClassList("catalog-description");
            card.Add(description);
            _catalogList!.Add(card);
        }

        private static string CategoryTerm(string categoryId) => categoryId switch
        {
            BuildCatalogCategories.Comfort => "blueprint.category.comfort",
            BuildCatalogCategories.Heating => "blueprint.category.heating",
            BuildCatalogCategories.Storage => "blueprint.category.storage",
            BuildCatalogCategories.Workstations => "blueprint.category.workstations",
            BuildCatalogCategories.Outdoor => "blueprint.category.outdoor",
            _ => "blueprint.category.other"
        };

        private static void SetActive(VisualElement? element, bool active)
        {
            if (element == null) return;
            if (active) element.AddToClassList("active");
            else element.RemoveFromClassList("active");
        }

        private void SelectCard(string cardId)
        {
            DeselectSite();
            _activeCardId = cardId;
            _ghostYawSteps = 0;
            BuildGhost();
            RebuildCatalogUi();
            SetStatus("build.mode.hint");
        }

        private void RefreshSelectionCard()
        {
            if (_selectionLabel == null || _selectionDescription == null) return;
            if (_activeCardId.Length > 0)
            {
                var (nameTerm, descriptionTerm) = CardTerms(_activeCardId);
                _selectionLabel.text = Loc.Get(nameTerm);
                _selectionDescription.text = Loc.Get(descriptionTerm);
            }
            else if (_selectedSiteId is not null)
            {
                var (nameTerm, descriptionTerm) = CardTerms(_selectedSiteProduct);
                _selectionLabel.text = Loc.Get(nameTerm);
                _selectionDescription.text = Loc.Get(descriptionTerm);
            }
            else
            {
                _selectionLabel.text = Loc.Get("blueprint.selection.none");
                _selectionDescription.text = Loc.Get("blueprint.catalog.help");
            }

            if (_availabilityLabel != null)
            {
                var waiting = _activeCardId.Length > 0 && !_ghostValid;
                _availabilityLabel.text = Loc.Get(waiting
                    ? "build.mode.blocked"
                    : "blueprint.availability.ready");
                _availabilityLabel.EnableInClassList("waiting", waiting);
            }

            if (_deleteButton != null)
            {
                _deleteButton.style.display = _selectedSiteId is not null
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
        }

        /// <summary>Термины карточки по id карточки ЛИБО по продукту площадки
        /// (дом-план и furniture.hearth = campfire.spot с hearth-вариантом
        /// сводятся к своим карточкам каталога).</summary>
        private static (string NameTerm, string DescriptionTerm) CardTerms(string id)
        {
            if (id == ProjectCardId)
            {
                return ("blueprint.catalog.project.name", "blueprint.catalog.project.description");
            }

            if (id == HutCardId || id == ContentIds.HutPlan)
            {
                return ("blueprint.catalog.hut.name", "blueprint.catalog.hut.description");
            }

            if (BuildCatalogDefinition.TryGet(id, out var entry))
            {
                return (entry.NameTerm, entry.DescriptionTerm);
            }

            return ("blueprint.selection.none", "blueprint.catalog.help");
        }

        private void SetStatus(string term)
        {
            if (_statusLabel != null) _statusLabel.text = Loc.Get(term);
        }

        // ─────────────────────────────────────────────────────────────────
        // Кадр.
        // ─────────────────────────────────────────────────────────────────

        private void Update()
        {
            var ready = _runner != null && _runner.IsReady && _runner.SupportsNpcCommands &&
                        !HutLayoutDesigner.WorldEditorOpen;
            if (_openButton != null)
            {
                _openButton.style.display = ready && !IsOpen ? DisplayStyle.Flex : DisplayStyle.None;
                // Панель выбранного персонажа занимает весь низ — кнопка
                // отъезжает выше её карточки, чтобы не лечь на «Отношения».
                _openButton.style.bottom = NpcSelection.HasSelection ? 306f : 16f;
            }

            var keyboard = Keyboard.current;
            if (ready && keyboard != null && keyboard.bKey.wasPressedThisFrame)
            {
                if (IsOpen) Close();
                else Open();
                return;
            }

            if (!IsOpen) return;
            if (_runner == null || !_runner.IsReady)
            {
                EndBuildMode();
                return;
            }

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                if (_activeCardId.Length > 0)
                {
                    DropActiveCard();
                }
                else if (_selectedSiteId is not null)
                {
                    DeselectSite();
                    RefreshSelectionCard();
                }
                else
                {
                    Close();
                    return;
                }
            }

            RefreshSnapshotCache();
            HandleRotation(keyboard);
            UpdateGhost();
            RefreshGridDots();
            HandlePlacement();
            CheckPendingPlacement();
            UpdateRotateArrows();
            if (Time.unscaledTime >= _nextOverlayRefresh)
            {
                _nextOverlayRefresh = Time.unscaledTime + OverlayInterval;
                RefreshSiteOverlays();
            }
        }

        private void DropActiveCard()
        {
            _activeCardId = string.Empty;
            ClearGhost();
            RebuildCatalogUi();
        }

        private void HandleRotation(Keyboard? keyboard)
        {
            if (keyboard == null) return;
            var delta = 0;
            if (keyboard.qKey.wasPressedThisFrame) delta -= 1;
            if (keyboard.eKey.wasPressedThisFrame) delta += 1;
            if (delta == 0) return;
            Rotate(delta);
        }

        /// <summary>Один шаг поворота ±60° — от Q/E и от мировых стрелок.
        /// Крутится либо ghost под курсором, либо выбранная пустая площадка
        /// (той же командой симуляции, что видит и сервер).</summary>
        private void Rotate(int delta)
        {
            if (_activeCardId.Length > 0)
            {
                _ghostYawSteps = ((_ghostYawSteps + delta) % 6 + 6) % 6;
                ApplyGhostTransform();
                return;
            }

            if (_selectedSiteId is { } siteId && _runner != null)
            {
                _selectedSiteRotation = ((_selectedSiteRotation + delta * 60f) % 360f + 360f) % 360f;
                _runner.EnqueueCommand(new RotateBuildSiteCommand(
                    new HexLive.Simulation.Common.ObjectId(siteId), _selectedSiteRotation));
                // Оверлей «полностью построено» перечитает поворот из snapshot.
                _nextSnapshotRefresh = 0f;
                InvalidateOverlay(siteId);
            }
        }

        private void HandlePlacement()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            var screen = mouse.position.ReadValue();

            if (mouse.rightButton.wasPressedThisFrame && _activeCardId.Length > 0)
            {
                DropActiveCard();
                return;
            }

            if (!mouse.leftButton.wasPressedThisFrame) return;
            if (PointerOverPanel(screen)) return;

            // Без карточки клик по миру — выбор уже размеченной площадки.
            if (_activeCardId.Length == 0)
            {
                TrySelectSite(screen);
                return;
            }

            if (_ghostTile is not { } tile) return;
            if (!_ghostValid)
            {
                SetStatus("build.mode.blocked");
                return;
            }

            // «Свой проект»: клик выбирает якорный гекс и открывает конструктор
            // прямо в мире — команда уйдёт из его кнопки «Построить».
            if (_activeCardId == ProjectCardId)
            {
                OpenBlueprintEditor(tile);
                return;
            }

            var yaw = _ghostYawSteps * 60f;
            if (_activeCardId == HutCardId)
            {
                _runner!.EnqueueCommand(new PlaceBuildingPlanCommand(tile, yaw));
            }
            else
            {
                _runner!.EnqueueCommand(new PlaceFurnitureSiteCommand(_activeCardId, tile, yaw));
            }

            _pendingTile = tile;
            _pendingDeadline = Time.unscaledTime + 1.5f;
            _nextSnapshotRefresh = 0f;
            _nextOverlayRefresh = 0f;
            SetStatus("build.mode.placed");
            // По одному предмету за выбор: поставил — карточка отпускается,
            // можно выбрать поставленное и крутить стрелками.
            DropActiveCard();
        }

        /// <summary>Команда исполнилась только в симуляции; если площадка так и
        /// не появилась — место забраковано, честно говорим об этом вместо
        /// тихого «ничего не произошло». На паузе не проверяем: команда ждёт
        /// первого тика, это не отказ.</summary>
        private void CheckPendingPlacement()
        {
            if (_pendingTile is not { } tile || Time.unscaledTime < _pendingDeadline) return;
            if (_runner != null && _runner.IsPaused)
            {
                _pendingDeadline = Time.unscaledTime + 0.5f;
                return;
            }

            _pendingTile = null;
            if (_snapshot == null) return;
            foreach (var obj in _snapshot.Objects)
            {
                if (obj.Tile.Equals(tile) && !string.IsNullOrEmpty(obj.BuildProduct)) return;
            }

            SetStatus("build.mode.blocked");
        }

        // ─────────────────────────────────────────────────────────────────
        // Выбор размеченной площадки: повернуть стрелками, снять, Esc.
        // ─────────────────────────────────────────────────────────────────

        private void TrySelectSite(Vector2 screen)
        {
            if (_snapshot == null || !TryPickTile(screen, out var tile)) return;
            foreach (var obj in _snapshot.Objects)
            {
                if (string.IsNullOrEmpty(obj.BuildProduct) || !obj.Tile.Equals(tile)) continue;
                if (DeliveredTotal(obj) > 0)
                {
                    // Начатую стройку не крутят и не сносят — только пустую.
                    SetStatus("build.mode.blocked");
                    return;
                }

                _selectedSiteId = obj.Id.Value;
                _selectedSiteProduct = obj.BuildProduct;
                _selectedSiteTile = obj.Tile;
                _selectedSiteRotation = obj.RotationDegrees;
                DrawSelectionOutline(obj.Tile);
                RefreshSelectionCard();
                return;
            }

            DeselectSite();
            RefreshSelectionCard();
        }

        private static int DeliveredTotal(ObjectSnapshot site)
        {
            var delivered = site.DeliveredLogs + site.DeliveredSticks + site.DeliveredRope +
                            site.DeliveredLeaves + site.DeliveredStones + site.DeliveredBoards;
            foreach (var element in site.ArchitectureElements)
            {
                delivered += element.DeliveredTotal;
            }

            return delivered;
        }

        private void DeselectSite()
        {
            _selectedSiteId = null;
            _selectedSiteProduct = string.Empty;
            if (_selectionOutline != null)
            {
                Destroy(_selectionOutline.gameObject);
                _selectionOutline = null;
            }
        }

        private void CancelSelectedSite()
        {
            if (_selectedSiteId is not { } siteId || _runner == null) return;
            _runner.EnqueueCommand(new CancelBuildSiteCommand(
                new HexLive.Simulation.Common.ObjectId(siteId)));
            InvalidateOverlay(siteId);
            DeselectSite();
            _nextSnapshotRefresh = 0f;
            _nextOverlayRefresh = 0f;
            RefreshSelectionCard();
        }

        private void InvalidateOverlay(int siteId)
        {
            if (!_siteOverlays.TryGetValue(siteId, out var overlay)) return;
            if (overlay != null) Destroy(overlay);
            _siteOverlays.Remove(siteId);
        }

        private void DrawSelectionOutline(TileCoord tile)
        {
            if (_selectionOutline == null)
            {
                var go = new GameObject("Build selection outline");
                _selectionOutline = go.AddComponent<LineRenderer>();
                _selectionOutline.useWorldSpace = true;
                _selectionOutline.loop = true;
                _selectionOutline.widthMultiplier = 0.06f;
                _selectionOutline.material = OutlineMaterial();
                _selectionOutline.shadowCastingMode =
                    UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            var center = HexSpatialMath.TileToWorld(tile);
            var y = _worldRenderer != null ? _worldRenderer.GroundTopY(tile) : 0.75f;
            var points = new Vector3[6];
            for (var corner = 0; corner < 6; corner++)
            {
                var angle = (60f * corner + 30f) * Mathf.Deg2Rad;
                points[corner] = new Vector3(
                    center.X + HexSpatialMath.HexRadius * 0.99f * Mathf.Cos(angle),
                    y + 0.04f,
                    center.Y + HexSpatialMath.HexRadius * 0.99f * Mathf.Sin(angle));
            }

            _selectionOutline.positionCount = 6;
            _selectionOutline.SetPositions(points);
            var gold = new Color(0.94f, 0.71f, 0.36f, 0.95f);
            _selectionOutline.startColor = gold;
            _selectionOutline.endColor = gold;
        }

        // ─────────────────────────────────────────────────────────────────
        // Мировые стрелки поворота — экранные кнопки у объекта.
        // ─────────────────────────────────────────────────────────────────

        private void UpdateRotateArrows()
        {
            if (_rotateLeft == null || _rotateRight == null) return;
            Vector3? worldAnchor = null;
            if (_activeCardId.Length > 0 && _ghostRoot != null && _ghostRoot.activeSelf)
            {
                worldAnchor = _ghostRoot.transform.position;
            }
            else if (_selectedSiteId is not null && _worldRenderer != null)
            {
                var center = HexSpatialMath.TileToWorld(_selectedSiteTile);
                worldAnchor = new Vector3(
                    center.X, _worldRenderer.GroundTopY(_selectedSiteTile), center.Y);
            }

            var panel = _document?.rootVisualElement.panel;
            if (worldAnchor is not { } anchor || _camera == null || panel == null)
            {
                _rotateLeft.style.display = DisplayStyle.None;
                _rotateRight.style.display = DisplayStyle.None;
                return;
            }

            var screen = _camera.WorldToScreenPoint(anchor + Vector3.up * 0.6f);
            if (screen.z <= 0f)
            {
                _rotateLeft.style.display = DisplayStyle.None;
                _rotateRight.style.display = DisplayStyle.None;
                return;
            }

            var panelPoint = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(screen.x, Screen.height - screen.y));
            _rotateLeft.style.display = DisplayStyle.Flex;
            _rotateRight.style.display = DisplayStyle.Flex;
            _rotateLeft.style.left = panelPoint.x - 64f;
            _rotateLeft.style.top = panelPoint.y - 18f;
            _rotateRight.style.left = panelPoint.x + 28f;
            _rotateRight.style.top = panelPoint.y - 18f;
        }

        // ─────────────────────────────────────────────────────────────────
        // Snapshot-кэш и клиентская валидация.
        // ─────────────────────────────────────────────────────────────────

        private void RefreshSnapshotCache()
        {
            if (Time.unscaledTime < _nextSnapshotRefresh) return;
            _nextSnapshotRefresh = Time.unscaledTime + SnapshotInterval;
            _snapshot = _runner!.CreateSnapshot();
            _tiles.Clear();
            _occupiedHexes.Clear();
            if (_snapshot == null) return;
            foreach (var tile in _snapshot.Tiles)
            {
                _tiles[tile.Coord] = tile;
            }

            foreach (var obj in _snapshot.Objects)
            {
                // §66: гекс занят стройкой или любым объектом с физическим
                // футпринтом (пальма, валун, станция); россыпь ресурсов гекс
                // не запирает — правду всё равно решит HexFreeForBuild.
                if (!string.IsNullOrEmpty(obj.BuildProduct))
                {
                    _occupiedHexes.Add(obj.Tile);
                    continue;
                }

                if (_runner!.TryGetObjectDefinition(obj.DefinitionId, out var definition) &&
                    definition != null && definition.ObstacleRadius > 0f)
                {
                    _occupiedHexes.Add(obj.Tile);
                    continue;
                }

                // HexFreeForBuild требует СВОБОДНЫЙ центральный junction —
                // кокос, лежащий точно в центре гекса, отклоняет всю площадку.
                // Без этой проверки ghost был зелёным, сим отказывал, и клик
                // «не срабатывал» с первого раза.
                if (_worldRenderer == null) continue;
                var anchor = _worldRenderer.ObjectAnchorPosition(obj);
                var centre = HexSpatialMath.TileToWorld(obj.Tile);
                if (Mathf.Abs(anchor.X - centre.X) < 0.3f &&
                    Mathf.Abs(anchor.Y - centre.Y) < 0.3f)
                {
                    _occupiedHexes.Add(obj.Tile);
                }
            }

            _gridDirty = true;
        }

        private bool TileBuildable(TileCoord coord) =>
            _tiles.TryGetValue(coord, out var tile) &&
            tile.Walkable && !tile.Water && !tile.Blocked &&
            !_occupiedHexes.Contains(coord);

        private bool EvaluateGhostValidity(TileCoord tile)
        {
            if (_activeCardId != HutCardId) return TileBuildable(tile);
            var footprint = Simulation.Bootstrap.BuildingBootstrap.FootprintTiles(
                ContentIds.HutPlan, tile, _ghostYawSteps * 60f);
            return footprint.All(TileBuildable);
        }

        // ─────────────────────────────────────────────────────────────────
        // Ghost.
        // ─────────────────────────────────────────────────────────────────

        private void BuildGhost()
        {
            ClearGhost();
            if (_activeCardId.Length == 0) return;
            _ghostRoot = new GameObject("Build mode ghost");
            _ghostRoot.SetActive(false);

            if (_activeCardId == HutCardId)
            {
                BuildPlanPreview(_ghostRoot.transform, CommittedBuildingPlans.PlayerHut);
            }
            else if (_activeCardId != ProjectCardId)
            {
                var model = GhostModel(_activeCardId);
                if (model != null) model.transform.SetParent(_ghostRoot.transform, false);
            }
            // «Свой проект» — только контур якорного гекса: чертить будет
            // конструктор, показывать нечего.

            var outline = new GameObject("Footprint outline");
            outline.transform.SetParent(_ghostRoot.transform, false);
            _ghostOutline = outline.AddComponent<LineRenderer>();
            _ghostOutline.useWorldSpace = true;
            _ghostOutline.loop = false;
            _ghostOutline.widthMultiplier = 0.05f;
            _ghostOutline.material = OutlineMaterial();
            _ghostOutline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ghostTile = null;
        }

        /// <summary>Готовый вид изделия — те же производственные фабрики, что у
        /// рендерера мира; никакой ghost-геометрии, которой нет в игре.</summary>
        private static GameObject? GhostModel(string catalogId)
        {
            if (catalogId == "furniture.hearth") return HutFurnitureFactory.BuildHearth();
            if (catalogId == ContentIds.Wardrobe) return WardrobeAssembly.BuildFinished();
            return BedAssembly.IsAssembled(catalogId) ? BedAssembly.BuildFinished(catalogId) : null;
        }

        /// <summary>Полная постройка плана — тем же BlueprintPreviewRenderer, что
        /// в конструкторе. Дочерний узел смещает план так, чтобы его якорный
        /// гекс лёг в начало координат родителя: тогда поворот корня крутит дом
        /// вокруг гекса, на который целится игрок, — ровно как повернёт его
        /// CreateHutPlanSite.</summary>
        private static void BuildPlanPreview(Transform parent, BuildingBlueprintDraft plan)
        {
            var planAnchor = BlueprintGeometry.ToWorld(
                BlueprintGeometry.HexCenter(BlueprintBuildingPlan.AnchorTile(plan)));
            var space = new GameObject("Plan space");
            space.transform.SetParent(parent, false);
            space.transform.localPosition = new Vector3(-planAnchor.X, 0f, -planAnchor.Y);
            var preview = space.AddComponent<BlueprintPreviewRenderer>();
            preview.Rebuild(plan, BlueprintEditorMode.Architecture);
            // Сеточный overlay конструктора в мире не нужен — это редакторский
            // слой, а не вид здания.
            var overlay = space.transform.Find("Blueprint grid overlay");
            if (overlay != null) overlay.gameObject.SetActive(false);
            // Architecture-режим превью прячет крышу (редакторская логика
            // «крышу видно только в её категории»); мировой призрак обязан
            // показывать дом ЦЕЛИКОМ — вернуть скрытые секторы.
            var elements = space.transform.Find("Blueprint elements");
            if (elements != null)
            {
                for (var index = 0; index < elements.childCount; index++)
                {
                    var child = elements.GetChild(index).gameObject;
                    if (!child.activeSelf) child.SetActive(true);
                }
            }
        }

        private void UpdateGhost()
        {
            if (_ghostRoot == null || _activeCardId.Length == 0) return;
            var mouse = Mouse.current;
            if (mouse == null || _camera == null) return;
            var screen = mouse.position.ReadValue();
            if (PointerOverPanel(screen) || !TryPickTile(screen, out var tile))
            {
                _ghostRoot.SetActive(false);
                _ghostTile = null;
                return;
            }

            var changed = _ghostTile is not { } current || !current.Equals(tile);
            _ghostTile = tile;
            if (changed || !_ghostRoot.activeSelf)
            {
                _ghostRoot.SetActive(true);
                ApplyGhostTransform();
            }
        }

        private void ApplyGhostTransform()
        {
            if (_ghostRoot == null || _worldRenderer == null || _ghostTile is not { } tile) return;
            var groundY = _worldRenderer.GroundTopY(tile);
            _ghostRoot.transform.position = SimulationUnityMapper.ToUnityTilePosition(tile, groundY);
            var yaw = _ghostYawSteps * 60f;
            _ghostRoot.transform.rotation = Quaternion.Euler(0f, GhostUnityYaw(yaw), 0f);

            _ghostValid = EvaluateGhostValidity(tile);
            DrawFootprintOutline(tile, groundY);
            RefreshSelectionCard();
        }

        /// <summary>Зеркало полного пайплайна рендерера (§120.2): сначала тот же
        /// квант, каким исполнитель проштампует площадку (0 нормализуется в 360 —
        /// НЕ «нетронутый» 0, который вид не вращает), затем архитектурный
        /// footprint — дом, гардероб, домашний очаг — как геометрия (−yaw), а
        /// прочая мебель — character-forward (90−yaw). Иначе ghost нулевого шага
        /// стоит на 90° иначе, чем встанет построенное.</summary>
        private float GhostUnityYaw(float simYaw)
        {
            var quantized = StampedHexYaw(simYaw);
            var footprint = _activeCardId == HutCardId ||
                            _activeCardId == ContentIds.Wardrobe ||
                            _activeCardId == "furniture.hearth";
            return footprint
                ? SimulationUnityMapper.ToUnityFootprintYawDegrees(quantized)
                : SimulationUnityMapper.ToUnityYawDegrees(quantized);
        }

        /// <summary>Зеркало internal <c>StructurePlacement.QuantizeHexYaw</c>:
        /// шесть симметрий 0+60k, и ноль нормализуется в 360 — именно это число
        /// исполнитель штампует в RotationDegrees площадки.</summary>
        private static float StampedHexYaw(float degrees)
        {
            var d = degrees % 360f;
            if (d < 0f) d += 360f;
            var step = (int)Mathf.Floor((d + 30f) / 60f) % 6;
            var stamped = step * 60f;
            return stamped <= 0f ? 360f : stamped;
        }

        private void DrawFootprintOutline(TileCoord tile, float groundY)
        {
            if (_ghostOutline == null) return;
            var tiles = _activeCardId == HutCardId
                ? Simulation.Bootstrap.BuildingBootstrap.FootprintTiles(
                    ContentIds.HutPlan, tile, _ghostYawSteps * 60f)
                : new[] { tile };

            // Один LineRenderer на все гексы: контуры сшиты через «подъём пера»
            // дублированной вершиной — для превью этого достаточно.
            var points = new List<Vector3>();
            foreach (var footprintTile in tiles)
            {
                var center = HexSpatialMath.TileToWorld(footprintTile);
                var y = _worldRenderer != null ? _worldRenderer.GroundTopY(footprintTile) : groundY;
                for (var corner = 0; corner <= 6; corner++)
                {
                    var angle = (60f * corner + 30f) * Mathf.Deg2Rad;
                    points.Add(new Vector3(
                        center.X + HexSpatialMath.HexRadius * 0.98f * Mathf.Cos(angle),
                        y + 0.03f,
                        center.Y + HexSpatialMath.HexRadius * 0.98f * Mathf.Sin(angle)));
                }
            }

            _ghostOutline.positionCount = points.Count;
            _ghostOutline.SetPositions(points.ToArray());
            var color = _ghostValid
                ? new Color(0.37f, 0.77f, 0.42f, 0.9f)
                : new Color(0.91f, 0.34f, 0.31f, 0.9f);
            _ghostOutline.startColor = color;
            _ghostOutline.endColor = color;
        }

        private Material? _outlineMaterial;

        private Material OutlineMaterial()
        {
            if (_outlineMaterial != null) return _outlineMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _outlineMaterial = new Material(shader) { name = "BuildGhostOutline" };
            return _outlineMaterial;
        }

        /// <summary>§120.8: конструктор поверх выбранного гекса. Лоток
        /// закрывается; «Построить» в конструкторе шлёт чертёж мировой
        /// командой — тот же путь, что и на сервере.</summary>
        private void OpenBlueprintEditor(TileCoord anchorTile)
        {
            var runner = _runner;
            var worldRenderer = _worldRenderer;
            if (runner == null || worldRenderer == null) return;
            var anchorWorld = SimulationUnityMapper.ToUnityTilePosition(
                anchorTile, worldRenderer.GroundTopY(anchorTile));
            EndBuildMode();

            HutLayoutDesigner.PendingWorldPlacement = new HutLayoutDesigner.WorldPlacementConfig
            {
                AnchorWorldPosition = anchorWorld,
                AnchorTile = anchorTile,
                OnBuild = draft =>
                {
                    // Сим кладёт AnchorTile чертежа на тайл команды; черчение
                    // шло от выбранного гекса как (0,0) — сложить смещения.
                    var draftAnchor = BlueprintBuildingPlan.AnchorTile(draft);
                    var target = new TileCoord(
                        anchorTile.Q + draftAnchor.Q, anchorTile.R + draftAnchor.R);
                    runner.EnqueueCommand(new PlaceBuildingBlueprintCommand(
                        BuildingBlueprintJson.Serialize(draft, pretty: false),
                        target, rotationDegrees: 0f));
                },
                OnClosed = null
            };

            var editorRoot = new GameObject("HexLive Blueprint Editor");
            editorRoot.AddComponent<UIDocument>();
            editorRoot.AddComponent<HutLayoutDesigner>();
        }

        private void ClearGhost()
        {
            if (_ghostRoot != null) Destroy(_ghostRoot);
            _ghostRoot = null;
            _ghostOutline = null;
            _ghostTile = null;
            _ghostValid = false;
            ClearGridDots();
        }

        // ─────────────────────────────────────────────────────────────────
        // Сетка «можно/нельзя»: зелёные и красные точки на центрах гексов,
        // пока выбрана карточка, — как overlay-слой конструктора (§120.0).
        // ─────────────────────────────────────────────────────────────────

        private void RefreshGridDots()
        {
            if (_activeCardId.Length == 0)
            {
                ClearGridDots();
                return;
            }

            if (_gridRoot == null && _tiles.Count > 0) BuildGridDots();
            if (!_gridDirty || _gridRoot == null) return;
            _gridDirty = false;
            foreach (var (renderer, tile) in _gridDots)
            {
                if (renderer == null) continue;
                renderer.sharedMaterial = TileBuildable(tile) ? DotFree() : DotBusy();
            }
        }

        private void BuildGridDots()
        {
            ClearGridDots();
            _gridRoot = new GameObject("Build grid dots");
            foreach (var pair in _tiles)
            {
                if (pair.Value.Water || !pair.Value.Walkable) continue;
                var centre = HexSpatialMath.TileToWorld(pair.Key);
                var y = _worldRenderer != null ? _worldRenderer.GroundTopY(pair.Key) : 0.75f;
                var dot = new GameObject("Dot");
                dot.transform.SetParent(_gridRoot.transform, false);
                dot.transform.position = new Vector3(centre.X, y + 0.025f, centre.Y);
                dot.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                dot.transform.localScale = Vector3.one * 0.16f;
                var filter = dot.AddComponent<MeshFilter>();
                filter.sharedMesh = DotMesh();
                var renderer = dot.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = TileBuildable(pair.Key) ? DotFree() : DotBusy();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _gridDots.Add((renderer, pair.Key));
            }
        }

        private void ClearGridDots()
        {
            if (_gridRoot != null) Destroy(_gridRoot);
            _gridRoot = null;
            _gridDots.Clear();
        }

        private Mesh DotMesh()
        {
            if (_dotMesh != null) return _dotMesh;
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _dotMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            Destroy(primitive);
            return _dotMesh;
        }

        private Material DotFree() => _dotFree ??= DotMaterial(
            "BuildGridDotFree", new Color(0.37f, 0.77f, 0.42f, 0.85f));

        private Material DotBusy() => _dotBusy ??= DotMaterial(
            "BuildGridDotBusy", new Color(0.91f, 0.34f, 0.31f, 0.7f));

        private static Material DotMaterial(string name, Color color)
        {
            var shader = Shader.Find("HexLive/BlueprintOverlay")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }

        // ─────────────────────────────────────────────────────────────────
        // «Полностью построено» поверх размеченных площадок.
        // ─────────────────────────────────────────────────────────────────

        private void RefreshSiteOverlays()
        {
            if (_snapshot == null || _worldRenderer == null) return;
            // §120.8: модульные объекты plan-площадок — источник геометрии
            // призрака «полностью построено»: чертёж клиенту не нужен.
            var modulesByOwner = new Dictionary<int, List<ObjectSnapshot>>();
            foreach (var obj in _snapshot.Objects)
            {
                if (obj.ArchitectureOwnerObjectId is not { } ownerId ||
                    obj.ArchitectureElements.Count != 1)
                {
                    continue;
                }

                if (!modulesByOwner.TryGetValue(ownerId, out var list))
                {
                    list = new List<ObjectSnapshot>();
                    modulesByOwner[ownerId] = list;
                }

                list.Add(obj);
            }

            var seen = new HashSet<int>();
            foreach (var obj in _snapshot.Objects)
            {
                if (string.IsNullOrEmpty(obj.BuildProduct)) continue;
                seen.Add(obj.Id.Value);
                if (_siteOverlays.ContainsKey(obj.Id.Value)) continue;
                modulesByOwner.TryGetValue(obj.Id.Value, out var modules);
                var overlay = BuildSiteOverlay(obj, modules);
                if (overlay != null) _siteOverlays[obj.Id.Value] = overlay;
            }

            List<int>? stale = null;
            foreach (var pair in _siteOverlays)
            {
                if (seen.Contains(pair.Key)) continue;
                stale ??= new List<int>();
                stale.Add(pair.Key);
            }

            if (stale == null) return;
            foreach (var id in stale)
            {
                if (_siteOverlays[id] != null) Destroy(_siteOverlays[id]);
                _siteOverlays.Remove(id);
            }
        }

        private GameObject? BuildSiteOverlay(ObjectSnapshot site, List<ObjectSnapshot>? modules)
        {
            var anchor = _worldRenderer!.ObjectAnchorPosition(site);
            var groundY = _worldRenderer.GroundTopY(site.Tile);
            var root = new GameObject($"Build preview (site {site.Id.Value} {site.BuildProduct})");

            if (site.BuildProduct == ContentIds.HutPlan)
            {
                // Дом собирается из СВОИХ модулей (произвольный чертёж §120.8
                // приходит теми же модульными объектами, что и committed-план);
                // каждый модуль — полная авторская модель, как её поставит
                // ArchitectureModuleView по завершении.
                root.transform.position = SimulationUnityMapper.ToUnityPosition(anchor, groundY);
                if (modules == null || modules.Count == 0)
                {
                    Destroy(root);
                    return null;
                }

                var frame = Quaternion.Euler(
                    0f, SimulationUnityMapper.ToUnityFootprintYawDegrees(site.RotationDegrees), 0f);
                foreach (var module in modules)
                {
                    var element = module.ArchitectureElements[0];
                    var localPosition = frame * new Vector3(
                        element.LocalX,
                        BlueprintArchitectureFactory.ModuleLift(element.DefinitionId),
                        element.LocalZ);
                    var wrapper = BlueprintArchitectureFactory.InstantiateModel(
                        element.DefinitionId, localPosition,
                        frame * BlueprintArchitectureFactory.ModuleRotation(
                            element.DefinitionId, element.LocalYaw));
                    if (wrapper != null) wrapper.transform.SetParent(root.transform, false);
                }

                return root;
            }

            var isHearth = site.Variant == Simulation.Runtime.BuildingRules.HutHearthVariant;
            var model = isHearth
                ? HutFurnitureFactory.BuildHearth()
                : GhostModel(site.BuildProduct);
            if (model == null)
            {
                Destroy(root);
                return null;
            }

            model.transform.SetParent(root.transform, false);
            // Чуть выше площадки — доставленные детали рисуются в тех же
            // координатах, и без сдвига готовый призрак с ними z-fight'ится.
            root.transform.position = SimulationUnityMapper.ToUnityPosition(anchor, groundY + 0.004f);
            if (site.RotationDegrees != 0f)
            {
                // Мебель плана стоит не в центре гекса — её якорь junction
                // выдаёт архитектурную симметрию (−yaw), как у рендерера.
                var centre = HexSpatialMath.TileToWorld(site.Tile);
                var offCentre =
                    Mathf.Abs(anchor.X - centre.X) > 0.05f || Mathf.Abs(anchor.Y - centre.Y) > 0.05f;
                var footprint = offCentre || isHearth ||
                                site.BuildProduct == ContentIds.Wardrobe;
                root.transform.rotation = Quaternion.Euler(0f, footprint
                    ? SimulationUnityMapper.ToUnityFootprintYawDegrees(site.RotationDegrees)
                    : SimulationUnityMapper.ToUnityYawDegrees(site.RotationDegrees), 0f);
            }

            return root;
        }

        private void ClearOverlays()
        {
            foreach (var pair in _siteOverlays)
            {
                if (pair.Value != null) Destroy(pair.Value);
            }

            _siteOverlays.Clear();
        }

        // ─────────────────────────────────────────────────────────────────
        // Указатель.
        // ─────────────────────────────────────────────────────────────────

        private bool PointerOverPanel(Vector2 screen)
        {
            if (_document?.rootVisualElement.panel == null) return false;
            var panelPosition = RuntimePanelUtils.ScreenToPanel(
                _document.rootVisualElement.panel, new Vector2(screen.x, Screen.height - screen.y));
            if (_panel != null && _panel.resolvedStyle.display == DisplayStyle.Flex &&
                _panel.worldBound.Contains(panelPosition))
            {
                return true;
            }

            return false;
        }

        /// <summary>Тайл под курсором — то же «пересечение с плоскостью верха
        /// каждого тайла», что у ручного приказа §121: коллайдеров у земли нет,
        /// высоты у тайлов разные.</summary>
        private bool TryPickTile(Vector2 screen, out TileCoord tile)
        {
            tile = default;
            if (_camera == null || _tiles.Count == 0) return false;
            var ray = _camera.ScreenPointToRay(screen);
            if (Mathf.Abs(ray.direction.y) < 0.0001f) return false;

            var bestT = float.PositiveInfinity;
            var found = false;
            foreach (var pair in _tiles)
            {
                var topY = SimulationUnityMapper.TileHeight + pair.Value.Elevation * 0.55f;
                var t = (topY - ray.origin.y) / ray.direction.y;
                if (t <= 0f || t >= bestT) continue;
                var hit = ray.origin + ray.direction * t;
                if (!PointInsideHex(hit.x, hit.z, pair.Key)) continue;
                bestT = t;
                tile = pair.Key;
                found = true;
            }

            return found;
        }

        private static bool PointInsideHex(float x, float z, TileCoord coord)
        {
            var center = HexSpatialMath.TileToWorld(coord);
            var dx = Mathf.Abs(x - center.X);
            var dz = Mathf.Abs(z - center.Y);
            var radius = HexSpatialMath.HexRadius;
            return dz <= radius &&
                   HexSpatialMath.Sqrt3 * dx + dz <= HexSpatialMath.Sqrt3 * radius;
        }

        private void LateUpdate()
        {
            // Стройка держит мировой указатель: пока лоток открыт, клик по
            // миру не должен выбирать NPC даже при неудачном подавлении.
            if (IsOpen) NpcSelection.PointerOverUi = true;
        }
    }
}
