using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §118: ручное управление. Проверяется не «есть флаг», а четыре обещания,
/// данные игроку:
/// <list type="number">
/// <item>под ручным управлением НИКТО, кроме игрока, целей ей не ставит;</item>
/// <item>приказ доходит до штатных систем и исполняется ими, а не второй
/// копией симуляции;</item>
/// <item>правило Кенши: стоящая отвечает на удары, идущая по приказу — нет;</item>
/// <item>выключили тумблер — автономия вернулась целиком.</item>
/// </list>
/// </summary>
public sealed class ManualControlTests
{
    private const int MediumTicks = 16;

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

    private static void Step(SimulationEngine engine, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            engine.Step();
        }
    }

    private static void TakeControl(SimulationEngine engine, NPCState npc)
    {
        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true));
        engine.Step();
    }

    private static bool HasTrace(WorldState world, EntityId npc, string type, string contains)
    {
        foreach (var e in world.Events.Items)
        {
            if (e.Type == type && e.EntityId == npc.Value &&
                (contains.Length == 0 || (e.Message?.Contains(contains) ?? false)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Свободный проходимый узел рядом с NPC — цель для «иди сюда».</summary>
    private static Junction NearbyFreeJunction(WorldState world, NPCState npc, int minTiles = 2)
    {
        return world.Junctions.Items.Values.First(j =>
            !j.Blocked &&
            j.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) >= minTiles &&
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) <= 4 &&
            npc.CurrentJunction is { } start &&
            Connectivity.Reachable(world, start, j.Id));
    }

    // ── 1. Автономия выключена ───────────────────────────────────────────

    [Test]
    public void ManualNpcNeverTakesAGoalFromTheAuction()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonist(engine.World);
        TakeControl(engine, npc);

        // Голод и жажда на пределе: обычная колонистка бросилась бы за едой
        // в первый же средний проход.
        npc.Needs.Hunger = 0.98f;
        npc.Needs.Thirst = 0.98f;

        Step(engine, MediumTicks * 8);

        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
            "Аукцион поставил цель персонажу под ручным управлением — значит, " +
            "игрок им на самом деле не управляет.");
        Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active),
            "Планировщик построил план ручной колонистке.");
    }

    [Test]
    public void ReleasingControlBringsTheAuctionBack()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonist(engine.World);
        TakeControl(engine, npc);
        npc.Needs.Hunger = 0.98f;
        Step(engine, MediumTicks * 4);
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));

        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, false));
        Step(engine, MediumTicks * 4);

        Assert.That(npc.Mind.ManualControl, Is.False);
        Assert.That(npc.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.None),
            "Вернули ИИ — она обязана снова сама себе ставить цели.");
    }

    // ── 2. Приказы исполняются штатными системами ────────────────────────

    [Test]
    public void MoveOrderWalksHerThereAndThenSheStandsIdle()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonist(engine.World);
        TakeControl(engine, npc);

        var destination = NearbyFreeJunction(engine.World, npc);
        var startTile = npc.Tile;

        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();

        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder),
            "Приказ идти не превратился в цель.");
        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(destination.Id));

        Step(engine, MediumTicks * 30);

        Assert.That(npc.Tile, Is.Not.EqualTo(startTile),
            "Она не сдвинулась с места по приказу игрока.");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
            "Приказ доигран — она обязана стоять и ждать следующего, а не " +
            "возвращаться к своим делам.");
    }

    [Test]
    public void InteractOrderPicksTheThingUp()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        // Кокос под ноги — приказ «подобрать» должен доехать до инвентаря
        // через обычный двухшаговый план.
        var here = npc.CurrentJunction!.Value;
        var neighbor = SpatialQueries.GetPassableNeighbors(world, here).First();
        var tile = world.Junctions.Items[neighbor].Tiles[0];
        var coconut = WorldObjectMutations.SpawnObject(
            world, "food.coconut", new FragmentId(1), tile, neighbor);

        var before = npc.Inventory.Items.Count(i => i.DefinitionId == "food.coconut");

        engine.Commands.Enqueue(new InteractCommand(npc.Id, coconut.Id, InteractionType.PickUp));
        engine.Step();

        Assert.That(npc.Plan.Steps.Count, Is.EqualTo(2),
            "План работы с объектом обязан быть тем же двухшаговым, что строит " +
            "планировщик: дойти и сделать.");

        Step(engine, MediumTicks * 30);

        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == "food.coconut"),
            Is.EqualTo(before + 1),
            "Приказ «подобрать» не довёл кокос до инвентаря.");
    }

    // ── 3. Отказы: приказ, который нельзя выполнить ──────────────────────

    [Test]
    public void OrderWithoutTheToolIsRejectedAndLeaksNothing()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        npc.Inventory.Items.Clear(); // без ножа

        var here = npc.CurrentJunction!.Value;
        var neighbor = SpatialQueries.GetPassableNeighbors(world, here).First();
        var tile = world.Junctions.Items[neighbor].Tiles[0];
        var coconut = WorldObjectMutations.SpawnObject(
            world, "food.coconut", new FragmentId(1), tile, neighbor);

        var reservedBefore = world.Reservations.Junctions.Count;

        // Проколоть кокос можно только лезвием — приказ обязан отлететь.
        engine.Commands.Enqueue(new InteractCommand(npc.Id, coconut.Id, InteractionType.Process));
        engine.Step();

        Assert.That(HasTrace(world, npc.Id, "ManualOrderRejected", "Reason=MissingTool"), Is.True,
            "Невыполнимый приказ обязан объяснить себя трассой — молчание " +
            "игрок читает как поломку.");
        Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
        Assert.That(world.Reservations.Junctions.Count, Is.EqualTo(reservedBefore),
            "Отклонённый приказ оставил за собой резервацию узла — так мир " +
            "зарастает клетками «занято навсегда».");
    }

    [Test]
    public void OrderToAnNpcUnderAiIsRefused()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonist(engine.World);
        engine.Step(); // узел NPC появляется на первом тике

        var destination = NearbyFreeJunction(engine.World, npc);
        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();

        Assert.That(HasTrace(engine.World, npc.Id, "ManualOrderRejected", "Reason=NotManual"),
            Is.True,
            "Приказ персонажу, которого игрок уже вернул ИИ, обязан отлетать: " +
            "иначе устаревший клик перебивает только что выбранную цель.");
    }

    [Test]
    public void SpammedOrdersDoNotLeakReservations()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        var reservedBefore = world.Reservations.Junctions.Count;
        var targets = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                        HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) is >= 2 and <= 5)
            .Take(12)
            .ToList();

        // Игрок кликает быстрее, чем идёт тик: каждый приказ обязан снимать за
        // собой всё, что взял предыдущий.
        foreach (var target in targets)
        {
            engine.Commands.Enqueue(new MoveToCommand(npc.Id, target.WorldPosition));
            engine.Step();
        }

        engine.Commands.Enqueue(new StopCommand(npc.Id));
        Step(engine, MediumTicks * 2);

        Assert.That(world.Reservations.Junctions.Count, Is.LessThanOrEqualTo(reservedBefore),
            "Спам приказов оставил резервации за собой.");
    }

    // ── 4. Правило Кенши ─────────────────────────────────────────────────

    /// <summary>
    /// Ставит рядом с целью ЖИВОГО нападающего. Нападающий тоже ручной и идёт
    /// по приказу атаки — потому что фальшивая сцепка (проставить поля руками)
    /// не переживает и одного среднего прохода: MobSystem гасит IsFighting
    /// всем, и владелец боя обязан защёлкивать флаг заново каждый проход. Без
    /// живого владельца §109 честно считает такую пару призраком и распускает
    /// её — то есть тест мерил бы не правило Кенши, а свой собственный
    /// неправильный сетап.
    /// </summary>
    private static NPCState AttackerNextTo(SimulationEngine engine, NPCState target)
    {
        var world = engine.World;
        var attacker = world.Entities.Npcs.Values.First(n => !n.Id.Equals(target.Id));
        attacker.Tile = target.Tile;
        attacker.CurrentJunction = target.CurrentJunction;
        attacker.Position = target.Position;

        engine.Commands.Enqueue(new SetManualControlCommand(attacker.Id, true));
        engine.Step();
        engine.Commands.Enqueue(new AttackNpcCommand(attacker.Id, target.Id));
        engine.Step();
        return attacker;
    }

    [Test]
    public void IdleManualNpcFightsBackWhenAttacked()
    {
        var engine = TestWorld.CreateEngine();
        var target = Colonist(engine.World);
        TakeControl(engine, target);

        AttackerNextTo(engine, target);
        Step(engine, MediumTicks * 3);

        Assert.That(target.Mind.CombatOpponentNpcId, Is.Not.Null,
            "Стоящая без приказа обязана ОТВЕЧАТЬ на удары — это правило Кенши " +
            "и штатный §109, ручной режим его не отменяет.");
    }

    [Test]
    public void ManualNpcUnderOrdersWalksThroughTheBeating()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var target = Colonist(world);
        TakeControl(engine, target);

        // Далёкая цель: приказ обязан быть ЕЩЁ НЕ ДОИГРАН к концу теста,
        // иначе «цель None» читалось бы как срыв, а это просто «дошла».
        var destination = NearbyFreeJunction(world, target, minTiles: 4);
        engine.Commands.Enqueue(new MoveToCommand(target.Id, destination.WorldPosition));
        engine.Step();
        Assert.That(target.Plan.Status, Is.EqualTo(PlanStatus.Active));

        AttackerNextTo(engine, target);
        Step(engine, MediumTicks * 2);

        Assert.That(target.Mind.CombatOpponentNpcId, Is.Null,
            "⭐ Правило Кенши: с активным приказом она НЕ оборачивается на " +
            "удары — идёт и терпит. Иначе игрок не может вывести раненую из боя.");
        Assert.That(target.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder),
            "Удары отобрали у неё приказ игрока.");
        Assert.That(target.IsFighting, Is.False,
            "Идущая по приказу не встаёт в боевую стойку.");
    }

    [Test]
    public void ManualNpcNeverFleesOnItsOwn()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var target = Colonist(world);
        TakeControl(engine, target);
        target.Health = 0.2f; // ниже порога, на котором ИИ убежал бы домой

        AttackerNextTo(engine, target);

        Step(engine, MediumTicks * 4);

        Assert.That(target.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Flee),
            "Ручная убежала сама — отступление обязано оставаться решением игрока.");
    }

    [Test]
    public void AttackOrderChasesAndEngages()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var attacker = Colonist(world);
        TakeControl(engine, attacker);

        var victim = world.Entities.Npcs.Values.First(n => !n.Id.Equals(attacker.Id));
        // Поставить жертву в паре шагов, чтобы погоня была короткой.
        var near = SpatialQueries.GetPassableNeighbors(world, attacker.CurrentJunction!.Value).First();
        victim.CurrentJunction = near;
        victim.Tile = world.Junctions.Items[near].Tiles[0];
        victim.Position = world.Junctions.Items[near].WorldPosition;

        engine.Commands.Enqueue(new AttackNpcCommand(attacker.Id, victim.Id));
        engine.Step();

        Assert.That(attacker.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerAttack));
        Assert.That(attacker.Mind.ManualAttackNpcId, Is.EqualTo(victim.Id));

        Step(engine, MediumTicks * 6);

        Assert.That(attacker.Mind.CombatOpponentNpcId, Is.EqualTo(victim.Id),
            "Приказ атаковать не довёл до сцепки. Если пара распадается каждый " +
            "проход — значит PlayerAttack забыли внести в списки обоснований " +
            "§109 (метла призрачных пар), и она бьёт воздух.");
        Assert.That(attacker.IsFighting, Is.True);
    }

    [Test]
    public void AttackOrderEndsWhenTheTargetIsDown()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var attacker = Colonist(world);
        TakeControl(engine, attacker);

        var victim = world.Entities.Npcs.Values.First(n => !n.Id.Equals(attacker.Id));
        engine.Commands.Enqueue(new AttackNpcCommand(attacker.Id, victim.Id));
        engine.Step();

        victim.Health = 0f;
        Step(engine, MediumTicks * 3);

        Assert.That(attacker.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
            "Цель мертва — приказ окончен, и она обязана стоять и ждать нового.");
        Assert.That(attacker.Mind.ManualAttackNpcId, Is.Null);
    }

    // ── 5. Сейв ──────────────────────────────────────────────────────────

    [Test]
    public void ManualFlagAndPendingOrderSurviveASave()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        var destination = NearbyFreeJunction(world, npc, minTiles: 3);
        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        buffer.Position = 0;
        var reloadedWorld = new HexLive.Simulation.Bootstrap.WorldStateFactory()
            .Create(HexLive.Simulation.Bootstrap.PrototypeWorldDefinitionFactory.Create(12345));
        using (var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(reloadedWorld, reader);
        }

        var reloaded = reloadedWorld.Entities.Npcs[npc.Id];
        Assert.That(reloaded.Mind.ManualControl, Is.True,
            "Под чьим управлением персонаж — обязано пережить сохранение.");
        Assert.That(reloaded.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder),
            "Недошедший приказ обязан продолжиться после загрузки: план едет в " +
            "блобе целиком, складывать цель незачем.");
        Assert.That(reloaded.Plan.TargetJunctionId, Is.EqualTo(destination.Id));
    }

    [Test]
    public void AttackOrderIsFoldedAwayBySave()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        var victim = world.Entities.Npcs.Values.First(n => !n.Id.Equals(npc.Id));
        engine.Commands.Enqueue(new AttackNpcCommand(npc.Id, victim.Id));
        engine.Step();
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerAttack));

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        buffer.Position = 0;
        var reloadedWorld = new HexLive.Simulation.Bootstrap.WorldStateFactory()
            .Create(HexLive.Simulation.Bootstrap.PrototypeWorldDefinitionFactory.Create(12345));
        using (var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(reloadedWorld, reader);
        }

        var reloaded = reloadedWorld.Entities.Npcs[npc.Id];
        Assert.That(reloaded.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
            "Сцепка боя в блоб не едет, значит и цель атаки обязана сложиться — " +
            "иначе после загрузки она стоит в боевой цели без противника.");
        Assert.That(reloaded.Mind.ManualControl, Is.True,
            "Сложить приказ — не то же самое, что вернуть персонажа ИИ.");
    }

    // ── 6. Нейтральность ─────────────────────────────────────────────────

    [Test]
    public void WorldWithoutASingleManualNpcIsUntouched()
    {
        // ⭐ Критерий приёмки §118: пока никем не управляют вручную, мир обязан
        // считаться в точности как до фичи. Порядок float-операций здесь и есть
        // поведение (golden_trace.sh ловит то же самое на живом острове).
        var reference = TestWorld.CreateEngine(4242);
        var withFeature = TestWorld.CreateEngine(4242);

        Spec118.ManualControlEnabled = false;
        try
        {
            for (var i = 0; i < 400; i++)
            {
                reference.Step();
            }
        }
        finally
        {
            Spec118.ManualControlEnabled = true;
        }

        for (var i = 0; i < 400; i++)
        {
            withFeature.Step();
        }

        foreach (var expected in reference.World.Entities.Npcs.Values)
        {
            var actual = withFeature.World.Entities.Npcs[expected.Id];
            Assert.That(actual.Position.X, Is.EqualTo(expected.Position.X).Within(0f),
                $"NPC{expected.Id.Value} разошлась по X — §118 меняет мир, в " +
                "котором нет ни одного ручного персонажа.");
            Assert.That(actual.Position.Y, Is.EqualTo(expected.Position.Y).Within(0f));
            Assert.That(actual.Mind.CurrentGoal, Is.EqualTo(expected.Mind.CurrentGoal));
            Assert.That(actual.Health, Is.EqualTo(expected.Health).Within(0f));
        }
    }
}

}
