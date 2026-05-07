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
}

}
