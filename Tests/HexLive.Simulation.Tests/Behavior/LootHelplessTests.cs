using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §111: увидел лежащего врага — обыскал. Тесты меряют три вещи, каждая из
/// которых уже ломалась в соседних механиках:
///
/// <list type="bullet">
/// <item>сцена вообще СЛУЧАЕТСЯ, в обе стороны (мачете рейдера должно доехать
/// до колонии — до §111 оно попадало туда только с трупа);</item>
/// <item>порядок добычи — оружие раньше барахла, иначе короткий обморок
/// уносит котелок, а нож остаётся;</item>
/// <item>в сознании — значит не жертва: спящую и плачущую §110 механика не
/// трогает, и это граница, а не деталь.</item>
/// </list>
/// </summary>
public sealed class LootHelplessTests
{
    // Смежная пара «чужак ↔ колонистка», как в SwingSignalTests: ближе
    // поставить нельзя, а дальше — уже про дорогу, а не про сцену.
    private static (SimulationEngine engine, NPCState outsider, NPCState colonist) Adjacent(
        int seed = 12345)
    {
        var engine = TestWorld.CreateEngine(seed);
        var world = engine.World;

        var outsider = world.Entities.Npcs.Values.FirstOrDefault(n => n.Faction != Faction.Colony);
        Assert.That(outsider, Is.Not.Null,
            "Прототипный мир обязан нести чужака (Spec72.OutsiderCount) — без него §111 нечем мерить.");

        var colonist = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        Assert.That(FactionRelations.AreHostile(outsider.Faction, colonist.Faction), Is.True,
            "§72 выключен — тогда враждебных нет вовсе и §111 не с кем сыграть.");

        var from = world.Junctions.Items.Values.First(j => j.Neighbors.Count > 0);
        var to = world.Junctions.Items[from.Neighbors[0]];
        // §125: сцена §111 меряет ОБЫСК, а не зоркость. Радиус восприятия
        // ВСЕХ тел закреплён на прежней константе 6 (0.6 × 10) — иначе исход
        // арены зависел бы от того, какое Восприятие выпало на этом сиде
        // (и не только участникам: подруги с другим радиусом ходят иначе).
        foreach (var body in world.Entities.Npcs.Values)
        {
            body.Attributes.Perception = 0.6f;
        }

        outsider.CurrentJunction = from.Id;
        outsider.Position = from.WorldPosition;
        outsider.Tile = from.Tiles[0];
        colonist.CurrentJunction = to.Id;
        colonist.Position = to.WorldPosition;
        colonist.Tile = to.Tiles[0];

        return (engine, outsider, colonist);
    }

    // Уложить без сознания на заведомо долгий срок: сцену меряем целиком, а не
    // гонку с пробуждением (её меряет отдельный тест ниже).
    private static void KnockOut(WorldState world, NPCState npc, int ticks = 4000)
    {
        npc.Mind.FaintedUntilTick = world.Tick + ticks;
        Assert.That(npc.IsUnconscious(world.Tick), Is.True);
    }

    // Tests of the two-person loot transaction must not accidentally become
    // tests of §111.8. The witness response has its own radius-bound test below.
    private static void SilenceAlliedWitnesses(WorldState world, NPCState victim)
    {
        foreach (var ally in world.Entities.Npcs.Values.Where(n =>
                     !n.Id.Equals(victim.Id) && FactionRelations.AreAllies(n, victim)))
        {
            ally.Mind.FaintedUntilTick = world.Tick + 10000;
        }
    }

    // Шагать до события или до потолка, собирая трассу: кольцо подрезается на
    // 2048 (~11 тиков), поэтому watermark, а не сравнение по ссылке.
    private static (bool seen, List<string> tail) StepUntil(
        SimulationEngine engine, string type, int maxTicks)
    {
        var world = engine.World;
        long watermark = 0;
        var tail = new List<string>();
        var seen = false;

        for (var i = 0; i < maxTicks && !seen; i++)
        {
            engine.Step();
            foreach (var e in world.Events.Items)
            {
                if (e.Seq <= watermark)
                {
                    continue;
                }

                watermark = e.Seq;
                if (e.Type.StartsWith("LootHelpless") || e.Type == "StrippedHelpless")
                {
                    tail.Add($"t{world.Tick} {e.Type}: {e.Message}");
                }

                if (e.Type == type)
                {
                    seen = true;
                }
            }
        }

        return (seen, tail);
    }

