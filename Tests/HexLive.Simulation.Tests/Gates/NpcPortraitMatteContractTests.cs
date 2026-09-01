using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §150.4 r2: портретный диск NPC прозрачен через тот же двухпроходный matte,
/// что импосторы — альфе кадра URP доверять нельзя (preserveFramebufferAlpha
/// выключен). Математика одна: премультиплированный RGB + линейная альфа;
/// круглая маска гасит и RGB.
/// </summary>
public sealed class NpcPortraitMatteContractTests
{
    [Test]
    public void PortraitBakeSharesTheImpostorMatte()
    {
        var cache = Read("UI", "NpcPortraitCache.cs");
        var impostor = Read("Views", "ObjectImpostor.cs");

        Assert.Multiple(() =>
        {
            Assert.That(cache, Does.Contain("ObjectImpostor.CaptureMatte("));
            Assert.That(cache, Does.Contain("ComposeMattePixels(black, white, pixels)"));
            Assert.That(cache, Does.Contain("p.r = (byte)(p.r * a);"),
                "Премультиплированная текстура: маска гасит и RGB.");
            Assert.That(impostor, Does.Contain(
                "internal static void ComposeMattePixels"),
                "Matte-математика одна на пекарню и фотографа.");
        });
    }

    private static string Read(string folder, string file) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            folder, file));
}

}
