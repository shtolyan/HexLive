using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Bootstrap;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

/// <summary>
/// §83: кадры зрителю уходят ПО тику, а не по 62.5-мс опросу — старый poll
/// добавлял каждому кадру до четверти тика случайной задержки отправки, и
/// клиентские часы были вынуждены буферизовать этот самодельный джиттер.
/// Контракт <see cref="WorldHost.WaitForNextTickAsync"/>: завершиться сразу,
/// если тик уже другой; проснуться от шага мира; не позже maxWait — чтобы
/// зрители стоящего на паузе мира по-прежнему замечали смену часов.
/// </summary>
[NonParallelizable]
public sealed class WorldHostTickSignalTests
{
    [Test]
    public async Task WaitForNextTick_ReturnsPromptlyWhenTickAlreadyMoved()
    {
        using var host = CreateHost();
        var sw = Stopwatch.StartNew();
        await host.WaitForNextTickAsync(
            host.Tick - 1, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(1),
            "тик уже отличается — ждать нечего");
    }

    [Test]
    public async Task WaitForNextTick_FallsBackToMaxWaitOnAQuietWorld()
    {
        using var host = CreateHost();
        host.PauseAsOperator();
        var sw = Stopwatch.StartNew();
        await host.WaitForNextTickAsync(
            host.Tick, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(sw.Elapsed.TotalMilliseconds, Is.GreaterThanOrEqualTo(150),
            "мир на паузе — вернуться можно только по таймауту");
        Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(5),
            "таймаут — это maxWait, а не вечность");
    }

    [Test]
    public async Task WaitForNextTick_WakesOnAStep()
    {
        using var host = CreateHost();
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => host.Run(cts.Token));
        try
        {
            var before = host.Tick;
            var sw = Stopwatch.StartNew();
            await host.WaitForNextTickAsync(
                before, TimeSpan.FromSeconds(30), cts.Token);
            // Тик при 4 Гц шагает каждые 250 мс; пробуждение по сигналу обязано
            // уложиться с запасом до 30-секундного фолбэка.
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(10),
                "сигнал тика не разбудил ожидающего зрителя");
            Assert.That(host.Tick, Is.Not.EqualTo(before));
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    [Test]
    public void WaitForNextTick_ThrowsOnCancellation_LikeTheOldPollDid()
    {
        using var host = CreateHost();
        host.PauseAsOperator();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        // ViewerConnection выходит из цикла отправки по OperationCanceledException —
        // замена опроса на сигнал обязана сохранить этот контракт.
        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () =>
            host.WaitForNextTickAsync(host.Tick, TimeSpan.FromSeconds(30), cts.Token));
    }

    private static WorldHost CreateHost()
    {
        var savePath = Path.Combine(
            Path.GetTempPath(), $"hexlive-worldhost-ticksignal-{Guid.NewGuid():N}.sav");
        return new WorldHost(
            seed: 12345,
            mode: GameMode.Feud,
            savePath,
            FindRepoFile("SimData", "simdata.json"),
            verboseTrace: false);
    }

    private static string FindRepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, Path.Combine(relativePath));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Не найден " + Path.Combine(relativePath) + " вверх от " +
            TestContext.CurrentContext.TestDirectory);
    }
}

}
