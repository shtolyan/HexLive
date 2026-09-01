using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §156: догон костра за проспанное. Живой такт сюда НЕ входит — его по-прежнему
/// отрабатывает <see cref="FireSystem"/> своим прежним кодом, читая
/// <c>Environment.IsRaining</c>. Здесь только тики, которых мир не видел.
/// </summary>
internal static class FireMath
{
    /// <summary>
    /// Сжигает топливо за slow-такты полуинтервала <c>(fromExclusive, toExclusive)</c>
    /// и продвигает вертел на столько же тактов живого огня.
    /// <para>
    /// ⭐ МОЛЧА (§156.3): ни FireOut, ни MeatRoasted, ни FireFueled. Костёр,
    /// погасший сорок тысяч тиков назад, не имеет права появиться в хронике
    /// сегодняшним тиком — это ложь о времени, а хронику читает человек.
    /// </para>
    /// <para>
    /// §156.4: укрытие, кольцо и «в доме» берутся состоянием НА МОМЕНТ
    /// пробуждения и держатся на всё окно — в спящем чанке процессы не
    /// взаимодействуют. Дождь исключение: он глобален и детерминирован, его
    /// интегрируем честно, по отрезкам постоянной ставки.
    /// </para>
    /// </summary>
    internal static void CatchUp(
        WorldState world, WorldObjectState fire, int fromExclusive, int toExclusive, int slow)
    {
        if (slow <= 0)
        {
            return;
        }

        var tick = (fromExclusive / slow + 1) * slow;
        if (tick >= toExclusive || fire.ResourceAmount <= 0f)
        {
            return; // бодрый чанк: догонять нечего, окно пусто
        }

        var indoor = ShelterMath.IsIndoor(world, fire.Tile);
        var dryBurn = WorldBalance.FireBurnPerSlowTick;
        if (BuildSiteMath.CampfireRingComplete(fire))
        {
            dryBurn *= SimBalance.CampfireRingBurnMultiplier;
        }

        if (indoor)
        {
            dryBurn *= SimBalance.IndoorFireBurnMultiplier;
        }

        var lastTick = toExclusive - 1;
        var liveSlowTicks = 0;
        while (tick < toExclusive && fire.ResourceAmount > 0f)
        {
            // Крыша отрезает дождевой канал целиком (§42/§120), поэтому под
            // крышей ставка постоянна и всё окно — один отрезок.
            bool raining;
            int segmentEnd;
            if (indoor)
            {
                raining = false;
                segmentEnd = lastTick;
            }
            else
            {
                raining = RainMath.RainSegmentAt(world.Seed, tick, lastTick, out segmentEnd);
            }

            var burn = raining ? dryBurn * 4f : dryBurn;
            var ticksInSegment = (segmentEnd - tick) / slow + 1;
            if (ticksInSegment <= 0)
            {
                break;
            }

            // Сколько тактов до нуля при этой ставке. Ставки дидические
            // (16 × {1|4} × {1|0.5} × {1|0.5}), поэтому k-кратное списание
            // равно k последовательным — §156.5.
            var ticksToEmpty = (int)System.MathF.Ceiling(fire.ResourceAmount / burn);
            if (ticksToEmpty > ticksInSegment)
            {
                fire.ResourceAmount -= ticksInSegment * burn;
                liveSlowTicks += ticksInSegment;
                tick = segmentEnd + slow;
                continue;
            }

            fire.ResourceAmount = 0f;
            liveSlowTicks += ticksToEmpty;
            tick += ticksToEmpty * slow;

            // §151: очередь дров кормит только костёр, который ГОРЕЛ и дошёл до
            // нуля — холодная куча ждёт розжига. Слотов три, так что цикл
            // ограничен ими, а не длиной сна.
            if (!ContainerLootMath.TryConsumeCampfireFuel(world, fire, out var queuedFuel))
            {
                break;
            }

            fire.ResourceAmount += queuedFuel;
        }

        AdvanceRoast(fire, liveSlowTicks);
    }

    /// <summary>
    /// Вертел за <paramref name="liveSlowTicks"/> тактов живого огня. Прибавка
    /// на такт — БАЗОВАЯ ставка, как в живом коде: дождь и кольцо меняют расход
    /// дров, а не скорость жарки.
    /// </summary>
    private static void AdvanceRoast(WorldObjectState fire, int liveSlowTicks)
    {
        if (liveSlowTicks <= 0)
        {
            return;
        }

        var progress = liveSlowTicks * WorldBalance.FireBurnPerSlowTick;
        for (var i = 0; i < fire.Contents.Count; i++)
        {
            var item = fire.Contents[i];
            if (item.DefinitionId != ContentIds.MeatRaw)
            {
                continue;
            }

            item.ResourceAmount += progress;
            if (item.ResourceAmount >= SimBalance.MeatRoastDurationTicks)
            {
                // Готовое мясо ВИСИТ дальше и не портится на вертеле (§54.14),
                // так что пересидевший кусок ничем не хуже вовремя снятого.
                fire.Contents[i] = new Agents.ItemInstance(ContentIds.MeatCooked);
            }
        }
    }
}

}
