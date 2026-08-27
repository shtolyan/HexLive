using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Core
{

// §129: derived door caches. A closed door is BEHAVIOUR, not topology — the
// portal junction is never Blocked, so none of the connectivity caches care
// about a swing. What does care:
//
//   - MovementSystem: "is my next step a closed portal, and whose door is it";
//   - the router: "which portals may THIS faction never step on" (hardAvoid);
//   - SpatialQueries.IsBarrierFor: "may hands reach through this junction".
//
// All three read the caches below. DoorByPortal changes only with build/
// demolition (TopologyVersion); the closed set and the per-faction bans also
// change on every swing (DoorStateVersion) — hence the two-key pair.
public static class DoorTopology
{
    public const string DoorDefinitionId = "architecture.door.wood";

    public static bool IsDoorPiece(WorldObjectState piece) => piece != null &&
        piece.DefinitionId == DoorDefinitionId && piece.IsArchitectureElement;

    // §129 / #237: кто вправе открыть эту дверь. Здесь стояла константа
    // Faction.Colony — верная ровно до §146, который дал дом с дверью КАЖДОМУ
    // девичьему лагерю. Живой серверный сейв (HugeIsland): шесть хижин, у пяти
    // жительницы Colony2…Colony6 — и ни одна не имела права открыть СВОЮ
    // дверь, поэтому роутер запирал их в собственном доме.
    //
    // Владелец ВЫВОДИТСЯ, а не хранится: §146.5 StakeCampHutPlans ставит сайт
    // хижины в 2-4 гексах от очага своего лагеря, так что «ближайший девичий
    // очаг» — точная обратная функция к тому, кто этот дом застолбил. Вывод
    // работает и на уже сохранённых мирах (миграции нет) и остаётся тем самым
    // ЕДИНСТВЕННЫМ местом, куда позже встанет сохраняемое поле.
    //
    // Аутсайдеры и Castaway домов не строят и владельцами не становятся
    // никогда. Все решения о вражде ниже идут через FactionRelations, а не
    // через самописное сравнение.
    public static Faction OwnerFaction(WorldState world, WorldObjectState door)
    {
        if (world is null || door is null)
        {
            return Faction.Colony;
        }

        var anchor = OwnerAnchorTile(world, door);
        var owner = Faction.Colony;
        var best = int.MaxValue;
        // AllFactions идёт по ординалу — порядок словаря FactionHomes не смеет
        // попадать в реплей (правило BedSiteSystem). Ничья по расстоянию
        // достаётся младшему ординалу.
        foreach (var faction in AllFactions)
        {
            if (!FactionRelations.IsGirlCamp(faction) ||
                !world.FactionHomes.TryGetValue(faction, out var home))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(anchor, home);
            if (distance < best)
            {
                best = distance;
                owner = faction;
            }
        }

        return owner;
    }

    // Дверь — LEGO-элемент §120: её собственный Tile стоит на периметре, между
    // двумя гексами. Меряем от здания, которому она принадлежит.
    private static TileCoord OwnerAnchorTile(WorldState world, WorldObjectState door) =>
        door.ArchitectureOwnerId is { } ownerId &&
        world.Entities.Objects.TryGetValue(ownerId, out var building)
            ? building.Tile
            : door.Tile;

    /// <summary>Портал закрытой двери? (пустой кэш ⇒ всегда false)</summary>
    public static bool IsClosedDoorPortal(WorldState world, JunctionId junctionId)
    {
        EnsureDoorCaches(world);
        return world.Caches.ClosedDoorPortals.Contains(junctionId);
    }

    /// <summary>Дверь, чей портал стоит на этом узле (независимо от створки).</summary>
    public static bool TryGetDoorAt(
        WorldState world, JunctionId junctionId, out WorldObjectState door)
    {
        EnsureDoorCaches(world);
        door = null;
        return world.Caches.DoorByPortal.TryGetValue(junctionId, out var doorId) &&
               world.Entities.Objects.TryGetValue(doorId, out door);
    }

    /// <summary>
    /// §129: узлы, на которые фракция F не имеет права ступать — закрытые
    /// порталы враждебных ей дверей. NULL при пустом наборе: колония в мире
    /// без враждебных дверей не платит ни аллокацией, ни лишней веткой в
    /// роутере (путь остаётся бит-в-бит как до §129).
    /// </summary>
    public static HashSet<JunctionId> ForbiddenFor(WorldState world, Faction faction)
    {
        EnsureDoorCaches(world);
        return world.Caches.FactionForbiddenJunctions.TryGetValue(faction, out var set) &&
               set.Count > 0
            ? set
            : null;
    }

    // ⭐ Порядок — ординал Faction, и это контракт: OwnerFaction разрешает
    // ничью по расстоянию младшим ординалом, а порядок словаря в реплей не
    // попадает.
    private static readonly Faction[] AllFactions =
        {
            Faction.Colony, Faction.Outsiders, Faction.Colony2, Faction.Colony3,
            Faction.Colony4, Faction.Colony5, Faction.Colony6, Faction.Castaway
        };

    private static void EnsureDoorCaches(WorldState world)
    {
        var caches = world.Caches;
        if (caches.DoorByPortalBuiltVersion != world.TopologyVersion)
        {
            caches.DoorByPortal.Clear();
            foreach (var worldObject in world.Entities.Objects.Values)
            {
                // One door = one navigation throat (BuildingDoorRules keeps the
                // same invariant); a malformed multi-portal door is not indexed.
                if (IsDoorPiece(worldObject) && worldObject.Junctions.Count == 1)
                {
                    caches.DoorByPortal[worldObject.Junctions[0]] = worldObject.Id;
                }
            }

            caches.DoorByPortalBuiltVersion = world.TopologyVersion;
            // The state layer below derives from this map — force its rebuild.
            caches.DoorStateBuiltTopologyVersion = 0;
        }

        if (caches.DoorStateBuiltTopologyVersion == world.TopologyVersion &&
            caches.DoorStateBuiltDoorVersion == world.DoorStateVersion)
        {
            return;
        }

        caches.ClosedDoorPortals.Clear();
        caches.FactionForbiddenJunctions.Clear();
        foreach (var pair in caches.DoorByPortal)
        {
            if (!world.Entities.Objects.TryGetValue(pair.Value, out var door) ||
                door.IsDoorOpen)
            {
                continue;
            }

            caches.ClosedDoorPortals.Add(pair.Key);
            var owner = OwnerFaction(world, door);
            foreach (var faction in AllFactions)
            {
                // #237: запрет роутера обязан совпадать с правом открыть
                // створку, а гейт движения спрашивает именно AreAllies. Между
                // «враждебна» и «союзница» жил НЕЙТРАЛЬНЫЙ лагерь (§146.12,
                // solo-camp режимы): роутер вёл её сквозь чужую закрытую дверь,
                // гейт отказывал, путь сбрасывался — вечная петля вместо
                // честного PathFailed. С выключенным §72 AreAllies истинна для
                // всех, поэтому kill-switch по-прежнему отдаёт пустые наборы.
                if (FactionRelations.AreAllies(faction, owner))
                {
                    continue;
                }

                if (!caches.FactionForbiddenJunctions.TryGetValue(faction, out var set))
                {
                    set = new HashSet<JunctionId>();
                    caches.FactionForbiddenJunctions[faction] = set;
                }

                set.Add(pair.Key);
            }
        }

        caches.DoorStateBuiltTopologyVersion = world.TopologyVersion;
        caches.DoorStateBuiltDoorVersion = world.DoorStateVersion;
    }
}

}
