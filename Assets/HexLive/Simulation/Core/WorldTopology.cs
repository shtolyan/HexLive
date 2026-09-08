using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Core
{

/// <summary>
/// §158.2: журнал топологии. Каждая смена <see cref="Junction.Blocked"/> /
/// <see cref="Junction.Door"/> оставляет здесь id узла, и потребитель
/// (связность, кэш замурованных швов, списки кандидатов слотов зверей)
/// пересчитывает ТОЛЬКО эти узлы вместо полного обхода графа. На «Островах»
/// граф — 2.5 млн узлов, и полный обход стоил 2–3 с на каждое брошенное
/// бревно; журнал делает цену пересчёта функцией числа изменений, не размера
/// мира.
/// </summary>
public sealed class TopologyJournal
{
    /// <summary>Сколько записей журнал держит, прежде чем отрезать старую
    /// половину. Отставший потребитель (курсор ниже <see cref="BaseIndex"/>)
    /// перестраивается целиком — это безопасно, просто дорого.</summary>
    public const int MaxEntries = 65536;

    /// <summary>Узлы, у которых менялись Blocked/Door, в порядке изменений.
    /// Повторы допустимы: потребитель сверяет текущее состояние узла со своим
    /// и пропускает узел, у которого ничего не изменилось.</summary>
    public List<JunctionId> Entries { get; } = new();

    /// <summary>Абсолютный индекс <c>Entries[0]</c>. Курсоры потребителей —
    /// абсолютные, чтобы обрезка журнала их не сдвигала.</summary>
    public int BaseIndex { get; set; }

    /// <summary>Версия топологии, на которой последний раз просили полную
    /// перестройку (<see cref="WorldTopology.InvalidateAll"/>): изменение,
    /// которое журнал описать не может — worldgen, загрузка сейва, смена флагов
    /// тайлов (Indoor) при достройке хижины. Потребитель, собранный раньше,
    /// обязан перестроиться целиком.</summary>
    public int FullInvalidationVersion { get; set; }

    /// <summary>Сколько раз просили полную перестройку — метрика прогона:
    /// на бодром мире это должно быть «загрузка плюс достроенные хижины».</summary>
    public int FullInvalidations { get; set; }

    public int EndIndex => BaseIndex + Entries.Count;

    internal HashSet<JunctionId> DedupScratch { get; } = new();
}

/// <summary>
/// §158.2: единственная дверь для записи проходимости узла. Прямое
/// <c>junction.Blocked = …</c> вне worldgen и загрузки сейва запрещено гейтом
/// <c>TopologyWriteLint</c>: такая запись не попадает в журнал, и связность
/// молча остаётся прошлой.
/// </summary>
public static class WorldTopology
{
    public static void SetBlocked(WorldState world, Junction junction, bool blocked)
    {
        if (junction.Blocked == blocked)
        {
            return;
        }

        junction.Blocked = blocked;
        Note(world, junction.Id);
    }

    public static void SetDoor(WorldState world, Junction junction, bool door)
    {
        if (junction.Door == door)
        {
            return;
        }

        junction.Door = door;
        Note(world, junction.Id);
    }

    /// <summary>Узел изменился (Blocked/Door): версия растёт, журнал получает
    /// запись. Все прежние <c>TopologyVersion++</c> при точечных изменениях —
    /// это оно.</summary>
    public static void Note(WorldState world, JunctionId junction)
    {
        world.TopologyVersion++;
        var journal = world.Topology;
        journal.Entries.Add(junction);
        if (journal.Entries.Count > TopologyJournal.MaxEntries)
        {
            var drop = journal.Entries.Count / 2;
            journal.Entries.RemoveRange(0, drop);
            journal.BaseIndex += drop;
        }
    }

    /// <summary>Сменились флаги ТАЙЛА (Indoor при достройке хижины): каждый
    /// его узел попадает в журнал, и кэши, читающие «в помещении ли узел»,
    /// пересматривают только их. Проходимость не менялась — связность такие
    /// записи пропускает.</summary>
    public static void NoteTile(WorldState world, TileCoord tile)
    {
        if (!world.Tiles.Items.TryGetValue(tile, out var state))
        {
            return;
        }

        foreach (var junctionId in state.Junctions)
        {
            Note(world, junctionId);
        }
    }

    /// <summary>Изменение, которого журнал не описывает (bulk worldgen,
    /// загрузка, ремонт при загрузке): все потребители перестраиваются
    /// целиком при следующем обращении.</summary>
    public static void InvalidateAll(WorldState world)
    {
        world.TopologyVersion++;
        var journal = world.Topology;
        journal.BaseIndex = journal.EndIndex;
        journal.Entries.Clear();
        journal.FullInvalidationVersion = world.TopologyVersion;
        journal.FullInvalidations++;
    }

    /// <summary>
    /// Догон потребителя до текущей версии. Возвращает <c>true</c>, если он
    /// обязан перестроиться ЦЕЛИКОМ (никогда не строился, отстал от обрезки
    /// журнала или пропустил <see cref="InvalidateAll"/>); иначе кладёт в
    /// <paramref name="changed"/> узлы, изменившиеся с прошлого догона (без
    /// повторов, в порядке первого упоминания). В обоих случаях курсор и
    /// версия потребителя переводятся на «сейчас»: перестройку или пересчёт
    /// вызывающий делает сразу же, это его обязанность.
    /// </summary>
    public static bool CatchUp(
        WorldState world, ref int builtVersion, ref int cursor, List<JunctionId> changed)
    {
        changed.Clear();
        var journal = world.Topology;
        var full = builtVersion <= 0 ||
                   builtVersion < journal.FullInvalidationVersion ||
                   cursor < journal.BaseIndex ||
                   cursor > journal.EndIndex;
        if (!full)
        {
            var seen = journal.DedupScratch;
            seen.Clear();
            var from = cursor - journal.BaseIndex;
            for (var i = from; i < journal.Entries.Count; i++)
            {
                var id = journal.Entries[i];
                if (seen.Add(id))
                {
                    changed.Add(id);
                }
            }
        }

        builtVersion = world.TopologyVersion;
        cursor = journal.EndIndex;
        return full;
    }
}

}
