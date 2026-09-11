using System.IO.Compression;
using HexLive.Launcher;
using NUnit.Framework;

namespace HexLive.Launcher.Tests;

[TestFixture]
public sealed class InstallerContractTests
{
    private string _root = null!;
    [SetUp] public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "hexlive-launcher-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Test]
    public void ExtractSafeExtractsPlayerInsideDestination()
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "player.zip");
        using (var output = ZipFile.Open(zip, ZipArchiveMode.Create))
        { var entry = output.CreateEntry("HexLive/HexLive.exe"); using var writer = new StreamWriter(entry.Open()); writer.Write("player"); }
        var destination = Path.Combine(_root, "out"); Directory.CreateDirectory(destination);
        LauncherService.ExtractSafe(zip, destination);
        Assert.That(File.ReadAllText(Path.Combine(destination, "HexLive", "HexLive.exe")), Is.EqualTo("player"));
    }

    [Test]
    public void ExtractSafeRejectsTraversal()
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "evil.zip");
        using (var output = ZipFile.Open(zip, ZipArchiveMode.Create)) output.CreateEntry("../outside.txt");
        var destination = Path.Combine(_root, "out"); Directory.CreateDirectory(destination);
        Assert.That(() => LauncherService.ExtractSafe(zip, destination), Throws.TypeOf<InvalidDataException>());
        Assert.That(File.Exists(Path.Combine(_root, "outside.txt")), Is.False);
    }

    [Test]
    public void InstallStateRequiresExistingExecutable()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "install-state.json"),
            "{\"version\":\"1\",\"executable\":\"Z:/missing/HexLive.exe\"}");
        Assert.That(LauncherService.ReadInstallState(_root), Is.Null);
    }
}
