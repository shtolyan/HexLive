using System.Collections;
using System.Reflection;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class ServerVoiceFailureRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Invoke(CharacterPanel panel, string method, params object[] arguments) =>
        typeof(CharacterPanel).GetMethod(method, Private)!.Invoke(panel, arguments);
    private static T Field<T>(CharacterPanel panel, string field) =>
        (T)typeof(CharacterPanel).GetField(field, Private)!.GetValue(panel);
    private static WebVoiceAgent Attach(CharacterPanel panel, string state)
    {
        var voice = new WebVoiceAgent();
        voice.Apply("[{\"type\":\"state\",\"state\":\"" + state + "\"}]");
        typeof(CharacterPanel).GetField("_serverVoice", Private)!.SetValue(panel, voice);
        return voice;
    }

    [TestCase("connecting", WebVoiceAgent.Phase.Connecting)]
    [TestCase("listening", WebVoiceAgent.Phase.Off)]
    [TestCase("speaking", WebVoiceAgent.Phase.Off)]
    public void ZeroBalanceOnlyEndsEstablishedCalls(string state, WebVoiceAgent.Phase expected)
    {
        var owner = new GameObject("VoiceBalanceFailure"); owner.SetActive(false);
        var panel = owner.AddComponent<CharacterPanel>();
        try
        {
            var voice = Attach(panel, state);
            Invoke(panel, "ApplyServerVoiceBalance", 0f);
            Assert.That(voice.Current, Is.EqualTo(expected));
            Assert.That(Field<string>(panel, "_callFailureTerm"),
                Is.EqualTo(state == "connecting" ? "" : "agent.call.credit"));
            if (state == "connecting")
            {
                voice.Apply("[{\"type\":\"state\",\"state\":\"error\",\"detail\":\"session:402\"}]");
                Invoke(panel, "TickServerVoice");
                Assert.That(voice.Current, Is.EqualTo(WebVoiceAgent.Phase.Off));
                Assert.That(Field<string>(panel, "_callFailureTerm"), Is.EqualTo("agent.call.credit"),
                    "The HTTP refusal must survive a zero-balance response that arrives first.");
            }
        }
        finally { Object.DestroyImmediate(owner); }
    }

    [TestCase("session:401", "agent.call.signin")]
    [TestCase("session:403", "agent.call.forbidden")]
    [TestCase("session:429", "agent.call.busy")]
    [TestCase("provider failure", "agent.call.failed")]
    public void FailedCallShowsLocalizedReasonRatherThanRawProviderDetail(string detail, string term)
    {
        var owner = new GameObject("VoiceHttpFailure"); owner.SetActive(false);
        var panel = owner.AddComponent<CharacterPanel>();
        var language = Loc.Current;
        try
        {
            Invoke(panel, "BuildAgentVoiceButton");
            Attach(panel, "connecting"); Invoke(panel, "FailServerVoice", detail);
            foreach (var current in new[] { Language.English, Language.Russian })
            {
                Loc.Current = current; Invoke(panel, "RenderServerVoiceButton");
                Assert.That(Field<Label>(panel, "_agentVoiceStateLabel").text, Is.EqualTo(Loc.Get(term)));
                Assert.That(Loc.Get(term), Is.Not.EqualTo(term));
                Assert.That(Field<VisualElement>(panel, "_agentVoiceButton").tooltip, Does.StartWith(Loc.Get(term)));
            }
        }
        finally { Loc.Current = language; Object.DestroyImmediate(owner); }
    }

    [UnityTest]
    public IEnumerator CreditRefusalStaysReadableUntilRetryAndFitsRealButton()
    {
        var owner = new GameObject("PersistentVoiceFailure"); owner.SetActive(false);
        var panel = owner.AddComponent<CharacterPanel>();
        var host = new GameObject("VoiceFailureLayout");
        var document = host.AddComponent<UIDocument>();
        var settings = Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
        settings.scaleMode = PanelScaleMode.ConstantPixelSize; document.panelSettings = settings;
        var language = Loc.Current;
        try
        {
            document.rootVisualElement.style.width = 360;
            document.rootVisualElement.style.height = 320;
            var button = (VisualElement)Invoke(panel, "BuildAgentVoiceButton");
            button.style.right = 90; button.style.display = DisplayStyle.Flex;
            document.rootVisualElement.Add(button);
            var voice = Attach(panel, "connecting");
            voice.Apply("[{\"type\":\"state\",\"state\":\"error\",\"detail\":\"session:402\"}]");
            Invoke(panel, "TickServerVoice");
            yield return new WaitForSecondsRealtime(5f);
            foreach (var current in new[] { Language.English, Language.Russian })
            {
                Loc.Current = current; Invoke(panel, "RenderServerVoiceButton");
                for (var i = 0; i < 3; i++) yield return null;
                var label = Field<Label>(panel, "_agentVoiceStateLabel");
                Assert.That(label.text, Is.EqualTo(Loc.Get("agent.call.credit")), "Failure must outlast the old four-second flash.");
                Assert.That(label.resolvedStyle.fontSize, Is.EqualTo(11f));
                var measured = label.MeasureTextSize(label.text, 0, VisualElement.MeasureMode.Undefined,
                    0, VisualElement.MeasureMode.Undefined);
                Assert.That(measured.x, Is.LessThanOrEqualTo(label.layout.width + .1f));
                Assert.That(label.worldBound.xMin, Is.GreaterThanOrEqualTo(0));
                Assert.That(label.worldBound.xMax, Is.LessThanOrEqualTo(360));
            }
            Invoke(panel, "ClearServerVoiceFailure"); Invoke(panel, "RenderServerVoiceButton");
            Assert.That(Field<Label>(panel, "_agentVoiceStateLabel").text, Is.EqualTo(Loc.Get("agent.call.start")));
        }
        finally
        {
            Loc.Current = language;
            Object.Destroy(host); Object.Destroy(owner); Object.Destroy(settings);
        }
    }

    [Test]
    public void ClearingSelectionClearsFailureEvenWhenCallEligibilityIsLost()
    {
        var owner = new GameObject("VoiceSelectionFailure"); owner.SetActive(false);
        var panel = owner.AddComponent<CharacterPanel>();
        try
        {
            Invoke(panel, "BuildAgentVoiceButton");
            Attach(panel, "connecting"); Invoke(panel, "FailServerVoice", "session:401");
            Invoke(panel, "RenderServerVoiceButton");
            Assert.That(Field<string>(panel, "_callFailureTerm"), Is.EqualTo("agent.call.signin"));
            Invoke(panel, "RefreshServerVoiceButton");
            Assert.That(Field<string>(panel, "_callFailureTerm"), Is.Empty,
                "Changing selection must clear a prior refusal before falling back from browser call UI.");
            Assert.That(Field<Label>(panel, "_agentVoiceStateLabel").style.fontSize.value.value, Is.EqualTo(8.5f),
                "Browser refusal styling must not leak into the desktop microphone caption.");
        }
        finally { Object.DestroyImmediate(owner); }
    }
}
}
