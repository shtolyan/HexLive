#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Audio;
using NUnit.Framework;
using UnityEditor;

namespace HexLive.Tests
{
    public sealed class WebAudioBufferTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [TestCase(true)]
        [TestCase(false)]
        public void CanonicalWebBuildConfiguresThePlayerVerifiedBufferAndCallback(bool initiallyActive)
        {
            var settings = FMODUnity.Settings.Instance;
            var snapshots = settings.Platforms.Select(p => (platform: p, json: EditorJsonUtility.ToJson(p))).ToArray();
            try
            {
                var platform = settings.Platforms.Single(p => p.GetType().Name == "PlatformWebGL");
                typeof(FMODUnity.Platform).GetField("active", Members).SetValue(platform, initiallyActive);
                var builder = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("HexLiveWebGLPlayerBuild")).First(t => t != null);
                builder.GetMethod("ConfigureWebAudioBuffer", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, null);
                Assert.That(typeof(FMODUnity.Platform).GetProperty("Active", Members).GetValue(platform), Is.True);
                Assert.That(typeof(FMODUnity.Platform).GetProperty("DSPBufferLength", Members).GetValue(platform), Is.EqualTo(4096));
                Assert.That(typeof(FMODUnity.Platform).GetProperty("DSPBufferCount", Members).GetValue(platform), Is.EqualTo(4));
                Assert.That(typeof(FMODUnity.Platform).GetProperty("CallbackHandler", Members).GetValue(platform), Is.TypeOf<WebAudioTuning>());
            }
            finally
            {
                foreach (var snapshot in snapshots)
                {
                    EditorJsonUtility.FromJsonOverwrite(snapshot.json, snapshot.platform);
                    EditorUtility.SetDirty(snapshot.platform);
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
        }

        [TestCase(null, 4096u, 4)]
        [TestCase("https://play.example/", 4096u, 4)]
        [TestCase("https://play.example/?perf=1", 4096u, 4)]
        [TestCase("https://play.example/?fmodbuf=2048,8", 2048u, 8)]
        [TestCase("https://play.example/?perf=1&fmodbuf=4096%2C4", 4096u, 4)]
        [TestCase("https://play.example/?fmodbuf=bad", 4096u, 4)]
        [TestCase("https://play.example/?fmodbuf=128,1", 4096u, 4)]
        [TestCase("https://play.example/?fmodbuf=4096", 4096u, 4)]
        public void PreInitPolicyActuallySetsTheNativeCoreBuffer(string url, uint expectedLength, int expectedCount)
        {
            Assert.That(FMOD.Factory.System_Create(out var core), Is.EqualTo(FMOD.RESULT.OK));
            try
            {
                // Start with inherited desktop values: missing/invalid query
                // must actively apply the web default, not leave these in place.
                Assert.That(core.setDSPBufferSize(1024, 4), Is.EqualTo(FMOD.RESULT.OK));
                var calls = 0;
                Action<FMOD.RESULT, string> report = (result, operation) =>
                {
                    calls++;
                    Assert.That(result, Is.EqualTo(FMOD.RESULT.OK), operation);
                };
                typeof(WebAudioTuning).GetMethod("ConfigureDspBuffer", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { core, url, report });
                Assert.That(core.getDSPBufferSize(out var length, out var count), Is.EqualTo(FMOD.RESULT.OK));
                Assert.That(length, Is.EqualTo(expectedLength));
                Assert.That(count, Is.EqualTo(expectedCount));
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { core.release(); }
        }

        [Test]
        public void EditorAndDesktopCallbackDoNotOverrideTheirOwnBuffer()
        {
            Assert.That(FMOD.Studio.System.create(out var studio), Is.EqualTo(FMOD.RESULT.OK));
            var tuning = UnityEngine.ScriptableObject.CreateInstance<WebAudioTuning>();
            try
            {
                Assert.That(studio.getCoreSystem(out var core), Is.EqualTo(FMOD.RESULT.OK));
                Assert.That(core.setDSPBufferSize(1024, 3), Is.EqualTo(FMOD.RESULT.OK));
                tuning.PreInitialize(studio, (_, operation) => Assert.Fail(operation));
                Assert.That(core.getDSPBufferSize(out var length, out var count), Is.EqualTo(FMOD.RESULT.OK));
                Assert.That(length, Is.EqualTo(1024));
                Assert.That(count, Is.EqualTo(3));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tuning);
                studio.release();
            }
        }
    }
}
#endif
