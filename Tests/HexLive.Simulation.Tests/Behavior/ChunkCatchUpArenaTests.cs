using System;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §156 фаза 3: арены пробуждения. Каждая мерит одно — проспавший объект обязан
/// оказаться там же, где оказался бы честно отработавший. Это и есть проверка
/// того, что догон считается формулой, а не «примерно».
/// </summary>
public sealed class ChunkCatchUpArenaTests
{
    /// <summary>
    /// ⭐ Костёр — БИТ В БИТ. Ставки горения дидические
    /// (16 × {1|4} × {1|0.5} × {1|0.5}), поэтому k-кратное списание точно равно
    /// k последовательным, и допуск здесь был бы поблажкой, прячущей ошибку
    /// (§156.5). Окно нарочно длиннее погодного цикла: дождь внутри сна меняет
    /// ставку вчетверо, и отрезки обязаны лечь на те же границы, что видел бы
    /// живой мир.
    /// </summary>
    [Test]
    public void SleptCampfireBurnsExactlyAsMuchFuelAsTheHonestOne()
    {
        WithChunkSleep(() =>
        {
            const int wakeTick = 7200;
            var honest = BurnFuel(sleepUntil: 0, wakeTick: wakeTick, fuel: 200_000f);
            var slept = BurnFuel(sleepUntil: wakeTick, wakeTick: wakeTick, fuel: 200_000f);

            Assert.That(slept, Is.EqualTo(honest),
                "проспавший костёр сжёг не столько дров, сколько честно горевший");
            Assert.That(honest, Is.LessThan(200_000f), "костёр обязан был гореть");
        });
    }

    /// <summary>Костёр, прогоревший во сне, обязан оказаться потухшим — и молча.</summary>
    [Test]
    public void CampfireThatRanOutMidSleepIsFoundDeadAndSilent()
    {
        WithChunkSleep(() =>
        {
            const int wakeTick = 4800;
            var honest = BurnFuel(sleepUntil: 0, wakeTick: wakeTick, fuel: 800f);
            var slept = BurnFuel(sleepUntil: wakeTick, wakeTick: wakeTick, fuel: 800f);

            Assert.Multiple(() =>
            {
                Assert.That(honest, Is.Zero, "800 единиц не могли пережить 4800 тиков");
                Assert.That(slept, Is.EqualTo(honest));
            });
        });
    }

    /// <summary>
    /// Падаль: списывается окно, а не фиксированные 16. Целые тики, поэтому
    /// тоже бит в бит.
    /// </summary>
    [Test]
    public void SleptCarcassRotsExactlyAsFarAsTheHonestOne()
    {
        WithChunkSleep(() =>
        {
            const int wakeTick = 1600;
            var honest = DecayCarcass(sleepUntil: 0, wakeTick: wakeTick);
            var slept = DecayCarcass(sleepUntil: wakeTick, wakeTick: wakeTick);

            Assert.That(slept, Is.EqualTo(honest));
            Assert.That(honest, Is.LessThan(SimBalance.CarcassDecayTicks));
        });
    }

    /// <summary>
    /// §156.4: спящая вещь только сохнет, и только базовой ставкой. Проверяем
    /// то, что обещано, — что за содержательный сон она высыхает досуха, а не
    /// что она повторяет живую кривую с её бустами.
    /// </summary>
    [Test]
    public void SleptItemDriesOutCompletely()
    {
        WithChunkSleep(() =>
        {
            var world = TestWorld.CreateWorld(12345);
            var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0);
            var stick = WorldObjectMutations.SpawnObject(
                world, ContentIds.Stick, new FragmentId(1), anchor.Tiles[0], anchor.Id);
            stick.Wetness = 1f;

            var system = new MoistureSystem();
            var chunk = ChunkMath.ChunkOf(stick.Tile);

            // Палку кто-то положил — значит чанк был бодр и получил штамп.
            world.Tick = 0;
            world.Caches.ActiveChunks.Add(chunk);
            ChunkMath.StampSimulated(world);

            // Тысяча тиков сна: базовой ставки (0.02 за такт) хватает с запасом.
            world.Tick = 1024;
            system.Run(world);

            Assert.That(stick.Wetness, Is.Zero,
                "за тысячу тиков сна вещь обязана была высохнуть досуха");
        });
    }

    // ── стенды ───────────────────────────────────────────────────────────

    private static float BurnFuel(int sleepUntil, int wakeTick, float fuel)
    {
        var world = TestWorld.CreateWorld(12345);
        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0);
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, new FragmentId(1), anchor.Tiles[0], anchor.Id);
        fire.ResourceAmount = fuel;

        Run(world, fire.Tile, sleepUntil, wakeTick, new FireSystem());
        return fire.ResourceAmount;
    }

    private static float DecayCarcass(int sleepUntil, int wakeTick)
    {
        var world = TestWorld.CreateWorld(12345);
        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0);
        var carcass = WorldObjectMutations.SpawnObject(
            world, ContentIds.CarcassAnimal, new FragmentId(1), anchor.Tiles[0], anchor.Id);
        carcass.ResourceAmount = SimBalance.CarcassDecayTicks;

        Run(world, carcass.Tile, sleepUntil, wakeTick, new CorpseSystem());
        return carcass.ResourceAmount;
    }

    /// <summary>
    /// Крутит slow-слой до <paramref name="wakeTick"/>, держа чанк объекта
    /// спящим, пока тик меньше <paramref name="sleepUntil"/>. Активный набор
    /// ставится руками: движок будит по живым NPC, а стенду нужен ровно один
    /// подконтрольный чанк.
    /// </summary>
    private static void Run(
        WorldState world, TileCoord tile, int sleepUntil, int wakeTick, ISimulationSystem system)
    {
        var chunk = ChunkMath.ChunkOf(tile);
        var weather = new WeatherSystem();
        for (world.Tick = 0; world.Tick <= wakeTick; world.Tick += world.SlowIntervalTicks)
        {
            world.Caches.ActiveChunks.Clear();
            // Нулевой такт бодрствует всегда: объект кто-то поставил, значит
            // человек там был. Так стенд повторяет настоящую последовательность
            // «пришли — ушли — вернулись», а не мир, где чанка не касались
            // никогда (у такого нет штампа и догонять в нём нечего, §156.1).
            var awake = world.Tick == 0 || world.Tick >= sleepUntil;
            if (awake)
            {
                world.Caches.ActiveChunks.Add(chunk);
            }

            weather.Run(world);
            system.Run(world);

            if (awake)
            {
                ChunkMath.StampSimulated(world);
            }
        }
    }

    private static void WithChunkSleep(Action body)
    {
        var previous = ChunkBalance.ChunkSleepEnabled;
        ChunkBalance.ChunkSleepEnabled = true;
        try
        {
            body();
        }
        finally
        {
            ChunkBalance.ChunkSleepEnabled = previous;
        }
    }
}

}
