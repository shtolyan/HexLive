using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §149 r3 (#236): контекстное меню у игрока НЕ первого лагеря. Меню считает
/// «свою» от лагеря той, кто отдаёт приказ, и обязано совпадать с приёмом
/// приказа: что предложено — принимается, что скрыто — отбивается. До правки
/// оба ответа были перевёрнуты (гейты стояли на буквальной
/// <c>Faction.Colony</c>), и у игрока лагеря Colony2..Colony6 меню предлагало
/// травлю собственной соседки и охоту на чужую.
/// </summary>
public sealed class ManualMenuTargetsTests
{
    // Мульти-лагерный мир: девушку НЕ из Colony выдают только в таком режиме, а
    // solo-смягчение §146 сделало бы соседний лагерь нейтральным и увело тест
    // от сценария бага. Один шаг движка обязателен — до него узлы у людей ещё
    // не расставлены, и приём приказа честно отвечает "TargetUnavailable".
    private static WorldState Build(int seed)
    {
        SimTrace.EnableAll();
        var definition = PrototypeWorldDefinitionFactory.Create(seed, GameMode.HugeIsland);
        var world = new WorldStateFactory().Create(definition);
        world.Mode = GameMode.HugeIsland;
        var engine = new SimulationEngine(
            world,
            new SimulationSettings
            {
                TickDeltaTime = definition.Simulation.TickDeltaTime,
                MediumInterval = definition.Simulation.MediumTickInterval,
                SlowInterval = definition.Simulation.SlowTickInterval,
            },
            new SimulationClock());
        SimulationSystemRegistry.RegisterDefaults(engine);
        engine.Step();
        return world;
    }

    private static NPCState Girl(WorldState world, Faction faction) =>
        world.Entities.Npcs.Values.Single(n => n.Faction == faction);

    /// <summary>Выданная сервером девушка соседнего лагеря: §123-право плюс
    /// ручной режим — ровно то, что даёт игроку реестр назначений.</summary>
    private static void HandToPlayer(WorldState world, NPCState npc)
    {
        world.PlayerControlledNpcs.Add(npc.Id.Value);
        npc.Mind.ManualControl = true;
        Assert.That(PlayerAuthority.IsPlayerOwned(world, npc), Is.True);
    }

    [Test]
    public void NonDefaultCampPlayerHuntsHerOwnCampAndNotTheFirstOne()
    {
        var world = Build(777);
        var player = Girl(world, Faction.Colony3);
        var mate = Girl(world, Faction.Colony2);
        mate.Faction = Faction.Colony3;
        var stranger = Girl(world, Faction.Colony);
        HandToPlayer(world, player);
        player.Inventory.Items.Add(new ItemInstance("tool.knife"));

        Assert.Multiple(() =>
        {
            Assert.That(ManualMenuTargets.SameSide(player.Faction, mate.Faction), Is.True,
                "Соседка по лагерю Colony3 — своя, меню обязано звать её в §56.");
            Assert.That(ManualMenuTargets.SameSide(player.Faction, stranger.Faction),
                Is.False,
                "Девушка первого лагеря игроку Colony3 — чужая.");
        });

        // Что предлагало СТАРОЕ меню (гейты на буквальной Faction.Colony):
        // травлю собственной соседки и охоту на чужую. Приём отбивает оба.
        var abuseOnMate = ManualCommandExecutor.Apply(
            world, new AbusePersonCommand(player.Id, mate.Id));
        var preyOnStranger = ManualCommandExecutor.Apply(
            world, new PreyPersonCommand(player.Id, stranger.Id));

        // Что предлагает НОВОЕ меню — приём принимает.
        var preyOnMate = ManualCommandExecutor.Apply(
            world, new PreyPersonCommand(player.Id, mate.Id));

        Assert.Multiple(() =>
        {
            Assert.That(abuseOnMate.Reason, Is.EqualTo("NotHostile"),
                "Сцена §81 против своей — мёртвый пункт старого меню.");
            Assert.That(preyOnStranger.Reason, Is.EqualTo("NotAlly"),
                "Охота §56 на чужую — второй мёртвый пункт старого меню.");
            Assert.That(preyOnMate.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"Prey по своей отклонён: {preyOnMate.Reason}");
            Assert.That(player.Mind.CurrentGoal, Is.EqualTo(GoalType.Prey));
        });
    }

    [Test]
    public void StandingOwnCampNeighbourStaysCarryableForANonDefaultCampPlayer()
    {
        var world = Build(777);
        var player = Girl(world, Faction.Colony3);
        var mate = Girl(world, Faction.Colony2);
        mate.Faction = Faction.Colony3;
        var stranger = Girl(world, Faction.Colony);
        HandToPlayer(world, player);

        Assert.Multiple(() =>
        {
            // §118.4 r2: своих носят всегда, чужую на ногах — нет. Меню и приём
            // обязаны отвечать одинаково, иначе пункт либо пропадает у своей,
            // либо висит серым над чужой.
            Assert.That(ManualCarryTargets.CanCarry(world, player, mate, dead: false),
                Is.True);
            Assert.That(ManualMenuTargets.SameSide(player.Faction, mate.Faction), Is.True,
                "Меню спрятало «взять на руки» у стоящей своей.");
            Assert.That(ManualCarryTargets.CanCarry(world, player, stranger, dead: false),
                Is.False);
            Assert.That(ManualMenuTargets.SameSide(player.Faction, stranger.Faction),
                Is.False,
                "Меню предложило носилки чужой на ногах — приём это отбивает.");
        });
    }

    [Test]
    public void CampMergeIsOfferedAndAcceptedBetweenTwoNonDefaultCamps()
    {
        var world = Build(777);
        var player = Girl(world, Faction.Colony3);
        var neighbour = Girl(world, Faction.Colony2);
        HandToPlayer(world, player);
        neighbour.Position = player.Position;
        player.Social.GetOrCreate(neighbour.Id).Affinity = 0.75f;
        neighbour.Social.GetOrCreate(player.Id).Affinity = 0.75f;

        Assert.Multiple(() =>
        {
            Assert.That(
                ManualMenuTargets.NeighbourCamp(player.Faction, neighbour.Faction),
                Is.True,
                "Дипломатия §146.12 пропала из меню игрока не первого лагеря.");
            Assert.That(ManualMenuTargets.NeighbourCamp(Faction.Colony3, Faction.Colony3),
                Is.False, "Свой же лагерь не объединяют (AlreadySameCamp).");
            Assert.That(ManualMenuTargets.NeighbourCamp(Faction.Colony3, Faction.Outsiders),
                Is.False, "Чужак — не соседний лагерь.");
            Assert.That(ManualMenuTargets.NeighbourCamp(Faction.Colony, Faction.Colony2),
                Is.True, "Ответ для первого лагеря обязан остаться прежним.");
        });

        var accepted = ManualCommandExecutor.Apply(world,
            new MergeCampsCommand(player.Id, neighbour.Id, useTargetCamp: false));

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted), accepted.Reason);
            Assert.That(neighbour.Faction, Is.EqualTo(player.Faction),
                "Объединение двух соседних лагерей обязано сойтись в один.");
        });
    }
}

}
