using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §132: один контракт места и высадки для обеих сторон.
/// Лимиты считают только живой ростер; трупы остаются в мире, но не занимают
/// место будущего выжившего. Высадка ищет свободную точку в семи гексах
/// (якорь лагеря + кольцо 1), так что прибывший не материализуется в костре,
/// стене или другом теле.
/// </summary>
internal static class PopulationArrivalMath
{
    internal readonly struct Landing
    {
        public readonly TileCoord Tile;
        public readonly JunctionId Junction;
        public readonly FragmentId Fragment;
        public readonly Float2 Position;

        public Landing(TileCoord tile, JunctionId junction, FragmentId fragment, Float2 position)
        {
            Tile = tile;
            Junction = junction;
            Fragment = fragment;
            Position = position;
        }
    }

    // §146.6: потолки — парные ручки, селектор по режиму мира. Живёт здесь,
    // а не в WorldBalance: гейт «у ручки есть читатель» не считает Balance/
    // читателем самого себя.
    public static int MaxLivingNpcsFor(Bootstrap.GameMode mode) => mode switch
    {
        Bootstrap.GameMode.BigIsland => WorldBalance.BigIslandMaxLivingNpcs,
        Bootstrap.GameMode.HugeIsland => WorldBalance.HugeIslandMaxLivingNpcs,
        Bootstrap.GameMode.Maniac => WorldBalance.HugeIslandMaxLivingNpcs,
        Bootstrap.GameMode.Islands => WorldBalance.IslandsMaxLivingNpcs,
        _ => WorldBalance.MaxLivingNpcs
    };

    // Квота ОДНОГО лагеря девушек (для Outsiders остаётся MaxOutsiderNpcs).
    public static int MaxCampNpcsFor(Bootstrap.GameMode mode) => mode switch
    {
        Bootstrap.GameMode.BigIsland => WorldBalance.BigIslandMaxCampNpcs,
        Bootstrap.GameMode.HugeIsland => WorldBalance.HugeIslandMaxCampNpcs,
        Bootstrap.GameMode.Maniac => WorldBalance.HugeIslandMaxCampNpcs,
        Bootstrap.GameMode.Islands => WorldBalance.IslandsMaxCampNpcs,
        _ => WorldBalance.MaxColonyNpcs
    };

    public static bool HasRoom(WorldState world, Faction faction)
    {
        // §146.6: потолки выбираются селектором по режиму мира.
        var worldCap = world == null ? 0 : world.CreationConfig?.PopulationLimit ?? MaxLivingNpcsFor(world.Mode);
        if (world == null || worldCap <= 0 ||
            world.Entities.Npcs.Count >= worldCap)
        {
            return false;
        }

        // §146.3: the girl-camp cap is a PER-CAMP quota — each girl faction
        // counts against its own copy of it, the Outsiders against theirs.
        var factionCap = FactionRelations.IsColonyKind(faction)
            ? MaxCampNpcsFor(world.Mode)
            : WorldBalance.MaxOutsiderNpcs;
        if (world.CreationConfig != null)
        {
            var camp = world.CreationConfig.Camp(faction);
            factionCap = camp?.Enabled == true ? camp.PopulationLimit : 0;
            if (faction == Faction.Outsiders && world.Mode == Bootstrap.GameMode.Islands)
                factionCap = world.CreationConfig.PopulationLimit;
        }
        if (factionCap <= 0)
        {
            return false;
        }

        var count = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // Capacity is literal membership, never diplomatic alliance. With
            // Spec72 disabled both factions are friendly, but they still occupy
            // different camp quotas and must not collapse into one count.
            if (npc.Faction == faction)
            {
                count++;
            }
        }

