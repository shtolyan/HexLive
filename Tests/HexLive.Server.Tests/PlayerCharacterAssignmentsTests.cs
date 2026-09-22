using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Wire;
using HexLive.Simulation.Agents;
using System.Threading.Tasks;
using HexLive.Server;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

/// <summary>§149: assignment is durable ownership; ControlLeases only protect
/// the currently active command stream.</summary>
[NonParallelizable]
public sealed class PlayerCharacterAssignmentsTests
{
    private string _directory = null!;
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "hexlive-player-assignments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "hexlive-players.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void TwoAuthoredCharactersStayAssignedTogetherAcrossReload()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, false);
        var roster = new[] { 1, 11, 901, 902 };
        var priority = new HashSet<int> { 901, 902 };
        Assert.That(assignments.Reconcile(Id(1), roster, roster, 2, priority),
            Is.EqualTo(new[] { 901, 902 }));
        assignments = PlayerCharacterAssignments.Load(_path, true);
        Assert.That(assignments.Reconcile(Id(1), roster, roster, 2, priority),
            Is.EqualTo(new[] { 901, 902 }));
        Assert.That(assignments.Reconcile(Id(2), roster, roster, 2, priority),
            Is.EqualTo(new[] { 1, 11 }), "Another player cannot take either authored body.");
    }

    [Test]
    public void AuthoredPriorityDoesNotEvictRetainedDyingSquadMemberAfterReload()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, false);
        var roster = new[] { 21, 901, 902 };
        assignments.Reconcile(Id(1), roster, roster, 2, new HashSet<int> { 901, 902 });
        assignments = PlayerCharacterAssignments.Load(_path, true);
        for (var reconnect = 0; reconnect < 3; reconnect++)
            Assert.That(assignments.Reconcile(Id(1), roster, new[] { 21, 901 }, 2,
                new HashSet<int> { 901 }), Is.EqualTo(new[] { 901, 902 }));
        Assert.That(assignments.Reconcile(Id(2), roster, roster, 2,
            new HashSet<int> { 901, 902 }), Is.EqualTo(new[] { 21 }));
    }

    [Test]
    public void NewlyAvailablePresetOnlyFillsVacantSlots()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, false);
        assignments.Reconcile(Id(1), new[] { 21 }, new[] { 21 }, 2);
        Assert.That(assignments.Reconcile(Id(1), new[] { 21, 901, 902 },
            new[] { 21, 901, 902 }, 2, new HashSet<int> { 901, 902 }),
            Is.EqualTo(new[] { 21, 901 }));
        Assert.That(assignments.Reconcile(Id(1), new[] { 901, 902 },
            new[] { 901, 902 }, 2, new HashSet<int> { 901, 902 }),
            Is.EqualTo(new[] { 901, 902 }), "Only actual removal frees a slot.");
    }

    [Test]
    public void FirstFreeIsStableAcrossReconnectAndDistinctForAnotherPlayer()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        var firstPlayer = Id(1);
        var secondPlayer = Id(2);

        Assert.Multiple(() =>
        {
            Assert.That(assignments.Reconcile(firstPlayer, new[] { 1, 2 }, new[] { 1, 2 }),
                Is.EqualTo(new[] { 1 }));
            Assert.That(assignments.Reconcile(secondPlayer, new[] { 1, 2 }, new[] { 1, 2 }),
                Is.EqualTo(new[] { 2 }));
            Assert.That(assignments.Reconcile(firstPlayer, new[] { 1, 2 }, new[] { 1, 2 }),
                Is.EqualTo(new[] { 1 }),
                "Reconnect must restore the old character, not take the next free one.");
        });
    }

    [Test]
    public void SimultaneousFirstConnectionsNeverReceiveTheSameCharacter()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);

        var first = Task.Run(() => assignments.Reconcile(
            Id(21), new[] { 1, 2 }, new[] { 1, 2 }));
        var second = Task.Run(() => assignments.Reconcile(
            Id(22), new[] { 1, 2 }, new[] { 1, 2 }));
        Task.WaitAll(first, second);

        Assert.That(new[] { first.Result[0], second.Result[0] },
            Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void DeadCharacterIsReplacedByTheFirstFreeOneOnNextConnect()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        var returningPlayer = Id(1);
        var otherPlayer = Id(2);
        assignments.Reconcile(returningPlayer, new[] { 1, 2, 3 }, new[] { 1, 2, 3 });
        assignments.Reconcile(otherPlayer, new[] { 1, 2, 3 }, new[] { 1, 2, 3 });

        var replacement = assignments.Reconcile(
            returningPlayer,
            retainableNpcIds: new[] { 2, 3 },
            assignableNpcIds: new[] { 2, 3 });

        Assert.That(replacement, Is.EqualTo(new[] { 3 }),
            "NPC1 died, NPC2 is reserved by another player, so NPC3 is first free.");
    }

    [Test]
    public void AssignmentSurvivesServerRestart()
    {
        var player = Id(7);
        var firstProcess = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        firstProcess.Reconcile(player, new[] { 11, 12 }, new[] { 11, 12 });

        var restarted = PlayerCharacterAssignments.Load(_path, continueExistingWorld: true);

        Assert.That(restarted.Reconcile(player, new[] { 11, 12 }, new[] { 11, 12 }),
            Is.EqualTo(new[] { 11 }));
    }

    [Test]
    public void FreshWorldClearsAStaleSidecar()
    {
        var player = Id(9);
        var oldWorld = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        oldWorld.Reconcile(player, new[] { 41 }, new[] { 41 });

        var freshWorld = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);

        Assert.That(freshWorld.Reconcile(player, new[] { 51 }, new[] { 51 }),
            Is.EqualTo(new[] { 51 }));
    }

    [Test]
    public void ListContractSupportsTwoCharactersAndCurrentSingleCharacterLimit()
    {
        var twoPerCamp = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "big-island.json"), continueExistingWorld: false);
        var onePerCamp = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-island.json"), continueExistingWorld: false);

        Assert.Multiple(() =>
        {
            Assert.That(twoPerCamp.Reconcile(
                    Id(1),
                    retainableNpcIds: new[] { 1, 2 },
                    assignableNpcIds: new[] { 1, 2 },
                    characterLimit: 2),
                Is.EqualTo(new[] { 1, 2 }),
                "BigIsland's player camp has NPC1/NPC2; the future squad limit owns both.");
            Assert.That(onePerCamp.Reconcile(
                    Id(2),
                    retainableNpcIds: new[] { 1 },
                    assignableNpcIds: new[] { 1 },
                    characterLimit: 1),
                Is.EqualTo(new[] { 1 }),
                "HugeIsland/Maniac start with one NPC in the player camp.");
        });
    }

    [Test]
    public void NonCanonicalClientIdCannotBecomeAPersistentPlayer()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PlayerCharacterAssignments.TryNormalizePlayerId("not-a-guid", out _), Is.False);
            Assert.That(PlayerCharacterAssignments.TryNormalizePlayerId(Id(5), out var canonical), Is.True);
            Assert.That(canonical, Is.EqualTo(Id(5)));
        });
    }

    [Test]
    public void ServerRejectsAnUnassignedActorAndTheWholeMixedGroup()
    {
        var assigned = new HashSet<int> { 1, 2 };
        var item = new InventoryItemRef(
            InventoryItemSource.Carried, 0, "tool.hammer");

        Assert.Multiple(() =>
        {
            Assert.That(PlayerCommandAssignment.Allows(
                new SetManualControlCommand(new EntityId(1), true), assigned), Is.True);
            Assert.That(PlayerCommandAssignment.Allows(
                new SetManualControlCommand(new EntityId(3), true), assigned), Is.False);
            Assert.That(PlayerCommandAssignment.Allows(
                new SetOutfitLockCommand(new EntityId(1), true), assigned), Is.True);
            Assert.That(PlayerCommandAssignment.Allows(
                new SetOutfitLockCommand(new EntityId(3), true), assigned), Is.False);
            Assert.That(PlayerCommandAssignment.Allows(
                new ManageInventoryCommand(new EntityId(1), item, InventoryAction.Drop),
                assigned), Is.True);
            Assert.That(PlayerCommandAssignment.Allows(
                new TransferInventoryCommand(
                    new EntityId(3), new EntityId(2), item, 1,
                    InventoryTransferDirection.Take), assigned), Is.False);
            Assert.That(PlayerCommandAssignment.Allows(
                new TransferContainerCommand(
                    new EntityId(1), new ObjectId(7), 0, "tool.hammer", 1,
                    InventoryTransferDirection.Take), assigned), Is.True);
            Assert.That(PlayerCommandAssignment.Allows(
                new SetGroupManualControlCommand(
                    new[] { new EntityId(1), new EntityId(3) }, true), assigned), Is.False,
                "A mixed group must be rejected atomically, not partially executed.");
        });
    }

    [Test]
    public void AssignedPlayerCanManageInventoryWithoutTakingManualControl()
    {
        var assigned = new HashSet<int> { 1 };
        var leases = new ControlLeases();
        var item = new InventoryItemRef(
            InventoryItemSource.Carried, 0, "tool.hammer");
        ISimulationCommand[] inventoryCommands =
        {
            new SetOutfitLockCommand(new EntityId(1), true),
            new ManageInventoryCommand(new EntityId(1), item, InventoryAction.Drop),
            new TransferInventoryCommand(
                new EntityId(1), new EntityId(2), item, 1,
                InventoryTransferDirection.Take),
            new TransferContainerCommand(
                new EntityId(1), new ObjectId(7), 0, "tool.hammer", 1,
                InventoryTransferDirection.Take),
        };

        foreach (var command in inventoryCommands)
        {
            Assert.That(PlayerCommandAuthorization.TryAuthorize(
                    command, assigned, leases, "ws:player", out var refusal),
                Is.True, $"{command.GetType().Name}: {refusal}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(leases.Snapshot(), Is.Empty,
                "Inventory management must neither acquire nor renew manual control.");
            Assert.That(PlayerCommandAuthorization.TryAuthorize(
                    new ManageInventoryCommand(new EntityId(2), item, InventoryAction.Drop),
                    assigned, leases, "ws:player", out var unassignedRefusal),
                Is.False);
            Assert.That(unassignedRefusal, Is.EqualTo("NotAssigned"));
            Assert.That(PlayerCommandAuthorization.TryAuthorize(
                    new MoveToCommand(new EntityId(1), new Float2(1f, 2f)),
                    assigned, leases, "ws:player", out var movementRefusal),
                Is.False);
            Assert.That(movementRefusal, Is.EqualTo("NoLease"),
                "Only inventory/garment actions bypass the manual-control lease.");
        });
    }

    // §149.2: в HugeIsland/Maniac в каждом из шести лагерей ОДНА девушка.
    // Пока выдавалась только Faction.Colony, свой лагерь исчерпывался первым
    // подключившимся, и второй игрок молча оставался ни с чем — сетевая игра
    // вдвоём была невозможна by design. Теперь второму достаётся девушка
    // соседнего лагеря.
    [Test]
    public void SecondPlayerOnHugeIslandGetsAGirlFromANeighbouringCamp()
    {
        using var hugeIsland = CreateHost(GameMode.HugeIsland, "huge-two-players.sav");
        var assignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-two-players.json"), continueExistingWorld: false);

        var mac = assignments.Reconcile(hugeIsland, Id(21), characterLimit: 1);
        var windows = assignments.Reconcile(hugeIsland, Id(22), characterLimit: 1);

        Assert.Multiple(() =>
        {
            Assert.That(mac, Is.EqualTo(new[] { 1 }),
                "Первый игрок получает первую свободную по возрастанию id.");
            Assert.That(windows, Has.Count.EqualTo(1),
                "Второй игрок обязан получить персонажа: девушек шесть, лагерей шесть.");
            Assert.That(windows[0], Is.Not.EqualTo(mac[0]),
                "Двум игрокам никогда не выдаётся одна и та же девушка.");
        });
    }

    [Test]
    public void RealModeRostersMatchTheOneAndTwoCharacterArchitecture()
    {
        using var bigIsland = CreateHost(GameMode.BigIsland, "big.sav");
        using var hugeIsland = CreateHost(GameMode.HugeIsland, "huge.sav");
        var bigAssignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "big-players.json"), continueExistingWorld: false);
        var hugeAssignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-players.json"), continueExistingWorld: false);

        Assert.Multiple(() =>
        {
            Assert.That(bigAssignments.Reconcile(bigIsland, Id(11), characterLimit: 2),
                Is.EqualTo(new[] { 1, 2 }));
            Assert.That(hugeAssignments.Reconcile(hugeIsland, Id(12), characterLimit: 1),
                Is.EqualTo(new[] { 1 }));
        });
    }

    [Test]
    public void LateWorldSwapNotificationDoesNotEraseAnEarlyNewWorldAssignment()
    {
        using var host = CreateHost(GameMode.BigIsland, "generation.sav");
        var assignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "generation-players.json"),
            continueExistingWorld: false);

        Assert.That(assignments.Reconcile(
            host, Id(31), worldGeneration: 0), Is.EqualTo(new[] { 1 }));

        // A viewer can enter generation 1 immediately after the supervisor
        // releases its swap lock, before Program receives WorldSwapped.
        Assert.That(assignments.Reconcile(
            host, Id(32), worldGeneration: 1), Is.EqualTo(new[] { 1 }));
        assignments.SwitchWorld(1);

        Assert.Multiple(() =>
        {
            Assert.That(assignments.Reconcile(
                host, Id(31), worldGeneration: 0), Is.Empty,
                "A late old-world viewer must not write its roster back.");
            Assert.That(assignments.Reconcile(
                host, Id(33), worldGeneration: 1), Is.EqualTo(new[] { 2 }),
                "The late notification must not free NPC1 from the early viewer.");
        });
    }

    // ⭐ §149.4: выдача — половина дела. За серверной границей прав стоит
    // вторая, внутри симуляции (§123), и она знала одну «игрокову» фракцию —
    // Faction.Colony. Поэтому выданная девушка СОСЕДНЕГО лагеря проходила
    // сервер и молча отбивалась симуляцией с «NotOwned»: персонаж выдан,
    // тумблер не переключается, приказы не доходят. Тест меряет именно акт —
    // взяла ли она ручное управление, — а не факт назначения.
    [Test]
    public void AssignedGirlFromANeighbouringCampCanBeTakenUnderManualControl()
    {
        using var host = CreateHost(GameMode.HugeIsland, "huge-manual.sav");
        var assignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-manual.json"), continueExistingWorld: false);

        var first = assignments.Reconcile(host, Id(41), characterLimit: 1);
        var second = assignments.Reconcile(host, Id(42), characterLimit: 1);
        Assert.That(second, Is.Not.Empty, "Второй игрок обязан получить персонажа.");

        var neighbour = second[0];
        var faction = host.Read(world =>
            world.Entities.Npcs[new EntityId(neighbour)].Faction);
        var admission = host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(neighbour), true));
        var manual = host.Read(world =>
            world.Entities.Npcs[new EntityId(neighbour)].Mind.ManualControl);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new[] { 1 }));
            Assert.That(faction, Is.Not.EqualTo(HexLive.Simulation.Agents.Faction.Colony),
                "Смысл теста — именно ЧУЖОЙ лагерь; иначе он проходит и на старом гейте.");
            Assert.That(admission.Accepted, Is.True,
                "Симуляция обязана признать выданную девушку игроковой.");
            Assert.That(manual, Is.True, "Тумблер должен реально переключиться.");
        });
    }

    // §149.4: право приходит из реестра назначений и не должно течь на всех
    // подряд — иначе одиночная игра получила бы шесть лагерей в управление.
    [Test]
    public void AnUnassignedGirlFromANeighbouringCampStaysUnderAi()
    {
        using var host = CreateHost(GameMode.HugeIsland, "huge-unassigned.sav");
        var assignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-unassigned.json"), continueExistingWorld: false);
        assignments.Reconcile(host, Id(43), characterLimit: 1);

        var stranger = host.Read(world =>
        {
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.Faction != HexLive.Simulation.Agents.Faction.Colony &&
                    !world.PlayerControlledNpcs.Contains(npc.Id.Value) &&
                    FactionRelations.IsGirlCamp(npc.Faction))
                {
                    return npc.Id.Value;
                }
            }

            return -1;
        });

        Assert.That(stranger, Is.GreaterThan(0), "В HugeIsland лагерей шесть.");
        var admission = host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(stranger), true));
        Assert.That(admission.Accepted, Is.False,
            "Невыданная девушка чужого лагеря остаётся под ИИ.");
    }

    // §149.4 / #231: крафт-модель обязана считать «своей» девушку ЛЮБОГО
    // выданного лагеря. CraftingOptions гейтился на буквальную Faction.Colony,
    // и у соседки при живой лизе и ManualControl=true каждый рецепт приходил
    // NotManual — «включите ручное управление» при включённом ручном.
    [Test]
    public void CraftingReadModelAcceptsAnAssignedNeighbouringCampGirl()
    {
        using var host = CreateHost(GameMode.HugeIsland, "huge-crafting.sav");
        var assignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-crafting.json"), continueExistingWorld: false);
        assignments.Reconcile(host, Id(41), characterLimit: 1);
        var second = assignments.Reconcile(host, Id(42), characterLimit: 1);
        Assert.That(second, Is.Not.Empty, "Второй игрок обязан получить персонажа.");

        var neighbour = second[0];
        var faction = host.Read(world =>
            world.Entities.Npcs[new EntityId(neighbour)].Faction);
        var admission = host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(neighbour), true));

        var snapshot = host.Read(world => PlayerCraftingOptions.Capture(
            world, new[] { neighbour }));

        Assert.Multiple(() =>
        {
            Assert.That(faction, Is.Not.EqualTo(HexLive.Simulation.Agents.Faction.Colony),
                "Смысл теста — именно ЧУЖОЙ лагерь; иначе он проходит и на старом гейте.");
            Assert.That(admission.Accepted, Is.True);
            Assert.That(snapshot.Npcs.Count, Is.EqualTo(1));
            Assert.That(snapshot.Npcs[0].Options, Is.Not.Empty);
            Assert.That(snapshot.Npcs[0].Options,
                Has.None.Matches<CraftRecipeOption>(option =>
                    option.BlockReason == CraftBlockReason.NotManual),
                "Выданная соседка под ручным управлением обязана мочь крафтить.");
        });
    }

    // §149 r3 / #236: контекстное меню обязано отвечать то же, что приём
    // приказа. Пока «своя» была буквальной Faction.Colony, у игрока лагеря
    // Colony2..Colony6 меню переворачивалось: девушка ПЕРВОГО лагеря попадала
    // в ветку своей (охота §56, «взять на руки» стоящей), хотя приём считает её
    // чужой и отбивает `PersonNotAvailable`/`NotAlly`; а объединение лагерей
    // §146.12 не показывалось вовсе — оно требовало carrier.Faction == Colony.
    [Test]
    public void ManualMenuTargetsMatchAdmissionForAnAssignedNeighbouringCampGirl()
    {
        using var host = CreateHost(GameMode.HugeIsland, "huge-menu.sav");
        var assignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-menu.json"), continueExistingWorld: false);
        assignments.Reconcile(host, Id(44), characterLimit: 1);
        var second = assignments.Reconcile(host, Id(45), characterLimit: 1);
        Assert.That(second, Is.Not.Empty, "Второй игрок обязан получить персонажа.");

        var neighbour = second[0];
        var manual = host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(neighbour), true));

        var (actorFaction, targetFaction, allies, targetLying) = host.Read(world =>
        {
            var actor = world.Entities.Npcs[new EntityId(neighbour)];
            var target = world.Entities.Npcs[new EntityId(1)];
            return (actor.Faction, target.Faction,
                FactionRelations.AreAllies(actor, target),
                target.IsLyingDown(world.Tick));
        });

        // Тот самый приказ, который старое меню предлагало на девушке первого
        // лагеря: «взять на руки» стоящую чужую.
        var carry = host.SubmitManualCommand(
            new CarryPersonCommand(new EntityId(neighbour), new EntityId(1)));

        Assert.Multiple(() =>
        {
            Assert.That(manual.Accepted, Is.True);
            Assert.That(actorFaction, Is.Not.EqualTo(HexLive.Simulation.Agents.Faction.Colony),
                "Смысл теста — именно ЧУЖОЙ лагерь; иначе он проходит и на старом гейте.");
            Assert.That(targetFaction, Is.EqualTo(HexLive.Simulation.Agents.Faction.Colony),
                "Цель — девушка ПЕРВОГО лагеря: её и путал буквальный гейт.");
            Assert.That(targetLying, Is.False, "Стоящая: лежачую носят любую.");
            Assert.That(allies, Is.False, "Разные лагеря — приём считает её чужой.");
            Assert.That(carry.Accepted, Is.False,
                "Приём отбивает «взять на руки» стоящую чужую (PersonNotAvailable).");

            Assert.That(ManualMenuTargets.SameSide(actorFaction, targetFaction), Is.False,
                "Меню обязано молчать там, где приём отбивает: старый гейт " +
                "target.Faction == Faction.Colony предлагал охоту §56 и носилки.");
            Assert.That(ManualMenuTargets.SameSide(actorFaction, actorFaction), Is.True,
                "Соседка по СВОЕМУ лагерю обязана остаться своей.");
            Assert.That(ManualMenuTargets.NeighbourCamp(actorFaction, targetFaction), Is.True,
                "§146.12: старый гейт требовал carrier.Faction == Faction.Colony, " +
                "и у игрока лагеря Colony2..Colony6 объединения не было в меню вовсе.");
            Assert.That(ManualMenuTargets.NeighbourCamp(actorFaction, actorFaction), Is.False,
                "Свой лагерь с самим собой не объединяют (AlreadySameCamp).");
        });
    }

    [Test]
    public void ServerCraftingReadModelContainsOnlyAssignedManualCharacters()
    {
        using var host = CreateHost(GameMode.BigIsland, "crafting.sav");
        var admission = host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(1), true));

        var snapshot = host.Read(world => PlayerCraftingOptions.Capture(
            world, new[] { 1, 1, 999 }));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Accepted, Is.True);
            Assert.That(snapshot.Npcs.Count, Is.EqualTo(1));
            Assert.That(snapshot.Npcs[0].NpcId, Is.EqualTo(1));
            Assert.That(snapshot.Npcs[0].Options, Is.Not.Empty);
            Assert.That(snapshot.Npcs[0].Options,
                Has.None.Matches<CraftRecipeOption>(option =>
                    option.BlockReason == CraftBlockReason.NotManual),
                "The server read model must observe the same manual-control state as admission.");
        });
    }

    [Test]
    public void IntroductionSurvivesReconnectAndRestartUntilAcknowledged()
    {
        var store = PlayerCharacterAssignments.Load(_path, false);
        var names = new Dictionary<int, string> { [1] = "masha" };
        store.Reconcile(Id(1), new[] { 1 }, new[] { 1 }, names: names);
        store = PlayerCharacterAssignments.Load(_path, true);
        store.Reconcile(Id(1), new[] { 1 }, new[] { 1 }, names: names);
        var notice = store.PendingNotices(Id(1)).Single();
        Assert.That(notice.Name, Is.EqualTo("masha"));
        Assert.That(notice.Kind, Is.EqualTo("assigned"));
        store.AcknowledgeNotices(Id(2), notice.Sequence);
        store.AcknowledgeNotices(Id(1), notice.Sequence + 1);
        Assert.That(store.PendingNotices(Id(1)), Has.Length.EqualTo(1));
        store.AcknowledgeNotices(Id(1), notice.Sequence);
        store = PlayerCharacterAssignments.Load(_path, true);
        store.Reconcile(Id(1), new[] { 1 }, new[] { 1 }, names: names);
        Assert.That(store.PendingNotices(Id(1)), Is.Empty);
    }

    [Test]
    public void OfflineDeathRetainsNameAndReplacementAcrossRestart()
    {
        using var host = CreateHost(GameMode.BigIsland, "notice-death.sav");
        var store = PlayerCharacterAssignments.Load(_path, false);
        var original = store.Reconcile(host, Id(1)).Single();
        var name = store.PendingNotices(Id(1)).Single().Name;
        store.AcknowledgeNotices(Id(1), 1);
        // The body has already disappeared by the time the player returns.
        host.Read(w => w.Entities.Npcs.Remove(new EntityId(original)));
        store = PlayerCharacterAssignments.Load(_path, true);
        var replacement = store.Reconcile(host, Id(1)).Single();
        Assert.That(replacement, Is.Not.EqualTo(original));
        store = PlayerCharacterAssignments.Load(_path, true);
        var notices = store.PendingNotices(Id(1));
        Assert.That(notices.Select(n => n.Kind), Is.EqualTo(new[] { "died", "assigned" }));
        Assert.That(notices[0].Name, Is.EqualTo(name));
        Assert.That(notices[1].NpcId, Is.EqualTo(replacement));
        store.Reconcile(host, Id(1));
        Assert.That(store.PendingNotices(Id(1)), Has.Length.EqualTo(2));
    }

    [Test]
    public void DeathWithoutCandidateAndDyingRetentionAreDistinct()
    {
        var store = PlayerCharacterAssignments.Load(_path, false);
        store.Reconcile(Id(1), new[] { 1 }, new[] { 1 });
        store.AcknowledgeNotices(Id(1), 1);
        store.Reconcile(Id(1), new[] { 1 }, Array.Empty<int>());
        Assert.That(store.PendingNotices(Id(1)), Is.Empty, "Dying is not death.");
        Assert.That(store.Reconcile(Id(1), Array.Empty<int>(), Array.Empty<int>(),
            deadNpcIds: new HashSet<int> { 1 }), Is.Empty);
        Assert.That(store.PendingNotices(Id(1)).Single().Kind, Is.EqualTo("died"));
        store = PlayerCharacterAssignments.Load(_path, true);
        store.Reconcile(Id(1), new[] { 2 }, new[] { 2 });
        Assert.That(store.PendingNotices(Id(1)).Select(n => n.Kind), Is.EqualTo(new[] { "died", "assigned" }));
    }

    [Test]
    public void LobbyReplacementVacancySurvivesEmptyRosterAndRestart()
    {
        var config = new WorldCreationConfig { CreatorPlayerId = Id(1), Seed = 12345, Name = "Introductions" };
        config.Camps.Add(new CampCreationConfig { Faction = Faction.Colony });
        config.Characters.Add(new CharacterCreationConfig { Id = 1, Name = "masha", Controlled = true });
        config.Characters.Add(new CharacterCreationConfig { Id = 2, Name = "nika" });
        using var host = new WorldHost(12345, GameMode.BigIsland, Path.Combine(_directory, "lobby.sav"),
            FindRepoFile("SimData", "simdata.json"), false, creationConfig: config);
        var store = PlayerCharacterAssignments.Load(_path, false);
        Assert.That(store.Reconcile(host, Id(1)), Is.EqualTo(new[] { 1 }));
        var reserve = host.Read(w => w.Entities.Npcs[new EntityId(2)]);
        host.Read(w => { w.Entities.Npcs.Clear(); return true; });
        Assert.That(store.Reconcile(host, Id(1)), Is.Empty);
        store = PlayerCharacterAssignments.Load(_path, true);
        host.Read(w => { w.Entities.Npcs.Add(reserve.Id, reserve); return true; });
        Assert.That(store.Reconcile(host, Id(2)), Is.Empty, "Other lobby visitors stay spectators.");
        Assert.That(store.Reconcile(host, Id(1)), Is.EqualTo(new[] { 2 }));
        Assert.That(store.PendingNotices(Id(1)).Select(n => n.Kind), Is.EqualTo(new[] { "assigned", "died", "assigned" }));
        Assert.That(store.Reconcile(host, Id(1)), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void AssignmentNoticeHandshakeRoundTrips()
    {
        var handshake = new Handshake();
        handshake.AssignmentNotices.Add(new PlayerAssignmentNotice { Sequence = 19, NpcId = 901, Name = "Маша", Kind = "died" });
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        handshake.Write(writer);
        stream.Position = 0;
        var copy = Handshake.Read(new BinaryReader(stream)).AssignmentNotices.Single();
        Assert.That((copy.Sequence, copy.NpcId, copy.Name, copy.Kind), Is.EqualTo((19L, 901, "Маша", "died")));
    }

    private WorldHost CreateHost(GameMode mode, string saveName) => new(
        seed: 12345,
        mode,
        savePath: Path.Combine(_directory, saveName),
        simDataPath: FindRepoFile("SimData", "simdata.json"),
        verboseTrace: false);

    private static string Id(int value) => new Guid(
        value, 0, 0, new byte[8]).ToString("N");

    private static string FindRepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = directory.FullName;
            for (var i = 0; i < relativePath.Length; i++)
            {
                candidate = Path.Combine(candidate, relativePath[i]);
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(string.Join('/', relativePath));
    }
}

}
