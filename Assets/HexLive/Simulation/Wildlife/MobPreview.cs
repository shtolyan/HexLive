using HexLive.Simulation.Common;

namespace HexLive.Simulation.Wildlife
{

// §147.2: превью виртуального зверя — ЧИСТАЯ функция (seed, слот, tick) →
// поза. Симуляция считает её на medium-тике для проверки материализации,
// клиент — каждый кадр для рендера; обе стороны обязаны сойтись бит-в-бит,
// поэтому здесь нет ни состояния, ни доступа к WorldState — только кольцо
// слота (испечённое один раз и сохранённое) и Hash01.
//
// Вся НЕСУЩАЯ математика (какой сегмент, пауза ли, альфа) живёт в ОДНОЙ
// функции ComputeStep: у сим-стороны кольцо — List<PatrolWaypoint>, у
// клиента — снапшотные вэйпоинты, и два экземпляра арифметики немедленно
// разъехались бы на 1 ULP или на порядке операций.
public static class MobPreview
{
    public struct Step
    {
        public int From;

        public int To;

        public float Alpha;

        public bool Paused;
    }

    // Соли 1123/1129 (§147.2, реестр солей). segmentTicks — тиков на сегмент
    // кольца (WildlifeBalance.MobPreviewSegmentTicks; = GlideSegmentSeconds
    // живого зверя при 4 Гц).
    public static Step ComputeStep(
        int seed, int slotId, int cycleIndex, int ringCount, int tick,
        int segmentTicks, float pauseChance)
    {
        var step = new Step { From = 0, To = 0, Alpha = 0f, Paused = true };
        if (ringCount <= 1 || segmentTicks <= 0)
        {
            return step;
        }

        // Фаза слота разводит превью по времени, чтобы стая не маршировала
        // синхронно; CycleIndex в соли — новый дом ходит по-новому.
        var phase = (int)(MathUtil.Hash01(seed, slotId, cycleIndex, 1123) *
            ringCount * segmentTicks);
        var t = tick + phase;
        var epoch = t / segmentTicks;
        step.Alpha = (t - epoch * segmentTicks) / (float)segmentTicks;
        step.Paused = MathUtil.Hash01(seed, slotId, epoch, 1129) < pauseChance;

        step.From = epoch % ringCount;
        if (step.From < 0)
        {
            step.From += ringCount;
        }

        step.To = (step.From + 1) % ringCount;
        return step;
    }

    // Сим-сторона: поза по кольцу слота. nearestIndex — вэйпоинт, чей тайл
    // считается «тайлом превью» для проверки радиуса материализации.
    public static void PreviewPose(
        int seed, MobSpawnSlot slot, int tick, int segmentTicks, float pauseChance,
        out Float2 position, out Float2 facing, out int nearestIndex)
    {
        var ring = slot.Ring;
        if (ring == null || ring.Count == 0)
        {
            position = default;
            facing = new Float2(0f, 1f);
            nearestIndex = 0;
            return;
        }

        var step = ComputeStep(
            seed, slot.SlotId, slot.CycleIndex, ring.Count, tick, segmentTicks, pauseChance);
        if (step.Paused || ring.Count == 1)
        {
            position = ring[step.From].Position;
            facing = Direction(
                ring[(step.From - 1 + ring.Count) % ring.Count].Position,
                ring[step.From].Position);
            nearestIndex = step.From;
            return;
        }

        var a = ring[step.From].Position;
        var b = ring[step.To].Position;
        position = Lerp(a, b, step.Alpha);
        facing = Direction(a, b);
        nearestIndex = step.Alpha < 0.5f ? step.From : step.To;
    }

    public static Float2 Lerp(Float2 a, Float2 b, float alpha) =>
        new(a.X + (b.X - a.X) * alpha, a.Y + (b.Y - a.Y) * alpha);

    public static Float2 Direction(Float2 from, Float2 to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = System.MathF.Sqrt(dx * dx + dy * dy);
        return length < 0.0001f ? new Float2(0f, 1f) : new Float2(dx / length, dy / length);
    }
}

}
