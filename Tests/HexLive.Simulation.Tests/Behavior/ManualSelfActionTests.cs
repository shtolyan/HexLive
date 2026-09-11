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
/// §121.9: самодействия — приказы колонистке на саму себя (клик по себе).
/// Крик о помощи, перевязка, отдых на земле, еда/питьё из рюкзака идут через
/// ту же SelfActionCommand и исполняются штатными билдерами планов; отказ
/// называет причину (NotInCombat/NotNeeded/NothingToEat…).
/// </summary>
public sealed class ManualSelfActionTests
{
    private static NPCState[] Colonists(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .ToArray();

    private static void TakeControl(SimulationEngine engine, NPCState npc)
    {
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true));
        engine.Step();
    }

    [Test]
    public void ExploreOrderUsesNativePlannerCompletesAndDoesNotEnableAutonomousExploration()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);
        var start = npc.Position;
        var admission = engine.ApplyManualCommand(new SelfActionCommand(npc.Id, SelfActionKind.Explore));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Explore));
        Assert.That(NpcControlPolicy.MayAuctionGoal(npc, GoalType.Explore), Is.False);
        Assert.That(NpcControlPolicy.MayPlanGoal(npc, GoalType.Explore), Is.False);
        var destination = npc.Plan.TargetJunctionId!.Value;
        Assert.That(PlanningSystem.IsExploreCandidate(world, npc, world.Junctions.Items[destination]), Is.True);

        // Optional technical snapshot for the calculated geometry diagram. It is
        // never sent to a model, nor used to choose the command's destination.
        var output = System.Environment.GetEnvironmentVariable("HEXLIVE_EXPLORE_DIAGNOSTIC");
        if (!string.IsNullOrEmpty(output))
            System.IO.File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new
            {
                hexRadius = HexSpatialMath.HexRadius,
                start = new { x = start.X, y = start.Y },
                destination = destination.Value,
                nodes = world.Junctions.Items.Values.Where(j => j.Tiles.Any(t =>
                    HexSpatialMath.HexDistance(npc.Tile, t) <= 9)).Select(j => new
                    { id = j.Id.Value, x = j.WorldPosition.X, y = j.WorldPosition.Y,
                      blocked = j.Blocked, candidate = PlanningSystem.IsExploreCandidate(world, npc, j) })
            }));

        var route = new System.Collections.Generic.List<object> { new { x = start.X, y = start.Y } };
        for (var i = 0; i < 1200 && npc.Mind.CurrentGoal == GoalType.Explore; i++)
        {
            engine.Step();
            if (!string.IsNullOrEmpty(output)) route.Add(new { x = npc.Position.X, y = npc.Position.Y });
        }
        if (!string.IsNullOrEmpty(output))
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(output))!;
            snapshot["route"] = System.Text.Json.JsonSerializer.SerializeToNode(route);
            System.IO.File.WriteAllText(output, snapshot.ToJsonString());
        }
        Assert.That(npc.CurrentJunction, Is.EqualTo(destination), "The accepted exploration must actually arrive.");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None), "A finite self-order must be swept after arrival.");
        Assert.That(npc.Position, Is.Not.EqualTo(start));
        Assert.That(npc.Mind.ManualControl, Is.True);
        for (var i = 0; i < 12; i++) engine.Step();
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None), "No automatic second exploration leg.");
    }

    [Test]
    public void ExploreWithoutDestinationRejectsAndLeavesNoStuckGoal()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Colonists(engine.World)[0];
        TakeControl(engine, npc);
        foreach (var junction in engine.World.Junctions.Items.Values) junction.Blocked = true;
        var admission = engine.ApplyManualCommand(new SelfActionCommand(npc.Id, SelfActionKind.Explore));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NoExploreDestination"));
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.None));
    }

    [Test]
    public void GoHomeOrderBuildsUrgentRouteIntoOwnCamp()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.GoHome));

        Assert.That(admission.Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"GoHome отклонён: {admission.Reason}");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Homeward));
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        Assert.That(npc.Plan.TargetTile, Is.Not.Null);
        Assert.That(ColonyQueries.InCamp(
            world, npc.Plan.TargetTile!.Value, npc.Faction), Is.True);
        Assert.That(GoalCatalog.UrgencyFor(GoalType.Homeward),
            Is.EqualTo(UrgencyClass.Hurry));
    }

    [Test]
    public void EatFromPackOrderEatsEvenBelowTheAutoNeedThreshold()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);

        npc.Inventory.Items.Add(new ItemInstance("food.meat_cooked"));
        // Ниже порога авто-нужды §121.6 — сама бы она есть не начала.
        npc.Needs.Hunger = 0.4f;

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.EatFromPack));

        Assert.That(admission.Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"EatFromPack отклонён: {admission.Reason}");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Eat));

        for (var i = 0; i < 400 && npc.Needs.Hunger > 0.15f; i++)
        {
            engine.Step();
        }

        Assert.That(npc.Needs.Hunger, Is.LessThan(0.15f),
            "Явный приказ поесть обязан работать и ниже порога авто-нужды: " +
            "план строит штатный планировщик (§121.6 inventory-only).");
    }

    [Test]
    public void EatFromPackWithEmptyPackIsRejected()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);
        while (npc.Inventory.FindFirstFood(world.Content) is { } food)
        {
            if (!npc.Inventory.Items.Remove(food))
            {
                break;
            }
        }

        npc.Inventory.Items.RemoveAll(i =>
            i.DefinitionId == ContentIds.Coconut ||
            i.DefinitionId == ContentIds.CoconutPierced ||
            i.DefinitionId == ContentIds.CoconutOpen);

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.EatFromPack));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NothingToEat"));
    }

    [Test]
    public void CallForHelpOutOfCombatIsRejectedAsNotInCombat()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.CallForHelp));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NotInCombat"),
            "Крик без агрессора механически пуст: Defend некому назначить цель.");
    }

    [Test]
    public void CallForHelpInCombatCriesAndKeepsThePlan()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = Colonists(world);
        var victim = pair[0];
        var attacker = pair[1];
        TakeControl(engine, victim);

        victim.Mind.CombatOpponentNpcId = attacker.Id;
        victim.IsFighting = true;

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(victim.Id, SelfActionKind.CallForHelp));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"CallForHelp отклонён: {admission.Reason}");
            Assert.That(victim.Mind.LastHelpCryTick, Is.EqualTo(world.Tick),
                "Кулдаун §57.9 не проштампован — крик не состоялся.");
            Assert.That(world.Events.Items.Any(
                    e => e.Type == "HelpCry" && e.EntityId == victim.Id.Value),
                Is.True,
                "Крик обязан звучать той же трассой HelpCry, что и автоматический.");
        });

        // Повторный крик до истечения кулдауна — честный отказ.
        var again = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(victim.Id, SelfActionKind.CallForHelp));
        Assert.That(again.Reason, Is.EqualTo("Cooldown"));
    }

    [Test]
    public void TreatSelfWithoutWoundsIsRejectedAsNotNeeded()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bandage));

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.TreatSelf));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NotNeeded"),
            "У здоровой с бинтом отказ обязан быть «не нужно», а не «нет бинта».");
    }

    [Test]
    public void TreatSelfWithBleedingWoundBelowAutoThresholdIsAccepted_Bug289()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bandage));
        npc.Wounds.Add(new WoundState
        {
            Id = 289,
            Zone = BodyPart.LegR,
            Severity = 0.08f,
            Heal01 = 0f,
            Clot01 = 0f,
            Stabilized = false,
            BleedFactor = 1f,
            Seed = 289
        });

        Assert.That(AidAssessment.NeedsDressing(npc), Is.True,
            "Manual self-treatment must use the same wound eligibility as aid to another colonist.");
        Assert.That(DecisionSystem.SelfTreatmentIndicated(npc, 1), Is.False,
            "This reproduces the old last-bandage/autonomy threshold: the explicit order must override it.");

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.TreatSelf));

        Assert.That(admission.Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"TreatSelf отклонён: {admission.Reason}");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.TreatWounds));
    }

    [Test]
    public void GroundSleepOrderStaysInsideTheCurrentHex_Bug196()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonists(world)[0];
        TakeControl(engine, npc);
        npc.Needs.Energy = 1f;

        var hearthTile = world.Entities.Objects.Values
            .First(obj => obj.DefinitionId == ContentIds.Campfire).Tile;
        var remote = world.Tiles.Items.Values
            .Where(tile => tile.Flags.HasFlag(TileFlags.Walkable) &&
                           !tile.Flags.HasFlag(TileFlags.Water) &&
                           HexSpatialMath.HexDistance(tile.Coord, hearthTile) > 8)
            .OrderBy(tile => tile.Coord.Q).ThenBy(tile => tile.Coord.R)
            .Select(tile => (Tile: tile, Solved: LyingSpot.TrySolveOnTile(
                world, npc, tile.Coord, out var placement), Placement: placement))
            .First(candidate => candidate.Solved &&
                                SpatialQueries.IsJunctionFree(world, candidate.Placement.Node));
        if (npc.CurrentJunction is { } oldJunction)
        {
            SpatialMutations.FreeJunction(world, oldJunction, npc.Id);
        }
        var oldTile = npc.Tile;
        npc.Tile = remote.Tile.Coord;
        SpatialMutations.MoveEntityToTile(world, npc.Id, oldTile, npc.Tile);
        npc.CurrentJunction = remote.Placement.Node;
        npc.Position = world.Junctions.Items[remote.Placement.Node].WorldPosition;
        SpatialMutations.OccupyJunction(world, remote.Placement.Node, npc.Id);

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.GroundSleep));

        Assert.That(admission.Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"GroundSleep отклонён: {admission.Reason}");
        var target = npc.Plan.Steps[^1].TargetJunction;

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Sleep),
                "Самодействие носит РОДНУЮ цель (§138).");
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(npc.Plan.Steps[^1].Type, Is.EqualTo(PlanStepType.GroundSleep),
                "План обязан использовать штатный GroundSleep, а не отдельное действие вида.");
            Assert.That(target, Is.Not.Null);
            Assert.That(world.Junctions.Items[target!.Value].Tiles, Does.Contain(npc.Tile),
                "Ручной приказ лечь не должен уводить персонажа в лагерь или на соседний гекс.");
        });

        // Дожидаемся фактического сна: так тест задевает и ручной
        // sweep, и штатный путь/укладку, но не смешивает проверку с боями
        // и погодой следующих двух минут мира.
        for (var i = 0; i < 48 &&
             npc.Execution.CurrentInteraction != InteractionType.Sleep; i++)
        {
            engine.Step();
        }

        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep),
            "Ручной GroundSleep не дошёл до фактической укладки.");
        npc.Execution.EndTick = world.Tick;
        engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(
                world.Events.Items.Any(e => e.Type == "ManualForbiddenGoalDropped" &&
                    e.EntityId == npc.Id.Value),
                Is.False,
                "Sweep §121.6 r2 снёс принятый приказ — цель забыли внести в " +
                "MayRetainGoalWhileManual.");
            Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep),
                "Bug #206: ручная колонистка с полной энергией встала на " +
                "первой 100-тиковой границе GroundSleep.");
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(npc.Execution.EndTick, Is.GreaterThan(world.Tick),
                "Блок сна не перевзвёлся на следующие 100 тиков.");
        });
    }
}

}
