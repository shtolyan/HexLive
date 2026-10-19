using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
public sealed class UnityAssetIndexTests
{
    private string _temporary;

    [SetUp]
    public void SetUp()
    {
        _temporary = Path.Combine(Path.GetTempPath(), "hexlive-meta-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporary);
    }

    [TearDown]
    public void TearDown()
    {
        var target = Path.GetFullPath(_temporary);
        Assert.That(target.StartsWith(Path.Combine(Path.GetTempPath(), "hexlive-meta-index-"), StringComparison.Ordinal), Is.True);
        Directory.Delete(target, true);
    }

    private static string Meta(string root, string relative, string guid, int lastFrame = 65)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "fileFormatVersion: 2\nguid: " + guid +
            "\nModelImporter:\n  clipAnimations:\n  - serializedVersion: 16\n    firstFrame: 5\n    lastFrame: " + lastFrame + "\n");
        return path;
    }

    [Test]
    public void SameGuidInDifferentRootsResolvesItsOwnClip()
    {
        const string guid = "1234567890abcdef1234567890abcdef";
        var first = Path.Combine(_temporary, "first");
        var second = Path.Combine(_temporary, "second");
        var a = Meta(first, "nested/a.fbx.meta", guid);
        var b = Meta(second, "b.fbx.meta", guid, 95);
        Assert.Multiple(() =>
        {
            Assert.That(UnityAsset.MetaByGuid(first, guid), Is.EqualTo(a));
            Assert.That(UnityAsset.MetaByGuid(second, guid), Is.EqualTo(b));
            Assert.That(UnityAsset.ClipSecondsByGuid(first, guid), Is.EqualTo(2));
            Assert.That(UnityAsset.ClipSecondsByGuid(second, guid), Is.EqualTo(3));
        });
    }

    [Test]
    public void IndexIncludesNonAnimationMetasAndMissingGuidStillReturnsNull()
    {
        const string guid = "abcdefabcdefabcdefabcdefabcdefab";
        var path = Meta(_temporary, "unrelated/deep/texture.png.meta", guid);
        File.WriteAllText(Path.Combine(_temporary, "without-guid.meta"), "fileFormatVersion: 2\n");
        Assert.That(UnityAsset.MetaByGuid(_temporary, guid), Is.EqualTo(path));
        Assert.That(UnityAsset.MetaByGuid(_temporary, new string('f', 32)), Is.Null);
    }

    [Test]
    public void ConcurrentFirstLookupsNeverObservePartialIndex()
    {
        var guids = Enumerable.Range(1, 192).Select(i => i.ToString("x32", CultureInfo.InvariantCulture)).ToArray();
        var paths = guids.Select((guid, i) => Meta(_temporary, "nested/" + i + ".asset.meta", guid)).ToArray();
        var tasks = guids.Select(guid => Task.Run(() => UnityAsset.MetaByGuid(_temporary, guid))).ToArray();
        Task.WaitAll(tasks);
        Assert.That(tasks.Select(task => task.Result).ToArray(), Is.EqualTo(paths));
    }
}
}
