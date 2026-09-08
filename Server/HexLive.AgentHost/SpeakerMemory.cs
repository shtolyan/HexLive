using System.Text.Json;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentHost;

public sealed class SpeakerMemory
{
    public PortablePlayerBond Bond { get; set; } = new();
    public string? RelationshipState { get; set; }
    public string VoiceName { get; set; } = "Голос";
    public string LastAssessmentReason { get; set; } = "";
    public List<PortableMemory> Facts { get; set; } = new();
    public List<string> AppliedMessageIds { get; set; } = new();
    public List<string> RecentConversation { get; set; } = new();
}

public static class AgentGameTime
{
    public static void Observe(PortablePlayerBond bond, MashaWorldHandle world)
    {
        if (bond.ClockWorldKey != world.EpisodeId ||
            (bond.LastClockTick >= 0 && world.Tick < bond.LastClockTick))
        {
            bond.ClockWorldKey = world.EpisodeId;
            bond.LastVoiceTick = bond.LastDepartureTick = bond.LastReturnTick = -1;
            bond.LastObservedPlayerPresent = null;
            bond.AwaitingReturnVoice = false;
        }
        bond.LastClockTick = world.Tick;
    }

    public static string Describe(PortablePlayerBond bond, MashaWorldHandle world)
    {
        string Elapsed(long from, long to) => world.DayLengthTicks <= 0 || from < 0 || to < from ||
            bond.ClockWorldKey != world.EpisodeId || world.Tick < bond.LastClockTick ? "неизвестно" :
            $"{(long)((to - from) * 1440d / world.DayLengthTicks)} игровых минут";
        return "<game_contact_time>\n" +
            "С последнего завершённого общения: " + Elapsed(bond.LastVoiceTick, world.Tick) + ".\n" +
            "Наблюдаемое отсутствие до возвращения: " + Elapsed(bond.LastDepartureTick, bond.LastReturnTick) + ".\n" +
            "Ожидается первая реплика после возвращения: " + (bond.AwaitingReturnVoice ? "да" : "нет") + ".\n" +
            "Это игровое время. Пауза не добавляет времени; молчание не доказывает отсутствие. " +
            "Не выдумывай длительность при неизвестных отметках.\n</game_contact_time>";
    }
}

public sealed partial class MashaMemoryStore
{
    public static PortablePlayerBond BondFor(MashaArchive archive, string speakerKey) =>
        speakerKey.Length == 0 ? archive.PlayerBond :
        archive.Speakers.TryGetValue(speakerKey, out var speaker) ? speaker.Bond : new();

    private SpeakerMemory GetSpeaker(string key)
    {
        if (!_archive.Speakers.TryGetValue(key, out var speaker))
            _archive.Speakers[key] = speaker = new();
        return speaker;
    }

