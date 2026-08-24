using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§146.12: neutral visits, hostility motives and real camp merges.</summary>
public sealed class CampDiplomacyTests
{
    private static WorldState Build(int seed = 12345)
    {
        SimTrace.EnableAll();
        return new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(seed, GameMode.HugeIsland));
    }

    private static NPCState Girl(WorldState world, Faction faction) =>
        world.Entities.Npcs.Values.Single(n => n.Faction == faction);

    private static (WorldState world, NPCState observer, NPCState neighbour,
        NPCState outsider) BuildSmallSoloCampWorld(int seed)
    {
        var world = TestWorld.CreateWorld(seed);
        world.Mode = GameMode.HugeIsland;
        var girls = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .ToArray();
        Assert.That(girls, Has.Length.GreaterThanOrEqualTo(2));
        girls[1].Faction = Faction.Colony2;
        var outsider = world.Entities.Npcs.Values.Single(n =>
            n.Faction == Faction.Outsiders);
        new PerceptionSystem().Run(world);
        return (world, girls[0], girls[1], outsider);
    }

    private static void MoveBeside(
        WorldState world, NPCState person, NPCState anchor)
    {
        var destinationId = SpatialQueries.GetPassableNeighbors(
                world, anchor.CurrentJunction!.Value)
            .First(id => SpatialQueries.IsJunctionFree(world, id));
        var destination = world.Junctions.Items[destinationId];
        if (person.CurrentJunction is { } previousJunction)
        {
            SpatialMutations.FreeJunction(world, previousJunction, person.Id);
            SpatialMutations.ReleaseJunctionReservation(
                world, previousJunction, person.Id);
        }

        var previousTile = person.Tile;
        person.Tile = destination.Tiles.Count > 0
            ? destination.Tiles[0]
            : anchor.Tile;
        person.Fragment = destination.Fragment;
        person.Position = destination.WorldPosition;
        person.CurrentJunction = destinationId;
        SpatialMutations.MoveEntityToTile(
            world, person.Id, previousTile, person.Tile);
        SpatialMutations.OccupyJunction(world, destinationId, person.Id);
    }

    private static void MoveOutOfSight(
        WorldState world, NPCState person, NPCState anchor)
    {
        if (person.CurrentJunction is { } previousJunction)
        {
            SpatialMutations.FreeJunction(world, previousJunction, person.Id);
            SpatialMutations.ReleaseJunctionReservation(
                world, previousJunction, person.Id);
        }

        var previousTile = person.Tile;
        person.Tile = new TileCoord(anchor.Tile.Q + 1000, anchor.Tile.R + 1000);
        person.Position = HexSpatialMath.TileToWorld(person.Tile);
        person.CurrentJunction = null;
        SpatialMutations.MoveEntityToTile(
            world, person.Id, previousTile, person.Tile);
    }

    private static void NeutralizeIncomingRelations(
        WorldState world, NPCState observer)
    {
        foreach (var person in world.Entities.Npcs.Values)
        {
            if (!person.Id.Equals(observer.Id))
            {
                person.Social.GetOrCreate(observer.Id).Affinity = 0f;
            }
        }
    }

    [Test]
    public void SoloCampsAreNeutralUntilHatredAndLootNeedsAMotive()
    {
        var world = Build();
        var host = Girl(world, Faction.Colony);
        var guest = Girl(world, Faction.Colony2);

        Assert.Multiple(() =>
        {
            Assert.That(FactionRelations.AreNeutral(world, host, guest), Is.True);
            Assert.That(FactionRelations.AreHostile(world, host, guest), Is.False);
            Assert.That(CampDiplomacyMath.CanLoot(world, host, guest), Is.False,
                "Сытую нейтральную соседку нельзя обыскивать по фракционному ярлыку.");
        });

        CampExpulsionSystem.BeginChallenge(world, host, guest);
        Assert.That(host.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Expel),
            "Нейтральная гостья не является нарушительницей лагеря.");

        var oldGuestTile = guest.Tile;
        guest.Tile = host.Tile;
        guest.Fragment = host.Fragment;
        guest.Position = host.Position;
        guest.CurrentJunction = null;
        SpatialMutations.MoveEntityToTile(world, guest.Id, oldGuestTile, guest.Tile);
        new PerceptionSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(host.Perception.Agents.Any(a => a.Id.Equals(guest.Id)), Is.True,
                "Нейтральная гостья должна попадать в обычный социальный список.");
            Assert.That(host.Perception.Hostiles.Any(a => a.Id.Equals(guest.Id)), Is.False);
        });

        host.Needs.Hunger = CampDiplomacyMath.DesperateLootHungerThreshold;
        Assert.That(CampDiplomacyMath.CanLoot(world, host, guest), Is.True,
            "Почти смертельный голод — отдельный мотив лута.");

        host.Needs.Hunger = 0f;
        host.Social.GetOrCreate(guest.Id).Affinity =
            CampDiplomacyMath.HatredAffinityThreshold;
        Assert.Multiple(() =>
        {
            Assert.That(FactionRelations.AreHostile(world, host, guest), Is.True,
                "Личная ненависть делает вражду направленной.");
            Assert.That(FactionRelations.AreHostile(world, guest, host), Is.False,
                "Нейтральная ответная сторона не должна мгновенно наследовать чужую ненависть.");
            Assert.That(CampDiplomacyMath.CanLoot(world, host, guest), Is.True);
        });
    }

    [Test]
    public void NeutralHumansDoNotAlarmWakeOrStressUntilPersonalHatred()
    {
        var (world, observer, neighbour, outsider) =
            BuildSmallSoloCampWorld(1461201);
        MoveOutOfSight(world, outsider, observer);
        MoveBeside(world, neighbour, observer);
        NeutralizeIncomingRelations(world, observer);
        observer.Needs.Hunger = 0.1f;
        observer.Needs.Thirst = 0.1f;
        observer.Health = 1f;
        neighbour.Social.GetOrCreate(observer.Id).Affinity = 0f;

        new PerceptionSystem().Run(world);
        observer.Needs.Stress = 0.5f;
        new NeedsDecaySystem().Run(world);
        Assert.That(observer.Needs.Stress,
            Is.EqualTo(0.5f - SimBalance.StressDownRate).Within(0.000001f),
            "Нейтральная соседка не является источником стресса.");

        neighbour.Social.GetOrCreate(observer.Id).Affinity = -0.4f;
        new PerceptionSystem().Run(world);
        observer.Needs.Stress = 0.5f;
        new NeedsDecaySystem().Run(world);
        Assert.That(observer.Needs.Stress,
            Is.EqualTo(0.5f + SimBalance.StressUpRate * 0.4f)
                .Within(0.000001f),
            "Страх должен зависеть от направленной ненависти к наблюдательнице.");

        neighbour.Social.GetOrCreate(observer.Id).Affinity = 0f;
        MoveBeside(world, outsider, observer);
        observer.Social.GetOrCreate(outsider.Id).Affinity = 0f;
        outsider.Social.GetOrCreate(observer.Id).Affinity = 0f;
        new PerceptionSystem().Run(world);
        observer.Needs.Stress = 0.5f;
        new NeedsDecaySystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(observer.Perception.Agents.Any(a => a.Id.Equals(outsider.Id)), Is.True,
                "Нейтральный чужак должен оставаться обычным видимым человеком.");
            Assert.That(observer.Perception.Hostiles.Any(a => a.Id.Equals(outsider.Id)), Is.False);
            Assert.That(observer.Needs.Stress,
                Is.EqualTo(0.5f - SimBalance.StressDownRate).Within(0.000001f));
            Assert.That(ExecutionSystem.GetSleepInterruptReason(
                world, observer, alreadyAsleep: true), Is.Null,
                "Нейтральный чужак не должен будить спящего.");
            Assert.That(PathfindingSystem.HostileRing(world, observer),
                Does.Not.Contain(outsider.CurrentJunction!.Value),
                "Маршрут не должен огибать нейтрального человека как угрозу.");
        });

        observer.Social.GetOrCreate(outsider.Id).Affinity =
            CampDiplomacyMath.HatredAffinityThreshold;
        world.Tick++;
        new PerceptionSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(observer.Perception.Hostiles.Any(a => a.Id.Equals(outsider.Id)), Is.True);
            Assert.That(ExecutionSystem.GetSleepInterruptReason(
                world, observer, alreadyAsleep: true), Is.EqualTo("SleepDanger"));
            Assert.That(PathfindingSystem.HostileRing(world, observer),
                Does.Contain(outsider.CurrentJunction!.Value));
        });
    }

    [Test]
    public void ActiveCrossCampConversationRelievesEachFriendByHerOwnAffinity()
    {
        var (world, initiator, listener, _) =
            BuildSmallSoloCampWorld(1461202);
        var outsider = world.Entities.Npcs.Values.Single(n =>
            n.Faction == Faction.Outsiders);
        MoveOutOfSight(world, outsider, initiator);
        MoveBeside(world, listener, initiator);
        NeutralizeIncomingRelations(world, initiator);
        NeutralizeIncomingRelations(world, listener);
        initiator.Needs.Hunger = listener.Needs.Hunger = 0.1f;
        initiator.Needs.Thirst = listener.Needs.Thirst = 0.1f;
        initiator.Health = listener.Health = 1f;
        initiator.Needs.Stress = listener.Needs.Stress = 0.5f;
        initiator.Social.GetOrCreate(listener.Id).Affinity = 0.8f;
        listener.Social.GetOrCreate(initiator.Id).Affinity = 0.25f;
        initiator.Execution.Status = ExecutionStatus.InProgress;
        initiator.Execution.CurrentInteraction = InteractionType.Talk;
        initiator.Plan.TargetAgentId = listener.Id;

        new PerceptionSystem().Run(world);
        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(initiator.Needs.Stress,
                Is.EqualTo(0.5f - SimBalance.StressDownRate * 1.8f)
                    .Within(0.000001f));
            Assert.That(listener.Needs.Stress,
                Is.EqualTo(0.5f - SimBalance.StressDownRate * 1.25f)
                    .Within(0.000001f),
                "Пассивная слушательница тоже получает своё направленное облегчение.");
        });
    }

    [Test]
    public void FriendshipScalesCrossCampAidDyingNeutralIsRescuedAndOutsiderIsExcluded()
    {
        var (world, helper, patient, outsider) =
            BuildSmallSoloCampWorld(1461203);
        MoveBeside(world, patient, helper);
        patient.Health = 0.3f;
        patient.Needs.Hunger = 0.1f;
        patient.Needs.Thirst = 0.1f;
        patient.Needs.Blood = 1f;
        helper.Social.GetOrCreate(patient.Id).Affinity = 0f;

        new PerceptionSystem().Run(world);
        var neutralView = helper.Perception.Agents.Single(a =>
            a.Id.Equals(patient.Id));
        Assert.Multiple(() =>
        {
            Assert.That(neutralView.AidKind, Is.EqualTo(AidKind.None));
            Assert.That(neutralView.Suffering, Is.Zero);
        });

        helper.Social.GetOrCreate(patient.Id).Affinity = 0.8f;
        new PerceptionSystem().Run(world);
        var friendView = helper.Perception.Agents.Single(a =>
            a.Id.Equals(patient.Id));
        Assert.Multiple(() =>
        {
            Assert.That(friendView.AidKind, Is.EqualTo(AidKind.Medicate));
            Assert.That(friendView.Suffering,
                Is.EqualTo(0.7f * 0.8f).Within(0.000001f),
                "Тяжесть обычной помощи масштабируется дружбой помощницы.");
            Assert.That(CampDiplomacyMath.CareWillingness(
                world, helper, outsider), Is.Zero,
                "Женский лагерь не помогает Outsiders даже при ручной симпатии.");
        });

        helper.Social.GetOrCreate(patient.Id).Affinity = 0f;
        patient.Mind.DyingCause = DyingCause.BloodLoss;
        Assert.That(CampDiplomacyMath.CareWillingness(world, helper, patient),
            Is.EqualTo(CampDiplomacyMath.EmergencyCareFloor));

        helper.Mind.CurrentGoal = GoalType.None;
        helper.Plan.Status = PlanStatus.None;
        helper.Execution.Status = ExecutionStatus.None;
        new RescueSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.Rescue));
            Assert.That(helper.Plan.TargetAgentId, Is.EqualTo(patient.Id));
            Assert.That(patient.Mind.PendingAidFrom, Is.EqualTo(helper.Id));
        });

        patient.Mind.DyingCause = DyingCause.None;
        Assert.That(CampDiplomacyMath.CareWillingness(world, helper, patient),
            Is.EqualTo(CampDiplomacyMath.EmergencyCareFloor),
            "Принятое спасение продолжается после первичной стабилизации.");
    }

    [Test]
    public void KnownFriendlyCampTurnsExploreIntoAThreeToEightTileVisitLeg()
    {
        var world = Build(31337);
        var visitor = Girl(world, Faction.Colony);
        var neighbour = Girl(world, Faction.Colony2);
        var home = world.FactionHomes[Faction.Colony2];
        visitor.Needs.Social = 0f;
        visitor.Needs.Hunger = 0f;
        visitor.Needs.Thirst = 0f;
        visitor.Needs.Energy = 1f;
        visitor.Needs.Comfort = 1f;
        // WorldStateFactory authors positions; the first perception pass binds
        // them to live junctions exactly as the simulation's opening tick does.
        new PerceptionSystem().Run(world);
        visitor.Memory.KnownAgents[neighbour.Id] = new AgentMemory
        {
            Id = neighbour.Id,
            Faction = neighbour.Faction,
            Tile = neighbour.Tile,
            Junction = neighbour.CurrentJunction,
            LastSeenTick = world.Tick
        };

        Assert.That(CampDiplomacyMath.TryFindVisitCamp(
            world, visitor, out var faction, out var rememberedHome), Is.True);
        Assert.That(faction, Is.EqualTo(Faction.Colony2));
        Assert.That(rememberedHome, Is.EqualTo(home));
        Assert.That(visitor.CurrentJunction, Is.Not.Null);
        Assert.That(PlanningSystem.HasExploreCandidate(world, visitor), Is.True,
            $"No Explore candidate from {visitor.Tile} / {visitor.CurrentJunction}");

        var before = HexSpatialMath.HexDistance(visitor.Tile, home);
        visitor.Mind.CurrentGoal = GoalType.Explore;
        visitor.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);
        var trace = string.Join("\n", world.Events.Items
            .Where(e => e.EntityId.Equals(visitor.Id))
            .Select(e => $"{e.Type}: {e.Message}"));

        Assert.Multiple(() =>
        {
            Assert.That(visitor.Plan.Status, Is.EqualTo(PlanStatus.Active), trace);
            Assert.That(visitor.Plan.TargetTile, Is.Not.Null, trace);
            var leg = HexSpatialMath.HexDistance(visitor.Tile, visitor.Plan.TargetTile!.Value);
            var after = HexSpatialMath.HexDistance(visitor.Plan.TargetTile.Value, home);
            Assert.That(leg, Is.InRange(3, 8), "Визит состоит из обычных Explore-переходов.");
            Assert.That(after, Is.LessThan(before), "Каждый переход обязан приближать к лагерю.");
        });
    }

    [Test]
    public void ManualMergeRequiresMutualMajorityAndCanChooseNeighbourHome()
    {
        var world = Build(777);
        var player = Girl(world, Faction.Colony);
        var neighbour = Girl(world, Faction.Colony2);
        var witness = Girl(world, Faction.Colony3);
        var neighbourHome = world.FactionHomes[Faction.Colony2];
        player.Mind.ManualControl = true;
        neighbour.Position = player.Position;
        player.Social.GetOrCreate(neighbour.Id).Affinity =
            CampDiplomacyMath.MergeAffinityThreshold;
        neighbour.Social.GetOrCreate(player.Id).Affinity = 0.75f;

        var refused = ManualCommandExecutor.Apply(world,
            new MergeCampsCommand(player.Id, neighbour.Id, useTargetCamp: true));
        Assert.Multiple(() =>
        {
            Assert.That(refused.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(refused.Reason, Is.EqualTo("RelationshipTooLow"),
                "Ровно 50% недостаточно: порог должен быть строго превышен обеими сторонами.");
        });

        player.Social.GetOrCreate(neighbour.Id).Affinity = 0.51f;
        witness.Memory.KnownAgents[neighbour.Id] = new AgentMemory
        {
            Id = neighbour.Id,
            Faction = Faction.Colony2,
            Tile = neighbour.Tile
        };
        world.ColonyArrivalsProcessedByFaction[Faction.Colony] = 2;
        world.ColonyArrivalsProcessedByFaction[Faction.Colony2] = 5;

        var accepted = ManualCommandExecutor.Apply(world,
            new MergeCampsCommand(player.Id, neighbour.Id, useTargetCamp: true));

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                accepted.Reason);
            Assert.That(player.Faction, Is.EqualTo(Faction.Colony));
            Assert.That(neighbour.Faction, Is.EqualTo(Faction.Colony));
            Assert.That(world.FactionHomes.ContainsKey(Faction.Colony2), Is.False);
            Assert.That(world.FactionHomes[Faction.Colony], Is.EqualTo(neighbourHome),
                "«Занять этот лагерь» выбирает дом собеседницы основным.");
            Assert.That(world.ColonyArrivalsProcessedByFaction[Faction.Colony], Is.EqualTo(5));
            Assert.That(witness.Memory.KnownAgents[neighbour.Id].Faction,
                Is.EqualTo(Faction.Colony));
            Assert.That(world.Events.Items.Count(e => e.Type == "CampsMerged"), Is.EqualTo(1));
            Assert.That(player.Journal.PendingType, Is.EqualTo("CampsMerged"));
            Assert.That(neighbour.Journal.PendingType, Is.EqualTo("CampsMerged"),
                "Зеркальная запись должна попасть и переговорщице соседнего лагеря.");
        });
    }

    [Test]
    public void AutomaticMergeChoosesTheCampWithMoreInfrastructure()
    {
        var world = Build(4242);
        var first = Girl(world, Faction.Colony4);
        var second = Girl(world, Faction.Colony5);
        var firstHome = world.FactionHomes[first.Faction];
        var secondHome = world.FactionHomes[second.Faction];
        first.Social.GetOrCreate(second.Id).Affinity = 0.75f;
        second.Social.GetOrCreate(first.Id).Affinity = 0.75f;

        var firstScore = CampDiplomacyMath.CampInfrastructureScore(world, firstHome);
        var secondScore = CampDiplomacyMath.CampInfrastructureScore(world, secondHome);
        var bedsNeeded = System.Math.Max(1, (firstScore - secondScore) / 8 + 1);
        var nextObjectId = world.Entities.Objects.Keys.Max(id => id.Value) + 1;
        for (var i = 0; i < bedsNeeded; i++)
        {
            var id = new ObjectId(nextObjectId + i);
            world.Entities.Objects[id] = new WorldObjectState
            {
                Id = id,
                DefinitionId = ContentIds.BedBasic,
                Tile = secondHome
            };
        }

        Assert.That(CampDiplomacyMath.TryMerge(
            world, first, second, CampHomeChoice.Automatic, out var reason), Is.True,
            reason);
        Assert.That(world.FactionHomes[first.Faction], Is.EqualTo(secondHome),
            "Автономный союз выбирает лагерь с большей реальной инфраструктурой.");
    }
}

}
