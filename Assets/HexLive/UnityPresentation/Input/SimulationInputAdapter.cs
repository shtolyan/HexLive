#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
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
/// ⭐ Ручной режим читается ИЗ СНАПШОТА, а не из своего поля. Тумблер живёт в
/// симуляции, и она же авторитет: кнопка, помнящая своё, рано или поздно
/// показывает одно, пока персонаж делает другое.
/// </summary>
public sealed class SimulationInputAdapter : MonoBehaviour
{
    [SerializeField] private SimulationRunnerBehaviour? _runner;

    // Тот же радиус, которым камера выбирает NPC: два разных числа значили бы,
    // что подсветилось одно, а кликнулось другое. После §121.1 r2 применяется
    // только к зверям и к людям БЕЗ готового вида (первые кадры после спавна,
    // примитивы прототипных сцен) — у остальных был честный точный луч.
    private const float PickRadiusPixels = 70f;
    private const float DoubleClickSeconds = 0.30f;
    private const float DoubleClickRadiusPixels = 18f;
    // Server lease may be configured as low as five seconds. A selected manual
    // actor proves the viewer is still actively presenting that control, so a
    // cheap idempotent heartbeat every two seconds keeps ownership alive while
    // still letting a disconnected viewer or a cleared selection expire.
    private const float RemoteLeaseHeartbeatSeconds = 2f;

    private Camera? _camera;
    private HexWorldRenderer? _worldRenderer;
    private WorldObjectView? _hovered;
    private int _hoveredNpcId = -1;
    private int _hoveredMobId = -1;
    private float _lastGroundClickTime = float.NegativeInfinity;
    private Vector2 _lastGroundClickPosition;
    private float _nextRemoteLeaseHeartbeatAt;

    private readonly List<ContextMenuEntry> _entries = new();
    private readonly List<int> _selectedColonyIds = new();
    private readonly List<int> _manualSelectedIds = new();

    public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

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
        RenewRemoteLeaseForSelection();
        if (!CommandMode || PointerBlocked())
        {
            ClearHover();
            return;
        }