    public async Task BindSpeakerAsync(string key, bool confirmedLegacyOwner, CancellationToken token)
    {
        if (key.Length is < 1 or > 200) throw new InvalidDataException("InvalidSpeakerKey");
        await _gate.WaitAsync(token);
        try
        {
            if (_archive.SchemaVersion < 2)
            {
                var backupDirectory = Path.Combine(Path.GetDirectoryName(_filePath)!, "backups");
                Directory.CreateDirectory(backupDirectory);
                var backup = Path.Combine(backupDirectory, "before-speakers-v2.json");
                if (!File.Exists(backup)) WriteArchiveAtomically(backup, _archive);
            }
            var speaker = GetSpeaker(key);
            if (confirmedLegacyOwner && _archive.PrimarySpeakerKey == null)
            {
                // Retain the original legacy record, user files and import provenance.
                speaker.Bond = JsonSerializer.Deserialize<PortablePlayerBond>(JsonSerializer.Serialize(_archive.PlayerBond))!;
                if (speaker.Bond.LastInteractionTick >= 0 && _archive.Worlds.Any(w =>
                    w.Id == speaker.Bond.LastInteractionEpisodeId && w.Game == "hexlive"))
                {
                    speaker.Bond.ClockWorldKey = speaker.Bond.LastInteractionEpisodeId;
                    speaker.Bond.LastVoiceTick = speaker.Bond.LastInteractionTick;
                    speaker.Bond.LastClockTick = speaker.Bond.LastInteractionTick;
                }
                speaker.Facts = _archive.CoreMemories.Where(m => m.Source is "model-user" or "molly-user")
                    .Select(m => JsonSerializer.Deserialize<PortableMemory>(JsonSerializer.Serialize(m))!).ToList();
                speaker.RelationshipState = null;
                foreach (var value in _archive.SuppressedMemoryValues.Where(v => v.StartsWith("legacy-user\n", StringComparison.Ordinal)).ToArray())
                    _archive.SuppressedMemoryValues.Add(key + "\n" + value["legacy-user\n".Length..]);
                _archive.PrimarySpeakerKey = key;
            }
            _archive.SchemaVersion = 2;
            await SaveAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasProcessedMessagesAsync(string key, IReadOnlyList<string> ids, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return _archive.Speakers.TryGetValue(key, out var speaker) && ids.Any(speaker.AppliedMessageIds.Contains); }
        finally { _gate.Release(); }
    }

    public async Task ObserveSpeakerPresenceAsync(string key, bool present, MashaWorldHandle world, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var bond = GetSpeaker(key).Bond;
            var oldTick = bond.LastClockTick;
            var oldWorld = bond.ClockWorldKey;
            AgentGameTime.Observe(bond, world);
            if (bond.LastObservedPlayerPresent == present)
            {
                if (oldTick != bond.LastClockTick || oldWorld != bond.ClockWorldKey) await SaveAsync(token);
                return;
            }
            if (present)
            {
                bond.LastReturnTick = world.Tick;
                if (bond.LastObservedPlayerPresent == false && bond.LastVoiceTick >= 0) bond.AwaitingReturnVoice = true;
            }
            else bond.LastDepartureTick = world.Tick;
            bond.LastObservedPlayerPresent = present;
            await SaveAsync(token);
        }
        finally { _gate.Release(); }
    }

    private void ApplySpeakerAssessment(MashaWorldHandle world, string turnId, CompanionDecision decision)
    {
        var speaker = GetSpeaker(world.SpeakerKey);
        var bond = speaker.Bond;
        // The request can finish after a newer heartbeat clock observation.
        // Only the observation path diagnoses a rollback; committing an earlier request must not.
        if (bond.ClockWorldKey != world.EpisodeId) AgentGameTime.Observe(bond, world);
        var relation = speaker.RelationshipState == null ? new VoiceRelationship(new(
            bond.Familiarity, bond.Trust, bond.Affinity, speaker.VoiceName, null)) :
            VoiceRelationship.Restore(speaker.RelationshipState);
        var ids = world.MessageIds.Length > 0 ? world.MessageIds : new[] { turnId };
        if (decision.RelationshipAssessment is { } assessment)
        {
            if (!relation.Apply(ids, assessment, DateTimeOffset.UtcNow)) return;
            speaker.LastAssessmentReason = assessment.Reason;
            speaker.VoiceName = relation.Snapshot.VoiceName;
            bond.Familiarity = relation.Snapshot.Familiarity;
            bond.Trust = relation.Snapshot.Trust;
            bond.Affinity = relation.Snapshot.Sympathy;
            speaker.RelationshipState = relation.ExportState();
        }
        foreach (var id in ids) if (!speaker.AppliedMessageIds.Contains(id)) speaker.AppliedMessageIds.Add(id);
        TrimOldest(speaker.AppliedMessageIds, VoiceRelationship.DeduplicationCapacity);
        bond.LastVoiceTick = Math.Max(world.Tick, bond.LastClockTick);
        bond.LastInteractionEpisodeId = world.EpisodeId;
        bond.LastInteractionTick = bond.LastVoiceTick;
        bond.LastInteractionUtc = DateTimeOffset.UtcNow;
        bond.AwaitingReturnVoice = false;
    }
}
