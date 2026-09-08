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
    public void PlayerPublishesAndBootstrapsTheConfigOnlyFistRecord()
    {
        var gearConfig = Source("Assets", "HexLive", "UnityPresentation", "Config",
            "GearConfig.cs");
        var tuning = Source("Assets", "HexLive", "UnityPresentation", "Config",
            "GearTuning.cs");
        var bootstrap = Source("Assets", "HexLive", "UnityPresentation", "Bootstrap",
            "PrototypeRuntimeBootstrap.cs");
        var atomicResources = Source("Assets", "HexLive", "UnityPresentation", "Content",
            "AtomicResources.cs");
        var contentService = Source("Assets", "HexLive", "UnityPresentation", "Content",
            "ContentAssetService.cs");
        var publisher = Source("Assets", "Editor", "AtomicContent",
            "AtomicContentBatchBuild.cs");
        var prewarmStart = tuning.IndexOf(
            "public static void PrewarmPresentation()", StringComparison.Ordinal);
        var prewarmEnd = tuning.IndexOf(
            "Presentation-side lookup for a gear item's VISUALS", prewarmStart,
            StringComparison.Ordinal);
        Assert.That(prewarmStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(prewarmEnd, Is.GreaterThan(prewarmStart));
        var prewarm = tuning[prewarmStart..prewarmEnd];

        Assert.Multiple(() =>
        {
            Assert.That(gearConfig, Does.Contain(
                "public const string FistContentType = \"config\";"));
            Assert.That(gearConfig, Does.Contain(
                "public const string FistContentId = \"gear.fist\";"));
            Assert.That(gearConfig, Does.Contain(
                "public const string FistContentPath = \"HexLive/gear.fist\";"));
            Assert.That(publisher, Does.Contain("DiscoverFistGearRecipe(Add);"));
            Assert.That(publisher, Does.Contain(
                "add(GearConfig.FistContentType, GearConfig.FistContentId"));
            Assert.That(publisher, Does.Contain("Main = fist.path,"),
                "The config-only record must publish fist.asset as its loadable main entry.");
            Assert.That(bootstrap, Does.Contain("Config.GearTuning.PrewarmPresentation();"),
                "Player bootstrap must queue fists before the first combat scene.");
            Assert.That(bootstrap, Does.Not.Contain("Config.GearTuning.LoadAndApply();"),
                "Player bootstrap must not replace server-authoritative simdata/recipes.");
            Assert.That(prewarm, Does.Contain("GearLibrary.LoadPublishedFist()"));
            Assert.That(prewarm, Does.Not.Contain("LoadAndApply"));
            Assert.That(prewarm, Does.Not.Contain("GearCatalog.Override"));
            Assert.That(prewarm, Does.Not.Contain("ApplyRecipe"));
            Assert.That(tuning, Does.Contain(
                "AtomicResources.Load<UnityEngine.Object>(\n" +
                "                GearConfig.FistContentPath) as GearConfig"),
                "Runtime load must address the exact record published by the batch builder.");
            Assert.That(atomicResources, Does.Contain(
                "default: type = \"config\"; id = Stable(normalized); break;"),
                "HexLive/gear.fist must resolve to the published config/gear.fist identity.");
            Assert.That(atomicResources, Does.Contain("Handles[key] = loaded;"),
                "The async preload result must be retained for later ConfigFor polling.");
            Assert.That(contentService, Does.Contain("_registryWaiters.Add(Start);"));
            Assert.That(contentService, Does.Contain("var callbacks = _registryWaiters.ToArray();"));
            Assert.That(contentService, Does.Contain("callback();"),
                "A preload issued before RefreshRegistry completes must not be lost.");
        });
    }

    [Test]
    public void NullMeansNoItemWhileEmptyMeansThePublishedFistSheet()
    {
        var source = Source("Assets", "HexLive", "UnityPresentation", "Config", "GearTuning.cs");
        var method = Method(
            source,
            "public static GearConfig ConfigFor(string gearId)",
            "internal static GearConfig LoadPublishedFist()");

        var nullGuard = method.IndexOf("if (gearId == null)", StringComparison.Ordinal);
        var cache = method.IndexOf("Configs.TryGetValue(gearId, out var cached)",
            StringComparison.Ordinal);
        var fistLoad = method.IndexOf("gearId.Length == 0\n                ? LoadPublishedFist()",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(nullGuard, Is.GreaterThanOrEqualTo(0),
                "Null must retain its optional/no-item meaning.");
            Assert.That(cache, Is.GreaterThan(nullGuard),
                "An already registered empty-id fist config must be read from cache.");
            Assert.That(fistLoad, Is.GreaterThan(cache),
                "An uncached empty id must load the published fist record.");
            Assert.That(method, Does.Not.Contain("gearId ??= string.Empty;"),
                "Null must never be collapsed into GearCatalog.Fist globally.");
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
