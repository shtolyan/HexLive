using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§30.17: причина смерти — состояние, а не находка в кольце трасс.
/// Кольцо держит 2048 записей: с многословной трассой это ~11 тиков, без неё —
/// сотни, поэтому пока причина выкапывалась оттуда, один и тот же сид давал
/// разную DeathRecord.Cause в редакторе, в билде и в headless-прогоне — а она
/// уходит в сейв и по проводу.</summary>
[NonParallelizable]
public sealed class DeathCauseTests
{
    [Test]
    public void CauseSurvivesARingFullOfChatter()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

        world.Tick = 1000;
        Trace.Emit(world, npc.Id, "SharkBite", $"NPC{npc.Id.Value} bitten by shark 7");

        // Кольцо (2048) переполняется болтовнёй — прежний поиск по нему уже
        // ничего бы не нашёл и молча свалился бы в вывод причины по нуждам.
        for (var i = 0; i < 2200; i++)
        {
            Trace.Emit(world, npc.Id, "PerceivedObject", $"Obj={i} noise");
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.DeathCauseText, Does.StartWith("SharkBite:"));
            Assert.That(npc.Mind.DeathCauseTick, Is.EqualTo(1000));
            Assert.That(world.Events.Items.Any(e => e.Type == "SharkBite"), Is.False,
                "предпосылка теста: событие уже вытеснено из кольца");
        });
    }

    [Test]
    public void ChatterDoesNotStampACause()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

        world.Tick = 500;
        Trace.Emit(world, npc.Id, "PerceptionUpdated", "Objects=3");
        Trace.Emit(world, npc.Id, "GoalScored", "Eat: Final=0.5");

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.DeathCauseText, Is.Empty);
            Assert.That(npc.Mind.DeathCauseTick, Is.EqualTo(int.MinValue));
        });
    }

    [Test]
    public void AStaleCauseIsNotCreditedToThisDeath()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

        world.Tick = 100;
        Trace.Emit(world, npc.Id, "Sunburn", "burned");

        // Через игровой час это уже не про ту смерть — окно 240 тиков.
        world.Tick = 100 + 241;
        Assert.That(world.Tick - npc.Mind.DeathCauseTick, Is.GreaterThan(240),
            "штамп обязан устареть — читатель обязан свалиться на вывод по состоянию");
    }
}

}
