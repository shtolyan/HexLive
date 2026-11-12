#nullable enable
using System;
using HexLive.Simulation.Social;
using UnityEngine;

namespace HexLive.UnityPresentation.TwoPeopleTest
{

public sealed class SocialContactClipCatalog : ScriptableObject
{
    public const string ResourcesPath = "HexLive/SocialContact";
#if UNITY_EDITOR
    public const string AssetPath = "Assets/HexLiveContent/RuntimeSource/SocialContact.asset";
#endif

    [Serializable]
    private struct Pair
    {
        public AnimationClip? initiator;
        public AnimationClip? partner;
        public readonly bool IsValid =>
            initiator != null && initiator.humanMotion && initiator.length > 0f &&
            partner != null && partner.humanMotion && partner.length > 0f;
    }

    [SerializeField] private Pair hugAccepted;
    [SerializeField] private Pair hugDeclined;
    [SerializeField] private Pair kissAccepted;
    [SerializeField] private Pair kissDeclined;

    internal bool HasAllClips =>
        hugAccepted.IsValid && hugDeclined.IsValid && kissAccepted.IsValid && kissDeclined.IsValid;

    internal bool TryGet(TalkTopic outcome, out AnimationClip? initiator, out AnimationClip? partner)
    {
        var pair = outcome switch
        {
            TalkTopic.HugAccepted => hugAccepted,
            TalkTopic.HugDeclined => hugDeclined,
            TalkTopic.KissAccepted => kissAccepted,
            TalkTopic.KissDeclined => kissDeclined,
            _ => default
        };
        initiator = pair.initiator;
        partner = pair.partner;
        return pair.IsValid;
    }
}

}
