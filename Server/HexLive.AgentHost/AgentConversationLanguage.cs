namespace HexLive.AgentHost;

/// <summary>§164: carry language context, not another pending player request.</summary>
public static class AgentConversationLanguage
{
    public static string LastMessage(string current, IReadOnlyList<string> recent, string previous = "")
    {
        if (!string.IsNullOrWhiteSpace(current)) return current;
        if (!string.IsNullOrWhiteSpace(previous)) return previous;
        // The legacy prefix is data; preserve compatibility with saved conversations.
        var prefixes = new[] { AgentPromptFiles.Text("MashaMemoryStore.09"), "Player: " };
        foreach (var line in recent.Reverse())
            foreach (var prefix in prefixes)
                if (line.StartsWith(prefix, StringComparison.Ordinal) && line.Length > prefix.Length)
                    return line[prefix.Length..];
        return "";
    }

    // A delivery hint for the existing lip-sync path, not the language decision.
    public static string SpeechTag(string speech) => speech.Any(c => c is >= '\u0400' and <= '\u04ff')
        ? "ru" : speech.Any(char.IsLetter) && speech.Where(char.IsLetter).All(char.IsAscii) ? "en" : "und";
}
