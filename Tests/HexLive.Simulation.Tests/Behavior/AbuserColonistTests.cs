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

/// <summary>
/// §126.3: абьюз — это ХАРАКТЕР актёра и ВРАЖДА жертвы, две независимые вещи.
///
/// <para>
/// До §126 обе роли играл один бит <c>Faction</c>, и разделить их было нечем.
/// Здесь стерегут ровно ту границу, ради которой их развели: колонистка с
/// чертой докапывается до чужаков — и не трогает своих НИКОГДА, чем бы её ни
/// давило. Некого гнобить — цель просто недоступна, и она живёт обычной жизнью
/// вместо того, чтобы искать жертву среди подруг.
/// </para>
/// </summary>
public sealed class AbuserColonistTests
{
    // §125.6: выбор жертвы спрашивает ГЛАЗА (список восприятия), а не ростер.
    // Телепортировать соседа вплотную мало — список наполняет сенсор на своём
    // тике, поэтому тест ставит в него запись сам, вместо того чтобы гонять
    // весь движок и надеяться, что за пару тиков они не разошлись.
    // Оба уходят на НЕЙТРАЛЬНЫЙ тайл: и дом колонии, и стоянка чужака помечены
    // Indoor (§29C.4A — там собаки бросают погоню), а §81 уважает святилище и
    // отказал бы от жертвы по совершенно постороннему поводу.
    private static void MoveToNeutralGround(WorldState world, params NPCState[] crowd)
    {
        var spot = world.Tiles.Items.Values.First(t =>
            t.Flags.HasFlag(TileFlags.Walkable) &&
            !t.Flags.HasFlag(TileFlags.Indoor) &&
            t.Junctions.Count > 0);

        foreach (var npc in crowd)
        {
            npc.Tile = spot.Coord;
            npc.CurrentJunction = spot.Junctions.First();
            npc.Position = HexSpatialMath.TileToWorld(spot.Coord);
        }
    }

    private static void PutInSight(NPCState observer, NPCState target)
    {
        var list = FactionRelations.AreHostile(observer, target)
            ? observer.Perception.Hostiles
            : observer.Perception.Agents;
        list.Add(new PerceivedAgent
        {
            Id = target.Id,
            Faction = target.Faction,
            Tile = target.Tile
        });
    }

    [Test]
    public void ColonistWithTheTrait_NeverPicksAHousemate()
    {
        var world = TestWorld.CreateWorld();
        var bully = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        bully.Traits.Add(TraitKind.Abuser);

        // Чужак рядом и на виду — иначе проверка выродилась бы в «жертв нет».
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        // И подруга тоже на виду: без неё «не выбрала свою» доказывало бы
        // только то, что своих она не видела.
        var housemate = world.Entities.Npcs.Values.First(
            n => n.Faction == Faction.Colony && !n.Id.Equals(bully.Id));

        MoveToNeutralGround(world, bully, stranger, housemate);
        PutInSight(bully, stranger);
        PutInSight(bully, housemate);

        var mark = AbuseMath.BestMark(world, bully, out _);

        Assert.That(mark, Is.Not.Null,
            "Чужак стоит вплотную, а жертвы нет — значит выбор жертвы поехал " +
            "вместе с переносом гейта актёра на черту.");
        Assert.That(mark.Faction, Is.Not.EqualTo(Faction.Colony),
            $"Абьюзерша выбрала СВОЮ (NPC{mark.Id.Value}). Жертву обязана " +
            "решать вражда (§126.3), иначе черта превращается в гражданскую войну.");
    }

    [Test]
    public void NoEnemyAround_MeansNoMarkAtAll()
    {
        var world = TestWorld.CreateWorld();
        var bully = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        bully.Traits.Add(TraitKind.Abuser);

        foreach (var stranger in world.Entities.Npcs.Values
                     .Where(n => n.Faction != Faction.Colony).ToList())
        {
            world.Entities.Npcs.Remove(stranger.Id);
        }

        Assert.That(AbuseMath.BestMark(world, bully, out _), Is.Null,
            "Чужих на острове нет, а жертва нашлась — «некого гнобить» обязано " +
            "означать недоступную цель, а не поиск среди своих.");
    }

    [Test]
    public void ColonistWithoutTheTrait_IsNotAnAbuserEvenNextToAnEnemy()
    {
        var world = TestWorld.CreateWorld();
        var girl = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        Assert.That(girl.Traits.Has(TraitKind.Abuser), Is.False,
            "Рарности §126 шипятся нулями — колонистка не может выкатиться " +
            "абьюзершей, пока фаза включения не прошла соаки.");

        // Симметрия проверки: сам ВЫБОР жертвы черты не спрашивает — она
        // решается враждой. Гейт актёра стоит в аукционе и латче, и именно
        // поэтому обычная девушка сцену не начнёт.
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        MoveToNeutralGround(world, girl, stranger);
        PutInSight(girl, stranger);

        Assert.That(AbuseMath.BestMark(world, girl, out _), Is.Not.Null,
            "Если бы выбор жертвы сам спрашивал черту, гейт актёра стал бы " +
            "дублем — а дубль однажды разъедется с оригиналом.");
    }

    [Test]
    public void TheStrangerStillCarriesBothTraits()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);

        Assert.That(stranger.Traits.Has(TraitKind.Abuser), Is.True,
            "Чужак растерял характер при переносе §81 с фракции на черту — " +
            "§81 замолчал бы целиком, и ни один гейт бы этого не заметил.");
        Assert.That(stranger.Traits.Has(TraitKind.Slob), Is.True,
            "Чужак растерял неряшливость — пойдёт полоскать рубаху (§89).");
    }
}

}
