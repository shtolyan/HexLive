using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;

namespace HexLive.Simulation.Runtime
{

// §147: машинерия патрульных слотов. НЕ система реестра — вызывается из
// MobSystem/RabbitSystem, когда слотовый режим включён (WolfSlotsFor>0),
// и ПОЛНОСТЬЮ заменяет их амбиентные спавнеры. Гости ночного рейда §46
// ортогональны: их id не резервировались слотами, хук смерти по ним — no-op.
internal static class MobSlots
{
    private const int RingWaypoints = 8;
    private const int SlotPairwiseMinTiles = 6;

    // §146.6-парные ручки: селекторы по режиму мира (0 = механика выключена,
    // работает старый амбиентный спавнер).
    internal static int WolfSlotsFor(HexLive.Simulation.Bootstrap.GameMode mode) => mode switch
    {
        HexLive.Simulation.Bootstrap.GameMode.BigIsland => WildlifeBalance.BigIslandWolfSlots,
        HexLive.Simulation.Bootstrap.GameMode.HugeIsland => WildlifeBalance.HugeIslandWolfSlots,
        HexLive.Simulation.Bootstrap.GameMode.Maniac => WildlifeBalance.HugeIslandWolfSlots,
        HexLive.Simulation.Bootstrap.GameMode.Islands => WildlifeBalance.IslandsWolfSlots,
        _ => WildlifeBalance.WolfSlots
    };

    internal static int CrabSlotsFor(HexLive.Simulation.Bootstrap.GameMode mode) => mode switch
    {
        HexLive.Simulation.Bootstrap.GameMode.BigIsland => WildlifeBalance.BigIslandCrabSlots,
        HexLive.Simulation.Bootstrap.GameMode.HugeIsland => WildlifeBalance.HugeIslandCrabSlots,
        HexLive.Simulation.Bootstrap.GameMode.Maniac => WildlifeBalance.HugeIslandCrabSlots,
        HexLive.Simulation.Bootstrap.GameMode.Islands => WildlifeBalance.IslandsCrabSlots,
        _ => WildlifeBalance.CrabSlots
    };

    // ── Генерация ────────────────────────────────────────────────────────

    // §147.1: ленивая, в первом проходе системы (паттерн самолечения
    // DreamQueue) — свежий мир и старый сейв получают слоты одинаково.
    internal static void EnsureSlots(WorldState world, string mobId, int target)
    {
        var existing = 0;
        foreach (var slot in world.MobSpawnSlots)
        {
            if (slot.MobId == mobId)
            {
                existing++;
            }
        }

        if (existing >= target)
        {
            return;
        }

        var baseList = SlotHomeBase(world, mobId);
        var excluded = _excludedScratch;
        CollectExcluded(world, baseList, mobId, excluded);
        var available = baseList.Count - excluded.Count;
        if (available <= 0)
        {
            return;
        }

        var pairwise = mobId == Content.MobIds.Crab ? 3 : SlotPairwiseMinTiles;
        var attempt = 0;
        while (existing < target && attempt < target * 12 && available > 0)
        {
            // §158.5: индекс считается по ОТФИЛЬТРОВАННОМУ списку, как и раньше,
            // но сам список не строится: исключённые позиции известны по дискам
            // вокруг людей и лагерей, и i-й допустимый элемент находится
            // пропуском исключённых. Выбранный узел «вынимается» так же —
            // добавлением его позиции к исключённым.
            var pick = (int)(MathUtil.Hash01(world.Seed, existing, attempt++, 1117) * available);
            pick = System.Math.Min(pick, available - 1);
            var position = SkipExcluded(pick, excluded);
            var junctionId = baseList[position];
            InsertSorted(excluded, position);
            available--;
            var tile = world.Junctions.Items[junctionId].Tiles[0];
            var tooClose = false;
            foreach (var slot in world.MobSpawnSlots)
            {
                if (slot.MobId == mobId && slot.Ring.Count > 0 &&
                    HexSpatialMath.HexDistance(slot.Ring[0].Tile, tile) < pairwise)
                {
                    tooClose = true;
                    break;
                }
            }

            if (tooClose)
            {
                continue;
            }

            var slot2 = new MobSpawnSlot
            {
                SlotId = NextSlotId(world),
                MobId = mobId,
                State = MobSlotState.Virtual,
                ReservedMobId = mobId == Content.MobIds.Crab
                    ? world.NextRabbitId++
                    : world.NextMobId++,
                HomeJunction = junctionId,
                StoredHealth = Content.MobCatalog.For(mobId).MaxHealth,
                CycleIndex = 0,
            };
            BakeRing(world, slot2);
            if (slot2.Ring.Count == 0)
            {
                continue;
            }

            world.MobSpawnSlots.Add(slot2);
            existing++;
        }
    }

