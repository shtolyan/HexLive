using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §121.9 (тёмная фаза): приказы §56 (каннибализм) и §81 (сцена травли) — за
/// собственным выключателем Spec121.ManualDarkOrdersEnabled. Проверяются
/// гейты приёма (физика остаётся, выбор — за игроком), родные цели и то, что
/// выключатель гасит именно этот слой, не трогая остальной ручной режим.
/// </summary>
public sealed class ManualDarkOrderTests
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

    // Спавн колонии — В ХИЖИНЕ, а помещение — санктуарий §81: сцену там не
    // затеять (гейт честный). Жертву выносим под открытое небо: пробуем
    // достижимые свободные узлы, пока санктуарий не отпустит.
    private static void PlaceOutdoors(WorldState world, NPCState person, NPCState origin)
    {
        var start = origin.CurrentJunction!.Value;
        foreach (var candidate in world.Junctions.Items.Values
                     .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                         SpatialQueries.IsJunctionFree(world, j.Id) &&
                         Connectivity.Reachable(world, start, j.Id))
                     .OrderBy(j => j.Id.Value))
        {
            MoveTo(world, person, candidate);
            if (!MobSystem.IsNpcInSanctuary(world, person))
            {
                return;
            }
        }

        Assert.Fail("Не нашлось наружного узла для жертвы сцены.");
    }

    private static void MoveTo(WorldState world, NPCState person, Junction destination)
    {
        if (person.CurrentJunction is { } previousJunction)
        {
            SpatialMutations.FreeJunction(world, previousJunction, person.Id);
            SpatialMutations.ReleaseJunctionReservation(
                world, previousJunction, person.Id);
        }

        var previousTile = person.Tile;
        person.Tile = destination.Tiles[0];
        person.Fragment = destination.Fragment;
        person.Position = destination.WorldPosition;
        person.CurrentJunction = destination.Id;
        SpatialMutations.MoveEntityToTile(world, person.Id, previousTile, person.Tile);
        SpatialMutations.OccupyJunction(world, destination.Id, person.Id);
    }

    [Test]
    public void DarkOrdersAreRejectedWhenTheSwitchIsOff()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = Colonists(world);
        TakeControl(engine, pair[0]);

        var previous = Spec121.ManualDarkOrdersEnabled;
        Spec121.ManualDarkOrdersEnabled = false;
        try
        {
            var prey = ManualCommandExecutor.Apply(
                world, new PreyPersonCommand(pair[0].Id, pair[1].Id));
            var abuse = ManualCommandExecutor.Apply(
                world, new AbusePersonCommand(pair[0].Id, pair[1].Id));

            Assert.That(prey.Reason, Is.EqualTo("FeatureDisabled"));
            Assert.That(abuse.Reason, Is.EqualTo("FeatureDisabled"));
        }
        finally
        {
            Spec121.ManualDarkOrdersEnabled = previous;
        }
    }

    [Test]
    public void PreyWithoutButcherToolIsRejectedAsMissingTool()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = Colonists(world);
        var predator = pair[0];
        TakeControl(engine, predator);
        predator.Inventory.Items.RemoveAll(i => GearCatalog.HasCapability(
            new List<ItemInstance> { i }, GearCapability.Butcher));

        var admission = ManualCommandExecutor.Apply(
            world, new PreyPersonCommand(predator.Id, pair[1].Id));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("MissingTool"),
            "Физический гейт §56 (разделочный нож) обязан остаться и у приказа.");
    }

    [Test]
    public void PreyOrderCarriesTheNativeGoalAndChasesTheNamedVictim()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = Colonists(world);
        var predator = pair[0];
        var victim = pair[1];
        TakeControl(engine, predator);
        predator.Inventory.Items.Add(new ItemInstance("tool.knife"));

        var admission = ManualCommandExecutor.Apply(
            world, new PreyPersonCommand(predator.Id, victim.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"Prey отклонён: {admission.Reason}");
            Assert.That(predator.Mind.CurrentGoal, Is.EqualTo(GoalType.Prey),
                "Приказ §56 обязан носить РОДНУЮ цель Prey — удары ведёт " +
                "штатная PredationSystem.");
            Assert.That(predator.Mind.ManualAttackNpcId, Is.EqualTo(victim.Id),
                "Жертву погони держит тот же якорь, что у приказа атаки.");
            Assert.That(predator.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(predator.Plan.TargetAgentId, Is.Null,
                "Prey-план — move-only: TargetAgentId направил бы диспетчер " +
                "исполнения в RunTalk.");
        });

        // Цель переживает решающие проходы (белый список за флагом).
        for (var i = 0; i < 32; i++)
        {
            engine.Step();
        }

        Assert.That(
            world.Events.Items.Any(e => e.Type == "ManualForbiddenGoalDropped" &&
                e.EntityId == predator.Id.Value),
            Is.False,
            "Sweep снёс тёмную цель при включённом выключателе.");
    }

    [Test]
    public void AbuseOnAHousemateIsRejectedAsNotHostile()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = Colonists(world);
        TakeControl(engine, pair[0]);

        var admission = ManualCommandExecutor.Apply(
            world, new AbusePersonCommand(pair[0].Id, pair[1].Id));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NotHostile"),
            "Сцена §81 строится на враждебности — своих приказом не травят.");
    }

    [Test]
    public void AbuseOnAHostileMarkClaimsHerAndCarriesTheNativeGoal()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = Colonists(world);
        var abuser = pair[0];
        var mark = pair[1];
        TakeControl(engine, abuser);
        // Чужак для сцены: §81 требует враждебную фракцию — и не в санктуарии.
        mark.Faction = Faction.Outsiders;
        PlaceOutdoors(world, mark, abuser);

        var admission = ManualCommandExecutor.Apply(
            world, new AbusePersonCommand(abuser.Id, mark.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"Abuse отклонён: {admission.Reason}");
            Assert.That(abuser.Mind.CurrentGoal, Is.EqualTo(GoalType.Abuse));
            Assert.That(abuser.Mind.AbuseTargetNpcId, Is.EqualTo(mark.Id));
            Assert.That(mark.Mind.PendingAbuseFrom, Is.EqualTo(abuser.Id),
                "Жертва не заклеймлена — сцена не удержит её и не будет видна " +
                "свидетельницам.");
            Assert.That(abuser.Plan.Status, Is.EqualTo(PlanStatus.Active));
        });
    }
}

}
