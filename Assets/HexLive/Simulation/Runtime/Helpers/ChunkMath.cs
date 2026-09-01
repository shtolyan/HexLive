using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
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
        !world.Caches.ActiveChunksComputed ||
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
        if (ChunkBalance.ChunkSleepEnabled &&
            world.Caches.ActiveChunksComputed &&
            world.Chunks.Items.TryGetValue(ChunkOf(tile), out var chunk))
        {
            return chunk.LastSimulatedTick;
        }

        // Ни разу не штампованный чанк считается СВЕЖИМ, а не проспавшим с
        // сотворения мира. Догонять там нечего: счётчики (топливо, влага)
        // заводит только чья-то работа, а работать в чанке, где никогда никого
        // не было, некому; всё остальное там якорное через SpawnTick и
        // догоняется само. Обратное соглашение выдало бы нетронутому краю
        // острова возраст мира на первом же визите.
        return world.Tick - world.SlowIntervalTicks;
    }

    /// <summary>
    /// Сколько slow-тактов чанк проспал СТРОГО до текущего: такты
    /// <c>(SleepWindowStart, world.Tick)</c>. Текущий такт не входит — его
    /// отрабатывает живой код системы своим прежним способом.
    /// </summary>
    internal static int SleptSlowTicks(WorldState world, TileCoord tile)
    {
        var slow = world.SlowIntervalTicks;
        if (slow <= 0)
        {
            return 0;
        }

        var from = SleepWindowStart(world, tile);
        return MultiplesBelow(world.Tick, slow) - MultiplesBelow(from + 1, slow);
    }

    /// <summary>Сколько кратных <paramref name="step"/> лежит в <c>[0, limit)</c>.</summary>
    private static int MultiplesBelow(int limit, int step) =>
        limit <= 0 ? 0 : (limit + step - 1) / step;

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
        var ordered = world.Caches.ActiveChunksOrdered;
        active.Clear();
        ordered.Clear();
        world.Caches.ActiveChunksComputed = ChunkBalance.ChunkSleepEnabled;
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
                    var chunk = new ChunkCoord(cq, cr);
                    if (active.Add(chunk))
                    {
                        ordered.Add(chunk);
                    }
                }
            }
        }

        // Порядок обхода не может браться у HashSet: у него его нет. Сортировка
        // по (Cq, Cr) делает его свойством решётки, а не порядка, в котором
        // словарь NPC выдал своих колонисток.
        ordered.Sort((a, b) => a.Cq != b.Cq ? a.Cq.CompareTo(b.Cq) : a.Cr.CompareTo(b.Cr));
    }

    /// <summary>
    /// Индекс «объекты по чанкам» существует и построен на текущей стороне
    /// чанка. Зовётся движком перед слоем Slow — то есть перед единственными
    /// читателями индекса.
    /// <para>
    /// Полная перестройка нужна ровно в трёх случаях: механику включили,
    /// сторону чанка покрутили ручкой, мир пришёл из сейва (индекс выводится из
    /// <c>Tile</c> целиком, поэтому своего формата в блобе у него нет). Во всех
    /// прочих тактах он поддерживается по месту — в тех же трёх точках, что и
    /// <c>ObjectsByTile</c>.
    /// </para>
    /// </summary>
    internal static void EnsureObjectIndex(WorldState world)
    {
        var caches = world.Caches;
        if (!ChunkBalance.ChunkSleepEnabled)
        {
            // Выключили — индекс не просто перестаёт быть нужным, он обязан
            // ИСЧЕЗНУТЬ: иначе включение обратно нашло бы совпавшую сторону,
            // решило, что строить нечего, и поехало на записях, протухших за
            // время работы без поддержки.
            if (caches.ObjectsByChunkSize != 0)
            {
                caches.ObjectsByChunk.Clear();
                caches.ObjectsByChunkSize = 0;
            }

            return;
        }

        if (caches.ObjectsByChunkSize == ChunkBalance.ChunkSizeTiles)
        {
            return;
        }

        caches.ObjectsByChunk.Clear();
        caches.ObjectsByChunkSize = ChunkBalance.ChunkSizeTiles;
        foreach (var obj in world.Entities.Objects.Values)
        {
            AddToObjectIndex(world, obj);
        }
    }

    /// <summary>Объект появился в мире (или переехал на новый тайл).</summary>
    internal static void AddToObjectIndex(WorldState world, WorldObjectState obj)
    {
        var caches = world.Caches;
        if (caches.ObjectsByChunkSize == 0)
        {
            return; // индекс не построен — его соберёт EnsureObjectIndex целиком
        }

        var chunk = ChunkOf(obj.Tile);
        if (!caches.ObjectsByChunk.TryGetValue(chunk, out var ids))
        {
            caches.ObjectsByChunk[chunk] = ids = new System.Collections.Generic.List<ObjectId>();
        }

        // ⭐ Вставка ПО ВОЗРАСТАНИЮ id, а не в хвост. Порядок этого списка — это
        // порядок, в котором мир получает свои плоды и хоронит свои трупы, и он
        // обязан быть одинаковым у мира, прожившего тысячу тиков, и у того же
        // мира, поднятого из сейва: перестройка после загрузки идёт по словарю,
        // чей порядок не наш. Списки короткие (десятки на чанк), так что цена —
        // сдвиг нескольких ссылок.
        var at = 0;
        while (at < ids.Count && ids[at].Value < obj.Id.Value)
        {
            at++;
        }

        if (at < ids.Count && ids[at].Value == obj.Id.Value)
        {
            return;
        }

        ids.Insert(at, obj.Id);
    }

    /// <summary>Объект исчез из мира (или уезжает со старого тайла).</summary>
    internal static void RemoveFromObjectIndex(WorldState world, ObjectId id, TileCoord tile)
    {
        var caches = world.Caches;
        if (caches.ObjectsByChunkSize == 0)
        {
            return;
        }

        if (caches.ObjectsByChunk.TryGetValue(ChunkOf(tile), out var ids))
        {
            ids.Remove(id);
        }
    }

    /// <summary>
    /// Объекты, которые система обязана обойти на этом такте, в
    /// ДЕТЕРМИНИРОВАННОМ порядке.
    /// <para>
    /// ⭐ Здесь и только здесь живёт разница между двумя мирами. При выключенной
    /// механике это весь ростер в его собственном порядке — то есть ровно то,
    /// что системы обходили до §156, откуда и берётся построчное совпадение
    /// golden_trace. При включённой обход идёт по бодрым чанкам, и спящий объект
    /// не стоит ничего: ни работы, ни обращения к хешу.
    /// </para>
    /// <para>
    /// Список берётся у вызывающего и им же переиспользуется — свежий на каждый
    /// вызов, потому что системы слоя порождают и убивают объекты друг у друга
    /// под ногами.
    /// </para>
    /// </summary>
    internal static void CollectTickable(
        WorldState world, System.Collections.Generic.List<WorldObjectState> into)
    {
        into.Clear();
        var caches = world.Caches;
        if (!ChunkBalance.ChunkSleepEnabled || !caches.ActiveChunksComputed)
        {
            foreach (var obj in world.Entities.Objects.Values)
            {
                into.Add(obj);
            }

            return;
        }

        foreach (var chunk in caches.ActiveChunksOrdered)
        {
            if (!caches.ObjectsByChunk.TryGetValue(chunk, out var ids))
            {
                continue;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (world.Entities.Objects.TryGetValue(ids[i], out var obj))
                {
                    into.Add(obj);
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
        if (!ChunkBalance.ChunkSleepEnabled || !world.Caches.ActiveChunksComputed)
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
