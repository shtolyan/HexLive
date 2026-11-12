using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §120.15: строитель идёт к ТОЙ детали дома, которую строит, а не к якорю
/// здания. Дом по плану — один build-site-агрегат с одним якорным junction в
/// центре якорного гекса, поэтому все девушки сходились в одну точку и «строили»
/// дальнюю стену с другого конца дома.
///
/// <para>
/// Деталь выбирается ближайшая к строителю из тех, с которыми сейчас можно
/// работать; та же ближайшая деталь получает установленный unit
/// (<see cref="BuildingRules.AssignDeliveredMaterial"/> c параметром near) —
/// она строит то, у чего стоит. Точка стояния — ближайший к детали свободный
/// достижимый узел, с любой стороны стены. Если рядом встать негде, кольцо
/// поиска расширяется; если не нашлось совсем — остаётся прежний подход к
/// якорю здания.
/// </para>
/// </summary>
internal static class BuildWorkPoint
{
    /// <summary>Кольца поиска места у детали: вплотную (один шаг суб-сетки от
    /// следа), затем полгекса, затем гекс.</summary>
    private static readonly float[] Reaches =
    {
        SpatialQueries.StandStepAllowance,
        HexSpatialMath.HexRadius,
        HexSpatialMath.HexRadius * 2f
    };

    /// <summary>Дальше этого от любой рабочей детали стройка здания не
    /// начинается — последнее кольцо <see cref="Reaches"/> плюс допуск на
    /// привязку к узлу.</summary>
    public static float MaxWorkDistance => HexSpatialMath.HexRadius * 2f + 0.4f;

    public static bool AppliesTo(WorldObjectState site) =>
        site != null && BuildSiteMath.IsSite(site) &&
        BuildSiteMath.IsArchitecturalBuilding(site.BuildProduct);

    /// <summary>Мировая позиция модуля дома: якорь здания плюс локальный сдвиг
    /// в шестишаговом базисе footprint (§120.2).</summary>
    public static Float2 ModulePosition(WorldState world, WorldObjectState owner, ArchitectureElementState element)
    {
        var origin = owner.Junctions.Count > 0 &&
                     world.Junctions.Items.TryGetValue(owner.Junctions[0], out var anchor)
            ? anchor.WorldPosition
            : HexSpatialMath.TileToWorld(owner.Tile);
        var radians = owner.RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Float2(
            origin.X + element.LocalX * cos - element.LocalZ * sin,
            origin.Y + element.LocalX * sin + element.LocalZ * cos);
    }

    private static bool Workable(ArchitectureElementState element) =>
        element.DemolitionPlanned || element.Buildable && !element.Complete;

    /// <summary>Позиция ближайшей к строителю детали, с которой сейчас можно
    /// работать (достройка либо разборка).</summary>
    public static bool TryNearestPiece(WorldState world, WorldObjectState site, Float2 from, out Float2 piece)
    {
        piece = default;
        if (!AppliesTo(site)) return false;
        var best = float.MaxValue;
        var bestKey = string.Empty;
        var found = false;
        foreach (var element in BuildingRules.Elements(world, site))
        {
            if (!Workable(element)) continue;
            var position = ModulePosition(world, site, element);
            var distance = HexSpatialMath.Distance(position, from);
            if (found && (distance > best ||
                          distance == best && string.CompareOrdinal(element.SlotKey, bestKey) >= 0)) continue;
            best = distance;
            bestKey = element.SlotKey;
            piece = position;
            found = true;
        }

        return found;
    }

    /// <summary>Свободные узлы у детали, от ближних к детали к дальним.
    /// Заблокированные (сама стена, мебель, дерево) и вода не годятся; дверной
    /// проём годится — в нём стоят, пока дверь строится.</summary>
    private static void CollectStands(WorldState world, Float2 piece, float reach, List<Junction> into)
    {
        into.Clear();
        var center = HexSpatialMath.WorldToTile(piece);
        var seen = new HashSet<JunctionId>();
        for (var dq = -2; dq <= 2; dq++)
        for (var dr = Math.Max(-2, -dq - 2); dr <= Math.Min(2, -dq + 2); dr++)
        {
            if (!world.Tiles.Items.TryGetValue(new TileCoord(center.Q + dq, center.R + dr), out var tile)) continue;
            foreach (var id in tile.Junctions)
            {
                if (!seen.Add(id) || !world.Junctions.Items.TryGetValue(id, out var junction) ||
                    junction.Blocked || world.SwimJunctions.Contains(id) ||
                    SpatialQueries.IsAllWaterJunction(world, id) ||
                    HexSpatialMath.Distance(junction.WorldPosition, piece) > reach) continue;
                into.Add(junction);
            }
        }

        into.Sort((a, b) =>
        {
            var byPiece = HexSpatialMath.Distance(a.WorldPosition, piece)
                .CompareTo(HexSpatialMath.Distance(b.WorldPosition, piece));
            return byPiece != 0 ? byPiece : a.Id.Value.CompareTo(b.Id.Value);
        });
    }

