namespace HexLive.Simulation.Runtime
{
// §41.5/#453: 4.4 s GetUp +0.35 s entry +0.25 s exit at4ticks/s.
public static class StandUpMath
{
    public const int MinimumGraceTicks = 20;
    public const float BedGetUpSeconds = 126f / 30f;
    private const float BedGetUpBlendSeconds = .85f;

    public static int BedGraceTicks(float tickSeconds) => System.Math.Max(GraceTicks,
        (int)System.Math.Ceiling((BedGetUpSeconds + BedGetUpBlendSeconds) /
            System.Math.Max(.001f, tickSeconds)));
    public static int GraceTicks => System.Math.Max(AiBalance.WakeGraceTicks, MinimumGraceTicks);
    public static int KneelingGraceTicks => System.Math.Max(GraceTicks,
        System.Math.Max(Spec53.ConsoleStandUpTicks, 28));
}
}
