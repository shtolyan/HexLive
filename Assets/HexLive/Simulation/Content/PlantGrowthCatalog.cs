using System;
using System.Collections.Generic;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Content
{

/// <summary>
/// §173: one authoritative seed → visible stages → mature plant table.
/// Adding another renewable plant means adding its seed and stages to content,
/// then one row here; the growth system and planting completion stay generic.
/// Durations are read from balance on every query so tests and runtime tuning
/// never retain a stale static copy.
/// </summary>
public static class PlantGrowthCatalog
{
    public sealed class Profile
    {
        private readonly Func<int> _stageDurationTicks;

        internal Profile(
            string seedDefinitionId,
            string matureDefinitionId,
            string[] stageDefinitionIds,
            Func<int> stageDurationTicks)
        {
            SeedDefinitionId = seedDefinitionId;
            MatureDefinitionId = matureDefinitionId;
            StageDefinitionIds = stageDefinitionIds;
            _stageDurationTicks = stageDurationTicks;
        }

        public string SeedDefinitionId { get; }
        public string MatureDefinitionId { get; }
        public string[] StageDefinitionIds { get; }
        public int StageDurationTicks => _stageDurationTicks();

        public int TotalGrowthTicks => StageDurationTicks * StageDefinitionIds.Length;
    }

    private static readonly Profile[] Profiles =
    {
        new Profile(
            ContentIds.Coconut,
            ContentIds.Palm,
            new[]
            {
                ContentIds.PalmSprout,
                ContentIds.PalmSapling,
                ContentIds.PalmYoung,
            },
            () => WorldBalance.PalmGrowthStageTicks),
        new Profile(
            ContentIds.HerbSeed,
            ContentIds.HerbBush,
            new[]
            {
                ContentIds.HerbSprout,
                ContentIds.HerbYoung,
                ContentIds.HerbBudding,
            },
            () => WorldBalance.HerbGrowthStageTicks),
        new Profile(
            ContentIds.YuccaSeed,
            ContentIds.Yucca,
            new[]
            {
                ContentIds.YuccaSprout,
                ContentIds.YuccaYoung,
                ContentIds.YuccaJuvenile,
            },
            () => WorldBalance.YuccaGrowthStageTicks),
    };

    public static IReadOnlyList<Profile> All => Profiles;

    public static bool TryGetBySeed(string definitionId, out Profile profile)
    {
        foreach (var candidate in All)
        {
            if (candidate.SeedDefinitionId == definitionId)
            {
                profile = candidate;
                return true;
            }
        }

        profile = null;
        return false;
    }

    public static bool TryGetByStage(
        string definitionId, out Profile profile, out int stageIndex)
    {
        foreach (var candidate in All)
        {
            for (var i = 0; i < candidate.StageDefinitionIds.Length; i++)
            {
                if (candidate.StageDefinitionIds[i] == definitionId)
                {
                    profile = candidate;
                    stageIndex = i;
                    return true;
                }
            }
        }

        profile = null;
        stageIndex = -1;
        return false;
    }

    /// <summary>Visible definition at age since planting, preserving overdue time.</summary>
    public static string DefinitionAtAge(Profile profile, int ageTicks)
    {
        if (profile.StageDurationTicks <= 0 || profile.StageDefinitionIds.Length == 0)
        {
            return profile.MatureDefinitionId;
        }

        var stageIndex = Math.Max(0, ageTicks) / profile.StageDurationTicks;
        return stageIndex >= profile.StageDefinitionIds.Length
            ? profile.MatureDefinitionId
            : profile.StageDefinitionIds[stageIndex];
    }
}

}
