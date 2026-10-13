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
public sealed class AccountBalanceRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Apply(CharacterPanel panel, string json) =>
        typeof(CharacterPanel).GetMethod("ApplyBalanceJson", Private)!.Invoke(panel, new object[] { json });
    private static Label Build(CharacterPanel panel, VisualElement root)
    {
        typeof(CharacterPanel).GetField("_root", Private)!.SetValue(panel, root);
        typeof(CharacterPanel).GetMethod("BuildBalance", Private)!.Invoke(panel, null);
        return root.Q<Label>("accountBalance");
    }
    private const string Partial = "{\"openingUsd\":5,\"balanceUsd\":4.9966288,\"spentUsd\":0.0033712,\"estimatedUsd\":0.0033712,\"pendingRequests\":1}";
    private const string Settled = "{\"openingUsd\":5,\"balanceUsd\":4.89139302127979723,\"spentUsd\":0.10860697872020277,\"estimatedUsd\":0.0033712,\"pendingRequests\":0}";

    [UnityTest]
    public IEnumerator FinalVoiceReceiptUpdatesBothNumbersAndShowsDollarCentUnitsInRealLayout()
    {
        var owner = new GameObject("AccountBalanceMethods"); owner.SetActive(false);
        var panel = owner.AddComponent<CharacterPanel>();
        var host = new GameObject("AccountBalanceLayout"); var document = host.AddComponent<UIDocument>();
        var settings = Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
        settings.scaleMode = PanelScaleMode.ConstantPixelSize; document.panelSettings = settings;
        var language = Loc.Current;
        try
        {
            document.rootVisualElement.style.width = 360;
            document.rootVisualElement.style.height = 100;
            var label = Build(panel, document.rootVisualElement);
            foreach (var current in new[] { Language.English, Language.Russian })
            {
                Loc.Current = current; Apply(panel, Partial);
                Assert.That(label.text, Does.Contain("4.9966 USD").And.Contain("0.0034 USD (0.34 ¢)"));
                Assert.That(label.text, Does.Contain(string.Format(Loc.Get("account.balance.pending"), 1)));
                Assert.That(label.tooltip, Does.Contain("1 USD = 100"));
                Apply(panel, Settled);
                Assert.That(label.text, Does.Contain("4.8914 USD").And.Contain("0.1086 USD (10.86 ¢)"));
                Assert.That(label.text, Does.Not.Contain("4.9966").And.Not.Contain("0.34 ¢"),
                    "A provider finalization must replace the whole display, not just the balance.");
                Assert.That(label.text, Does.Contain(Loc.Get("account.balance.estimated")));
                Assert.That(label.tooltip, Does.Contain("0.1052 USD").And.Contain("0.0034 USD"));
                for (var i = 0; i < 4; i++) yield return null;
                foreach (var line in label.text.Split('\n'))
                    Assert.That(label.MeasureTextSize(line, 0, VisualElement.MeasureMode.Undefined, 0,
                        VisualElement.MeasureMode.Undefined).x, Is.LessThanOrEqualTo(label.layout.width + .1f));
                Assert.That(label.worldBound.xMin, Is.GreaterThanOrEqualTo(0));
                Assert.That(label.worldBound.xMax, Is.LessThanOrEqualTo(360));
                Assert.That(label.worldBound.yMax, Is.LessThanOrEqualTo(100));
            }
        }
        finally { Loc.Current = language; Object.Destroy(host); Object.Destroy(owner); Object.Destroy(settings); }
    }

    [TestCase("{}")]
    [TestCase("{\"openingUsd\":5,\"balanceUsd\":4.89139302127979723,\"spentUsd\":0.0033712,\"estimatedUsd\":0.0033712,\"pendingRequests\":0}")]
    [TestCase("{\"openingUsd\":5,\"balanceUsd\":5,\"spentUsd\":0,\"estimatedUsd\":1,\"pendingRequests\":0}")]
    public void InvalidOrInconsistentSnapshotCannotShowFakeMoneyOrHangUpVoice(string json)
    {
        var owner = new GameObject("InvalidAccountBalance"); owner.SetActive(false);
        var panel = owner.AddComponent<CharacterPanel>();
        var language = Loc.Current;
        try
        {
            var label = Build(panel, new VisualElement());
            var voice = new WebVoiceAgent(); voice.Apply("[{\"type\":\"state\",\"state\":\"listening\"}]");
            typeof(CharacterPanel).GetField("_serverVoice", Private)!.SetValue(panel, voice);
            foreach (var current in new[] { Language.English, Language.Russian })
            {
                Loc.Current = current; Apply(panel, Settled); Apply(panel, json);
                Assert.That(label.text, Is.EqualTo(Loc.Get("account.balance.unavailable")));
                Assert.That(voice.Current, Is.EqualTo(WebVoiceAgent.Phase.Listening));
            }
        }
        finally { Loc.Current = language; Object.DestroyImmediate(owner); }
    }
}
}
