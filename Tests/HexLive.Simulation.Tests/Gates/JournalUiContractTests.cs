using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>Headless source contract for the §136.9 journal viewport.</summary>
public sealed class JournalUiContractTests
{
    [Test]
    public void JournalHasNoVisibleScrollbarsAndScrollsByDirectContentDrag()
    {
        var path = Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.Journal.cs");
        var journal = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(journal, Does.Contain(
                "horizontalScrollerVisibility = ScrollerVisibility.Hidden"));
            Assert.That(journal, Does.Contain(
                "verticalScrollerVisibility = ScrollerVisibility.Hidden"));
            Assert.That(journal, Does.Contain(
                "touchScrollBehavior = ScrollView.TouchScrollBehavior.Elastic"));
            Assert.That(journal, Does.Contain(
                "contentViewport.AddManipulator(_journalDragScroll)"));
            Assert.That(journal, Does.Contain(
                "JournalDragScrollManipulator : PointerManipulator"));
            Assert.That(journal, Does.Contain("DragThreshold = 6f"));
            Assert.That(journal, Does.Contain("target.CapturePointer(_pointerId)"));
            Assert.That(journal, Does.Contain("target.schedule.Execute(TickInertia)"));
            Assert.That(journal, Does.Contain(
                "target.RegisterCallback<WheelEvent>(OnWheel)"));
            Assert.That(journal, Does.Contain("private static void OnWheel(WheelEvent evt)"));
            Assert.That(journal, Does.Contain("evt.StopPropagation();"),
                "Wheel and trackpad scrolling belong to camera zoom/yaw, not the journal.");
        });
    }
}
