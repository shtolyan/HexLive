using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>Headless source gate for the §128 Unity UI that dotnet cannot instantiate.</summary>
public sealed class LootTransferUiContractTests
{
    private static string ReadLocalization() => Regex.Replace(
        File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset")),
        @"\\u([0-9a-fA-F]{4})",
        match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());

    private static string Presentation(params string[] parts) => Path.Combine(
        new[] { RepoPaths.Root, "Assets", "HexLive", "UnityPresentation" }
            .Concat(parts).ToArray());

    [Test]
    public void ExchangeIsTwoEqualScrollingWindowsWithAuthoritativeDragDrops()
    {
        var panel = File.ReadAllText(Presentation("UI", "LootTransferPanel.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("loot-own-window"));
            Assert.That(panel, Does.Contain("loot-target-window"));
            Assert.That(panel, Does.Contain("pane.style.width = Length.Percent(50f)"));
            // §128.1: панель СКРОЛЛИТСЯ со своим скроллбаром. Раньше здесь стояло
            // ровно обратное требование, и лестница плотности жала ячейки до 28
            // единиц, а остаток всё равно срезала рамка с overflow: hidden.
            Assert.That(panel, Does.Contain("new ScrollView("));
            Assert.That(panel, Does.Contain("verticalScrollerVisibility"));
            Assert.That(panel, Does.Contain("ScrollerVisibility.Auto"));
            Assert.That(panel, Does.Contain("private const int MaxDensityTier = 1"),
                "Лестница усадки укорочена: дальше вещи не мельчают, а скроллятся.");
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
    // §128.1a: жест держится захватом указателя, а не тем, куда всплывёт событие.
    // Прежняя версия слушала PointerUp на панели-приёмнике и брала приёмник из
    // evt.target — и разваливалась от пересборки раскладки посреди жеста.
    public void DragIsHeldByPointerCaptureAndResolvedGeometrically()
    {
        var panel = File.ReadAllText(Presentation("UI", "LootTransferPanel.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("_frame.CapturePointer("),
                "Указатель забирает РАМКА: она переживает пересборку содержимого.");
            Assert.That(panel, Does.Contain("ReleasePointer("));
            Assert.That(panel, Does.Contain("PointerCaptureOutEvent"),
                "Потеря захвата обязана гасить жест, иначе он зависнет навсегда.");
            Assert.That(panel, Does.Contain("worldBound.Contains("),
                "Приёмник определяется геометрией точки отпускания, а не evt.target.");
            Assert.That(panel, Does.Contain("_rebuildDeferred"),
                "Пересборка раскладки во время жеста откладывается.");
            Assert.That(panel, Does.Contain("_dragGhost"),
                "За курсором едет призрак вещи, иначе жест невидим.");
        });
    }

    [Test]
    // §128.1b (#344): loot, gift и контейнеры сходятся в DropOn, поэтому
    // выбор количества обязан стоять до развилки двух авторитетных команд.
    public void StackTransfersAlwaysAskForAnExactQuantity()
    {
        var panel = File.ReadAllText(Presentation("UI", "LootTransferPanel.cs"));
        var localization = ReadLocalization();

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("InventoryState.IsStackable(_drag.DefinitionId)"));
            Assert.That(panel, Does.Contain("ShowQuantityPicker(destinationId, _drag)"));
            Assert.That(panel, Does.Contain("new SliderInt"));
            Assert.That(panel, Does.Contain("new IntegerField"));
            Assert.That(panel, Does.Contain("KeyCode.KeypadEnter"));
            Assert.That(panel, Does.Contain("ExecuteTransfer(destinationId, item, count)"));
            Assert.That(panel, Does.Contain("new TransferInventoryCommand("));
            Assert.That(panel, Does.Contain("new TransferContainerCommand("));
            Assert.That(localization, Does.Contain("Term: loot.quantity_title"));
            Assert.That(localization, Does.Contain("Сколько {0} переместить?"));
        });
    }

    [Test]
    // §128.4: правило двойного клика и его порог живут в ОДНОМ файле, и оба
    // инвентаря спрашивают именно его.
    public void DoubleClickRuleIsSharedBetweenExchangeAndColonistInventory()
    {
        var shared = File.ReadAllText(Presentation("UI", "InventoryQuickAction.cs"));
        var panel = File.ReadAllText(Presentation("UI", "LootTransferPanel.cs"));
        var character = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var localization = ReadLocalization();

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain("public const float DoubleClickSeconds = 0.35f"));
            Assert.That(shared, Does.Contain("InventoryQuickAction.TakeFromOther"));
            Assert.That(shared, Does.Contain("InventoryQuickAction.TakeOff"));
            Assert.That(shared, Does.Contain("InventoryQuickAction.Wear"));
            // Своя сторона НИКОГДА не отдаёт вещь двойным кликом: отдача — только
            // перетаскиванием, иначе случайный двойной клик ссыпет гардероб в труп.
            Assert.That(shared, Does.Contain("if (!ownSide) return InventoryQuickAction.TakeFromOther;"));
            Assert.That(shared, Does.Contain("definition?.Layer is not null"),
                "«Можно надеть» — тот же признак, что принимает PlayerInventoryMath.");

