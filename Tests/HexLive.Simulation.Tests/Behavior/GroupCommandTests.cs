using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
    /// <summary>§123 authoritative group orders, formation, and inventory.</summary>
    public sealed class GroupCommandTests
    {
        private static List<NPCState> Colonists(WorldState world, int count = 3) =>
            world.Entities.Npcs.Values
                .Where(n => n.Faction == Faction.Colony && n.Health > 0f)
                .OrderBy(n => n.Id.Value).Take(count).ToList();

        private static void Step(SimulationEngine engine, int count)
        {
            for (var i = 0; i < count; i++) engine.Step();
        }

        [Test]
        public void FormationGeometryHasTheSpecifiedCountsAndPitch()
        {
            var world = TestWorld.CreateWorld();
            var tile = world.Tiles.Items[TileCoord.Zero];
            Assert.That(tile.Junctions, Has.Count.EqualTo(61));

            var cluster = GroupFormationPlanner.CollectClusterJunctions(
                world, HexSpatialMath.TileToWorld(TileCoord.Zero));
            Assert.That(cluster.Distinct().ToList(), Has.Count.EqualTo(373));

            var nearestStep = float.MaxValue;
            foreach (var id in tile.Junctions)
            {
                var junction = world.Junctions.Items[id];
                foreach (var neighbor in junction.Neighbors)
                {
                    nearestStep = System.Math.Min(nearestStep,
                        HexSpatialMath.Distance(junction.WorldPosition,
                            world.Junctions.Items[neighbor].WorldPosition));
                }
            }
            Assert.That(nearestStep, Is.EqualTo(0.375f).Within(0.0001f));
        }

        [Test]
        public void GroupMoveFiltersAiAndAssignsUniqueReservedEndpoints()
        {
            var engine = TestWorld.CreateEngine();
            engine.Step();
            var actors = Colonists(engine.World);
            Assert.That(actors, Has.Count.EqualTo(3));
            actors[0].Mind.ManualControl = true;
            actors[1].Mind.ManualControl = true;
            actors[2].Mind.ManualControl = false;
            actors[2].Mind.CurrentGoal = GoalType.Idle;

            var ids = actors.Select(n => n.Id).ToList();
            var click = engine.World.Junctions.Items.Values.First(j =>
                !j.Blocked && j.Tiles.Count > 0 &&
                HexSpatialMath.HexDistance(actors[0].Tile, j.Tiles[0]) >= 3 &&
                actors[0].CurrentJunction is { } start &&
                Connectivity.Reachable(engine.World, start, j.Id));
            engine.Commands.Enqueue(new GroupMoveCommand(
                ids, click.WorldPosition));
            engine.Step();

            var ordered = actors.Take(2).Select(n => n.Plan.TargetJunctionId).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(ordered, Has.All.Not.Null,
                    string.Join("\n", engine.World.Events.Items
                        .Where(e => e.Type.Contains("Order"))
                        .Select(e => $"{e.Type}: {e.Message}")));
                Assert.That(ordered.Distinct().ToList(), Has.Count.EqualTo(2));
                Assert.That(actors[2].Mind.CurrentGoal, Is.Not.EqualTo(GoalType.PlayerOrder));
                foreach (var actor in actors.Take(2))
                {
                    var target = actor.Plan.TargetJunctionId!.Value;
                    Assert.That(engine.World.Reservations.Junctions[target].Owner,
                        Is.EqualTo(actor.Id));
                }
                Assert.That(engine.World.Events.Items.Any(e =>
                    e.Type == "GroupOrderResult" && e.Message.Contains("Accepted=2") &&
                    e.Message.Contains("AI=1")), Is.True);
            });

            var endpoints = actors.Take(2)
                .ToDictionary(n => n.Id, n => n.Plan.TargetJunctionId!.Value);
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
                WorldSaveSerializer.Write(engine.World, writer);
            buffer.Position = 0;
            var loadedWorld = TestWorld.CreateWorld();
            using (var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
                WorldSaveSerializer.Read(loadedWorld, reader);
            foreach (var endpoint in endpoints)
                Assert.That(loadedWorld.Reservations.Junctions[endpoint.Value].Owner,
                    Is.EqualTo(endpoint.Key));

            var resumed = Resume(loadedWorld);
            resumed.Commands.Enqueue(new GroupStopCommand(ids));
            resumed.Step();
            foreach (var endpoint in endpoints)
                Assert.That(loadedWorld.Reservations.Junctions.ContainsKey(endpoint.Value), Is.False,
                    "Cancelling a loaded group move must release its endpoint reservation.");
        }

        [Test]
        public void FormationAssignmentIsDeterministicForReversedInput()
        {
            var world = TestWorld.CreateWorld();
            var actors = Colonists(world);
            var click = actors[0].Position;
            var forward = GroupFormationPlanner.Plan(world, actors, click).Assignments
                .ToDictionary(a => a.Npc.Id.Value, a => a.Destination.Value);
            actors.Reverse();
            var reverse = GroupFormationPlanner.Plan(world, actors, click).Assignments
                .ToDictionary(a => a.Npc.Id.Value, a => a.Destination.Value);
            Assert.That(reverse, Is.EqualTo(forward));
        }

        [Test]
        public void FormationExcludesBlockedAndForeignReservedJunctions()
        {
            var world = TestWorld.CreateWorld();
            var actor = Colonists(world, 1)[0];
            var click = actor.Position;
            var cluster = GroupFormationPlanner.CollectClusterJunctions(world, click);
            var occupied = world.Entities.Npcs.Values
                .Where(n => n.CurrentJunction is not null)
                .Select(n => n.CurrentJunction!.Value)
                .ToHashSet();
            var candidates = cluster.Where(id =>
                    world.Junctions.Items.TryGetValue(id, out var junction) &&
                    !junction.Blocked && !occupied.Contains(id))
                .OrderBy(id => id.Value).Take(2).ToList();
            Assert.That(candidates, Has.Count.EqualTo(2));

            var blocked = candidates[0];
            var reserved = candidates[1];
            world.Junctions.Items[blocked].Blocked = true;
            world.TopologyVersion++;
            var outsider = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
            world.Reservations.Junctions[reserved] = new ReservationRecord
            {
                Owner = outsider.Id,
                StartTick = world.Tick,
                EndTick = world.Tick + 100
            };

            var result = GroupFormationPlanner.Plan(world, new[] { actor }, click);
            Assert.That(result.Assignments.All(a =>
                a.Destination != blocked && a.Destination != reserved), Is.True);
        }

        [Test]
        public void FormationUsesEachActorsJumpCapabilityWhileCarrying()
        {
            var world = TestWorld.CreateWorld();
            var actors = Colonists(world, 2);
            var actor = actors[0];
            var passenger = actors[1];
            actor.CurrentJunction = SpatialQueries.FindNearestJunction(world, actor.Position);
            Assert.That(actor.CurrentJunction, Is.Not.Null);
            var start = actor.CurrentJunction!.Value;

            // Other bodies must not turn this into an avoidance test. The
            // foreign id remains a valid reservation owner but has no node.
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (!other.Id.Equals(actor.Id)) other.CurrentJunction = null;
            }

            var destination = world.Junctions.Items.Values
                .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                    Connectivity.Reachable(world, start, j.Id, canJump: true) &&
                    !Connectivity.Reachable(world, start, j.Id, canJump: false))
                .OrderBy(j => j.Id.Value).FirstOrDefault();
            Assert.That(destination, Is.Not.Null,
                "The prototype world must contain a shelf reachable only by jumping.");

            var click = destination!.WorldPosition;
            var cluster = GroupFormationPlanner.CollectClusterJunctions(world, click);
            var outsider = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
            foreach (var id in cluster)
            {
                if (id.Equals(destination.Id)) continue;
                world.Reservations.Junctions[id] = new ReservationRecord
                {
                    Owner = outsider.Id,
                    StartTick = world.Tick,
                    EndTick = world.Tick + 100
                };
            }

            actor.Body.Parts[BodyPart.LegL] = 0.5f;
            actor.Body.Parts[BodyPart.LegR] = 0.5f;
            var withoutJump = GroupFormationPlanner.Plan(world, new[] { actor }, click);
            actor.Body.Parts[BodyPart.LegL] = 1f;
            actor.Body.Parts[BodyPart.LegR] = 1f;
            var withJump = GroupFormationPlanner.Plan(world, new[] { actor }, click);
            actor.CarriedNpcId = passenger.Id;
            passenger.CarriedByNpcId = actor.Id;
            var whileCarrying = GroupFormationPlanner.Plan(world, new[] { actor }, click);

            Assert.Multiple(() =>
            {
                Assert.That(withoutJump.Assignments, Is.Empty);
                Assert.That(withJump.Assignments, Has.Count.EqualTo(1));
                Assert.That(withJump.Assignments[0].Destination, Is.EqualTo(destination.Id));
                Assert.That(whileCarrying.Assignments, Has.Count.EqualTo(1),
                    "Occupied hands must not remove the carrier's physical jump capability.");
                Assert.That(whileCarrying.Assignments[0].Destination, Is.EqualTo(destination.Id));
            });
        }

        [Test]
        public void PathfindingKeepsJumpCapabilityWhileCarrying()
        {
            var world = TestWorld.CreateWorld();
            var actors = Colonists(world, 2);
            var carrier = actors[0];
            var passenger = actors[1];
            carrier.CurrentJunction = SpatialQueries.FindNearestJunction(world, carrier.Position);
            Assert.That(carrier.CurrentJunction, Is.Not.Null);
            var start = carrier.CurrentJunction!.Value;

            foreach (var other in world.Entities.Npcs.Values)
            {
                if (!other.Id.Equals(carrier.Id)) other.CurrentJunction = null;
            }
            world.Mobs.Clear();

            var destination = world.Junctions.Items.Values
                .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                    Connectivity.Reachable(world, start, j.Id, canJump: true) &&
                    !Connectivity.Reachable(world, start, j.Id, canJump: false))
                .OrderBy(j => j.Id.Value).FirstOrDefault();
            Assert.That(destination, Is.Not.Null,
                "The prototype world must contain a shelf reachable only by jumping.");

            carrier.Body.Parts[BodyPart.LegL] = 1f;
            carrier.Body.Parts[BodyPart.LegR] = 1f;
            carrier.CarriedNpcId = passenger.Id;
            passenger.CarriedByNpcId = carrier.Id;
            carrier.Mind.CurrentGoal = GoalType.PlayerOrder;
            carrier.Plan.Goal = GoalType.PlayerOrder;
            carrier.Plan.TargetJunctionId = destination!.Id;
            carrier.Plan.TargetTile = destination.Tiles[0];
            carrier.Plan.Status = PlanStatus.Active;
            carrier.Movement.JunctionPath.Clear();
            carrier.Movement.IsMoving = false;

            new PathfindingSystem().Run(world);

            Assert.Multiple(() =>
            {
                Assert.That(carrier.Movement.IsMoving, Is.True);
                Assert.That(carrier.Movement.JunctionPath, Is.Not.Empty);
                Assert.That(carrier.Movement.JunctionPath[^1], Is.EqualTo(destination.Id));
            });
        }

        [Test]
        public void OutsiderCannotReceiveForgedPlayerCommands()
        {
            var engine = TestWorld.CreateEngine();
            var outsider = engine.World.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
            outsider.Mind.ManualControl = false;
            outsider.Inventory.Items.Add(new ItemInstance(ContentIds.LeatherPants));
            engine.Commands.Enqueue(new SetManualControlCommand(outsider.Id, true));
            engine.Commands.Enqueue(new ManageInventoryCommand(outsider.Id,
                new InventoryItemRef(InventoryItemSource.Carried,
                    outsider.Inventory.Items.Count - 1, ContentIds.LeatherPants),
                InventoryAction.Drop));
            engine.Step();

            Assert.Multiple(() =>
            {
                Assert.That(outsider.Mind.ManualControl, Is.False);
                Assert.That(outsider.Inventory.Items.Any(i =>
                    i.DefinitionId == ContentIds.LeatherPants), Is.True);
                Assert.That(engine.World.Events.Items.Any(e =>
                    e.Type == "ManualOrderRejected" && e.EntityId == outsider.Id.Value &&
                    e.Message.Contains("Reason=NotOwned")), Is.True);
            });
        }

        [Test]
        public void GroupStopAndAttackApplyToAllLivingManualColonists()
        {
            var engine = TestWorld.CreateEngine();
            var actors = Colonists(engine.World, 2);
            foreach (var actor in actors) actor.Mind.ManualControl = true;
            var target = engine.World.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
            var ids = actors.Select(n => n.Id).ToList();

            engine.Commands.Enqueue(new GroupAttackNpcCommand(ids, target.Id));
            engine.Step();
            Assert.That(actors.All(n => n.Mind.ManualAttackNpcId == target.Id), Is.True);

            engine.Commands.Enqueue(new GroupStopCommand(ids));
            engine.Step();
            Assert.That(actors.All(n => n.Mind.CurrentGoal == GoalType.None &&
                n.Mind.ManualAttackNpcId is null), Is.True);
        }

        [Test]
        public void PlayerInventoryWearIsImmediateAndPreservesAiPlan()
        {
            var engine = TestWorld.CreateEngine();
            var npc = Colonists(engine.World, 1)[0];
            var formerOwner = engine.World.Entities.Npcs.Values.First(other => other.Id != npc.Id);
            npc.Mind.ManualControl = false;
            npc.Mind.CurrentGoal = GoalType.Explore;
            npc.Plan.Goal = GoalType.Explore;
            npc.Plan.Status = PlanStatus.Active;
            var preservedStep = new PlanStep
            {
                Type = PlanStepType.Wait,
                TimeoutEndTick = engine.World.Tick + 40
            };
            npc.Plan.Steps.Clear();
            npc.Plan.Steps.Add(preservedStep);

            var pants = new ItemInstance(ContentIds.LeatherPants)
                { OwnerId = formerOwner.Id.Value };
            npc.Inventory.Items.Add(pants);
            var index = npc.Inventory.Items.Count - 1;
            var result = engine.ApplyManualCommand(new ManageInventoryCommand(npc.Id,
                new InventoryItemRef(InventoryItemSource.Carried, index, ContentIds.LeatherPants),
                InventoryAction.Wear));

            Assert.Multiple(() =>
            {
                Assert.That(result.Accepted, Is.True);
                Assert.That(npc.Mind.ManualControl, Is.False,
                    "Inventory management must stay available in AI mode.");
                Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Explore));
                Assert.That(npc.Plan.Goal, Is.EqualTo(GoalType.Explore));
                Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
                Assert.That(npc.Plan.Steps, Has.Count.EqualTo(1));
                Assert.That(npc.Plan.Steps[0], Is.SameAs(preservedStep));
                Assert.That(npc.WornItems.Any(item => ReferenceEquals(item, pants)), Is.True);
                Assert.That(npc.Inventory.Items.Any(item => ReferenceEquals(item, pants)), Is.False);
                Assert.That(engine.World.Events.Items.Any(e =>
                    e.Type.Contains("WearPermission") && e.EntityId == npc.Id.Value), Is.False,
                    "An explicit player action must not start an owner-permission scene.");
            });
        }

        private static SimulationEngine Resume(WorldState world)
        {
            var clock = new SimulationClock();
            clock.Resume();
            var engine = new SimulationEngine(world, new SimulationSettings(), clock);
            SimulationSystemRegistry.RegisterDefaults(engine);
            return engine;
        }
    }
}
