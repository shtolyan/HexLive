using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Headless contract for the live identity portrait. Unity owns the render and
/// UI assemblies; the simulation suite guards their isolation/layout source.
/// </summary>
public sealed class IdentityPortraitUiContractTests
{
    private static string Presentation(params string[] parts) => Path.Combine(
        new[] { RepoPaths.Root, "Assets", "HexLive", "UnityPresentation" }
            .Concat(parts).ToArray());

    [Test]
    public void WidePortraitKeepsTheOldFaceAnchorAndIsolatesTheSelectedActor()
    {
        var stage = File.ReadAllText(Presentation("UI", "PortraitStage.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(stage, Does.Contain("private const int TextureWidth = 512"));
            Assert.That(stage, Does.Contain("private const int TextureHeight = 368"));
            Assert.That(stage, Does.Contain("SubjectViewportX = 108f / 396f"));
            Assert.That(stage, Does.Contain("rotation * Vector3.right * horizontalOffset"));
            Assert.That(stage, Does.Contain("_camera.cullingMask = 1 << _portraitLayer"));
            Assert.That(stage, Does.Contain("IsolateSelectedActor()"));
            Assert.That(stage, Does.Contain("RestoreActorLayers()"));
            Assert.That(stage, Does.Contain("RenderPipelineManager.beginCameraRendering"));
            Assert.That(stage, Does.Contain("Camera.onPreCull"));
            Assert.That(stage, Does.Not.Contain("1 << actorsLayer"),
                "An Actors-layer camera allows every neighbour to photobomb the card.");
            Assert.That(stage, Does.Not.Contain("_camera.Render()"),
                "The portrait must stay inside the normal URP camera order.");
        });
    }

    [Test]
    public void IdentityCardUsesAFullBackgroundAndCompactOverlayControls()
    {
        var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var start = panel.IndexOf("private VisualElement BuildIdentityColumn()",
            StringComparison.Ordinal);
        var end = panel.IndexOf("private VisualElement BuildNeedsColumn()", start,
            StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var identity = panel[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(identity, Does.Contain("_portrait.style.left = 0f"));
            Assert.That(identity, Does.Contain("_portrait.style.right = 0f"));
            Assert.That(identity, Does.Contain("_portrait.style.top = 0f"));
            Assert.That(identity, Does.Contain("_portrait.style.bottom = 0f"));
            Assert.That(identity, Does.Contain("BackgroundSizeType.Cover"));
            Assert.That(identity, Does.Not.Contain("portraitSize"));
            Assert.That(identity, Does.Not.Contain("wrap.Add(_healthRing)"));
            Assert.That(identity, Does.Contain("vitalsCluster.style.right = 4f"));
            Assert.That(identity, Does.Contain("vitalsCluster.style.bottom = 12f"));
            Assert.That(identity, Does.Contain("vitalsCluster.style.width = 218f"));
            Assert.That(identity, Does.Contain("vitalsRail.style.right = 68f"));
            Assert.That(identity, Does.Not.Contain("readoutScrim"),
                "The full RenderTexture must not be covered by a half-card dimmer.");
            Assert.That(identity, Does.Contain("healthBadge.style.right = 0f"));
            Assert.That(identity, Does.Contain("healthBadge.style.bottom = 0f"));
            Assert.That(identity, Does.Contain("healthBadge.style.width = 96f"));
            // §105 r3: the badge carries the ring, the number and two letters.
            // A heart glyph and a second red percentage under it read as three
            // competing HP readouts.
            Assert.That(identity, Does.Contain("new Label(\"HP\")"));
            Assert.That(identity, Does.Not.Contain("♥ HP"));
            Assert.That(identity, Does.Not.Contain("_healthLockedValue"));
            Assert.That(identity, Does.Contain("button.style.left = 12f"));
            Assert.That(identity, Does.Contain("\"HexLive/UI/IdentityAiIcon\", \"🧠\""));
            Assert.That(identity, Does.Contain("\"HexLive/UI/IdentityManualIcon\", \"🎮\""));
            Assert.That(identity, Does.Contain("_starvingBadge.style.right = 14f"));
            Assert.That(identity, Does.Contain("var glyph = new Label(\"🎒\")"));
            Assert.That(identity, Does.Contain("vitalsRail.style.backgroundColor = IdentityGlass"));
            Assert.That(identity, Does.Contain("vitalsRail.Add(statusRow)"));
            Assert.That(identity, Does.Contain("vitalsRail.Add(uvRow)"));
            Assert.That(identity, Does.Contain("vitalsCluster.Add(vitalsRail)"));
            Assert.That(identity, Does.Contain("vitalsCluster.Add(healthBadge)"));
            Assert.That(identity, Does.Contain("right = 80f"),
                "Transient order feedback must not cover the journal button column.");
            Assert.That(identity, Does.Not.Contain("statusRow.style.backgroundColor"));
            Assert.That(identity, Does.Not.Contain("uvRow.style.backgroundColor"));
        });
    }

