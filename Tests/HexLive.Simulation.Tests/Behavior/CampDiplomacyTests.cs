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
