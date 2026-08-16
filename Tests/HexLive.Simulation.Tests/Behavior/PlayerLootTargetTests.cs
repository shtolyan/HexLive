using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §128 r2 (баг #164). Игрок вправе обыскать ЛЮБОГО лежащего: мёртвую, спящую,
/// без сознания. Раньше проходила только живая в отключке, и над телом или
/// спящей в меню оставалось одно «взять на руки».
/// </summary>
public sealed class PlayerLootTargetTests
{
    private static (Core.WorldState world, NPCState looter, NPCState other) Pair(int seed = 3311)
    {
        var world = TestWorld.CreateWorld(seed);
        var people = world.Entities.Npcs.Values.OrderBy(npc => npc.Id.Value).ToArray();
        return (world, people[0], people[1]);
    }

    [Test]
    public void AwakeAndWellPersonIsNotALootTarget()
    {
        var (world, looter, other) = Pair();

        Assert.That(PlayerLootTargets.TryResolve(world, looter, other.Id, out _, out _),
            Is.False, "здоровую бодрствующую обыскивать нельзя");
    }

    [Test]
    public void SleeperIsALootTarget()
    {
        var (world, looter, other) = Pair();
        other.Execution.CurrentInteraction = InteractionType.Sleep;

        Assert.That(PlayerLootTargets.TryResolve(world, looter, other.Id, out var resolved, out _),
            Is.True);
        Assert.That(resolved.Id, Is.EqualTo(other.Id));
    }

    [Test]
    public void UnconsciousPersonIsALootTarget()
    {
        var (world, looter, other) = Pair();
        other.Mind.FaintedUntilTick = world.Tick + 100;

        Assert.That(PlayerLootTargets.TryResolve(world, looter, other.Id, out _, out _), Is.True);
    }

    [Test]
    public void CorpseIsALootTarget_EvenThoughItLivesInAnotherRegistry()
    {
        var (world, looter, other) = Pair();
        world.Entities.Npcs.Remove(other.Id);
        other.Health = 0f;
        world.Entities.Corpses[other.Id] = other;

        Assert.That(PlayerLootTargets.TryResolve(world, looter, other.Id, out var resolved, out _),
            Is.True, "тело лежит в отдельном реестре, и это не повод его не обыскать");
        Assert.That(resolved.Id, Is.EqualTo(other.Id));
    }

    [Test]
    public void SelfIsNeverALootTarget()
    {
        var (world, looter, _) = Pair();
        looter.Execution.CurrentInteraction = InteractionType.Sleep;

        Assert.That(PlayerLootTargets.TryResolve(world, looter, looter.Id, out _, out _), Is.False);
    }

    [Test]
    public void SomeoneElsesArmsKeepTheirBurden()
    {
        var (world, looter, other) = Pair();
        var third = world.Entities.Npcs.Values.First(npc =>
            npc.Id != looter.Id && npc.Id != other.Id);
        other.Mind.FaintedUntilTick = world.Tick + 100;
        other.CarriedByNpcId = third.Id;

        Assert.That(PlayerLootTargets.TryResolve(world, looter, other.Id, out _, out _), Is.False);

        other.CarriedByNpcId = looter.Id;
        Assert.That(PlayerLootTargets.TryResolve(world, looter, other.Id, out _, out var carriedBySelf),
            Is.True, "«взял — обыскал»: на своих руках можно");
        Assert.That(carriedBySelf, Is.True);
    }
}

}
