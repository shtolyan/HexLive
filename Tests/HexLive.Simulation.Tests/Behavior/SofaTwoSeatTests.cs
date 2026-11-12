using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>§176.3 r2 (bug #735): the apartment sofa seats two, side by side.</summary>
public sealed class SofaTwoSeatTests
{
    private const string Sofa = FurnitureSeats.SofaDefinitionId;

    [TestCase(0)]
    [TestCase(7)]
    [TestCase(629209070)]
    public void EverySofaHasTwoDistinctSeatStandsInFrontOfItsCushions(int seed)
    {
        var world = CreateWorld(seed);
        for (var visit = 1; visit <= 6; visit++)
        {
            Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
            var sofa = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == Sofa &&
                PortalWorld.TryGetApartmentFurnitureVisit(obj.Variant, out _));
            Assert.That(FurnitureSeats.TryGetFrame(world, sofa, out var center, out var forward, out var left));
            var stands = new JunctionId[2];
            for (var seat = 0; seat < 2; seat++)
            {
                Assert.That(FurnitureSeats.TryGetSeatStand(world, sofa, seat, out stands[seat]), Is.True,
                    $"seed {seed} visit {visit} seat {seat}");
                var relative = world.Junctions.Items[stands[seat]].WorldPosition - center;
                var ahead = relative.X * forward.X + relative.Y * forward.Y;
                var side = relative.X * left.X + relative.Y * left.Y;
                TestContext.Out.WriteLine(
                    $"seed {seed} visit {visit} seat {seat}: stand ahead {ahead:F3} side {side:F3} " +
                    $"(cushion side {FurnitureSeats.SeatLateral(Sofa, seat):F3})");
                Assert.That(ahead, Is.InRange(.3f, 1.0f));
                Assert.That(System.MathF.Sign(side), Is.EqualTo(System.MathF.Sign(FurnitureSeats.SeatLateral(Sofa, seat))));
                Assert.That(FurnitureSeats.SeatAt(world, sofa, stands[seat]), Is.EqualTo(seat));
            }

            Assert.That(stands[0], Is.Not.EqualTo(stands[1]), $"seed {seed} visit {visit}");
            Assert.That(HexSpatialMath.Distance(world.Junctions.Items[stands[0]].WorldPosition,
                world.Junctions.Items[stands[1]].WorldPosition), Is.GreaterThanOrEqualTo(.37f));
            Assert.That(world.Portal.Visitor, Is.Null);
        }
    }

    [TestCase(0f)]
    [TestCase(60f)]
    [TestCase(120f)]
    [TestCase(180f)]
    [TestCase(240f)]
    [TestCase(300f)]
    public void ASofaCarriedOutAndPlacedOnTheIslandKeepsBothSeatsInEveryYaw(float yaw)
    {
        var world = CreateWorld(0);
        world.FactionHomes.Remove(Faction.Colony2);
        var tile = world.Tiles.Items[TileCoord.Zero];
        var target = StructurePlacement.CenterJunction(world, tile.Coord).Value;
        var origin = world.Junctions.Items.Values.First(point =>
            point.Fragment == world.Junctions.Items[target].Fragment && !point.Blocked &&
            HexSpatialMath.Distance(point.WorldPosition, HexSpatialMath.TileToWorld(tile.Coord)) is > 1.3f and < 1.6f);
        var actor = AddNpc(world, 1, origin.Id);
        actor.Mind.ManualControl = true;
        actor.Inventory.Items.Add(new ItemInstance(Sofa));
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        var reference = new InventoryItemRef(InventoryItemSource.Carried, 0, Sofa);
        var receipt = engine.ApplyManualCommand(
            new PlaceInventoryFurnitureCommand(actor.Id, reference, target, tile.Coord, yaw));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);
        var sofa = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == Sofa);

        Assert.That(FurnitureSeats.TryGetFrame(world, sofa, out var center, out var forward, out var left));
        var stands = new JunctionId[2];
        for (var seat = 0; seat < 2; seat++)
        {
            Assert.That(FurnitureSeats.TryGetSeatStand(world, sofa, seat, out stands[seat]), Is.True, $"seat {seat}");
            var relative = world.Junctions.Items[stands[seat]].WorldPosition - center;
            TestContext.Out.WriteLine(
                $"yaw {sofa.RotationDegrees}: seat {seat} stand ahead " +
                $"{relative.X * forward.X + relative.Y * forward.Y:F3} side {relative.X * left.X + relative.Y * left.Y:F3}");
        }

        Assert.That(stands[0], Is.Not.EqualTo(stands[1]));
    }

    [TestCase(0)]
    [TestCase(629209070)]
    public void TwoPeopleSitOnTheSameSofaAtTheSameTimeOnDifferentCushions(int seed)
    {
        var world = CreateWorld(seed);
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
        var sofa = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == Sofa &&
            PortalWorld.TryGetApartmentFurnitureVisit(obj.Variant, out _));
        var free = FreeLivingJunctions(world, sofa).ToArray();
        Assert.That(free.Length, Is.GreaterThanOrEqualTo(3));

        // The first one is a player-controlled order, the second decides on
        // her own: both doors into the seat rules are exercised.
        var first = AddNpc(world, 1, free[0]);
        first.Mind.ManualControl = true;
        var second = AddNpc(world, 2, free[1]);
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        engine.Register(new ObjectReservationSystem());

        var order = engine.ApplyManualCommand(new InteractCommand(first.Id, sofa.Id, InteractionType.Sit));
        Assert.That(order.Accepted, Is.True, order.Reason);
        for (var i = 0; i < 200 && first.Execution.CurrentInteraction != InteractionType.Sit; i++) engine.Step();
        Assert.That(first.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sit));
        Assert.That(sofa.IsOccupied, Is.True);

        // She sees the sofa through ordinary perception: one cushion is free.
        new PerceptionSystem().Run(world);
        var seen = second.Perception.Objects.Single(obj => obj.Id == sofa.Id);
        Assert.That(seen.IsOccupied, Is.False, "a sofa with a free cushion must not look taken");
        Assert.That(PlanningSystem.HasObjectCandidateForGoal(world, second, GoalType.Sit, sofa.Id), Is.True);
        second.Mind.CurrentGoal = GoalType.Sit;
        new PlanningSystem().Run(world);
        Assert.That(second.Plan.TargetObjectId, Is.EqualTo(sofa.Id));

        var together = 0;
        for (var i = 0; i < 200; i++)
        {
            engine.Step();
            if (first.Execution.CurrentInteraction == InteractionType.Sit &&
                second.Execution.CurrentInteraction == InteractionType.Sit)
            {
                together++;
                break;
            }
        }

        Assert.That(together, Is.EqualTo(1), "both must be sitting at the same tick");
        var firstSeat = FurnitureSeats.SeatAt(world, sofa, first.CurrentJunction);
        var secondSeat = FurnitureSeats.SeatAt(world, sofa, second.CurrentJunction);
        TestContext.Out.WriteLine(
            $"seed {seed}: seats {firstSeat}/{secondSeat}, bodies " +
            $"{HexSpatialMath.Distance(first.Position, second.Position):F3} wu apart");
        Assert.Multiple(() =>
        {
            Assert.That(first.Execution.TargetObject, Is.EqualTo(sofa.Id));
            Assert.That(second.Execution.TargetObject, Is.EqualTo(sofa.Id));
            Assert.That(new[] { firstSeat, secondSeat }, Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(first.CurrentJunction, Is.Not.EqualTo(second.CurrentJunction));
            Assert.That(HexSpatialMath.Distance(first.Position, second.Position), Is.GreaterThanOrEqualTo(.37f));
            Assert.That(PortalWorld.TryGetModernSeatFacing(Sofa, sofa.RotationDegrees, out var facing));
            Assert.That(MathUtil.DeltaAngle(first.RotationDegrees, facing), Is.EqualTo(0f).Within(.6f));
            Assert.That(MathUtil.DeltaAngle(second.RotationDegrees, facing), Is.EqualTo(0f).Within(.6f));
        });

        // A third person finds both cushions taken.
        var third = AddNpc(world, 3, free[2]);
        third.Mind.ManualControl = true;
        var refused = engine.ApplyManualCommand(new InteractCommand(third.Id, sofa.Id, InteractionType.Sit));
        Assert.That(refused.Accepted, Is.False);
        Assert.That(refused.Reason, Does.Contain("Occupied"));

        // Whoever leaves first, the sofa stays claimed while the other sits:
        // it cannot be picked up from under her.
        var owner = sofa.CurrentUser == first.Id ? first : second;
        var stayer = owner == first ? second : first;
        PlanInterruption.TryAbort(world, owner, InterruptionCause.ExecutionFailure, "test: stands up");
        engine.Step();
        Assert.That(stayer.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sit));
        Assert.That(sofa.IsOccupied, Is.True);
        Assert.That(sofa.CurrentUser, Is.EqualTo(stayer.Id));
    }

    private static System.Collections.Generic.IEnumerable<JunctionId> FreeLivingJunctions(
        WorldState world, WorldObjectState sofa)
    {
        var anchor = sofa.Junctions[0];
        var stands = Enumerable.Range(0, 2).Select(seat =>
            FurnitureSeats.TryGetSeatStand(world, sofa, seat, out var stand) ? stand : default).ToHashSet();
        return world.Junctions.Items.Values
            .Where(point => point.Fragment == PortalWorld.ApartmentFragment && !point.Blocked && !point.Door &&
                point.Tiles.All(tile => PortalWorld.RoomAt(tile, world.Seed, world.Portal.VisitSerial) ==
                    PortalApartmentRoom.Living) &&
                !stands.Contains(point.Id) && SpatialQueries.IsJunctionFree(world, point.Id) &&
                Connectivity.Reachable(world, point.Id, stands.First(), false) &&
                HexSpatialMath.Distance(point.WorldPosition, world.Junctions.Items[anchor].WorldPosition) > 1.2f)
            .OrderBy(point => point.Id.Value)
            .Select(point => point.Id);
    }

    private static NPCState AddNpc(WorldState world, int id, JunctionId at)
    {
        var junction = world.Junctions.Items[at];
        var npc = new NPCState
        {
            Id = new EntityId(id), Faction = Faction.Colony, Tile = junction.Tiles[0],
            CurrentJunction = at, Fragment = junction.Fragment, Position = junction.WorldPosition,
        };
        PopulationArrivalMath.AddToWorld(world, npc, at);
        return npc;
    }

    private static WorldState CreateWorld(int seed)
    {
        var bootstrap = new HexLive.Simulation.Bootstrap.WorldBootstrapDefinition();
        bootstrap.Simulation.Seed = seed;
        bootstrap.Simulation.EnablePortalTopology = true;
        var island = new HexLive.Simulation.Bootstrap.FragmentBootstrap { Id = 1 };
        for (var q = -4; q <= 4; q++)
        for (var r = -4; r <= 4; r++)
            if (HexSpatialMath.HexDistance(new TileCoord(q, r), TileCoord.Zero) <= 4)
                island.Tiles.Add(new HexLive.Simulation.Bootstrap.TileBootstrap { Q = q, R = r });
        bootstrap.Fragments.Add(island);
        var world = new HexLive.Simulation.Bootstrap.WorldStateFactory().CreateTopology(bootstrap);
        world.FactionHomes[Faction.Colony] = TileCoord.Zero;
        world.FactionHomes[Faction.Colony2] = new TileCoord(3, 0);
        return world;
    }
}
