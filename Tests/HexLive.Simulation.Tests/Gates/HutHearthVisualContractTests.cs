using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class HutHearthVisualContractTests
{
    [Test]
    public void IntegratedHearthCachesItsChildFireEffect()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));
        var furniture = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Environment",
            "HutFurnitureFactory.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(furniture, Does.Contain(
                "AtomicResources.Load<GameObject>(HearthPrefabPath)"));
            Assert.That(furniture, Does.Contain("child.name != \"fire_point\""));
            Assert.That(furniture, Does.Contain(
                "child.SetParent(host.transform, worldPositionStays: true)"));
            Assert.That(renderer, Does.Contain("GetComponentInChildren<"));
            Assert.That(renderer, Does.Contain(
                "HexLive.UnityPresentation.Environment.CampfireEffect>(true)"));
            Assert.That(renderer, Does.Contain("fire.SetLit(worldObject.ResourceAmount > 0f)"));
        });
    }

    [Test]
    public void BurningHearthOwnsAnAudibleSpatialCrackleLifecycle_Bug213()
    {
        var effect = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Environment",
            "CampfireEffect.cs"));
        var audio = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Audio",
            "FmodSfx.cs"));
        var sample = Path.Combine(RepoPaths.Root, "Assets", "StreamingAssets",
            "HexLive", "Sfx", "loop_fire_0.wav");

        Assert.Multiple(() =>
        {
            Assert.That(effect, Does.Contain("FmodSfx.Sfx.LoopFire"));
            Assert.That(effect, Does.Contain("if (_lit && isActiveAndEnabled)"));
            Assert.That(effect, Does.Contain("private void OnDisable()"));
            Assert.That(effect, Does.Contain("FmodSfx.StopLoop(ref _crackle)"));
            Assert.That(audio, Does.Contain(
                "[Sfx.LoopFire] = new Def(0.85f, 1.2f, 28f, 0f, loop: true)"));
            // §152: звук едет в Player внутри StreamingAssets, не через
            // atomic-реестр — иначе костёр молчит до прихода каталога.
            Assert.That(audio, Does.Contain(
                "Path.Combine(Application.streamingAssetsPath, \"HexLive\", \"Sfx\")"));
            Assert.That(File.Exists(sample), Is.True,
                "Crackle-сэмпл обязан лежать в StreamingAssets рядом с Player.");
            Assert.That(new FileInfo(sample).Length, Is.GreaterThan(44),
                "WAV не должен оказаться пустым файлом или LFS-указателем.");
        });
    }
}
