using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class ContextMenuEntryTests
{
    [TestCase("Выпить (пусто)")]
    [TestCase("Drink (empty)")]
    public void AlreadyExplainedDisabledActionHasOneCaption_Bug637(string caption)
    {
        var entry = new ContextMenuEntry(caption, () => { }, false, caption);
        Assert.Multiple(() =>
        {
            Assert.That(entry.Label, Is.EqualTo(caption));
            Assert.That(entry.DisabledHint, Is.Null);
            Assert.That(entry.Enabled, Is.False);
        });
    }

    [Test]
    public void DistinctMissingToolReasonAndActionArePreserved()
    {
        var calls = 0;
        var entry = new ContextMenuEntry("Проколоть", () => calls++, false, "Нужен нож") { Danger = true };
        Assert.That(entry.DisabledHint, Is.EqualTo("Нужен нож"));
        Assert.That(entry.Danger, Is.True);
        entry.Activate();
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void SubmenuUsesTheSameCaptionRule()
    {
        var child = new ContextMenuEntry("Child", () => { });
        var parent = new ContextMenuEntry("Unavailable", new[] { child }, false, "Unavailable");
        Assert.That(parent.DisabledHint, Is.Null);
        Assert.That(parent.Children, Is.EquivalentTo(new[] { child }));
    }
}
}
