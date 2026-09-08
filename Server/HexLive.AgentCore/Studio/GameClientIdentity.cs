using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace HexLive.AgentCore.Studio;

/// <summary>Read the standalone game's identity; never invent a new player or read Editor prefs.</summary>
public static class GameClientIdentity
{
    public const string Preference = "HexLive.RemoteClientId";
    public static string Normalize(string? value) => Guid.TryParseExact(value?.Trim().TrimEnd('\0'), "N", out var id)
        ? id.ToString("N") : throw new InvalidDataException("GameClientIdentityMissing");

    public static async Task<string> ReadAsync(CancellationToken token)
    {
        if (OperatingSystem.IsMacOS())
        {
            var start = new ProcessStartInfo("/usr/bin/defaults")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("read"); start.ArgumentList.Add("com.juilcylove.hexgirls"); start.ArgumentList.Add(Preference);
            using var process = Process.Start(start) ?? throw new IOException("GameClientIdentityMissing");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token); await error;
                if (process.ExitCode != 0) throw new InvalidDataException("GameClientIdentityMissing");
                return Normalize(await output);
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\JuicyLove\HexLive", writable: false);
            var values = key?.GetValueNames().Where(n => n == Preference || n.StartsWith(Preference + "_h", StringComparison.Ordinal))
                .Select(n => key.GetValue(n)).Select(v => v is byte[] bytes ? Encoding.UTF8.GetString(bytes) : v as string)
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray();
            if (values?.Length == 1) return Normalize(values[0]);
        }
        throw new InvalidDataException("GameClientIdentityMissing");
    }
}
