using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §169. Умершая в конце окна умирания (§105) УЖЕ ЛЕЖАЛА — смерть не должна
/// доигрывать падение из положения стоя. Игрок: «умер с голоду и зачем-то встал
/// и лёг».
/// </summary>
public sealed class LyingDeathPoseTests
{
    private static NPCState Victim(Core.WorldState world)
    {
        var npc = world.Entities.Npcs.Values.First();
        npc.Needs.Hunger = 1f;
        npc.Needs.Blood = 1f;
        return npc;
    }

    private static void DrainDyingWindow(Core.WorldState world, NPCState npc)
    {
        for (var i = 0; i < 4000 && npc.Health > 0f && npc.IsDying; i++)
        {
            world.Tick++;
            npc.Mind.DyingReserve = 0f;
            MortalityHelpers.TickDying(world, npc);
        }
    }

    [Test]
    public void DeathAtTheEndOfTheDyingWindowKeepsTheLyingPose()
    {
        var world = TestWorld.CreateWorld(6021);
        var npc = Victim(world);

        MortalityHelpers.EnterDying(world, npc, DyingCause.Starvation);
        Assert.That(npc.IsDying, Is.True, "фикстура: она в окне умирания");

        // Окно тикает по своим часам: первый шаг только перештамповывает, а
        // запас вычитается со следующего. Крутим до смерти с потолком, чтобы
        // тест падал по существу, а не висел.
        DrainDyingWindow(world, npc);

        Assert.That(npc.Health, Is.EqualTo(0f), "окно обязано было закончиться смертью");
        Assert.That(npc.DeathAnimVariant, Is.LessThan(0),
            "лежала при смерти — значит и остаётся лежать, без клипа падения");
    }

    [Test]
    public void TheCorpseSweepKeepsAnAlreadyStampedLyingPose()
    {
        var world = TestWorld.CreateWorld(6021);
        var npc = Victim(world);
        var id = npc.Id;

        MortalityHelpers.EnterDying(world, npc, DyingCause.Starvation);
        DrainDyingWindow(world, npc);
        Assert.That(npc.DeathAnimVariant, Is.LessThan(0));

        // Свип подбирает тело СЛЕДУЮЩИМ проходом, когда IsDying уже погас.
        Assert.That(npc.IsDying, Is.False, "фикстура: признак умирания снят");
        Assert.That(npc.IsLyingDown(world.Tick), Is.False,
            "и именно поэтому свип не может узнать правду сам");

        new MobSystem().Run(world);

        Assert.That(world.Entities.Corpses.TryGetValue(id, out var body), Is.True,
            "тело обязано попасть в реестр трупов");
        Assert.That(body.DeathAnimVariant, Is.LessThan(0),
            "свип не должен затирать чужую метку «умерла уже лёжа»");
    }
}

}
