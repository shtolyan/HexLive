#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Wearing.Garments;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace HexLive.Tests
{
    public sealed class WebAudioWarmupTests
    {
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        private static object Field(Type type, string name) => type.GetField(name, Static).GetValue(null);
        private static object Call(Type type, string name, params object[] args) => type.GetMethod(name, Static).Invoke(null, args);
        private static string[] Plan(string[] files, string voice) => (string[])Call(
            typeof(FmodSfx).Assembly.GetType("HexLive.UnityPresentation.Audio.WebAudioWarmupPlan"), "SelectFiles", files, voice);

        [SetUp]
        public void ResetAudioState()
        {
            Call(typeof(WebAudioFiles), "ResetStatics");
            Call(typeof(FmodSfx), "ResetStatics");
            ContentQueue.Reset();
        }

        [TearDown]
        public void ReleaseTestSounds()
        {
            foreach (FMOD.Sound[] group in ((IDictionary)Field(typeof(FmodSfx), "Sounds")).Values)
                foreach (var sound in group) sound.release();
            foreach (var name in new[] { "_voicesGroup", "_agentVoicesGroup", "_environmentGroup" })
            {
                var group = (FMOD.ChannelGroup)Field(typeof(FmodSfx), name);
                if (group.hasHandle()) group.release();
            }
            Call(typeof(FmodSfx), "ResetStatics");
            Call(typeof(WebAudioFiles), "ResetStatics");
            ContentQueue.Reset();
        }

        [Test]
        public void MissingDownloadsCompleteOnceAfterTheWholeBatch()
        {
            var a = Path.Combine(WebAudioFiles.LocalRoot, "not-in-manifest-a.fsb");
            var b = Path.Combine(WebAudioFiles.LocalRoot, "not-in-manifest-b.fsb");
            var calls = 0;
            WebAudioFiles.EnsureLocal(new[] { a, b }, ok => { calls++; Assert.That(ok, Is.False); });
            Assert.That(calls, Is.EqualTo(1), "inline failure must not finish each prefix of the batch");
        }

        [Test]
        public void DuplicateMissingFilesDoNotDuplicateCompletion()
        {
            var path = Path.Combine(WebAudioFiles.LocalRoot, "absent.fsb");
            var calls = 0;
            WebAudioFiles.EnsureLocal(new[] { path, path }, _ => calls++);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void EmptyOrAlreadyPresentBatchCompletesOnceWithoutDownload()
        {
            var path = Path.Combine(WebAudioFiles.LocalRoot, "present.fsb");
            ((HashSet<string>)Field(typeof(WebAudioFiles), "Present")).Add(path);
            foreach (var batch in new[] { Array.Empty<string>(), new[] { path, path } })
            {
                var calls = 0;
                WebAudioFiles.EnsureLocal(batch, ok => { calls++; Assert.That(ok, Is.True); });
                Assert.That(calls, Is.EqualTo(1));
            }
        }

        [Test]
        public void StarterPlanSelectsExactVoiceAndPairedSidecars()
        {
            var files = new[] { "voice_marta_happy_agree_0.fsb", "voice_marta_happy_agree_0.vis",
                "voice_marta_happy_agree_extra_0.fsb", "voice_jana_happy_agree_0.fsb", "menu_music.fsb" };
            Assert.That(Plan(files, " Marta "), Is.EquivalentTo(files.Take(2)));
        }

        [Test]
        public void StarterPlanCapsEachGroupAtThreeVariantsAndKeepsTheCorpusLazy()
        {
            var files = Enumerable.Range(0, 30).SelectMany(i => new[]
                { $"voice_marta_work_build_{i}.fsb", $"voice_marta_work_build_{i}.vis" })
                .Concat(new[] { "voice_marta_angry_topic_dogs_0.fsb" }).ToArray();
            var plan = Plan(files, "marta");
            Assert.That(plan.Length, Is.EqualTo(6));
            Assert.That(plan.All(p => p.Contains("work_build")), Is.True);
            Assert.That(plan.Distinct().Count(), Is.EqualTo(plan.Length));
        }

        [Test]
        public void MissingSidecarIsNotInventedAndUnknownVoicesDownloadNothing()
        {
            var files = new[] { "voice_marta_sad_hunger_0.ogg" };
            Assert.That(Plan(files, "marta"), Is.EqualTo(files));
            Assert.That(Plan(files, "missing"), Is.Empty);
        }

        [Test]
        public void AudioJobKeepsTheExistingLoadingGateBusyAndClosesOnlyOnce()
        {
            var finish = (Action)Call(typeof(FmodSfx), "BeginAudioWarmup");
            Assert.That(ContentQueue.IsIdle, Is.False);
            Assert.That(ContentQueue.Remaining, Is.EqualTo(1));
            finish(); finish();
            Assert.That(ContentQueue.IsIdle, Is.True);
            Assert.That(ContentQueue.Remaining, Is.Zero);
        }

        [Test]
        public void EarlyEngineInitializationDoesNotPretendSamplesArePrepared()
        {
            Assert.That(Call(typeof(FmodSfx), "EnsureWebAudioEngine"), Is.True);
            Assert.That(FMODUnity.RuntimeManager.IsInitialized, Is.True);
            Assert.That(Field(typeof(FmodSfx), "_ready"), Is.False);
            Assert.That(((IDictionary)Field(typeof(FmodSfx), "Sounds")).Count, Is.Zero);
        }

        [TestCase("ru")]
        [TestCase("en")]
        public void AuthoredLocaleHasPairedStarterClipsForEveryVoice(string locale)
        {
            var root = Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets", "HexLive", "Sfx", "VoicesLoc", locale);
            var voices = Directory.GetDirectories(root);
            Assert.That(voices.Length, Is.GreaterThan(0));
            foreach (var folder in voices)
            {
                var files = Plan(Directory.GetFiles(folder, "*", SearchOption.AllDirectories), Path.GetFileName(folder));
                Assert.That(files.Length, Is.EqualTo(132), folder + ": 22 groups x 3 authored variants + vis");
                Assert.That(files.Where(p => !p.EndsWith(".vis")).All(p => files.Contains(Path.ChangeExtension(p, ".vis"))), Is.True);
            }
        }

        [UnityTest]
        public IEnumerator DownloadedOpeningVocabularyIsOpenedInNativeFmodBeforeGateReleases()
        {
            FmodSfx.Prewarm();
            Assert.That(Field(typeof(FmodSfx), "_ready"), Is.True);
            var bank = FmodSfx.CurrentVoiceBank;
            var root = (string)Call(typeof(FmodSfx), "VoiceRoot", bank);
            var localeFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            var files = Plan(localeFiles, "marta").Concat(Plan(localeFiles, "jana")).ToArray();
            Assert.That(files.Length, Is.GreaterThan(0), "authored current-locale starter files must exist");
            ((HashSet<string>)Field(typeof(WebAudioFiles), "Present")).UnionWith(files);
            ((HashSet<string>)Field(typeof(FmodSfx), "RequestedWarmVoices")).Add("marta");
            ((HashSet<string>)Field(typeof(FmodSfx), "RequestedWarmVoices")).Add("jana");
            Call(typeof(FmodSfx), "WarmRequestedVoices");
            Assert.That(ContentQueue.IsIdle, Is.False, "opening FMOD groups must remain inside the curtain");
            var groups = (IDictionary)Field(typeof(FmodSfx), "Sounds");
            var opened = groups.Keys.Cast<string>().Count(key => key.StartsWith("voice_"));
            for (var frame = 0; frame < 100 && !ContentQueue.IsIdle; frame++)
            {
                yield return null;
                var now = groups.Keys.Cast<string>().Count(key => key.StartsWith("voice_"));
                Assert.That(now - opened, Is.LessThanOrEqualTo(1), "all voice jobs share one group-per-frame budget");
                opened = now;
            }
            Assert.That(ContentQueue.IsIdle, Is.True);
            Assert.That(ContentQueue.Remaining, Is.Zero);
            Assert.That(groups.Contains("voice_marta_happy_topic_smalltalk"), Is.True);
            Assert.That(groups.Contains("voice_jana_sad_wet"), Is.True);
            var line = FmodSfx.PlayTracked("voice_marta_happy_topic_smalltalk", UnityEngine.Vector3.zero, 0f);
            try
            {
                Assert.That(line.IsValid, Is.True);
                Assert.That(FmodSfx.GetLengthMs(ref line), Is.GreaterThan(0));
                Assert.That(File.Exists(line.VisemeFile), Is.True);
            }
            finally { FmodSfx.StopLoop(ref line); }
            Call(typeof(FmodSfx), "WarmRequestedVoices");
            Assert.That(ContentQueue.IsIdle, Is.True, "snapshot repetition must not enqueue the same voice again");
        }
    }
}
#endif
