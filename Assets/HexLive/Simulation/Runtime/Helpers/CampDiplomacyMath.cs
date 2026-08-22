using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §146.12: one contract for neutral solo camps, visits and real camp merges.
/// The mode-aware hostility overloads live on <see cref="FactionRelations"/>;
/// this class owns the thresholds and the atomic world mutation.
/// </summary>
public static class CampDiplomacyMath
{
    public const float HatredAffinityThreshold = -0.50f;
    public const float MergeAffinityThreshold = 0.50f;
    public const float DesperateLootHungerThreshold = 0.90f;
    public const float VisitSocialThreshold = 0.55f;

    public static bool IsSoloCampMode(GameMode mode) =>
        mode is GameMode.HugeIsland or GameMode.Maniac;

    public static bool CanLoot(
        WorldState world, NPCState looter, NPCState victim)
    {
        if (FactionRelations.AreHostile(world, looter.Faction, victim.Faction))
        {
            return true;
        }

        return IsSoloCampMode(world.Mode) &&
               looter.Faction != victim.Faction &&
               FactionRelations.IsGirlCamp(looter.Faction) &&
               FactionRelations.IsGirlCamp(victim.Faction) &&
               (looter.Social.GetOrCreate(victim.Id).Affinity <=
                    HatredAffinityThreshold ||
                looter.Needs.Hunger >= DesperateLootHungerThreshold);
    }

    /// <summary>
    /// A lonely healthy NPC walks toward a camp whose member she has actually
    /// met. The exact anchor comes from the live FactionHomes table, so merges
    /// and save/load cannot leave a stale private camp coordinate behind.
    /// </summary>
    public static bool TryFindVisitCamp(
        WorldState world, NPCState visitor, out Faction faction, out TileCoord home)
    {
        faction = default;
        home = default;
        if (!IsSoloCampMode(world.Mode) ||
            visitor.Needs.Social >= VisitSocialThreshold ||
            visitor.Needs.Hunger >= 0.5f || visitor.Needs.Thirst >= 0.5f ||
            visitor.Needs.Energy <= 0.5f || visitor.Health < 0.75f ||
            !FactionRelations.IsGirlCamp(visitor.Faction))
        {
            return false;
        }

        var found = false;
        var bestAffinity = float.MinValue;
        var bestDistance = int.MaxValue;
        foreach (var pair in world.FactionHomes.OrderBy(p => (int)p.Key))
        {
            var candidateFaction = pair.Key;
            if (candidateFaction == visitor.Faction ||
                !FactionRelations.IsGirlCamp(candidateFaction) ||
                ColonyQueries.InCamp(world, visitor.Tile, candidateFaction))
            {
                continue;
            }

            var knowsMember = false;
            var candidateAffinity = float.MinValue;
            foreach (var memory in visitor.Memory.KnownAgents.Values)
            {
                if (memory.Faction != candidateFaction ||
                    !world.Entities.Npcs.TryGetValue(memory.Id, out var member) ||
                    member.Health <= 0f)
                {
                    continue;
                }

                knowsMember = true;
                candidateAffinity = Math.Max(candidateAffinity,
                    visitor.Social.GetOrCreate(member.Id).Affinity);
            }

            if (!knowsMember || candidateAffinity <= HatredAffinityThreshold)
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(visitor.Tile, pair.Value);
            if (!found || candidateAffinity > bestAffinity + 0.0001f ||
                (Math.Abs(candidateAffinity - bestAffinity) <= 0.0001f &&
                 distance < bestDistance))
            {
                found = true;
                faction = candidateFaction;
                home = pair.Value;
                bestAffinity = candidateAffinity;
                bestDistance = distance;
            }
        }

        return found;
    }

    public static float VisitScoreBonus(WorldState world, NPCState npc) =>
        TryFindVisitCamp(world, npc, out _, out _)
            ? 0.25f + (VisitSocialThreshold - npc.Needs.Social) * 0.5f
            : 0f;

