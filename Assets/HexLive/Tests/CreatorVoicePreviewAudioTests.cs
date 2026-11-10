#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Localization;
using NUnit.Framework;

namespace HexLive.Tests
{
public sealed class CreatorVoicePreviewAudioTests
{
    [Test]
    public void CachedVoiceTracksCurrentLanguageWithoutWaitingForTheDeferredI2Event()
    {
        var previous = Loc.Current; FmodSfx.Loop handle = default;
        try
        {
            FmodSfx.Prewarm();
            // No yield or forced localization callback: this is the actual
            // same-frame race, including an already selected/loading group.
            foreach (var language in new[] { Language.Russian, Language.English, Language.Russian })
            {
                Loc.Current = language;
                const string group = "voice_marta_agree_directive";
                Assert.That(FmodSfx.HasSound(group), Is.True);
                handle = FmodSfx.PlayTracked(group, UnityEngine.Vector3.zero, listenerRelative: true);
                Assert.That(handle.IsValid, Is.True);
                var locale = language == Language.Russian ? "ru" : "en";
                Assert.That(handle.File.Replace('\\', '/'), Does.Contain("/VoicesLoc/" + locale + "/marta/"));
                Assert.That(FmodSfx.IsPlaying(ref handle), Is.True);
                FmodSfx.StopLoop(ref handle);
            }
        }
        finally { FmodSfx.StopLoop(ref handle); Loc.Current = previous; }
    }
    [TestCase(Language.Russian, "ru")]
    [TestCase(Language.English, "en")]
    public void PreviewPlaysActualLocalizedAudioInTheVoicesGroupWithoutSpatialAttenuationOrPitchJitter(Language language, string locale)
    {
        var previous = Loc.Current;
        var audio = new CreatorVoicePreviewAudio();
        try
        {
            Loc.Current = language; audio.Prepare();
            Assert.That(audio.Failed, Is.False);
            foreach (var voice in new[] { "marta", "molly", "jana", "jolly", "kshishtof", "masha" })
            {
                var groups = audio.Groups(voice);
                Assert.That(groups, Is.Not.Empty, voice);
                Assert.That(groups.All(g => g.StartsWith("voice_" + voice + "_", StringComparison.Ordinal)), Is.True);
                var group = groups.First();
                Assert.That(audio.Ready(group), Is.True, group);
                Assert.That(audio.Start(group), Is.True, group);
                var handle = (FmodSfx.Loop)typeof(CreatorVoicePreviewAudio).GetField("_playing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(audio);
                Assert.That(handle.File.Replace('\\', '/'), Does.Contain("/VoicesLoc/" + locale + "/" + voice + "/"));
                Assert.That(audio.IsPlaying, Is.True);
                var channel = (FMOD.Channel)typeof(FmodSfx.Loop).GetField("Channel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handle);
                Assert.That(channel.getMode(out var mode), Is.EqualTo(FMOD.RESULT.OK));
                Assert.That((mode & FMOD.MODE._2D) != 0, Is.True);
                Assert.That((mode & FMOD.MODE._3D) == 0, Is.True);
                Assert.That(channel.getPitch(out var pitch), Is.EqualTo(FMOD.RESULT.OK)); Assert.That(pitch, Is.EqualTo(1f));
                Assert.That(channel.getChannelGroup(out var category), Is.EqualTo(FMOD.RESULT.OK));
                Assert.That(category.getName(out var name, 128), Is.EqualTo(FMOD.RESULT.OK)); Assert.That(name, Is.EqualTo("HexLiveVoices"));
                audio.Stop(); Assert.That(audio.IsPlaying, Is.False);
            }
        }
        finally { audio.Stop(); Loc.Current = previous; }
    }
}
}
#endif