    [Test]
    public void BackpackIsAnAccentedTopRightControl()
    {
        var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var start = panel.IndexOf("private VisualElement BuildInventoryButton()",
            StringComparison.Ordinal);
        var end = panel.IndexOf("private VisualElement BuildNeedsColumn()", start,
            StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var backpack = panel[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(backpack, Does.Contain("button.style.right = 12f"));
            Assert.That(backpack, Does.Contain("button.style.top = 12f"));
            Assert.That(backpack, Does.Contain("button.style.width = 58f"));
            Assert.That(backpack, Does.Not.Contain("button.style.bottom"));
            Assert.That(backpack, Does.Contain("inventoryButtonInner"));
            Assert.That(backpack, Does.Contain("inventoryButtonAccent"));
            Assert.That(backpack, Does.Contain("SetBorder(button, NeonCyanDim, 1.5f)"));
            Assert.That(backpack, Does.Contain(
                "Resources.Load<Texture2D>(\"HexLive/UI/IdentityBackpackIcon\")"));
        });
    }

    [Test]
    public void JournalSitsDirectlyBelowTheBackpackAtTheRightEdge()
    {
        var journal = File.ReadAllText(Presentation("UI", "CharacterPanel.Journal.cs"));
        var start = journal.IndexOf("private VisualElement BuildJournalButton()",
            StringComparison.Ordinal);
        var end = journal.IndexOf("private void BuildJournalWindow()", start,
            StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var button = journal[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(button, Does.Contain("button.style.right = 12f"));
            Assert.That(button, Does.Contain("button.style.top = 78f"));
            Assert.That(button, Does.Contain("button.style.width = 58f"));
            Assert.That(button, Does.Contain("button.style.height = 58f"));
        });
    }

    [Test]
    public void GeneratedIdentityIconsShipInPlayerResourcesWithFallbacks()
    {
        // Бутстрап-иконки UI живут в Player Resources (как эмодзи-баблы), не в
        // atomic-реестре: панель обязана рисоваться до прихода каталога.
        var ui = Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "HexLive", "UI");
        var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var names = new[]
        {
            "IdentityBackpackIcon",
            "IdentityAiIcon",
            "IdentityManualIcon",
        };

        Assert.Multiple(() =>
        {
            foreach (var name in names)
            {
                var png = Path.Combine(ui, name + ".png");
                Assert.That(File.Exists(png), Is.True, png);
                Assert.That(new FileInfo(png).Length, Is.GreaterThan(1024), png);
                Assert.That(File.Exists(png + ".meta"), Is.True, png + ".meta");
                Assert.That(panel, Does.Contain("HexLive/UI/" + name));
            }

            Assert.That(panel, Does.Contain("Text is deliberately only a missing-resource fallback"));
            Assert.That(panel, Does.Contain("var glyph = new Label(\"🎒\")"));
        });
    }

    [Test]
    public void NeonGridIsAPlayerShaderWithAnUnscaledAnimationClock()
    {
        var shaderPath = Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "HexLive", "UI",
            "PortraitNeonGrid.shader");
        Assert.That(File.Exists(shaderPath), Is.True);
        var shader = File.ReadAllText(shaderPath);
        var stage = File.ReadAllText(Presentation("UI", "PortraitStage.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("Shader \"HexLive/PortraitNeonGrid\""));
            Assert.That(shader, Does.Contain("\"LightMode\" = \"SRPDefaultUnlit\""));
            Assert.That(shader, Does.Contain("_UnscaledTime"));
            Assert.That(shader, Does.Contain("floorCoordinates"));
            Assert.That(stage, Does.Contain(
                "Resources.Load<Shader>(NeonGridShaderPath)"));
            Assert.That(stage, Does.Contain("!shader.isSupported"));
            Assert.That(stage, Does.Contain("Time.unscaledTime"));
        });
    }
}
