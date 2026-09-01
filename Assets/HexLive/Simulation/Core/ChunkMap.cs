using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Core
{

/// <summary>§156: что мир помнит про один чанк.</summary>
public sealed class ChunkState
{
    /// <summary>
    /// Последний тик, на котором slow-слой досчитал этот чанк. Единый якорь
    /// «чанк спал с тика X» для процессов, у которых своего якоря нет (топливо
    /// костра, водосбор, влага).
    /// <para>
    /// Отсутствие записи в карте = 0 = «не симулировался с сотворения мира», и
    /// это корректно: в нетронутом чанке лежат только объекты worldgen, уже
    /// якорные через <c>SpawnTick</c>.
    /// </para>
    /// </summary>
    public int LastSimulatedTick { get; set; }
}

/// <summary>
/// §156: карта чанков. Пуста, пока механика выключена, и содержит запись только
/// про те чанки, которые хоть раз были активны, — то есть растёт по следам
/// колонии, а не по площади острова.
/// </summary>
public sealed class ChunkMap
{
    public Dictionary<ChunkCoord, ChunkState> Items { get; } = new();
}

}
