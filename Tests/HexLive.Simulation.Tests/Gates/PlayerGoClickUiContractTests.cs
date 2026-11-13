using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §121.13 r2: the UI half of «a ground click and the action queue». Unity
/// code is not compiled here, so the sources are checked as text.
/// </summary>
public sealed class PlayerGoClickUiContractTests
{
    private static string Read(params string[] parts) =>
        RepoPaths.ReadText(Path.Combine(new[] { RepoPaths.Root }.Concat(parts).ToArray()));

    private static string Adapter() => Read("Assets", "HexLive", "UnityPresentation", "Input", "SimulationInputAdapter.cs");

    [Test]
    public void PlainClickReplacesOnlyWalkingAndOtherwiseOffersGoHere()
    {
        var adapter = Adapter();
        var ground = adapter.Substring(adapter.IndexOf("var go = new MoveToCommand(new EntityId(OrderNpcId), point);",
            System.StringComparison.Ordinal));
        ground = ground.Substring(0, ground.IndexOf("DestinationMarker.Show", System.StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(ground, Does.Contain("if (append)\n                {\n                    EnqueueOrder(OrderNpcId, go);"),
                "Shift appends a waypoint in every state");
            Assert.That(ground, Does.Contain("else if (PlayerQueueHoldsAction(clickSnapshot, OrderNpcId))\n" +
                                             "                {\n                    OpenGoHereMenu(mousePos, OrderNpcId, point);"),
                "A non-movement action in the queue turns the click into the «Идти сюда» menu");
            Assert.That(ground, Does.Contain("_runner.EnqueueCommand(new ReplaceMovementOrderCommand(go));"),
                "Only-walking queue: one wire command, not a client-side cancel+move pair");
            Assert.That(ground, Does.Not.Contain("CancelPlayerOrderCommand"));
            Assert.That(adapter, Does.Contain("npc.PlayerOrders.Any(order => order.Action != \"MoveTo\")"),
                "Decided from the same snapshot queue the list draws");
        });
    }

    [Test]
    public void GoHereMenuAppendsToTheEndAndShiftReachesTheAdapter()
    {
        var adapter = Adapter();
        var menu = adapter.Substring(adapter.IndexOf("private void OpenGoHereMenu(", System.StringComparison.Ordinal));
        menu = menu.Substring(0, menu.IndexOf("private void EnqueueOrder(", System.StringComparison.Ordinal));
        var camera = Read("Assets", "HexLive", "UnityPresentation", "Input", "RtsCameraController.cs");
        Assert.Multiple(() =>
        {
            Assert.That(menu, Does.Contain("Loc.Get(\"menu.walk_here\")"), "«Идти сюда» is an I2 term");
            Assert.That(menu, Does.Contain("EnqueueOrder(actorId, new MoveToCommand(new EntityId(actorId), point));"),
                "Choosing it appends an ordinary queued go");
            Assert.That(Regex.Matches(menu, "new ContextMenuEntry\\(").Count, Is.EqualTo(1), "One entry");
            Assert.That(menu, Does.Contain("ContextMenuPanel.Open(mousePos, NpcTitle(actorId), _entries);"));
            Assert.That(adapter, Does.Contain("public bool TryHandleManualClick(Vector2 mousePos, bool append = false)"));
            Assert.That(camera, Does.Contain("_manualInput.TryHandleManualClick(mousePosition, append: shift)"));
        });
    }

    [Test]
    public void DestinationMarkerIsAHexagonOrientedLikeTheTiles()
    {
        var marker = Read("Assets", "HexLive", "UnityPresentation", "Rendering", "DestinationMarker.cs");
        Assert.Multiple(() =>
        {
            Assert.That(marker, Does.Not.Contain("PrimitiveType.Quad"), "No square any more");
            Assert.That(marker, Does.Contain("BuildHexRingMesh(HexOuterRadius, HexInnerRadius)"));
            Assert.That(marker, Does.Contain("var angle = Mathf.Deg2Rad * (90f + 60f * k);"),
                "Pointy along ±Z, the same orientation as PointInsideHex / the world tiles");
            Assert.That(marker, Does.Contain("new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle))"));
            Assert.That(marker, Does.Contain("transform.localScale = new Vector3(scale, 1f, scale);"));
        });

        var terms = Read("Assets", "Resources", "I2Languages.asset");
        var toast = Regex.Match(terms, @"- Term: '?toast\.order_rejected\.QueueHasAction'?\n(?:.*\n){3}      - (.*)\n      - (.*)\n");
        Assert.That(toast.Success, Is.True);
        Assert.That(Regex.IsMatch(toast.Groups[2].Value, "[А-Яа-яЁё]"), Is.True);
    }
}
