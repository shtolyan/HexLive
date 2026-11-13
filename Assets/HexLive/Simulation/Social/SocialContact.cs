namespace HexLive.Simulation.Social
{

public static class SocialContact
{
    public const float HugFriendshipGain = 0.16f;
    public const float KissAffinityGain = 0.20f;

    public static bool IsRequest(TalkTopic? topic) => topic is TalkTopic.Hug or TalkTopic.Kiss or
        TalkTopic.Greet or TalkTopic.Dance or TalkTopic.PassionateKiss or TalkTopic.FlirtGesture or TalkTopic.Apologize;

    public static bool IsResult(TalkTopic? topic) => topic is TalkTopic.HugAccepted or TalkTopic.HugDeclined
        or TalkTopic.KissAccepted or TalkTopic.KissDeclined or TalkTopic.GreetAccepted or TalkTopic.GreetDeclined or
        TalkTopic.DanceAccepted or TalkTopic.DanceDeclined or TalkTopic.PassionateKissAccepted or
        TalkTopic.PassionateKissDeclined or TalkTopic.FlirtAccepted or TalkTopic.FlirtDeclined or
        TalkTopic.ApologyAccepted;

    public static bool IsKiss(TalkTopic topic) => topic is TalkTopic.Kiss or TalkTopic.KissAccepted or
        TalkTopic.KissDeclined or TalkTopic.PassionateKiss or TalkTopic.PassionateKissAccepted or
        TalkTopic.PassionateKissDeclined;

    public static bool IsAccepted(TalkTopic topic) => topic is TalkTopic.HugAccepted or
        TalkTopic.KissAccepted or TalkTopic.GreetAccepted or TalkTopic.DanceAccepted or
        TalkTopic.PassionateKissAccepted or TalkTopic.FlirtAccepted or TalkTopic.ApologyAccepted;

    public static float Duration(TalkTopic result) => result switch
    {
        TalkTopic.HugAccepted => 3.133f,
        TalkTopic.HugDeclined => 4.033f,
        TalkTopic.KissAccepted => 8.033f,
        TalkTopic.GreetAccepted => 2.633f,
        TalkTopic.GreetDeclined => 3.267f,
        TalkTopic.DanceAccepted => 2.567f,
        TalkTopic.PassionateKissAccepted => 5f,
        TalkTopic.PassionateKissDeclined => 2.833f,
        _ => 4f
    };
}

}
