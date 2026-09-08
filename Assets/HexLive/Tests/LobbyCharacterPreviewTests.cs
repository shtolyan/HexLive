using System.Collections;
using HexLive.Simulation.Bootstrap;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace HexLive.Tests
{
// Run in PlayMode with the selected server's runtime content registry available.
public sealed class LobbyCharacterPreviewTests
{
    private sealed class Runner : MonoBehaviour { }
    [UnityTest, Explicit("Requires runtime actor/eye content from the selected server")]
    public IEnumerator AppearanceChangesKeepAnimatorAndBodyChangeReplacesIt()
    {
        var host = new GameObject("LobbyPreviewTest");
        var runner = host.AddComponent<Runner>();
        var character = new CharacterCreationConfig { Body = "Molly", Skin = "Molly", Eyes = "blue", Hair = "none", HairColour = "prototype" };
        var preview = new LobbyCharacterPreview(new VisualElement(), runner, character);
        try
        {
            yield return Ready(preview);
            var animator = preview.Animator; Assert.That(animator, Is.Not.Null);
            yield return new WaitForSecondsRealtime(.5f);
            var phase = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            character.Skin = "Jana"; character.Eyes = "green"; preview.Refresh(character);
            character.Skin = "Marta"; character.Eyes = "brown"; preview.Refresh(character);
            character.Skin = "Molly"; character.Eyes = "blue"; preview.Refresh(character);
            yield return Ready(preview); yield return new WaitForSecondsRealtime(.2f);
            Assert.That(preview.Animator, Is.SameAs(animator));
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.GreaterThan(phase));
            character.Body = "Jana"; preview.Refresh(character); yield return Ready(preview);
            Assert.That(preview.Animator, Is.Not.SameAs(animator));
        }
        finally { preview.Dispose(); Object.Destroy(host); }
    }
    private static IEnumerator Ready(LobbyCharacterPreview preview)
    {
        yield return null;
        var deadline = Time.realtimeSinceStartup + 70;
        while (!preview.IsReady && !preview.Failed && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.That(preview.IsReady, Is.True, "Appearance failed or timed out; check runtime asset connection.");
    }
}
}