    private static int NextSlotId(WorldState world)
    {
        var max = 0;
        foreach (var slot in world.MobSpawnSlots)
        {
            max = System.Math.Max(max, slot.SlotId);
        }

        return max + 1;
    }

    // §147.1: фильтр кандидатов — те же правила, что у TrySpawnDog/-Rabbit:
    // не blocked/indoor/all-water, вдали от NPC и стоянок; крабы — у воды.
    private static bool IsSlotHomeBase(WorldState world, Junction junction, bool crab) =>
        !junction.Blocked && junction.Tiles.Count > 0 &&
        !IsIndoorTile(world, junction.Tiles[0]) &&
        !SpatialQueries.IsAllWaterJunction(world, junction.Id) &&
        (!crab || NearWater(world, junction.Tiles[0], 2));

    private sealed class JunctionIdOrder : IComparer<JunctionId>
    {
        public static readonly JunctionIdOrder Instance = new();

        public int Compare(JunctionId a, JunctionId b) => a.Value.CompareTo(b.Value);
    }

    internal static List<JunctionId> SlotHomeBase(WorldState world, string mobId)
    {
        var crab = mobId == Content.MobIds.Crab;

        // §147 PERF: the static half of the filter (blocked/indoor/all-water,
        // crabs near water) is topology — cached per TopologyVersion, exactly
        // like MobForbiddenJunctions. The base list is sorted by id once;
        // filtering below preserves that order, so the final candidate list is
        // byte-identical to the old scan-then-sort.
        var baseList = crab ? world.Caches.CrabSlotHomeBase : world.Caches.LandSlotHomeBase;
        var builtVersion = crab
            ? world.Caches.CrabSlotHomeBaseBuiltVersion
            : world.Caches.LandSlotHomeBaseBuiltVersion;
        if (builtVersion != world.TopologyVersion)
        {
            // §158.5: догон по журналу топологии — пересматриваются только
            // изменившиеся узлы; список остаётся отсортированным по id, так
            // что итог байт-в-байт равен полной перестройке.
            var cursor = crab
                ? world.Caches.CrabSlotHomeBaseJournalCursor
                : world.Caches.LandSlotHomeBaseJournalCursor;
            var changed = world.Caches.TopologyChangedScratch;
            var full = WorldTopology.CatchUp(world, ref builtVersion, ref cursor, changed);
            if (full)
            {
                baseList.Clear();
                foreach (var junction in world.Junctions.Items.Values)
                {
                    if (IsSlotHomeBase(world, junction, crab))
                    {
                        baseList.Add(junction.Id);
                    }
                }

                baseList.Sort((a, b) => a.Value.CompareTo(b.Value));
            }
            else
            {
                foreach (var id in changed)
                {
                    var keep = world.Junctions.Items.TryGetValue(id, out var junction) &&
                               IsSlotHomeBase(world, junction, crab);
                    var at = baseList.BinarySearch(id, JunctionIdOrder.Instance);
                    if (keep && at < 0)
                    {
                        baseList.Insert(~at, id);
                    }
                    else if (!keep && at >= 0)
                    {
                        baseList.RemoveAt(at);
                    }
                }
            }

            if (crab)
            {
                world.Caches.CrabSlotHomeBaseBuiltVersion = builtVersion;
                world.Caches.CrabSlotHomeBaseJournalCursor = cursor;
            }
            else
            {
                world.Caches.LandSlotHomeBaseBuiltVersion = builtVersion;
                world.Caches.LandSlotHomeBaseJournalCursor = cursor;
            }
        }

        return baseList;
    }

    private static readonly List<int> _excludedScratch = new();
    private static readonly HashSet<JunctionId> _excludedSeen = new();

