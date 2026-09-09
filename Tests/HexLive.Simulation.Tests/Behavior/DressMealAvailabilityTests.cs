using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class DressMealAvailabilityTests
{
    [Test]
    public void ReservedGarmentDoesNotRepeatedlyInterruptAnAccessibleMeal()
    {
        var (engine, npc, garment) = Arena();
        var events = Step(engine, npc, 400);

        Assert.Multiple(() =>
        {
            Assert.That(events.Count(e => e.Type == "PlanFailed" &&
                e.Message.Contains("Goal=Dress")), Is.Zero,
                "A visible but currently reserved garment must not repeatedly win and fail.");
            Assert.That(events.Count(e => e.Type == "GoalSelected" &&
                e.Message.Contains("Dress") && e.Message.Contains("CHANGED from Eat")), Is.Zero,
                "The unavailable wardrobe bid must not tear up the real meal route.");
            Assert.That(events.Any(e => e.Type == "PlanBuilt" &&
                e.Message.Contains("Goal=Eat")), Is.True);
            Assert.That(npc.WornItems.Any(i => i.DefinitionId == garment.DefinitionId), Is.False);
        });

        events.AddRange(Step(engine, npc, 800));
        Assert.That(events.Any(e => e.Type == "ItemConsumed"), Is.True,
            "The normal engine must actually reach and eat the accessible meal.");
    }

    [Test]
    public void FreeingTheGarmentApproachRestoresDressingWithoutABlacklist()
    {
        var (engine, npc, garment) = Arena();
        Step(engine, npc, 80);
        engine.World.Reservations.Junctions.Remove(garment.Junctions[0]);
        var events = Step(engine, npc, 600);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Any(i => i.DefinitionId == garment.DefinitionId), Is.True,
                "Temporary occupation must not turn into a permanent shun or suppressed Dress goal.");
            Assert.That(events.Any(e => e.Type == "InteractionCompleted" &&
                e.Message.StartsWith("Dress")), Is.True,
                "Resume must walk and complete the ordinary interaction.");
        });
    }

    private static List<SimulationEvent> Step(SimulationEngine engine, NPCState npc, int ticks)
    {
        var result = new List<SimulationEvent>();
        var last = engine.World.Events.HighestSeq;
        for (var i = 0; i < ticks; i++)
        {
            engine.Step();
            result.AddRange(engine.World.Events.Items.Where(e => e.Seq > last && e.EntityId == npc.Id.Value));
            last = engine.World.Events.HighestSeq;
        }
        return result;
    }

    private static (SimulationEngine engine, NPCState npc, WorldObjectState garment) Arena()
    {
        // Controlled regression for #379's seed, not the unavailable historical
        // tick 177799/Nika 902 save. The separate restore probe uses her real body.
        // Keep the standard engine/registry: perception, movement and needs stay live.
        var engine = TestWorld.CreateEngine(12345);
        for (var i = 0; i < 4; i++) engine.Step();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First();
        foreach (var other in world.Entities.Npcs.Values.Where(n => n != npc))
        {
            other.Mind.ManualControl = true;
            other.Mind.LastManualInputTick = world.Tick + 2000;
            other.Mind.CurrentGoal = GoalType.None;
            other.Plan.Status = PlanStatus.None;
            other.Plan.Steps.Clear();
            other.Execution.Status = ExecutionStatus.None;
            other.Movement.IsMoving = false;
            other.Movement.JunctionPath.Clear();
        }
        world.Mobs.Clear();
        var pack = npc.WornItems.Single(i => world.Content.ObjectDefinitions[i.DefinitionId].Layer == WearLayer.Bags);
        npc.WornItems.Remove(pack);
        var current = npc.CurrentJunction!.Value;
        var anchor = world.Junctions.Items.Values.Where(j => !j.Blocked && j.Id != current &&
                world.Tiles.Items[npc.Tile].Junctions.Contains(j.Id) &&
                Connectivity.Reachable(world, current, j.Id, npc.Body.CanJump))
            .OrderBy(j => DistanceSquared(j.WorldPosition, npc.Position)).First();
        var garment = WorldObjectMutations.SpawnObject(world, pack.DefinitionId,
            npc.Fragment, npc.Tile, anchor.Id);
        garment.Owner = npc.Id;
        world.Reservations.Junctions.Remove(anchor.Id);
        Assert.That(SpatialMutations.TryReserveJunction(world, anchor.Id,
            new EntityId(99999), world.Tick, 2000), Is.True);

        var far = world.Junctions.Items.Values.Where(j => !j.Blocked &&
                !SpatialQueries.IsAllWaterJunction(world, j.Id) &&
                Connectivity.Reachable(world, current, j.Id, npc.Body.CanJump))
            .OrderByDescending(j => DistanceSquared(j.WorldPosition, npc.Position)).First();
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire,
            far.Fragment, far.Tiles[0], far.Id);
        fire.Contents.Add(new ItemInstance(ContentIds.MeatCooked));
        npc.Memory.KnownObjects[fire.Id] = new ObjectMemory
        {
            Id = fire.Id, DefinitionId = fire.DefinitionId, Tile = fire.Tile,
            Junction = far.Id, LastSeenTick = world.Tick, IsPermanent = true
        };
        npc.Memory.Version++;
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));
        npc.Mind.Cooldowns.Clear();
        foreach (GoalType goal in Enum.GetValues(typeof(GoalType)))
            if (goal is not GoalType.Eat and not GoalType.Dress and not GoalType.None and not GoalType.Idle)
                npc.Mind.Cooldowns.Add(new GoalCooldown { Goal = goal, EndTick = world.Tick + 2000 });
        npc.Mind.Cooldowns.Add(new GoalCooldown { Goal = GoalType.Dress, EndTick = world.Tick + 8 });
        npc.Needs.Hunger = 0.55f;
        npc.Needs.Energy = npc.Needs.Stamina = 1f;
        npc.Needs.Thirst = npc.Needs.Social = 0f;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.Steps.Clear();
        npc.Execution.Status = ExecutionStatus.None;
        npc.Mind.WakeGraceUntilTick = 0;
        return (engine, npc, garment);
    }

    private static float DistanceSquared(Float2 a, Float2 b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
}

}