        UpdateHover();
    }

    private void OnDisable()
    {
        ClearHover();
        ResetGroundClickCadence();
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
            if (npc.IsManualControl) _manualSelectedIds.Add(npc.Id.Value);
        }

        _selectedColonyIds.Sort();
        _manualSelectedIds.Sort();
        if (_selectedColonyIds.Count == 1 && _manualSelectedIds.Count == 1)
        {
            ManualNpcId = _manualSelectedIds[0];
        }
    }

    private static bool PointerBlocked() =>
        NpcSelection.PointerOverUi ||
        TacticalMapPanel.PointerOverMap ||
        HexInspectorPanel.PointerOverPanel ||
        ContextMenuPanel.BlocksWorldPointer ||
        LootTransferPanel.IsOpen ||
        GameMenu.IsOpen ||
        EndSummaryPanel.IsOpen;

    private void RenewRemoteLeaseForSelection()
    {
        var now = Time.unscaledTime;
        if (_runner == null || !_runner.Link.IsRemote ||
            !_runner.SupportsNpcCommands || _manualSelectedIds.Count == 0)
        {
            // The next genuine manual selection renews immediately instead of
            // waiting a full interval with a possibly near-expired server lease.
            _nextRemoteLeaseHeartbeatAt = now;
            return;
        }

        if (now < _nextRemoteLeaseHeartbeatAt)
        {
            return;
        }

        _nextRemoteLeaseHeartbeatAt = now + RemoteLeaseHeartbeatSeconds;
        if (_manualSelectedIds.Count == 1)
        {
            _runner.EnqueueCommand(new SetManualControlCommand(
                new EntityId(_manualSelectedIds[0]), true));
            return;
        }

        _runner.EnqueueCommand(new SetGroupManualControlCommand(
            SelectedActors(), true));
    }

    // ── Наведение ────────────────────────────────────────────────────────

    private void UpdateHover()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null || _camera == null)
        {
            ClearHover();
            return;
        }

        var mousePos = mouse.position.ReadValue();
        var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;

        PickTarget(snapshot, mousePos, out _hoveredNpcId, out _hoveredMobId, out var objectHit);

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

    private void ClearHover()
    {
        _hovered?.SetHighlighted(false);
        _hovered = null;
        _hoveredNpcId = -1;
        _hoveredMobId = -1;
    }

    // Предикаты пикера держим полями: они уходят делегатами в
    // WorldObjectPicker каждый кадр наведения, и создавать их заново значило бы
    // мусорить в Update.
    private System.Func<WorldObjectView, bool>? _pickEligible;
    private System.Func<WorldObjectView, bool>? _pickDefers;

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

        var layer = view.gameObject.layer;
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
        _runner != null &&
        _runner.TryGetObjectDefinition(view.ContextDefinitionId, out var definition) &&
        definition != null && definition.Interactions.Count > 0;

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
                !view.TryRaycastVisibleGeometry(ray, distance, out var hitDistance))
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
        if (_runner == null)
        {
            return false;
        }

        // Открытое меню закрывается кликом мимо себя — и на этом клик кончается:
        // иначе то же нажатие тут же отдало бы приказ идти под меню. Гейт режима
        // стоит НИЖЕ: меню могло остаться висеть после выключения 🎮.
        if (ContextMenuPanel.IsOpen)
        {
            ContextMenuPanel.Close();
            ResetGroundClickCadence();
            return true;
        }

        if (!CommandMode)
        {
            return false;
        }

        if (_hoveredNpcId >= 0)
        {
            ResetGroundClickCadence();
            // §121.9: клик по себе — меню самодействий, не приказ и не выбор.
            if (_hoveredNpcId == OrderNpcId)
            {
                OpenSelfMenu(mousePos, _hoveredNpcId);
            }
            else
            {
                OpenNpcMenu(mousePos, _hoveredNpcId);
            }

            return true;
        }

        if (_hoveredMobId >= 0)
        {
            ResetGroundClickCadence();
            OpenMobMenu(mousePos, _hoveredMobId);
            return true;
        }

        if (_hovered != null)
        {
            ResetGroundClickCadence();
            OpenObjectMenu(mousePos, _hovered);
            return true;
        }

        if (TryPickGroundPoint(mousePos, out var point))
        {
            var run = ConsumeGroundDoubleClick(mousePos, Time.unscaledTime);
            if (_selectedColonyIds.Count == 1 && ManualNpcId >= 0)
            {
                _runner.EnqueueCommand(
                    new MoveToCommand(new EntityId(ManualNpcId), point, run));
            }
            else
            {
                _runner.EnqueueCommand(new GroupMoveCommand(SelectedActors(), point, run));
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
            if (!manual.Contains(actor.Value))
            {
                runner.EnqueueCommand(new SetManualControlCommand(actor, true));
            }

            runner.EnqueueCommand(new MoveToCommand(actor, point, run));
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

        PickTarget(snapshot, mousePos, out var npcId, out var mobId, out var objectHit);
        if (npcId >= 0)
        {
            ResetGroundClickCadence();
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

        if (mobId >= 0)
        {
            ResetGroundClickCadence();
            OpenMobMenu(mousePos, mobId);
            return true;
        }

        if (objectHit != null)
        {
            ResetGroundClickCadence();
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

        EnsureManual(actorId);
        _runner.EnqueueCommand(command);
    }

    // The first click is dispatched immediately as Walk. If a second release
    // lands close enough and soon enough, its Run order atomically replaces
    // the first one through the normal command queue. Unscaled time keeps the
    // gesture usable while the simulation itself is paused.
    private bool ConsumeGroundDoubleClick(Vector2 position, float now)
    {
        var elapsed = now - _lastGroundClickTime;
        var isDouble = elapsed >= 0f && elapsed <= DoubleClickSeconds &&
            Vector2.Distance(position, _lastGroundClickPosition) <= DoubleClickRadiusPixels;
        if (isDouble)
        {
            ResetGroundClickCadence();
            return true;
        }

        _lastGroundClickTime = now;
        _lastGroundClickPosition = position;
        return false;
    }

    private void ResetGroundClickCadence()
    {
        _lastGroundClickTime = float.NegativeInfinity;
        _lastGroundClickPosition = default;
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
            (clicked.Contents.Count > 0 ||
             definition.HasTag(ObjectTags.Remains) ||
             definition.HasTag("Container") ||
             definition.HasTag(ObjectTags.Wardrobe) ||
             definition.InventoryCapacity > 0);

        var carried = CarriedItems();
        var actorId = OrderNpcId;
        var actor = new EntityId(actorId);
        _entries.Clear();
        foreach (var interaction in definition.Interactions)
        {
            // §128.5 / bug #199: a container's Loot action opens the two-sided
            // inventory panel; the old one-item interaction must not create a
            // duplicate identically named menu entry beside it.
            if (isContainer && interaction.Type == InteractionType.Loot) continue;
            var ok = HasEveryTool(carried, interaction);
            var objectId = view.ContextObjectId;
            var type = interaction.Type;
            _entries.Add(new ContextMenuEntry(
                Loc.Get($"interaction.{type}.verb"),
                () => EnqueueOrder(actorId,
                    new InteractCommand(actor, new ObjectId(objectId), type)),
                ok,
                ok ? null : Loc.Get("menu.missing_tool")));
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

        // §128.5: ОБЫСКАТЬ ВЕЩЬ — истлевшее тело, снятый рюкзак, аптечку.
        // Пустой мешок или шкаф тоже открывается: это не только источник,
        // но и назначение для перетаскивания вещей из левой панели.
        if (isContainer)
        {
            var containerId = view.ContextObjectId;
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.loot_person"),
                () => LootTransferPanel.OpenContainer(actorId, containerId)));
        }

        if (_entries.Count == 0)
        {
            return;
        }

        ContextMenuPanel.Open(mousePos, ObjectTitle(definition, view.ContextDefinitionId), _entries);
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

        var lying = dead || target.IsUnconscious || target.IsDying || target.IsFainted ||
            target.IsPlayingDead || target.CurrentInteraction == "Sleep";

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
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.romance"),
                () => EnqueueOrder(carrier!.Id.Value, new RomancePersonCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId),
                    forced: false)),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
        }

        // §146.12: two explicit diplomatic outcomes over the same command
        // bus. Both directed affinities must be >50%; the simulation repeats
        // every check and owns the actual atomic merge.
        var neighbourCamp = !dead && carrier != null &&
            carrier.Faction == Faction.Colony &&
            target.Faction is Faction.Colony2 or Faction.Colony3 or
                Faction.Colony4 or Faction.Colony5 or Faction.Colony6;
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

        // Помочь можно и лежащей без сознания (§53.8 стабилизация) — поэтому
        // условие мягче, чем у разговора.
        if (!dead)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.aid"),
                () => OpenAidMenu(mousePos, carrier!.Id.Value, npcId),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
        }

        if (!dead && lying)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.treat_limbs"),
                () => EnqueueOrder(carrier!.Id.Value, new TreatLimbsCommand(
                    new EntityId(carrier.Id.Value), new EntityId(npcId))),
                canOrderSocial, canOrderSocial ? null : socialBlocked));
        }

        // §121.9 (тёмная фаза): необратимые акты — только через подменю
        // подтверждения, случайный клик не должен запускать ни охоту на
        // соседку (§56), ни сцену травли (§81).
        if (!dead && HexLive.Simulation.Runtime.Spec121.ManualDarkOrdersEnabled &&
            canOrderSocial)
        {
            if (target.Faction == Faction.Colony && !target.IsUnconscious)
            {
                _entries.Add(new ContextMenuEntry(Loc.Get("menu.dark.prey"),
                    () => OpenConfirmMenu(mousePos, NpcTitle(npcId),
                        "menu.dark.prey.confirm",
                        () => EnqueueOrder(carrier!.Id.Value, new PreyPersonCommand(
                            new EntityId(carrier.Id.Value), new EntityId(npcId))))));
            }
            else if (target.Faction != Faction.Colony && !lying)
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
                    () => LootTransferPanel.Open(carrier.Id.Value, npcId)));
            }
        }
        // §118.4 r2 (#166): СВОИХ берут на руки всегда — спят они или нет, здоровы
        // или переломаны. Чужой на ногах в руки не даётся: это уже не носилки.
        else if (lying || target.Faction == Faction.Colony)
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
                canCarry, canCarry ? null : blockedReason));
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
                canLoot, canLoot ? null : blockedReason));
        }
        // «Выбрать» — потому что в ручном режиме простой клик по человеку
        // открывает меню, а не переключает выбор: атака по неосторожному
        // клику — ровно то, от чего Kenshi защищается отдельным пунктом.
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.select"),
            () => NpcSelection.Select(npcId)));

        ContextMenuPanel.Open(mousePos, NpcTitle(npcId), _entries);
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
    // всегда активны: правду («нечем перевязаться», «не в бою», «нечего
    // стирать») знает симуляция, отказ придёт честным тостом причины.
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
        Add("menu.self.call_help", SelfActionKind.CallForHelp);
        Add("menu.self.treat", SelfActionKind.TreatSelf);
        Add("menu.self.sit", SelfActionKind.GroundSit);
        Add("menu.self.sleep", SelfActionKind.GroundSleep);
        Add("menu.self.bathe", SelfActionKind.Bathe);
        Add("menu.self.wash", SelfActionKind.WashClothes);
        Add("menu.self.eat", SelfActionKind.EatFromPack);
        Add("menu.self.drink", SelfActionKind.DrinkFromPack);
        Add("menu.self.go_home", SelfActionKind.GoHome);
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.stop"),
            () => EnqueueOrder(npcId, new StopCommand(actor))));
        ContextMenuPanel.Open(mousePos, NpcTitle(npcId), _entries);
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

    // §121.9: подменю видов помощи (§53). Пять видов всегда активны — правду
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
        Add("menu.aid.treat", AidKind.Treat);
        Add("menu.aid.medicate", AidKind.Medicate);
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
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.attack"),
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

    /// <summary>
    /// ЛЮБОЙ из перечисленных инструментов — это ANY-OF, ровно как гейт на
    /// входе в ExecutionSystem: полено колется топором ИЛИ ножом. Серость
    /// пункта обязана совпадать с тем, что скажет симуляция, иначе игрок
    /// кликает по доступному на вид действию и получает отказ.
    /// </summary>
    private static bool HasEveryTool(List<string> carried, InteractionDefinition interaction)
    {
        if (interaction.RequiredCapabilities.Count == 0)
        {
            return true;
        }

        foreach (var capability in interaction.RequiredCapabilities)
        {
            for (var i = 0; i < carried.Count; i++)
            {
                if (GearCatalog.For(carried[i]).Has(capability))
                {
                    return true;
                }
            }
        }

        return false;
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
    private bool TryPickGroundPoint(Vector2 mousePos, out Float2 point)
    {
        point = default;
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
            found = true;
        }

        return found;
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
