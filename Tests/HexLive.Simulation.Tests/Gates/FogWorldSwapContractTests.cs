using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §148 / bug #287: карта памяти тумана — зеркало снапшота, а не биография
/// вида. При смене мира под живым клиентом (другой seed или регрессия тика)
/// рендерер обязан забыть _everSeenTiles и связанные множества — иначе новая
/// игра на сервере наследует разведанность предыдущего мира.
/// </summary>
public sealed class FogWorldSwapContractTests
{
    [Test]
    public void WorldSwapClearsTheFogMemoryMirror()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Rendering", "HexWorldRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(renderer, Does.Contain("snapshot.Seed != _lastWorldSeed ||"),
                "Личность мира — seed, а не только регрессия тика: реконнект к " +
                "новой игре может прийти с большим тиком.");
            Assert.That(renderer, Does.Contain("_everSeenTiles.Clear();"),
                "Смена мира обязана забывать карту памяти тумана.");
            Assert.That(renderer, Does.Contain("_lastSeenNpcTiles.Clear();"),
                "«?» на местах чужаков прошлого мира — тоже память, тоже забыть.");
            Assert.That(renderer, Does.Contain("_lastWorldSeed = snapshot.Seed;"));
        });
    }
}

}