            Assert.That(panel, Does.Contain("InventoryQuickActions.Resolve("));
            Assert.That(panel, Does.Contain("DoubleClickWatch _doubleClick"));
            Assert.That(panel, Does.Contain("ManageInventoryCommand("),
                "Надеть/снять в окне обмена идёт штатным приказом §123.");
            Assert.That(panel, Does.Contain("InventoryTransferDirection.TakeAndWear"),
                "Носимая вещь забирается и надевается одной авторитетной командой.");
            Assert.That(panel, Does.Contain("ExecuteTransfer(_looterId, item, 1, wear: true)"));
            Assert.That(panel, Does.Not.Contain("_autoWearDefinitionId"),
                "Нет ожидания свободной ячейки и выбора экземпляра только по definition id.");

            Assert.That(character, Does.Contain("InventoryQuickActions.Resolve("));
            Assert.That(character, Does.Contain("TryInventoryQuickAction("));

            Assert.That(localization, Does.Contain("Term: loot.equipping"));
            Assert.That(localization, Does.Contain("Term: loot.no_quick_action"));
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
        var localization = ReadLocalization();

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
            Assert.That(localization, Does.Contain("Term: menu.loot_person"));
            Assert.That(localization, Does.Contain("Term: loot.title"));
            Assert.That(localization, Does.Contain("Обобрать"));
            Assert.That(localization, Does.Contain("Обмен вещами"));
        });
    }

    [Test]
    // §153.1: «Подарить» — тот же файл окна и тот же приказ, но одностороннее
    // направление и живая цель. Проверяется именно то, что ломается молча:
    // пункт меню, отдельный вход в панель, режим, гашение обратного жеста и
    // ЧЕТЫРЕ строки локализации. Без последних окно открывается с сырыми
    // ключами вместо текста, и никакой C# этого не заметит.
    public void ContextMenuOffersGiftForAnAwakePersonAndThePanelGoesOneWay()
    {
        var adapter = File.ReadAllText(Presentation("Input", "SimulationInputAdapter.cs"));
        var panel = File.ReadAllText(Presentation("UI", "LootTransferPanel.cs"));
        var speech = File.ReadAllText(Presentation("UI", "SpeechCatalog.cs"));
        var history = File.ReadAllText(Presentation("History", "GameHistoryFormatter.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("Loc.Get(\"menu.gift_person\")"));
            Assert.That(adapter, Does.Contain("LootTransferPanel.OpenGift(carrier!.Id.Value, npcId)"));

            Assert.That(panel, Does.Contain("public static void OpenGift("),
                "Подарок входит в то же окно своим входом, а не флагом снаружи.");
            Assert.That(panel, Does.Contain("InventoryTransferDirection.Take"));
            Assert.That(panel, Does.Contain("Loc.Get(\"gift.take_forbidden\")"),
                "Обратный жест обязан быть ОТКАЗОМ с текстом, а не молчаливым no-op.");
            Assert.That(panel, Does.Contain("_statusHeld"),
                "Отказ обязан пережить конец жеста, иначе игрок его не прочитает.");
            Assert.That(panel, Does.Contain("IsGiftable("),
                "У подарка своё условие цели: IsLootable закрыл бы окно сразу.");

            // §153.3: ступень реакции обязана быть слышна — иначе «то, что надо»
            // и «зачем ты мне это» выглядят над головой одинаково.
            Assert.That(speech, Does.Contain("[\"GiftReceived:Loved\"]"));
            Assert.That(speech, Does.Contain("[\"GiftReceived:Disliked\"]"));
            Assert.That(history, Does.Contain("\"GiftGiven\" => F(\"history.GiftGiven\""));

            foreach (var term in new[]
                     {
                         "menu.gift_person", "gift.title", "gift.drag_hint",
                         "gift.take_forbidden", "history.GiftGiven",
                         "history.detail.gift.Loved", "history.detail.gift.Disliked"
                     })
            {
                Assert.That(localization, Does.Contain("- Term: " + term + "\n"),
                    $"Строка {term} не заведена в I2 — окно покажет сырой ключ.");
            }
        });
    }

    [Test]
    public void EmptyRemainsAndWardrobeOpenTheTwoSidedContainerPanel()
    {
        var adapter = File.ReadAllText(Presentation("Input", "SimulationInputAdapter.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("definition.HasTag(ObjectTags.Remains)"));
            Assert.That(adapter, Does.Contain("definition.HasTag(ObjectTags.Wardrobe)"));
            Assert.That(adapter, Does.Contain("if (isContainer && interaction.Type == InteractionType.Loot) continue"));
            Assert.That(adapter, Does.Contain("LootTransferPanel.OpenContainer(actorId, containerId)"));
        });
    }
}

}
