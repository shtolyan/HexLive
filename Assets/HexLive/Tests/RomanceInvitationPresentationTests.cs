#if UNITY_EDITOR
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Tests
{
public sealed class RomanceInvitationPresentationTests
{
    [Test]
    public void LegacyConsentReasonResolvesThroughRealFeedbackAndBothI2Languages()
    {
        var original = Loc.Current;
        try
        {
            ManualOrderFeedback.Report(1, "Order=Romance Reason=NoMutualConsent");
            var key = ManualOrderFeedback.ReasonKey;
            Loc.Current = Language.Russian;
            Assert.That(Loc.Get(key), Does.Match("[А-Яа-яЁё]"));
            Assert.That(Loc.Get(key), Is.Not.EqualTo(key));
            Loc.Current = Language.English;
            Assert.That(Loc.Get(key), Is.Not.EqualTo(key).And.Not.Empty);
        }
        finally { Loc.Current = original; ManualOrderFeedback.Clear(); }
    }

    [TestCase("RomanceRequest", "Flirt")]
    [TestCase("RomanceIncoming", "Flirt")]
    [TestCase("RomanceAccepted", "Agree")]
    [TestCase("RomanceRejected", "Fail")]
    [TestCase("RomanceRefused", "Fail")]
    public void InvitationAndAnswerHaveAuthoredCueIcons(string cue, string icon)
    {
        Assert.That(SpeechCatalog.ForCue(cue).PopIcon, Is.EqualTo(icon));
        Assert.That(UnityEngine.Resources.Load<UnityEngine.Texture2D>("HexLive/UI/Emoji/" + icon), Is.Not.Null, icon);
    }

    [TestCase("FlirtAccepted", "I like you too.", "Ты мне тоже нравишься.")]
    [TestCase("FlirtDeclined", "I’m not interested.", "Мне это неинтересно.")]
    [TestCase("PassionateKissAccepted", "Yes, I’d like that.", "Да, я тоже хочу.")]
    [TestCase("PassionateKissDeclined", "I don’t want to kiss.", "Я не хочу целоваться.")]
    public void ContactReplyHasBothI2Translations(string cue, string english, string russian)
    {
        var original = Loc.Current;
        try
        {
            var key = SpeechCatalog.ForCue(cue).ContactReplyKey;
            Assert.That(key, Is.Not.Null);
            Loc.Current = Language.English;
            Assert.That(Loc.Get(key), Is.EqualTo(english));
            Loc.Current = Language.Russian;
            Assert.That(Loc.Get(key), Is.EqualTo(russian));
        }
        finally { Loc.Current = original; }
    }
}
}
#endif
