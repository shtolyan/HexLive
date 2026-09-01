using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §156.5: расписание дождя как формула. <see cref="WeatherSystem"/> и раньше
/// не держал состояния — фронт разыгрывается на цикл чистой функцией от
/// (сид, цикл), — но спрашивать его умели только про «сейчас». Догон спящего
/// чанка спрашивает про ОТРЕЗОК: сколько тактов из проспанных лил дождь.
/// </summary>
internal static class RainMath
{
    // Те же соли и литералы, что в WeatherSystem.Run. Второго набора здесь
    // быть не может: разъехавшись, две копии расписания дали бы «бутылка
    // наполнилась, а дождя никто не видел».
    private const float FrontChance = 0.45f;
    private const float StartSpread = 2100f;
    private const int MinDuration = 300;
    private const float DurationSpread = 600f;

    /// <summary>Шёл ли дождь на тике <paramref name="tick"/> по расписанию.</summary>
    internal static bool RainingAt(int seed, int tick)
    {
        if (tick < 0)
        {
            return false;
        }

        var cycle = tick / EnvironmentSystem.EventCycleTicks;
        if (!TryFront(seed, cycle, out var start, out var end))
        {
            return false;
        }

        return tick >= start && tick < end;
    }

    /// <summary>
    /// Сколько slow-тактов из полуинтервала <c>(fromExclusive, toInclusive]</c>
    /// пришлись на дождь. Именно тактов, а не тиков: <c>Environment.IsRaining</c>
    /// выставляет slow-слой, и всякий потребитель дождя видит его уже
    /// проквантованным этим шагом.
    /// <para>
    /// Цикл здесь по ПОГОДНЫМ ЦИКЛАМ (÷2400), а не по пропущенным тикам, и это
    /// узаконенное §156.2 исключение: десять суток сна — сто итераций хеша один
    /// раз на объект, тогда как перебор тиков был бы ровно той зависимостью от
    /// длительности сна, ради снятия которой §156 и существует.
    /// </para>
    /// </summary>
    internal static long RainSlowTicksInWindow(
        int seed, int fromExclusive, int toInclusive, int slowInterval)
    {
        if (slowInterval <= 0 || toInclusive < 0)
        {
            return 0;
        }

        // Дождя до сотворения мира не было, каким бы длинным ни было окно.
        var windowStart = System.Math.Max(fromExclusive + 1L, 0L);
        var windowEnd = toInclusive + 1L; // полуинтервал [windowStart, windowEnd)
        if (windowEnd <= windowStart)
        {
            return 0;
        }

        var cycleTicks = (long)EnvironmentSystem.EventCycleTicks;
        var total = 0L;
        var firstCycle = windowStart / cycleTicks;
        var lastCycle = (windowEnd - 1) / cycleTicks;
        for (var cycle = firstCycle; cycle <= lastCycle; cycle++)
        {
            if (!TryFront(seed, (int)cycle, out var start, out var end))
            {
                continue;
            }

            var a = System.Math.Max((long)start, windowStart);
            var b = System.Math.Min((long)end, windowEnd);
            if (b <= a)
            {
                continue;
            }

            total += MultiplesBelow(b, slowInterval) - MultiplesBelow(a, slowInterval);
        }

        return total;
    }

    /// <summary>
    /// Фронт этого цикла: <c>[start, end)</c>, уже обрезанный границей цикла.
    /// ⭐ Обрезка не косметика: на тике следующего цикла индекс уже другой, и
    /// WeatherSystem перекатывает роллы заново — хвост длинного фронта в мире
    /// просто не существует.
    /// </summary>
    private static bool TryFront(int seed, int cycle, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (cycle < 0 || MathUtil.Hash01(seed, cycle, 17, 3301) >= FrontChance)
        {
            return false;
        }

        var cycleStart = cycle * EnvironmentSystem.EventCycleTicks;
        start = cycleStart + (int)(MathUtil.Hash01(seed, cycle, 18, 3301) * StartSpread);
        var duration = MinDuration + (int)(DurationSpread * MathUtil.Hash01(seed, cycle, 19, 3302));
        end = System.Math.Min(start + duration, cycleStart + EnvironmentSystem.EventCycleTicks);
        return end > start;
    }

    /// <summary>Сколько кратных <paramref name="step"/> лежит в <c>[0, limit)</c>.</summary>
    private static long MultiplesBelow(long limit, int step) =>
        limit <= 0 ? 0 : (limit + step - 1) / step;
}

}
