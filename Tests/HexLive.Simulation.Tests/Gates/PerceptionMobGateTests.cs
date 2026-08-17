using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §125.4 / §144.7: зверь обязан попадать в восприятие — и по ЕЁ радиусу.
/// <para>
/// Гейт существует из-за конкретной живой аварии. Внешний контур управления
/// обещал в описании инструмента, что <c>mobId</c> берётся из сводки
/// восприятия, — а мобов в восприятии не было вовсе. Обещание прожило до
/// первого настоящего агента: на колонистку напал зверь, наружу приехало
/// <c>fighting=true</c> при ПУСТОМ списке врагов (там только люди), и контур не
/// мог ни назвать напавшего, ни ударить в ответ.
/// </para>
/// <para>
/// Второй смысл гейта — не дать подменить радиус. Соблазн взять
/// <c>AiBalance.PerceptionRadiusTiles</c> велик: он рядом, он публичный и он
/// называется почти так же. Но это радиус ОБЪЕКТОВ. Возьми его — и зоркая
/// перестанет видеть волка, которого видит сторожевой проход, а слепая получит
/// право бить того, кого не видит.
/// </para>
/// </summary>
[NonParallelizable]
public sealed class PerceptionMobGateTests
{
    [Test]
    public void MobRadiusIsHerOwn_NotTheFlatObjectConstant()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var observer = Colonists(world)[0];

        // Пятый гекс: дальше объектной константы (3) и ближе зоркого глаза.
        var tile = TileAtDistance(world, observer.Tile, 5);
        SpawnMob(world, id: 1, tile);

        observer.Attributes.Perception = 0f; // пол — три гекса
        perception.Run(world);
        Assert.That(observer.Perception.Mobs, Is.Empty,
            "на пятом гексе ненаблюдательная зверя не видит");

        observer.Attributes.Perception = 0.5f; // радиус 8
        perception.Run(world);
        Assert.That(observer.Perception.Mobs.Select(m => m.Id), Does.Contain(1),
            "зоркая обязана увидеть того же зверя — радиус личный, а не общий");

        Assert.That(AiBalance.PerceptionRadiusTiles, Is.LessThan(5),
            "Если объектная константа однажды дорастёт до пяти, этот тест " +
            "перестанет различать две формулы и начнёт врать про покрытие.");
    }

    [Test]
    public void DeadMobIsNotPerceived()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var observer = Colonists(world)[0];
        observer.Attributes.Perception = 0.5f;

        var mob = SpawnMob(world, id: 2, TileAtDistance(world, observer.Tile, 2));
        perception.Run(world);
        Assert.That(observer.Perception.Mobs, Is.Not.Empty);

        mob.Health = 0f;
        perception.Run(world);
        Assert.That(observer.Perception.Mobs, Is.Empty,
            "труп зверя — не цель для атаки, и предлагать его как цель нельзя");
    }

    [Test]
    public void TargetsMeSeparatesTheOneComingForHerFromThePackWalkingBy()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var colonists = Colonists(world);
        var observer = colonists[0];
        var other = colonists[1];
        observer.Attributes.Perception = 0.5f;

        var tile = TileAtDistance(world, observer.Tile, 2);
        var hunter = SpawnMob(world, id: 3, tile);
        var passerby = SpawnMob(world, id: 4, tile);
        hunter.TargetNpc = observer.Id;
        passerby.TargetNpc = other.Id;

        perception.Run(world);

        var seen = observer.Perception.Mobs.ToDictionary(m => m.Id);
        Assert.Multiple(() =>
        {
            Assert.That(seen[3].TargetsMe, Is.True);
            Assert.That(seen[4].TargetsMe, Is.False,
                "иначе волк, идущий мимо к соседке, неотличим от волка на неё");
        });
    }

    [Test]
    public void OrderIsByIdAndNotBySpawnAccident()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var observer = Colonists(world)[0];
        observer.Attributes.Perception = 0.5f;

        var tile = TileAtDistance(world, observer.Tile, 2);
        // Порядок world.Mobs — это порядок спавна и смертей, а не свойство мира.
        SpawnMob(world, id: 9, tile);
        SpawnMob(world, id: 4, tile);
        SpawnMob(world, id: 7, tile);

        perception.Run(world);

        Assert.That(observer.Perception.Mobs.Select(m => m.Id), Is.EqualTo(new[] { 4, 7, 9 }));
    }

    [Test]
    public void ContextSummaryCarriesTheMobIdThatAttackMobPromises()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var observer = Colonists(world)[0];
        observer.Attributes.Perception = 0.5f;

        var mob = SpawnMob(world, id: 5, TileAtDistance(world, observer.Tile, 2));
        mob.TargetNpc = observer.Id;
        perception.Run(world);

        var context = LlmDecisionContextBuilder.Build(world, observer);
        var repeated = LlmDecisionContextBuilder.Build(world, observer);

        Assert.Multiple(() =>
        {
            Assert.That(context.PerceptionSummary, Does.Contain("mobs=[{mobId=5"),
                "ровно то поле, за которым инструмент атаки посылает агента");
            Assert.That(context.PerceptionSummary, Does.Contain("targetsMe=true"));
            Assert.That(context.PerceptionSummary, Is.EqualTo(repeated.PerceptionSummary),
                "сводка обязана быть детерминированной: два вызова подряд — один текст");
        });
    }

    private static MobState SpawnMob(WorldState world, int id, TileCoord tile)
    {
        var junction = world.Tiles.Items[tile].Junctions[0];
        var mob = new MobState
        {
            Id = id,
            MobId = HexLive.Simulation.Content.MobIds.Dog,
            Tile = tile,
            Junction = junction,
            Health = 10f,
        };
        world.Mobs.Add(mob);
        return mob;
    }

    private static NPCState[] Colonists(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .OrderBy(npc => npc.Id.Value)
            .ToArray();

    private static TileCoord TileAtDistance(WorldState world, TileCoord origin, int distance) =>
        world.Tiles.Items
            .Where(pair =>
                HexLive.Simulation.Spatial.HexSpatialMath.HexDistance(origin, pair.Key) == distance &&
                pair.Value.Junctions.Count > 0)
            .OrderBy(pair => pair.Key.Q)
            .ThenBy(pair => pair.Key.R)
            .Select(pair => pair.Key)
            .First();
}

}