        return count < factionCap;
    }

    public static bool TryPickLanding(
        WorldState world, TileCoord home, int sequence, int salt, out Landing landing)
    {
        var candidates = new List<Landing>();
        var seen = new HashSet<JunctionId>();
        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            if (HexSpatialMath.HexDistance(pair.Key, home) > 1 ||
                !tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Blocked) ||
                tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            var points = new List<Junction>();
            foreach (var junctionId in tile.Junctions)
            {
                if (!seen.Add(junctionId) ||
                    !world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                    junction.Blocked ||
                    !SpatialQueries.IsJunctionFree(world, junctionId) ||
                    SpatialQueries.IsAllWaterJunction(world, junctionId))
                {
                    continue;
                }

                points.Add(junction);
            }

            foreach (var point in points)
            {
                candidates.Add(new Landing(pair.Key, point.Id, point.Fragment, point.WorldPosition));
            }
        }

        if (candidates.Count == 0)
        {
            landing = default;
            return false;
        }

        candidates.Sort((a, b) =>
        {
            var aq = a.Tile.Q.CompareTo(b.Tile.Q);
            if (aq != 0) return aq;
            var ar = a.Tile.R.CompareTo(b.Tile.R);
            return ar != 0 ? ar : a.Junction.Value.CompareTo(b.Junction.Value);
        });
        var index = (int)(MathUtil.Hash01(world.Seed, sequence, 132, salt) * candidates.Count);
        landing = candidates[System.Math.Min(candidates.Count - 1, index)];
        return true;
    }

    /// <summary>
    /// §157.5/§157.7: высадка С МОРЯ — сухой свободный узел, касающийся
    /// глубокой воды, в кольце [min, max] тайлов от дома и ближе к ЭТОМУ дому,
    /// чем к любому другому девичьему (то есть на своём острове). Общий берег
    /// потерпевшей и островного чужака; отличаются только кольцо и соль.
    /// </summary>
    internal static bool TryPickShoreLanding(
        WorldState world, TileCoord home, int minDistance, int maxDistance,
        int sequence, int salt, out Landing landing)
    {
        var candidates = new List<(Landing landing, int distance)>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id) ||
                !SpatialQueries.IsJunctionFree(world, junction.Id))
            {
                continue;
            }

            var touchesSea = false;
            foreach (var neighbor in junction.Neighbors)
            {
                if (SpatialQueries.IsAllWaterJunction(world, neighbor))
                {
                    touchesSea = true;
                    break;
                }
            }
            if (!touchesSea)
            {
                continue;
            }

            TileCoord? dry = null;
            foreach (var coord in junction.Tiles)
            {
                if (world.Tiles.Items.TryGetValue(coord, out var tile) &&
                    tile.Flags.HasFlag(TileFlags.Walkable) &&
                    !tile.Flags.HasFlag(TileFlags.Water) &&
                    !tile.Flags.HasFlag(TileFlags.Blocked))
                {
                    dry = coord;
                    break;
                }
            }
            if (dry is not { } dryTile)
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(dryTile, home);
            if (distance < minDistance || distance > maxDistance)
            {
                continue;
            }

            var ownShore = true;
            foreach (var pair in world.FactionHomes)
            {
                if (FactionRelations.IsGirlCamp(pair.Key) && !pair.Value.Equals(home) &&
                    HexSpatialMath.HexDistance(dryTile, pair.Value) < distance)
                {
                    ownShore = false;
                    break;
                }
            }
            if (!ownShore)
            {
                continue;
            }

            candidates.Add((new Landing(
                dryTile, junction.Id, junction.Fragment, junction.WorldPosition), distance));
        }

        if (candidates.Count == 0)
        {
            landing = default;
            return false;
        }

        candidates.Sort((a, b) =>
        {
            var byDistance = a.distance.CompareTo(b.distance);
            return byDistance != 0
                ? byDistance
                : a.landing.Junction.Value.CompareTo(b.landing.Junction.Value);
        });
        // Ближайший отрезок берега: кольцо [min, max] лишь отсекает лагерь и
        // чужой остров, а прибой выносит туда, где море ближе всего к дому.
        var nearest = candidates[0].distance;
        var pool = candidates.FindAll(candidate => candidate.distance <= nearest + ShoreBandTiles);
        var pick = (int)(MathUtil.Hash01(world.Seed, sequence, pool.Count, salt) * pool.Count);
        landing = pool[System.Math.Min(pool.Count - 1, pick)].landing;
        return true;
    }

    private const int ShoreBandTiles = 3;

    public static ColonistAppearance.Look RollFemaleLook(WorldState world, int id)
    {
        var names = new HashSet<string>();
        var looks = new HashSet<string>();
        var hair = new HashSet<string>();
        void Take(NPCState npc)
        {
            if (!string.IsNullOrEmpty(npc.DisplayName)) names.Add(npc.DisplayName);
            if (!string.IsNullOrEmpty(npc.Hairstyle)) hair.Add(npc.Hairstyle);
            if (!string.IsNullOrEmpty(npc.ActorMesh))
            {
                looks.Add(ColonistAppearance.LookKey(
                    npc.ActorMesh, npc.SkinSet, npc.Hairstyle));
            }
        }

        foreach (var npc in world.Entities.Npcs.Values) Take(npc);
        foreach (var npc in world.Entities.Corpses.Values) Take(npc);
        return ColonistAppearance.Roll(world.Seed, id, names, looks, hair);
    }

    public static void AddToWorld(WorldState world, NPCState npc, JunctionId junction)
    {
        world.Entities.Npcs[npc.Id] = npc;
        if (world.CreationConfig?.Owns(npc) == true) world.PlayerControlledNpcs.Add(npc.Id.Value);

        if (!world.Occupancy.EntitiesInTile.TryGetValue(npc.Tile, out var occupied))
        {
            occupied = new List<EntityId>();
            world.Occupancy.EntitiesInTile[npc.Tile] = occupied;
        }
        occupied.Add(npc.Id);
        world.Occupancy.JunctionOwner[junction] = npc.Id;

        if (!world.Caches.EntitiesByTile.TryGetValue(npc.Tile, out var byTile))
        {
            byTile = new List<EntityId>();
            world.Caches.EntitiesByTile[npc.Tile] = byTile;
        }
        byTile.Add(npc.Id);

        if (!world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var byFragment))
        {
            byFragment = new List<EntityId>();
            world.Caches.EntitiesByFragment[npc.Fragment] = byFragment;
        }
        byFragment.Add(npc.Id);
    }
}

}
