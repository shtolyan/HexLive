using System.Text.Json;

namespace HexLive.AgentHost;

public sealed record DialogueExample(string Id, string Situation, float Trust, float Sympathy,
    string Player, string Reply, string Qualities);

/// <summary>§163.3a: authored, approved examples; never imported into autobiographical memory.</summary>
public static class DialogueStyles
{
    public const string Masha = "masha-sharp-v1";
    public static string Contract => AgentPromptFiles.Read("masha-style.md");

    public static IReadOnlyList<DialogueExample> Examples => new[]
    {
        new DialogueExample("god-approved", "pressure", .1f, -.8f,
            AgentPromptFiles.Text("DialogueStyles.01"),
            AgentPromptFiles.Text("DialogueStyles.02"),
            AgentPromptFiles.Text("DialogueStyles.03")),
        new DialogueExample("ocean-approved", "survival", .1f, -.8f,
            AgentPromptFiles.Text("DialogueStyles.04"),
            AgentPromptFiles.Text("DialogueStyles.05"),
            AgentPromptFiles.Text("DialogueStyles.06")),
        new DialogueExample("machete-approved", "danger", .35f, .2f,
            AgentPromptFiles.Text("DialogueStyles.07"),
            AgentPromptFiles.Text("DialogueStyles.08"),
            AgentPromptFiles.Text("DialogueStyles.09")),
        new DialogueExample("bluff-approved", "danger", 1f, 1f,
            AgentPromptFiles.Text("DialogueStyles.10"),
            AgentPromptFiles.Text("DialogueStyles.11"),
            AgentPromptFiles.Text("DialogueStyles.12")),
        new DialogueExample("saved-approved", "help", 1f, 1f,
            AgentPromptFiles.Text("DialogueStyles.13"),
            AgentPromptFiles.Text("DialogueStyles.14"),
            AgentPromptFiles.Text("DialogueStyles.15"))
    };

    public static DialogueExample[] Select(float trust, float sympathy, string situation) => Examples
        .OrderBy(e => Math.Abs(e.Trust - trust) + 2 * Math.Abs(e.Sympathy - sympathy) +
            (e.Situation == situation ? 0 : .35f))
        .ThenBy(e => e.Id, StringComparer.Ordinal).Take(2).ToArray();

    public static string Build(string? styleId, string memory, string transcript)
    {
        if (string.IsNullOrEmpty(styleId)) return "";
        if (styleId != Masha) throw new InvalidDataException("UnknownDialogueStyle");
        var trust = 0f; var sympathy = 0f;
        const string marker = "<voice_relationship>";
        var from = memory.IndexOf(marker, StringComparison.Ordinal);
        var to = memory.IndexOf("</voice_relationship>", StringComparison.Ordinal);
        if (from >= 0 && to > from)
        {
            using var json = JsonDocument.Parse(memory.Substring(from + marker.Length, to - from - marker.Length));
            trust = json.RootElement.GetProperty("Trust").GetSingle();
            sympathy = json.RootElement.GetProperty("Sympathy").GetSingle();
        }
        var text = transcript.ToLowerInvariant();
        var situation = new[] { AgentPromptFiles.Text("DialogueStyles.16"), AgentPromptFiles.Text("DialogueStyles.17"), AgentPromptFiles.Text("DialogueStyles.18"), AgentPromptFiles.Text("DialogueStyles.19") }.Any(text.Contains) ? "danger" :
            new[] { AgentPromptFiles.Text("DialogueStyles.20"), AgentPromptFiles.Text("DialogueStyles.21"), AgentPromptFiles.Text("DialogueStyles.22") }.Any(text.Contains) ? "pressure" :
            new[] { AgentPromptFiles.Text("DialogueStyles.23"), AgentPromptFiles.Text("DialogueStyles.24"), AgentPromptFiles.Text("DialogueStyles.25"), AgentPromptFiles.Text("DialogueStyles.26") }.Any(text.Contains) ? "survival" : "help";
        var currentTone = sympathy >= .75f
            ? AgentPromptFiles.Text("DialogueStyles.27") +
              AgentPromptFiles.Text("DialogueStyles.28") +
              AgentPromptFiles.Text("DialogueStyles.29") +
              AgentPromptFiles.Text("DialogueStyles.30") +
              (trust <= .25f ? AgentPromptFiles.Text("DialogueStyles.31") : AgentPromptFiles.Text("DialogueStyles.32"))
            : sympathy <= -.25f
                ? AgentPromptFiles.Text("DialogueStyles.33")
                : AgentPromptFiles.Text("DialogueStyles.34");
        return Contract + "\n<current_delivery>" + currentTone + "</current_delivery>\n<speech_examples_not_memories>\n" +
            JsonSerializer.Serialize(Select(trust, sympathy, situation), new JsonSerializerOptions {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n</speech_examples_not_memories>";
    }

    // Recognize the imported authored identity, never a display name or NPC number.
    public static string? DetectAuthoredWorkspace(string root)
    {
        var path = Path.Combine(root, ".state", "state.json");
        if (!File.Exists(path)) return null;
        foreach (var entry in new[] { root, Path.Combine(root, ".state"), path })
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedWorkspaceNotAllowed");
        if (new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("InvalidRelationshipArchive");
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.TryGetProperty("identity", out var identity) &&
            identity.TryGetProperty("id", out var id) && id.GetString() == "masha" ? Masha : null;
    }
}