    private static readonly List<Junction> Scratch = new();

    private static bool Usable(
        WorldState world, NPCState npc, Junction stand, HashSet<JunctionId> occupied, bool requireFreeClaim)
    {
        if (occupied.Contains(stand.Id)) return false;
        var own = npc.CurrentJunction is { } current && current.Equals(stand.Id);
        if (!own && !SpatialQueries.IsJunctionFree(world, stand.Id)) return false;
        if (requireFreeClaim &&
            world.Reservations.Junctions.TryGetValue(stand.Id, out var reservation) &&
            reservation.Owner != npc.Id && reservation.EndTick >= world.Tick) return false;
        return own || npc.CurrentJunction is not { } from ||
               Connectivity.Reachable(world, from, stand.Id, PlanningSystem.CanUseRoutineTraversal(npc));
    }

    /// <summary>Есть ли у детали место, куда эта строительница может дойти —
    /// немутирующая половина <see cref="TryReserve"/> для аукциона целей.</summary>
    public static bool HasStand(WorldState world, NPCState npc, WorldObjectState site)
    {
        if (!TryNearestPiece(world, site, npc.Position, out var piece)) return false;
        var occupied = PathfindingSystem.OtherActorJunctions(world, npc);
        foreach (var reach in Reaches)
        {
            CollectStands(world, piece, reach, Scratch);
            foreach (var stand in Scratch)
                if (Usable(world, npc, stand, occupied, requireFreeClaim: true)) return true;
        }

        return false;
    }

    /// <summary>Занять место у своей детали. false — у детали встать негде
    /// (вызывающий откатывается к якорю здания).</summary>
    public static bool TryReserve(
        WorldState world, NPCState npc, WorldObjectState site, int durationTicks, out JunctionId stand)
    {
        stand = default;
        if (!TryNearestPiece(world, site, npc.Position, out var piece)) return false;
        var occupied = PathfindingSystem.OtherActorJunctions(world, npc);
        foreach (var reach in Reaches)
        {
            CollectStands(world, piece, reach, Scratch);
            foreach (var candidate in Scratch)
            {
                if (!Usable(world, npc, candidate, occupied, requireFreeClaim: false) ||
                    !SpatialMutations.TryReserveJunction(world, candidate.Id, npc.Id, world.Tick, durationTicks))
                    continue;
                stand = candidate.Id;
                return true;
            }
        }

        return false;
    }

    /// <summary>Гейт старта: строительница стоит у какой-то рабочей детали
    /// этого здания (а не обязательно у его якоря).</summary>
    public static bool StandsAtPiece(WorldState world, NPCState npc, WorldObjectState site) =>
        TryNearestPiece(world, site, npc.Position, out var piece) &&
        HexSpatialMath.Distance(piece, npc.Position) <= MaxWorkDistance;
}

/// <summary>
/// §120.13 r2 (bug #721): пока игрок расставляет в режиме стройки, его лагерь
/// не начинает НОВЫХ строительных дел — он ещё проектирует. Клиент держит паузу,
/// пока режим открыт, и снимает её кнопкой «Готово». Пауза живёт в памяти мира
/// и сама истекает: потерянный клиент не оставит лагерь без строителей.
/// </summary>
public static class ConstructionHold
{
    /// <summary>Сколько тиков живёт пауза без продления (клиент продлевает её
    /// каждые пару секунд, пока режим открыт).</summary>
    public const int Ticks = 1200;

    public static bool Active(WorldState world, Faction faction) =>
        world != null && world.ConstructionHoldUntil.TryGetValue(faction, out var until) &&
        world.Tick <= until;

    internal static void Set(WorldState world, IEnumerable<Faction> factions, bool active)
    {
        foreach (var faction in factions)
        {
            if (active) world.ConstructionHoldUntil[faction] = world.Tick + Ticks;
            else world.ConstructionHoldUntil.Remove(faction);
        }
    }
}

}
