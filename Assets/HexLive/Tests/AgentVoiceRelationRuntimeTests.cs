using System.Collections;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
/// <summary>§159.10: the bond with the player's voice in the relations column.</summary>
public sealed class AgentVoiceRelationRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [UnityTest]
    public IEnumerator LearnedVoiceNameRebuildsAnAlreadyVisibleRelationshipWithoutReconnect()
    {
        var owner = new GameObject("VoiceNameMethods"); owner.SetActive(false);
        var character = owner.AddComponent<CharacterPanel>();
        var relations = new VisualElement();
        typeof(CharacterPanel).GetField("_relationsContainer", Private)!.SetValue(character, relations);
        typeof(CharacterPanel).GetField("_agentAttachedNpcId", Private)!.SetValue(character, 7);
        var parse = typeof(CharacterPanel).GetMethod("ParseAgentRelation", Private)!;
        var update = typeof(CharacterPanel).GetMethod("UpdateRelations", Private)!;
        var npc = new NpcSnapshot { Id = new HexLive.Simulation.Common.EntityId(7) };
        try
        {
            parse.Invoke(character,new object[]{"{\"familiarity\":0.02,\"trust\":0,\"affinity\":0,\"voiceName\":\"Голос\"}"});
            update.Invoke(character,new object[]{npc});
            Assert.That(relations.Query<Label>().ToList().Any(l=>l.text=="Голос"),Is.True);
            parse.Invoke(character,new object[]{"{\"familiarity\":0.02,\"trust\":0,\"affinity\":0,\"voiceName\":\"Толя\"}"});
            update.Invoke(character,new object[]{npc});
            Assert.That(relations.Query<Label>().ToList().Any(l=>l.text=="Толя"),Is.True);
            Assert.That(relations.Query<Label>().ToList().Any(l=>l.text=="Голос"),Is.False);
            yield return null;
        }
        finally { Object.Destroy(owner); }
    }
}
}
