using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;
using HexLive.Simulation.Navigation;

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
    // §132.3: two complete tiles beyond both the predator's aggro and the AI danger ring.
    internal const int PredatorLandingMarginTiles = 2;

    internal static HashSet<TileCoord> UnsafeHumanLandingTiles(WorldState world)
    {
        var unsafeTiles = new HashSet<TileCoord>();
        void Exclude(TileCoord center, string species)
        {
            var stats = MobCatalog.For(species);
            if (stats.AttackDamage <= 0f || (stats.AttackMediums & AttackMedium.Land) == 0) return;
            var radius = System.Math.Max(stats.AggroRadiusTiles, Spec62.DangerRingTiles) + PredatorLandingMarginTiles;
            for (var q = -radius; q <= radius; q++)
            for (var r = System.Math.Max(-radius, -q - radius); r <= System.Math.Min(radius, -q + radius); r++)
                unsafeTiles.Add(new TileCoord(center.Q + q, center.R + r));
        }
        foreach (var mob in world.Mobs)
            if (mob.Health > 0f) Exclude(mob.Tile, mob.MobId);
        foreach (var slot in world.MobSpawnSlots)
        {
            if (slot.State != MobSlotState.Virtual) continue;
            foreach (var waypoint in slot.Ring) Exclude(waypoint.Tile, slot.MobId);
            if (slot.Ring.Count == 0 && world.Junctions.Items.TryGetValue(slot.HomeJunction, out var home))
                foreach (var tile in home.Tiles) Exclude(tile, slot.MobId);
        }
        return unsafeTiles;
    }
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

        var factionCap = CampCapacity(world, faction);
        if (factionCap <= 0)
        {
            return false;
        }

        return CampResidents(world, faction) < factionCap;
    }

    /// <summary>
    /// The living-population quota of one camp (0 = closed). §146.3: the girl-camp
    /// cap is a PER-CAMP quota — each girl faction counts against its own copy of
    /// it, the Outsiders against theirs.
    /// </summary>
    public static int CampCapacity(WorldState world, Faction faction)
    {
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

        return factionCap;
    }

    /// <summary>Living members of a camp — what its quota is counted against.</summary>
    public static int CampResidents(WorldState world, Faction faction)
    {
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

        return count;
    }

    public static bool TryPickLanding(
        WorldState world, TileCoord home, int sequence, int salt, out Landing landing, bool nearestToHome = false, Float2? nearPoint = null)
    {
        var candidates = new List<Landing>();
        var unsafeTiles = UnsafeHumanLandingTiles(world);
        var seen = new HashSet<JunctionId>();
        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            if (unsafeTiles.Contains(pair.Key) ||
                HexSpatialMath.HexDistance(pair.Key, home) > 1 ||
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
        if (nearestToHome)
        {
            var center = nearPoint ?? HexSpatialMath.TileToWorld(home);
            candidates.Sort((a, b) =>
            {
                var ax = a.Position.X - center.X; var ay = a.Position.Y - center.Y;
                var bx = b.Position.X - center.X; var by = b.Position.Y - center.Y;
                var distance = (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
                return distance != 0 ? distance : a.Junction.Value.CompareTo(b.Junction.Value);
            });
            landing = candidates[0]; return true;
        }
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
        var unsafeTiles = UnsafeHumanLandingTiles(world);
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
            if (distance < minDistance || distance > maxDistance || unsafeTiles.Contains(dryTile))
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

    // #629: a shared landmass needs an approach to the inhabited camp,
    // not a spawn at the unrelated, remote Outsiders settlement.
    internal static bool TryPickCampApproach(
        WorldState world, TileCoord home, int sequence, out Landing landing)
    {
        var points = new List<Junction>();
        var minimum = Spec72.MaxCampRadiusTiles;
        LocalSearch.CollectWithinTiles(world, home, minimum + 2, points);
        var unsafeTiles = UnsafeHumanLandingTiles(world);
        var candidates = new List<Landing>();
        foreach (var point in points)
        {
            if (point.Blocked || !SpatialQueries.IsJunctionFree(world, point.Id) ||
                SpatialQueries.IsAllWaterJunction(world, point.Id)) continue;
            foreach (var coord in point.Tiles)
            {
                var distance = HexSpatialMath.HexDistance(home, coord);
                if (distance < minimum || distance > minimum + 2 || unsafeTiles.Contains(coord) ||
                    !world.Tiles.Items.TryGetValue(coord, out var tile) ||
                    !tile.Flags.HasFlag(TileFlags.Walkable) ||
                    tile.Flags.HasFlag(TileFlags.Water) || tile.Flags.HasFlag(TileFlags.Blocked)) continue;
                var closest = true;
                foreach (var other in world.FactionHomes)
                    if (FactionRelations.IsGirlCamp(other.Key) && !other.Value.Equals(home) &&
                        HexSpatialMath.HexDistance(coord, other.Value) < distance)
                    { closest = false; break; }
                if (!closest) continue;
                var canReachResidents = false;
                foreach (var resident in world.Entities.Npcs.Values)
                    if (world.FactionHomes.TryGetValue(resident.Faction, out var residentHome) &&
                        residentHome.Equals(home) && resident.CurrentJunction is { } occupied &&
                        Connectivity.Reachable(world, point.Id, occupied, true))
                    { canReachResidents = true; break; }
                if (!canReachResidents) continue;
                candidates.Add(new Landing(coord, point.Id, point.Fragment, point.WorldPosition));
                break;
            }
        }
        if (candidates.Count == 0) { landing = default; return false; }
        candidates.Sort((a,b) => a.Junction.Value.CompareTo(b.Junction.Value));
        var pick = (int)(MathUtil.Hash01(world.Seed, sequence, 629, 15801) * candidates.Count);
        landing = candidates[System.Math.Min(candidates.Count-1, pick)];
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
