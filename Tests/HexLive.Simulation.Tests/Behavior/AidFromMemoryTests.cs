using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§125.7: «ушла за дровами, но помнит, что дома лежит раненая».
/// Память хранит ВЕРДИКТ последней встречи — чем помочь и насколько плохо, —
/// и этого хватает, чтобы вернуться. Живая страдающая всегда важнее
/// вспомненной, а протухшая вера основанием для похода не является.</summary>
[NonParallelizable]
public sealed class AidFromMemoryTests
{
    [Test]
    public void SeeingAWoundedFriendRecordsTheVerdict()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (helper, ward) = Pair(world);
        helper.Attributes.Perception = 0.5f;

        Wound(ward);
        Place(world, ward, new TileCoord(helper.Tile.Q + 2, helper.Tile.R));
        world.Tick = 500;
        perception.Run(world);

        var met = helper.Memory.KnownAgents[ward.Id];
        Assert.Multiple(() =>
        {
            Assert.That(met.AidKind, Is.Not.EqualTo(AidKind.None),
                "вердикт «чем помочь» обязан лечь в память вместе со встречей");
            Assert.That(met.Suffering, Is.GreaterThan(0f));
            Assert.That(met.Junction, Is.Not.Null,
                "без узла план помощи по памяти построить не из чего");
        });
    }

    [Test]
    public void OutOfSightWardBecomesARememberedEntry()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (helper, ward) = Pair(world);
        helper.Attributes.Perception = 0.4f;

        Wound(ward);
        Place(world, ward, new TileCoord(helper.Tile.Q + 2, helper.Tile.R));
        world.Tick = 500;
        perception.Run(world);
        Assert.That(helper.Perception.Remembered, Is.Empty,
            "пока видит — это зрение, а не память");

        // Ушла за дровами: подруга осталась дома, из глаз пропала.
        Place(world, helper, new TileCoord(helper.Tile.Q + 9, helper.Tile.R));
        world.Tick = 560;
        perception.Run(world);

        var remembered = helper.Perception.Remembered
            .SingleOrDefault(r => r.Id.Equals(ward.Id));
        Assert.That(remembered, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(helper.Perception.Agents.Any(a => a.Id.Equals(ward.Id)), Is.False);
            Assert.That(remembered.Age, Is.EqualTo(60));
            Assert.That(remembered.AidKind, Is.Not.EqualTo(AidKind.None));
            Assert.That(remembered.Junction, Is.Not.Null);
        });
    }

    [Test]
    public void StaleBeliefStopsBeingAReasonToWalk()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (helper, ward) = Pair(world);
        helper.Attributes.Perception = 0.4f;

        Wound(ward);
        Place(world, ward, new TileCoord(helper.Tile.Q + 2, helper.Tile.R));
        world.Tick = 500;
        perception.Run(world);

        Place(world, helper, new TileCoord(helper.Tile.Q + 9, helper.Tile.R));
        world.Tick = 500 + Spec53.AidMemoryMaxAgeTicks + 1;
        perception.Run(world);

        var remembered = helper.Perception.Remembered
            .SingleOrDefault(r => r.Id.Equals(ward.Id));
        Assert.That(remembered, Is.Not.Null, "запись ещё жива — истёк лишь срок ДОВЕРИЯ");
        Assert.That(remembered.Age, Is.GreaterThan(Spec53.AidMemoryMaxAgeTicks),
            "возраст веры перевалил порог: идти по ней больше нельзя");
    }

    [Test]
    public void MemoryOfAHealthyFriendIsNotACallForHelp()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (helper, ward) = Pair(world);
        helper.Attributes.Perception = 0.4f;

        Healthy(ward);
        Place(world, ward, new TileCoord(helper.Tile.Q + 2, helper.Tile.R));
        world.Tick = 500;
        perception.Run(world);

        Place(world, helper, new TileCoord(helper.Tile.Q + 9, helper.Tile.R));
        world.Tick = 520;
        perception.Run(world);

        var remembered = helper.Perception.Remembered
            .SingleOrDefault(r => r.Id.Equals(ward.Id));
        Assert.That(remembered, Is.Not.Null);
        Assert.That(remembered.AidKind, Is.EqualTo(AidKind.None),
            "здоровую помнят как здоровую — за ней не бегут");
    }

    private static void Healthy(NPCState npc)
    {
        // Прототипные девушки стартуют голодными (0.5..0.7), поэтому «здоровая»
        // в этом тесте — это явно сытая и напоенная, иначе вердикт честно
        // скажет Feed, и тест мерил бы стартовый баланс, а не память.
        npc.Needs.Hunger = 0.1f;
        npc.Needs.Thirst = 0.1f;
        npc.Needs.Stress = 0f;
        npc.Needs.Blood = 1f;
        npc.Health = 1f;
    }

    private static void Wound(NPCState npc)
    {
        // Кровь на дне — AidAssessment сворачивает это в Treat с высокой
        // тяжестью, теми же правилами, что и для живого взгляда.
        npc.Needs.Blood = 0.3f;
        npc.Health = 0.5f;
    }

    private static (NPCState Helper, NPCState Ward) Pair(WorldState world)
    {
        var colonists = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .OrderBy(npc => npc.Id.Value)
            .ToArray();
        Assert.That(colonists.Length, Is.GreaterThanOrEqualTo(2));

        foreach (var other in colonists.Skip(2))
        {
            Place(world, other, new TileCoord(colonists[0].Tile.Q + 14, colonists[0].Tile.R));
        }

        return (colonists[0], colonists[1]);
    }

    private static void Place(WorldState world, NPCState npc, TileCoord tile)
    {
        SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, tile);
        npc.Tile = tile;
        npc.Position = HexSpatialMath.TileToWorld(tile);
        // Узел обязателен: именно от него строится подход, поэтому память без
        // него бесполезна, а тест без него мерил бы не то.
        npc.CurrentJunction = SpatialQueries.FindNearestJunction(world, npc.Position);
    }
}

}
