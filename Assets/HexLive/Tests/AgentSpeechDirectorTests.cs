using System.Collections;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HexLive.Tests
{
    public sealed class AgentSpeechDirectorTests
    {
        [Test]
        public void DirectReplyUsesGenericListenerRelativeMouthEvenWhenOrdinarySpeechIsDisabled()
        {
            var stage = new Stage();
            var director = new NpcSpeechDirector(stage);
            Assert.That(director.SayExternal("test.wav", "test.vis", "warm", SpeechCatalog.Rank.Talk), Is.True);
            Assert.That(stage.ListenerRelative, Is.True);
            Assert.That(stage.Started, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AlarmInterruptsExternalTalkThroughTheSameDirector()
        {
            var stage = new Stage();
            var director = new NpcSpeechDirector(stage);
            Assert.That(director.SayExternal("test.wav", "test.vis", "warm", SpeechCatalog.Rank.Talk), Is.True);
            yield return null;
            Assert.That(director.Say("hurt_wound"), Is.True);
            Assert.That(stage.Stopped, Is.EqualTo(1));
        }

        [Test]
        public void WorkAndConversationCannotCutALongPlayerReply()
        {
            var now = 0f;
            var stage = new Stage { AllowOrdinary = true, ExternalLength = 20f };
            var director = new NpcSpeechDirector(stage, () => now);
            Assert.That(director.SayExternal("reply.wav", "reply.vis", "warm",
                SpeechCatalog.Rank.Talk), Is.True);
            now = 7f; // Past GlobalGap, with most of the WAV still remaining.
            Assert.That(director.Say("work_chop"), Is.False);
            director.OnCue("SleepRejected:Hunger");
            now = 14f;
            director.SetConversationTopic("SmallTalk");
            director.OnTalkTurn();
            Assert.That(director.SayExternal("next.wav", "next.vis", "warm",
                SpeechCatalog.Rank.Talk), Is.False);
            Assert.That(stage.Stopped, Is.Zero);
            Assert.That(stage.OrdinaryStarted, Is.Zero);
            now = 25f;
            Assert.That(director.SayExternal("next.wav", "next.vis", "warm",
                SpeechCatalog.Rank.Talk), Is.True);
        }

        [Test]
        public void SilentAlarmDoesNotReleaseTheMouthBeforeTheReplyEnds()
        {
            var now = 0f;
            var stage = new Stage { AllowOrdinary = true, ExternalLength = 20f };
            var director = new NpcSpeechDirector(stage, () => now);
            Assert.That(director.SayExternal("reply.wav", "reply.vis", "warm",
                SpeechCatalog.Rank.Talk), Is.True);
            now = 7f;
            director.OnCue("MedicalAidRejected");
            now = 15f;
            Assert.That(director.Say("work_chop"), Is.False);
            Assert.That(stage.Stopped, Is.Zero);
        }

        [Test]
        public void OrdinaryTalkAndWorldSpeechKeepTheirExistingPriorities()
        {
            var now = 0f;
            var stage = new Stage { AllowOrdinary = true, ExternalLength = 20f };
            var director = new NpcSpeechDirector(stage, () => now);
            Assert.That(director.SayExternal("world.wav", "world.vis", "warm",
                SpeechCatalog.Rank.Ambient), Is.True);
            now = 7f;
            Assert.That(director.Say("work_chop"), Is.True);
            Assert.That(stage.Stopped, Is.EqualTo(1));
        }

        private sealed class Stage : ISpeechStage
        {
            public int Started;
            public int Stopped;
            public bool ListenerRelative;
            public bool AllowOrdinary;
            public int OrdinaryStarted;
            public float ExternalLength = 3f;
            public float PlayExternalVoiceLine(string wav, string vis, string emotion, bool listenerRelative)
            { Started++; ListenerRelative = listenerRelative; return ExternalLength; }
            public float PlayVoiceLine(string id) { OrdinaryStarted++; return .5f; }
            public void StopVoiceLine() => Stopped++;
            public bool CanSpeakExternal(bool playerReply) => playerReply || AllowOrdinary;
            public bool CanSpeak(bool alarm) => alarm || AllowOrdinary;
            public void ShowSpeechIcon(string key, float seconds, bool alarm = false) { }
            public void ShowSpeechImage(Sprite image, float seconds, bool alarm = false) { }
            public void HideSpeechIcon() { }
        }
    }
}
