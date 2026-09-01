using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §146.14 (bug #291): «свой/чужой очаг» для приказа «Сделать домом» — одна
/// формулировка на сим и UI (правило §149 r3). Свой = очаг в нашем лагере или
/// на ничьей земле (новый костёр в пустоши дальше радиуса лагеря); чужой =
/// очаг в радиусе НЕсоюзного дома, который не дальше нашего — ничья остаётся
/// нам, тем же тайбрейком, что <c>ColonyQueries.InCamp</c> («hostile camp
/// strictly closer claims the tile»).
/// </summary>
public static class CampHomeMath
{
    public static bool IsForeignCampTile(WorldState world, Faction actor, TileCoord tile)
    {
        var ours = int.MaxValue;
        if (world.FactionHomes.TryGetValue(actor, out var ourHome))
        {
            ours = HexSpatialMath.HexDistance(tile, ourHome);
        }

        foreach (var pair in world.FactionHomes)
        {
            if (Foreign(actor, pair.Key, tile, pair.Value, ours))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Снапшотная перегрузка для UI — тот же предикат по
    /// <c>WorldSnapshot.CampHomes</c>, без владения миром.</summary>
    public static bool IsForeignCampTile(
        IReadOnlyList<Debug.CampHomeSnapshot> homes, Faction actor, TileCoord tile)
    {
        var ours = int.MaxValue;
        for (var i = 0; i < homes.Count; i++)
        {
            if (homes[i].Faction == actor)
            {
                ours = HexSpatialMath.HexDistance(tile, homes[i].Tile);
            }
        }

        for (var i = 0; i < homes.Count; i++)
        {
            if (Foreign(actor, homes[i].Faction, tile, homes[i].Tile, ours))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Тайл — уже домашний якорь этой фракции?</summary>
    public static bool IsHomeTile(
        IReadOnlyList<Debug.CampHomeSnapshot> homes, Faction actor, TileCoord tile)
    {
        for (var i = 0; i < homes.Count; i++)
        {
            if (homes[i].Faction == actor && homes[i].Tile.Equals(tile))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Foreign(
        Faction actor, Faction owner, TileCoord tile, TileCoord home, int ourDistance)
    {
        if (FactionRelations.AreAllies(actor, owner))
        {
            return false;
        }

        var distance = HexSpatialMath.HexDistance(tile, home);
        return distance <= Spec72.MaxCampRadiusTiles && distance < ourDistance;
    }
}

}
