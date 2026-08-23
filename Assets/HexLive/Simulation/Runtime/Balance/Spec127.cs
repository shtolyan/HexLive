namespace HexLive.Simulation.Runtime
{

/// <summary>§127: authoritative timing, consent gates and consequences.</summary>
public static class Spec127
{
    public const bool RomanceEnabled = true;

    // 24 000 ticks/day => 1 000/hour => 1 334 ticks ≈ 80 game minutes.
    // The original 667/32 timeline was doubled as a whole, so both the
    // 0.1→1 ramp and the fixed-speed climax receive twice as much time.
    public const int DurationTicks = 1_334;
    public const int ClimaxTicks = 64;
    public const int VictimCryingTicks = 360;
    public const int CooldownTicks = 6_000;
    public const float AutonomousAfterTalkChance = 0.25f;
    public const float ForcedAfterAbuseChance = 0.35f;

    public const float MinAffinity = 0.50f;
    public const float MinTrust = 0.25f;
    public const float MinFamiliarity = 0.45f;

    public const float ConsentSocialGain = 0.85f;
    public const float ConsentAffinityGain = 0.10f;
    public const float ConsentTrustGain = 0.06f;
    public const float ConsentFamiliarityGain = 0.08f;

    public const float ForcedInitiatorSocialGain = 1f;
    public const float ForcedVictimSocialLoss = 0.70f;
    public const float ForcedAffinityLoss = 0.80f;
    public const float ForcedTrustLoss = 0.90f;
    public const float ForcedPelvisDamageMin = 0.05f;
    public const float ForcedPelvisDamageMax = 0.08f;

    // One full scene can move either extreme of the normalized stress scale
    // all the way to its authored outcome (§127.12).
    public const float StressChangePerTick = 1f / DurationTicks;

    public const float MinPlaybackSpeed = 0.1f;
    public const float MaxPlaybackSpeed = 1f;
    public const float ClimaxPlaybackSpeed = 0.5f;
}

}
