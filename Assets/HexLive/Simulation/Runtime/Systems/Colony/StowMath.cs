using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §133: где колонистка раздевается перед купанием и куда складывает одежду.
///
/// <para>
/// Раньше она раздевалась прямо у воды, и куча оставалась на берегу: прерванное
/// купание = брошенные вещи на другом конце острова, а сама она ходила голой,
/// теряя карманы (а с ними и слоты инвентаря). Теперь порядок предпочтений
/// такой: ГАРДЕРОБ в доме → сушилка → просто свободная точка у дома → и лишь
/// если дома нет вовсе, остаётся прежнее поведение у воды.
/// </para>
/// </summary>
public static class StowMath
{
    /// <summary>Куда встать и на что вешать; null — дома нет, работаем по-старому.</summary>
    public readonly struct UndressSpot
    {
        public UndressSpot(JunctionId stand, ObjectId? stowObject)
        {
            Stand = stand;
            StowObject = stowObject;
        }

        /// <summary>Джанкшен, на котором она стоит и раздевается.</summary>
        public JunctionId Stand { get; }

        /// <summary>Гардероб/сушилка, если нашлись; иначе вещи ложатся на землю.</summary>
        public ObjectId? StowObject { get; }
    }

    /// <summary>
    /// Место для раздевания. Приоритет — гардероб, потом сушилка, потом просто
    /// точка у дома. Возвращает null, если дома нет или до него не дойти.
    /// </summary>
    public static UndressSpot? FindUndressSpot(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from ||
            ColonyQueries.Home(world, npc.Faction) is not { } home)
        {
            return null;
        }

        WorldObjectState best = null;
        var bestRank = int.MaxValue;
        var bestDistance = float.MaxValue;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Junctions.Count == 0 ||
                HexSpatialMath.HexDistance(obj.Tile, home) > Spec133.HomeStowRadiusTiles ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            // Дом важнее двора: гардероб выигрывает у сушилки, даже если сушилка
            // ближе (прямое решение игрока — «если есть дом, раздеваться дома»).
            var rank = def.Tags.Contains(ObjectTags.Wardrobe) ? 0
                : obj.DefinitionId == ContentIds.DryingRack ? 1
                : -1;
            if (rank < 0 || ExecutionSystem.RackIsFull(world, obj) ||
                !Connectivity.Reachable(
                    world, from, obj.Junctions[0], PlanningSystem.CanUseRoutineTraversal(npc)))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(
                world.Junctions.Items[obj.Junctions[0]].WorldPosition, npc.Position);
            if (rank < bestRank || (rank == bestRank && distance < bestDistance))
            {
                bestRank = rank;
                bestDistance = distance;
                best = obj;
            }
        }

        if (best != null && StandFor(world, npc, best.Junctions[0]) is { } stand)
        {
            return new UndressSpot(stand, best.Id);
        }

        // Ни гардероба, ни сушилки — но раздеться у дома всё равно лучше, чем на
        // берегу: куча остаётся там, где колония ходит каждый день.
        return FreeJunctionOnTile(world, npc, home) is { } homeStand
            ? new UndressSpot(homeStand, null)
            : (UndressSpot?)null;
    }

    /// <summary>
    /// Место у станции: сам её джанкшен, если свободен (гардероб и сушилка
    /// ничего не блокируют, поэтому встать можно прямо на него), иначе соседний.
    ///
    /// <para>
    /// ⭐ Функция обязана быть ЧИСТО ЧИТАЮЩЕЙ. Первая версия звала
    /// <c>TryReserveBesideJunction</c>, а тот резервирует точку как побочный
    /// эффект: запрос «а где бы раздеться» молча занимал джанкшены у дома на
    /// каждой попытке спланировать купание, и мир расходился ещё до первого
    /// захода в воду (соак сида 12345: петли 0.1% → 2.4%, провалы планов
    /// 1.2% → 8.6%). Резервирует пусть тот, кто строит план.
    /// </para>
    /// </summary>
    private static JunctionId? StandFor(WorldState world, NPCState npc, JunctionId anchor)
    {
        if (npc.CurrentJunction is { } current && current.Equals(anchor))
        {
            return anchor;
        }

        if (SpatialQueries.IsJunctionFree(world, anchor) &&
            world.Junctions.Items.TryGetValue(anchor, out var junction) && !junction.Blocked)
        {
            return anchor;
        }

        SpatialQueries.CollectStandableAround(world, anchor, _rimScratch, 96,
            SpatialQueries.BesideReach(0f), null, InteractionReach.RimMode);
        JunctionId? best = null;
        var bestDistance = float.MaxValue;
        foreach (var rim in _rimScratch)
        {
            if (!SpatialQueries.IsJunctionFree(world, rim) ||
                !world.Junctions.Items.TryGetValue(rim, out var candidate) || candidate.Blocked)
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(candidate.WorldPosition, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = rim;
            }
        }

        return best;
    }

    private static readonly System.Collections.Generic.List<JunctionId> _rimScratch = new();

    private static JunctionId? FreeJunctionOnTile(WorldState world, NPCState npc, TileCoord tile)
    {
        if (npc.CurrentJunction is not { } from ||
            !world.Tiles.Items.TryGetValue(tile, out var homeTile))
        {
            return null;
        }

        JunctionId? best = null;
        var bestDistance = float.MaxValue;
        foreach (var junctionId in homeTile.Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                junction.Blocked || !SpatialQueries.IsJunctionFree(world, junctionId) ||
                !Connectivity.Reachable(
                    world, from, junctionId, PlanningSystem.CanUseRoutineTraversal(npc)))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = junctionId;
            }
        }

        return best;
    }
}

}