    public static bool CanMerge(
        WorldState world, NPCState first, NPCState second, out string reason)
    {
        if (!IsSoloCampMode(world.Mode))
        {
            reason = "SoloCampModeOnly";
            return false;
        }

        if (first is null || second is null || first.Id.Equals(second.Id) ||
            first.Health <= 0f || second.Health <= 0f ||
            first.IsUnconscious(world.Tick) || second.IsUnconscious(world.Tick))
        {
            reason = "TargetUnavailable";
            return false;
        }

        if (first.Faction == second.Faction)
        {
            reason = "AlreadySameCamp";
            return false;
        }

        if (!FactionRelations.IsGirlCamp(first.Faction) ||
            !FactionRelations.IsGirlCamp(second.Faction))
        {
            reason = "NotNeighbourCamp";
            return false;
        }

        if (!world.FactionHomes.ContainsKey(first.Faction) ||
            !world.FactionHomes.ContainsKey(second.Faction))
        {
            reason = "CampMissing";
            return false;
        }

        if (first.Social.GetOrCreate(second.Id).Affinity <= MergeAffinityThreshold ||
            second.Social.GetOrCreate(first.Id).Affinity <= MergeAffinityThreshold)
        {
            reason = "RelationshipTooLow";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool TryMerge(
        WorldState world, NPCState first, NPCState second,
        CampHomeChoice choice, out string reason)
    {
        if (!CanMerge(world, first, second, out reason))
        {
            return false;
        }

        var firstFaction = first.Faction;
        var secondFaction = second.Faction;
        var firstHome = world.FactionHomes[firstFaction];
        var secondHome = world.FactionHomes[secondFaction];
        var canonical = firstFaction == Faction.Colony || secondFaction == Faction.Colony
            ? Faction.Colony
            : (Faction)Math.Min((int)firstFaction, (int)secondFaction);

        var selectedHome = choice switch
        {
            CampHomeChoice.FirstCamp => firstHome,
            CampHomeChoice.SecondCamp => secondHome,
            _ => ChooseAutomaticHome(world, firstFaction, firstHome,
                secondFaction, secondHome)
        };

        world.ColonyArrivalsProcessedByFaction.TryGetValue(firstFaction, out var firstArrivals);
        world.ColonyArrivalsProcessedByFaction.TryGetValue(secondFaction, out var secondArrivals);
        world.ColonyArrivalsProcessedByFaction.Remove(firstFaction);
        world.ColonyArrivalsProcessedByFaction.Remove(secondFaction);
        world.ColonyArrivalsProcessedByFaction[canonical] =
            Math.Max(firstArrivals, secondArrivals);

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Faction == firstFaction || npc.Faction == secondFaction)
            {
                npc.Faction = canonical;
            }

            foreach (var memory in npc.Memory.KnownAgents.Values)
            {
                if (memory.Faction == firstFaction || memory.Faction == secondFaction)
                {
                    memory.Faction = canonical;
                }
            }
        }

        world.FactionHomes.Remove(firstFaction);
        world.FactionHomes.Remove(secondFaction);
        world.FactionHomes[canonical] = selectedHome;
        world.DoorStateVersion++;

        Trace.Emit(world, first.Id, "CampsMerged",
            $"Target=NPC{second.Id.Value} First={firstFaction} " +
            $"Second={secondFaction} Canonical={canonical} " +
            $"Home={selectedHome.Q},{selectedHome.R}");
        reason = string.Empty;
        return true;
    }

    private static TileCoord ChooseAutomaticHome(
        WorldState world, Faction firstFaction, TileCoord firstHome,
        Faction secondFaction, TileCoord secondHome)
    {
        var firstScore = CampInfrastructureScore(world, firstHome);
        var secondScore = CampInfrastructureScore(world, secondHome);
        if (firstScore != secondScore)
        {
            return firstScore > secondScore ? firstHome : secondHome;
        }

        var firstResidents = world.Entities.Npcs.Values.Count(n =>
            n.Health > 0f && n.Faction == firstFaction);
        var secondResidents = world.Entities.Npcs.Values.Count(n =>
            n.Health > 0f && n.Faction == secondFaction);
        if (firstResidents != secondResidents)
        {
            return firstResidents > secondResidents ? firstHome : secondHome;
        }

        return (int)firstFaction <= (int)secondFaction ? firstHome : secondHome;
    }

    internal static int CampInfrastructureScore(WorldState world, TileCoord home)
    {
        var score = 0;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (HexSpatialMath.HexDistance(obj.Tile, home) > Spec72.MaxCampRadiusTiles ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            if (def.HasTag("Bed")) score += 8;
            else if (obj.IsArchitectureElement) score += 4;
            else if (def.HasTag("Campfire")) score += 3;
            else if (def.HasTag("BuildSite")) score += 1;
        }

        return score;
    }
}

public enum CampHomeChoice
{
    Automatic = 0,
    FirstCamp = 1,
    SecondCamp = 2
}

}
