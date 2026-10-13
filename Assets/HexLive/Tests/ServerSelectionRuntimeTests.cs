#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class ServerSelectionRuntimeTests
    {
        [Test]
        public void DirectoryRejectsCredentialUrlsDuplicatesAndUnsupportedDocuments()
        {
            var json = Resources.Load<TextAsset>(ServerDirectory.ResourcePath).text;
            Assert.That(ServerDirectory.Read(json).Length, Is.EqualTo(2));
            Assert.Throws<ArgumentException>(() => ServerDirectory.Read(json.Replace("\"version\": 1", "\"version\": 2")));
            Assert.Throws<ArgumentException>(() => ServerDirectory.Read(json.Replace("wss://play.", "wss://secret@play.")));
            Assert.Throws<ArgumentException>(() => ServerDirectory.Read(json.Replace("\"new-york\"", "\"singapore\"")));
            Assert.Throws<ArgumentException>(() => ServerDirectory.Read(json.Replace("/play/\"", "/play/?token=secret\"")));
            Assert.That(ServerDirectory.SameOrigin("wss://a.test/watch", "https://a.test/play/"), Is.True);
            Assert.That(ServerDirectory.SameOrigin("wss://a.test/watch", "https://b.test/play/"), Is.False);
            Assert.That(ServerDirectory.SameOrigin("wss://a.test/watch", "https://a.test:444/play/"), Is.False);
        }

        [TestCase(-1, "offline")]
        [TestCase(99, "good")]
        [TestCase(100, "fair")]
        [TestCase(249, "fair")]
        [TestCase(250, "poor")]
        public void PingQualityBoundaries(int milliseconds, string expected) =>
            Assert.That(ServerDirectory.PingClass(milliseconds), Is.EqualTo(expected));

        [UnityTest]
        public IEnumerator CenteredCardsShowMeasuredQualityAndClickConnectsOnce()
        {
            var host = new GameObject("Server menu verification");
            var owner = host.AddComponent<ServerMenuTestHost>();
            var settings = Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080); settings.match = 1;
            var target = new RenderTexture(1920, 1080, 0); target.Create(); settings.targetTexture = target;
            var document = host.AddComponent<UIDocument>(); document.panelSettings = settings;
            var calls = 0;
            var protocol = Handshake.ProtocolVersion;
            IEnumerator Probe(ServerDirectoryEntry entry, Action<ServerProbeResult> done)
            {
                yield return null;
                done(new ServerProbeResult(entry.id == "singapore" ? 80 : 180, protocol));
            }
            var menu = new ServerSelectionPanel(owner, _ => ++calls,
                "wss://play.62-146-235-120.sslip.io/watch", Probe);
            try
            {
                var root = document.rootVisualElement;
                root.style.width = Length.Percent(100); root.style.height = Length.Percent(100);
                root.style.backgroundImage = new StyleBackground(Resources.Load<Texture2D>("HexLive/UI/loading_island"));
                root.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                root.Add(menu);
                yield return null; yield return null; yield return null;
                var sg = menu.Q<Button>("server-singapore"); var ny = menu.Q<Button>("server-new-york");
                Assert.That(sg.enabledSelf, Is.True); Assert.That(ny.enabledSelf, Is.False, "NY has no deployed web Player yet.");
                Assert.That(sg.ClassListContains("good"), Is.True); Assert.That(ny.ClassListContains("fair"), Is.True);
                Assert.That(sg.Q<Label>(className: "server-ping").text, Does.Contain("80"));
                var card = menu.Q<VisualElement>(className: "server-selection-card").worldBound;
                Assert.That(Math.Abs(card.center.x - root.worldBound.center.x), Is.LessThan(2));
                Assert.That(Math.Abs(card.center.y - root.worldBound.center.y), Is.LessThan(2));
                Assert.That(card.width, Is.GreaterThan(300));
                yield return null;
                var previousTarget = RenderTexture.active;
                var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                    var folder = Path.GetFullPath("Build/MSIRelease/20261012-seams/server-menu-tests");
                    Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "server-menu.png"), image.EncodeToPNG());
                }
                finally { RenderTexture.active = previousTarget; Object.Destroy(image); }
                protocol++;
                Click(menu.Q<Button>(className: "server-refresh"));
                yield return null; yield return null;
                Assert.That(sg.enabledSelf, Is.False, "Own-server protocol mismatch must block connect.");
                protocol--;
                Click(menu.Q<Button>(className: "server-refresh"));
                yield return null; yield return null;
                Click(sg); Click(sg);
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(settings); target.Release(); Object.DestroyImmediate(target); }
        }

        private static void Click(Button button) => typeof(Clickable).GetMethod("SimulateSingleClick",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(button.clickable, new object[] { null, 0 });

        [UnityTest]
        public IEnumerator DetachedPanelCancelsProbesAndRejectsLateResults()
        {
            var host = new GameObject("Server menu cancellation");
            var owner = host.AddComponent<ServerMenuTestHost>();
            var settings = Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
            var document = host.AddComponent<UIDocument>(); document.panelSettings = settings;
            var cancelled = 0;
            Action<ServerProbeResult> late = null;
            IEnumerator Probe(ServerDirectoryEntry entry, Action<ServerProbeResult> done)
            {
                late = done;
                try { yield return new WaitForSecondsRealtime(30); }
                finally { cancelled++; }
            }
            var menu = new ServerSelectionPanel(owner, _ => Assert.Fail("Detached menu connected"), null, Probe);
            try
            {
                document.rootVisualElement.Add(menu); yield return null;
                var row = menu.Q<Button>("server-new-york");
                menu.RemoveFromHierarchy(); late(new ServerProbeResult(10, Handshake.ProtocolVersion));
                Assert.That(cancelled, Is.EqualTo(2));
                Assert.That(row.ClassListContains("pending"), Is.True);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(settings); }
        }
    }

    public sealed class ServerMenuTestHost : MonoBehaviour { }
}
#endif
