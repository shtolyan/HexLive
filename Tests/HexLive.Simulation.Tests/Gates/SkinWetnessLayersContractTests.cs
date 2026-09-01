using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §35.5 / bug #290: дождь/вода и пот — два раздельных слоя мокроты кожи.
/// Дождевой пул сохнет до НУЛЯ (как ткань), пот живёт своим слоем, видимый
/// блеск — max двух. Единый пул с дном на уровне пота держал дождевую воду
/// на тёплой коже бессрочно: одежда высыхала, кожа блестела «как после
/// купания».
/// </summary>
public sealed class SkinWetnessLayersContractTests
{
    [Test]
    public void RainPoolDriesToZeroAndSweatIsItsOwnLayer()
    {
        var view = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "NpcActorView.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(view, Does.Contain(
                "_skinWetness = Mathf.Max(0f, _skinWetness - dt * SkinDryPerSecond);"),
                "Дно дождевого пула кожи — ноль, как у ткани.");
            Assert.That(view, Does.Contain("private float _skinSweat;"),
                "Пот — отдельный слой, не цель дождевого пула.");
            Assert.That(view, Does.Contain(
                "var skinWet01 = Mathf.Max(_skinWetness, _skinSweat);"),
                "Видимая мокрота — max дождя и пота.");
            Assert.That(view, Does.Not.Contain(
                "Mathf.Max(rainWet > 0.5f ? 1f : 0f, sweatLevel)"),
                "Единый пул с дном-потом — источник бага #290.");
            // #286 остаётся нетронутым: мёртвая зона пота от температуры.
            Assert.That(view, Does.Contain("SweatThermalGate"));
        });
    }
}

}
