using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §54.2 r2 (баг #168). «Островитянки рубят все деревья.» Оказалось, рубит их
/// не заготовка дров, а ПОХОД ЗА ВОДОЙ: ветка добычи «подойти и потрясти
/// пальму» шага Interact не ставит вовсе, а исполнитель в этом случае брал
/// первый глагол объекта — у пальмы это «свалить её». Замер (соак 120 000
/// тиков, сид 777): роща 17 → 0, и каждая пальма с 41 267-го тика падала под
/// целью GetWater.
/// </summary>
public sealed class PalmGroveTests
{
    private static WorldObjectState AnyPalm(WorldState world) =>
        world.Entities.Objects.Values.First(obj =>
            world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
            def.Tags.Contains("Palm"));

    [Test]
    public void APlanWithNoVerbNeverFallsBackToFellingTheTree()
    {
        var world = TestWorld.CreateWorld(9091);
        var npc = world.Entities.Npcs.Values.First();
        var palm = world.Content.ObjectDefinitions[AnyPalm(world).DefinitionId];

        Assert.That(palm.Interactions.Select(i => i.Type), Is.All.EqualTo(InteractionType.Harvest),
            "фикстура опирается на то, что у пальмы один глагол — свалить её");

        // Ровно то, что приходит из ForagePlan: шага Interact нет, глагола нет.
        Assert.That(ExecutionSystem.ResolveInteraction(world, npc, palm, null), Is.Null,
            "«план не назвал глагол» не значит «руби»");
    }

    [Test]
    public void AVerblessPlanStillGetsAnHarmlessFallback()
    {
        var world = TestWorld.CreateWorld(9091);
        var npc = world.Entities.Npcs.Values.First();
        var coconut = world.Content.ObjectDefinitions[ContentIds.Coconut];

        var resolved = ExecutionSystem.ResolveInteraction(world, npc, coconut, null);

        Assert.That(resolved, Is.Not.Null, "безобидный глагол фолбэк по-прежнему находит");
        Assert.That(resolved!.Type, Is.Not.EqualTo(InteractionType.Harvest));
    }

    [Test]
    public void TheGroveReserveIsCountedOverTheWholeIsland()
    {
        var world = TestWorld.CreateWorld(9091);
        var palms = world.Entities.Objects.Values.Count(obj =>
            world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
            def.Tags.Contains("Palm"));

        Assert.That(ColonyQueries.WorldCountWithTag(world, "Palm"), Is.EqualTo(palms));
        Assert.That(palms, Is.GreaterThan(SimBalance.PalmGroveReserve),
            "фикстура обязана начинаться с рощи больше резерва");
    }

    [Test]
    public void TheCensusFollowsTheWorldFromOneTickToTheNext()
    {
        var world = TestWorld.CreateWorld(9091);
        var before = ColonyQueries.WorldCountWithTag(world, "Palm");
        var palm = AnyPalm(world);

        WorldObjectMutations.DespawnObject(world, palm.Id);
        Assert.That(ColonyQueries.WorldCountWithTag(world, "Palm"), Is.EqualTo(before),
            "внутри одного тика перепись не пересчитывается — это и есть кэш");

        world.Tick++;
        Assert.That(ColonyQueries.WorldCountWithTag(world, "Palm"), Is.EqualTo(before - 1));
    }
}

}
