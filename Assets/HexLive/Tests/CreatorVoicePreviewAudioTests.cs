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