    /// <summary>§158.5: позиции в базовом списке узлов, которые НЕ подходят
    /// домом слота (ближе радиуса к девушке или лагерю), отсортированные.
    /// Собираются по дискам вокруг людей и лагерей: узел с Tiles[0] внутри
    /// диска числится в списке узлов этого тайла, так что диски накрывают все
    /// исключённые, а предикат FarFromPeople сверяет каждого точно. Цена —
    /// площадь дисков, не размер мира (раньше: два миллиона узлов × двенадцать
    /// расстояний на каждый переезд слота).</summary>
    internal static void CollectExcluded(
        WorldState world, List<JunctionId> baseList, string mobId, List<int> excluded)
    {
        excluded.Clear();
        _excludedSeen.Clear();
        var crab = mobId == Content.MobIds.Crab;
        var minFromNpc = crab
            ? System.Math.Max(WildlifeBalance.RabbitSpawnMinDistanceFromNpc,
                WildlifeBalance.CrabMaterializeRadiusTiles + 2)
            : System.Math.Max(WildlifeBalance.DogSpawnMinDistanceFromNpc,
                WildlifeBalance.MobMaterializeRadiusTiles + 2);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            AddExcludedDisc(world, baseList, mobId, npc.Tile, minFromNpc, excluded);
        }

        if (Spec72.Enabled)
        {
            foreach (var home in world.FactionHomes)
            {
                AddExcludedDisc(world, baseList, mobId, home.Value,
                    Spec72.DogSpawnMinDistanceFromCamp, excluded);
            }
        }

        excluded.Sort();
    }

    private static void AddExcludedDisc(
        WorldState world, List<JunctionId> baseList, string mobId, TileCoord center,
        int radius, List<int> excluded)
    {
        for (var dq = -radius + 1; dq <= radius - 1; dq++)
        {
            for (var dr = -radius + 1; dr <= radius - 1; dr++)
            {
                var coord = new TileCoord(center.Q + dq, center.R + dr);
                if (HexSpatialMath.HexDistance(coord, center) >= radius ||
                    !world.Tiles.Items.TryGetValue(coord, out var tile))
                {
                    continue;
                }

                foreach (var junctionId in tile.Junctions)
                {
                    if (!_excludedSeen.Add(junctionId) || FarFromPeople(world, junctionId, mobId))
                    {
                        continue;
                    }

                    var at = baseList.BinarySearch(junctionId, JunctionIdOrder.Instance);
                    if (at >= 0)
                    {
                        excluded.Add(at);
                    }
                }
            }
        }
    }

    /// <summary>Позиция i-го НЕисключённого элемента базового списка.</summary>
    internal static int SkipExcluded(int index, List<int> excludedSorted)
    {
        var position = index;
        for (var i = 0; i < excludedSorted.Count; i++)
        {
            if (excludedSorted[i] <= position)
            {
                position++;
            }
            else
            {
                break;
            }
        }

        return position;
    }

    private static void InsertSorted(List<int> sorted, int value)
    {
        var at = sorted.BinarySearch(value);
        if (at < 0)
        {
            sorted.Insert(~at, value);
        }
    }

    /// <summary>§147.3: дом слота дальше радиуса материализации + запас — свежий
    /// слот обязан родиться ПРЕВЬЮ, а не сразу живым зверем под ногами у
    /// девушки; и не ближе DogSpawnMinDistanceFromCamp к лагерю (§72).</summary>
    internal static bool FarFromPeople(WorldState world, JunctionId junctionId, string mobId)
    {
        var crab = mobId == Content.MobIds.Crab;
        if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || junction.Tiles.Count == 0)
        {
            return false;
        }

