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
    public void HugeIsland_IsAcceptedByCliAndPersistedByV2Header()
    {
        var fresh = ServerOptions.Parse(new[] { "--mode", "hugeisland" });
        Assert.That(fresh!.Mode, Is.EqualTo(GameMode.HugeIsland));

        var save = Path.Combine(_directory, "hugeisland.sav");
        WriteSaveHeader(save, version: 2, seed: 24680,
            mode: GameMode.HugeIsland, tick: 7654);
        var resumed = ServerOptions.Parse(new[] { "--save", save });
        resumed!.ContinueExistingSaveIfPresent();

        Assert.Multiple(() =>
        {
            Assert.That(resumed.Seed, Is.EqualTo(24680));
            Assert.That(resumed.Mode, Is.EqualTo(GameMode.HugeIsland));
        });
    }

    [Test]
    public void Maniac_IsAcceptedByCliAndPersistedByV2Header()
    {
        var fresh = ServerOptions.Parse(new[] { "--mode", "maniac" });
        Assert.That(fresh!.Mode, Is.EqualTo(GameMode.Maniac));

        var save = Path.Combine(_directory, "maniac.sav");
        WriteSaveHeader(save, version: 2, seed: 31337,
            mode: GameMode.Maniac, tick: 9876);
        var resumed = ServerOptions.Parse(new[] { "--save", save });
        resumed!.ContinueExistingSaveIfPresent();

        Assert.Multiple(() =>
        {
            Assert.That(resumed.Seed, Is.EqualTo(31337));
            Assert.That(resumed.Mode, Is.EqualTo(GameMode.Maniac));
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
