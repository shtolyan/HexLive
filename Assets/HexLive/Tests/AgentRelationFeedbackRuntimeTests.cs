using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
/// <summary>§159.10: public assessment through real runtime UITK layout.</summary>
public sealed class AgentRelationFeedbackRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Serializable]
    private sealed class Payload
    {
        public string reason;
        public string voiceName = "Голос";
        public bool hasChange = true;
        public float familiarityDelta = .02f;
        public float trustDelta = -.03f;
        public float affinityDelta;
    }

    [UnityTest]
    public IEnumerator CompactDeltasOpenBoundedPlainTextHoverPopupAndRepositionAfterExpansion()
    {
        var owner = new GameObject("RelationMethods");
        owner.SetActive(false); // Exercise production UI methods without starting a player world.
        var character = owner.AddComponent<CharacterPanel>();
        var host = new GameObject("RelationLayout");
        var document = host.AddComponent<UIDocument>();
        var template = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        Assert.That(template, Is.Not.Null);
        var settings = Object.Instantiate(template);
        settings.scaleMode = PanelScaleMode.ConstantPixelSize;
        document.panelSettings = settings;
        try
        {
            const string reason = "Короткая оценка";
            var expandedReason = ("<b>Не разметка</b>\n\n" +
                string.Join(" ", Enumerable.Repeat("длинная причина оценки", 20))).Substring(0, 240);
            typeof(CharacterPanel).GetMethod("ParseAgentRelation", Private)!.Invoke(character,
                new object[] { JsonUtility.ToJson(new Payload { reason = reason }) });
            typeof(CharacterPanel).GetField("_root", Private)!.SetValue(character, document.rootVisualElement);
            var buildCard = typeof(CharacterPanel).GetMethod("BuildRelationFocusCard", Private)!;
            var npcCard = (VisualElement)buildCard.Invoke(character, new object[] { new RelationshipSnapshot { OtherId = 42, OtherName = "Dasha" } });
            Assert.That(npcCard.Q<VisualElement>(className: "agent-relation-feedback"), Is.Null);
            var voiceCard = (VisualElement)buildCard.Invoke(character, new object[] { new RelationshipSnapshot { OtherId = -159, OtherName = "Голос" } });
            var feedback = voiceCard.Q<VisualElement>(className: "agent-relation-feedback");
            Assert.That(feedback, Is.Not.Null, "Only the voice relation contains its assessment.");
            document.rootVisualElement.style.width = 360;
            document.rootVisualElement.style.height = 320;
            document.rootVisualElement.Add(voiceCard);
            for (var i = 0; i < 6; i++) yield return null;
            Assert.That(feedback.panel, Is.Not.Null);
            Assert.That(feedback.layout.height, Is.EqualTo(24f).Within(.1f));
            var deltas = feedback.Query<Label>(className: "agent-relation-delta").ToList();
            Assert.That(deltas.Select(x => x.text), Is.EqualTo(new[] { "+0", "+2", "-3" }),
                "Compact values follow the same Affinity/Familiarity/Trust order as the rings.");

            using (var enter = MouseEnterEvent.GetPooled()) feedback.SendEvent(enter);
            for (var i = 0; i < 3; i++) yield return null;
            var popup = document.rootVisualElement.Q<VisualElement>(className: "agent-relation-tooltip");
            Assert.That(popup, Is.Not.Null);
            Assert.That(popup.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(popup.pickingMode, Is.EqualTo(PickingMode.Ignore), "The popup cannot steal hover or clicks.");
            Assert.That(popup.worldBound.xMin, Is.GreaterThanOrEqualTo(document.rootVisualElement.worldBound.xMin - .1f));
            Assert.That(popup.worldBound.xMax, Is.LessThanOrEqualTo(document.rootVisualElement.worldBound.xMax + .1f));
            Assert.That(popup.worldBound.yMin, Is.GreaterThanOrEqualTo(document.rootVisualElement.worldBound.yMin - .1f));
            Assert.That(popup.worldBound.yMax, Is.LessThanOrEqualTo(document.rootVisualElement.worldBound.yMax + .1f));
            var popupReason = popup.Q<Label>(className: "agent-relation-tooltip-reason");
            Assert.That(popupReason.enableRichText, Is.False);
            Assert.That(popupReason.text, Is.EqualTo(reason));
            Assert.That(popupReason.resolvedStyle.whiteSpace, Is.EqualTo(WhiteSpace.Normal));
            var details = popup.Query<Label>(className: "agent-relation-tooltip-metric-value").ToList();
            Assert.That(details.Select(x => x.text), Is.EqualTo(new[] { "+0%", "+2%", "-3%" }));

            typeof(CharacterPanel).GetMethod("ParseAgentRelation", Private)!.Invoke(character,
                new object[] { JsonUtility.ToJson(new Payload { reason = reason }) });
            yield return null;
            Assert.That(popup.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                "Unchanged relation polling must not dismiss a popup under a stationary pointer.");

            typeof(CharacterPanel).GetMethod("ParseAgentRelation", Private)!.Invoke(character,
                new object[] { JsonUtility.ToJson(new Payload { reason = expandedReason, trustDelta = .03f }) });
            Assert.That(popup.resolvedStyle.display, Is.EqualTo(DisplayStyle.None),
                "A changed payload must not leave stale details visible.");
            using (var leaveAfterChange = MouseLeaveEvent.GetPooled()) feedback.SendEvent(leaveAfterChange);
            using (var firstReenter = MouseEnterEvent.GetPooled()) feedback.SendEvent(firstReenter);
            using (var immediateLeave = MouseLeaveEvent.GetPooled()) feedback.SendEvent(immediateLeave);
            using (var finalReenter = MouseEnterEvent.GetPooled()) feedback.SendEvent(finalReenter);
            for (var i = 0; i < 6; i++) yield return null;
            Assert.That(popup.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(popup.resolvedStyle.visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(popup.Q<Label>(className: "agent-relation-tooltip-reason").text, Is.EqualTo(expandedReason));
            Assert.That(popup.Query<Label>(className: "agent-relation-tooltip-metric-value").ToList()
                .Select(x => x.text), Is.EqualTo(new[] { "+0%", "+2%", "+3%" }));
            Assert.That(popup.worldBound.xMin, Is.GreaterThanOrEqualTo(document.rootVisualElement.worldBound.xMin - .1f));
            Assert.That(popup.worldBound.xMax, Is.LessThanOrEqualTo(document.rootVisualElement.worldBound.xMax + .1f));
            Assert.That(popup.worldBound.yMin, Is.GreaterThanOrEqualTo(document.rootVisualElement.worldBound.yMin - .1f));
            Assert.That(popup.worldBound.yMax, Is.LessThanOrEqualTo(document.rootVisualElement.worldBound.yMax + .1f),
                "A stale short-popup schedule must not leave expanded details outside the panel.");

            using (var leave = MouseLeaveEvent.GetPooled()) feedback.SendEvent(leave);
            yield return null;
            Assert.That(popup.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }
        finally
        {
            Object.Destroy(host);
            Object.Destroy(owner);
            Object.Destroy(settings);
        }
    }
}
}
