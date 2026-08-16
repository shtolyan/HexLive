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
/// §121: ручное управление. Проверяется не «есть флаг», а обещания, данные
/// игроку:
/// <list type="number">
/// <item>под ручным управлением никто, кроме игрока, целей ей не ставит —
/// кроме узкого аукциона авто-нужд §121.6 (еда и питьё без приказа);</item>
/// <item>приказ доходит до штатных систем и исполняется ими, а не второй
/// копией симуляции;</item>
/// <item>самозащита §121.2: атакованная бросает приказ и дерётся — всегда;</item>
/// <item>выключили тумблер — автономия вернулась целиком; забытая на
/// §121.7-таймауте возвращается сама.</item>
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
        // §121.6: голодная ручная сама берёт цель еды прямо на тике включения
        // режима. Тестам, которые проверяют НЕ авто-нужды, нужен сытый
        // персонаж — иначе каждый ассерт «цель None» ловит законный GetFood.
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
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

    private static void PlaceOnFreeNeighbor(WorldState world, NPCState person, NPCState anchor)
    {
        var destinationId = SpatialQueries.GetPassableNeighbors(
                world, anchor.CurrentJunction!.Value)
            .First(id => SpatialQueries.IsJunctionFree(world, id));
        var destination = world.Junctions.Items[destinationId];
        if (person.CurrentJunction is { } previousJunction)
        {
            SpatialMutations.FreeJunction(world, previousJunction, person.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previousJunction, person.Id);
        }

        var previousTile = person.Tile;
        person.Tile = destination.Tiles[0];
        person.Fragment = destination.Fragment;
        person.Position = destination.WorldPosition;
        person.CurrentJunction = destinationId;
        SpatialMutations.MoveEntityToTile(world, person.Id, previousTile, person.Tile);
        SpatialMutations.OccupyJunction(world, destinationId, person.Id);
    }

    // ── 1. Автономия выключена ───────────────────────────────────────────

    [Test]
    public void ManualNpcNeverTakesAGoalFromTheAuction()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonist(engine.World);
        TakeControl(engine, npc);

        // Нужды удовлетворены: даже узкий аукцион §121.6 обязан молчать, а
        // большой закрыт совсем — никакие стирки, стройки и разговоры.
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;

        Step(engine, MediumTicks * 8);

        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
            "Аукцион поставил цель персонажу под ручным управлением — значит, " +
            "игрок им на самом деле не управляет.");
        Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active),
            "Планировщик построил план ручной колонистке.");
    }

    [Test]
    public void HungryManualNpcAutoEatsButTakesNothingElse()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonist(engine.World);
        TakeControl(engine, npc);

        // §121.6: еда в рюкзаке + сильный голод → без приказа сама ест.
        npc.Inventory.Items.Add(new ItemInstance("food.meat_cooked"));
        npc.Needs.Hunger = 0.95f;
        npc.Needs.Thirst = 0f;

        var sawEat = false;
        for (var i = 0; i < MediumTicks * 12; i++)
        {
            engine.Step();
            var goal = npc.Mind.CurrentGoal;
            sawEat |= goal == GoalType.Eat;
            Assert.That(
                goal is GoalType.None or GoalType.Eat or GoalType.Drink
                     or GoalType.GetFood or GoalType.GetWater,
                Is.True,
                $"Ручной без приказа взял цель {goal} — авто-аукцион §121.6 " +
                "разрешает только еду и питьё.");
        }

        Assert.That(sawEat, Is.True,
            "Голодная ручная с едой в рюкзаке так и не поела — авто-нужды " +
            "§121.6 не работают.");
        Assert.That(npc.Needs.Hunger, Is.LessThan(0.95f),
            "Цель Eat была, а голод не снизился — план не исполнился.");
    }

    [Test]
    public void PlayerOrderOverridesAutoNeedGoal()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        npc.Inventory.Items.Add(new ItemInstance("food.meat_cooked"));
        npc.Needs.Hunger = 0.95f;
        npc.Needs.Thirst = 0f;

        // Один средний проход — авто-аукцион выбирает еду. Больше не шагаем:
        // короткая еда успела бы доиграться, и прекондиция стала бы гонкой.
        Step(engine, 5);
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Eat),
            "Прекондиция: авто-цель еды не выбралась.");

        var destination = NearbyFreeJunction(world, npc, minTiles: 3);
        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();

        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder),
            "Приказ игрока обязан перебивать авто-цель §121.6 немедленно.");
    }

    // ── 1b. §121.6 r2: авто-нужды берутся ТОЛЬКО из своего рюкзака ───────
    //
    // Обещание игрока: под ручным управлением она стоит и ждёт приказов.
    // Съесть то, что уже лежит у неё в рюкзаке, — не автономия (она никуда
    // не идёт и ничего не выбирает в мире); а вот пойти за кокосом, к
    // водосборнику или к костру — это ровно та самая автономия, ради
    // выключения которой существует §121.

    /// <summary>
    /// Кокосы вокруг неё — соблазн, на который старая версия шла. Кладём на
    /// ВСЕ проходимые соседние узлы: один сосед — это гонка с проголодавшейся
    /// соседкой-ИИ, и утащенный ею кокос превратил бы тест в пустой (есть
    /// стало бы просто нечего, и «стоит» доказывало бы не то).
    /// </summary>
    private static void DropAroundHer(WorldState world, NPCState npc, string definitionId)
    {
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(
                     world, npc.CurrentJunction!.Value))
        {
            var junction = world.Junctions.Items[neighbor];
            if (junction.Tiles.Count == 0)
            {
                continue;
            }

            WorldObjectMutations.SpawnObject(
                world, definitionId, new FragmentId(1), junction.Tiles[0], neighbor);
        }
    }

    /// <summary>Видела ли она соблазн — иначе замер «стоит» ничего не значит.</summary>
    private static bool SeesReachable(NPCState npc, string definitionId) =>
        npc.Perception.Objects.Any(o => o.DefinitionId == definitionId && o.IsReachable);

    /// <summary>
    /// Держит нужду на «остро, но не смертельно»: латч голодания/обезвоживания
    /// поднял бы §60/§105 и подменил бы предмет замера.
    /// </summary>
    private static void HoldNeeds(NPCState npc, float hunger, float thirst)
    {
        npc.Needs.Hunger = hunger;
        npc.Needs.Thirst = thirst;
    }

    [Test]
    public void StarvingManualNpcWithAnEmptyPackWaitsInsteadOfWalkingToGroundFood()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        npc.Inventory.Items.Clear(); // пустой рюкзак: ни еды, ни лезвия

        // Готовые к еде кокосы лежат в шаге — старый §121.6 честно шёл за ними.
        DropAroundHer(world, npc, ContentIds.CoconutOpen);
        var startTile = npc.Tile;

        var sawTemptation = false;
        for (var i = 0; i < MediumTicks * 8; i++)
        {
            HoldNeeds(npc, 0.9f, 0f);
            engine.Step();
            sawTemptation |= SeesReachable(npc, ContentIds.CoconutOpen);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
                $"Ручная с ПУСТЫМ рюкзаком взяла цель {npc.Mind.CurrentGoal} — " +
                "голод не даёт права уходить за едой в мир, она ждёт приказа.");
        }

        Assert.That(npc.Tile, Is.EqualTo(startTile),
            "Ручная пошла за едой сама — приказа на это не было.");
        Assert.That(sawTemptation, Is.True,
            "Прекондиция: она обязана была ВИДЕТЬ достижимую еду — иначе " +
            "тест проходит вхолостую (есть было просто нечего).");
    }

    [Test]
    public void DehydratedManualNpcWithAnEmptyPackWaitsInsteadOfWalkingToWater()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        npc.Inventory.Items.Clear();
        npc.BottleWater = WaterKind.None;
        npc.BottleCharges = 0;

        // Продырявленный кокос с водой пьётся без лезвия — самый дешёвый
        // источник, к которому старая версия уходила сама.
        DropAroundHer(world, npc, ContentIds.CoconutPierced);
        var startTile = npc.Tile;

        var sawTemptation = false;
        for (var i = 0; i < MediumTicks * 8; i++)
        {
            HoldNeeds(npc, 0f, 0.9f);
            engine.Step();
            sawTemptation |= SeesReachable(npc, ContentIds.CoconutPierced);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
                $"Ручная с пустым рюкзаком взяла цель {npc.Mind.CurrentGoal} — " +
                "жажда не открывает ей поход к воде без приказа.");
        }

        Assert.That(npc.Tile, Is.EqualTo(startTile),
            "Ручная пошла за водой сама.");
        Assert.That(sawTemptation, Is.True,
            "Прекондиция: достижимый источник воды обязан был попасть ей в " +
            "восприятие — иначе замер «стоит» ничего не доказывает.");
    }

    [Test]
    public void GroundFoodAndSpitRoastAreNotHerOwnPack()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        npc.Inventory.Items.Clear();

        // Два самых заманчивых мировых источника разом: готовый кокос под
        // ногами и жареное мясо на вертеле (§54.17 — старый Eat уходил к нему
        // даже с полным рюкзаком).
        DropAroundHer(world, npc, ContentIds.CoconutOpen);
        foreach (var fire in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(fire.DefinitionId, out var def) &&
                def.Tags.Contains("Campfire"))
            {
                fire.Contents.Add(new ItemInstance(ContentIds.MeatCooked));
            }
        }

        for (var i = 0; i < MediumTicks * 8; i++)
        {
            HoldNeeds(npc, 0.9f, 0f);
            engine.Step();
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
                "Еда в МИРЕ (земля, вертел) — не её рюкзак: за ней ходят " +
                "только по приказу.");
        }

        // ⭐ И обратная половина: тот же кокос, но В РЮКЗАКЕ — законный обед.
        // Разделяет случаи именно владение, а не «кокосы запрещены».
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));
        var ate = false;
        for (var i = 0; i < MediumTicks * 12 && !ate; i++)
        {
            HoldNeeds(npc, 0.9f, 0f);
            engine.Step();
            ate |= npc.Mind.CurrentGoal == GoalType.Eat;
        }

        Assert.That(ate, Is.True,
            "Тот же кокос, положенный ей в рюкзак, обязан съедаться без " +
            "приказа — иначе слайс запретил не поход, а саму еду.");
    }

    [Test]
    public void ManualNpcStillDrinksFromHerOwnBottle()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        npc.BottleWater = WaterKind.Rain;
        npc.BottleCharges = SimBalance.BottleCapacity;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0.9f;

        // Минимум по ходу замера, а не значение в конце: жажда снова растёт
        // после глотка, и «сколько её сейчас» ничего не доказывает.
        var drank = false;
        var lowestThirst = npc.Needs.Thirst;
        for (var i = 0; i < MediumTicks * 12; i++)
        {
            engine.Step();
            drank |= npc.Mind.CurrentGoal == GoalType.Drink;
            lowestThirst = System.Math.Min(lowestThirst, npc.Needs.Thirst);
        }

        Assert.That(drank, Is.True,
            "Своя полная фляга — питьё на месте, а не поход: §121.6 обязан её " +
            "разрешать.");
        Assert.That(lowestThirst, Is.LessThan(0.9f),
            "Цель Drink была, а жажда ни разу не упала — план не исполнился.");
    }

    [Test]
    public void ActivePlayerAttackIsNotReplacedByHungerOrThirst()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var attacker = Colonist(world);
        TakeControl(engine, attacker);

        var victim = world.Entities.Npcs.Values.First(n => !n.Id.Equals(attacker.Id));
        var near = SpatialQueries.GetPassableNeighbors(
            world, attacker.CurrentJunction!.Value).First();
        victim.CurrentJunction = near;
        victim.Tile = world.Junctions.Items[near].Tiles[0];
        victim.Position = world.Junctions.Items[near].WorldPosition;

        engine.Commands.Enqueue(new AttackNpcCommand(attacker.Id, victim.Id));
        engine.Step();
        Assert.That(attacker.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerAttack));

        // Полный рюкзак И острые нужды: приказ игрока не уступает авто-нужде.
        attacker.Inventory.Items.Add(new ItemInstance("food.meat_cooked"));
        attacker.BottleWater = WaterKind.Rain;
        attacker.BottleCharges = SimBalance.BottleCapacity;

        // Замеряется ровно одно: НУЖДА не подменяет приказ. Цель по ходу боя
        // может на такт уйти в None (самозащита §121.2 сносит план, а
        // ManualOrderSystem защёлкивает пару обратно средним проходом) — это
        // законно и к §121.6 отношения не имеет. А вот `Eat`/`Drink` посреди
        // приказа означали бы, что узкий аукцион пробил гейт занятости.
        for (var i = 0; i < MediumTicks * 4; i++)
        {
            HoldNeeds(attacker, 0.9f, 0.9f);
            engine.Step();
            Assert.That(attacker.Mind.CurrentGoal,
                Is.Not.EqualTo(GoalType.Eat).And.Not.EqualTo(GoalType.Drink),
                $"Приказ атаки подменён авто-нуждой {attacker.Mind.CurrentGoal} — " +
                "гейт занятости §121.6 пробит.");
            Assert.That(attacker.Mind.ManualAttackNpcId, Is.EqualTo(victim.Id),
                "Голод/жажда уронили сам приказ атаки — авто-нужда не имеет " +
                "права его отменять.");
        }
    }

    [Test]
    public void ManualNpcIsNeverAssignedReactiveRescueOrProstheticWork()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var helper = Colonist(world);
        TakeControl(engine, helper);
        var patient = world.Entities.Npcs.Values.First(n =>
            n.Id != helper.Id && FactionRelations.AreAllies(helper, n));

        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Plan.Status = npc.Id == helper.Id ? PlanStatus.Completed : PlanStatus.Active;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.PendingAidFrom = null;
        }

        patient.Mind.ComaCause = ComaCause.BloodLoss;
        patient.Health = System.Math.Max(0.2f, patient.Body.Mean());
        helper.Mind.ProstheticAidTargetId = patient.Id;
        helper.Mind.ProstheticAidPart = BodyPart.ArmL;

        new RescueSystem().Run(world);
        new ProstheticAidSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
                "Reactive aid bypassed Manual mode and assigned autonomous work.");
            Assert.That(helper.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
            Assert.That(patient.Mind.PendingAidFrom, Is.Not.EqualTo(helper.Id));
        });
    }

    [Test]
    public void TakingManualControlCancelsAnApproachingRescueClaim()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var helper = Colonist(world);
        var patient = world.Entities.Npcs.Values.First(n =>
            n.Id != helper.Id && FactionRelations.AreAllies(helper, n));

        helper.Mind.CurrentGoal = GoalType.Rescue;
        helper.Plan.Goal = GoalType.Rescue;
        helper.Plan.Status = PlanStatus.Active;
        helper.Plan.TargetAgentId = patient.Id;
        patient.Mind.PendingAidFrom = helper.Id;

        // Сытая: иначе авто-нужды §121.6 тут же поставят GetFood, и ассерты
        // «цель None, плана нет» ловят не отмену помощи, а законную еду.
        helper.Needs.Hunger = 0f;
        helper.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(helper.Id, true));
        engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(helper.Mind.ManualControl, Is.True);
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(helper.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
            Assert.That(patient.Mind.PendingAidFrom, Is.Null,
                "A cancelled automatic approach must not reserve the patient forever.");
        });
    }

    [Test]
    public void ReleasingControlBringsTheAuctionBack()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonist(engine.World);
        TakeControl(engine, npc);
        Step(engine, MediumTicks * 4);
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));

        // Голод ставится ПЕРЕД возвратом ИИ: под ручным §121.6 сам взял бы
        // цель еды, и «аукцион вернулся» перестало бы отличаться от «авто-нужды».
        npc.Needs.Hunger = 0.98f;
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
    public void MoveOrderInterruptingSleepWaitsForGetUpBeforeWalking()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        // Reproduce bug #95 without a presentation-only fake: the actor is in
        // the exact sim state exported as lying on a bed when the player gives
        // a move order. The order must be retained, but translation must wait
        // for the full authored GetUp window.
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Sleep;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + 100;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.Sleep;
        var sleepingPosition = npc.Position;
        var destination = NearbyFreeJunction(world, npc);

        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();

        Assert.That(npc.Execution.CurrentInteraction, Is.Null,
            "Ручной приказ обязан разбудить персонажа, а не оставить Sleep живым.");
        Assert.That(npc.Mind.WakeGraceUntilTick,
            Is.GreaterThanOrEqualTo(world.Tick + AiBalance.WakeGraceTicks - 1),
            "Прерванный сон не получил окно для GetUp.");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder),
            "Приказ игрока должен ждать подъёма, а не теряться.");

        var graceUntil = npc.Mind.WakeGraceUntilTick;
        while (world.Tick < graceUntil)
        {
            engine.Step();
            Assert.That(npc.Position, Is.EqualTo(sleepingPosition),
                "Персонаж начал скользить в лежачей позе до конца GetUp.");
        }

        Step(engine, MediumTicks * 4);
        Assert.That(npc.Position, Is.Not.EqualTo(sleepingPosition),
            "После завершения GetUp сохранённый приказ обязан начать движение.");
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

    [Test]
    public void ManualSleepUsesApproachJunctionAndStaysInBedUntilCancelled()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        npc.Needs.Energy = 1f;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;

        var bed = world.Entities.Objects.Values.First(o =>
            o.DefinitionId == ContentIds.BedBasic &&
            o.Variant == ContentIds.HutBedVariant && !o.IsOccupied);
        var bedAnchor = bed.Junctions[0];

        engine.Commands.Enqueue(new InteractCommand(npc.Id, bed.Id, InteractionType.Sleep));
        engine.Step();

        Assert.That(npc.Plan.TargetJunctionId, Is.Not.EqualTo(bedAnchor),
            "Manual sleep must reserve a standing approach point, not the bed footprint.");

        for (var i = 0; i < MediumTicks * 120 &&
             npc.Execution.CurrentInteraction != InteractionType.Sleep; i++)
        {
            engine.Step();
        }

        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep),
            "The manual order never reached the authored bed interaction.");
        var firstBlockEnd = npc.Execution.EndTick;
        while (world.Tick <= firstBlockEnd + 2)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep),
                "A fully rested manual character stood up after the first sleep block.");
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder));
            Assert.That(npc.Execution.EndTick, Is.GreaterThan(world.Tick),
                "Manual sleep was not re-armed as a persistent player order.");
        });

        engine.Commands.Enqueue(new StopCommand(npc.Id));
        engine.Step();
        Assert.That(npc.Execution.CurrentInteraction, Is.Null,
            "Stop must wake a character held in manual sleep.");
    }

    [Test]
    public void ManualCarrierCanMoveAndPutDownADeadBody()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var carrier = Colonist(world);
        TakeControl(engine, carrier);
        var victim = world.Entities.Npcs.Values.First(n => n.Id != carrier.Id);
        PlaceOnFreeNeighbor(world, victim, carrier);
        victim.Health = 0f;
        MobSystem.RemoveDeadNpc(world, victim.Id);
        var body = world.Entities.Corpses[victim.Id];
        var anchor = world.Entities.Objects.Values.Single(o =>
            o.DefinitionId == ContentIds.CorpseNpc && o.CurrentUser == victim.Id);

        engine.Commands.Enqueue(new CarryPersonCommand(carrier.Id, victim.Id));
        for (var i = 0; i < MediumTicks * 40 && carrier.CarriedNpcId is null; i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.EqualTo(victim.Id));
            Assert.That(body.CarriedByNpcId, Is.EqualTo(carrier.Id));
            Assert.That(anchor.Junctions, Is.Empty,
                "Старый якорь не должен позволять обыскивать унесённое тело издалека.");
        });

        var beforeMove = carrier.Position;
        var destinationId = SpatialQueries.GetPassableNeighbors(
                world, carrier.CurrentJunction!.Value)
            .First(id => SpatialQueries.IsJunctionFree(world, id));
        engine.Commands.Enqueue(new MoveToCommand(
            carrier.Id, world.Junctions.Items[destinationId].WorldPosition));
        for (var i = 0; i < MediumTicks * 10 && carrier.Position.Equals(beforeMove); i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(carrier.Position, Is.Not.EqualTo(beforeMove));
            Assert.That(carrier.CarriedNpcId, Is.EqualTo(victim.Id),
                "Обычный MoveTo не должен автоматически ронять тело.");
            Assert.That(body.Position, Is.EqualTo(carrier.Position));
        });

        engine.Commands.Enqueue(new PutDownPersonCommand(carrier.Id));
        engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.Null);
            Assert.That(body.CarriedByNpcId, Is.Null);
            Assert.That(anchor.Junctions, Has.Count.EqualTo(1));
            Assert.That(body.CurrentJunction, Is.EqualTo(anchor.Junctions[0]));
            Assert.That(anchor.Tile, Is.EqualTo(body.Tile));
        });
    }

    [Test]
    public void ManualCarrierCanPickUpAnUnconsciousLivingPerson()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var carrier = Colonist(world);
        TakeControl(engine, carrier);
        var patient = world.Entities.Npcs.Values.First(n => n.Id != carrier.Id);
        PlaceOnFreeNeighbor(world, patient, carrier);
        patient.Mind.ComaCause = ComaCause.Exhaustion;

        engine.Commands.Enqueue(new CarryPersonCommand(carrier.Id, patient.Id));
        for (var i = 0; i < MediumTicks * 40 && carrier.CarriedNpcId is null; i++)
        {
            engine.Step();
        }

        Assert.That(carrier.CarriedNpcId, Is.EqualTo(patient.Id));
        Assert.That(patient.CarriedByNpcId, Is.EqualTo(carrier.Id));

        engine.Commands.Enqueue(new PutDownPersonCommand(carrier.Id));
        engine.Step();
        Assert.That(patient.CarriedByNpcId, Is.Null);
        Assert.That(patient.CurrentJunction, Is.Not.Null,
            "Живой человек после PutDown должен снова получить лежачий якорь.");
    }

    // ── 3. Отказы: приказ, который нельзя выполнить ──────────────────────

    [TestCase(InterruptionCause.CombatVictim)]
    [TestCase(InterruptionCause.PathFailure)]
    public void AcceptedAdmissionIsDistinctFromLaterOrderInterruption(
        InterruptionCause cause)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        var destination = NearbyFreeJunction(world, npc, minTiles: 3);

        var previousTrace = SimTrace.Enabled;
        SimTrace.Enabled = true;
        try
        {
            var admission = ManualCommandExecutor.Apply(
                world, new MoveToCommand(npc.Id, destination.WorldPosition));

            Assert.Multiple(() =>
            {
                Assert.That(admission.Status,
                    Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
                Assert.That(admission.Order, Is.EqualTo("MoveTo"));
                Assert.That(admission.Reason, Is.Empty);
                Assert.That(HasTrace(
                    world, npc.Id, "ManualCommandAdmission", "Status=Accepted"),
                    Is.True,
                    "Admission must be a structured trace before any system can " +
                    "later interrupt the accepted order.");
            });

            Assert.That(
                PlanInterruption.TryAbort(world, npc, cause, "admission-status-test"),
                Is.True);
            Assert.That(HasTrace(
                world, npc.Id, "ManualOrderInterrupted", $"Cause={cause}"),
                Is.True,
                "An admitted order that later stops must name self-defence/path " +
                "as lifecycle cause instead of looking like command rejection.");
        }
        finally
        {
            SimTrace.Enabled = previousTrace;
        }
    }

    [Test]
    public void RejectedAdmissionCarriesReasonAndHasNoInterruptionTrace()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        engine.Step(); // initialize the NPC junction, but leave her under AI
        var destination = NearbyFreeJunction(world, npc);

        var previousTrace = SimTrace.Enabled;
        SimTrace.Enabled = true;
        try
        {
            var admission = ManualCommandExecutor.Apply(
                world, new MoveToCommand(npc.Id, destination.WorldPosition));

            Assert.Multiple(() =>
            {
                Assert.That(admission.Status,
                    Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
                Assert.That(admission.Order, Is.EqualTo("MoveTo"));
                Assert.That(admission.Reason, Is.EqualTo("NotManual"));
                Assert.That(HasTrace(
                    world, npc.Id, "ManualCommandAdmission", "Status=Rejected"),
                    Is.True);
                Assert.That(HasTrace(
                    world, npc.Id, "ManualCommandAdmission", "Reason=NotManual"),
                    Is.True);
                Assert.That(HasTrace(
                    world, npc.Id, "ManualOrderInterrupted", string.Empty),
                    Is.False,
                    "A command that never entered the plan has no lifecycle to interrupt.");
            });
        }
        finally
        {
            SimTrace.Enabled = previousTrace;
        }
    }

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
    public void ManualNpcUnderOrdersDropsThemAndFightsBack()
    {
        // §121.2 (правило Кенши ОТМЕНЕНО решением игрока): атакованная ручная
        // бросает текущий приказ и отвечает боем — независимо от того, стояла
        // она или шла. «Идёт и терпит побои» читалось как поломка.
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var target = Colonist(world);
        TakeControl(engine, target);

        // Далёкая цель: к моменту ударов приказ ещё активен.
        var destination = NearbyFreeJunction(world, target, minTiles: 4);
        engine.Commands.Enqueue(new MoveToCommand(target.Id, destination.WorldPosition));
        engine.Step();
        Assert.That(target.Plan.Status, Is.EqualTo(PlanStatus.Active));

        AttackerNextTo(engine, target);
        Step(engine, MediumTicks * 3);

        Assert.That(target.Mind.CombatOpponentNpcId, Is.Not.Null,
            "§121.2: атакованная ручная обязана ОТВЕТИТЬ — даже посреди приказа.");
        Assert.That(target.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.PlayerOrder),
            "Приказ обязан быть брошен: самозащита сносит его причиной CombatVictim.");
        Assert.That(target.IsFighting, Is.True,
            "Атакованная не встала в боевую стойку.");
    }

    // ── 4b. Политика §121.5: причины-«выборы» не сносят приказ ───────────

    [Test]
    public void ChoiceInterruptionsAreDeniedForOrderedManual()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        var destination = NearbyFreeJunction(world, npc, minTiles: 3);
        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));

        // Обход угрозы — «выбор»: политика обязана отказать и оставить план.
        Assert.That(
            PlanInterruption.TryAbort(world, npc, InterruptionCause.ThreatReroute, "test"),
            Is.False,
            "ThreatReroute снёс приказ ручной — дыра «обход зверя рвёт приказ» вернулась.");
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active),
            "Отказ политики обязан оставлять план нетронутым.");

        // Самозащита и игрок проходят всегда.
        Assert.That(
            PlanInterruption.TryAbort(world, npc, InterruptionCause.CombatVictim, "test"),
            Is.True,
            "Самозащита §121.2 обязана проходить у ручной.");
    }

    // ── 4c. §121.7: таймаут бездействия ──────────────────────────────────

    [Test]
    public void SparseWorldTicksDoNotExtendTheRealtimeLease()
    {
        var realtimeSeconds = 0d;
        var engine = TestWorld.CreateEngine(
            clock: new SimulationClock(() => realtimeSeconds));
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        engine.Clock.SetSpeed(0.1f); // server slow-speed floor
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;

        // Окно истекло — следующий средний проход обязан отпустить её под ИИ
        // с player-visible событием (молчаливый возврат читается как поломка).
        // Шагов ровно на один Medium-проход: кольцо событий держит ~11 тиков,
        // и лишние шаги вытесняют ManualControlExpired до ассерта.
        realtimeSeconds += Spec121.ManualIdleReleaseSeconds;
        Step(engine, 5);

        Assert.Multiple(() =>
        {
            Assert.That(world.Tick,
                Is.LessThan(Spec121.ManualIdleReleaseSeconds / engine.Settings.TickDeltaTime),
                "Precondition: the sparse-cadence case accidentally consumed the old tick budget.");
            Assert.That(npc.Mind.ManualControl, Is.False,
                "§121.7: sparse world ticks extended the real-time inactivity lease.");
            Assert.That(HasTrace(world, npc.Id, "ManualControlExpired", ""), Is.True,
                "Возврат по таймауту обязан быть виден игроку (ManualControlExpired).");
        });
    }

    [Test]
    public void FastForwardTicksDoNotPrematurelyExpireTheRealtimeLease()
    {
        var realtimeSeconds = 0d;
        var engine = TestWorld.CreateEngine(
            clock: new SimulationClock(() => realtimeSeconds));
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        engine.Clock.SetSpeed(200f); // server/operator fast-forward ceiling
        realtimeSeconds = Spec121.ManualIdleReleaseSeconds - 0.001d;

        // Drive the lease owner across more world ticks than the old 1200-tick
        // timeout, without advancing wall time. Calling only the focused medium
        // system keeps unrelated survival/combat behavior out of this clock test.
        var formerTickTimeout =
            (int)(Spec121.ManualIdleReleaseSeconds / engine.Settings.TickDeltaTime);
        var manualOrders = new ManualOrderSystem();
        for (var i = 0; i < formerTickTimeout + engine.Settings.MediumInterval; i++)
        {
            world.Tick++;
            if (world.Tick % engine.Settings.MediumInterval == 0)
            {
                manualOrders.Run(world);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(world.Tick, Is.GreaterThan(formerTickTimeout));
            Assert.That(realtimeSeconds,
                Is.LessThan(Spec121.ManualIdleReleaseSeconds));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Mind.ManualControl, Is.True,
                "Fast-forward/server tick speed consumed a real-time manual lease.");
        });
    }

    [Test]
    public void ActiveOrderBlocksTheIdleTimeout()
    {
        var realtimeSeconds = 0d;
        var engine = TestWorld.CreateEngine(
            clock: new SimulationClock(() => realtimeSeconds));
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);

        var destination = NearbyFreeJunction(world, npc, minTiles: 4);
        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));

        // Даже с «протухшим» окном активный приказ держит ручной режим:
        // таймер §121.7 стартует только ПОСЛЕ завершения приказа.
        realtimeSeconds += Spec121.ManualIdleReleaseSeconds * 2;
        engine.Step();

        Assert.That(npc.Mind.ManualControl, Is.True,
            "Таймаут отпустил ручную ПОСРЕДИ приказа — а обязан ждать его конца.");
    }

    [Test]
    public void OrderCompletionRestartsTheIdleWindow()
    {
        var realtimeSeconds = 0d;
        var engine = TestWorld.CreateEngine(
            clock: new SimulationClock(() => realtimeSeconds));
        var world = engine.World;
        var npc = Colonist(world);
        TakeControl(engine, npc);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;

        var destination = NearbyFreeJunction(world, npc, minTiles: 2);
        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();
        // Окно «протухает» во время похода — завершение приказа обязано
        // перезапустить его, а не отпустить её в момент прибытия.
        realtimeSeconds += Spec121.ManualIdleReleaseSeconds * 2;

        var arrived = false;
        for (var i = 0; i < 600 && !arrived; i++)
        {
            engine.Step();
            arrived = npc.Mind.CurrentGoal == GoalType.None &&
                npc.Plan.Status != PlanStatus.Active;
        }

        Assert.That(arrived, Is.True, "Прекондиция: приказ так и не доигрался.");
        Assert.That(npc.Mind.ManualControl, Is.True,
            "Поход длиннее таймаута «истёк» в момент прибытия — окно обязано " +
            "перезапускаться завершением приказа.");
        Assert.That(npc.Mind.ManualControlLeaseRenewedAtSeconds,
            Is.EqualTo(realtimeSeconds),
            "Lease не продлился на завершении приказа.");
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
        Assert.That(reloaded.Mind.ManualControlLeaseRenewedAtSeconds, Is.Null,
            "Монотонный process-relative lease не должен попадать в сейв; " +
            "после загрузки idle-проход начинает полное новое окно.");
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
        // ⭐ Критерий приёмки §121: пока никем не управляют вручную, мир обязан
        // считаться в точности как до фичи. Порядок float-операций здесь и есть
        // поведение (golden_trace.sh ловит то же самое на живом острове).
        var reference = TestWorld.CreateEngine(4242);
        var withFeature = TestWorld.CreateEngine(4242);

        Spec121.ManualControlEnabled = false;
        try
        {
            for (var i = 0; i < 400; i++)
            {
                reference.Step();
            }
        }
        finally
        {
            Spec121.ManualControlEnabled = true;
        }

        for (var i = 0; i < 400; i++)
        {
            withFeature.Step();
        }

        foreach (var expected in reference.World.Entities.Npcs.Values)
        {
            var actual = withFeature.World.Entities.Npcs[expected.Id];
            Assert.That(actual.Position.X, Is.EqualTo(expected.Position.X).Within(0f),
                $"NPC{expected.Id.Value} разошлась по X — §121 меняет мир, в " +
                "котором нет ни одного ручного персонажа.");
            Assert.That(actual.Position.Y, Is.EqualTo(expected.Position.Y).Within(0f));
            Assert.That(actual.Mind.CurrentGoal, Is.EqualTo(expected.Mind.CurrentGoal));
            Assert.That(actual.Health, Is.EqualTo(expected.Health).Within(0f));
        }
    }
}

}
