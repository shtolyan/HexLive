using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: одежда не должна лежать по всей карте. Скучная фоновая работа —
/// подобрать забытую вещь и отнести её в гардероб.
/// </summary>
public sealed class StowClothesTests
{
    private const string Shirt = "clothing.tshirt_big_tshirt_lost_angels";

    private static (SimulationEngine engine, NPCState npc, WorldObjectState stray) StrayFarFromHome()
    {
        var engine = TestWorld.CreateEngine(12345);
        for (var i = 0; i < 4; i++)
        {
            engine.Step();
        }

        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var home = ColonyQueries.Home(world, npc.Faction).Value;

        // Вещь кладём рядом с НЕЙ, но далеко от дома: так она её видит, и
        // проверяется именно уборка, а не дальность восприятия.
        var junction = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(j.Tiles[0], home) > Spec133.HomeStowRadiusTiles &&
            HexSpatialMath.HexDistance(j.Tiles[0], npc.Tile) <= 1);
        var stray = WorldObjectMutations.SpawnObject(
            world, Shirt, npc.Fragment, junction.Tiles[0], junction.Id);

        new PerceptionSystem().Run(world);
        return (engine, npc, stray);
    }

    /// <summary>⭐ Забытая вещь опознана, и путь к ней спланирован.</summary>
    [Test]
    public void AGarmentLyingFarFromHomeBecomesAChore()
    {
        var (engine, npc, stray) = StrayFarFromHome();
        var world = engine.World;

        Assert.That(StrayGarmentMath.FindStray(world, npc)?.Id, Is.EqualTo(stray.Id),
            "Забытая вещь не опознана как работа.");

        npc.Mind.CurrentGoal = GoalType.StowClothes;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);

        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active), "План уборки не построился.");
        Assert.That(npc.Plan.Steps[^1].Type, Is.EqualTo(PlanStepType.StowCarriedGarment));
        Assert.That(npc.Plan.Steps[^1].TargetObject, Is.EqualTo(stray.Id));
    }

    /// <summary>Чужую вещь не трогают даже из лучших побуждений.</summary>
    [Test]
    public void AHousematesGarmentIsLeftAlone()
    {
        var (engine, npc, stray) = StrayFarFromHome();
        var world = engine.World;
        var other = world.Entities.Npcs.Values.First(
            n => n.Faction == Faction.Colony && n.Id != npc.Id);
        stray.Owner = other.Id;

        Assert.That(StrayGarmentMath.FindStray(world, npc), Is.Null,
            "Понесли домой чужую вещь, не спросив хозяйку.");
    }

    /// <summary>Купальную кучу не разбирают: человек вернётся к воде голым.</summary>
    [Test]
    public void ABathersPileIsNeverTidiedAway()
    {
        var (engine, npc, stray) = StrayFarFromHome();
        var world = engine.World;
        var bather = world.Entities.Npcs.Values.First(
            n => n.Faction == Faction.Colony && n.Id != npc.Id);
        bather.Mind.RedressGarments.Add(stray.Id);

        Assert.That(StrayGarmentMath.FindStray(world, npc), Is.Null,
            "Унесли одежду, которую ждёт купальщица.");
    }

    /// <summary>Вещь доезжает до гардероба и остаётся своей.</summary>
    [Test]
    public void TheGarmentEndsUpInTheWardrobe()
    {
        var (engine, npc, stray) = StrayFarFromHome();
        var world = engine.World;
        var strayId = stray.Id;
        npc.Mind.CurrentGoal = GoalType.StowClothes;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);

        // Нога 1: она на месте вещи — поднимает её в руку.
        npc.CurrentJunction = npc.Plan.TargetJunctionId;
        npc.Movement.IsMoving = false;
        npc.Plan.Steps.RemoveAll(s => s.Type == PlanStepType.MoveToJunction);
        npc.Plan.CurrentStepIndex = 0;
        new ExecutionSystem().Run(world);

        Assert.That(npc.Execution.HeldGarment, Is.Not.Null, "Вещь не взяли в руки.");
        Assert.That(world.Entities.Objects.ContainsKey(strayId), Is.False,
            "Поднятая вещь всё ещё лежит на земле.");

        // Нога 2: она у дома — кладёт вещь.
        npc.CurrentJunction = npc.Plan.TargetJunctionId;
        new ExecutionSystem().Run(world);

        Assert.That(npc.Execution.HeldGarment, Is.Null, "Вещь так и осталась в руках.");
        var wardrobe = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Wardrobe);
        var stowed = world.Entities.Objects.Values.FirstOrDefault(
            o => o.DefinitionId == Shirt && o.Junctions.Count > 0 &&
                 o.Junctions[0].Equals(wardrobe.Junctions[0]));
        Assert.That(stowed, Is.Not.Null, "Принесённая вещь не оказалась в гардеробе.");
        Assert.That(stowed.Owner, Is.EqualTo(npc.Id),
            "Ничейная вещь, принесённая домой, не досталась той, кто её принесла.");
    }
}

}
