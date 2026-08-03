using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §107: сговор против чужака — арифметика и то, что он доходит до удара.
///
/// <para>
/// Ассерты на СГОВОР и на ПЕРВЫЙ УДАР, а не на исход охоты: до конца её могут
/// не довести ночь, волк, поднявшийся голод и вода (§106), и гейт на исход был
/// бы флаки. Сколько охот доходит до конца — вопрос к соаку
/// (<c>--trace-preset grouphunt</c>), а не к гейту.
/// </para>
/// </summary>
public sealed class GroupHuntTests
{
    [Test]
    public void Pact_NeedsEveryoneToHateHim()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        var girls = Colony(world).Take(3).ToList();
        Assert.That(girls.Count, Is.EqualTo(3), "Прототипный мир обязан нести троих колонисток.");

        Hate(girls, stranger, Spec107.GroupHuntHateThreshold - 0.05f);
        Assert.That(GroupHuntMath.PactHolds(world, girls, stranger, out _), Is.True,
            "Все трое ненавидят его сильнее порога — сговор обязан складываться.");

        // Одной хватает, чтобы всё расстроить: это решение ВСЕХ, а не
        // большинства — иначе несогласная развалила бы группу на первом же
        // перепланировании, и «идут вместе» стало бы неправдой.
        girls[1].Social.GetOrCreate(stranger.Id).Affinity =
            Spec107.GroupHuntHateThreshold + 0.05f;
        Assert.That(GroupHuntMath.PactHolds(world, girls, stranger, out var blocked), Is.False,
            "Одна не согласна — сговора нет.");
        Assert.That(blocked, Does.Contain($"NPC{girls[1].Id.Value}"));
    }

    [Test]
    public void Pact_RefusesAFewerThanMinimumCircle()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        var pair = Colony(world).Take(Spec107.GroupHuntMinGirls - 1).ToList();
        Hate(pair, stranger, -1f);

        Assert.That(GroupHuntMath.PactHolds(world, pair, stranger, out var blocked), Is.False,
            "Вдвоём это разговор, а не сговор.");
        Assert.That(blocked, Does.Contain("TooFew"));
    }

    [Test]
    public void Pact_RefusesWhileTheCooldownRuns()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        var girls = Colony(world).Take(3).ToList();
        Hate(girls, stranger, -1f);

        girls[2].Mind.GroupHuntCooldownUntilTick = world.Tick + 10;
        Assert.That(GroupHuntMath.PactHolds(world, girls, stranger, out var blocked), Is.False,
            "Кулдаун проверяется В САМОМ сговоре: цель реактивная, аукцион её не " +
            "спрашивает, и кулдаун цели её бы не удержал — они пошли бы снова " +
            "тем же вечером.");
        Assert.That(blocked, Does.Contain("Cooldown"));
    }

    [Test]
    public void Pact_RefusesAgainstSomebodyAlreadyDown()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        var girls = Colony(world).Take(3).ToList();
        Hate(girls, stranger, -1f);

        // IsProne выводится из отнятых ног — «лежит» здесь ставим обмороком.
        stranger.Mind.FaintedUntilTick = world.Tick + 100;
        Assert.That(GroupHuntMath.PactHolds(world, girls, stranger, out var blocked), Is.False,
            "Лежачего не бьют — и без этого сговор против отключённого завершался " +
            "успехом в тот же тик, не ударив ни разу.");
        Assert.That(blocked, Does.Contain("AlreadyDown"));
    }

    [Test]
    public void Pact_HandsTheGoalToEverybodyAtOnce()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        var girls = Colony(world).Take(3).ToList();
        Hate(girls, stranger, -1f);

        Assert.That(GroupHuntMath.TryFormPact(world, girls, stranger), Is.True);
        foreach (var girl in girls)
        {
            Assert.That(girl.Mind.CurrentGoal, Is.EqualTo(GoalType.GroupHunt),
                $"NPC{girl.Id.Value} осталась при своём деле — группа неполная.");
            Assert.That(girl.Mind.GroupHuntTargetNpcId, Is.EqualTo(stranger.Id));
            Assert.That(girl.Mind.GoalLock?.Goal, Is.EqualTo(GoalType.GroupHunt),
                "Замок цели — весь бюджет охоты; без него её перебьёт первое же дело.");
            // Портрет ТОГО, о ком сговорились, всплывает над каждой — включая
            // третью, которая ни с кем не разговаривала.
            Assert.That(girl.Execution.LastSocialCueKind, Is.EqualTo("GroupHuntPact"));
            Assert.That(girl.Execution.LastSocialCuePeerId, Is.EqualTo(stranger.Id));
        }
    }

    /// <summary>
    /// ⭐ Прототипный остров и НИЧЕГО НЕ ПОДКРУЧЕНО: он сам доводит их до
    /// ненависти своими сценами (§81 + свидетельницы §107), они сами
    /// сговариваются и сами доходят до удара.
    ///
    /// <para>
    /// Ненависть НЕ засеивается руками нарочно. Засеянная проверяла бы только
    /// вторую половину дуги — и первая же попытка это доказала: с ненавистью
    /// с нулевого тика сговор случался в 7867, охота разваливалась, а
    /// GroupHuntDone выдавался за чужую работу. Цепочка целиком ловит и такое.
    /// </para>
    /// </summary>
    [Test]
    public void GroupHunt_LandsBlows_OnThePrototypeIsland()
    {
        var engine = TestWorld.CreateEngine(313);
        var world = engine.World;

        long watermark = 0;
        int? pactAt = null;
        int? struckAt = null;
        var blockedTail = new Queue<string>();

        for (var tick = 0; tick < 24000 && struckAt is null; tick++)
        {
            engine.Step();
            foreach (var e in world.Events.Items)
            {
                if (e.Seq <= watermark)
                {
                    continue;
                }

                watermark = e.Seq;
                switch (e.Type)
                {
                    case "GroupHuntPactFormed":
                        pactAt ??= world.Tick;
                        break;
                    case "GroupHuntStruck":
                        struckAt ??= world.Tick;
                        break;
                    default:
                        if (e.Type.StartsWith("GroupHunt", System.StringComparison.Ordinal))
                        {
                            blockedTail.Enqueue($"t{world.Tick} {e.Type}: {e.Message}");
                            while (blockedTail.Count > 20)
                            {
                                blockedTail.Dequeue();
                            }
                        }

                        break;
                }
            }

        }

        Assert.That(pactAt, Is.Not.Null,
            "За 24000 тиков реального мира трое ненавидящих его колонисток ни разу " +
            "не сговорились. Последние причины отказа:\n" +
            string.Join("\n", blockedTail));
        Assert.That(struckAt, Is.Not.Null,
            $"Сговор случился на тике {pactAt}, но до удара дело не дошло — " +
            "значит сломана либо дорога к нему (BuildGroupHuntPlan), либо сцепка " +
            "боя (GroupHuntSystem). Хвост событий охоты:\n" +
            string.Join("\n", blockedTail));
    }

    private static IEnumerable<NPCState> Colony(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value);

    private static void Hate(IEnumerable<NPCState> girls, NPCState stranger, float affinity)
    {
        foreach (var girl in girls)
        {
            girl.Social.GetOrCreate(stranger.Id).Affinity = affinity;
        }
    }
}

}
