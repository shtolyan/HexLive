using System.Collections.Generic;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §138.7: «нажала Создать пять раз — она делает пять». Очередь — клиентский
/// планировщик над read-моделью крафта; здесь её автомат прогоняется на
/// поддельной модели: готово / прервано / отказано / нет ресурсов / нет ручного.
/// </summary>
public sealed class CraftQueueTests
{
    private const int Npc = 7;
    private const string Rope = "resource.rope";

    private sealed class FakeWorld
    {
        public readonly CraftRecipeOption Option = new() { OutputDefinitionId = Rope, CanCraft = true };
        public bool Manual = true;
        public readonly List<CraftByOutputCommand> Sent = new();
        public int Tick;

        public void Step(CraftQueue queue) => queue.Tick(++Tick, _ => Manual,
            (_, into) => { into.Add(Option); return true; }, Sent.Add);

        public void Finish()
        {
            Option.IsActive = false;
            Option.IsResume = false;
        }
    }

    [Test]
    public void EachPressMakesOneMoreAndTheQueueRunsUntilDone()
    {
        var queue = new CraftQueue();
        var world = new FakeWorld();
        queue.Add(Npc, Rope, world.Option);
        queue.Add(Npc, Rope, world.Option);
        Assert.That(queue.Add(Npc, Rope, world.Option), Is.EqualTo(3));

        world.Step(queue);
        Assert.That(world.Sent, Has.Count.EqualTo(1), "the first order goes out at once");
        world.Option.IsActive = true; world.Step(queue);
        world.Finish(); world.Step(queue);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(2));
        Assert.That(world.Sent, Has.Count.EqualTo(2), "the next order follows the finished item");

        world.Option.IsActive = true; world.Step(queue);
        world.Finish(); world.Step(queue);
        world.Option.IsActive = true; world.Step(queue);
        world.Finish(); world.Step(queue);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(0));
        Assert.That(world.Sent, Has.Count.EqualTo(3), "exactly three orders for three presses");
    }

    [Test]
    public void AnInterruptedProjectIsContinuedNotCounted()
    {
        var queue = new CraftQueue();
        var world = new FakeWorld();
        queue.Add(Npc, Rope, world.Option);
        queue.Add(Npc, Rope, world.Option);
        world.Step(queue);
        world.Option.IsActive = true; world.Step(queue);
        world.Option.IsActive = false; world.Option.IsResume = true; world.Step(queue);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(2), "a half-made project is not an item");
        Assert.That(world.Sent, Has.Count.EqualTo(2), "…and is ordered to continue");
    }

    [Test]
    public void PressingWhileSheAlreadyCraftsCountsTheCurrentItem()
    {
        var queue = new CraftQueue();
        var world = new FakeWorld();
        world.Option.IsActive = true;
        Assert.That(queue.Add(Npc, Rope, world.Option), Is.EqualTo(2));
        world.Step(queue);
        Assert.That(world.Sent, Is.Empty, "nothing new while the current item is in the making");
    }

    [Test]
    public void MissingResourcesStopTheQueueWithItsReason()
    {
        var queue = new CraftQueue();
        var world = new FakeWorld();
        queue.Add(Npc, Rope, world.Option);
        queue.Add(Npc, Rope, world.Option);
        world.Step(queue);
        world.Option.IsActive = true; world.Step(queue);
        world.Finish();
        world.Option.CanCraft = false;
        world.Option.BlockReason = CraftBlockReason.MissingResources;
        world.Step(queue);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(0));
        Assert.That(queue.TryGetStop(Npc, out var output, out var reason), Is.True);
        Assert.That((output, reason), Is.EqualTo((Rope, CraftBlockReason.MissingResources)));
    }

    [Test]
    public void ARefusedOrderStopsAfterTheTimeout()
    {
        var queue = new CraftQueue();
        var world = new FakeWorld();
        queue.Add(Npc, Rope, world.Option);
        world.Step(queue);
        world.Option.CanCraft = false;
        world.Option.BlockReason = CraftBlockReason.Unreachable;
        for (var i = 0; i < CraftQueue.StartTimeoutTicks; i++) world.Step(queue);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(0));
        Assert.That(queue.TryGetStop(Npc, out _, out var reason) && reason == CraftBlockReason.Unreachable, Is.True);
    }

    [Test]
    public void LosingManualControlClearsTheQueue()
    {
        var queue = new CraftQueue();
        var world = new FakeWorld();
        queue.Add(Npc, Rope, world.Option);
        queue.Add(Npc, Rope, world.Option);
        world.Manual = false;
        world.Step(queue);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(0));
        Assert.That(world.Sent, Is.Empty);
    }

    [Test]
    public void CancelKeepsOnlyTheItemInTheMaking()
    {
        var queue = new CraftQueue();
        var world = new FakeWorld();
        for (var i = 0; i < 4; i++) queue.Add(Npc, Rope, world.Option);
        world.Step(queue);
        world.Option.IsActive = true; world.Step(queue);
        queue.Cancel(Npc, Rope);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(1));
        world.Finish(); world.Step(queue);
        Assert.That(queue.Remaining(Npc, Rope), Is.EqualTo(0));
        Assert.That(world.Sent, Has.Count.EqualTo(1));
    }
}
}
