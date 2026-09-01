using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §104.9 / bug #350: an abuse swing is an ordinary authoritative melee
/// swing. The empty gear id means fists and must resolve the same ordered
/// punch/kick table that the simulation indexes.
/// </summary>
public sealed class FistAttackPresentationContractTests
{
    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoPaths.Root }.Concat(parts).ToArray()));

    private static string Method(string source, string signature, string nextSignature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        var end = source.IndexOf(nextSignature, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing {signature}");
        Assert.That(end, Is.GreaterThan(start), $"Missing boundary {nextSignature}");
        return source[start..end];
    }

    [Test]
    public void EmptyFistIdReadsTheRegisteredConfigBeforeRejectingLazyLoad()
    {
        var source = Source("Assets", "HexLive", "UnityPresentation", "Config", "GearTuning.cs");
        var method = Method(
            source,
            "public static GearConfig ConfigFor(string gearId)",
            "public static bool RequiresAuthoredConfig(string gearId)");

        var normalize = method.IndexOf("gearId ??= string.Empty;", StringComparison.Ordinal);
        var cache = method.IndexOf("Configs.TryGetValue(gearId, out var cached)",
            StringComparison.Ordinal);
        var rejectLazyLoad = method.IndexOf("if (gearId.Length == 0)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(normalize, Is.GreaterThanOrEqualTo(0),
                "Null and empty must converge on the GearCatalog.Fist sentinel.");
            Assert.That(cache, Is.GreaterThan(normalize),
                "The canonical fist config must be read from the registered cache.");
            Assert.That(rejectLazyLoad, Is.GreaterThan(cache),
                "Only the impossible empty-path lazy-load may be rejected, after cache lookup.");
            Assert.That(method, Does.Not.Contain("if (string.IsNullOrEmpty(gearId))"),
                "Rejecting empty before the cache silently discards fist.asset.");
        });
    }

    [Test]
    public void FistAssetAndSimulationShareTheFourRealAttackVariantsInOrder()
    {
        var fistAsset = Path.Combine(RepoPaths.Root, "Assets", "HexLiveContent",
            "RuntimeSource", "Gear", "fist.asset");
        var rows = UnityAsset.StrikeRows(fistAsset);
        var sim = SimData.GearStrikes();
        Assert.That(sim.TryGetValue(string.Empty, out var simRows), Is.True,
            "simdata must contain the empty-id fist sheet.");

        var assetsRoot = Path.Combine(RepoPaths.Root, "Assets");
        var clipNames = rows.Select(row => UnityAsset.MetaByGuid(assetsRoot, row.ClipGuid))
            .Select(meta => meta == null
                ? string.Empty
                : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(meta)))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(4));
            Assert.That(simRows, Has.Count.EqualTo(rows.Count),
                "StrikeIndex must address the same number of authored and simulated rows.");
            Assert.That(clipNames, Is.EqualTo(new[]
            {
                "Punch A_once_to65",
                "Punch B_once_to52",
                "Kick A_once_to48",
                "Kick B_once_to45"
            }), "The canonical table must remain the real hand/foot attack set.");
        });
    }

    [Test]
    public void SnapshotStrikeAndWeaponReachTheCanonicalClipLookup()
    {
        var exporter = Source("Assets", "HexLive", "Simulation", "Debug",
            "WorldSnapshotExporter.cs");
        var renderer = Source("Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs");
        var actor = Source("Assets", "HexLive", "UnityPresentation", "Wearing",
            "NpcActorView.cs");
        var setCombat = Method(actor,
            "public void SetCombat(bool fighting, string weaponId, bool swinging, int strikeIndex = -1,",
            "private void SetHandProp(");

        Assert.Multiple(() =>
        {
            Assert.That(exporter, Does.Contain(
                "MeleeWeaponId = Runtime.MeleeSwing.EffectiveWeapon(npc),"));
            Assert.That(exporter, Does.Contain("StrikeIndex = npc.SwingStrikeIndex,"));
            Assert.That(exporter, Does.Contain("SwingStartTick = npc.SwingStartTick,"));
            Assert.That(renderer, Does.Contain(
                "actorView.SetCombat(npc.IsFighting, WeaponFor(npc), npc.IsSwinging, npc.StrikeIndex,\n" +
                "            npc.SwingStartTick);"));
            Assert.That(setCombat, Does.Contain(
                "Config.GearLibrary.AttackClipsFor(weaponId ?? string.Empty)"));
            Assert.That(setCombat, Does.Contain("attackClips[strikeIndex]"),
                "The sim-selected punch/kick index must select the authored clip directly.");
        });
    }
}
