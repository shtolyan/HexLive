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

        private sealed class Stage : ISpeechStage
        {
            public int Started;
            public int Stopped;
            public bool ListenerRelative;
            public float PlayExternalVoiceLine(string wav, string vis, string emotion, bool listenerRelative)
            { Started++; ListenerRelative = listenerRelative; return 3f; }
            public float PlayVoiceLine(string id) => .5f;
            public void StopVoiceLine() => Stopped++;
            public bool CanSpeakExternal(bool playerReply) => playerReply;
            public bool CanSpeak(bool alarm) => alarm;
            public void ShowSpeechIcon(string key, float seconds, bool alarm = false) { }
            public void ShowSpeechImage(Sprite image, float seconds, bool alarm = false) { }
            public void HideSpeechIcon() { }
        }
    }
}
