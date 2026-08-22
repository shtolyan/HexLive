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
/// §121.9: социальные ручные приказы — Поговорить, Помочь, Шина/протез.
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

    // ── Шина / протез ────────────────────────────────────────────────────

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
