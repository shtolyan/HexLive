#nullable enable
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Social;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Views;
using UnityEngine;
// Unity 6 завела свой UnityEngine.EntityId, и `using UnityEngine` делает имя
// неоднозначным. Алиас — та же развязка, что в AbuseTestBootstrap.
using EntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.UnityPresentation.Input
{

/// <summary>
/// §121: мышь в ручном режиме. Висит на камере рядом с
/// <see cref="RtsCameraController"/>, который отдаёт сюда левый клик ДО своей
/// обычной обработки — пока выбранной колонисткой управляет игрок, тот же клик
/// значит другое.
///
/// Что делает: подсвечивает то, на что наведён курсор; по клику открывает меню
/// действий (объект — его взаимодействия из каталога; человек или зверь —
/// «атаковать/выбрать»); по клику в пустую землю отдаёт приказ идти.
///
/// §121.11 (bug #294): ОДИН клик — всегда приказ идти. Жеста-темпа (двойного
/// клика на бег) здесь больше нет: темп — постоянная настройка персонажа,
/// тумблер «шагом/бегом» в карточке, и решает её симуляция.
///
/// ⭐ Ручной режим читается ИЗ СНАПШОТА, а не из своего поля. Тумблер живёт в
/// симуляции, и она же авторитет: кнопка, помнящая своё, рано или поздно
/// показывает одно, пока персонаж делает другое.
/// </summary>
public sealed partial class SimulationInputAdapter : MonoBehaviour
{
    [SerializeField] private SimulationRunnerBehaviour? _runner;

    // Тот же радиус, которым камера выбирает NPC: два разных числа значили бы,
    // что подсветилось одно, а кликнулось другое. После §121.1 r2 применяется
    // только к зверям и к людям БЕЗ готового вида (первые кадры после спавна,
    // примитивы прототипных сцен) — у остальных был честный точный луч.
    private const float PickRadiusPixels = 70f;
    // §121.7 / #728: «забыли про неё» решает ВНИМАНИЕ игрока, а не одни приказы.
    // Пока выделенная ручная колонистка у игрока под рукой — он двигает мышь,
    // жмёт клавиши или держит открытым её окно (лут, инвентарь, меню) — клиент
    // раз в две секунды шлёт идемпотентный SetManualControl(true), и симуляция
    // продлевает окно простоя. Снял выделение или отошёл от компьютера —
    // сердцебиение молчит, и через ManualIdleReleaseSeconds её забирает ИИ.
    private const float PresenceHeartbeatSeconds = 2f;
    private const float PresenceAttentionSeconds = 4f;
    private float _attentionAt = float.NegativeInfinity;
    private float _presenceSentAt = float.NegativeInfinity;

    private Camera? _camera;
    private HexWorldRenderer? _worldRenderer;
    private WorldObjectView? _hovered;
    private int _hoveredNpcId = -1;
    private int _hoveredMobId = -1;
    private bool _hasHoveredMob;
    private Renderer[]? _hoveredMobRenderers;

    private readonly List<ContextMenuEntry> _entries = new();
    private readonly List<int> _selectedColonyIds = new();
    private readonly List<int> _manualSelectedIds = new();
    private readonly Dictionary<TileCoord, List<PlantingJunction>>
        _plantingJunctionsByTile = new();
    private readonly HashSet<JunctionId> _plantingObjectJunctions = new();
    private readonly HashSet<JunctionId> _furnitureBlockedJunctions = new();

    private int _plantingActorId = -1;
    private int _plantingSourceIndex = -1;
    private string _plantingSeedDefinitionId = string.Empty;
    private JunctionId? _plantingCandidateJunction;
    private TileCoord _plantingCandidateTile;
    private Float2 _plantingCandidatePosition;
    private bool _plantingCandidateValid;
    private GameObject? _furniturePlacementPreview;
    private int _furnitureYawSteps;
    private int? _furnitureBlockedStamp;
    private bool IsPlacingFurniture => PortableFurniture.CanCarry(_plantingSeedDefinitionId);

    private readonly struct PlantingJunction
    {
        public PlantingJunction(JunctionId id, Float2 position)
        {
            Id = id;
            Position = position;
        }

        public JunctionId Id { get; }
        public Float2 Position { get; }
    }

    public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

    /// <summary>
    /// §173.2: inventory «Plant» enters a short-lived placement mode. The
    /// physical inventory index and expected id are retained together so a
    /// later inventory shuffle cannot silently plant a different item.
    /// </summary>
    public bool BeginPlantingPlacement(
        int actorId, int sourceIndex, string seedDefinitionId)
    {
        if (_runner == null)
        {
            _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
        }

        var snapshot = _runner != null && _runner.IsReady &&
            _runner.SupportsNpcCommands
                ? _runner.CreateSnapshot()
                : null;
        var actor = snapshot?.Npcs.FirstOrDefault(candidate =>
            candidate.Id.Value == actorId);
        if (actor == null || !_runner!.CanControlNpc(actor.Id) ||
            !HasCarriedSeedAtSource(actor, sourceIndex, seedDefinitionId) ||
            (!InventoryPlacementMath.CanPlace(seedDefinitionId) && !PortableFurniture.CanCarry(seedDefinitionId)))
        {
            return false;
        }

        CancelPlantingPlacement();
        foreach (var junction in snapshot!.Junctions)
        {
            foreach (var tile in junction.Tiles)
            {
                if (!_plantingJunctionsByTile.TryGetValue(tile, out var candidates))
                {
                    candidates = new List<PlantingJunction>();
                    _plantingJunctionsByTile.Add(tile, candidates);
                }

                candidates.Add(new PlantingJunction(junction.Id, junction.WorldPosition));
            }
        }

        _plantingActorId = actorId;
        _plantingSourceIndex = sourceIndex;
        _plantingSeedDefinitionId = seedDefinitionId;
        _plantingCandidateJunction = null;
        _plantingCandidateValid = false;
        ContextMenuPanel.Close();
        PlantingPlacementMarker.Hide();
        return true;
    }

    /// <summary>Returns true when an active planting placement was cancelled.</summary>
    public bool TryCancelPlantingPlacement()
    {
        if (_plantingActorId < 0)
        {
            return false;
        }

        CancelPlantingPlacement();
        return true;
    }

    /// <summary>Кем сейчас управляет игрок, или -1.</summary>
    public int ManualNpcId { get; private set; } = -1;

    /// <summary>
    /// Единственная выделенная живая колонистка — исполнительница приказов из
    /// контекстного меню, НЕЗАВИСИМО от тумблера 🎮: приказ сам берёт
    /// управление (см. <see cref="EnsureManual"/>). -1, если выделено не одно.
    /// </summary>
    private int OrderNpcId => _selectedColonyIds.Count == 1 ? _selectedColonyIds[0] : -1;

    // Режим команд = среди выделенных есть хотя бы одна ручная (🎮). В чистом
    // AI-режиме (🧠) клики только выделяют: сим всё равно отклонит приказ
    // (requireManual в ManualCommandExecutor), а маркер и меню без исполнения
    // читаются игроком как «игра сломалась».
    private bool CommandMode => _manualSelectedIds.Count > 0;

    private void Awake() => _camera = GetComponent<Camera>();

    private void Update()
    {
        if (_runner == null)
        {
            _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
        }

        if (_camera == null)
        {
            // Не через ?? : у объектов Unity «уничтоженный» сравнивается с null
            // перегруженным оператором, а ?? смотрит на настоящую ссылку и
            // такой объект пропускает.
            var own = GetComponent<Camera>();
            _camera = own != null ? own : Camera.main;
        }

        RefreshControlSelection();
        UpdatePresenceHeartbeat();
        if (_plantingActorId >= 0)
        {
            if (!UpdatePlantingPlacement())
            {
                CancelPlantingPlacement();
            }

            ClearHover();
            return;
        }

        if (!NpcSelection.HasSelection || PointerBlocked())
        {
            ClearHover();
            return;
        }

        var perfHover = HexLive.UnityPresentation.Diagnostics.WebPerf.Begin();
        UpdateHover();
        HexLive.UnityPresentation.Diagnostics.WebPerf.End(HexLive.UnityPresentation.Diagnostics.WebPerf.Hover, perfHover);
    }

    private void OnDisable()
    {
        ClearHover();
        CancelPlantingPlacement();
    }

    private void UpdatePresenceHeartbeat()
    {
        var now = Time.unscaledTime;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if ((mouse != null &&
             (mouse.delta.ReadValue().sqrMagnitude > 0f ||
              mouse.scroll.ReadValue().sqrMagnitude > 0f ||
              mouse.leftButton.isPressed || mouse.rightButton.isPressed ||
              mouse.middleButton.isPressed)) ||
            keyboard?.anyKey.isPressed == true ||
            // HTML-окна WebGL забирают указатель себе: мышь над ними до Unity
            // не доходит, поэтому открытое окно персонажа само есть внимание.
            LootTransferPanel.IsOpen || ContextMenuPanel.IsOpen ||
            CharacterPanel.BlocksWorldPointer || CraftProjectDetailsPanel.BlocksWorldInput ||
            _plantingActorId >= 0)
        {
            _attentionAt = now;
        }

        if (_runner == null || _manualSelectedIds.Count == 0 ||
            now - _attentionAt > PresenceAttentionSeconds ||
            now - _presenceSentAt < PresenceHeartbeatSeconds)
        {
            return;
        }

        _presenceSentAt = now;
        foreach (var id in _manualSelectedIds)
        {
            _runner.EnqueueCommand(new SetManualControlCommand(new EntityId(id), true));
        }
    }

    private bool UpdatePlantingPlacement()
    {
        var runner = _runner;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (runner == null || !runner.IsReady || !runner.SupportsNpcCommands ||
            mouse == null || !runner.CanControlNpc(new EntityId(_plantingActorId)))
        {
            return false;
        }

        var snapshot = runner.CreateSnapshot();
        var actor = snapshot?.Npcs.FirstOrDefault(candidate =>
            candidate.Id.Value == _plantingActorId);
        if (snapshot == null || actor == null || actor.Health <= 0f ||
            !HasCarriedSeedAtSource(
                actor, _plantingSourceIndex, _plantingSeedDefinitionId))
        {
            return false;
        }

        _plantingCandidateJunction = null;
        _plantingCandidateValid = false;
        if (PointerBlocked() ||
            !TryPickGroundPoint(
                mouse.position.ReadValue(), out var point, out var tile) ||
            !_plantingJunctionsByTile.TryGetValue(tile, out var candidates) ||
            candidates.Count == 0)
        {
            PlantingPlacementMarker.Hide();
            if (_furniturePlacementPreview != null) _furniturePlacementPreview.SetActive(false);
            return true;
        }

        var closest = candidates[0];
        var bestDistance = DistanceSquared(point, closest.Position);
        for (var i = 1; i < candidates.Count; i++)
        {
            var distance = DistanceSquared(point, candidates[i].Position);
            if (distance < bestDistance ||
                (distance == bestDistance && candidates[i].Id.Value < closest.Id.Value))
            {
                closest = candidates[i];
                bestDistance = distance;
            }
        }

        var junction = snapshot.Junctions.FirstOrDefault(candidate =>
            candidate.Id.Equals(closest.Id));
        var ground = snapshot.Tiles.FirstOrDefault(candidate =>
            candidate.Coord.Equals(tile));
        if (junction == null || ground == null)
        {
            PlantingPlacementMarker.Hide();
            if (_furniturePlacementPreview != null) _furniturePlacementPreview.SetActive(false);
            return true;
        }

        _plantingObjectJunctions.Clear();
        var placingSeed = PlantGrowthCatalog.TryGetBySeed(_plantingSeedDefinitionId, out _);
        foreach (var worldObject in snapshot.Objects)
        {
            if (placingSeed && worldObject.SurfaceObjectId is null &&
                PlantGrowthCatalog.TryGetBySeed(worldObject.DefinitionId, out _)) continue;
            foreach (var objectJunction in worldObject.Junctions)
            {
                _plantingObjectJunctions.Add(objectJunction);
            }
        }

        var actorAtPoint = DistanceSquared(actor.Position, closest.Position) <= 0.04f;
        var anotherPersonAtPoint = snapshot.Npcs.Any(person =>
            person.Id.Value != _plantingActorId && person.Health > 0f &&
            DistanceSquared(person.Position, closest.Position) <= 0.04f);
        var animalAtPoint = snapshot.Mobs.Any(mob =>
            DistanceSquared(mob.Position, closest.Position) <= 0.04f);
        _plantingCandidateJunction = closest.Id;
        _plantingCandidateTile = tile;
        _plantingCandidatePosition = closest.Position;
        _plantingCandidateValid = ground.Walkable && !ground.Blocked && !ground.Water &&
            !junction.Blocked && !junction.IsSwimmable &&
            !_plantingObjectJunctions.Contains(closest.Id) &&
            !anotherPersonAtPoint && !animalAtPoint &&
            (!junction.Occupied || actorAtPoint) &&
            (!junction.Reserved || actorAtPoint);

        var position = SimulationUnityMapper.ToUnityPosition(closest.Position, TileTopY(ground));
        if (IsPlacingFurniture)
        {
            const float reach = PortableFurniture.ReachWorldUnits;
            _plantingCandidateValid &= !actorAtPoint &&
                DistanceSquared(actor.Position, closest.Position) <= reach * reach &&
                PortalWorld.DestinationAt(actor.Tile) == PortalWorld.DestinationAt(tile) &&
                PortableFurniture.FitsInsideTile(_plantingSeedDefinitionId, tile, closest.Position) &&
                FurnitureFootprintFree(runner, snapshot, tile, closest.Position, candidates);
            if (UnityEngine.InputSystem.Keyboard.current?.rKey.wasPressedThisFrame == true)
                _furnitureYawSteps = (_furnitureYawSteps + 1) % 6;
            if (PortalWorld.IsApartmentTile(tile)) position.y += ModernApartmentView.FloorLift;
            else if (ground.HasFloor)
                position.y += HexLive.UnityPresentation.Environment.HutAssembly.FloorSurfaceLift;
            UpdateFurniturePlacementPreview(position);
        }
        PlantingPlacementMarker.Show(
            position, _plantingCandidateValid);
        return true;
    }

    private bool FurnitureFootprintFree(SimulationRunnerBehaviour runner, WorldSnapshot snapshot, TileCoord tile,
        Float2 position, List<PlantingJunction> candidates)
    {
        if (_furnitureBlockedStamp != snapshot.JunctionsBlockedStamp)
        {
            _furnitureBlockedStamp = snapshot.JunctionsBlockedStamp;
            _furnitureBlockedJunctions.Clear();
            foreach (var point in snapshot.Junctions)
                if (point.Blocked) _furnitureBlockedJunctions.Add(point.Id);
        }

        float radius = PortableFurniture.FootprintRadius(_plantingSeedDefinitionId);
        float radiusSquared = radius * radius;
        foreach (var candidate in candidates)
            if (_furnitureBlockedJunctions.Contains(candidate.Id) &&
                DistanceSquared(candidate.Position, position) <= radiusSquared) return false;
        foreach (var person in snapshot.Npcs)
            if (person.Health > 0f && DistanceSquared(person.Position, position) <= radiusSquared) return false;
        foreach (var obj in snapshot.Objects)
        {
            if (PortalWorld.IsApartmentTile(tile) && obj.DefinitionId == PortalWorld.DoorDefinitionId &&
                PortalWorld.IsApartmentTile(obj.Tile) &&
                PortalWorld.TryGetWindowEdge(tile, snapshot.Seed, obj.Tile, out _)) return false;
            if (obj.Tile != tile) continue;
            if (!string.IsNullOrEmpty(obj.BuildProduct) || obj.DefinitionId == PortalWorld.DoorDefinitionId)
                return false;
            if (obj.ArchitectureOwnerObjectId.HasValue) continue;
            if (_plantingSeedDefinitionId == ContentIds.StorageChest && obj.Junctions.Count > 0)
            {
                var index = candidates.FindIndex(point => point.Id == obj.Junctions[0]);
                if (index >= 0)
                {
                    var other = candidates[index].Position;
                    if (PortableFurniture.CanShareTile(_plantingSeedDefinitionId, obj.DefinitionId, position, other))
                        continue;
                    if (DistanceSquared(position, other) <= radiusSquared) return false;
                }
            }
            if (PortableFurniture.CanCarry(obj.DefinitionId) ||
                runner.TryGetObjectDefinition(obj.DefinitionId, out var definition) && definition != null &&
                (definition.HasTag("Furniture") || definition.HasTag("Bed") || definition.HasTag("Chair")))
                return false;
        }
        return true;
    }

    private void UpdateFurniturePlacementPreview(Vector3 position)
    {
        if (!_plantingCandidateValid)
        {
            if (_furniturePlacementPreview != null) _furniturePlacementPreview.SetActive(false);
            return;
        }
        if (_furniturePlacementPreview == null)
        {
            var prefab = ModernApartmentView.LoadPrefab(_plantingSeedDefinitionId);
            if (prefab == null) return;
            _furniturePlacementPreview = new GameObject("Furniture placement preview");
            Instantiate(prefab, _furniturePlacementPreview.transform, false);
            foreach (var renderer in _furniturePlacementPreview.GetComponentsInChildren<Renderer>())
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }
        _furniturePlacementPreview.transform.SetPositionAndRotation(position, Quaternion.Euler(0f,
            SimulationUnityMapper.ToUnityFootprintYawDegrees(_furnitureYawSteps * 60f), 0f));
        _furniturePlacementPreview.SetActive(true);
    }

    private void CancelPlantingPlacement()
    {
        _plantingActorId = -1;
        _plantingSourceIndex = -1;
        _plantingSeedDefinitionId = string.Empty;
        _plantingCandidateJunction = null;
        _plantingCandidateValid = false;
        _plantingJunctionsByTile.Clear();
        _plantingObjectJunctions.Clear();
        _furnitureBlockedJunctions.Clear();
        _furnitureBlockedStamp = null;
        _furnitureYawSteps = 0;
        if (_furniturePlacementPreview != null) Destroy(_furniturePlacementPreview);
        _furniturePlacementPreview = null;
        PlantingPlacementMarker.Hide();
    }

    private void RefreshControlSelection()
    {
        _selectedColonyIds.Clear();
        _manualSelectedIds.Clear();
        ManualNpcId = -1;
        if (_runner == null || !_runner.IsReady || !_runner.SupportsNpcCommands ||
            !NpcSelection.HasSelection)
        {
            return;
        }

        var snapshot = _runner.CreateSnapshot();
        if (snapshot == null)
        {
            return;
        }

        foreach (var npc in snapshot.Npcs)
        {
            if (!NpcSelection.Contains(npc.Id.Value) ||
                !_runner.CanControlNpc(npc.Id) || npc.Health <= 0f)
            {
                continue;
            }

            _selectedColonyIds.Add(npc.Id.Value);
            if (npc.IsManualControl && !CharacterPanel.BlocksManualInput(npc.Id.Value)) _manualSelectedIds.Add(npc.Id.Value);
        }

        _selectedColonyIds.Sort();
        _manualSelectedIds.Sort();
        if (_selectedColonyIds.Count == 1 && _manualSelectedIds.Count == 1)
        {
            ManualNpcId = _manualSelectedIds[0];
        }
    }

    private static bool PointerBlocked() =>
        RuntimeWindow.BlocksWorldInput ||
        NpcSelection.PointerOverUi ||
        TacticalMapPanel.PointerOverMap ||
        HexInspectorPanel.PointerOverPanel ||
        ContextMenuPanel.BlocksWorldPointer ||
        LootTransferPanel.IsOpen || CraftProjectDetailsPanel.BlocksWorldInput || CharacterPanel.BlocksWorldPointer ||
        GameMenu.IsOpen || PlayerAssignmentDialog.IsOpen || AdminVoicePanel.BlocksGameInput ||
        EndSummaryPanel.IsOpen ||
        // Bug #279: окно отчёта об ошибке блокирует мир своим флагом — общий
        // NpcSelection.PointerOverUi затирается CharacterPanel каждый кадр.
        BugReportPanel.IsOpen;

    // ── Наведение ────────────────────────────────────────────────────────

    private void UpdateHover()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null || _camera == null)
        {
            ClearHover();
            return;
        }

        // House-only picking must work before any NPC ray initialized this cache.
        if (_worldRenderer == null) _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
        var mousePos = mouse.position.ReadValue();
        // §168.11 PERF: the pick walks every object view and ray-tests every
        // person — in the browser that is a real slice of the frame. Re-pick
        // at once when the cursor or the camera moved; otherwise ten times a
        // second is enough to follow people walking under a still cursor.
        var cameraTransform = _camera.transform;
        var now = Time.unscaledTime;
        if (mousePos == _hoverMousePos && cameraTransform.position == _hoverCameraPos &&
            cameraTransform.rotation == _hoverCameraRot && now < _hoverNextPickTime)
        {
            return;
        }

        _hoverMousePos = mousePos;
        _hoverCameraPos = cameraTransform.position;
        _hoverCameraRot = cameraTransform.rotation;
        _hoverNextPickTime = now + HoverRepickSeconds;
        var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;

        PickTarget(snapshot, mousePos, out _hoveredNpcId, out _hoveredMobId, out var objectHit);
        // Hover reveal is visual and also works while the selected colonist uses AI.
        // The ground tile keeps the cutaway stable after its roof no longer hits the ray.
        var hasGround = TryPickGroundPoint(mousePos, out _, out var hoverTile);
        // Roofs need no context verb to be a visual hover surface. Keep this
        // separate from command picking so clicking a roof gains no empty menu.
        _pickHutEligible ??= view => _worldRenderer != null &&
            _worldRenderer.IsHutHoverSurface(view.ObjectId) && !IsBeyondSmallPropCull(view);
        _pickHutDefers ??= _ => false;
        var houseHit = WorldObjectPicker.Pick(_camera.ScreenPointToRay(mousePos),
            _pickHutEligible, _pickHutDefers, out _);
        _worldRenderer?.SetHutHover(houseHit != null ? houseHit.ObjectId : objectHit != null ? objectHit.ObjectId : -1,
            hasGround ? hoverTile : (TileCoord?)null);
        var mobRenderers = _worldRenderer?.WildlifeRenderers(_hoveredMobId);
        _hasHoveredMob = mobRenderers != null && _hoveredNpcId < 0 && objectHit == null;
        if (!_hasHoveredMob) mobRenderers = null;
        if (!ReferenceEquals(mobRenderers,_hoveredMobRenderers))
        {
            if (_hoveredMobRenderers != null) HoverHighlight.Active.Clear(_hoveredMobRenderers);
            _hoveredMobRenderers=mobRenderers;
            if (_hoveredMobRenderers != null) HoverHighlight.Active.Apply(_hoveredMobRenderers);
        }

        if (!ReferenceEquals(objectHit, _hovered))
        {
            _hovered?.SetHighlighted(false);
            _hovered = objectHit;
            _hovered?.SetHighlighted(true);
        }
    }

    // Общий пикер целей: наведение в ручном режиме и правый клик используют
    // ОДНУ выборку — иначе подсветилось бы одно, а меню открылось на другом.
    private void PickTarget(WorldSnapshot? snapshot, Vector2 mousePos,
        out int npcId, out int mobId, out WorldObjectView? objectHit)
    {
        npcId = -1;
        mobId = -1;
        objectHit = PickObjectUnderCursor(mousePos, out _);

        // §121.1 r2: предмет под курсором ВСЕГДА важнее человека и зверя —
        // правило игрока: «персонаж как-нибудь прокликнется, предмет — уже
        // никак». Лежащая на вещах или стоящая на кокосе выбирается по
        // свободному от вещей пикселю своего тела; глубинного спора и узкого
        // радиуса-исключения больше нет.
        if (objectHit != null)
        {
            // #255: a bed or another stationary station can sit directly
            // under every visible triangle of a lying ward. It is furniture,
            // not the portable "item under her" protected by §121.1 r2. Let
            // the exact body ray win in that one case so her aid menu remains
            // reachable; coconuts, clothes and other PickUp objects keep the
            // player's item-first rule unchanged.
            if (!IsPortablePickTarget(objectHit) &&
                TryRaycastNpc(snapshot, mousePos, out var lyingNpcId, out _) &&
                TryFindPerson(snapshot!, lyingNpcId, out var lyingPerson, out var lyingDead) &&
                !lyingDead && IsLyingPerson(lyingPerson))
            {
                npcId = lyingNpcId;
                objectHit = null;
            }

            return;
        }

        if (TryRaycastNpc(snapshot, mousePos, out var rayNpcId, out _))
        {
            npcId = rayNpcId;
            return;
        }

        // Экранный радиус — запасной путь ТОЛЬКО для людей без готового вида
        // (первые кадры после спавна, примитивы прототипных сцен): у остальных
        // был честный точный луч, и промах по нему — промах, а не повод
        // растянуть тело на 70 px вокруг ног. Зверям точного луча пока нет.
        npcId = PickNpcByScreenRadius(snapshot, mousePos, out _);
        if (npcId < 0)
        {
            mobId = PickMobUnderCursor(snapshot, mousePos, out _);
        }
    }

    private bool IsPortablePickTarget(WorldObjectView view)
    {
        if (_runner == null ||
            !_runner.TryGetObjectDefinition(view.ContextDefinitionId, out var definition) ||
            definition == null)
        {
            // Unknown legacy objects keep the conservative item-first rule.
            return true;
        }

        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == InteractionType.PickUp)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLyingPerson(NpcSnapshot person) =>
        person.IsUnconscious || person.IsDying || person.IsFainted ||
        person.IsPlayingDead || person.CurrentInteraction == "Sleep";

    private const float HoverRepickSeconds = 0.1f;
    private Vector2 _hoverMousePos;
    private Vector3 _hoverCameraPos;
    private Quaternion _hoverCameraRot;
    private float _hoverNextPickTime;

    private void ClearHover()
    {
        _worldRenderer?.SetHutHover(-1, null);
        _hoverNextPickTime = 0f;
        _hovered?.SetHighlighted(false);
        _hovered = null;
        _hoveredNpcId = -1;
        _hoveredMobId = -1;
        _hasHoveredMob=false;
        if (_hoveredMobRenderers != null) HoverHighlight.Active.Clear(_hoveredMobRenderers);
        _hoveredMobRenderers=null;
    }

    // Предикаты пикера держим полями: они уходят делегатами в
    // WorldObjectPicker каждый кадр наведения, и создавать их заново значило бы
    // мусорить в Update.
    private System.Func<WorldObjectView, bool>? _pickEligible;
    private System.Func<WorldObjectView, bool>? _pickDefers;
    private System.Func<WorldObjectView, bool>? _pickHutEligible;
    private System.Func<WorldObjectView, bool>? _pickHutDefers;

    private WorldObjectView? PickObjectUnderCursor(Vector2 mousePos, out float bestDistance)
    {
        bestDistance = float.MaxValue;
        if (_camera == null)
        {
            return null;
        }

        _pickEligible ??= view => view.ObjectId >= 0 && HasContextActions(view) &&
            !IsBeyondSmallPropCull(view);
        _pickDefers ??= DefersToContents;
        return WorldObjectPicker.Pick(
            _camera.ScreenPointToRay(mousePos), _pickEligible, _pickDefers, out bestDistance);
    }

    // §121.1 r3: вешалка уступает луч своему содержимому. Признак берётся из
    // КАТАЛОГА — умеет «повесить», значит это сушилка/гардероб (§35.5B, §133),
    // и вещь на ней важнее её самой. Списка id здесь нет сознательно: новая
    // вешалка получит то же поведение без правки ввода.
    private bool DefersToContents(WorldObjectView view)
    {
        if (_runner == null ||
            !_runner.TryGetObjectDefinition(view.ContextDefinitionId, out var definition) ||
            definition == null)
        {
            return false;
        }

        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == InteractionType.Hang)
            {
                return true;
            }
        }

        return false;
    }

    // §121.4: SmallProps-слой отсекается камерой за layerCullDistances — проп
    // там НЕ РИСУЕТСЯ, но его bounds остаются валидными, и пикинг «подсвечивал»
    // невидимое. Невидимое не кликается.
    private bool IsBeyondSmallPropCull(WorldObjectView view)
    {
        if (_camera == null)
        {
            return false;
        }

        // §150.4: вид с активным импостором виден на любом отдалении — его
        // квад живёт на обычном слое, и отсечка SmallProps на него не
        // распространяется: видимое обязано быть кликабельным.
        if (view.TryGetComponent<ObjectImpostor>(out var impostor) &&
            impostor.DistantActive)
        {
            return false;
        }

        // Слой берётся с ВИДИМОЙ геометрии, не с корня вида: SmallProps
        // назначается дочернему FBX-инстансу (SuppressSmallPropShadows), а
        // корень остаётся на Default с cull 0 — проверка по корню была
        // мёртвой, и задистанс-куленные пропы оставались кликабельными.
        var layer = view.VisibleGeometryLayer();
        var cull = _camera.layerCullDistances[layer];
        if (cull <= 0f)
        {
            return false;
        }

        // Камера режет этот слой сферически (layerCullSpherical) — меряем той
        // же метрикой, дистанцией до позиции камеры.
        var offset = view.transform.position - _camera.transform.position;
        return offset.sqrMagnitude > cull * cull;
    }

    // Decorative/resource producers can visually enclose a real ground item:
    // a herb bush encloses its shed leaf, and furniture can overlap dropped
    // clothes. Such a view has no contextual action of its own and must not
    // swallow the ray before the actionable object behind it is considered.
    // The definition catalog remains the single eligibility source used again
    // by OpenObjectMenu and ManualCommandExecutor; this is only hit selection.
    private bool HasContextActions(WorldObjectView view) =>
        view.HasDoorActions || (_runner != null &&
            _runner.TryGetObjectDefinition(view.ContextDefinitionId, out var definition) &&
            definition != null && definition.Interactions.Count > 0);

    // Точное попадание луча в видимую геометрию тела — с ДИСТАНЦИЕЙ, чтобы
    // UpdateHover мог сравнить человека и объект по глубине (§121.1).
    private bool TryRaycastNpc(
        WorldSnapshot? snapshot, Vector2 mousePos, out int npcId, out float distance)
    {
        npcId = -1;
        distance = float.PositiveInfinity;
        if (snapshot == null || _camera == null)
        {
            return false;
        }

        if (_worldRenderer == null)
        {
            _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
        }

        if (_worldRenderer == null)
        {
            return false;
        }

        var ray = _camera.ScreenPointToRay(mousePos);
        foreach (var person in People(snapshot))
        {
            // §121.9: своя ЕДИНСТВЕННАЯ выделенная — легальная цель
            // ТОЧНОГО луча: клик по ней открывает само-меню. Только точный луч:
            // 70px-фолбэк ниже по-прежнему исключает выделенных, поэтому клик
            // «рядом с ней» остаётся приказом идти / меню объекта.
            var isSelf = person.Id.Value == OrderNpcId;
            if ((NpcSelection.Contains(person.Id.Value) && !isSelf) ||
                !CanTargetPerson(person) || // §148: невидимого не выбрать
                !_worldRenderer.TryGetActorView(person.Id.Value, out var view) ||
                !NpcBodyPicker.TryIntersect(view, ray, distance, out var hitDistance))
            {
                continue;
            }

            distance = hitDistance;
            npcId = person.Id.Value;
        }

        return npcId >= 0;
    }

    private int PickNpcByScreenRadius(
        WorldSnapshot? snapshot, Vector2 mousePos, out float bestDistance)
    {
        bestDistance = PickRadiusPixels;
        if (snapshot == null || _camera == null)
        {
            return -1;
        }

        var bestId = -1;
        foreach (var npc in People(snapshot))
        {
            if (NpcSelection.Contains(npc.Id.Value) || !CanTargetPerson(npc))
            {
                continue; // сама себе не цель; §148 — невидимого не выбрать
            }

            // §121.1 r2: у человека с готовым видом уже был точный луч по его
            // геометрии — радиус вокруг ног дал бы «огромный клик» поверх
            // предметов и соседок. Радиус остаётся только виду-примитиву.
            if (_worldRenderer != null &&
                _worldRenderer.TryGetActorView(npc.Id.Value, out _))
            {
                continue;
            }

            var world = SimulationUnityMapper.ToUnityPosition(
                npc.Position, SimulationUnityMapper.CameraTargetHeight);
            var screen = _camera.WorldToScreenPoint(world);
            if (screen.z <= 0f)
            {
                continue;
            }

            var distance = Vector2.Distance(new Vector2(screen.x, screen.y), mousePos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestId = npc.Id.Value;
            }
        }

        return bestId;
    }

    private static IEnumerable<NpcSnapshot> People(WorldSnapshot snapshot)
    {
        foreach (var npc in snapshot.Npcs) yield return npc;
        foreach (var corpse in snapshot.Corpses) yield return corpse;
    }

    /// <summary>§148: по человеку, которого наши сейчас не видят, кликнуть
    /// нельзя — на его последнем известном месте висит «?», и это ЗНАНИЕ
    /// игрока, а не цель. Один предикат на все три пути наведения (точный луч,
    /// экранный радиус, ховер), чтобы «нельзя кликнуть» нельзя было забыть в
    /// одном из них.</summary>
    internal bool CanTargetPerson(NpcSnapshot person)
    {
        var owned = _runner != null && _runner.CanControlNpc(person.Id);
        if (owned)
        {
            return true;
        }

        if (_worldRenderer == null)
        {
            _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
        }

        return _worldRenderer != null &&
            _worldRenderer.IsNpcPickable(person.Id.Value, person.Tile, false);
    }

    private int PickMobUnderCursor(
        WorldSnapshot? snapshot, Vector2 mousePos, out float bestDistance)
    {
        bestDistance = PickRadiusPixels;
        if (snapshot == null || _camera == null)
        {
            return -1;
        }

        if (_worldRenderer != null && _worldRenderer.TryPickWildlife(
            _camera.ScreenPointToRay(mousePos), out var wildlifeId)) return wildlifeId;
        var bestId = -1;
        foreach (var mob in snapshot.Mobs)
        {
            var world = SimulationUnityMapper.ToUnityPosition(
                mob.Position, SimulationUnityMapper.CameraTargetHeight * 0.5f);
            var screen = _camera.WorldToScreenPoint(world);
            if (screen.z <= 0f)
            {
                continue;
            }

            var distance = Vector2.Distance(new Vector2(screen.x, screen.y), mousePos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestId = mob.Id;
            }
        }

        return bestId;
    }

    // ── Клик ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Левый клик в ручном режиме. Возвращает истину, если клик СЪЕДЕН —
    /// тогда камера не делает свою обычную работу (выбор NPC / гекса).
    /// </summary>
    public bool TryHandleManualClick(Vector2 mousePos)
    {
        if (_plantingActorId >= 0)
        {
            if (_runner != null && _plantingCandidateValid &&
                _plantingCandidateJunction is { } target)
            {
                var actor = new EntityId(_plantingActorId);
                var source = new InventoryItemRef(InventoryItemSource.Carried, _plantingSourceIndex,
                    _plantingSeedDefinitionId);
                ISimulationCommand command = IsPlacingFurniture
                    ? new PlaceInventoryFurnitureCommand(actor, source, target, _plantingCandidateTile,
                        _furnitureYawSteps * 60f)
                    : new PlantInventorySeedCommand(actor, source, target, _plantingCandidateTile);
                EnqueueOrder(_plantingActorId, command);
                DestinationMarker.Show(SimulationUnityMapper.ToUnityPosition(
                    _plantingCandidatePosition,
                    GroundMarkerY(_plantingCandidatePosition)));
                CancelPlantingPlacement();
            }

            // An invalid red hex keeps placement alive, but the click is still
            // consumed so it cannot select a different person or issue MoveTo.
            return true;
        }

        if (CharacterPanel.BlocksManualInput(NpcSelection.PrimaryId)) return true;
        if (_runner == null)
        {
            return false;
        }

        // Открытое меню закрывается кликом мимо себя — и на этом клик кончается:
        // иначе то же нажатие тут же отдало бы приказ идти под меню. Гейт режима
        // стоит НИЖЕ: меню могло остаться висеть после выключения 🎮.
        if (RuntimeWindow.BlocksWorldInput || CraftProjectDetailsPanel.BlocksWorldInput || CharacterPanel.BlocksWorldPointer) return true;

        if (ContextMenuPanel.IsOpen)
        {
            ContextMenuPanel.Close();
            return true;
        }

        if (_selectedColonyIds.Count == 0)
        {
            return false;
        }

        var clickSnapshot = _runner.IsReady ? _runner.CreateSnapshot() : null;
        if (CommandMode && TryOpenTargetChooser(clickSnapshot, mousePos)) return true;
        PickTarget(clickSnapshot, mousePos, out var clickedNpc, out var clickedMob, out var clickedObject);

        if (clickedNpc >= 0)
        {
            // #731: выделен ровно один свой персонаж — левый клик по ДРУГОМУ
            // человеку на сцене открывает меню действий в любом режиме, а не
            // переключает выделение (сменить персонажа — список лагеря). Клик
            // по себе под ИИ по-прежнему отдаётся камере (follow, §123.1).
            if (!CommandMode && (OrderNpcId < 0 || clickedNpc == OrderNpcId))
                return false;

            // §121.9: клик по себе — меню самодействий, не приказ и не выбор.
            if (clickedNpc == OrderNpcId)
            {
                OpenSelfMenu(mousePos, clickedNpc);
            }
            else
            {
                OpenNpcMenu(mousePos, clickedNpc);
            }

            return true;
        }

        if (clickedMob != -1)
        {
            OpenMobMenu(mousePos, clickedMob);
            return true;
        }

        if (clickedObject != null)
        {
            OpenObjectMenu(mousePos, clickedObject);
            return true;
        }

        if (TryPickGroundPoint(mousePos, out var point))
        {
            // §121.11 (bug #294): ОДИН клик — всегда идти. Темпа жест больше не
            // несёт (run: null): им владеет постоянная настройка персонажа в
            // симуляции, и у каждой выделенной девушки она своя.
            if (OrderNpcId >= 0)
            {
                EnqueueOrder(OrderNpcId, new MoveToCommand(new EntityId(OrderNpcId), point));
            }
            else
            {
                EnsureGroupManual();
                _runner.EnqueueCommand(new GroupMoveCommand(OrderActors(), point));
            }
            DestinationMarker.Show(SimulationUnityMapper.ToUnityPosition(point, GroundMarkerY(point)));
            return true;
        }

        return false;
    }

    /// <summary>
    /// §150: an explicit RTS click on either map size. Unlike an ordinary
    /// world-ground click (§121.1), the map gesture takes control of every
    /// living selected actor this client owns, then queues the normal move
    /// command behind that control edge in the same tick.
    /// </summary>
    public bool TryMoveSelectionFromMap(Float2 point, bool run)
    {
        var runner = _runner;
        if (runner == null || !runner.IsReady || !runner.SupportsNpcCommands ||
            !NpcSelection.HasSelection)
        {
            return false;
        }

        var snapshot = runner.CreateSnapshot();
        if (snapshot == null)
        {
            return false;
        }

        var actors = new List<EntityId>();
        var manual = new HashSet<int>();
        foreach (var npc in snapshot.Npcs)
        {
            if (!NpcSelection.Contains(npc.Id.Value) || npc.Health <= 0f ||
                !runner.CanControlNpc(npc.Id))
            {
                continue;
            }

            actors.Add(npc.Id);
            if (npc.IsManualControl)
            {
                manual.Add(npc.Id.Value);
            }
        }

        if (actors.Count == 0)
        {
            return false;
        }

        actors.Sort((a, b) => a.Value.CompareTo(b.Value));
        if (actors.Count == 1)
        {
            var actor = actors[0];
            runner.EnqueueCommand(new QueuePlayerOrderCommand(new MoveToCommand(actor, point, run)));
        }
        else
        {
            if (manual.Count != actors.Count)
            {
                runner.EnqueueCommand(new SetGroupManualControlCommand(actors, true));
            }

            runner.EnqueueCommand(new GroupMoveCommand(actors, point, run));
        }

        DestinationMarker.Show(
            SimulationUnityMapper.ToUnityPosition(point, GroundMarkerY(point)));
        return true;
    }

    /// <summary>
    /// Правый клик (без drag) при выделенной колонистке — контекстное меню
    /// цели под курсором, БЕЗ требования тумблера 🎮: раньше до «атаковать/
    /// обобрать» было не добраться иначе как через ручной режим. Приказ из
    /// меню сам берёт управление (<see cref="EnsureManual"/>), а таймаут
    /// §121.5 потом штатно возвращает её ИИ. Исключение — управление
    /// инвентарём §123/§128: оно доступно в обоих режимах и не переключает
    /// тумблер управления.
    /// </summary>
    public bool TryHandleContextClick(Vector2 mousePos)
    {
        if (TryCancelPlantingPlacement())
        {
            return true;
        }

        if (CharacterPanel.BlocksManualInput(NpcSelection.PrimaryId)) return true;
        if (RuntimeWindow.BlocksWorldInput || CraftProjectDetailsPanel.BlocksWorldInput || CharacterPanel.BlocksWorldPointer) return true;
        var runner = _runner;
        if (runner == null || _camera == null || !runner.SupportsNpcCommands ||
            _selectedColonyIds.Count == 0)
        {
            return false;
        }

        var snapshot = runner.IsReady ? runner.CreateSnapshot() : null;
        if (snapshot == null)
        {
            return false;
        }

        if (TryOpenTargetChooser(snapshot, mousePos)) return true;
        PickTarget(snapshot, mousePos, out var npcId, out var mobId, out var objectHit);
        if (npcId >= 0)
        {
            if (npcId == OrderNpcId)
            {
                OpenSelfMenu(mousePos, npcId);
            }
            else
            {
                OpenNpcMenu(mousePos, npcId);
            }

            return true;
        }

        if (mobId != -1)
        {
            OpenMobMenu(mousePos, mobId);
            return true;
        }

        if (objectHit != null)
        {
            OpenObjectMenu(mousePos, objectHit);
            return true;
        }

        return false;
    }

    // Приказ из контекстного меню сам берёт управление: не-ручной
    // исполнительнице впереди приказа в ту же очередь встаёт SetManualControl,
    // и сим принимает приказ тем же тиком. Уже ручную не трогаем.
    private void EnsureManual(int actorId)
    {
        if (_runner == null || actorId < 0 ||
            !_runner.CanControlNpc(new EntityId(actorId)) ||
            _manualSelectedIds.Contains(actorId))
        {
            return;
        }

        _runner.EnqueueCommand(new SetManualControlCommand(new EntityId(actorId), true));
    }

    private void EnqueueOrder(int actorId, ISimulationCommand command)
    {
        if (_runner == null)
        {
            return;
        }

        if (QueuePlayerOrderCommand.Supports(command))
        {
            _runner.EnqueueCommand(new QueuePlayerOrderCommand(command));
            return;
        }

        EnsureManual(actorId);
        _runner.EnqueueCommand(command);
    }

    private void OpenObjectMenu(Vector2 mousePos, WorldObjectView view)
    {
        var runner = _runner;
        if (runner == null ||
            !runner.TryGetObjectDefinition(view.ContextDefinitionId, out var definition) ||
            definition == null)
        {
            return;
        }

        if (OrderNpcId < 0)
        {
            _entries.Clear();
            _entries.Add(new ContextMenuEntry(
                Loc.Get("menu.select_one_character"), () => { }, false,
                Loc.Get("menu.select_one_character")));
            ContextMenuPanel.Open(
                mousePos, ObjectTitle(definition, view.ContextDefinitionId), _entries);
            return;
        }

        // One coherent frame for the generic actions and all state-derived
        // additions below. Empty tagged containers (including a wardrobe) are
        // still valid drop targets even though Contents has no cells yet.
        var snapshot = runner.IsReady ? runner.CreateSnapshot() : null;
        var clicked = FindObject(snapshot, view.ObjectId);
        var isContainer = clicked != null &&
            (clicked.ContainerCapacity > 0 ||
             clicked.Contents.Count > 0 ||
             definition.HasTag(ObjectTags.Remains) ||
             definition.HasTag("Container") ||
             definition.HasTag(ObjectTags.Wardrobe) ||
             definition.InventoryCapacity > 0);

        // Приказ уходит по КОНТЕКСТНОЙ цели (секция дома проксирует меню на
        // своего footprint-owner), поэтому и состояние стройки спрашиваем у неё.
        var contextObject = view.ContextObjectId == view.ObjectId
            ? clicked
            : FindObject(snapshot, view.ContextObjectId);

        var carried = CarriedItems();
        var actorId = OrderNpcId;
        var actor = new EntityId(actorId);
        var buildOffered = false;
        _entries.Clear();
        // A door view can proxy its general menu onto the hut. These commands
        // deliberately address the actual architecture leaf, never the owner.
        if (clicked != null && DoorInteractionMath.CanTargetClosedDoor(clicked))
        {
            var doorId = new ObjectId(clicked.Id.Value);
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.knock_door"),
                () => EnqueueOrder(actorId,new KnockDoorCommand(actor,doorId))));
            var person = snapshot?.Npcs.Find(n => n.Id.Value == actorId);
            var hands = person == null ? 0 :
                (BodyPartFunctionSnapshotMath.LimbFunction(person.BodyPartConditions,BodyPart.ArmR) >= BodyState.UsableHandFunctionThreshold ? 1 : 0) +
                (BodyPartFunctionSnapshotMath.LimbFunction(person.BodyPartConditions,BodyPart.ArmL) >= BodyState.UsableHandFunctionThreshold ? 1 : 0);
            var canBreak = ActionRequirements.HasTool(carried,new[]{GearCapability.ChopWood},hands);
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.break_door"),
                () => EnqueueOrder(actorId,new BreakDoorCommand(actor,doorId)),canBreak,
                canBreak ? null : Loc.Get("menu.missing_tool")) { Danger=true });
        }
        foreach (var interaction in definition.Interactions)
        {
            // §128.5 / bug #199: a container's Loot action opens the two-sided
            // inventory panel; the old one-item interaction must not create a
            // duplicate identically named menu entry beside it.
            if (isContainer && interaction.Type == InteractionType.Loot) continue;
            if (clicked != null && clicked.CraftWorkRequired > 0 &&
                (interaction.Type == InteractionType.PickUp || interaction.Type == InteractionType.Craft)) continue;
            if (clicked != null && interaction.Type == InteractionType.PickUp &&
                PortableFurniture.CanCarry(clicked.DefinitionId) &&
                !PortableFurniture.CanShowPickup(clicked,
                    snapshot?.Tiles.Exists(tile => tile.Coord == clicked.Tile && tile.HasFloor) == true)) continue;
            if (clicked != null && interaction.Type == InteractionType.SwitchFlashlight &&
                (interaction.Id == FlashlightMath.SwitchOffInteractionId) !=
                    FlashlightMath.IsOn(clicked.ResourceAmount, clicked.Durability)) continue;
            if (clicked != null && interaction.Type == InteractionType.SwitchChestLid &&
                (interaction.Id == StorageChest.CloseInteractionId) != StorageChest.IsOpen(clicked.Variant)) continue;

            // §54.14 (bug #292): готовая постройка не предлагает строить себя.
            // У костра глагол Build стоит в каталоге ДВАЖДЫ (build.upgrade и
            // build.furniture — §54.14 отдаёт оба одному обработчику), поэтому
            // достроенный костёр показывал игроку две одинаковые строки
            // «Строить», ведущие в никуда. Пункт остаётся ровно один и только
            // пока у цели открыт строительный счёт; тот же предикат
            // авторитетно повторяет ManualCommandExecutor.
            if (interaction.Type == InteractionType.Build)
            {
                if (buildOffered || !BuildSiteView.AcceptsBuildOrder(contextObject)) continue;
                buildOffered = true;
            }

            var actingNpc = snapshot?.Npcs.Find(candidate => candidate.Id.Value == actorId);
            var usableHands = actingNpc is null ? 0
                : (BodyPartFunctionSnapshotMath.LimbFunction(actingNpc.BodyPartConditions, BodyPart.ArmR) >= BodyState.UsableHandFunctionThreshold ? 1 : 0)
                + (BodyPartFunctionSnapshotMath.LimbFunction(actingNpc.BodyPartConditions, BodyPart.ArmL) >= BodyState.UsableHandFunctionThreshold ? 1 : 0);
            var ok = ActionRequirements.HasTool(carried, interaction.RequiredCapabilities, usableHands);
            // #749: «Строить» адресует ту деталь дома, на которую нажали —
            // симуляция сама переведёт приказ на владельца следа и поведёт
            // строителя именно к ней. Остальные действия идут контекстной цели.
            var objectId = interaction.Type == InteractionType.Build ? view.ObjectId : view.ContextObjectId;
            var type = interaction.Type;
            var interactionId = interaction.Id;
            var emptyDrink = type == InteractionType.Drink && clicked != null &&
                ItemCatalog.IsDrainedWaterShell(clicked.DefinitionId, clicked.ResourceAmount);
            var emptyBattery = type == InteractionType.SwitchFlashlight && clicked != null && clicked.Durability <= 0f;
            if (emptyDrink || emptyBattery) ok = false;
            // Bug #312: взять чужое на приватной земле чужого лагеря — это
            // «Украсть», красным. Предикат тот же, что применит симуляция
            // (TheftMath), посчитан по снапшоту: якоря лагерей + владелец.
            var theft = IsTheftTarget(snapshot, clicked, actingNpc, type);
            var verb = interactionId == PortalWorld.TravelInteractionId && clicked != null
                ? Loc.Get(PortalWorld.IsApartmentTile(clicked.Tile) ? "portal.return" : "portal.enter")
                : type == InteractionType.SwitchFlashlight
                    ? Loc.Get(interactionId == FlashlightMath.SwitchOnInteractionId ? "menu.flashlight_on" : "menu.flashlight_off")
                : type == InteractionType.SwitchChestLid
                    ? Loc.Get(interactionId == StorageChest.OpenInteractionId ? "menu.storage_chest_open" : "menu.storage_chest_close")
                : InteractionVerb(interaction);
            var label = emptyDrink ? Loc.Get("menu.drink_empty") : theft && type == InteractionType.PickUp ? Loc.Get("menu.steal") : verb;
            void Activate() => EnqueueOrder(actorId, new InteractCommand(actor, new ObjectId(objectId), type, interactionId));
            var hint = emptyDrink ? Loc.Get("menu.drink_empty") : emptyBattery ? Loc.Get("toast.order_rejected.BatteryEmpty") :
                ok ? null : Loc.Get(usableHands == 0 ? "menu.needs_hand" : "menu.missing_tool");
            _entries.Add(type == InteractionType.Sleep
                ? SleepMenuEntry(label, Activate, actingNpc, ok, hint)
                : new ContextMenuEntry(label, Activate, ok, hint) { Danger = theft });

            // §121.10 (баг #270): рядом с «Подобрать» — «Собрать все». Собрать
            // все — однотипное на исходном гексе и шести соседних (#467).
            // Количество намеренно НЕ считаем и не показываем: игрок просил
            // упростить («давай даже не будем считать»), а сколько там листьев
            // — знает мир, а не меню прошлого кадра. Приказ сам разберёт гекс
            // по одному предмету за раз.
            // Bug #331: у вертела «собрать все» лишено смысла — мясо и так
            // берётся по кусочку (сам пункт называется «Взять кусочек»).
            // #746: тот же «…все» — для обработки и сбора с места: распилить,
            // разрубить, вскрыть, сорвать всё однотипное на гексе и шести
            // соседних. Механика одна (очередь обычных приказов §121.10), а
            // подпись собирается из глагола самого действия.
            var allPickUp = type == InteractionType.PickUp && interactionId != "take.from.spit" &&
                !PortableFurniture.CanCarry(view.ContextDefinitionId);
            var allWork = type is InteractionType.Process or InteractionType.Harvest;
            if (allPickUp || allWork)
            {
                _entries.Add(new ContextMenuEntry(
                    allWork ? string.Format(Loc.Get("menu.action_all"), verb)
                        : theft ? Loc.Get("menu.steal_all") : Loc.Get("menu.gather_all"),
                    () => EnqueueOrder(actorId,
                        new GatherAllOnHexCommand(
                            actor, new ObjectId(objectId), type, interactionId)),
                    ok,
                    ok ? null : Loc.Get(usableHands == 0 ? "menu.needs_hand" : "menu.missing_tool"))
                { Danger = theft });
            }
        }

        // §124.1: у несущей человека клик по кровати добавляет «Положить» —
        // рядом со «Спать» из каталога. Занятость кровати авторитетно решает
        // симуляция (Occupied придёт тостом): в ObjectSnapshot её нет.
        if (HexLive.Simulation.Content.ContentIds.IsBed(view.DefinitionId))
        {
            NpcSnapshot? me = null;
            if (snapshot != null)
            {
                foreach (var candidate in snapshot.Npcs)
                {
                    if (candidate.Id.Value == ManualNpcId)
                    {
                        me = candidate;
                        break;
                    }
                }
            }

            if (me?.CarriedNpcId is not null)
            {
                var bedId = view.ObjectId;
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.put_in_bed"),
                    () => EnqueueOrder(actorId,
                        new PutPersonInBedCommand(actor, new ObjectId(bedId)))));
            }
        }

        // §146.14 (bug #291): «Сделать домом» — только на СВОЁМ очаге (наш
        // лагерь или ничья земля; предикат общий с исполнителем —
        // CampHomeMath, правило §149 r3). На чужом очаге пункта нет вовсе:
        // присоединение к чужому лагерю — только дипломатией §146.12. Именно
        // EnqueueCommand, не EnqueueOrder: перенос якоря — состояние лагеря,
        // переводить девушку в ручной режим ради него нельзя.
        if (definition.HasTag("Campfire") && snapshot != null && clicked != null)
        {
            NpcSnapshot? orderer = null;
            foreach (var candidate in snapshot.Npcs)
            {
                if (candidate.Id.Value == actorId)
                {
                    orderer = candidate;
                    break;
                }
            }

            if (orderer != null && !CampHomeMath.IsForeignCampTile(
                    snapshot.CampHomes, orderer.Faction, clicked.Tile))
            {
                var already = CampHomeMath.IsHomeTile(
                    snapshot.CampHomes, orderer.Faction, clicked.Tile);
                var hearthId = view.ContextObjectId;
                _entries.Add(new ContextMenuEntry(
                    Loc.Get("menu.make_home"),
                    () => runner.EnqueueCommand(
                        new SetCampHomeCommand(actor, new ObjectId(hearthId))),
                    !already,
                    already ? Loc.Get("menu.make_home.already") : null));
            }
        }

        // §54.14 (bug #293): «Пожарить мясо» — прямо в меню костра. Раньше
        // повесить кусок на вертел можно было только через вкладку крафта в
        // рюкзаке, и игрок этого пути не находил вовсе. Приказ тот же самый,
        // которым мясо вешает сама колонистка (рецепт CookMeat): огонь,
        // готовый вертел и свободные крюки проверяет симуляция, а не меню
        // прошлого кадра. Пункт показан, только когда сырое мясо при себе —
        // иначе он был бы обещанием без содержания.
        if (definition.HasTag(ObjectTags.Campfire))
        {
            var hasRawMeat = carried.Any(HumanMeatCatalog.IsRaw);
            var fireId = new ObjectId(view.ContextObjectId);
            _entries.Add(new ContextMenuEntry(
                Loc.Get("menu.cook_meat"),
                () => EnqueueOrder(actorId,
                    new CraftItemCommand(actor, GoalType.CookMeat, fireId)),
                hasRawMeat,
                hasRawMeat ? null : Loc.Get("menu.cook_meat.no_meat")));
        }

        // §128.5: ОБЫСКАТЬ ВЕЩЬ — истлевшее тело, снятый рюкзак, аптечку.
        // Пустой мешок или шкаф тоже открывается: это не только источник,
        // но и назначение для перетаскивания вещей из левой панели.
        if (isContainer)
        {
            var containerId = view.ContextObjectId;
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.loot_person"),
                () => LootTransferPanel.OpenContainer(actorId, containerId))
            { Danger = IsTheftTarget(snapshot, clicked,
                snapshot?.Npcs.Find(n => n.Id.Value == actorId), InteractionType.Loot) });
        }

        // Clicking the pack hanging in a wardrobe can hit the pack mesh
        // before the wardrobe. Offer the outer wardrobe from that hit too.
        if (snapshot != null && clicked != null && clicked.Junctions.Count > 0 &&
            definition.Layer != null)
        {
            foreach (var wardrobe in snapshot.Objects)
            {
                if (wardrobe.DefinitionId != ContentIds.Wardrobe || wardrobe.Junctions.Count == 0 ||
                    !wardrobe.Junctions[0].Equals(clicked.Junctions[0]) ||
                    !runner.TryGetObjectDefinition(wardrobe.DefinitionId, out var wardrobeDefinition) ||
                    wardrobeDefinition == null) continue;

                var wardrobeId = wardrobe.Id.Value;
                var label = $"{Loc.Get("menu.loot_person")} · {ObjectTitle(wardrobeDefinition, wardrobe.DefinitionId)}";
                _entries.Add(new ContextMenuEntry(label,
                    () => LootTransferPanel.OpenContainer(actorId, wardrobeId))
                { Danger = IsTheftTarget(snapshot, wardrobe,
                    snapshot.Npcs.Find(n => n.Id.Value == actorId), InteractionType.Loot) });
                break;
            }
        }

        if (snapshot != null)
        {
            if (clicked != null && clicked.CraftWorkRequired > 0)
                _entries.AddRange(CraftProjectEntries(clicked, actorId));
            else
                foreach (var project in snapshot.Objects)
                {
                    if (project.CraftWorkRequired <= 0 ||
                        project.CraftStationObjectId != view.ContextObjectId) continue;
                    var title = CraftProjectDetailsPanel.ItemName(runner, project.DefinitionId);
                    _entries.Add(new ContextMenuEntry(title, CraftProjectEntries(project, actorId)));
                }
        }

        if (_entries.Count == 0)
        {
            return;
        }

        ContextMenuPanel.Open(mousePos, ObjectTitle(definition, view.ContextDefinitionId), _entries);
    }

    private List<ContextMenuEntry> CraftProjectEntries(ObjectSnapshot project, int actorId)
    {
        var projectId = project.Id.Value;
        var entries = new List<ContextMenuEntry>();
        var ready = project.CraftWorkDone >= project.CraftWorkRequired;
        var actor = new EntityId(actorId);
        if (ready)
        {
            string? pickUpId = null;
            if (_runner != null && _runner.TryGetObjectDefinition(project.DefinitionId, out var definition) &&
                definition != null)
                foreach (var interaction in definition.Interactions)
                    if (interaction.Type == InteractionType.PickUp) { pickUpId = interaction.Id; break; }
            entries.Add(new ContextMenuEntry(Loc.Get("craft.project.take"),
                () => EnqueueOrder(actorId, new InteractCommand(actor,
                    new ObjectId(projectId), InteractionType.PickUp, pickUpId ?? string.Empty)), pickUpId != null));
        }
        else
        {
            entries.Add(new ContextMenuEntry(Loc.Get("craft.project.resume"),
                () => EnqueueOrder(actorId, new InteractCommand(actor,
                    new ObjectId(projectId), InteractionType.Craft, "craft.resume")),
                !project.CraftActive, project.CraftActive ? Loc.Get("craft.project.active") : null));
        }
        entries.Add(new ContextMenuEntry(Loc.Get("craft.project.details"), () =>
        {
            if (_runner != null) CraftProjectDetailsPanel.Open(_runner, projectId, transform);
        }));
        return entries;
    }

    private static ObjectSnapshot? FindObject(WorldSnapshot? snapshot, int objectId)
    {
        if (snapshot == null)
        {
            return null;
        }

        foreach (var obj in snapshot.Objects)
        {
            if (obj.Id.Value == objectId) return obj;
        }

        return null;
    }

    private void OpenNpcMenu(Vector2 mousePos, int npcId)
    {
        var runner = _runner;
        if (runner == null)
        {
            return;
        }

        var snapshot = runner.IsReady ? runner.CreateSnapshot() : null;
        if (snapshot == null || !TryFindPerson(snapshot, npcId, out var target, out var dead))
        {
            return;
        }

        var me = OrderNpcId;
        NpcSnapshot? carrier = null;
        foreach (var candidate in snapshot.Npcs)
        {
            if (candidate.Id.Value == me)
            {
                carrier = candidate;
                break;
            }
        }

        _entries.Clear();
        if (!dead)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.attack"),
                () => EnqueueNpcAttack(me, npcId)));
        }

        var lying = dead || IsLyingPerson(target);

        // §121.9: социальные приказы. Меню не предугадывает сим: занятая или
        // не в духе цель откажет по прибытии честным cue (TalkRejected), а
        // нехватка припаса — тостом NoSupplies. Серость здесь — только про
        // «кто приказывает» (один выделенный ручной, цель не на чужих руках).
        var canOrderSocial = carrier != null && _selectedColonyIds.Count == 1 &&
            carrier.Id.Value != npcId &&
            target.CarriedByNpcId is null;
        var socialBlocked = target.CarriedByNpcId is not null
            ? Loc.Get("menu.carried_by_other")
            : Loc.Get("menu.select_one_character");
        if (!dead && !target.IsUnconscious)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.talk_to"),
                () => EnqueueOrder(carrier!.Id.Value, new TalkToCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId))),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.ask_bathe"),
                () => EnqueueOrder(carrier!.Id.Value, new TalkToCommand(
                    carrier.Id, new EntityId(npcId), TalkTopic.AskBathe)),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.ask_wash_clothes"),
                () => EnqueueOrder(carrier!.Id.Value, new TalkToCommand(
                    carrier.Id, new EntityId(npcId), TalkTopic.AskWashClothes)),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
            if (target.TravelLeaderNpcId is { } travelLeader)
            {
                var canRelease = runner.CanControlNpc(new EntityId(travelLeader));
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.travel.release"),
                    () => runner.EnqueueCommand(new ReleaseTravelPartyCommand(new EntityId(travelLeader),target.Id)),
                    canRelease,canRelease ? null : socialBlocked));
            }
            else
            {
                var blocked = !canOrderSocial || carrier is null
                    ? socialBlocked : TravelInviteBlockedHint(carrier, target);
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.travel.invite"),
                    () => EnqueueOrder(carrier!.Id.Value,new RequestTravelPartyCommand(carrier.Id,target.Id)),
                    blocked is null, blocked));
            }
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.romance"),
                () => EnqueueOrder(carrier!.Id.Value, new RomancePersonCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId),
                    forced: false)),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
            // §153.1: «Подарить» открывает то же окно обмена §128, но отдачей
            // в одну сторону. Отдельного интерфейса передачи нет по замыслу:
            // игрок уже умеет перетаскивать вещь между двумя панелями, и
            // второе такое окно означало бы вторую его версию с багами.
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.gift_person"),
                () => LootTransferPanel.OpenGift(carrier!.Id.Value, npcId),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
        }

        // §149 r3 (#236): «своя» — лагерь ТОЙ, КТО ОТДАЁТ приказ. Буквальная
        // Faction.Colony у игрока лагеря Colony2..Colony6 переворачивала меню:
        // соседка по лагерю уходила в ветку чужой, а чужая — в ветку своей.
        var sameSide = carrier != null &&
            ManualMenuTargets.SameSide(carrier.Faction, target.Faction);

        // §146.12: two explicit diplomatic outcomes over the same command
        // bus. Both directed affinities must be >50%; the simulation repeats
        // every check and owns the actual atomic merge.
        var neighbourCamp = !dead && carrier != null &&
            ManualMenuTargets.NeighbourCamp(carrier.Faction, target.Faction);
        if (!dead && carrier != null && target.Faction == Faction.Outsiders)
        {
            var ready = canOrderSocial && !lying &&
                AffinityTo(carrier, npcId) > 0.5f && AffinityTo(target, carrier.Id.Value) > 0.5f &&
                TrustTo(target, carrier.Id.Value) > 0.5f;
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.recruit_outsider"),
                () => EnqueueOrder(carrier.Id.Value, new MergeCampsCommand(carrier.Id, target.Id, false)),
                ready, ready ? null : Loc.Get("menu.recruit_outsider.requirements")));
        }
        if (neighbourCamp)
        {
            var mutualAffinity = AffinityTo(carrier!, npcId) > 0.50f &&
                AffinityTo(target, carrier!.Id.Value) > 0.50f;
            var closeEnough = HexSpatialMath.Distance(
                carrier.Position, target.Position) <= HexSpatialMath.HexRadius * 2f;
            var canMerge = canOrderSocial && !lying && mutualAffinity && closeEnough;
            var mergeHint = lying
                ? Loc.Get("toast.order_rejected.TargetUnavailable")
                : !mutualAffinity
                    ? Loc.Get("menu.camp_merge.need_relation")
                    : !closeEnough
                        ? Loc.Get("menu.camp_merge.need_nearby")
                        : socialBlocked;
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.camp_merge.invite"),
                () => EnqueueOrder(carrier!.Id.Value, new MergeCampsCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId),
                    useTargetCamp: false)),
                canMerge, canMerge ? null : mergeHint));
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.camp_merge.occupy"),
                () => EnqueueOrder(carrier!.Id.Value, new MergeCampsCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId),
                    useTargetCamp: true)),
                canMerge, canMerge ? null : mergeHint));
        }

        // Обычная помощь (еда/вода/утешение) остаётся отдельной от медицины.
        if (!dead)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.aid"),
                () => OpenAidMenu(mousePos, carrier!.Id.Value, npcId),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.medical_aid"),
                () => EnqueueOrder(carrier!.Id.Value, new MedicalAidCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId))),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.treat_limbs"),
                () => EnqueueOrder(carrier!.Id.Value, new TreatLimbsCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId))),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
        }

        // §167.3: указание. Своей — прямое обещание (SetDirective) и отпуск в
        // ИИ, чтобы смещение аукциона вообще работало; соседке — просьба
        // темой разговора, решает её готовность; чужой — серый пункт.
        if (!dead && !target.IsUnconscious)
        {
            var targetOwned = runner.CanControlNpc(new EntityId(npcId));
            var manyOwnSelected = _selectedColonyIds.Count > 1 && _selectedColonyIds.Contains(npcId);
            var directiveEnabled = targetOwned || (sameSide && canOrderSocial);
            var directiveHint = targetOwned ? null
                : !sameSide ? Loc.Get("menu.directive.not_ally")
                : socialBlocked;
            _entries.Add(new ContextMenuEntry(
                Loc.Get(manyOwnSelected ? "menu.directive.all" : "menu.directive"),
                BuildDirectiveEntries(kind =>
                {
                    if (manyOwnSelected)
                    {
                        var actors = OrderActors();
                        runner.EnqueueCommand(new SetDirectiveCommand(actors, kind));
                        runner.EnqueueCommand(new SetGroupManualControlCommand(actors, false));
                    }
                    else if (targetOwned)
                    {
                        var one = new[] { new EntityId(npcId) };
                        runner.EnqueueCommand(new SetDirectiveCommand(one, kind));
                        runner.EnqueueCommand(new SetGroupManualControlCommand(one, false));
                    }
                    else
                    {
                        EnqueueOrder(carrier!.Id.Value, new TalkToCommand(
                            new EntityId(carrier.Id.Value), new EntityId(npcId),
                            DirectiveMath.TopicOf(kind)));
                    }
                }),
                directiveEnabled, directiveEnabled ? null : directiveHint));
        }

        if (dead || target.IsUnconscious)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.butcher_person"),
                () => OpenConfirmMenu(mousePos, NpcTitle(npcId), "menu.butcher_person.confirm",
                    () => EnqueueOrder(carrier!.Id.Value, new ButcherPersonCommand(
                        new EntityId(carrier.Id.Value), new EntityId(npcId)))),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
        }

        // §121.9 (тёмная фаза): необратимые акты — только через подменю
        // подтверждения, случайный клик не должен запускать ни охоту на
        // соседку (§56), ни сцену травли (§81).
        if (!dead && HexLive.Simulation.Runtime.Spec121.ManualDarkOrdersEnabled &&
            canOrderSocial)
        {
            if (!target.IsUnconscious)
            {
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.dark.prey"),
                    () => OpenConfirmMenu(mousePos, NpcTitle(npcId),
                        "menu.dark.prey.confirm",
                        () => EnqueueOrder(carrier!.Id.Value, new PreyPersonCommand(
                            new EntityId(carrier.Id.Value), new EntityId(npcId))))));
            }
            if (!sameSide && !lying)
            {
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.dark.abuse"),
                    () => OpenConfirmMenu(mousePos, NpcTitle(npcId),
                        "menu.dark.abuse.confirm",
                        () => EnqueueOrder(carrier!.Id.Value, new AbusePersonCommand(
                            new EntityId(carrier.Id.Value), new EntityId(npcId))))));
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.dark.force_romance"),
                    () => OpenConfirmMenu(mousePos, NpcTitle(npcId),
                        "menu.dark.force_romance.confirm",
                        () => EnqueueOrder(carrier!.Id.Value,
                            new RomancePersonCommand(
                                new EntityId(carrier.Id.Value),
                                new EntityId(npcId), forced: true)))));
            }
        }

        if (carrier != null && carrier.CarriedNpcId == npcId)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.put_down_person"),
                () => EnqueueOrder(carrier.Id.Value,
                    new PutDownPersonCommand(new EntityId(carrier.Id.Value)))));
            // §128: «взял — обыскал». Несомый САМИМ носильщиком — легальная
            // цель обыска, обмен идёт прямо в руках.
            if (lying)
            {
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.loot_person"),
                    () => LootTransferPanel.Open(carrier.Id.Value, npcId))
                { Danger = !dead || TheftMath.IsForeignPrivateGround(snapshot, carrier.Faction, target.Tile) });
            }
        }
        // §118.4 r2 (#166): СВОИХ берут на руки всегда — спят они или нет, здоровы
        // или переломаны. Чужой на ногах в руки не даётся: это уже не носилки.
        else if (lying || sameSide || carrier != null && PortalWorld.IsApartmentTile(carrier.Tile) &&
                 PortalWorld.IsApartmentTile(target.Tile) && target.ProfileId == PortalWorld.ResidentProfileId)
        {
            var canCarry = carrier != null && _selectedColonyIds.Count == 1 &&
                carrier.CarriedNpcId is null &&
                target.CarriedByNpcId is null && carrier.Id.Value != npcId;
            var blockedReason = carrier?.CarriedNpcId is not null
                ? Loc.Get("menu.hands_occupied")
                : Loc.Get("menu.select_one_character");
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.carry_person"),
                () => EnqueueOrder(carrier!.Id.Value, new CarryPersonCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId))),
                canCarry, canCarry ? null : blockedReason) { Danger = !lying && !sameSide });
        }
        // §128 r2 (#164): обыскать можно ЛЮБОГО лежащего — мёртвую, спящую,
        // без сознания. Раньше пункт показывался только для живой в отключке, и
        // над телом или спящей в меню оставалось одно «взять на руки».
        if (lying && carrier?.CarriedNpcId != npcId)
        {
            var canLoot = carrier != null && _selectedColonyIds.Count == 1 &&
                carrier.Id.Value != npcId &&
                carrier.CarriedNpcId is null && target.CarriedByNpcId is null;
            // §128: подсказка называет НАСТОЯЩУЮ причину — раньше «на руках у
            // другой» показывало враньё «выберите одного персонажа».
            var blockedReason = carrier?.CarriedNpcId is not null
                ? Loc.Get("menu.hands_occupied")
                : target.CarriedByNpcId is not null
                    ? Loc.Get("menu.carried_by_other")
                    : Loc.Get("menu.select_one_character");
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.loot_person"),
                () => LootTransferPanel.Open(carrier!.Id.Value, npcId),
                canLoot, canLoot ? null : blockedReason)
            { Danger = !dead || carrier != null && TheftMath.IsForeignPrivateGround(snapshot, carrier.Faction, target.Tile) });
        }
        // «Выбрать» — потому что в ручном режиме простой клик по человеку
        // открывает меню, а не переключает выбор: атака по неосторожному
        // клику — ровно то, от чего Kenshi защищается отдельным пунктом.
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.select"),
            () => NpcSelection.Activate(npcId)));

        ContextMenuPanel.Open(mousePos, NpcTitle(npcId), _entries);
    }

    private static string? TravelInviteBlockedHint(NpcSnapshot leader, NpcSnapshot target)
    {
        if (leader.IsFighting || target.IsFighting || leader.TravelLeaderNpcId.HasValue)
            return Loc.Get("toast.order_rejected.TargetUnavailable");
        if (target.IsManualControl) return Loc.Get("menu.travel.target_manual");
        var trust = TrustTo(target, leader.Id.Value);
        if (trust <= TravelPartyMath.RequiredTrust)
            return string.Format(Loc.Get("menu.travel.need_trust"), Mathf.FloorToInt(Mathf.Clamp01(trust) * 100f));
        return TravelPartyMath.IsWithinInviteRange(leader.Position, target.Position)
            ? null : Loc.Get("toast.order_rejected.TravelTargetTooFar");
    }

    private static float TrustTo(NpcSnapshot source, int otherId)
    {
        foreach (var relation in source.RelationshipDetails)
            if (relation.OtherId == otherId) return relation.Trust;
        return 0f;
    }

    private static float AffinityTo(NpcSnapshot source, int otherId)
    {
        foreach (var relation in source.RelationshipDetails)
        {
            if (relation.OtherId == otherId)
            {
                return relation.Affinity;
            }
        }

        return 0f;
    }

    // §121.9: само-меню — то, что колонистка делает сама с собой. Пункты
    // обычно активны: правду о нуждах проверяет симуляция. Питьё отдельно
    // повторяет доступность реальных глотков, чтобы пустая ёмкость не обещала действие.
    private void OpenSelfMenu(Vector2 mousePos, int npcId)
    {
        var runner = _runner;
        if (runner == null)
        {
            return;
        }

        var actor = new EntityId(npcId);
        _entries.Clear();
        void Add(string key, SelfActionKind kind) => _entries.Add(new ContextMenuEntry(
            Loc.Get(key),
            () => EnqueueOrder(npcId, new SelfActionCommand(actor, kind))));
        var selfSnapshot = runner.IsReady ? runner.CreateSnapshot() : null;
        var self = selfSnapshot?.Npcs.Find(candidate => candidate.Id.Value == npcId);
        Add("menu.self.call_help", SelfActionKind.CallForHelp);
        Add("menu.self.treat", SelfActionKind.TreatSelf);
        Add("menu.self.sit", SelfActionKind.GroundSit);
        _entries.Add(SleepMenuEntry(Loc.Get("menu.self.sleep"),
            () => EnqueueOrder(npcId, new SelfActionCommand(actor, SelfActionKind.GroundSleep)), self));
        Add("menu.self.bathe", SelfActionKind.Bathe);
        Add("menu.self.wash", SelfActionKind.WashClothes);
        Add("menu.self.eat", SelfActionKind.EatFromPack);
        var canDrink = ManualMenuTargets.CanDrinkFromPack(self, id =>
            runner.TryGetObjectDefinition(id, out var definition) && definition != null &&
            definition.Interactions.Any(interaction => interaction.Type == InteractionType.Drink));
        _entries.Add(new ContextMenuEntry(Loc.Get(canDrink ? "menu.self.drink" : "menu.drink_empty"),
            () => EnqueueOrder(npcId, new SelfActionCommand(actor, SelfActionKind.DrinkFromPack)),
            canDrink, canDrink ? null : Loc.Get("toast.order_rejected.NothingToDrink")));
        Add("menu.self.go_home", SelfActionKind.GoHome);
        // §167.2: крикнуть всем своим в радиусе — каждая решает сама.
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.shout"),
            BuildDirectiveEntries(
                kind => EnqueueOrder(npcId, new ShoutDirectiveCommand(actor, kind)),
                shout: true)));
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.stop"),
            () => EnqueueOrder(npcId, new StopCommand(actor))));
        ContextMenuPanel.Open(mousePos, NpcTitle(npcId), _entries);
    }

    private static ContextMenuEntry SleepMenuEntry(string label, System.Action activate, NpcSnapshot? actor,
        bool enabled = true, string? hint = null)
    {
        var reason = actor?.ManualSleepRefusal;
        if (!string.IsNullOrEmpty(reason))
            return new ContextMenuEntry($"{label} ({Loc.Get("menu.sleep_refusal." + reason)})", activate, false);
        return new ContextMenuEntry(label, activate, enabled, hint);
    }

    // §167.3: три вида указания v1 (дрова заложены в enum, но пункта пока нет).
    private static List<ContextMenuEntry> BuildDirectiveEntries(
        System.Action<DirectiveKind> issue, bool shout = false)
    {
        var prefix = shout ? "menu.shout." : "menu.directive.";
        return new List<ContextMenuEntry>
        {
            new(Loc.Get(prefix + "build"), () => issue(DirectiveKind.Build)),
            new(Loc.Get(prefix + "stock_food"), () => issue(DirectiveKind.StockFood)),
            new(Loc.Get(prefix + "stock_water"), () => issue(DirectiveKind.StockWater)),
            new(Loc.Get(prefix + "bathe"), () => issue(DirectiveKind.Bathe)),
            new(Loc.Get(prefix + "wash_clothes"), () => issue(DirectiveKind.WashClothes)),
        };
    }

    // §121.9: подтверждение необратимого приказа — второе меню из одного
    // пункта. Клик мимо меню закрывает его, то есть «передумала» бесплатно.
    private void OpenConfirmMenu(
        Vector2 mousePos, string title, string confirmKey, System.Action confirmed)
    {
        _entries.Clear();
        _entries.Add(new ContextMenuEntry(Loc.Get(confirmKey), confirmed));
        ContextMenuPanel.Open(mousePos, title, _entries);
    }

    // §121.9: подменю немедицинской помощи (§53). Три вида всегда активны — правду
    // о припасе и нужде знает симуляция, отказ придёт честным тостом.
    private void OpenAidMenu(Vector2 mousePos, int actorId, int targetId)
    {
        var runner = _runner;
        if (runner == null)
        {
            return;
        }

        _entries.Clear();
        void Add(string key, AidKind kind) => _entries.Add(new ContextMenuEntry(
            Loc.Get(key),
            () => EnqueueOrder(actorId, new AidPersonCommand(
                new EntityId(actorId), new EntityId(targetId), kind))));
        Add("menu.aid.feed", AidKind.Feed);
        Add("menu.aid.hydrate", AidKind.Hydrate);
        Add("menu.aid.console", AidKind.Console);
        ContextMenuPanel.Open(mousePos, NpcTitle(targetId), _entries);
    }

    private void OpenMobMenu(Vector2 mousePos, int mobId)
    {
        var runner = _runner;
        if (runner == null)
        {
            return;
        }

        var me = OrderNpcId;
        _entries.Clear();
        _entries.Add(new ContextMenuEntry(Loc.Get(mobId < 0 ? "goal.Hunt" : "menu.attack"),
            () => EnqueueMobAttack(me, mobId)));

        ContextMenuPanel.Open(mousePos, Loc.Get("menu.target.beast"), _entries);
    }

    // Приказы уходят только ручному подмножеству: сим отфильтровал бы AI-девочек
    // сам, но слать заведомо отклоняемые id и рисовать для них маркер нельзя.
    private List<EntityId> SelectedActors()
    {
        var result = new List<EntityId>(_manualSelectedIds.Count);
        foreach (var id in _manualSelectedIds) result.Add(new EntityId(id));
        return result;
    }

    // Атака из меню — явный приказ игрока: идёт ВСЕМ выделенным колонисткам,
    // а не только ручным, и сама берёт их под управление.
    private List<EntityId> OrderActors()
    {
        var result = new List<EntityId>(_selectedColonyIds.Count);
        foreach (var id in _selectedColonyIds) result.Add(new EntityId(id));
        return result;
    }

    private void EnsureGroupManual()
    {
        if (_runner == null || _manualSelectedIds.Count == _selectedColonyIds.Count)
        {
            return;
        }

        _runner.EnqueueCommand(new SetGroupManualControlCommand(OrderActors(), true));
    }

    private void EnqueueNpcAttack(int single, int target)
    {
        if (_runner == null)
        {
            return;
        }

        if (_selectedColonyIds.Count == 1 && single >= 0)
        {
            EnqueueOrder(single,
                new AttackNpcCommand(new EntityId(single), new EntityId(target)));
        }
        else
        {
            EnsureGroupManual();
            _runner.EnqueueCommand(new GroupAttackNpcCommand(
                OrderActors(), new EntityId(target)));
        }
    }

    private void EnqueueMobAttack(int single, int target)
    {
        if (_runner == null)
        {
            return;
        }

        if (_selectedColonyIds.Count == 1 && single >= 0)
        {
            EnqueueOrder(single, new AttackMobCommand(new EntityId(single), target));
        }
        else
        {
            EnsureGroupManual();
            _runner.EnqueueCommand(new GroupAttackMobCommand(OrderActors(), target));
        }
    }

    private string ObjectTitle(ObjectDefinition definition, string definitionId)
    {
        var key = $"item.{ItemInfo.Slug(definitionId)}.name";
        return Loc.Has(key) ? Loc.Get(key) : definition.DisplayName;
    }

    private string NpcTitle(int npcId)
    {
        var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
        if (snapshot != null)
        {
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value == npcId)
                {
                    return Loc.NpcName(npc.DisplayName);
                }
            }


            foreach (var corpse in snapshot.Corpses)
            {
                if (corpse.Id.Value == npcId)
                {
                    return Loc.NpcName(corpse.DisplayName);
                }
            }
        }

        return Loc.Get("menu.target.person");
    }

    private static bool TryFindPerson(
        WorldSnapshot snapshot, int npcId, out NpcSnapshot person, out bool dead)
    {
        foreach (var npc in snapshot.Npcs)
        {
            if (npc.Id.Value == npcId)
            {
                person = npc;
                dead = false;
                return true;
            }
        }

        foreach (var corpse in snapshot.Corpses)
        {
            if (corpse.Id.Value == npcId)
            {
                person = corpse;
                dead = true;
                return true;
            }
        }

        person = null!;
        dead = false;
        return false;
    }

    private List<string> CarriedItems()
    {
        var items = new List<string>();
        var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
        if (snapshot == null)
        {
            return items;
        }

        foreach (var npc in snapshot.Npcs)
        {
            if (npc.Id.Value != OrderNpcId)
            {
                continue;
            }

            items.AddRange(npc.InventoryItems);
            items.AddRange(npc.HolsteredItems);
            break;
        }

        return items;
    }

    // Bug #312: тот же предикат кражи, что применит симуляция (TheftMath),
    // посчитанный по снапшоту: вещь на приватной земле НЕсоюзного лагеря
    // (5 гексов от его якоря), и владелец вещи — не союзник.
    private static bool IsTheftTarget(WorldSnapshot? snapshot, ObjectSnapshot? target,
        NpcSnapshot? actor, InteractionType interaction) =>
        snapshot != null && target != null && actor != null &&
        TheftMath.IsTheft(snapshot, actor, target, interaction);

    private static string InteractionVerb(InteractionDefinition interaction)
    {
        var actionKey = ActionRequirements.LabelKey(interaction);
        var localized = Loc.Get(actionKey);
        return localized == actionKey
            ? Loc.Get(ActionRequirements.FallbackLabelKey(interaction))
            : localized;
    }

    // ── Земля под курсором ───────────────────────────────────────────────

    private float GroundMarkerY(Float2 point)
    {
        var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
        if (snapshot == null)
        {
            return 0f;
        }

        for (var i = 0; i < snapshot.Tiles.Count; i++)
        {
            var tile = snapshot.Tiles[i];
            if (PointInsideHex(point.X, point.Y, tile.Coord))
            {
                return TileTopY(tile);
            }
        }

        return 0f;
    }

    /// <summary>
    /// Точка на земле под курсором. Пересечение считается с ПЛОСКОСТЬЮ ВЕРХА
    /// каждого тайла и проверяется попаданием в шестиугольник — тем же
    /// способом, что и выбор гекса у камеры: коллайдеров у земли нет, а
    /// высота у тайлов разная.
    /// </summary>
    private bool TryPickGroundPoint(Vector2 mousePos, out Float2 point) =>
        TryPickGroundPoint(mousePos, out point, out _);

    private bool TryPickGroundPoint(
        Vector2 mousePos, out Float2 point, out TileCoord tileCoord)
    {
        point = default;
        tileCoord = default;
        if (_camera == null)
        {
            return false;
        }

        var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
        if (snapshot == null || snapshot.Tiles.Count == 0)
        {
            return false;
        }

        var ray = _camera.ScreenPointToRay(mousePos);
        if (Mathf.Abs(ray.direction.y) < 0.0001f)
        {
            return false;
        }

        var bestT = float.PositiveInfinity;
        var found = false;
        for (var i = 0; i < snapshot.Tiles.Count; i++)
        {
            var tile = snapshot.Tiles[i];
            var t = (TileTopY(tile) - ray.origin.y) / ray.direction.y;
            if (t <= 0f || t >= bestT)
            {
                continue;
            }

            var hit = ray.origin + ray.direction * t;
            if (!PointInsideHex(hit.x, hit.z, tile.Coord))
            {
                continue;
            }

            bestT = t;
            point = new Float2(hit.x, hit.z);
            tileCoord = tile.Coord;
            found = true;
        }

        return found;
    }

    private static float DistanceSquared(Float2 a, Float2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// §173.2 / bug #481: SourceIndex addresses the physical carried list.
    /// InventoryItems is a presentation list where stackable resources are
    /// folded and moved, so only the derived container slots can revalidate it.
    /// </summary>
    private static bool HasCarriedSeedAtSource(
        NpcSnapshot actor, int sourceIndex, string seedDefinitionId)
    {
        if (sourceIndex < 0 || string.IsNullOrEmpty(seedDefinitionId))
        {
            return false;
        }

        foreach (var container in actor.InventoryContainers)
        {
            foreach (var slot in container.Slots)
            {
                if (slot.SourceIndex == sourceIndex &&
                    slot.ItemDefinitionId == seedDefinitionId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Та же ступень высоты, что у камеры и рендерера. Число повторяется в
    // четвёртый раз по всему проекту — не свожу его сюда одной правкой, чтобы
    // §121 не тащил за собой рефакторинг геометрии мира.
    private const float ElevationStep = 0.55f;

    private static float TileTopY(TileSnapshot tile)
    {
        var y = SimulationUnityMapper.TileHeight + tile.Elevation * ElevationStep;
        return tile.Water ? y + ElevationStep * Rendering.SwimVisuals.SurfaceStepOffset : y;
    }

    private static bool PointInsideHex(float x, float z, TileCoord coord)
    {
        var center = HexLive.Simulation.Spatial.HexSpatialMath.TileToWorld(coord);
        var dx = Mathf.Abs(x - center.X);
        var dz = Mathf.Abs(z - center.Y);
        var radius = HexLive.Simulation.Spatial.HexSpatialMath.HexRadius;
        return dz <= radius &&
            HexLive.Simulation.Spatial.HexSpatialMath.Sqrt3 * dx + dz <=
            HexLive.Simulation.Spatial.HexSpatialMath.Sqrt3 * radius;
    }
}

}
