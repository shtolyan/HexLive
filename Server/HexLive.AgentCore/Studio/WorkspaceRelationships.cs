using System.Text.Json;
using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

public sealed record SavedVoiceRelationship(string SpeakerKey, string VoiceName, float Familiarity,
    float Trust, float Sympathy, string Reason);

public static class WorkspaceRelationships
{
    public static async Task<IReadOnlyList<SavedVoiceRelationship>> ReadAsync(string root)
    {
        var state = Path.Combine(root, ".state");
        var path = Path.Combine(state, "state.json");
        foreach (var directory in new[] { root, state })
        {
            if (!Directory.Exists(directory)) return [];
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedWorkspaceNotAllowed");
        }
        if (!File.Exists(path)) return [];
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("InvalidRelationshipArchive");
        await using var stream = File.OpenRead(path);
        var archive = await JsonSerializer.DeserializeAsync<MashaArchive>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return archive?.Speakers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new SavedVoiceRelationship(
            p.Key, p.Value.VoiceName, p.Value.Bond.Familiarity, p.Value.Bond.Trust, p.Value.Bond.Affinity,
            p.Value.LastAssessmentReason)).ToArray() ?? [];
    }
}
