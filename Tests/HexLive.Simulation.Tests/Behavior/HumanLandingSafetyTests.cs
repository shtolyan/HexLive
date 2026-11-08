using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class HumanLandingSafetyTests
{
    [Test]
    public void HumanLandingRejectsLivePredatorsAndVirtualPatrols()
    {
        var world = TestWorld.CreateWorld();
        var home = ColonyQueries.Home(world, Faction.Colony).Value;
        Assert.That(PopulationArrivalMath.TryPickLanding(world, home, 1, 132, out var original), Is.True);
        world.Mobs.Add(new MobState { Id = 900, Tile = home, Health = 1f });
        Assert.That(PopulationArrivalMath.TryPickLanding(world, home, 1, 132, out _), Is.False);
        world.Mobs.Clear();
        var slot = new MobSpawnSlot { SlotId = 900, MobId = MobIds.Dog, State = MobSlotState.Virtual };
        slot.Ring.Add(new PatrolWaypoint { Tile = home, Position = original.Position, Junction = original.Junction });
        world.MobSpawnSlots.Add(slot);
        Assert.That(PopulationArrivalMath.TryPickLanding(world, home, 1, 132, out _), Is.False);
        slot.State = MobSlotState.Cooldown;
        Assert.That(PopulationArrivalMath.TryPickLanding(world, home, 1, 132, out _), Is.True);
    }
}
}
