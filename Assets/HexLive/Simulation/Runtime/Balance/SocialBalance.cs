namespace HexLive.Simulation.Runtime
{

// Talk/quarrel relationship deltas and the ambient-social/sleep-comfort
// normalizers that used to be private consts in ExecutionSystem.Social and
// NeedsDecaySystem. Static so the SocialBalance config asset can push tuned
// values at boot; systems read them through shims at the old const names.
public static class SocialBalance
{
    // §28.15C: relationship gain both sides split from a finished talk.
    public static float TalkRelationshipGain = 0.075f;

    // §28.15D: a quarrel still vents social need — but costs affinity and
    // embarrasses the started-it side.
    public static float QuarrelInitiatorSocialGain = 0.15f;
    public static float QuarrelListenerSocialGain = 0.10f;
    public static float QuarrelAffinityLoss = 0.18f;
    public static float QuarrelEmbarrassment = 0.30f;

    // §28.15B: below this affinity the invited girl refuses to talk...
    public static float RefusalAffinityThreshold = -0.25f;
    // ...unless she is this lonely (social need above the threshold).
    public static float LonelinessOverrideThreshold = 0.25f;
    // A refusal stings the inviter's view of the refuser.
    public static float RejectionAffinityPenalty = 0.075f;

    // §49 ambient social: passive proximity gain can lift Social only up to
    // this cap — a real conversation is needed to go higher.
    public static float AmbientSocialCap = 0.6f;

    // §49 sleep comfort: the per-"night" comfort targets are dripped at
    // target/this per sleeping slow tick. 75 is a PER-SLOW-TICK divisor, not a
    // night length — it deliberately does not follow the (10x stretched)
    // visual clock. The drain it balances against (ComfortRate) is also per
    // slow tick, so the ratio — and the real-time pace — holds either way.
    public static float SleepComfortNightSlowTicks = 75f;
}

}