    [Test]
    public void Outsider_StripsUnconsciousColonist()
    {
        var (engine, outsider, colonist) = Adjacent();
        var world = engine.World;

        KnockOut(world, colonist);
        SilenceAlliedWitnesses(world, colonist);
        // The authored wave-0 outsider already carries a knife (§72.14), so a
        // second knife has no marginal loadout value and is correctly skipped.
        // Use a tool he does not own to verify the actual transfer contract.
        colonist.Inventory.Items.Add(new ItemInstance("tool.hammer"));

        var (seen, tail) = StepUntil(engine, "StrippedHelpless", 600);

        Assert.That(seen, Is.True,
            "Чужак не обыскал лежащую рядом колонистку за 600 тиков. Трасса:\n  " +
            string.Join("\n  ", tail));
        Assert.That(outsider.Inventory.Items.Any(i => i.DefinitionId == "tool.hammer"), Is.True,
            "Молоток обязан переехать к нему: сцена сыграла, а вещь осталась.");
        Assert.That(colonist.Inventory.Items.Any(i => i.DefinitionId == "tool.hammer"), Is.False);
    }

    [Test]
    public void TakingItem_StampsItsDefinitionForTheSingleSpeechBubble()
    {
        var (engine, outsider, colonist) = Adjacent();
        var world = engine.World;
        KnockOut(world, colonist);
        SilenceAlliedWitnesses(world, colonist);
        colonist.Inventory.Items.Clear();
        colonist.Inventory.Items.Add(new ItemInstance("tool.hammer"));

        var (seen, tail) = StepUntil(engine, "LootHelplessTook", 600);

        Assert.That(seen, Is.True, "No loot beat reached. Trace:\n  " + string.Join("\n  ", tail));
        Assert.Multiple(() =>
        {
            Assert.That(outsider.Execution.LastSocialCueKind, Is.EqualTo("LootHelplessTook"));
            Assert.That(outsider.Execution.LastSocialCueItemId, Is.EqualTo("tool.hammer"));
            Assert.That(outsider.Execution.LastSocialCuePeerId, Is.Null);
            var snapshot = HexLive.Simulation.Debug.WorldSnapshotExporter.Export(world)
                .Npcs.Single(n => n.Id.Equals(outsider.Id));
            Assert.That(snapshot.SocialCueItemId, Is.EqualTo("tool.hammer"));
        });

        SocialCueSignals.Stamp(world, outsider, "TalkRequest", colonist.Id);
        Assert.That(outsider.Execution.LastSocialCueItemId, Is.Empty,
            "A normal cue must not inherit the previous loot picture.");
    }

    [Test]
    public void AttackInterruption_CleansLootButKeepsFightAndUsesFullCooldown()
    {
        var (engine, outsider, victim) = Adjacent();
        var world = engine.World;
        world.Tick = 100;
        var defender = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && !n.Id.Equals(victim.Id));

        outsider.Mind.CurrentGoal = GoalType.LootHelpless;
        outsider.Mind.LootHelplessTargetNpcId = victim.Id;
        outsider.Mind.LootHelplessTakenCount = 1;
        outsider.Plan.Status = PlanStatus.Active;
        outsider.Plan.TargetAgentId = victim.Id;
        outsider.Execution.Status = ExecutionStatus.InProgress;
        outsider.Execution.CurrentInteraction = InteractionType.Loot;
        victim.Mind.PendingLootedBy = outsider.Id;

        defender.Mind.CurrentGoal = GoalType.Defend;
        defender.Mind.CombatOpponentNpcId = outsider.Id;
        defender.IsFighting = true;
        outsider.Mind.CombatOpponentNpcId = defender.Id;
        outsider.IsFighting = true;

        ExecutionSystem.AbortLootHelplessForAttack(world, outsider, defender.Id);

