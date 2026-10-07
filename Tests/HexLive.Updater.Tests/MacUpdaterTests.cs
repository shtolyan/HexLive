using System.Diagnostics;
using System.IO.Compression;
using HexLive.Updater;
using NUnit.Framework;

namespace HexLive.Updater.Tests;

// These exercise the installation transaction with disposable process fixtures.
// Production still runs codesign and Gatekeeper before entering this transaction.
[Platform("MacOsX")]
public sealed class MacUpdaterTests
{
    private string _root = "", _state = "", _old = "", _journalPath = "";
    private InstallJournal _journal = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "hexlive mac update " + Guid.NewGuid());
        _state = Path.Combine(_root, ".hexlive-updates");
        _old = Path.Combine(_root, "Releases", "v1.0.0", "HexLive.app");
        _journalPath = Path.Combine(_state, "journal.json");
        var candidate = Path.Combine(_state, "v2", "HexLive.app");
        Directory.CreateDirectory(candidate);
        WriteExecutable(Path.Combine(_old, "Contents", "MacOS", "HexLive"),
            "printf old > " + Quote(Path.Combine(_root, "old-started")));
        Directory.CreateSymbolicLink(Path.Combine(_root, "HexLive.app"), "Releases/v1.0.0/HexLive.app");
        _journal = new InstallJournal {
            installPath = Path.Combine(_root, "HexLive.app"), backup = Path.Combine(_state, "previous"),
            candidate = candidate, executable = Path.Combine(candidate, "Contents", "MacOS", "HexLive"),
            acknowledgement = Path.Combine(_state, "ack-test"), version = "2.0.0", sha256 = new string('a', 64) };
    }

    [TearDown] public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public async Task SwitchAndRollbackPreserveRelativeLatestTargetAndBothVersions()
    {
        await Program.Switch(_journal, _journalPath);
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_journal.candidate));
        Assert.That(Resolve(_journal.backup), Is.EqualTo(_old));
        Program.Restore(_journal, _journalPath);
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_old));
        Assert.That(Directory.Exists(_journal.candidate), Is.True);
        Assert.That(Directory.Exists(_old), Is.True);
        Program.Restore(_journal, _journalPath);
    }

    [Test]
    public async Task RollbackReplacesBrokenLatestLink()
    {
        await Program.Switch(_journal, _journalPath);
        Directory.Delete(_journal.candidate, true);
        Program.Restore(_journal, _journalPath);
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_old));
        Assert.That(_journal.phase, Is.EqualTo("rolledBack"));
    }

    [Test]
    public async Task ManuallyInstalledRealAppCanBeSwitchedAndRestored()
    {
        Directory.Delete(_journal.installPath);
        Directory.Move(_old, _journal.installPath);
        await Program.Switch(_journal, _journalPath);
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_journal.candidate));
        Program.Restore(_journal, _journalPath);
        Assert.That(new DirectoryInfo(_journal.installPath).LinkTarget, Is.Null);
        Assert.That(File.Exists(Path.Combine(_journal.installPath, "Contents/MacOS/HexLive")), Is.True);
        Assert.That(Directory.Exists(_journal.candidate), Is.True);
    }

    [Test]
    public async Task ConfirmedRestartKeepsArgumentsAndRemovesOldFailureMarker()
    {
        var recorded = Path.Combine(_root, "arguments.txt");
        WriteExecutable(_journal.executable, "printf '%s\\n' \"$@\" > " + Quote(recorded) + "\n" + Ack("2.0.0"));
        File.WriteAllText(Path.Combine(_state, "failed-release.txt"), _journal.sha256);
        await Program.Activate(_journal, _journalPath, new[] { "-hexlive-server", "wss://test.invalid/watch", "имя с пробелом" }, TimeSpan.FromSeconds(5));
        Assert.That(_journal.phase, Is.EqualTo("complete"));
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_journal.candidate));
        Assert.That(File.ReadAllLines(recorded), Is.EqualTo(new[] {
            "-hexlive-server", "wss://test.invalid/watch", "имя с пробелом",
            "-hexlive-install-path", _journal.installPath, "-hexlive-update-ack", _journal.acknowledgement }));
        Assert.That(File.Exists(_journal.acknowledgement), Is.False);
        Assert.That(File.Exists(Path.Combine(_state, "failed-release.txt")), Is.False);
        Assert.That(Directory.Exists(_old), Is.True);
    }

    [Test]
    public async Task FailedStartupRestoresAndRestartsPreviousApplication()
    {
        WriteExecutable(_journal.executable, "exit 7");
        Assert.ThrowsAsync<IOException>(() => Program.Activate(_journal, _journalPath, Array.Empty<string>(), TimeSpan.FromSeconds(5)));
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_old));
        Assert.That(File.ReadAllText(Path.Combine(_state, "failed-release.txt")), Is.EqualTo(_journal.sha256));
        await WaitForFile(Path.Combine(_root, "old-started"));
    }

    [Test]
    public async Task WrongAcknowledgementTimesOutAndKillsOnlyCandidate()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        WriteExecutable(_journal.executable, "echo $$ > " + Quote(pidFile) + "\n" + Ack("1.0.0") + "\nexec /bin/sleep 30");
        using var other = Process.Start("/bin/sleep", "30")!;
        try
        {
            Assert.ThrowsAsync<IOException>(() => Program.Activate(_journal, _journalPath, Array.Empty<string>(), TimeSpan.FromMilliseconds(500)));
            Assert.That(other.HasExited, Is.False);
            var pid = int.Parse(File.ReadAllText(pidFile));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            Assert.That(Resolve(_journal.installPath), Is.EqualTo(_old));
            await WaitForFile(Path.Combine(_root, "old-started"));
        }
        finally { if (!other.HasExited) { other.Kill(); await other.WaitForExitAsync(); } }
    }

    [Test]
    public async Task RecoveryKeepsAlreadyAcknowledgedVersion()
    {
        await Program.Switch(_journal, _journalPath);
        File.WriteAllText(_journal.acknowledgement, "2.0.0");
        Program.Recover(_journal, _journalPath);
        Assert.That(_journal.phase, Is.EqualTo("complete"));
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_journal.candidate));
    }

    [Test]
    public async Task RecoveryRollsBackUnacknowledgedVersion()
    {
        await Program.Switch(_journal, _journalPath);
        Program.Recover(_journal, _journalPath);
        Assert.That(_journal.phase, Is.EqualTo("rolledBack"));
        Assert.That(Resolve(_journal.installPath), Is.EqualTo(_old));
    }

    [Test]
    public async Task DittoArchivePreservesExecutableAndFrameworkLinks()
    {
        if (!OperatingSystem.IsMacOS()) return;
        WriteExecutable(_journal.executable, "exit 0");
        var framework = Path.Combine(_journal.candidate, "Contents", "Frameworks", "Test.framework");
        Directory.CreateDirectory(Path.Combine(framework, "Versions", "A"));
        File.WriteAllText(Path.Combine(framework, "Versions", "A", "Test"), "framework");
        Directory.CreateSymbolicLink(Path.Combine(framework, "Versions", "Current"), "A");
        File.CreateSymbolicLink(Path.Combine(framework, "Test"), "Versions/Current/Test");
        var zipPath = Path.Combine(_root, "player.zip");
        await Program.Run("/usr/bin/ditto", "-c", "-k", "--keepParent", _journal.candidate, zipPath);
        var output = Path.Combine(_root, "extracted"); Directory.CreateDirectory(output);
        using var zip = ZipFile.OpenRead(zipPath);
        Program.ExtractSafe(zipPath, output, zip.Entries.Sum(e => e.Length), true);
        var app = Path.Combine(output, "HexLive.app");
        Assert.That(File.ReadAllText(Path.Combine(app, "Contents/Frameworks/Test.framework/Test")), Is.EqualTo("framework"));
        Assert.That(File.GetUnixFileMode(Path.Combine(app, "Contents/MacOS/HexLive")) & UnixFileMode.UserExecute, Is.Not.Zero);
        await Program.Run(Path.Combine(app, "Contents/MacOS/HexLive"));
    }

    [Test]
    public void SymlinkEscapeInMacArchiveIsRejectedBeforeExtraction()
    {
        var zipPath = Path.Combine(_root, "unsafe.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("HexLive.app/Contents/escape");
            entry.ExternalAttributes = unchecked((int)(0xa1ffu << 16));
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../../../outside");
        }
        var output = Path.Combine(_root, "extracted"); Directory.CreateDirectory(output);
        Assert.Throws<InvalidDataException>(() => Program.ExtractSafe(zipPath, output, 100, true));
        Assert.That(Directory.GetFileSystemEntries(output), Is.Empty);
    }

    private static string Resolve(string path) => new DirectoryInfo(path).ResolveLinkTarget(true)!.FullName;
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static string Ack(string version) =>
        "while [ \"$#\" -gt 0 ]; do\n if [ \"$1\" = '-hexlive-update-ack' ]; then shift; printf '%s' " + Quote(version) + " > \"$1\"; break; fi\n shift\ndone";
    private static void WriteExecutable(string path, string script)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static async Task WaitForFile(string path)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(path) && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
        Assert.That(File.Exists(path), Is.True);
    }
}
