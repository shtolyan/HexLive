using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>§163.3a: shared prompt assembly. Authored voice is never model-writable memory.</summary>
public static class AgentPromptBuilder
{
    public static string Rules => AgentPromptFiles.Read("rules.md");

    public static string Build(string? styleId, string memoryContext, string transcript)
    {
        var mandatory = AgentPromptFiles.Read("assessment.md");
        return Rules + mandatory + DialogueStyles.Build(styleId, memoryContext, transcript) +
            AgentPromptFiles.Read("language.md");
    }
}
