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
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Views;
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
        private const string PanelResource = "HexLive/UI/HutConstructor/HutConstructorPanel";
        private const string DraftId = "hut_constructor_autosave";
        private const float PickRadius = 0.34f;
        private readonly BlueprintCommandHistory _history = new();
        private readonly BlueprintDraftStore _store = new();
        private readonly HashSet<string> _selectedIds = new();
        private readonly Dictionary<Tool, string> _toolButtonNames = new()
        {
            [Tool.Select] = "tool-select",
            [Tool.Room] = "tool-room",
            [Tool.Wall] = "tool-wall",
            [Tool.Window] = "tool-window",
            [Tool.Door] = "tool-door",
            [Tool.Support] = "tool-support",
            [Tool.Floor] = "tool-floor",
            [Tool.Roof] = "tool-roof",
            [Tool.Bed] = "tool-bed",
            [Tool.Hearth] = "tool-hearth",
            [Tool.Wardrobe] = "tool-wardrobe"
        };

        private UIDocument? _document;
        private VisualElement? _panel;
        private Label? _statusLabel;
        private Label? _selectionLabel;
        private SimulationRunnerBehaviour? _runner;
        private Camera? _camera;
        private Transform? _source;
        private BlueprintPreviewRenderer? _preview;
        private Transform? _handles;
        private BuildingBlueprintDraft _draft = null!;
        private BlueprintEditorMode _mode = BlueprintEditorMode.Rooms;
        private Tool _tool = Tool.Select;
        private string _selectedId = string.Empty;
        private int _selectedRoomId;
        private bool _gestureActive;
        private HexBuildNodeKey _wallStart;
        private FloorSectorKey _roomStart;
        private string _dragFurnitureId = string.Empty;
        private string _dragOpeningId = string.Empty;
        private BlueprintRoomResizeHandle? _roomResizeHandle;
        private BuildingBlueprintDraft? _gesturePreview;
        private bool _initialized;
        private float _autosaveDeadline;
        private string _statusKey = "blueprint.status.ready";

        private enum Tool
        {
            Select,
            Room,
            Wall,
            Window,
            Door,
            Support,
            Floor,
            Roof,
            Bed,
            Hearth,
            Wardrobe
        }

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            ConfigurePanelSettings();
            BuildUi();
            Loc.LanguageChanged += ApplyLanguage;
        }

        private void Start()
        {
            _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            _camera = Camera.main;
            if (!_store.TryLoad(DraftId, out _draft, out var error))
            {
                _draft = BuiltInBuildingBlueprints.Hut1Hex();
                _draft.BlueprintId = DraftId;
                if (!string.IsNullOrEmpty(error)) Debug.LogWarning($"[BlueprintEditor] {error}", this);
            }
            ApplyLanguage();
        }

        private void OnDestroy()
        {
            Loc.LanguageChanged -= ApplyLanguage;
        }

        private void Update()
        {
            if (!_initialized) InitializePreview();
            if (!_initialized || _preview == null || _camera == null) return;
            if (_runner != null && !_runner.IsPaused) _runner.TogglePause();
            HandleKeyboard();
            HandlePointer();
            _preview.UpdateCutaway(_camera);
            if (_autosaveDeadline > 0f && Time.unscaledTime >= _autosaveDeadline)
            {
                _autosaveDeadline = 0f;
                SaveDraft(false);
            }
        }

        private void InitializePreview()
        {
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
            var sheet = Resources.Load<StyleSheet>(PanelResource);
            if (tree == null || sheet == null)
            {
                Debug.LogError("[BlueprintEditor] UI Toolkit resources are missing.", this);
                return;
            }
            tree.CloneTree(root);
            root.styleSheets.Add(sheet);
            _panel = root.Q("constructor-panel");
            if (_panel != null) _panel.pickingMode = PickingMode.Position;
            _statusLabel = root.Q<Label>("status-label");
            _selectionLabel = root.Q<Label>("selection-label");

            BindMode(root, "mode-rooms", BlueprintEditorMode.Rooms);
            BindMode(root, "mode-architecture", BlueprintEditorMode.Architecture);
            BindMode(root, "mode-furniture", BlueprintEditorMode.Furniture);
            BindMode(root, "mode-roof", BlueprintEditorMode.Roof);
            foreach (var pair in _toolButtonNames)
            {
                var captured = pair.Key;
                root.Q<Button>(pair.Value).clicked += () => SetTool(captured);
            }
            root.Q<Button>("rotate-left").clicked += () => RotateSelected(-1);
            root.Q<Button>("rotate-right").clicked += () => RotateSelected(1);
            root.Q<Button>("delete-selection").clicked += DeleteSelected;
            root.Q<Button>("undo").clicked += Undo;
            root.Q<Button>("redo").clicked += Redo;
            root.Q<Button>("save").clicked += () => SaveDraft(true);
            root.Q<Button>("export").clicked += ExportDraft;
            RefreshUiState();
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
                case Tool.Select:
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
                var floor = NearestSector(local);
                var sector = new RoofSectorKey(floor.Hex, floor.Sector);
                result = Execute(working => BlueprintEditorCommands.AddRoofSector(working, sector));
            }
            else if (_tool is Tool.Bed or Tool.Hearth or Tool.Wardrobe)
            {
                var (tile, slot) = NearestJunction(local);
                var definition = _tool == Tool.Bed ? ContentIds.BedBasic :
                    _tool == Tool.Hearth ? "furniture.hearth" : "furniture.wardrobe";
                result = Execute(working => BlueprintEditorCommands.PlaceFurniture(working, definition, tile, slot));
                if (result.Succeeded)
                {
                    _selectedId = _draft.Furniture.Last().Id;
                    _selectedIds.Clear();
                    _selectedIds.Add(_selectedId);
                    RebuildPreview();
                }
            }
            if (result != null)
            {
                if (!result.Succeeded) ShowCommandPreview(result);
                SetStatus(result.Message);
            }
        }

        private BlueprintCommandResult Execute(Func<BuildingBlueprintDraft, BlueprintCommandResult> gesture)
        {
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
            var candidateId = string.Empty;
            var candidateRoomId = 0;
            var best = PickRadius * PickRadius;
            foreach (var item in _draft.Furniture)
            {
                var point = BlueprintGeometry.JunctionToWorld(item.PrimaryJunction);
                var sq = Sqr(local.x - point.X, local.z - point.Y);
                if (sq >= best) continue;
                best = sq;
                candidateId = item.Id;
            }
            foreach (var element in _draft.Elements)
            {
                var point = ElementCenter(element);
                var sq = Sqr(local.x - point.x, local.z - point.z);
                if (sq >= best) continue;
                best = sq;
                candidateId = element.Id;
                candidateRoomId = element.Kind == BlueprintElementKind.FloorSector ? element.RoomId : 0;
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

        private void RotateSelected(int delta)
        {
            if (_selectedIds.Count > 1 || !_draft.Furniture.Any(item => item.Id == _selectedId)) return;
            var result = Execute(working => BlueprintEditorCommands.Rotate(working, _selectedId, delta));
            SetStatus(result.Message);
        }

        private void DeleteSelected()
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
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
            _preview.Rebuild(_draft, _mode, _selectedId, selectedIds: _selectedIds);
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
                junctionConflicts, buildConflicts, changed, invalid, _selectedIds);
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
                .SelectMany(element => BlueprintGeometry.RoofSupports(element.RoofSector))
                .Where(node => !supports.Contains(node)).Distinct().ToArray();
        }

        private void RebuildHandles()
        {
            if (_preview == null) return;
            if (_handles != null) Destroy(_handles.gameObject);
            if (string.IsNullOrEmpty(_selectedId)) return;
            var selectedFurniture = _draft.Furniture.FirstOrDefault(item => item.Id == _selectedId);
            if (selectedFurniture == null)
            {
                if (_selectedRoomId > 0) BuildRoomResizeHandles();
                return;
            }
            var point = BlueprintGeometry.JunctionToWorld(selectedFurniture.PrimaryJunction);
            _handles = new GameObject("Selection handles").transform;
            _handles.SetParent(_preview.transform, false);
            _handles.localPosition = new Vector3(point.X, HutAssembly.FloorSurfaceLift + 0.08f, point.Y);
            AddHandle("drag-handle", Vector3.zero, new Color(1f, 0.63f, 0.20f), 0.11f);
            AddRotationArc("rotate-left-handle", -1, -155f, -25f);
            AddRotationArc("rotate-right-handle", 1, 25f, 155f);
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

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"room-resize-{side.Key}";
                go.transform.SetParent(_handles, false);
                go.transform.localPosition = midpoint + outward * 0.14f + Vector3.up *
                    (HutAssembly.FloorSurfaceLift + 0.07f);
                go.transform.localRotation = Quaternion.LookRotation(outward, Vector3.up);
                go.transform.localScale = new Vector3(0.15f, 0.08f, 0.28f);
                go.GetComponent<Renderer>().material.color = new Color(1f, 0.63f, 0.20f);
                var handle = go.AddComponent<BlueprintRoomResizeHandle>();
                handle.RoomId = _selectedRoomId;
                handle.Outward = outward;
                handle.AddSectors = candidates.ToArray();
                handle.RemoveSectors = owners;
            }
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

        private void AddRotationArc(string name, int direction, float from, float to)
        {
            if (_handles == null) return;
            var go = new GameObject(name);
            go.transform.SetParent(_handles, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = 14;
            line.startWidth = 0.025f;
            line.endWidth = 0.025f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = line.endColor = new Color(1f, 0.63f, 0.20f);
            for (var index = 0; index < line.positionCount; index++)
            {
                var t = index / (line.positionCount - 1f);
                var radians = Mathf.Lerp(from, to, t) * Mathf.Deg2Rad;
                line.SetPosition(index, new Vector3(Mathf.Cos(radians) * 0.38f, 0f, Mathf.Sin(radians) * 0.38f));
            }
            AddHandle(name + "-tip", line.GetPosition(line.positionCount - 1),
                new Color(1f, 0.63f, 0.20f), 0.09f);
            go.AddComponent<BlueprintRotationHandle>().Direction = direction;
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
                var direction = name.Contains("rotate-left", StringComparison.Ordinal) ? -1 :
                    name.Contains("rotate-right", StringComparison.Ordinal) ? 1 : 0;
                if (direction == 0) continue;
                var markerScreen = _camera.WorldToScreenPoint(marker.position);
                if ((new Vector2(markerScreen.x, markerScreen.y) - screen).sqrMagnitude > 28f * 28f) continue;
                RotateSelected(direction);
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

        private void BindMode(VisualElement root, string name, BlueprintEditorMode mode)
        {
            root.Q<Button>(name).clicked += () =>
            {
                _mode = mode;
                _tool = mode switch
                {
                    BlueprintEditorMode.Rooms => Tool.Room,
                    BlueprintEditorMode.Architecture => Tool.Wall,
                    BlueprintEditorMode.Furniture => Tool.Select,
                    BlueprintEditorMode.Roof => Tool.Roof,
                    _ => Tool.Select
                };
                CancelGesture();
                RefreshUiState();
            };
        }

        private void SetTool(Tool tool)
        {
            _tool = tool;
            _mode = tool switch
            {
                Tool.Room or Tool.Floor => BlueprintEditorMode.Rooms,
                Tool.Wall or Tool.Window or Tool.Door or Tool.Support => BlueprintEditorMode.Architecture,
                Tool.Bed or Tool.Hearth or Tool.Wardrobe => BlueprintEditorMode.Furniture,
                Tool.Select => _mode,
                Tool.Roof => BlueprintEditorMode.Roof,
                _ => _mode
            };
            CancelGesture();
            RefreshUiState();
        }

        private void RefreshUiState()
        {
            if (_document == null) return;
            var root = _document.rootVisualElement;
            SetActive(root.Q("mode-rooms"), _mode == BlueprintEditorMode.Rooms);
            SetActive(root.Q("mode-architecture"), _mode == BlueprintEditorMode.Architecture);
            SetActive(root.Q("mode-furniture"), _mode == BlueprintEditorMode.Furniture);
            SetActive(root.Q("mode-roof"), _mode == BlueprintEditorMode.Roof);
            foreach (var pair in _toolButtonNames) SetActive(root.Q(pair.Value), pair.Key == _tool);

            root.Q("tool-room").style.display = _mode == BlueprintEditorMode.Rooms ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q("tool-floor").style.display = _mode == BlueprintEditorMode.Rooms ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var name in new[] { "tool-wall", "tool-window", "tool-door", "tool-support" })
                root.Q(name).style.display = _mode == BlueprintEditorMode.Architecture ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q("tool-select").style.display = DisplayStyle.Flex;
            foreach (var name in new[] { "tool-bed", "tool-hearth", "tool-wardrobe" })
                root.Q(name).style.display = _mode == BlueprintEditorMode.Furniture ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q("tool-roof").style.display = _mode == BlueprintEditorMode.Roof ? DisplayStyle.Flex : DisplayStyle.None;

            root.Q<Button>("undo").SetEnabled(_history.CanUndo);
            root.Q<Button>("redo").SetEnabled(_history.CanRedo);
            var selected = _draft?.Furniture.FirstOrDefault(item => item.Id == _selectedId);
            if (_selectionLabel != null)
                _selectionLabel.text = _selectedIds.Count > 1
                    ? $"{Loc.Get("blueprint.selection")}: {_selectedIds.Count}"
                    : selected != null
                    ? $"{Loc.Get("blueprint.selection")}: {Loc.Get("blueprint.item." + ItemToken(selected.DefinitionId))} · {selected.YawStep * 60}°"
                    : _selectedRoomId > 0
                        ? $"{Loc.Get("blueprint.room")} #{_selectedRoomId} · " +
                          Loc.Get(BlueprintValidator.IsIndoorRoom(_draft, _selectedRoomId)
                              ? "blueprint.indoor.ready"
                              : "blueprint.indoor.incomplete")
                        : Loc.Get("blueprint.selection.none");
            root.Q<Button>("rotate-left").SetEnabled(selected != null && _selectedIds.Count <= 1);
            root.Q<Button>("rotate-right").SetEnabled(selected != null && _selectedIds.Count <= 1);
            root.Q<Button>("delete-selection").SetEnabled(!string.IsNullOrEmpty(_selectedId));
        }

        private void ApplyLanguage()
        {
            if (_document == null) return;
            var root = _document.rootVisualElement;
            Text<Label>(root, "title-label", "blueprint.title");
            Text<Button>(root, "mode-rooms", "blueprint.mode.rooms");
            Text<Button>(root, "mode-architecture", "blueprint.mode.architecture");
            Text<Button>(root, "mode-furniture", "blueprint.mode.furniture");
            Text<Button>(root, "mode-roof", "blueprint.mode.roof");
            foreach (var pair in _toolButtonNames)
                Text<Button>(root, pair.Value, "blueprint.tool." + pair.Key.ToString().ToLowerInvariant());
            Text<Button>(root, "delete-selection", "blueprint.action.delete");
            Text<Button>(root, "undo", "blueprint.action.undo");
            Text<Button>(root, "redo", "blueprint.action.redo");
            Text<Button>(root, "save", "blueprint.action.save");
            Text<Button>(root, "export", "blueprint.action.export");
            Text<Label>(root, "shortcut-label", "blueprint.shortcuts");
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

        private static void SetActive(VisualElement? element, bool active)
        {
            if (element == null) return;
            if (active) element.AddToClassList("active");
            else element.RemoveFromClassList("active");
        }

        private static string ItemToken(string definitionId) => definitionId switch
        {
            ContentIds.BedBasic => "bed",
            "furniture.hearth" => "hearth",
            "furniture.wardrobe" => "wardrobe",
            _ => "furniture"
        };

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
