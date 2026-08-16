using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class ButcherPresentationContractTests
{
    [Test]
    public void ButcherUsesKneelingCraftWhileKeepingKnifeProp()
    {
        var actor = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing",
            "NpcActorView.cs"));
        var methodStart = actor.IndexOf("public void SetInteraction(",
            System.StringComparison.Ordinal);
        var methodEnd = actor.IndexOf("public void SetWardrobeAction(", methodStart,
            System.StringComparison.Ordinal);

        Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(methodEnd, Is.GreaterThan(methodStart));
        var setInteraction = actor.Substring(methodStart, methodEnd - methodStart);
        Assert.Multiple(() =>
        {
            Assert.That(setInteraction,
                Does.Contain("var butchering = !_legless && interaction == \"Butcher\";"));
            Assert.That(setInteraction,
                Does.Contain("var kneelingCraft = crafting || looting || butchering ||"));
            Assert.That(setInteraction,
                Does.Contain("SetHandProp(crafting || looting ? string.Empty"),
                "Butcher нельзя добавлять в очистку hand prop: нож должен остаться в руке.");
            Assert.That(actor, Does.Not.Contain("case \"Butcher\":"),
                "Старый procedural Work fallback для Butcher должен быть удалён.");
        });
    }
}