        Assert.Multiple(() =>
        {
            Assert.That(victim.Mind.PendingLootedBy, Is.Null);
            Assert.That(outsider.Mind.LootHelplessTargetNpcId, Is.Null);
            Assert.That(outsider.Execution.Status, Is.EqualTo(ExecutionStatus.None));
            Assert.That(outsider.Mind.LootHelplessCooldownUntilTick,
                Is.EqualTo(world.Tick + Spec111.LootHelplessCooldownTicks));
            Assert.That(outsider.Mind.CombatOpponentNpcId, Is.EqualTo(defender.Id));
            Assert.That(defender.Mind.CombatOpponentNpcId, Is.EqualTo(outsider.Id));
            Assert.That(defender.Mind.CurrentGoal, Is.EqualTo(GoalType.Defend));
        });
    }

    [Test]
    public void Colonist_StripsUnconsciousOutsider_MacheteReachesTheColony()
    {
        // ⭐ Главный сюжет фичи. Мачете §79 не крафтится, и до §111 попадало в
        // колонию единственным путём — с трупа чужака.
        var (engine, outsider, colonist) = Adjacent();
        var world = engine.World;

        KnockOut(world, outsider);
        outsider.Inventory.Items.Add(new ItemInstance("tool.machete"));

        var (seen, tail) = StepUntil(engine, "StrippedHelpless", 600);

        Assert.That(seen, Is.True,
            "Колонистка не обыскала вырубленного чужака — симметрия §111.1 сломана. Трасса:\n  " +
            string.Join("\n  ", tail));
        // Кто именно подошла первой — дело сида: рядом стоит поставленная нами,
        // но выиграть аукцион могла и соседка. Механику меряет ФРАКЦИЯ.
        var inColony = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .Any(n => n.Inventory.Items.Any(i => i.DefinitionId == "tool.machete"));
        Assert.That(inColony, Is.True,
            "Мачете обязано доехать до колонии. Трасса:\n  " + string.Join("\n  ", tail));
        Assert.That(outsider.Inventory.Items.Any(i => i.DefinitionId == "tool.machete"), Is.False,
            "И обязано УЙТИ с него: разоружили — значит разоружили.");
    }

    [Test]
    public void Waking_AbortsTheScene()
    {
        var (engine, _, colonist) = Adjacent();
        var world = engine.World;

        // Обморока хватает на подход и на первую-вторую вещь, но не на весь
        // рюкзак: такт — LootHelplessTakeTicks (10), и её надо разбудить раньше,
        // чем карманы опустеют.
        KnockOut(world, colonist, ticks: 25);
        SilenceAlliedWitnesses(world, colonist);
        colonist.Inventory.Items.Add(new ItemInstance("tool.knife"));
        colonist.Inventory.Items.Add(new ItemInstance("tool.hammer"));
        colonist.Inventory.Items.Add(new ItemInstance("tool.saw"));

        var (aborted, tail) = StepUntil(engine, "LootHelplessAborted", 600);

        Assert.That(aborted, Is.True,
            "Очнувшаяся обязана обрывать сцену (MarkWoke). Трасса:\n  " +
            string.Join("\n  ", tail));
        Assert.That(colonist.Mind.PendingLootedBy, Is.Null,
            "Заявка на тело не снята — тело осталось «занятым навсегда», и больше " +
            "его не тронет никто (урок §81).");
    }

    [Test]
    public void TakeOrder_WeaponsByPriority_ThenTools_ThenRest()
    {
        var world = TestWorld.CreateWorld();
        var victim = world.Entities.Npcs.Values.First();
        victim.Inventory.Items.Clear();
        victim.Inventory.Items.Add(new ItemInstance("resource.stick"));
        victim.Inventory.Items.Add(new ItemInstance("tool.pot"));
        victim.Inventory.Items.Add(new ItemInstance("tool.knife"));
        victim.Inventory.Items.Add(new ItemInstance("tool.machete"));

        var taken = new List<string>();
        while (LootHelplessMath.HasLoot(victim))
        {
            var spoil = LootHelplessMath.NextSpoil(world, victim);
            taken.Add(spoil.DefinitionId);
            victim.Inventory.Items.Remove(spoil);
        }

        Assert.That(taken[0], Is.EqualTo("tool.machete"),
            "Сперва САМОЕ мощное оружие: обезоружить важнее, чем набрать.");
        Assert.That(taken[1], Is.EqualTo("tool.knife"));
        Assert.That(taken.IndexOf("tool.pot"), Is.LessThan(taken.IndexOf("resource.stick")),
            "Инструмент раньше барахла.");
    }

    [Test]
    public void ConsciousMarks_AreNotVictims()
    {
        var (engine, outsider, colonist) = Adjacent();
        var world = engine.World;
        colonist.Inventory.Items.Add(new ItemInstance("tool.knife"));

        Assert.That(LootHelplessMath.IsLootableBy(world, outsider, colonist), Is.False,
            "Стоящая на ногах — не добыча §111: это §81.");

        // §110: плачущая В СОЗНАНИИ. Граница, а не деталь — иначе §111 съел бы
        // сцену утешения.
        colonist.Mind.CryingUntilTick = world.Tick + 240;
        Assert.That(LootHelplessMath.IsLootableBy(world, outsider, colonist), Is.False,
            "Рыдающая §110 в сознании — обыскивать её нельзя.");

        // Спящая — тоже в сознании в смысле §60: тихий грабёж спящей это §40.5.
        colonist.Mind.CryingUntilTick = 0;
        colonist.Execution.CurrentInteraction = InteractionType.Sleep;
        Assert.That(LootHelplessMath.IsLootableBy(world, outsider, colonist), Is.False,
            "Спящая — не добыча §111 (§81.6 оставил её краже §40.5).");
    }

    [Test]
    public void Allies_AreNeverVictims()
    {
        var (engine, _, colonist) = Adjacent();
        var world = engine.World;
        var sister = world.Entities.Npcs.Values
            .First(n => n.Faction == Faction.Colony && !n.Id.Equals(colonist.Id));

        KnockOut(world, colonist);
        colonist.Inventory.Items.Add(new ItemInstance("tool.knife"));

        Assert.That(LootHelplessMath.IsLootableBy(world, sister, colonist), Is.False,
            "Своих не обыскивают: §111 живёт строго на вражде §72.");
    }

    [Test]
    public void NearbyAllies_DefendTheLootedVictim_ButDistantAlliesDoNot()
    {
        var (engine, outsider, victim) = Adjacent();
        var world = engine.World;
        var allies = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony && !n.Id.Equals(victim.Id))
            .Take(2)
            .ToArray();
        Assert.That(allies.Length, Is.EqualTo(2),
            "Для проверки радиуса нужны две союзницы жертвы.");

        var near = allies[0];
        var far = allies[1];
        near.Tile = new TileCoord(victim.Tile.Q + Spec111.LootWitnessRadiusTiles, victim.Tile.R);
        far.Tile = new TileCoord(victim.Tile.Q + Spec111.LootWitnessRadiusTiles + 1, victim.Tile.R);

        var responders = CombatHelpSystem.RallyLootWitnesses(world, victim, outsider.Id);

        Assert.That(responders, Is.EqualTo(1),
            "На обыск должны ответить все бодрствующие союзники в трёх гексах.");
        Assert.That(near.Mind.CurrentGoal, Is.EqualTo(GoalType.Defend));
        Assert.That(near.Mind.CombatAssistAttackerNpcId, Is.EqualTo(outsider.Id),
            "Свидетель должен бить обыскивающего, а не саму жертву.");
        Assert.That(far.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Defend),
            "Союзница за пределом радиуса не должна видеть сцену.");
    }

    [Test]
    public void MultipleLootWitnesses_EngageOneAttacker_AndAttackerRepliesToNearest()
    {
        var (engine, outsider, victim) = Adjacent();
        var world = engine.World;
        var defenders = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony && !n.Id.Equals(victim.Id))
            .Take(2)
            .ToArray();
        Assert.That(defenders.Length, Is.EqualTo(2));

        defenders[0].Position = outsider.Position + new Float2(0.1f, 0f);
        defenders[1].Position = outsider.Position + new Float2(0.3f, 0f);
        defenders[0].Mind.CombatAssistAttackerNpcId = outsider.Id;
        defenders[1].Mind.CombatAssistAttackerNpcId = outsider.Id;

        HumanCombatPairing.EngageAssist(world, defenders[1], outsider);
        HumanCombatPairing.EngageAssist(world, defenders[0], outsider);

        Assert.That(defenders[0].Mind.CombatOpponentNpcId, Is.EqualTo(outsider.Id));
        Assert.That(defenders[1].Mind.CombatOpponentNpcId, Is.EqualTo(outsider.Id),
            "Несколько свидетелей должны иметь право бить одну цель.");
        Assert.That(outsider.Mind.CombatOpponentNpcId, Is.EqualTo(defenders[0].Id),
            "Ответная цель должна быть ближайшим живым противником.");
    }

    [Test]
    public void EndingLoot_ClearsAssistPairsAndSwingSlotsSymmetrically()
    {
        var (engine, outsider, victim) = Adjacent();
        var world = engine.World;
        var defender = world.Entities.Npcs.Values
            .First(n => n.Faction == Faction.Colony && !n.Id.Equals(victim.Id));
        defender.Mind.CombatAssistAttackerNpcId = outsider.Id;
        defender.Mind.CurrentGoal = GoalType.Defend;
        defender.StrikeLandsAtTick = world.Tick + 10;
        outsider.StrikeLandsAtTick = world.Tick + 10;
        HumanCombatPairing.EngageAssist(world, defender, outsider);

        HumanCombatPairing.ClearAssistsAgainst(world, outsider.Id);

        Assert.That(defender.Mind.CombatAssistAttackerNpcId, Is.Null);
        Assert.That(defender.Mind.CombatOpponentNpcId, Is.Null);
        Assert.That(outsider.Mind.CombatOpponentNpcId, Is.Null);
        Assert.That(defender.StrikeLandsAtTick, Is.Zero);
        Assert.That(outsider.StrikeLandsAtTick, Is.Zero);
    }
}

}
