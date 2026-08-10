#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
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
    // что подсветилось одно, а кликнулось другое.
    private const float PickRadiusPixels = 70f;

    private Camera? _camera;
    private HexWorldRenderer? _worldRenderer;
    private WorldObjectView? _hovered;
    private int _hoveredNpcId = -1;
    private int _hoveredMobId = -1;

    private readonly List<ContextMenuEntry> _entries = new();
    private readonly List<int> _selectedColonyIds = new();
    private readonly List<int> _manualSelectedIds = new();

    public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

    /// <summary>Кем сейчас управляет игрок, или -1.</summary>
    public int ManualNpcId { get; private set; } = -1;

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
        if (!CommandMode || PointerBlocked())
        {
            ClearHover();
            return;
        }

        UpdateHover();
    }

    private void OnDisable() => ClearHover();

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
                npc.Faction != Faction.Colony || npc.Health <= 0f)
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
        HexInspectorPanel.PointerOverPanel ||
        ContextMenuPanel.PointerOverPanel ||
        GameMenu.IsOpen ||
        EndSummaryPanel.IsOpen;

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

        // Живое — вперёд неживого: промахнуться по человеку, попав в куст за
        // его спиной, обиднее, чем наоборот.
        _hoveredNpcId = PickNpcUnderCursor(snapshot, mousePos);
        _hoveredMobId = _hoveredNpcId >= 0 ? -1 : PickMobUnderCursor(snapshot, mousePos);

        var objectHit = _hoveredNpcId < 0 && _hoveredMobId < 0
            ? PickObjectUnderCursor(mousePos)
            : null;

        if (!ReferenceEquals(objectHit, _hovered))
        {
            _hovered?.SetHighlighted(false);
            _hovered = objectHit;
            _hovered?.SetHighlighted(true);
        }
    }

    private void ClearHover()
    {
        _hovered?.SetHighlighted(false);
        _hovered = null;
        _hoveredNpcId = -1;
        _hoveredMobId = -1;
    }

    private WorldObjectView? PickObjectUnderCursor(Vector2 mousePos)
    {
        if (_camera == null)
        {
            return null;
        }

        var ray = _camera.ScreenPointToRay(mousePos);
        WorldObjectView? best = null;
        var bestDistance = float.MaxValue;

        var all = WorldObjectView.All;
        for (var i = 0; i < all.Count; i++)
        {
            var view = all[i];
            if (view == null || view.ObjectId < 0 || !HasContextActions(view))
            {
                continue;
            }

            if (view.TryIntersect(ray, out var distance) && distance < bestDistance)
            {
                bestDistance = distance;
                best = view;
            }
        }

        return best;
    }

    // Decorative/resource producers can visually enclose a real ground item:
    // a herb bush encloses its shed leaf, and furniture can overlap dropped
    // clothes. Such a view has no contextual action of its own and must not
    // swallow the ray before the actionable object behind it is considered.
    // The definition catalog remains the single eligibility source used again
    // by OpenObjectMenu and ManualCommandExecutor; this is only hit selection.
    private bool HasContextActions(WorldObjectView view) =>
        _runner != null &&
        _runner.TryGetObjectDefinition(view.DefinitionId, out var definition) &&
        definition != null && definition.Interactions.Count > 0;

    private int PickNpcUnderCursor(WorldSnapshot? snapshot, Vector2 mousePos)
    {
        if (snapshot == null || _camera == null)
        {
            return -1;
        }

        if (_worldRenderer == null)
        {
            _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
        }

        if (_worldRenderer != null)
        {
            var ray = _camera.ScreenPointToRay(mousePos);
            var bestRayDistance = float.PositiveInfinity;
            var rayHitId = -1;
            foreach (var person in People(snapshot))
            {
                if (NpcSelection.Contains(person.Id.Value) ||
                    !_worldRenderer.TryGetActorView(person.Id.Value, out var view) ||
                    !view.TryRaycastVisibleGeometry(ray, bestRayDistance, out var hitDistance))
                {
                    continue;
                }

                bestRayDistance = hitDistance;
                rayHitId = person.Id.Value;
            }

            if (rayHitId >= 0)
            {
                return rayHitId;
            }
        }

        var bestId = -1;
        var bestDistance = PickRadiusPixels;
        foreach (var npc in People(snapshot))
        {
            if (NpcSelection.Contains(npc.Id.Value))
            {
                continue; // сама себе не цель
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

    private int PickMobUnderCursor(WorldSnapshot? snapshot, Vector2 mousePos)
    {
        if (snapshot == null || _camera == null)
        {
            return -1;
        }

        var bestId = -1;
        var bestDistance = PickRadiusPixels;
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
            return true;
        }

        if (!CommandMode)
        {
            return false;
        }

        if (_hoveredNpcId >= 0)
        {
            OpenNpcMenu(mousePos, _hoveredNpcId);
            return true;
        }

        if (_hoveredMobId >= 0)
        {
            OpenMobMenu(mousePos, _hoveredMobId);
            return true;
        }

        if (_hovered != null)
        {
            OpenObjectMenu(mousePos, _hovered);
            return true;
        }

        if (TryPickGroundPoint(mousePos, out var point))
        {
            if (_selectedColonyIds.Count == 1 && ManualNpcId >= 0)
            {
                _runner.EnqueueCommand(new MoveToCommand(new EntityId(ManualNpcId), point));
            }
            else
            {
                _runner.EnqueueCommand(new GroupMoveCommand(SelectedActors(), point));
            }
            DestinationMarker.Show(SimulationUnityMapper.ToUnityPosition(point, GroundMarkerY(point)));
            return true;
        }

        return false;
    }

    private void OpenObjectMenu(Vector2 mousePos, WorldObjectView view)
    {
        var runner = _runner;
        if (runner == null ||
            !runner.TryGetObjectDefinition(view.DefinitionId, out var definition) ||
            definition == null)
        {
            return;
        }

        if (_selectedColonyIds.Count != 1 || ManualNpcId < 0)
        {
            _entries.Clear();
            _entries.Add(new ContextMenuEntry(
                Loc.Get("menu.select_one_character"), () => { }, false,
                Loc.Get("menu.select_one_character")));
            ContextMenuPanel.Open(
                mousePos, ObjectTitle(definition, view.DefinitionId), _entries);
            return;
        }

        var carried = CarriedItems();
        var actor = new EntityId(ManualNpcId);
        _entries.Clear();
        foreach (var interaction in definition.Interactions)
        {
            var ok = HasEveryTool(carried, interaction);
            var objectId = view.ObjectId;
            var type = interaction.Type;
            _entries.Add(new ContextMenuEntry(
                Loc.Get($"interaction.{type}.verb"),
                () => runner.EnqueueCommand(
                    new InteractCommand(actor, new ObjectId(objectId), type)),
                ok,
                ok ? null : Loc.Get("menu.missing_tool")));
        }

        if (_entries.Count == 0)
        {
            return;
        }

        ContextMenuPanel.Open(mousePos, ObjectTitle(definition, view.DefinitionId), _entries);
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

        var me = ManualNpcId;
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
                () => EnqueueNpcAttack(runner, me, npcId)));
        }

        var lying = dead || target.IsUnconscious || target.IsDying || target.IsFainted ||
            target.IsPlayingDead || target.CurrentInteraction == "Sleep";
        if (carrier != null && carrier.CarriedNpcId == npcId)
        {
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.put_down_person"),
                () => runner.EnqueueCommand(
                    new PutDownPersonCommand(new EntityId(carrier.Id.Value)))));
        }
        else if (lying)
        {
            var canCarry = carrier != null && _selectedColonyIds.Count == 1 &&
                _manualSelectedIds.Count == 1 && carrier.CarriedNpcId is null &&
                target.CarriedByNpcId is null;
            var blockedReason = carrier?.CarriedNpcId is not null
                ? Loc.Get("menu.hands_occupied")
                : Loc.Get("menu.select_one_character");
            _entries.Add(new ContextMenuEntry(Loc.Get("menu.carry_person"),
                () => runner.EnqueueCommand(new CarryPersonCommand(
                    new EntityId(carrier!.Id.Value), new EntityId(npcId))),
                canCarry, canCarry ? null : blockedReason));
        }
        // «Выбрать» — потому что в ручном режиме простой клик по человеку
        // открывает меню, а не переключает выбор: атака по неосторожному
        // клику — ровно то, от чего Kenshi защищается отдельным пунктом.
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.select"),
            () => NpcSelection.Select(npcId)));

        ContextMenuPanel.Open(mousePos, NpcTitle(npcId), _entries);
    }

    private void OpenMobMenu(Vector2 mousePos, int mobId)
    {
        var runner = _runner;
        if (runner == null)
        {
            return;
        }

        var me = ManualNpcId;
        _entries.Clear();
        _entries.Add(new ContextMenuEntry(Loc.Get("menu.attack"),
            () => EnqueueMobAttack(runner, me, mobId)));

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

    private void EnqueueNpcAttack(SimulationRunnerBehaviour runner, int single, int target)
    {
        if (_selectedColonyIds.Count == 1 && single >= 0)
        {
            runner.EnqueueCommand(new AttackNpcCommand(new EntityId(single), new EntityId(target)));
        }
        else
        {
            runner.EnqueueCommand(new GroupAttackNpcCommand(
                SelectedActors(), new EntityId(target)));
        }
    }

    private void EnqueueMobAttack(SimulationRunnerBehaviour runner, int single, int target)
    {
        if (_selectedColonyIds.Count == 1 && single >= 0)
        {
            runner.EnqueueCommand(new AttackMobCommand(new EntityId(single), target));
        }
        else
        {
            runner.EnqueueCommand(new GroupAttackMobCommand(SelectedActors(), target));
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
            if (npc.Id.Value != ManualNpcId)
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
