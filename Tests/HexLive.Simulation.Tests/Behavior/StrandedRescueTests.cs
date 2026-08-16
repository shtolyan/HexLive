using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §118.4 r2 (баг #166). Застрявшую — в сознании, ноги не держат, дороги домой
/// нет — доносят; своих носят на руках всегда, спят они или нет.
/// </summary>
public sealed class StrandedRescueTests
{
    private static void BreakLegs(NPCState npc)
    {
        npc.Body.Parts[BodyPart.LegL] = 0.05f;
        npc.Body.Parts[BodyPart.LegR] = 0.05f;
    }

    // В свежесозданном мире узел NPC ещё не назначен (его ставит первый тик), а
    // без узла предикат отвечает «не застряла» по построению — то есть тест без
    // этой подготовки проверял бы ровно ничего.
    private static void Stand(WorldState world, NPCState npc)
    {
        npc.CurrentJunction = world.Tiles.Items[npc.Tile].Junctions
            .First(id => !world.Junctions.Items[id].Blocked);
    }

    [Test]
    public void AHealthyColonistIsNotARescueRequest()
    {
        var world = TestWorld.CreateWorld(5510);
        var npc = world.Entities.Npcs.Values.First();
        Stand(world, npc);

        Assert.That(KenshiRescueMath.IsStranded(world, npc), Is.False);
    }

    [Test]
    public void ACrawlerWhoCanStillReachHomeIsNotStranded()
    {
        var world = TestWorld.CreateWorld(5510);
        var npc = world.Entities.Npcs.Values.First();
        Stand(world, npc);
        BreakLegs(npc);

        Assert.That(npc.Body.IsCrawling, Is.True, "фикстура обязана ползти");
        Assert.That(KenshiRescueMath.IsStranded(world, npc), Is.False,
            "доползёт сама — это §50.9, а не спасение");
    }

    [Test]
    public void ACrawlerCutOffFromHomeIsARescueRequest()
    {
        var world = TestWorld.CreateWorld(5510);
        var npc = world.Entities.Npcs.Values.First();
        Stand(world, npc);
        BreakLegs(npc);

        // Отрезаем дом: с её проходимостью (без прыжка) дороги туда не остаётся.
        var home = ColonyQueries.Home(world, npc.Faction);
        Assert.That(home, Is.Not.Null);
        foreach (var junction in world.Tiles.Items[home!.Value].Junctions)
        {
            world.Junctions.Items[junction].Blocked = true;
        }

        world.TopologyVersion++;

        Assert.That(KenshiRescueMath.IsStranded(world, npc), Is.True);
        Assert.That(KenshiRescueMath.NeedsRescue(world, npc), Is.True,
            "застрявшая обязана попадать в очередь спасения");
    }

    [Test]
    public void AnUnconsciousCrawlerGoesThroughTheOldDoorNotThisOne()
    {
        var world = TestWorld.CreateWorld(5510);
        var npc = world.Entities.Npcs.Values.First();
        Stand(world, npc);
        BreakLegs(npc);
        npc.Mind.FaintedUntilTick = world.Tick + 100;

        Assert.That(KenshiRescueMath.IsStranded(world, npc), Is.False,
            "без сознания — это кома/умирание, у них своя ветка");
    }

    [Test]
    public void OwnColonistIsCarriableAwakeAndOnHerFeet()
    {
        var world = TestWorld.CreateWorld(5510);
        var people = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .OrderBy(npc => npc.Id.Value)
            .ToArray();
        var carrier = people[0];
        var mate = people[1];

        Assert.That(mate.IsLyingDown(world.Tick), Is.False, "фикстура: она на ногах");
        Assert.That(ManualCarryTargets.CanCarry(world, carrier, mate, dead: false), Is.True);
    }

    [Test]
    public void AStrangerOnHisFeetIsNotCarriable()
    {
        var world = TestWorld.CreateWorld(5510);
        var carrier = world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);
        var stranger = world.Entities.Npcs.Values.FirstOrDefault(npc =>
            !FactionRelations.AreAllies(npc, carrier));
        if (stranger is null)
        {
            Assert.Ignore("в этом сиде чужака нет");
        }

        Assert.That(ManualCarryTargets.CanCarry(world, carrier, stranger!, dead: false),
            Is.EqualTo(stranger!.IsLyingDown(world.Tick)),
            "чужого носят только лежачим");
    }
}

}
