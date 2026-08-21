using System;
using System.IO;
using HexLive.Simulation.Bootstrap;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

public sealed class ServerStartupSaveHeaderTests
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hexlive-server-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void ExistingV2Save_OverridesStartupSeedAndMode()
    {
        var save = Path.Combine(_directory, "bigisland.sav");
        WriteSaveHeader(save, version: 2, seed: 1144051735, mode: GameMode.BigIsland, tick: 36425);
        var options = ServerOptions.Parse(new[] { "--save", save });

        options!.ContinueExistingSaveIfPresent();

        Assert.Multiple(() =>
        {
            Assert.That(options.Seed, Is.EqualTo(1144051735));
            Assert.That(options.Mode, Is.EqualTo(GameMode.BigIsland));
        });
    }

    [Test]
    public void ExistingV1Save_IsContinuedAsLegacyFeud()
    {
        var save = Path.Combine(_directory, "legacy.sav");
        WriteSaveHeader(save, version: 1, seed: 987, mode: GameMode.BigIsland, tick: 1234);
        var options = ServerOptions.Parse(new[] { "--mode", "bigisland", "--save", save });

        options!.ContinueExistingSaveIfPresent();

        Assert.Multiple(() =>
        {
            Assert.That(options.Seed, Is.EqualTo(987));
            Assert.That(options.Mode, Is.EqualTo(GameMode.Feud));
        });
    }

    [Test]
    public void ExistingUnknownSave_IsRejectedInsteadOfBeingOverwritten()
    {
        var save = Path.Combine(_directory, "corrupt.sav");
        File.WriteAllBytes(save, new byte[] { 1, 2, 3, 4, 5 });
        var options = ServerOptions.Parse(new[] { "--save", save });

        Assert.That(() => options!.ContinueExistingSaveIfPresent(),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("refusing to start over it"));
    }

    private static void WriteSaveHeader(string path, int version, int seed, GameMode mode, int tick)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write(unchecked((int)0x48584C53));
        writer.Write(version);
        writer.Write(seed);
        if (version >= 2)
        {
            writer.Write((int)mode);
        }

        writer.Write(tick);
        writer.Write(0);
    }
}

}
