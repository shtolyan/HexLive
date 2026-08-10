using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §125.6: свидетели смерти находятся ЧЕРЕЗ ВОСПРИЯТИЕ — и это работает только
/// потому, что список восприятия является СНИМКОМ, снятым в начале medium-тика,
/// когда покойница была ещё жива.
/// <para>
/// ⭐ Ловушка, ради которой тест существует: к моменту разбора смерти
/// (<c>MobSystem.RemoveDeadNpc</c>) тело уже удалено из <c>Entities.Npcs</c> и
/// переехало в <c>Corpses</c>. Если кто-нибудь научит <c>PerceptionSystem</c>
/// отбрасывать мёртвых или переставит его после <c>MobSystem</c>, горе и
/// облегчение врагов исчезнут МОЛЧА — ни одно исключение не сработает, просто
/// колония перестанет замечать смерти.
/// </para>
/// </summary>
[NonParallelizable]
public sealed class DeathWitnessTests
{
    [Test]
    public void PerceptionListOutlivesTheBodyForThisTick()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var colonists = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .ToArray();
        Assert.That(colonists.Length, Is.GreaterThanOrEqualTo(2));

        var witness = colonists[0];
        var doomed = colonists[1];
        witness.Attributes.Perception = 0.5f; // радиус 5

        Place(world, doomed, new TileCoord(witness.Tile.Q + 1, witness.Tile.R));
        perception.Run(world);
        Assert.That(PerceptionMath.Sees(witness, doomed.Id), Is.True,
            "предпосылка: соседку видно, пока она жива");

        // Смерть и уборка тела: из живого реестра она пропадает…
        world.Entities.Npcs.Remove(doomed.Id);

        // …но снимок восприятия этого тика её ещё держит — на нём и стоит
        // разбор смерти в MobSystem.
        Assert.That(PerceptionMath.Sees(witness, doomed.Id), Is.True,
            "свидетель обязан 'видеть' покойницу в снимке этого тика — иначе " +
            "горе и облегчение врагов исчезнут молча");
    }

    [Test]
    public void PerceptionDoesNotDropTheDeadWhileTheyAreStillInTheRoster()
    {
        // Второй край той же ловушки: пока тело лежит в Npcs (Health <= 0, но
        // medium-свип MobSystem ещё не прошёл), восприятие обязано его видеть.
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var colonists = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .ToArray();

        var witness = colonists[0];
        var dead = colonists[1];
        witness.Attributes.Perception = 0.5f;

        Place(world, dead, new TileCoord(witness.Tile.Q + 1, witness.Tile.R));
        dead.Health = 0f;
        perception.Run(world);

        Assert.That(PerceptionMath.Sees(witness, dead.Id), Is.True);
    }

    private static void Place(WorldState world, NPCState npc, TileCoord tile)
    {
        SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, tile);
        npc.Tile = tile;
        npc.Position = HexSpatialMath.TileToWorld(tile);
    }
}

}
