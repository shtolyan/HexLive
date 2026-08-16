using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>Headless source gate for the §128 Unity UI that dotnet cannot instantiate.</summary>
public sealed class LootTransferUiContractTests
{
    private static string Presentation(params string[] parts) => Path.Combine(
        new[] { RepoPaths.Root, "Assets", "HexLive", "UnityPresentation" }
            .Concat(parts).ToArray());

    [Test]
    public void ExchangeIsTwoEqualUnscrolledWindowsWithAuthoritativeDragDrops()
    {
        var panel = File.ReadAllText(Presentation("UI", "LootTransferPanel.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("loot-own-window"));
            Assert.That(panel, Does.Contain("loot-target-window"));
            Assert.That(panel, Does.Contain("pane.style.width = Length.Percent(50f)"));
            Assert.That(panel, Does.Not.Contain("ScrollView"));
            Assert.That(panel, Does.Not.Contain("Scroller"));
            Assert.That(panel, Does.Contain("private const float DragThreshold = 6f"));
            Assert.That(panel, Does.Contain("using HexLive.Simulation.Content;"),
                "ItemInfo.Slug belongs to the simulation content namespace.");
            Assert.That(panel, Does.Contain("new TransferInventoryCommand("));
            Assert.That(panel, Does.Contain("slot.SourceIndex"));
            Assert.That(panel, Does.Contain("slot.StackCount"));
            Assert.That(panel, Does.Contain("container.OwnerSourceIndex"));
            Assert.That(panel, Does.Contain("InventoryItemSource.Worn"));
            Assert.That(panel, Does.Not.Contain("ShowItemDetail"),
                "The exchange has no hover or click popup competing with drag-and-drop.");
        });
    }

    [Test]
    // §128 r2 (#164): «лежит — можно обыскать», включая мёртвую и спящую. Раньше
    // контракт закреплял ровно обратное («только живая в отключке»), и это была
    // не защита, а зафиксированный симптом.
    public void ContextMenuOffersLootForAnyLyingPersonOfEitherFaction()
    {
        var adapter = File.ReadAllText(Presentation("Input", "SimulationInputAdapter.cs"));
        var bootstrap = File.ReadAllText(Presentation("Bootstrap", "PrototypeRuntimeBootstrap.cs"));
        var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("if (lying && carrier?.CarriedNpcId != npcId)"));
            Assert.That(adapter, Does.Contain("Loc.Get(\"menu.loot_person\")"));
            Assert.That(adapter, Does.Contain("LootTransferPanel.Open(carrier!.Id.Value, npcId)"));
            Assert.That(adapter, Does.Not.Contain("AreHostile(carrier"),
                "Allied and foreign lying people must share the same interaction.");
            // Тело живёт в отдельном списке снапшота — без этой ветки панель
            // молча не открылась бы над мёртвой.
            Assert.That(File.ReadAllText(Presentation("UI", "LootTransferPanel.cs")),
                Does.Contain("snapshot.Corpses"));
            Assert.That(bootstrap, Does.Contain("AddComponent<LootTransferPanel>()"));
            Assert.That(camera, Does.Contain("UI.LootTransferPanel.IsOpen"));
            Assert.That(camera, Does.Contain("UI.LootTransferPanel.Close()"));
            Assert.That(localization, Does.Contain("Term: 'menu.loot_person'"));
            Assert.That(localization, Does.Contain("Term: 'loot.title'"));
            Assert.That(localization, Does.Contain("'Обобрать'"));
            Assert.That(localization, Does.Contain("'Обмен вещами'"));
        });
    }
}

}
