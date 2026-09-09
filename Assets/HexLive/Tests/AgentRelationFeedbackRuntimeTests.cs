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
    public IEnumerator LongPlainReasonWrapsAndScrollsWithinItsRuntimePanel()
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
            var reason = ("<b>Не разметка</b> " + string.Join(" ", Enumerable.Repeat("длинная причина оценки", 20))).Substring(0, 240);
            typeof(CharacterPanel).GetMethod("ParseAgentRelation", Private)!.Invoke(character,
                new object[] { JsonUtility.ToJson(new Payload { reason = reason }) });
            var buildCard = typeof(CharacterPanel).GetMethod("BuildRelationFocusCard", Private)!;
            var npcCard = (VisualElement)buildCard.Invoke(character, new object[] { new RelationshipSnapshot { OtherId = 42, OtherName = "Dasha" } });
            Assert.That(npcCard.Q<ScrollView>(className: "agent-relation-feedback"), Is.Null);
            var voiceCard = (VisualElement)buildCard.Invoke(character, new object[] { new RelationshipSnapshot { OtherId = -159, OtherName = "Голос" } });
            var feedback = voiceCard.Q<ScrollView>(className: "agent-relation-feedback");
            Assert.That(feedback, Is.Not.Null, "Only the voice relation contains its assessment.");
            feedback.RemoveFromHierarchy();
            document.rootVisualElement.style.width = 200;
            document.rootVisualElement.style.height = 200;
            document.rootVisualElement.Add(feedback);
            for (var i = 0; i < 6; i++) yield return null;
            var text = feedback.Q<Label>(className: "agent-relation-feedback-text");
            Assert.Multiple(() =>
            {
                Assert.That(feedback.panel, Is.Not.Null);
                Assert.That(feedback.layout.height, Is.GreaterThan(0).And.LessThanOrEqualTo(72.1f));
                Assert.That(text.enableRichText, Is.False);
                Assert.That(text.text, Does.Contain(reason));
                Assert.That(text.text, Does.Contain(Loc.Get("rel.familiarity") + " +2%"));
                Assert.That(text.text, Does.Contain(Loc.Get("rel.trust") + " -3%"));
                Assert.That(text.text, Does.Contain(Loc.Get("rel.affinity") + " 0%"));
                Assert.That(text.resolvedStyle.whiteSpace, Is.EqualTo(WhiteSpace.Normal));
                Assert.That(feedback.contentContainer.layout.height, Is.GreaterThan(feedback.contentViewport.layout.height));
            });
            feedback.scrollOffset = new Vector2(0, 30);
            yield return null;
            Assert.That(feedback.scrollOffset.y, Is.GreaterThan(0), "Long reasons remain readable by scrolling after actual UITK layout.");
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
