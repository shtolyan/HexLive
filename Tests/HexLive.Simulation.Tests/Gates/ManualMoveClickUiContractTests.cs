using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>Headless source gate for the §121 Unity click cadence.</summary>
public sealed class ManualMoveClickUiContractTests
{
    /// <summary>
    /// §121.11 (bug #294): один клик — всегда приказ идти, и он НЕ несёт темпа.
    /// Гейт стережёт обе половины: жеста двойного клика в вводе не осталось
    /// вовсе, а команда уходит без аргумента темпа — иначе клиент снова начал
    /// бы решать за симуляцию, каким шагом идти.
    /// </summary>
    [Test]
    public void GroundSingleClickAlwaysMovesAndCarriesNoPaceGesture()
    {
        var path = Path.Combine(
            new[] { RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
                "Input", "SimulationInputAdapter.cs" });
        var adapter = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Not.Contain("DoubleClickSeconds"));
            Assert.That(adapter, Does.Not.Contain("DoubleClickRadiusPixels"));
            Assert.That(adapter, Does.Not.Contain("ConsumeGroundDoubleClick"));
            Assert.That(adapter, Does.Not.Contain("ResetGroundClickCadence"));
            Assert.That(adapter, Does.Contain(
                "new MoveToCommand(new EntityId(ManualNpcId), point)"));
            Assert.That(adapter, Does.Contain(
                "new GroupMoveCommand(SelectedActors(), point)"));
        });
    }

    /// <summary>
    /// §121.11: темп — постоянная настройка ПЕРСОНАЖА, и решает её симуляция.
    /// Тумблер карточки только шлёт команду и рисует то, что приехало в
    /// снапшоте: кнопка, помнящая своё, показывала бы одно, пока колонистка
    /// бежит другое — ровно та же ловушка, что у тумблера 🧠/🎮.
    /// </summary>
    [Test]
    public void PaceIsACharacterSettingOwnedByTheSimulation()
    {
        var panel = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.cs"));
        var executor = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "Simulation", "Runtime",
            "Systems", "Ai", "ManualCommandExecutor.cs"));

        Assert.Multiple(() =>
        {
            // Тот же тумблер, что 🧠/🎮: одна вёрстка сегмента на оба, тот же
            // отступ 12 px, но в НИЖНЕМ левом углу карточки.
            Assert.That(panel, Does.Contain("private VisualElement BuildPaceToggle()"));
            Assert.That(panel, Does.Contain("BuildToggleSegment("));
            Assert.That(panel, Does.Contain("HexLive/UI/IdentityWalkIcon"));
            Assert.That(panel, Does.Contain("HexLive/UI/IdentityRunIcon"));
            Assert.That(panel, Does.Contain("button.style.left = 12f;"));
            Assert.That(panel, Does.Contain("button.style.bottom = 12f;"));
            Assert.That(panel, Does.Contain("new HexLive.Simulation.Runtime.SetRunByDefaultCommand("));
            Assert.That(panel, Does.Contain("_runByDefaultNow = npc.RunByDefault;"));
            Assert.That(panel, Does.Contain("Loc.Get(\"panel.pace.walk\")"));
            Assert.That(panel, Does.Contain("Loc.Get(\"panel.pace.run\")"));

            // Приказ без темпа берёт его у самой девушки, поэтому групповой
            // приказ ходит разным темпом у разных участниц.
            Assert.That(executor, Does.Contain(
                "private static bool PaceFor(NPCState npc, bool? requested) =>"));
            Assert.That(executor, Does.Contain("requested ?? npc.Mind.RunByDefault"));
            Assert.That(executor, Does.Contain(
                "var run = PaceFor(assignment.Npc, command.Run);"));
        });
    }

    [Test]
    public void PaceToggleHasBothLocalizedTermsAndItsIcons()
    {
        var loc = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(loc, Does.Contain("    - Term: panel.pace.walk\n"));
            Assert.That(loc, Does.Contain("    - Term: panel.pace.run\n"));
            Assert.That(loc, Does.Contain("    - Term: panel.pace.tooltip\n"));
            foreach (var icon in new[] { "IdentityWalkIcon", "IdentityRunIcon" })
            {
                var png = Path.Combine(
                    RepoPaths.Root, "Assets", "Resources", "HexLive", "UI", icon + ".png");
                Assert.That(File.Exists(png), Is.True, $"{icon}.png is missing");
                Assert.That(File.Exists(png + ".meta"), Is.True,
                    $"{icon}.png.meta is missing — Unity would invent a new guid");
            }
        });
    }

    [Test]
    public void ContextMenuOwnsTheWholePointerSequenceWithoutWorldClickThrough()
    {
        var uiRoot = Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation");
        var menu = File.ReadAllText(Path.Combine(uiRoot, "UI", "ContextMenuPanel.cs"));
        var camera = File.ReadAllText(Path.Combine(uiRoot, "Input", "RtsCameraController.cs"));
        var adapter = File.ReadAllText(Path.Combine(uiRoot, "Input", "SimulationInputAdapter.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(menu, Does.Contain("public static bool BlocksWorldPointer"));
            Assert.That(menu, Does.Contain("_worldPointerSuppressed = true;"),
                "Closing an item on MouseDown must keep the world blocked through release.");
            Assert.That(menu, Does.Contain("Time.frameCount > _worldPointerReleaseFrame"),
                "The suppression latch must survive the physical release frame.");
            Assert.That(camera, Does.Contain("UI.ContextMenuPanel.BlocksWorldPointer"));
            Assert.That(camera, Does.Contain(
                "UI.ContextMenuPanel.IsOpen && !UI.ContextMenuPanel.PointerOverPanel"),
                "A click outside an open menu must close only the menu.");
            Assert.That(adapter, Does.Contain("ContextMenuPanel.BlocksWorldPointer"));
        });
    }

    [Test]
    public void LyingWardCanBeTargetedThroughStationButNotThroughPortableItem()
    {
        var adapter = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Input",
            "SimulationInputAdapter.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("!IsPortablePickTarget(objectHit)"));
            Assert.That(adapter, Does.Contain("IsLyingPerson(lyingPerson)"));
            Assert.That(adapter, Does.Contain("interaction.Type == InteractionType.PickUp"));
            Assert.That(adapter, Does.Contain("objectHit = null;"));
            Assert.That(adapter, Does.Contain("Add(\"menu.aid.hydrate\", AidKind.Hydrate)"));
            Assert.That(adapter, Does.Contain("Add(\"menu.aid.feed\", AidKind.Feed)"));
        });
    }
}
