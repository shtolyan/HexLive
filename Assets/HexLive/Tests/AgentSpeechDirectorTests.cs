using System.Collections;
using System.Reflection;
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

        [Test]
        public void ExternalPlaybackGainPassesThroughTheMouthArbiter()
        {
            var stage = new Stage();
            var director = new NpcSpeechDirector(stage);
            Assert.That(director.SayExternal("test.wav", "test.vis", "warm",
                SpeechCatalog.Rank.Talk, 1.2f), Is.True);
            Assert.That(stage.PlaybackGain, Is.EqualTo(1.2f));
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
        public void SelectionResetKeepsTheActiveReplyAndItsPreparedQueue()
        {
            var host = new GameObject("InactiveVoiceSelectionTest");
            host.SetActive(false);
            try
            {
                var panel = host.AddComponent<CharacterPanel>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(CharacterPanel);
                var until = type.GetField("_agentReplyFocusUntil", flags);
                var wav = type.GetField("_agentReplyWav", flags);
                until.SetValue(panel, 100f);
                wav.SetValue(panel, "current-reply.wav");
                var queue = type.GetField("_preparedAgentSpeech", flags).GetValue(panel);
                var entry = System.Activator.CreateInstance(queue.GetType().GenericTypeArguments[0], true);
                queue.GetType().GetMethod("Enqueue").Invoke(queue, new[] { entry });

                type.GetMethod("ResetAgentVoiceSelection", flags).Invoke(panel, null);

                Assert.That(until.GetValue(panel), Is.EqualTo(100f));
                Assert.That(wav.GetValue(panel), Is.EqualTo("current-reply.wav"));
                Assert.That(queue.GetType().GetProperty("Count").GetValue(queue), Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void AudibleAlarmStillInterruptsReplyAfterASilentWarning()
        {
            var now = 0f;
            var stage = new Stage { AllowOrdinary = true, ExternalLength = 20f };
            var director = new NpcSpeechDirector(stage, () => now);
            Assert.That(director.SayExternal("reply.wav", "reply.vis", "warm",
                SpeechCatalog.Rank.Talk), Is.True);
            now = 7f;
            director.OnCue("MedicalAidRejected");
            now = 8f;
            Assert.That(director.Say("hurt_wound"), Is.True);
            Assert.That(stage.Stopped, Is.EqualTo(1));
            Assert.That(stage.OrdinaryStarted, Is.EqualTo(1));
        }

        [Test]
        public void SnapshotTopicChangesAndAnotherNpcDoNotStopTheReply()
        {
            var now = 0f;
            var stage = new Stage { AllowOrdinary = true, ExternalLength = 20f };
            var director = new NpcSpeechDirector(stage, () => now);
            var otherStage = new Stage { AllowOrdinary = true };
            var otherDirector = new NpcSpeechDirector(otherStage, () => now);
            Assert.That(director.SayExternal("reply.wav", "reply.vis", "warm",
                SpeechCatalog.Rank.Talk), Is.True);
            now = 7f;
            director.SetConversationTopic("SmallTalk");
            director.OnTalkTurn();
            director.SetConversationTopic(string.Empty);
            director.OnInteraction("Chop");
            director.Tick();
            Assert.That(otherDirector.Say("work_chop"), Is.True);
            Assert.That(stage.Stopped, Is.Zero);
            Assert.That(stage.Started, Is.EqualTo(1));
            Assert.That(stage.OrdinaryStarted, Is.Zero);
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

        [Test]
        public void PhoneCallOwnsTheSameMouthAndDropsOldCues()
        {
            var now = 0f;
            var stage = new Stage { AllowOrdinary = true };
            var director = new NpcSpeechDirector(stage, () => now);
            Assert.That(director.Say("work_chop"), Is.True);
            director.SetPhoneVoice(true);
            director.SetPhoneVoice(true);
            Assert.That(stage.Stopped, Is.EqualTo(1), "reserve only once");
            now = 60f;
            Assert.That(director.Say("hurt_wound"), Is.False, "even an alarm must not overlap WebRTC");
            Assert.That(director.SayExternal("reply.wav", "reply.vis", "warm", SpeechCatalog.Rank.Talk), Is.False);
            director.OnCue("SleepRejected:Hunger");
            director.OnTalkTurn();
            director.Tick();
            Assert.That(stage.OrdinaryStarted, Is.EqualTo(1));
            director.SetPhoneVoice(false);
            director.Tick();
            Assert.That(stage.OrdinaryStarted, Is.EqualTo(1), "no queued stale speech on release");
            now += 7f;
            Assert.That(director.Say("work_chop"), Is.True);
        }

        [TestCase("happy_romance")]
        [TestCase("cry_romance_forced")]
        public void PairSceneCutsOldSpeechAndOnlyAllowsItsOwnSounds(string sceneLine)
        {
            var now = 0f;
            var stage = new Stage { AllowOrdinary = true };
            var director = new NpcSpeechDirector(stage, () => now);
            director.SetConversationTopic("Weather");
            director.OnTalkTurn();
            director.SetRomanceScene(true);
            director.SetRomanceScene(true);
            Assert.That(stage.Stopped, Is.EqualTo(1), "the line playing on entry is cut once");
            Assert.That(stage.Hidden, Is.EqualTo(1), "the old speech bubble is cleared");

            director.SetConversationTopic("Weather");
            director.OnTalkTurn();
            director.OnCue("MedicalAidRejected");
            director.OnInteraction("Chop");
            director.SetState(new SpeechCatalog.BodyState { Hunger = 1f });
            now = 30f;
            director.Tick();
            Assert.That(director.Say("hurt_wound"), Is.False);
            Assert.That(director.SayExternal("reply.wav", "reply.vis", "warm", SpeechCatalog.Rank.Talk), Is.False);
            Assert.That(stage.Shown, Is.EqualTo(2), "unrelated bubbles are suppressed too");
            Assert.That(stage.OrdinaryStarted, Is.EqualTo(1));
            Assert.That(director.Say(sceneLine), Is.True);
            Assert.That(stage.LastOrdinaryLine, Is.EqualTo(sceneLine));

            director.SetRomanceScene(false);
            Assert.That(stage.Stopped, Is.EqualTo(2), "an unfinished scene sound is cut on exit");
            now = 36f;
            director.Tick();
            Assert.That(stage.OrdinaryStarted, Is.EqualTo(2), "no stale speech replays");
            director.SetConversationTopic("Weather");
            director.OnTalkTurn();
            Assert.That(stage.LastOrdinaryLine, Is.EqualTo("sad_topic_weather"));
        }

        private sealed class Stage : ISpeechStage
        {
            public int Started;
            public int Stopped;
            public bool ListenerRelative;
            public bool AllowOrdinary;
            public int OrdinaryStarted;
            public string LastOrdinaryLine;
            public int Shown;
            public int Hidden;
            public float ExternalLength = 3f;
            public float PlaybackGain;
            public float PlayExternalVoiceLine(string wav, string vis, string emotion,
                bool listenerRelative, float playbackGain)
            { Started++; ListenerRelative = listenerRelative; PlaybackGain = playbackGain; return ExternalLength; }
            public float PlayVoiceLine(string id) { OrdinaryStarted++; LastOrdinaryLine = id; return .5f; }
            public void StopVoiceLine() => Stopped++;
            public bool CanSpeakExternal(bool playerReply) => playerReply || AllowOrdinary;
            public bool CanSpeak(bool alarm) => alarm || AllowOrdinary;
            public void ShowSpeechIcon(string key, float seconds, bool alarm = false) => Shown++;
            public void ShowSpeechImage(Sprite image, float seconds, bool alarm = false) => Shown++;
            public void HideSpeechIcon() => Hidden++;
        }
    }
}
