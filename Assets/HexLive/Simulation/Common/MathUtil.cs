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
}

}
