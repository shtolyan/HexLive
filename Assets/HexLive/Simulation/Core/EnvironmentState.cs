namespace HexLive.Simulation.Core
{
public sealed class EnvironmentState
{
    public float GlobalTemperature { get; set; }

    public int GlobalCrowdLevel { get; set; }

    // Spec 19.7A: tick-derived clock. 0 = 06:00, wraps every game day.
    public float TimeOfDayNormalized { get; set; }

    public DayPhase Phase { get; set; } = DayPhase.Morning;

    // Spec 35.4: 0 at night, peaks 0.9 at midday.
    public float UvIndex { get; set; }

    // Spec 35.5: seeded rain fronts.
    public bool IsRaining { get; set; }

    public int RainUntilTick { get; set; }
}

public enum DayPhase
{
    Morning, // 06-12
    Day,     // 12-18
    Evening, // 18-24
    Night    // 00-06
}

}
