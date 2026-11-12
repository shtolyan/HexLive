namespace HexLive.Simulation.Social
{

public static class SocialContact
{
    public const float HugFriendshipGain = 0.16f;
    public const float KissAffinityGain = 0.20f;

    public static bool IsRequest(TalkTopic? topic) => topic is TalkTopic.Hug or TalkTopic.Kiss;

    public static bool IsResult(TalkTopic? topic) => topic is TalkTopic.HugAccepted or TalkTopic.HugDeclined
        or TalkTopic.KissAccepted or TalkTopic.KissDeclined;

    public static bool IsKiss(TalkTopic topic) => topic is TalkTopic.Kiss or TalkTopic.KissAccepted or TalkTopic.KissDeclined;

    public static bool IsAccepted(TalkTopic topic) => topic is TalkTopic.HugAccepted or TalkTopic.KissAccepted;

    public static float Duration(TalkTopic result) => result switch
    {
        TalkTopic.HugAccepted => 3.133f,
        TalkTopic.HugDeclined => 4.033f,
        TalkTopic.KissAccepted => 8.033f,
        _ => 4f
    };
}

}
