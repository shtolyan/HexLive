using System.Diagnostics;
using System.IO;

namespace HexLive.Launcher;

public sealed record UpdateRequest(int WaitPid, int RequiredProtocol)
{
    public static UpdateRequest? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "--update") return null;
        if (args.Length != 5 || args[1] != "--wait-pid" || args[3] != "--required-protocol" ||
            !int.TryParse(args[2], out var pid) || pid <= 0 ||
            !int.TryParse(args[4], out var protocol) || protocol <= 0)
            throw new ArgumentException("Неверные параметры обновления.");
        return new(pid, protocol);
    }

    public async Task WaitForGameAsync()
    {
        Process game;
        try { game = Process.GetProcessById(WaitPid); }
        catch (ArgumentException) { return; }
        using (game)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            // Never terminate another process. Installation starts only after it exits.
            await game.WaitForExitAsync(timeout.Token);
        }
    }

    public static string InstallRoot(string executable)
    {
        var directory = Directory.GetParent(Path.GetFullPath(executable));
        for (var i = 0; directory != null && i < 5; ++i, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "install-state.json"))) return directory.FullName;
        return LauncherPaths.DefaultInstallRoot;
    }
}
