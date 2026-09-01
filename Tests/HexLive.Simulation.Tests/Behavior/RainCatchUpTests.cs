using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §156.5: расписание дождя как формула, и водосбор, который умеет досчитать
/// проспанное. Формула обязана совпадать с тем, что мир показывал вживую, —
/// иначе бутылка наполняется дождём, которого никто не видел.
/// </summary>
public sealed class RainCatchUpTests
{
    /// <summary>
    /// ⭐ Главный тест связи: прогоняем настоящий движок и на каждом slow-такте
    /// сверяем Environment.IsRaining с RainMath. Второй набор солей или
    /// разъехавшийся литерал упадут именно здесь, а не через полгода в виде
    /// «бутылка полная, а дождя не было».
    /// </summary>
    [Test]
    public void FormulaAgreesWithTheWeatherSystemItReplaces()
    {
        var engine = TestWorld.CreateEngine(seed: 12345);
        var world = engine.World;
        var checkedTicks = 0;

        for (var i = 0; i < 6000; i++)
        {
            engine.Step();

            // Tick инкрементируется В КОНЦЕ Step, поэтому только что отработал
            // такт world.Tick - 1. Слой Slow трогал мир, если кратен ему именно
            // он, и env.IsRaining относится к нему же.
            var ran = world.Tick - 1;
            if (ran < 0 || ran % world.SlowIntervalTicks != 0)
            {
                continue;
            }

            checkedTicks++;
            Assert.That(RainMath.RainingAt(world.Seed, ran),
                Is.EqualTo(world.Environment.IsRaining),
                $"расписание разошлось с миром на тике {ran}");
        }

        Assert.That(checkedTicks, Is.GreaterThan(100),
            "сверить удалось подозрительно мало тактов — сломан тест, а не формула");
    }

    /// <summary>
    /// Счёт по окну равен честному перебору. Проверяются именно ловушки §156.5:
    /// окно внутри одного фронта, окно через границу цикла (хвост фронта
    /// обрезается), окно не на slow-сетке, окно длиной в много циклов.
    /// </summary>
    [Test]
    public void WindowCountMatchesBruteForce()
    {
        const int slow = 16;
        var seeds = new[] { 12345, 424242, 7, 350 };
        var windows = new[]
        {
            (0, 100), (0, 2400), (2390, 2410), (1, 15), (16, 16),
            (100, 5000), (0, 24000), (7999, 8001), (2399, 2401), (-16, 0),
        };

        foreach (var seed in seeds)
        {
            foreach (var (from, to) in windows)
            {
                var expected = 0L;
                for (var t = System.Math.Max(from + 1, 0); t <= to; t++)
                {
                    if (t % slow == 0 && RainMath.RainingAt(seed, t))
                    {
                        expected++;
                    }
                }

                Assert.That(RainMath.RainSlowTicksInWindow(seed, from, to, slow),
                    Is.EqualTo(expected),
                    $"сид {seed}, окно ({from}, {to}]");
            }
        }
    }

    /// <summary>Дождя до сотворения мира не бывает, каким бы длинным ни было окно.</summary>
    [Test]
    public void NothingRainedBeforeTickZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RainMath.RainingAt(12345, -1), Is.False);
            Assert.That(RainMath.RainSlowTicksInWindow(12345, -10000, -1, 16), Is.Zero);
            Assert.That(RainMath.RainSlowTicksInWindow(12345, 100, 50, 16), Is.Zero);
        });
    }

    /// <summary>
    /// ⭐ Арена пробуждения: бутылка в проспавшем чанке обязана оказаться ровно
    /// там, где была бы после честного прогона. Это и есть §156.2 — «стоимость
    /// пробуждения не зависит от длительности сна», проверенная результатом.
    /// </summary>
    [Test]
    public void SleptCollectorCatchesUpToTheHonestlySimulatedOne()
    {
        var previous = ChunkBalance.ChunkSleepEnabled;
        ChunkBalance.ChunkSleepEnabled = true;
        try
        {
            // Десять погодных циклов: шанс, что за них не выпало ни одного
            // фронта, ничтожен, а тест без дождя ничего не мерил бы.
            const int wakeTick = 24000;
            var honest = Fill(sleepUntil: 0, wakeTick: wakeTick);
            var slept = Fill(sleepUntil: wakeTick, wakeTick: wakeTick);

            Assert.That(honest, Is.GreaterThan(0f),
                "за десять циклов дождь обязан был идти — иначе тест ничего не меряет");
            Assert.That(slept, Is.EqualTo(honest).Within(1e-4f),
                "проспавший водосбор набрал не столько, сколько честно отработавший");
        }
        finally
        {
            ChunkBalance.ChunkSleepEnabled = previous;
        }
    }

    /// <summary>
    /// Ставит водосбор с бутылкой и крутит слой до <paramref name="wakeTick"/>.
    /// Пока тик меньше <paramref name="sleepUntil"/>, чанк объекта держится
    /// спящим — система обязана его пропускать и догнать одним махом.
    /// </summary>
    private static float Fill(int sleepUntil, int wakeTick)
    {
        var world = TestWorld.CreateWorld(12345);
        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0);
        var tile = anchor.Tiles[0];
        var junction = anchor.Id;

        var collector = WorldObjectMutations.SpawnObject(
            world, WaterCollectorMath.CollectorId, new FragmentId(1), tile, junction);
        var bottle = WorldObjectMutations.SpawnObject(
            world, WaterCollectorMath.VesselId, new FragmentId(1), tile, junction);
        bottle.ResourceAmount = 0f;

        var chunkOfCollector = ChunkMath.ChunkOf(collector.Tile);
        var system = new WaterCollectorSystem();
        var weather = new WeatherSystem();

        for (world.Tick = 0; world.Tick <= wakeTick; world.Tick += world.SlowIntervalTicks)
        {
            // Активный набор ставим руками: движок будит по живым NPC, а нам
            // нужен ровно один спящий чанк без оглядки на то, где стоят девушки.
            world.Caches.ActiveChunks.Clear();
            var awake = world.Tick >= sleepUntil;
            if (awake)
            {
                world.Caches.ActiveChunks.Add(chunkOfCollector);
            }

            weather.Run(world);
            system.Run(world);

            if (awake)
            {
                ChunkMath.StampSimulated(world);
            }
        }

        return bottle.ResourceAmount;
    }
}

}
