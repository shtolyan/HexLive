using System.Text.Json;
using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

public sealed record SavedAgentActivity(DateTimeOffset Timestamp, string World, string Text, bool IsIntent);

/// <summary>Read-only projection of saved intentions and diary, never runtime status or transcripts.</summary>
public static class WorkspaceActivity
{
    public static async Task<IReadOnlyList<SavedAgentActivity>> ReadAsync(string root)
    {
        foreach (var directory in new[] { root, Path.Combine(root, ".state") })
        {
            if (!Directory.Exists(directory)) return Array.Empty<SavedAgentActivity>();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedWorkspaceNotAllowed");
        }
        var path = Path.Combine(root, ".state", "state.json");
        if (!File.Exists(path)) return Array.Empty<SavedAgentActivity>();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("InvalidActivityArchive");
        await using var stream = File.OpenRead(path);
        var archive = await JsonSerializer.DeserializeAsync<MashaArchive>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (archive?.Worlds ?? []).Where(w => w != null).SelectMany(w =>
            (w.Journal ?? []).Where(j => j != null && !string.IsNullOrWhiteSpace(j.Text))
                .Select(j => new SavedAgentActivity(j.CreatedAtUtc, Clip(w.Label, 128), Clip(j.Text, 400), false))
                .Concat(string.IsNullOrWhiteSpace(w.LastIntentSummary) ? [] :
                    new[] { new SavedAgentActivity(w.LastSeenUtc, Clip(w.Label, 128), Clip(w.LastIntentSummary, 240), true) }))
            .OrderByDescending(x => x.Timestamp).Take(32).ToArray();
    }
    private static string Clip(string? value, int limit) => string.IsNullOrEmpty(value) ? "" : value[..Math.Min(value.Length, limit)];
}