        var tile = junction.Tiles[0];
        var minFromNpc = crab
            ? System.Math.Max(WildlifeBalance.RabbitSpawnMinDistanceFromNpc,
                WildlifeBalance.CrabMaterializeRadiusTiles + 2)
            : System.Math.Max(WildlifeBalance.DogSpawnMinDistanceFromNpc,
                WildlifeBalance.MobMaterializeRadiusTiles + 2);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (HexSpatialMath.HexDistance(tile, npc.Tile) < minFromNpc)
            {
                return false;
            }
        }

        if (Spec72.Enabled)
        {
            foreach (var home in world.FactionHomes)
            {
                if (HexSpatialMath.HexDistance(tile, home.Value) < Spec72.DogSpawnMinDistanceFromCamp)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool NearWater(WorldState world, TileCoord tile, int radius)
    {
        // §147 PERF: the answer is "is any WATER tile within `radius` of this
        // tile" — walk the 19-coord neighbourhood with dictionary lookups (the
        // PerceptionSystem ring shape) instead of scanning all 4032 tiles per
        // junction; the old form also boxed two enums per HasFlag, which alone
        // was 19.6 GB of garbage on the first BigIsland tick.
        for (var dq = -radius; dq <= radius; dq++)
        {
            var lo = System.Math.Max(-radius, -dq - radius);
            var hi = System.Math.Min(radius, -dq + radius);
            for (var dr = lo; dr <= hi; dr++)
            {
                var coord = new TileCoord(tile.Q + dq, tile.R + dr);
                if (world.Tiles.Items.TryGetValue(coord, out var state) &&
                    (state.Flags & TileFlags.Water) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // §147.1: кольцо печётся один раз (BFS от дома мимо запретных узлов),
    // сохраняется и едет клиенту — превью и материализация читают одни байты.
    private static void BakeRing(WorldState world, MobSpawnSlot slot)
    {
        slot.Ring.Clear();
        if (!world.Junctions.Items.TryGetValue(slot.HomeJunction, out var home))
        {
            return;
        }

        var homeTile = home.Tiles.Count > 0 ? home.Tiles[0] : default;
        // §147 PERF: world-owned scratch — a shortfall tick used to bake up to
        // target*12 rings, each allocating its own List+HashSet+Queue.
        var reach = world.Caches.RingReachScratch;
        reach.Clear();
        reach.Add(home);
        var visited = world.Caches.RingVisitedScratch;
        visited.Clear();
        visited.Add(home.Id);
        var queue = world.Caches.RingQueueScratch;
        queue.Clear();
        queue.Enqueue(home);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var neighborId in current.Neighbors)
            {
                if (visited.Contains(neighborId) ||
                    !world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                {
                    continue;
                }

                visited.Add(neighborId);
                var allWater = SpatialQueries.IsAllWaterJunction(world, neighborId);
                if (neighbor.Tiles.Count == 0 || neighbor.Blocked || allWater ||
                    neighbor.Door || IsIndoorTile(world, neighbor.Tiles[0]))
                {
                    continue;
                }

                if (HexSpatialMath.HexDistance(neighbor.Tiles[0], homeTile) >
                    WildlifeBalance.MobPatrolRadiusTiles)
                {
                    continue;
                }

                reach.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        if (reach.Count < 2)
        {
            return;
        }

        // Маршрут — СЛУЧАЙНАЯ ПРОГУЛКА короткими шагами (~1 тайл), как моб в
        // ММОРПГ: туда-сюда в радиусе, без правильной геометрии. Первая версия
        // раскладывала K точек по углу вокруг дома — получалось кольцо с
        // далёкими соседями, и превью носилось по нему волчком (замечание
        // игрока). Соседние точки близко ⇒ при фиксированной длительности
        // отрезка скорость — неторопливый шаг.
        reach.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        var walker = home;
        slot.Ring.Add(new PatrolWaypoint
        {
            Junction = home.Id,
            Tile = homeTile,
            Position = home.WorldPosition,
        });

        var stepCandidates = world.Caches.RingStepScratch;
        for (var i = 1; i < RingWaypoints; i++)
        {
            stepCandidates.Clear();
            foreach (var junction in reach)
            {
                var dx = junction.WorldPosition.X - walker.WorldPosition.X;
                var dy = junction.WorldPosition.Y - walker.WorldPosition.Y;
                var sq = dx * dx + dy * dy;
                // 0.9..3.4 wu — от «не топтаться на месте» до ~1.3 тайла.
                if (sq >= 0.81f && sq <= 11.56f)
                {
                    stepCandidates.Add(junction);
                }
            }

            if (stepCandidates.Count == 0)
            {
                break;
            }

            var pick = (int)(MathUtil.Hash01(
                world.Seed, slot.SlotId, slot.CycleIndex * 131 + i, 1137) *
                stepCandidates.Count);
            pick = System.Math.Min(pick, stepCandidates.Count - 1);
            walker = stepCandidates[pick];
            slot.Ring.Add(new PatrolWaypoint
            {
                Junction = walker.Id,
                Tile = walker.Tiles[0],
                Position = walker.WorldPosition,
            });
        }

        if (slot.Ring.Count < 2)
        {
            slot.Ring.Clear();
        }
    }

    // ── Жизненный цикл ───────────────────────────────────────────────────

    // §147.3: материализация. Радиус — это радиус СУЩЕСТВОВАНИЯ, не агра:
    // фильтры убежищ не дублируются, их применит обычный RunDog на том же
    // medium-тике. previewPosition — точка превью (середина сегмента), чтобы
    // живой зверь появился РОВНО там, где его рисовал клиент.
    internal static bool TryMaterialize(
        WorldState world, MobSpawnSlot slot, int radiusTiles,
        out PatrolWaypoint waypoint, out Float2 previewPosition)
    {
        waypoint = default;
        previewPosition = default;
        MobPreview.PreviewPose(
            world.Seed, slot, world.Tick,
            WildlifeBalance.MobPreviewSegmentTicks,
            WildlifeBalance.MobPreviewPauseChance,
            out previewPosition, out _, out var nearestIndex);
        if (slot.Ring.Count == 0)
        {
            return false;
        }

        var previewTile = slot.Ring[nearestIndex].Tile;
        var seen = false;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f &&
                HexSpatialMath.HexDistance(previewTile, npc.Tile) <= radiusTiles)
            {
                seen = true;
                break;
            }
        }

        if (!seen)
        {
            return false;
        }

        // Вэйпоинт мог зарасти (дом на кольце): fallback — первый свободный.
        for (var i = 0; i < slot.Ring.Count; i++)
        {
            var candidate = slot.Ring[(nearestIndex + i) % slot.Ring.Count];
            if (world.Junctions.Items.TryGetValue(candidate.Junction, out var junction) &&
                !junction.Blocked)
            {
                waypoint = candidate;
                if (i > 0)
                {
                    // Превью-точка лежала на заросшем сегменте — появляемся
                    // на живом вэйпоинте, без лерпа в стену.
                    previewPosition = candidate.Position;
                }

                return true;
            }
        }

        // Кольцо мертво целиком — детерминированный ре-хоуминг вместо спавна.
        Rehome(world, slot);
        return false;
    }

    // §147.4: смерть → кулдаун. Вызывается из ОБОИХ свипов смерти волка
    // (medium в MobSystem и fast в AnimalCombatSystem) и из крабьего.
    internal static void OnMobRemoved(WorldState world, string mobId, int removedId, int cooldownTicks)
    {
        foreach (var slot in world.MobSpawnSlots)
        {
            if (slot.State == MobSlotState.Live && slot.MobId == mobId &&
                slot.ReservedMobId == removedId)
            {
                slot.State = MobSlotState.Cooldown;
                slot.CooldownUntilTick = world.Tick + cooldownTicks;
                slot.CycleIndex++;
                return;
            }
        }
    }

    // §147.4: кулдаун истёк — новый дом подальше от людей и лагерей, новое
    // кольцо по ТЕКУЩЕЙ топологии, полное здоровье, снова превью.
    internal static void Rehome(WorldState world, MobSpawnSlot slot)
    {
        var baseList = SlotHomeBase(world, slot.MobId);
        var excluded = _excludedScratch;
        CollectExcluded(world, baseList, slot.MobId, excluded);
        var available = baseList.Count - excluded.Count;
        if (available <= 0)
        {
            // Некуда: остаёмся в кулдауне до следующего medium-прохода.
            slot.State = MobSlotState.Cooldown;
            slot.CooldownUntilTick = world.Tick + WildlifeBalance.DogRespawnCheckTicks;
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, slot.SlotId, slot.CycleIndex, 1131) * available);
        pick = System.Math.Min(pick, available - 1);
        var home = baseList[SkipExcluded(pick, excluded)];
        slot.HomeJunction = home;
        BakeRing(world, slot);
        if (slot.Ring.Count == 0)
        {
            slot.State = MobSlotState.Cooldown;
            slot.CooldownUntilTick = world.Tick + WildlifeBalance.DogRespawnCheckTicks;
            return;
        }

        slot.StoredHealth = Content.MobCatalog.For(slot.MobId).MaxHealth;
        slot.State = MobSlotState.Virtual;
    }

    private static bool IsIndoorTile(WorldState world, TileCoord tile) =>
        world.Tiles.Items.TryGetValue(tile, out var state) &&
        (state.Flags & TileFlags.Indoor) != 0;
}

}
