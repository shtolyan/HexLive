using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §156: чья это клетка решётки, спит ли она и с какого тика. Единственное
/// место, где тайл превращается в чанк, — второе такое означало бы две решётки,
/// которые однажды разъедутся.
/// </summary>
internal static class ChunkMath
{
    /// <summary>
    /// Деление с округлением к минус бесконечности. Усечённое деление склеило бы
    /// <c>Q ∈ [-7..7]</c> в один чанк вдвое шире прочих — и ровно на нулевом
    /// меридиане, где стоит стартовый лагерь.
    /// </summary>
    internal static int FloorDiv(int a, int size)
    {
        var q = a / size;
        return a % size != 0 && (a < 0) != (size < 0) ? q - 1 : q;
    }

    internal static ChunkCoord ChunkOf(TileCoord tile) =>
        new ChunkCoord(
            FloorDiv(tile.Q, ChunkBalance.ChunkSizeTiles),
            FloorDiv(tile.R, ChunkBalance.ChunkSizeTiles));

    /// <summary>
    /// Горячий вопрос всех систем с политикой <c>PerChunk</c>. При выключенной
    /// механике — константа true, поэтому фильтры в системах ничего не стоят и
    /// ничего не меняют.
    /// </summary>
    internal static bool IsAwake(WorldState world, TileCoord tile) =>
        !ChunkBalance.ChunkSleepEnabled ||
        world.Caches.ActiveChunks.Contains(ChunkOf(tile));

    /// <summary>
    /// Начало окна догона: система обязана досчитать чанку всё, что случилось в
    /// <c>(результат, world.Tick]</c>.
    /// <para>
    /// ⚠️ При ВЫКЛЮЧЕННОЙ механике штампов нет вовсе, и наивный возврат нуля
    /// заставил бы формулы интегрировать погоду с сотворения мира. Возврат
    /// «предыдущего slow-такта» — не оптимизация, а условие эквивалентности:
    /// именно он делает окно равным одному такту и арифметику — прежней.
    /// </para>
    /// </summary>
    internal static int SleepWindowStart(WorldState world, TileCoord tile)
    {
        if (!ChunkBalance.ChunkSleepEnabled)
        {
            return world.Tick - world.SlowIntervalTicks;
        }

        return world.Chunks.Items.TryGetValue(ChunkOf(tile), out var chunk)
            ? chunk.LastSimulatedTick
            : 0;
    }

    /// <summary>
    /// §156.1: радиус пробуждения ОДНОЙ колонистки. Персональный, потому что
    /// зоркость — характеристика (§125): у наблюдательной мир обязан жить
    /// дальше, чем у ненаблюдательной. Пол закрывает то, что зоркостью не
    /// выражается вовсе — смертный крик §57 и материализацию зверя §147.3.
    /// </summary>
    internal static int WakeRadiusTiles(NPCState npc) =>
        System.Math.Max(
            PerceptionMath.RadiusTiles(npc),
            System.Math.Max(
                WildlifeBalance.MobMaterializeRadiusTiles,
                ChunkBalance.MinWakeRadiusTiles))
        + ChunkBalance.WakeRadiusMarginTiles;

    /// <summary>
    /// Пересобирает активный набор по живым NPC. Зовёт движок в начале тика —
    /// не система: заводить сороковую <see cref="ISimulationSystem"/> значило бы
    /// трогать реестр §30 и его прибитый порядок ради работы, у которой нет
    /// своего места в слоях.
    /// <para>
    /// Диск радиуса R целиком лежит в axial-прямоугольнике
    /// <c>[Q−R..Q+R] × [R−R..R+R]</c>, и мы будим все чанки этого
    /// прямоугольника. Пере-покрытие по углам ромба узаконено (§156.1): лишний
    /// разбуженный чанк стоит перебора, недоразбуженный — вранья.
    /// </para>
    /// </summary>
    internal static void RebuildActiveChunks(WorldState world)
    {
        var active = world.Caches.ActiveChunks;
        active.Clear();
        if (!ChunkBalance.ChunkSleepEnabled)
        {
            return;
        }

        var size = ChunkBalance.ChunkSizeTiles;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // Трупы не будят: они лежат в Corpses, а мёртвая в живом ростере
            // держится ещё один тик после смерти (см. EntitiesByTile).
            if (npc.Health <= 0f)
            {
                continue;
            }

            var radius = WakeRadiusTiles(npc);
            var q0 = FloorDiv(npc.Tile.Q - radius, size);
            var q1 = FloorDiv(npc.Tile.Q + radius, size);
            var r0 = FloorDiv(npc.Tile.R - radius, size);
            var r1 = FloorDiv(npc.Tile.R + radius, size);
            for (var cq = q0; cq <= q1; cq++)
            {
                for (var cr = r0; cr <= r1; cr++)
                {
                    active.Add(new ChunkCoord(cq, cr));
                }
            }
        }
    }

    /// <summary>
    /// Штамп «этот чанк досчитан по текущий тик». Ставится ПОСЛЕ slow-слоя:
    /// системы этого такта обязаны увидеть ещё старое окно, иначе догон
    /// пропустит сам себя.
    /// </summary>
    internal static void StampSimulated(WorldState world)
    {
        if (!ChunkBalance.ChunkSleepEnabled)
        {
            return;
        }

        foreach (var chunk in world.Caches.ActiveChunks)
        {
            if (!world.Chunks.Items.TryGetValue(chunk, out var state))
            {
                world.Chunks.Items[chunk] = state = new ChunkState();
            }

            state.LastSimulatedTick = world.Tick;
        }
    }
}

}
