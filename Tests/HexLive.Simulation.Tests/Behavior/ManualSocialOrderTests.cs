using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §121.9: социальные ручные приказы — Поговорить, Помочь, Медицинская помощь,
/// Протез.
/// Проверяются обещания, данные игроку:
/// <list type="number">
/// <item>принятый приказ носит РОДНУЮ цель (Socialize/Aid, прецедент §138) и
/// исполняется ШТАТНОЙ системой — рукопожатие, реплики, расход припаса те же,
/// что у автономных;</item>
/// <item>цель доживает до исполнения: sweep §121.6 r2 её не сносит (грабля
/// §138 «приказ принят и убит в тот же тик»), а по завершении
/// ManualOrderSystem возвращает её в None;</item>
/// <item>отказ называет причину (TargetSelf/NoSupplies/NoLimbDamage…).</item>
/// </list>
/// </summary>
public sealed class ManualSocialOrderTests
{
    private static NPCState[] TwoColonists(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .Take(2)
            .ToArray();

    private static void Step(SimulationEngine engine, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            engine.Step();
        }
    }

    private static void TakeControl(SimulationEngine engine, NPCState npc)
    {
        // Сытая: узкий аукцион §121.6 молчит и не подменяет проверяемую цель.
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true));
        engine.Step();
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
        person.Tile = destination.Tiles.Count > 0 ? destination.Tiles[0] : person.Tile;
        person.Fragment = destination.Fragment;
        person.Position = destination.WorldPosition;
        person.CurrentJunction = destinationId;
        SpatialMutations.MoveEntityToTile(world, person.Id, previousTile, person.Tile);
        SpatialMutations.OccupyJunction(world, destinationId, person.Id);
    }

    // ── Поговорить ───────────────────────────────────────────────────────

    [Test]
    public void TalkToOrderRunsTheStandardTalkAndSweepsBackToNone()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var npc = pair[0];
        var target = pair[1];
        TakeControl(engine, npc);
        TakeControl(engine, target);
        PlaceOnFreeNeighbor(world, target, npc);

        var admission = ManualCommandExecutor.Apply(
            world, new TalkToCommand(npc.Id, target.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"TalkTo отклонён: {admission.Reason}");
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Socialize),
                "Приказ поговорить обязан носить РОДНУЮ цель Socialize (§138), " +
                "чтобы его исполнял штатный RunTalk.");
            Assert.That(target.Mind.PendingTalkFrom, Is.EqualTo(npc.Id),
                "Цель не заклеймлена — рукопожатие §28.8 не начнётся.");
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        });

        var sawTalk = false;
        for (var i = 0; i < 600 && (!sawTalk || npc.Mind.CurrentGoal != GoalType.None); i++)
        {
            engine.Step();
            sawTalk |= npc.Execution.Status == ExecutionStatus.InProgress &&
                npc.Execution.CurrentInteraction == InteractionType.Talk;
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawTalk, Is.True,
                "До InteractionType.Talk дело так и не дошло — приказ либо снесён " +
                "sweep'ом (грабля §138), либо не исполняется штатной системой.");
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
                "Доигранный приказ обязан вернуть цель в None (ManualOrderSystem), " +
                "иначе ручная застревает: ни авто-нужд, ни таймаута §121.7.");
        });
    }

    [Test]
    public void TalkToSelfIsRejected()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TwoColonists(world)[0];
        TakeControl(engine, npc);

        var admission = ManualCommandExecutor.Apply(
            world, new TalkToCommand(npc.Id, npc.Id));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("TargetSelf"));
    }

    // ── Помочь ───────────────────────────────────────────────────────────

    [Test]
    public void AidOrderFeedsTheStarvingWardWithTheHelpersSupplies()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var helper = pair[0];
        var ward = pair[1];
        TakeControl(engine, helper);
        TakeControl(engine, ward);
        PlaceOnFreeNeighbor(world, ward, helper);

        helper.Inventory.Items.Add(new ItemInstance("food.meat_cooked"));
        ward.Needs.Hunger = 0.95f;

        var admission = ManualCommandExecutor.Apply(
            world, new AidPersonCommand(helper.Id, ward.Id, AidKind.Feed));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"Aid отклонён: {admission.Reason}");
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.Aid),
                "Приказ помочь обязан носить РОДНУЮ цель Aid (§53/§138).");
            Assert.That(ward.Mind.PendingAidFrom, Is.EqualTo(helper.Id),
                "Подопечная не заклеймлена — она не будет ждать помощь.");
        });

        for (var i = 0; i < 800 && ward.Needs.Hunger > 0.6f; i++)
        {
            engine.Step();
        }

        Assert.That(ward.Needs.Hunger, Is.LessThan(0.6f),
            "Помощь так и не дошла: голод подопечной не снизился — RunAid не " +
            "отработал приказ (§53.7 расход припаса помощницы).");
    }

    [Test]
    public void AidWithoutSuppliesIsRejected()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var helper = pair[0];
        var ward = pair[1];
        TakeControl(engine, helper);
        PlaceOnFreeNeighbor(world, ward, helper);
        ward.Needs.Hunger = 0.95f;
        // Опустошить съедобное: готовую еду и кокосы (их AidSupply тоже
        // считает платёжным средством при наличии клинка).
        while (helper.Inventory.FindFirstFood(world.Content) is { } food)
        {
            if (!helper.Inventory.Items.Remove(food))
            {
                break;
            }
        }

        helper.Inventory.Items.RemoveAll(i =>
            i.DefinitionId == ContentIds.Coconut ||
            i.DefinitionId == ContentIds.CoconutPierced ||
            i.DefinitionId == ContentIds.CoconutOpen);

        var admission = ManualCommandExecutor.Apply(
            world, new AidPersonCommand(helper.Id, ward.Id, AidKind.Feed));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NoSupplies"),
            "Отказ обязан называть настоящую причину: помощь стоит припаса (§53.7).");
    }

    // §53.9 (баг #240): БЕСПОМОЩНУЮ — ту, что сама не поест и не попьёт —
    // кормят и поят именно тем, что выбрал игрок. Живая переоценка §53.3 по
    // прибытии отвечала за лежачую Treat (у неё кровь в полу и открытая рана),
    // и приказ «Накормить» молча умирал на «нечем перевязать», не тронув голода.
    [Test]
    public void AidFeedsAComatoseWardTheLiveAssessmentWouldRatherBandage()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var helper = pair[0];
        var ward = pair[1];
        TakeControl(engine, helper);
        TakeControl(engine, ward);
        PlaceOnFreeNeighbor(world, ward, helper);

        // Еда есть, перевязать нечем — ровно та помощница, на которой приказ
        // и обрывался.
        helper.Inventory.Items.Add(new ItemInstance("food.meat_cooked"));
        helper.Inventory.Items.RemoveAll(i => i.DefinitionId.Contains("bandage"));

        ward.Wounds.Clear();
        ward.Wounds.Add(new WoundState
        {
            Id = 240, Zone = BodyPart.ArmL, Severity = 0.10f,
            Heal01 = 0f, Clot01 = 0f, Stabilized = false, BleedFactor = 1f, Seed = 240
        });
        ward.Needs.Blood = 0.30f;
        ward.Needs.Hunger = 0.60f;
        ward.Needs.Energy = 0f;
        NeedsDecaySystem.EnterComa(world, ward, ComaCause.Exhaustion);

        Assert.That(AidAssessment.Assess(ward, world.Tick, out _),
            Is.EqualTo(AidKind.Treat),
            "Сцена перестала быть той, ради которой тест написан: §53.3 обязана " +
            "хотеть здесь ПЕРЕВЯЗКУ, иначе приказ «Накормить» ничего не проверяет.");

        var admission = ManualCommandExecutor.Apply(
            world, new AidPersonCommand(helper.Id, ward.Id, AidKind.Feed));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"Aid отклонён: {admission.Reason}");

        var fedHer = false;
        for (var i = 0; i < 400 && ward.Needs.Hunger > 0.35f; i++)
        {
            ward.Needs.Energy = 0f; // держим её без сознания весь поход
            engine.Step();
            fedHer |= helper.Execution.CurrentInteraction == InteractionType.FeedOther;
        }

        Assert.Multiple(() =>
        {
            Assert.That(fedHer, Is.True,
                "Помощница так и не начала кормить: приказ игрока переигран " +
                "переоценкой §53.3 и оборван на «нечем перевязать».");
            Assert.That(ward.Needs.Hunger, Is.LessThan(0.35f),
                "Голод лежачей не снизился — еда до неё не дошла (§53.7).");
        });
    }

    // §53.9 (баг #240): у беспомощной нужда не обязана дорасти до §53.3-шного
    // порога страдания. Жажда 0.4 у той, кто сама не попьёт, — это забота
    // игрока; автономная формула здесь отвечает None, и приказ «Напоить»
    // обрывался по прибытии как «ей уже не нужна помощь».
    [Test]
    public void AidHydratesAComatoseWardBelowTheAutonomousSufferingThreshold()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var helper = pair[0];
        var ward = pair[1];
        TakeControl(engine, helper);
        TakeControl(engine, ward);
        PlaceOnFreeNeighbor(world, ward, helper);

        helper.BottleWater = WaterKind.Rain;
        helper.BottleCharges = 2;

        ward.Wounds.Clear();
        ward.Needs.Blood = 1f;
        ward.Health = 1f;
        ward.Needs.Stress = 0f;
        ward.Needs.Hunger = 0.10f;
        ward.Needs.Thirst = 0.40f;
        ward.Needs.Energy = 0f;
        NeedsDecaySystem.EnterComa(world, ward, ComaCause.Exhaustion);

        Assert.That(AidAssessment.Assess(ward, world.Tick, out _),
            Is.EqualTo(AidKind.None),
            "Сцена перестала быть той, ради которой тест написан: §53.3 обязана " +
            "здесь молчать, иначе приказ «Напоить» ничего не проверяет.");

        var admission = ManualCommandExecutor.Apply(
            world, new AidPersonCommand(helper.Id, ward.Id, AidKind.Hydrate));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"Aid отклонён: {admission.Reason}");

        var gaveHerWater = false;
        for (var i = 0; i < 400 && ward.Needs.Thirst >= 0.40f; i++)
        {
            ward.Needs.Energy = 0f;
            engine.Step();
            gaveHerWater |= helper.Execution.CurrentInteraction == InteractionType.HydrateOther;
        }

        Assert.Multiple(() =>
        {
            Assert.That(gaveHerWater, Is.True,
                "Помощница так и не начала поить: приказ игрока оборван " +
                "переоценкой §53.3 как «помощь больше не нужна».");
            Assert.That(ward.Needs.Thirst, Is.LessThan(0.40f),
                "Жажда лежачей не снизилась — вода до неё не дошла (§53.7).");
        });
    }

    [Test]
    public void AidToOutsiderIsRejectedBeforeSuppliesAreSpent()
    {
        var engine = TestWorld.CreateEngine(1461210);
        var world = engine.World;
        var helper = TwoColonists(world)[0];
        var outsider = world.Entities.Npcs.Values.Single(n =>
            n.Faction == Faction.Outsiders);
        TakeControl(engine, helper);

        var admission = ManualCommandExecutor.Apply(
            world, new AidPersonCommand(helper.Id, outsider.Id, AidKind.Feed));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(admission.Reason, Is.EqualTo("NotAlly"));
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        });
    }

    // ── Медицинская помощь / протез ──────────────────────────────────────

    [Test]
    public void MedicalAidTreatsAnOutsidersOpenWound()
    {
        var engine = TestWorld.CreateEngine(1461210);
        var world = engine.World;
        var medic = TwoColonists(world)[0];
        var patient = world.Entities.Npcs.Values.Single(n =>
            n.Faction == Faction.Outsiders);
        TakeControl(engine, medic);
        PlaceOnFreeNeighbor(world, patient, medic);
        // Даже сильный голод не должен превращать явную «Медицинскую помощь»
        // в кормление: меню обычной помощи для этого остаётся отдельно.
        patient.Needs.Hunger = 0.95f;
        patient.Needs.Thirst = 0f;
        patient.Wounds.Clear();
        patient.Wounds.Add(new WoundState
        {
            Id = 1219,
            Zone = BodyPart.ArmL,
            Severity = 0.4f,
            Heal01 = 0f,
            Clot01 = 0f,
            Stabilized = false,
            BleedFactor = 1f,
            Seed = 1219
        });
        medic.Inventory.Items.Add(MedicalSupplyMath.CreateBandage(herbal: false));

        var admission = ManualCommandExecutor.Apply(
            world, new MedicalAidCommand(medic.Id, patient.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
            Assert.That(medic.Mind.CurrentGoal, Is.EqualTo(GoalType.Aid));
        });

        for (var i = 0; i < 800 && !patient.Wounds[0].Stabilized; i++)
        {
            engine.Step();
        }

        Assert.That(patient.Wounds[0].Stabilized, Is.True,
            "Явная медицинская помощь должна исполняться и для чужака, а не " +
            "отменяться дипломатическим гейтом по прибытии.");
    }

    [Test]
    public void MedicalAidChoosesSplintWithoutAskingThePlayerForAType()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var medic = pair[0];
        var patient = pair[1];
        TakeControl(engine, medic);
        PlaceOnFreeNeighbor(world, patient, medic);
        patient.Wounds.Clear();
        patient.Body.Parts[BodyPart.LegR] = 0.1f;
        patient.Body.Condition(BodyPart.LegR).BluntDamage = 0.9f;
        medic.Inventory.Items.Add(ContentIds.Splint);

        var admission = ManualCommandExecutor.Apply(
            world, new MedicalAidCommand(medic.Id, patient.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
            Assert.That(medic.Mind.CurrentGoal, Is.EqualTo(GoalType.Splint));
        });
    }

    [Test]
    public void MedicalAidNamesTheMissingBandage()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var medic = pair[0];
        var patient = pair[1];
        TakeControl(engine, medic);
        PlaceOnFreeNeighbor(world, patient, medic);
        medic.Inventory.Items.RemoveAll(i => i.DefinitionId == ContentIds.Bandage);
        patient.Wounds.Clear();
        patient.Wounds.Add(new WoundState
        {
            Id = 1220,
            Zone = BodyPart.Torso,
            Severity = 0.3f,
            Heal01 = 0f,
            Clot01 = 0f,
            Stabilized = false,
            BleedFactor = 1f,
            Seed = 1220
        });

        var admission = ManualCommandExecutor.Apply(
            world, new MedicalAidCommand(medic.Id, patient.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(admission.Reason, Is.EqualTo("NoBandage"));
            Assert.That(medic.Execution.LastSocialCueKind,
                Is.EqualTo("MedicalAidRejected"),
                "Нехватка должна отвечать жёлтым предупреждением над врачом.");
        });
    }

    [Test]
    public void TreatLimbsOnHealthyTargetIsRejectedAsNoDamage()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = TwoColonists(world);
        var medic = pair[0];
        var patient = pair[1];
        TakeControl(engine, medic);
        PlaceOnFreeNeighbor(world, patient, medic);

        var admission = ManualCommandExecutor.Apply(
            world, new TreatLimbsCommand(medic.Id, patient.Id));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NoLimbDamage"),
            "У здоровой пациентки отказ обязан быть «нечего лечить», а не " +
            "молчание и не враньё про припасы.");
    }
}

}
