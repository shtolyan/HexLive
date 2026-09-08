using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Spatial
{

/// <summary>
/// §158.4: перечисление узлов ОКРЕСТНОСТИ вместо обхода всего графа.
/// Кандидат «сесть на уступ», «остыть у воды», «берег для стирки», «укрытие
/// для бегства» — это всегда «ближайший подходящий узел», и раньше каждый
/// такой поиск шёл по всем 2.5 млн узлов «Островов» (1.7–2 с на план). Тайл
/// знает свои узлы (<see cref="Tile.Junctions"/>, узел на ребре числится в
/// обоих тайлах), поэтому окрестность перечисляется кольцами тайлов.
/// <para>
/// Порядок кандидатов ФИКСИРОВАН по возрастанию id узла — это порядок
/// вставки в <c>Junctions.Items</c>, то есть ровно тот порядок, в котором их
/// видел прежний полный обход; ничьи по расстоянию решаются так же.
/// </para>
/// </summary>
public static class LocalSearch
{
    // Центры соседних тайлов отстоят на HexRadius·√3; на гекс-дистанции k
    // евклидово расстояние между центрами не меньше k·HexRadius·√3·(√3/2) =
    // k·1.5·HexRadius. Узел лежит внутри своего тайла (≤ HexRadius от
    // центра), точка отсчёта — внутри своего. Отсюда обе оценки ниже.
    private const float RingStepWorld = HexSpatialMath.HexRadius * 1.5f;

    /// <summary>Радиус в тайлах, гарантированно накрывающий все узлы в
    /// пределах <paramref name="worldDistance"/> от точки внутри центрального
    /// тайла.</summary>
    public static int TileRadiusCovering(float worldDistance)
    {
        if (worldDistance <= 0f)
        {
            return 1;
        }

        // Локальный поиск не умеет «всё»: радиус больше MaxRadiusTiles — это
        // просьба о полном обходе, которую §158.1 запрещает; такой запрос
        // честно обрезается до предела, а не переполняет int.
        var reach = worldDistance + 2f * HexSpatialMath.HexRadius;
        var rings = reach / RingStepWorld + 1f;
        return rings >= MaxRadiusTiles ? MaxRadiusTiles : (int)System.MathF.Ceiling(rings);
    }

    /// <summary>Потолок радиуса локального поиска в тайлах (64 тайла ≈ 144 wu,
    /// диск ≈ 12 тысяч тайлов — уже дороже, чем позволяет бюджет тика).</summary>
    public const int MaxRadiusTiles = 64;

    /// <summary>Нижняя граница расстояния от точки внутри центрального тайла
    /// до любого узла тайла на гекс-дистанции <paramref name="ring"/>:
    /// кольцо дальше этой границы не может дать кандидата ближе уже
    /// найденного.</summary>
    public static float RingLowerBound(int ring) =>
        System.MathF.Max(0f, ring * RingStepWorld - 2f * HexSpatialMath.HexRadius);

    /// <summary>Узлы всех тайлов в гекс-радиусе <paramref name="radius"/> от
    /// <paramref name="center"/>, без повторов, по возрастанию id.</summary>
    public static void CollectWithinTiles(
        WorldState world, TileCoord center, int radius, List<Junction> into)
    {
        into.Clear();
        var seen = world.Caches.LocalSearchSeenScratch;
        seen.Clear();
        for (var ring = 0; ring <= radius; ring++)
        {
            AppendRing(world, center, ring, into, seen);
        }

        into.Sort(ByJunctionId);
    }

    /// <summary>Узлы тайлов ровно на гекс-дистанции <paramref name="ring"/>
    /// от центра, по возрастанию id. <paramref name="seen"/> — общий на весь
    /// поиск набор, чтобы узел на ребре двух колец не пришёл дважды.</summary>
    public static void CollectRing(
        WorldState world, TileCoord center, int ring, List<Junction> into,
        HashSet<JunctionId> seen)
    {
        into.Clear();
        AppendRing(world, center, ring, into, seen);
        into.Sort(ByJunctionId);
    }

    private static void AppendRing(
        WorldState world, TileCoord center, int ring, List<Junction> into,
        HashSet<JunctionId> seen)
    {
        if (ring == 0)
        {
            AppendTile(world, center, into, seen);
            return;
        }

        // Кольцо гекс-дистанции ring: все (dq, dr) с max(|dq|,|dr|,|dq+dr|) == ring.
        for (var dq = -ring; dq <= ring; dq++)
        {
            var lo = System.Math.Max(-ring, -dq - ring);
            var hi = System.Math.Min(ring, -dq + ring);
            for (var dr = lo; dr <= hi; dr++)
            {
                if (System.Math.Max(System.Math.Abs(dq),
                        System.Math.Max(System.Math.Abs(dr), System.Math.Abs(dq + dr))) != ring)
                {
                    continue;
                }

                AppendTile(world, new TileCoord(center.Q + dq, center.R + dr), into, seen);
            }
        }
    }

    private static void AppendTile(
        WorldState world, TileCoord coord, List<Junction> into, HashSet<JunctionId> seen)
    {
        if (!world.Tiles.Items.TryGetValue(coord, out var tile))
        {
            return;
        }

        foreach (var junctionId in tile.Junctions)
        {
            if (seen.Add(junctionId) && world.Junctions.Items.TryGetValue(junctionId, out var junction))
            {
                into.Add(junction);
            }
        }
    }

    /// <summary>Максимум колец для поиска «ближайшего подходящего»: дальше
    /// этого узел не ищется вовсе. 24 тайла ≈ 54 wu — больше любого разумного
    /// «сходить к берегу/укрытию»; на прежнем полном обходе цели за этим
    /// радиусом всё равно проигрывали по расстоянию.</summary>
    public const int NearestSearchMaxRadiusTiles = 24;

    /// <summary>Ближайший к <paramref name="from"/> узел, прошедший
    /// <paramref name="accept"/>, кольцами от <paramref name="center"/>.
    /// Ничья по расстоянию — меньший id (порядок прежнего полного обхода).
    /// Кольцо, чья нижняя граница дальше найденного, не открывается.</summary>
    public static Junction FindNearest(
        WorldState world, TileCoord center, Float2 from, int maxRadiusTiles,
        System.Func<Junction, bool> accept)
    {
        var seen = world.Caches.LocalSearchSeenScratch;
        var ring = world.Caches.LocalSearchRingScratch;
        seen.Clear();
        Junction best = null;
        var bestDistance = float.MaxValue;
        for (var k = 0; k <= maxRadiusTiles; k++)
        {
            if (best is not null && RingLowerBound(k) > bestDistance)
            {
                break;
            }

            CollectRing(world, center, k, ring, seen);
            for (var i = 0; i < ring.Count; i++)
            {
                var junction = ring[i];
                var distance = HexSpatialMath.Distance(from, junction.WorldPosition);
                if (distance > bestDistance ||
                    (distance == bestDistance && best is not null && junction.Id.Value > best.Id.Value) ||
                    !accept(junction))
                {
                    continue;
                }

                best = junction;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static int ByJunctionId(Junction a, Junction b) => a.Id.Value.CompareTo(b.Id.Value);
}

}
