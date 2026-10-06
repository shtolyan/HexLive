namespace HexLive.AgentCore.Studio;

/// <summary>Use the installed desktop CLI for both inference and the model catalog.</summary>
public static class CodexExecutable
{
    public static string Resolve() => OperatingSystem.IsMacOS()
        ? ResolveMacOS(File.Exists)
        : OperatingSystem.IsWindows() ? "codex.exe" : "codex";

    public static string ResolveMacOS(Func<string, bool> exists)
    {
        const string resources = "/Applications/ChatGPT.app/Contents/Resources/";
        foreach (var path in new[]
        {
            resources + "codex-cli/CodexCLI.app/Contents/MacOS/codex",
            resources + "codex"
        })
            if (exists(path)) return path;
        return "codex";
    }
}
