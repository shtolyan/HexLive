using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Headless source/asset gate for the presentation contracts that cannot be
/// instantiated by the simulation-only test assembly.
/// </summary>
public sealed class CharacterDollAndInventoryUiContractTests
{
    private static string Presentation(params string[] parts) =>
        Path.Combine(new[]
        {
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation"
        }.Concat(parts).ToArray());

    [Test]
    public void CharacterPanelIsFiftyFiftyAndContainsNoScroller()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("_invItemsPane.style.width = Length.Percent(50f)"));
            Assert.That(source, Does.Contain("_invDollPane.style.width = Length.Percent(50f)"));
            Assert.That(source, Does.Not.Contain("new ScrollView"));
            Assert.That(source, Does.Not.Contain("Scroller"));
            Assert.That(source, Does.Not.Contain("_invLeftColumn"));
            Assert.That(source, Does.Not.Contain("_invCenterColumn"));
            Assert.That(source, Does.Not.Contain("_invRightColumn"));
            Assert.That(source, Does.Contain("48f, 42f, 36f, 30f, 28f"));
            Assert.That(source, Does.Contain("mergedHands"));
            Assert.That(source, Does.Contain("slots.Add(BuildInventorySlotCell"));
        });
    }

    [Test]
    public void InventoryDetailRemainsSingleUnscrolledCard()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        Assert.That(Regex.Matches(source, @"void\s+BuildInventoryDetail\s*\(").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(source, @"void\s+ShowItemDetail\s*\(").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(source, @"void\s+BuildItemStats\s*\(").Count, Is.EqualTo(1));
    }

    [Test]
    public void InventoryAndHealthUseOnePersistentStage()
    {
        var stagePath = Presentation("UI", "CharacterDollStage.cs");
        Assert.That(File.Exists(stagePath), Is.True);
        Assert.That(File.Exists(Presentation("UI", "InventoryPreviewStage.cs")), Is.False);
        Assert.That(File.Exists(Presentation("UI", "HealthDollStage.cs")), Is.False);

        var stage = File.ReadAllText(stagePath);
        var setModeStart = stage.IndexOf("public void SetMode", StringComparison.Ordinal);
        var setTargetStart = stage.IndexOf("public void SetTarget", setModeStart, StringComparison.Ordinal);
        var setMode = stage[setModeStart..setTargetStart];
        Assert.Multiple(() =>
        {
            Assert.That(stage, Does.Contain("Inventory,"));
            Assert.That(stage, Does.Contain("CharacterDollMode.Health"));
            Assert.That(stage, Does.Contain("_normalBodyRenderer.enabled"));
            Assert.That(stage, Does.Contain("_healthBodyRenderer.enabled"));
            Assert.That(stage, Does.Contain("ActorBodyResolver.TryResolve"));
            Assert.That(stage, Does.Contain("FittedProstheticPoseFollower"));
            Assert.That(setMode, Does.Not.Contain("Instantiate("),
                "Mode switching must retain the existing clone.");
        });

        foreach (var bootstrap in new[]
                 {
                     Presentation("Bootstrap", "PrototypeRuntimeBootstrap.cs"),
                     Presentation("AbuseTest", "AbuseTestBootstrap.cs"),
                     Presentation("WolfFightTest", "WolfFightTestBootstrap.cs")
                 })
        {
            var source = File.ReadAllText(bootstrap);
            Assert.That(Regex.Matches(source, @"AddComponent<(?:UI\.)?CharacterDollStage>").Count,
                Is.EqualTo(1), bootstrap);
            Assert.That(source, Does.Not.Contain("HealthDollStage"), bootstrap);
            Assert.That(source, Does.Not.Contain("InventoryPreviewStage"), bootstrap);
        }
    }

    [Test]
    public void DollCameraUsesAuthoredBoundsInsteadOfTheSourcesEvaluatedPose()
    {
        var stage = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var frameStart = stage.IndexOf("private void FrameClone()", StringComparison.Ordinal);
        var signatureStart = stage.IndexOf(
            "private int SourceVisualSignature", frameStart, StringComparison.Ordinal);
        var framing = stage[frameStart..signatureStart];

        Assert.Multiple(() =>
        {
            Assert.That(framing, Does.Contain("TryGetStableWorldBounds(renderer"));
            Assert.That(framing, Does.Contain("renderer.localBounds"));
            Assert.That(framing, Does.Contain("renderer.localToWorldMatrix"));
            Assert.That(framing, Does.Not.Match(@"bounds\s*=\s*renderer\.bounds"));
            Assert.That(framing, Does.Not.Contain("Encapsulate(renderer.bounds)"),
                "The evaluated source pose makes sitting and lying dolls change camera scale.");
        });
    }

    [Test]
    public void ActorBodyFlowHasNoNamedDonorOrLegacySelectionFallback()
    {
        var resolver = File.ReadAllText(Presentation("Wearing", "ActorBodyResolver.cs"));
        var limbFactory = File.ReadAllText(Presentation("Wearing", "SeveredLimbFactory.cs"));
        var actorView = File.ReadAllText(Presentation("Wearing", "NpcActorView.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(resolver, Does.Not.Contain("Molly"));
            Assert.That(resolver, Does.Not.Contain("Marta"));
            Assert.That(resolver, Does.Not.Contain("Genesis"));
            Assert.That(resolver, Does.Not.Contain("vertexCount"));
            Assert.That(limbFactory, Does.Not.Contain("BuildFromMarta"));
            Assert.That(limbFactory, Does.Not.Contain("ActorName.Molly"));
            Assert.That(actorView, Does.Not.Contain("defaulting to Marta"));
        });
    }

    [Test]
    public void SeveredLimbUsesEvaluatedPoseCloneAtPersistedHexAnchor()
    {
        var factory = File.ReadAllText(Presentation("Wearing", "SeveredLimbFactory.cs"));
        var dropView = File.ReadAllText(Presentation("Wearing", "SeveredLimbDropView.cs"));
        var actorView = File.ReadAllText(Presentation("Wearing", "NpcActorView.cs"));
        var renderer = File.ReadAllText(Presentation("Rendering", "HexWorldRenderer.cs"));
        var simulation = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "Simulation", "Runtime", "Helpers",
            "AmputateSystemHelpers.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(factory, Does.Contain("BuildFromCurrentPose"));
            Assert.That(factory, Does.Contain("BakeMesh(posedMesh, false)"));
            Assert.That(factory, Does.Contain("CloneChildren"));
            Assert.That(factory, Does.Contain("DestroyTransient(poseClone)"));
            Assert.That(factory, Does.Not.Contain("BuildFromMarta"));
            Assert.That(actorView, Does.Contain("TryGetSeveredLimbOriginalScale"));
            Assert.That(dropView, Does.Contain("new WaitForEndOfFrame()"));
            Assert.That(dropView, Does.Contain("SetParent(transform, true)"));
            Assert.That(renderer, Does.Contain("FreshLimbPoseWindowTicks"));
            Assert.That(renderer, Does.Contain("SeveredLimbDropView"));
            Assert.That(simulation, Does.Contain(
                "limb.RotationDegrees = NormalizeObjectFacing(npc.RotationDegrees)"));
        });
    }

    [Test]
    public void ActorPrefabsDoNotReferenceLegacyNativeBodiesAndBodyFbxAreReadable()
    {
        var assets = Path.Combine(RepoPaths.Root, "Assets");
        var actors = Path.Combine(assets, "Resources", "HexLive", "Actors");
        var metaByGuid = Directory.EnumerateFiles(assets, "*.fbx.meta", SearchOption.AllDirectories)
            .Select(path => (path, match: Regex.Match(File.ReadAllText(path), @"(?m)^guid: (\w{32})$")))
            .Where(entry => entry.match.Success)
            .ToDictionary(entry => entry.match.Groups[1].Value, entry => entry.path);

        var legacyGuids = new HashSet<string>(StringComparer.Ordinal)
        {
            "6cdf2be7231d7bb45b39833186608d02",
            "380879164ab69ba489d4a458601c83be",
            "b42d10d8a93b8924f9ef2f79dbf878f0",
            "35015e74319864d4db6671513d7da417"
        };
        var problems = new List<string>();
        foreach (var prefab in Directory.EnumerateFiles(actors, "*.prefab"))
        {
            var yaml = File.ReadAllText(prefab);
            foreach (var legacy in legacyGuids)
            {
                if (yaml.Contains(legacy, StringComparison.Ordinal))
                {
                    problems.Add($"{Path.GetFileName(prefab)} references legacy asset {legacy}");
                }
            }

            var fbxMetas = Regex.Matches(yaml, @"guid: (\w{32}), type: 3").Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .Where(metaByGuid.ContainsKey)
                .Select(guid => metaByGuid[guid])
                .Where(path => path.EndsWith(".fbx.meta", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fbxMetas.Count == 0)
            {
                problems.Add($"{Path.GetFileName(prefab)} has no FBX dependency");
                continue;
            }
            if (!fbxMetas.Any(path => Regex.IsMatch(
                    File.ReadAllText(path), @"(?m)^\s+isReadable: 1$")))
            {
                problems.Add($"{Path.GetFileName(prefab)} has no readable FBX dependency");
            }
        }

        Assert.That(problems, Is.Empty, string.Join("\n", problems));
    }
}

}
