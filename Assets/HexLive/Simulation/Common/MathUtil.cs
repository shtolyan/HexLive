namespace HexLive.Simulation.Common
{
public static class MathUtil
{
    public static float Clamp01(float value)
    {
        if (value < 0f)
        {
            return 0f;
        }

        return value > 1f ? 1f : value;
    }

    public static float MoveTowards(float current, float target, float maxDelta)
    {
        if (current < target)
        {
            return current + maxDelta > target ? target : current + maxDelta;
        }

        return current - maxDelta < target ? target : current - maxDelta;
    }

    public static float DeltaAngle(float current, float target)
    {
        var delta = target - current;
        // Normalize to -180..+180 (handle C# negative modulo)
        delta = ((delta % 360f) + 540f) % 360f - 180f;
        return delta;
    }

    public static float NormalizeAngle(float angle)
    {
        return ((angle % 360f) + 540f) % 360f - 180f;
    }

    public static float RotateTowards(float current, float target, float maxDelta)
    {
        var delta = DeltaAngle(current, target);
        if (Abs(delta) <= maxDelta) return NormalizeAngle(target);
        var step = delta > 0f ? maxDelta : -maxDelta;
        return NormalizeAngle(current + step);
    }

    public static float Abs(float value)
    {
        return value < 0f ? -value : value;
    }

    public static float Clamp(float value, float min, float max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    // Seeded variant (spec 29C.1): runs are reproducible per seed and
    // diverge across seeds.
    public static float Hash01(int seed, int a, int b, int c)
    {
        return Hash01(seed ^ (a * 486187739), b, c);
    }

    // Stateless deterministic hash → [0, 1). Used for outcome rolls
    // (spec 28.15B): no RNG state, identical across runs and resumes.
    public static float Hash01(int a, int b, int c)
    {
        unchecked
        {
            var h = 2166136261u;
            h = (h ^ (uint)a) * 16777619u;
            h = (h ^ (uint)b) * 16777619u;
            h = (h ^ (uint)c) * 16777619u;
            h ^= h >> 13;
            h *= 0x5BD1E995u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}

}
