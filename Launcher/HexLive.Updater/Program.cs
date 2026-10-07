using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HexLive.Updates;

namespace HexLive.Updater;

public sealed class InstallRequest
{
    public string envelope = "", archive = "", installPath = "";
    public int parentPid;
    public long parentStartTicks;
    public string[] arguments = Array.Empty<string>();
}
public sealed class InstallJournal
{
    public string installPath = "", backup = "", candidate = "", executable = "", acknowledgement = "", sha256 = "";
    public string version = "";
    public string phase = "prepared";
}
public static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var log = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "updater.log");
        try
        {
            using var configStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("update-config.json")
                ?? throw new InvalidDataException("Updater trust configuration is missing");
            var config = JsonSerializer.Deserialize<UpdateConfig>(configStream, Json)!;
            var request = JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(args[0]), Json)!;
            var envelope = JsonSerializer.Deserialize<ReleaseEnvelope>(request.envelope, Json)!;
            var release = JsonSerializer.Deserialize<ReleaseManifest>(ReleaseTrust.Verify(envelope, config), Json)!;
            release.Validate(config.platform, config.architecture);
            if ((OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "unsupported") != release.platform)
                throw new InvalidDataException("Wrong OS");
            ReleaseTrust.VerifyArchive(request.archive, release);
            var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.installPath));
            if (Path.GetFileName(install) != (release.platform == "macos" ? "HexLive.app" : "HexLive"))
                throw new InvalidDataException("Unexpected application directory");
            var root = Path.GetDirectoryName(install)!;
            var stateRoot = Path.Combine(root, ".hexlive-updates");
            Directory.CreateDirectory(stateRoot);
            using var installLock = new FileStream(Path.Combine(stateRoot, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var journalPath = Path.Combine(stateRoot, "journal.json");
            if (File.Exists(journalPath))
            {
                var prior = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journalPath), Json)!;
                ValidateJournal(prior, install, stateRoot);
                Recover(prior, journalPath);
            }
            var candidateRoot = Path.Combine(stateRoot, release.version + "-" + release.sha256[..12] + "-" + Guid.NewGuid().ToString("N"));
            EnsureSpace(root, release.unpackedSize + release.size);
            Directory.CreateDirectory(candidateRoot);
            ExtractSafe(request.archive, candidateRoot, release.unpackedSize, release.platform == "macos");
            var candidate = Path.Combine(candidateRoot, Path.GetFileName(install));
            var executable = release.platform == "macos" ? Path.Combine(candidate, "Contents", "MacOS", "HexLive") : Path.Combine(candidate, "HexLive.exe");
            if (!File.Exists(executable)) throw new InvalidDataException("Player executable missing");
            if (OperatingSystem.IsMacOS())
            {
                await Run("/usr/bin/codesign", "--verify", "--deep", "--strict", candidate);
                await Run("/usr/sbin/spctl", "--assess", "--type", "execute", candidate);
            }
            File.WriteAllText(args[0] + ".ready", "ready");
            // Wait for the exact originating process; PID reuse must not block or kill another app.
            try
            {
                using var parent = Process.GetProcessById(request.parentPid);
                if (parent.StartTime.ToUniversalTime().Ticks == request.parentStartTicks)
                    await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (ArgumentException) { }
            var journal = new InstallJournal {
                installPath = install, candidate = candidate, executable = executable, sha256 = release.sha256, version = release.version,
                backup = Path.Combine(stateRoot, "previous-" + Guid.NewGuid().ToString("N")),
                acknowledgement = Path.Combine(stateRoot, "ack-" + Guid.NewGuid().ToString("N")) };
            await Activate(journal, journalPath, request.arguments, TimeSpan.FromSeconds(120));
            return 0;
        }
        catch (Exception ex) { File.AppendAllText(log, DateTime.UtcNow.ToString("O") + " " + ex + "\n"); return 1; }
        finally { try { File.Delete(args[0]); } catch (IOException) { } }
    }
    public static void EnsureSpace(string path, long required)
    {
        var full = Path.GetFullPath(path);
        var drive = DriveInfo.GetDrives().Where(d => full.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Name.Length).First();
        if (drive.AvailableFreeSpace < required + 64L * 1024 * 1024) throw new IOException("Not enough disk space");
    }
    internal static void ValidateJournal(InstallJournal j, string install, string root)
    {
        if (j.installPath != install) throw new InvalidDataException("Journal installation mismatch");
        Under(root, j.backup); Under(root, j.candidate); Under(root, j.acknowledgement);
    }
    internal static bool Acknowledged(InstallJournal journal)
    {
        try
        {
            return journal.version.Length > 0 && File.Exists(journal.acknowledgement) &&
                File.ReadAllText(journal.acknowledgement).Trim() == journal.version;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static void Recover(InstallJournal journal, string journalPath)
    {
        if (journal.phase == "complete" || journal.phase == "rolledBack") return;
        if (journal.phase == "switched" && Acknowledged(journal))
        {
            journal.phase = "complete";
            WriteAtomic(journalPath, journal);
            TryDelete(journal.acknowledgement);
        }
        else Restore(journal, journalPath);
    }

    // Called only after archive, platform signature and originating-process checks.
    internal static async Task Activate(InstallJournal journal, string journalPath, string[] arguments, TimeSpan timeout)
    {
        WriteAtomic(journalPath, journal);
        Process? child = null;
        try
        {
            await Switch(journal, journalPath);
            var start = new ProcessStartInfo(journal.executable) { UseShellExecute = false, WorkingDirectory = journal.candidate };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("-hexlive-install-path"); start.ArgumentList.Add(journal.installPath);
            start.ArgumentList.Add("-hexlive-update-ack"); start.ArgumentList.Add(journal.acknowledgement);
            child = Process.Start(start) ?? throw new IOException("Player did not start");
            var clock = Stopwatch.StartNew();
            while (!Acknowledged(journal) && !child.HasExited && clock.Elapsed < timeout)
                await Task.Delay(100);
            if (!Acknowledged(journal)) throw new IOException("Player did not acknowledge startup");
            journal.phase = "complete"; WriteAtomic(journalPath, journal);
        }
        catch
        {
            if (child != null && !child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            Restore(journal, journalPath);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(journalPath)!, "failed-release.txt"), journal.sha256);
            var previousExe = OperatingSystem.IsMacOS() ? Path.Combine(journal.installPath, "Contents", "MacOS", "HexLive") : Path.Combine(journal.installPath, "HexLive.exe");
            if (File.Exists(previousExe))
            {
                var start = new ProcessStartInfo(previousExe) { UseShellExecute = false, WorkingDirectory = journal.installPath };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.ArgumentList.Add("-hexlive-install-path"); start.ArgumentList.Add(journal.installPath);
                using var previous = Process.Start(start);
            }
            throw;
        }
        finally { child?.Dispose(); }
        // Cleanup is not an installation failure: never roll back a confirmed release
        // just because its acknowledgement or an older failure marker cannot be deleted.
        TryDelete(journal.acknowledgement);
        TryDelete(Path.Combine(Path.GetDirectoryName(journalPath)!, "failed-release.txt"));
    }

    internal static async Task Switch(InstallJournal journal, string journalPath)
    {
        if (!Directory.Exists(journal.installPath)) throw new DirectoryNotFoundException("Installed player missing");
        var installedLink = new DirectoryInfo(journal.installPath).ResolveLinkTarget(true);
        if (installedLink != null)
        {
            // Resolve relative links before placing their backup in a different directory.
            await CreateLink(journal.backup, installedLink.FullName);
            if (OperatingSystem.IsWindows()) Directory.Delete(journal.installPath);
        }
        else Directory.Move(journal.installPath, journal.backup);
        journal.phase = "backedUp"; WriteAtomic(journalPath, journal);
        if (OperatingSystem.IsWindows()) await CreateLink(journal.installPath, journal.candidate);
        else ReplaceLink(journal.installPath, journal.candidate);
        journal.phase = "switched"; WriteAtomic(journalPath, journal);
    }

    private static void ReplaceLink(string path, string target)
    {
        var pending = path + ".update-" + Guid.NewGuid().ToString("N");
        Directory.CreateSymbolicLink(pending, target);
        try
        {
            // File.Move refuses directory symlinks on .NET/macOS. POSIX rename
            // replaces the link itself atomically, including a dangling old link.
            if (Rename(pending, path) != 0)
                throw new IOException("Cannot atomically replace application link",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { if (new DirectoryInfo(pending).LinkTarget != null) Directory.Delete(pending); }
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string source, string destination);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Restore(InstallJournal j, string journalPath)
    {
        var backupLink = new DirectoryInfo(j.backup).LinkTarget;
        if (Directory.Exists(j.backup) || backupLink != null)
        {
            var installLink = new DirectoryInfo(j.installPath).LinkTarget;
            if (Directory.Exists(j.installPath) && installLink == null)
                throw new IOException("Refusing to replace an unexpected real directory");
            if (!OperatingSystem.IsWindows() && backupLink != null)
            {
                ReplaceLink(j.installPath, new DirectoryInfo(j.backup).ResolveLinkTarget(true)!.FullName);
                Directory.Delete(j.backup);
            }
            else
            {
                if (installLink != null) Directory.Delete(j.installPath);
                Directory.Move(j.backup, j.installPath);
            }
        }
        j.phase = "rolledBack"; WriteAtomic(journalPath, j);
    }
    public static void Under(string root, string target)
    {
        if (!Path.GetFullPath(target).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path escapes destination");
    }
    public static void ExtractSafe(string archive, string destination, long maximumBytes, bool mac)
    {
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Split('/').Any(p => p == ".." || p == "." || p.EndsWith(' ') || p.EndsWith('.')) || !names.Add(name.TrimEnd('/')))
                throw new InvalidDataException("Invalid archive path");
            var target = Path.GetFullPath(Path.Combine(destination, name)); Under(destination, target);
            total = checked(total + entry.Length);
            if (total > maximumBytes) throw new InvalidDataException("Archive exceeds declared size");
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind == 0xa000)
            {
                if (!mac || entry.Length > 4096) throw new InvalidDataException("Invalid symlink");
                using var reader = new StreamReader(entry.Open());
                var link = reader.ReadToEnd();
                if (Path.IsPathRooted(link) || link.Split('/').Contains("..") || link.Contains('\\')) throw new InvalidDataException("Absolute symlink");
                Under(destination, Path.Combine(Path.GetDirectoryName(target)!, link));
                // Symlink descendants could redirect subsequent extraction outside the signed tree.
                if (zip.Entries.Any(other => other.FullName.StartsWith(name.TrimEnd('/') + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("Archive contains symlink descendants");
            }
            else if (kind != 0 && kind != 0x8000 && kind != 0x4000) throw new InvalidDataException("Unsupported archive entry");
        }
        if (mac) Run("/usr/bin/ditto", "-x", "-k", archive, destination).GetAwaiter().GetResult();
        else zip.ExtractToDirectory(destination);
    }
    internal static async Task CreateLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        if ((path + target).IndexOfAny(new[] { '"', '%', '\r', '\n', '!' }) >= 0)
            throw new IOException("Unsupported installation path");
        await Run("cmd.exe", "/d", "/c", $"mklink /J \"{path}\" \"{target}\"");
    }
    internal static async Task Run(string command, params string[] args)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start " + command);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(command + " failed");
    }
    internal static void WriteAtomic<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
        { JsonSerializer.Serialize(output, value, Json); output.Flush(true); }
        File.Move(temp, path, true);
    }
}
