using System;
using System.IO;
using System.Text.Json;
using HexLive.Server.Releases;
using NUnit.Framework;

namespace HexLive.Server.Tests.Releases;

[TestFixture]
public sealed class PlayerReleaseStoreTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "hexlive-releases-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void ReadsLatestAndOnlyRegularShaNamedBlob()
    {
        var platform = Path.Combine(_root, "windows");
        var blobs = Path.Combine(platform, "blobs");
        Directory.CreateDirectory(blobs);
        var sha = new string('a', 64);
        File.WriteAllBytes(Path.Combine(blobs, sha), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(platform, "HexLiveInstaller.exe"), new byte[] { 4, 5 });
        File.WriteAllText(Path.Combine(platform, "latest.json"),
            "{\"version\":\"0.1.1\",\"playerRelease\":{\"archiveSha256\":\"" + sha + "\"}}");

        var store = new PlayerReleaseStore(_root);
        using var latest = store.ReadLatest();
        Assert.That(latest!.RootElement.GetProperty("version").GetString(), Is.EqualTo("0.1.1"));
        Assert.That(store.TryGetBlob(sha, out var path, out var size), Is.True);
        Assert.That(path, Is.EqualTo(Path.Combine(blobs, sha)));
        Assert.That(size, Is.EqualTo(3));
        Assert.That(store.TryGetBlob("../windows-latest.json", out _, out _), Is.False);
        Assert.That(store.TryGetInstaller(out var installer, out var installerSize), Is.True);
        Assert.That(installer, Is.EqualTo(Path.Combine(platform, "HexLiveInstaller.exe")));
        Assert.That(installerSize, Is.EqualTo(2));
    }

    [TestCase("")]
    [TestCase("abc")]
    [TestCase("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void RejectsInvalidBlobIdentity(string value) => Assert.That(PlayerReleaseStore.IsSha256(value), Is.False);
}
