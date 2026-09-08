using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Agents
{

/// <summary>§159: оценка завершённого разговора Маши с голосом игрока.</summary>
public enum CompanionReaction
{
    None,
    Warm,
    Neutral,
    Tense,
    Hostile
}

/// <summary>§159.4: постоянные отношения Маши с невидимым знакомым голосом.</summary>
public sealed class PlayerVoiceBond
{
    public float Familiarity { get; set; }
    public float Trust { get; set; }
    public float Affinity { get; set; }
    public int LastInteractionTick { get; set; } = -1;
}

public sealed class CompanionMemoryUpsert
{
    public CompanionMemoryUpsert(string key, string value, float importance)
    {
        Key = key ?? string.Empty;
        Value = value ?? string.Empty;
        // Keep the submitted value intact so the authoritative command
        // boundary can reject malformed/non-finite input instead of silently
        // accepting it as a clamped LLM fact.
        Importance = importance;
    }

    public string Key { get; }
    public string Value { get; }
    public float Importance { get; }
}

public sealed class CompanionMemoryCard
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public float Importance { get; set; }
    public int LastUpdatedTick { get; set; }
}

public sealed class CompanionNarrativeEntry
{
    public int Tick { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// §159: небольшое, строго ограниченное долговременное состояние LLM-компаньона.
/// Транскриптам здесь места нет: мост присылает только извлечённые факты,
/// короткое намерение и личную дневниковую запись.
/// </summary>
public sealed class CompanionState
{
    public const int MaxMemories = 64;
    public const int MaxJournalEntries = 48;
    public const int MaxAppliedTurnIds = 64;
    public const int MaxIntentCharacters = 240;
    public const int MaxMemoryKeyCharacters = 64;
    public const int MaxMemoryValueCharacters = 400;
    public const int MaxJournalCharacters = 400;

    public PlayerVoiceBond PlayerVoiceBond { get; } = new();
    // §159.1: persisted migration latch for the player-approved starter look.
    // It is deliberately separate from the spawn marker: an existing Masha
    // may need one upgrade, while later ordinary wardrobe changes must survive.
    public int AuthoredOutfitVersion { get; set; }
    public int HexkufaExposure { get; set; }
    public string LastIntentSummary { get; set; } = string.Empty;
    public int LastJournalHour { get; set; } = -1;
    public List<CompanionMemoryCard> Memories { get; } = new();
    public List<CompanionNarrativeEntry> NarrativeJournal { get; } = new();
    public List<string> AppliedTurnIds { get; } = new();

    public bool HasApplied(string turnId) =>
        !string.IsNullOrWhiteSpace(turnId) && AppliedTurnIds.Contains(turnId);

    /// <summary>Applies one validated bridge result. Returns false for a replay.</summary>
    public bool ApplyTurn(
        string turnId,
        CompanionReaction reaction,
        string intentSummary,
        IReadOnlyList<CompanionMemoryUpsert> memoryUpserts,
        string journalText,
        int tick,
        int ticksPerHour,
        ref float social)
    {
        turnId = Trim(turnId, 80);
        if (turnId.Length == 0 || HasApplied(turnId))
        {
            return false;
        }

        LastIntentSummary = Trim(intentSummary, MaxIntentCharacters);
        ApplyReaction(reaction, tick, ref social);

        var count = Math.Min(memoryUpserts?.Count ?? 0, 3);
        for (var i = 0; i < count; i++)
        {
            UpsertMemory(memoryUpserts[i], tick);
        }

        journalText = Trim(journalText, MaxJournalCharacters);
        var hour = ticksPerHour > 0 ? tick / ticksPerHour : tick;
        if (journalText.Length > 0 && hour != LastJournalHour)
        {
            NarrativeJournal.Add(new CompanionNarrativeEntry { Tick = tick, Text = journalText });
            LastJournalHour = hour;
            while (NarrativeJournal.Count > MaxJournalEntries)
            {
                NarrativeJournal.RemoveAt(0);
            }
        }

        AppliedTurnIds.Add(turnId);
        while (AppliedTurnIds.Count > MaxAppliedTurnIds)
        {
            AppliedTurnIds.RemoveAt(0);
        }

        return true;
    }

    /// <summary>
    /// §159.8: the world owns only the physical Social need. Personal bond,
    /// intent and memories are committed by MashaCore outside the save.
    /// </summary>
    public bool ApplyVoiceSocialEffect(
        string turnId,
        CompanionReaction reaction,
        ref float social)
    {
        turnId = Trim(turnId, 80);
        if (turnId.Length == 0 || HasApplied(turnId)) return false;

        social = MathUtil.Clamp01(social + SocialDelta(reaction));
        AppliedTurnIds.Add(turnId);
        while (AppliedTurnIds.Count > MaxAppliedTurnIds)
        {
            AppliedTurnIds.RemoveAt(0);
        }
        return true;
    }

    private void ApplyReaction(CompanionReaction reaction, int tick, ref float social)
    {
        var familiarity = 0f;
        var trust = 0f;
        var affinity = 0f;
        var socialDelta = SocialDelta(reaction);
        switch (reaction)
        {
            case CompanionReaction.Warm:
                familiarity = 0.03f; trust = 0.03f; affinity = 0.02f;
                break;
            case CompanionReaction.Neutral:
                familiarity = 0.02f;
                break;
            case CompanionReaction.Tense:
                familiarity = 0.01f; trust = -0.03f; affinity = -0.02f;
                break;
            case CompanionReaction.Hostile:
                familiarity = 0.01f; trust = -0.06f; affinity = -0.04f;
                break;
        }

        social = MathUtil.Clamp01(social + socialDelta);
        PlayerVoiceBond.Familiarity = MathUtil.Clamp01(PlayerVoiceBond.Familiarity + familiarity);
        PlayerVoiceBond.Trust = MathUtil.Clamp01(PlayerVoiceBond.Trust + trust);
        PlayerVoiceBond.Affinity = MathUtil.Clamp01(PlayerVoiceBond.Affinity + affinity);
        if (reaction != CompanionReaction.None)
        {
            PlayerVoiceBond.LastInteractionTick = tick;
        }
    }

    private static float SocialDelta(CompanionReaction reaction) => reaction switch
    {
        CompanionReaction.Warm => 0.18f,
        CompanionReaction.Neutral => 0.08f,
        CompanionReaction.Tense => -0.06f,
        CompanionReaction.Hostile => -0.12f,
        _ => 0f
    };

    private void UpsertMemory(CompanionMemoryUpsert update, int tick)
    {
        var key = Trim(update?.Key, MaxMemoryKeyCharacters);
        var value = Trim(update?.Value, MaxMemoryValueCharacters);
        if (key.Length == 0 || value.Length == 0)
        {
            return;
        }

        var existing = Memories.Find(x => string.Equals(x.Key, key, StringComparison.Ordinal));
        if (existing is null)
        {
            if (Memories.Count >= MaxMemories)
            {
                Memories.Sort((a, b) =>
                {
                    var importance = a.Importance.CompareTo(b.Importance);
                    return importance != 0 ? importance : a.LastUpdatedTick.CompareTo(b.LastUpdatedTick);
                });
                Memories.RemoveAt(0);
            }

            existing = new CompanionMemoryCard { Key = key };
            Memories.Add(existing);
        }

        existing.Value = value;
        existing.Importance = MathUtil.Clamp01(update.Importance);
        existing.LastUpdatedTick = tick;
    }

    internal static string Trim(string? value, int maxCharacters)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= maxCharacters ? value : value.Substring(0, maxCharacters);
    }
}

}
