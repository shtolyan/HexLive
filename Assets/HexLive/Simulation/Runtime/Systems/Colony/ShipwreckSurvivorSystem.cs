using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §146.10: one deterministic wounded woman is washed ashore near the player's
/// HugeIsland/Maniac camp. She is aid-compatible but not controllable; after treatment,
/// food and water she changes faction once and joins Colony.
/// </summary>
public sealed class ShipwreckSurvivorSystem : ISimulationSystem
{
    internal const int SurvivorId = 900;
    internal const float JoinHealth = 0.65f;
    internal const float JoinBlood = 0.70f;
    internal const float JoinHungerMax = 0.45f;
    internal const float JoinThirstMax = 0.45f;

    public string Name => nameof(ShipwreckSurvivorSystem);
    public TickLayer Layer => TickLayer.Medium;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.Global;

    public void Run(WorldState world)
    {
        if (world.Mode is not (GameMode.HugeIsland or GameMode.Maniac))
        {
            return;
        }

        var id = new EntityId(SurvivorId);
        if (world.Entities.Npcs.TryGetValue(id, out var survivor))
        {
            TryJoin(world, survivor);
            return;
        }

        // A dead candidate remains a corpse with the same stable id. The event
        // is consumed by the life, not by a mutable cursor, so saves need no new
        // field and a failed rescue never respawns a replacement.
        if (world.Entities.Corpses.ContainsKey(id))
        {
            return;
        }

        var arrivalDay = 3 + (int)(MathUtil.Hash01(world.Seed, SurvivorId, 146, 14681) * 3f);
        if (EnvironmentSystem.CalendarDay(world.Tick) < arrivalDay ||
            world.Entities.Npcs.Count >= PopulationArrivalMath.MaxLivingNpcsFor(world.Mode) ||
            !world.FactionHomes.TryGetValue(Faction.Colony, out var home) ||
            !TryPickNearbyShore(world, home, out var landing))
        {
            return;
        }

        Spawn(world, landing, arrivalDay);
    }

    private static void TryJoin(WorldState world, NPCState survivor)
    {
        if (survivor.Faction != Faction.Castaway || survivor.Health < JoinHealth ||
            survivor.Needs.Blood < JoinBlood || survivor.Needs.Hunger > JoinHungerMax ||
            survivor.Needs.Thirst > JoinThirstMax || survivor.IsDying ||
            MortalityHelpers.IsBleeding(survivor) || WoundMath.NeedsAftercare(survivor))
        {
            return;
        }

        survivor.Faction = Faction.Colony;
        if (world.CreationConfig?.Owns(survivor) == true) world.PlayerControlledNpcs.Add(survivor.Id.Value);
        survivor.Mind.PendingAidFrom = null;
        survivor.Mind.PendingAidSinceTick = 0;
        Trace.Emit(world, survivor.Id, "ShipwreckSurvivorJoined",
            $"Name={survivor.DisplayName} Health={survivor.Health:F2} " +
            $"Hunger={survivor.Needs.Hunger:F2} Thirst={survivor.Needs.Thirst:F2}");
    }

    private readonly struct ShoreLanding
    {
        public readonly TileCoord Tile;
        public readonly JunctionId Junction;
        public readonly FragmentId Fragment;
        public readonly Float2 Position;

        public ShoreLanding(
            TileCoord tile, JunctionId junction, FragmentId fragment, Float2 position)
        {
            Tile = tile;
            Junction = junction;
            Fragment = fragment;
            Position = position;
        }
    }

    private static bool TryPickNearbyShore(
        WorldState world, TileCoord home, out ShoreLanding landing)
    {
        var candidates = new List<(ShoreLanding landing, int distance)>();
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
            if (distance <= 18)
            {
                candidates.Add((new ShoreLanding(
                    dryTile, junction.Id, junction.Fragment, junction.WorldPosition), distance));
            }
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
        var nearest = candidates[0].distance;
        var pool = candidates.FindAll(candidate => candidate.distance <= nearest + 2);
        var pick = (int)(MathUtil.Hash01(
            world.Seed, SurvivorId, pool.Count, 14683) * pool.Count);
        landing = pool[System.Math.Min(pool.Count - 1, pick)].landing;
        return true;
    }

    private static void Spawn(WorldState world, ShoreLanding landing, int arrivalDay)
    {
        var look = PopulationArrivalMath.RollFemaleLook(world, SurvivorId);
        var npc = new NPCState
        {
            Id = new EntityId(SurvivorId),
            DisplayName = look.NameId,
            ActorMesh = look.Mesh,
            SkinSet = look.SkinSet,
            EyeColor = look.EyeColor,
            Hairstyle = look.Hairstyle,
            VoiceBank = look.VoiceBank,
            Faction = Faction.Castaway,
            Fragment = landing.Fragment,
            Tile = landing.Tile,
            Position = landing.Position,
            CurrentJunction = landing.Junction,
        };

        npc.Needs.Hunger = 0.68f;
        npc.Needs.Thirst = 0.74f;
        npc.Needs.Energy = 0.90f;
        npc.Needs.Comfort = 0.85f;
        npc.Needs.Social = 0.70f;
        npc.Needs.ThermalDiscomfort = 0.72f;
        npc.Needs.Blood = 0.62f;

        npc.Body.Parts[BodyPart.Head] = 0.62f;
        npc.Body.Parts[BodyPart.Torso] = 0.52f;
        npc.Body.Parts[BodyPart.Pelvis] = 0.58f;
        npc.Body.Parts[BodyPart.ArmL] = 0.48f;
        npc.Body.Parts[BodyPart.ArmR] = 0.34f;
        npc.Body.Parts[BodyPart.LegL] = 0.28f;
        npc.Body.Parts[BodyPart.LegR] = 0.32f;
        npc.Health = npc.Body.Mean();
        npc.Mind.FaintedUntilTick = world.Tick + WorldBalance.DayLengthTicks / 12;

        npc.Wounds.Add(new WoundState
        {
            Id = npc.NextWoundId++,
            Zone = BodyPart.ArmR,
            Severity = 0.34f,
            Heal01 = 0f,
            Clot01 = 0.35f,
            Stabilized = false,
            BleedFactor = 0.8f,
            Seed = world.Seed ^ SurvivorId ^ 14687
        });
        npc.Wounds.Add(new WoundState
        {
            Id = npc.NextWoundId++,
            Zone = BodyPart.LegL,
            Severity = 0.28f,
            Heal01 = 0f,
            Clot01 = 0.45f,
            Stabilized = false,
            BleedFactor = 0.65f,
            Seed = world.Seed ^ SurvivorId ^ 14689
        });

        npc.CompassionTrait = Spec53.TraitMin +
            MathUtil.Hash01(world.Seed, SurvivorId, 53, 5301) *
            (Spec53.TraitMax - Spec53.TraitMin);
        AttributeMath.Roll(npc, world.Seed, SurvivorId);
        TraitMath.Roll(npc, world.Seed, SurvivorId);
        // No clothes and no inventory by design: only the body reaches shore.
        EquipmentMath.Recalculate(world, npc);

        PopulationArrivalMath.AddToWorld(world, npc, landing.Junction);
        Trace.Emit(world, npc.Id, "ShipwreckSurvivorAppeared",
            $"Day={arrivalDay} Name={npc.DisplayName} " +
            $"Tile={npc.Tile.Q},{npc.Tile.R} Junction={landing.Junction.Value}");
    }
}

}
