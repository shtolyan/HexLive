using System.Diagnostics;

namespace HexLive.AgentHost;

/// <summary>Subscription-backed inference via the supported CLI, never extracted OAuth credentials.</summary>
public static class CodexDecisionRunner
{
    public static ProcessStartInfo CreateStartInfo(string executable, string directory)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // In particular, do not pass MCP, XAI, ElevenLabs or OpenAI API credentials.
        start.Environment.Clear();
        foreach (var key in new[] { "HOME", "USER", "LOGNAME", "PATH", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["RUST_LOG"] = "off";
        return start;
    }

    public static async Task CheckLoginAsync(string executable, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(executable, Path.GetTempPath());
        start.ArgumentList.Add("login");
        start.ArgumentList.Add("status");
        var (code, output) = await RunAsync(start, "", cancellationToken);
        if (code != 0 || !output.Contains("Logged in using ChatGPT", StringComparison.Ordinal))
            throw new InvalidOperationException("Codex requires ChatGPT login; API-key login is not accepted.");
    }

    public static async Task<string> DecideAsync(string executable, string prompt,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        await CheckLoginAsync(executable, timeout.Token);
        var directory = Directory.CreateTempSubdirectory("hexlive-codex-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var output = Path.Combine(directory, "decision.json");
        try
        {
            var start = CreateStartInfo(executable, directory);
            foreach (var arg in new[] { "exec", "--ignore-user-config", "--ephemeral",
                "--skip-git-repo-check", "-s", "read-only", "-m", "gpt-6-astra",
                "-c", "forced_login_method=\"chatgpt\"", "-c", "model_reasoning_effort=\"high\"",
                "-c", "web_search=\"disabled\"", "--disable", "shell_tool",
                "--disable", "multi_agent", "--disable", "apps", "--disable", "plugins",
                "--disable", "browser_use", "--disable", "computer_use",
                "--disable", "image_generation", "--disable", "hooks",
                "-o", output, "-" }) start.ArgumentList.Add(arg);
            var (code, _) = await RunAsync(start, prompt, timeout.Token);
            if (code != 0 || !File.Exists(output))
                throw new InvalidOperationException("Codex subscription request failed; no provider fallback.");
            if (new FileInfo(output).Length > 32768)
                throw new InvalidDataException("Codex decision exceeds size limit.");
            return await File.ReadAllTextAsync(output, timeout.Token);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start,
        string input, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        process.Start();
        using var stop = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        // Drain concurrently. Never forward CLI streams (which can contain prompts) to host logs.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(CancellationToken.None);
        var result = (await stdout) + (await stderr);
        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, result);
    }
}
