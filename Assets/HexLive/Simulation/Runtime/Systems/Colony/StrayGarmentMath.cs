using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §133: одежда, забытая вдали от дома. Игрок просил, чтобы вещи не были
/// раскиданы по всей карте, — значит, у колонии должна быть скучная фоновая
/// работа «подобрать своё и отнести домой».
/// </summary>
public static class StrayGarmentMath
{
    /// <summary>
    /// Ближайшая валяющаяся вещь, которую ЕЙ можно унести домой, или null.
    ///
    /// <para>
    /// Берут только своё и ничейное: чужую вещь без спроса не носят даже из
    /// добрых побуждений (§133). Не трогают то, что лежит на станции — оно уже
    /// дома, — и то, что ждёт хозяйку в купальной куче: унести её значит
    /// оставить человека голым у воды.
    /// </para>
    /// </summary>
    public static WorldObjectState FindStray(WorldState world, NPCState npc)
    {
        if (ColonyQueries.Home(world, npc.Faction) is not { } home)
        {
            return null;
        }

        // ⭐ Смотрим и ПАМЯТЬ, не только глаза. Забытая вещь по определению лежит
        // далеко (ближнее и так у дома), и в поле зрения её почти никогда нет:
        // первая версия фильтровала по perceived.IsReachable и за 12000 тиков не
        // нашла ни одной вещи, хотя рубашка спокойно валялась в десяти тайлах.
        // Достижимость спрашиваем честно, у графа, а не у флага восприятия.
        WorldObjectState best = null;
        var bestDistance = float.MaxValue;
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!world.Entities.Objects.TryGetValue(perceived.Id, out var obj) ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) ||
                def.Layer is null || obj.IsOccupied ||
                npc.Memory.IsShunned(perceived.Id, world.Tick))
            {
                continue;
            }

            var ownerId = ClothingOwnership.OwnerIdOf(obj);
            if (ownerId != 0 && ownerId != npc.Id.Value &&
                ClothingOwnership.OwnerAlive(world, ownerId))
            {
                continue; // чужое — не наше дело
            }

            if (HexSpatialMath.HexDistance(obj.Tile, home) <= Spec133.HomeStowRadiusTiles ||
                OnAStation(world, obj) || AwaitedByABather(world, perceived.Id) ||
                obj.Junctions.Count == 0 ||
                npc.CurrentJunction is not { } from ||
                !Connectivity.Reachable(
                    world, from, obj.Junctions[0], PlanningSystem.CanUseRoutineTraversal(npc)))
            {
                continue;
            }

            if (perceived.Distance < bestDistance)
            {
                bestDistance = perceived.Distance;
                best = obj;
            }
        }

        return best;
    }

    private static bool OnAStation(WorldState world, WorldObjectState garment)
    {
        if (garment.Junctions.Count == 0)
        {
            return false;
        }

        foreach (var other in world.Entities.Objects.Values)
        {
            if (other.Junctions.Count == 0 || !other.Junctions[0].Equals(garment.Junctions[0]) ||
                !world.Content.ObjectDefinitions.TryGetValue(other.DefinitionId, out var def))
            {
                continue;
            }

            if (def.Tags.Contains(ObjectTags.Rack) || def.Tags.Contains(ObjectTags.Wardrobe))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AwaitedByABather(WorldState world, ObjectId garment)
    {
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Mind.RedressGarments.Contains(garment))
            {
                return true;
            }
        }

        return false;
    }
}

}
