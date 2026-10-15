#if UNITY_EDITOR
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{

public sealed class StandingAnimationTests
{
    [TestCase("GetUp", 1f)] [TestCase("GetUp", 4f)] [TestCase("GetUp", 0f)]
    [TestCase("RestUp", 1f)] [TestCase("RestUp", 4f)]
    [TestCase("StandUp", 1f)] [TestCase("StandUp", 4f)]
    [TestCase("PrayUp", 1f)] [TestCase("PrayUp", 4f)]
    public void RiseUsesAuthoredCadenceAndClearsTheOldWalkingSignal(string stateName, float worldSpeed)
    {
        var host = new GameObject("stand-up animator");
        var owner = new GameObject("stand-up methods"); owner.SetActive(false);
        var body = new GameObject("wake anchor");
        var controller = new AnimatorController(); controller.AddLayer("Base Layer");
        var clip = new AnimationClip(); clip.SetCurve("", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0, 0, 2, 0));
        try
        {
            foreach (var name in new[] { "Speed", "Gait", "TurnDirection" }) controller.AddParameter(name, AnimatorControllerParameterType.Float);
            controller.AddParameter("Emote", AnimatorControllerParameterType.Trigger);
            var state = controller.layers[0].stateMachine.AddState(stateName); state.motion = clip;
            var animator = host.AddComponent<Animator>(); animator.runtimeAnimatorController = controller;
            animator.Play(stateName); animator.Update(.1f);
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName(stateName), Is.True);
            var actor = owner.AddComponent<NpcActorView>();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(NpcActorView).GetField("_animator", flags)!.SetValue(actor, animator);
            typeof(NpcActorView).GetField("_simSpeed", flags)!.SetValue(actor, worldSpeed);
            typeof(NpcActorView).GetField("_animSpeed", flags)!.SetValue(actor, .65f);
            typeof(NpcActorView).GetField("_wasWalking", flags)!.SetValue(actor, true);
            typeof(NpcActorView).GetField("_bodyRoot", flags)!.SetValue(actor, body.transform);
            typeof(NpcActorView).GetField("_wakeBlend", flags)!.SetValue(actor, 1f);
            typeof(NpcActorView).GetField("_wakeFromPos", flags)!.SetValue(actor, Vector3.zero);
            typeof(NpcActorView).GetField("_wakeFromRot", flags)!.SetValue(actor, Quaternion.identity);
            typeof(NpcActorView).GetField("_moveEpsilon", flags)!.SetValue(actor, .01f);
            animator.SetFloat("Speed", 1f);
            typeof(NpcActorView).GetMethod("SampleMotion", flags)!.Invoke(actor, null);
            Assert.That(animator.speed, Is.EqualTo(worldSpeed));
            Assert.That(animator.GetFloat("Speed"), Is.Zero);
            Assert.That(animator.GetFloat("Gait"), Is.Zero);
            Assert.That(typeof(NpcActorView).GetField("_wasWalking", flags)!.GetValue(actor), Is.False);
            typeof(NpcActorView).GetMethod("PlayEmote", flags)!.Invoke(actor, new object[] { clip });
            Assert.That(animator.GetBool("Emote"), Is.False, "An ambient gesture must not replace the rise.");
            body.transform.position = Vector3.right * .1f;
            typeof(NpcActorView).GetMethod("ApplyWakeEase", flags)!.Invoke(actor, null);
            Assert.That(body.transform.position, Is.EqualTo(Vector3.zero), "The visible body must stay on its wake anchor during the rise.");
            Assert.That(typeof(NpcActorView).GetField("_wakeBlend", flags)!.GetValue(actor), Is.EqualTo(1f));
        }
        finally { Object.DestroyImmediate(owner); Object.DestroyImmediate(host); Object.DestroyImmediate(body); Object.DestroyImmediate(controller); Object.DestroyImmediate(clip); }
    }

    [Test]
    public void RealRiseClipsAndTheirTransitionsFitInsideTheModelCooldown()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/HexLive/UnityPresentation/Actors/HexNpcLocomotion.controller");
        var states = controller.layers[0].stateMachine.states.Select(s => s.state).ToArray();
        foreach (var name in new[] { "GetUp", "RestUp", "StandUp", "PrayUp" })
        {
            var state = states.Single(s => s.name == name);
            Assert.That(state.speed, Is.EqualTo(1f));
            Assert.That(state.speedParameterActive, Is.False);
            var entry = states.SelectMany(s => s.transitions.Select(t => new { source = s, transition = t }))
                .Where(p => p.transition.destinationState == state).Select(p => p.transition.hasFixedDuration
                    ? p.transition.duration : p.transition.duration * p.source.motion.averageDuration).DefaultIfEmpty(0).Max();
            var exit = state.transitions.Where(t => t.hasExitTime).Max(t => state.motion.averageDuration * t.exitTime +
                (t.hasFixedDuration ? t.duration : t.duration * state.motion.averageDuration));
            var available = (name == "PrayUp" ? StandUpMath.KneelingGraceTicks : StandUpMath.GraceTicks) * .25f;
            Assert.That(entry + exit, Is.LessThanOrEqualTo(available), name + " needs " + (entry + exit) + " seconds including blends");
        }
    }
}
}
#endif
