#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Social;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace HexLive.UnityPresentation.TwoPeopleTest
{

[DefaultExecutionOrder(150)]
public sealed class SocialContactPairView : MonoBehaviour
{
    private static readonly Dictionary<string, AnimationClip> Clips = new(StringComparer.Ordinal);
    private NpcActorView? first;
    private NpcActorView? second;
    private TalkTopic result;
    private int startTick;
    private Vector3 firstPosition;
    private Vector3 secondPosition;
    private float phase;
    private float phaseSpeed;
    private float phaseAt;
    private PlayableGraph graph;
    private AnimationClipPlayable firstTrack;
    private AnimationClipPlayable secondTrack;
    private SocialHugContact? firstHug;
    private SocialHugContact? secondHug;
    private SocialKissContact? firstKiss;
    private SocialKissContact? secondKiss;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetClips() => Clips.Clear();

    internal static bool CanPlayAll() =>
        TryClips(TalkTopic.HugAccepted, out _, out _) && TryClips(TalkTopic.HugDeclined, out _, out _) &&
        TryClips(TalkTopic.KissAccepted, out _, out _) && TryClips(TalkTopic.KissDeclined, out _, out _);

    private static bool TryClips(TalkTopic topic, out AnimationClip? x, out AnimationClip? y)
    {
        var stem = topic switch
        {
            TalkTopic.HugAccepted => "a2a_friendly_T_hug_succeed",
            TalkTopic.HugDeclined => "a2a_friendly_T_hug_fail",
            TalkTopic.KissAccepted => "a2a_romantic_T_kiss_firstKiss_succeed",
            _ => "a2a_romantic_T_kiss_shy_fail"
        };
        x = FindClip(stem + "_x");
        y = FindClip(stem + "_y");
        return x != null && y != null;
    }

    private static AnimationClip? FindClip(string name)
    {
        if (Clips.TryGetValue(name, out var cached) && cached != null) return cached;
#if UNITY_EDITOR
        foreach (var asset in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(
            $"Assets/HexLiveLocalExperiments/SimsSocial/Clips/{name}.fbx"))
            if (asset is AnimationClip clip && clip.name == name && clip.humanMotion)
                return Clips[name] = clip;
#endif
        foreach (var clip in AtomicResources.LoadAll<AnimationClip>("HexLive/SocialContact"))
            if (clip.humanMotion && clip.name == name) Clips[clip.name] = clip;
        return Clips.TryGetValue(name, out cached) ? cached : null;
    }

    internal bool Sync(NpcActorView a, NpcActorView b, TalkTopic topic,
        int start, int end, float tick, float tickSeconds, float simSpeed, Vector3 positionA, Vector3 positionB)
    {
        if (a.BodyAnimator == null || b.BodyAnimator == null || a.RagdollActive || b.RagdollActive)
        {
            Stop();
            return false;
        }

        if (!graph.IsValid() || first != a || second != b || result != topic || startTick != start)
        {
            Stop();
            if (!TryClips(topic, out var x, out var y)) return false;
            first = a;
            second = b;
            result = topic;
            startTick = start;
            graph = PlayableGraph.Create("Social contact");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            firstTrack = Bind(a.BodyAnimator, x!);
            secondTrack = Bind(b.BodyAnimator, y!);
            a.SetSocialContactVisual(true);
            b.SetSocialContactVisual(true);
            graph.Play();
            graph.Evaluate(0f);
            if (result == TalkTopic.HugAccepted)
            {
                firstHug = CreateHug(a);
                secondHug = CreateHug(b);
            }
            if (result == TalkTopic.KissAccepted)
            {
                firstKiss = new SocialKissContact(a.BodyAnimator);
                secondKiss = new SocialKissContact(b.BodyAnimator);
            }
        }

        firstPosition = positionA;
        secondPosition = positionB;
        var ticks = Mathf.Max(1, end - start);
        phase = Mathf.Clamp01((tick - start) / ticks);
        phaseSpeed = simSpeed / Mathf.Max(0.001f, ticks * tickSeconds);
        phaseAt = Time.unscaledTime;
        return true;
    }

    private static SocialHugContact CreateHug(NpcActorView actor) =>
        new(actor.BodyAnimator, actor.BodyAnimator.transform,
            actor.BodyAnimator.GetComponentsInChildren<SkinnedMeshRenderer>(false));

    private AnimationClipPlayable Bind(Animator animator, AnimationClip clip)
    {
        var track = AnimationClipPlayable.Create(graph, clip);
        track.SetApplyFootIK(false);
        track.SetSpeed(0);
        AnimationPlayableOutput.Create(graph, animator.name, animator).SetSourcePlayable(track);
        return track;
    }

    private void LateUpdate()
    {
        if (!graph.IsValid() || first == null || second == null) return;
        var progress = Mathf.Clamp01(phase + (Time.unscaledTime - phaseAt) * phaseSpeed);
        var axis = secondPosition - firstPosition;
        axis.y = 0f;
        if (axis.sqrMagnitude < 0.0001f) axis = first.transform.forward;
        axis.Normalize();
        var separation = (SocialContact.IsKiss(result) ? 0.70f : 0.76f) *
            Mathf.Abs(first.BodyAnimator.transform.lossyScale.y);
        var midpoint = (firstPosition + secondPosition) * 0.5f;
        var stage = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.16f, progress)) *
            Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f, 0.94f, progress));
        first.transform.SetPositionAndRotation(Vector3.Lerp(firstPosition, midpoint - axis * separation * 0.5f, stage),
            Quaternion.LookRotation(axis));
        second.transform.SetPositionAndRotation(Vector3.Lerp(secondPosition, midpoint + axis * separation * 0.5f, stage),
            Quaternion.LookRotation(-axis));

        firstHug?.RestoreFingerPose();
        secondHug?.RestoreFingerPose();
        firstTrack.SetTime(progress * firstTrack.GetAnimationClip().length);
        secondTrack.SetTime(progress * secondTrack.GetAnimationClip().length);
        graph.Evaluate(0f);

        var hugWeight = SocialHugContact.Weight(progress);
        if (firstHug != null && secondHug != null && hugWeight > 0f)
        {
            firstHug.ReadPose();
            secondHug.ReadPose();
            firstHug.Fit(secondHug, hugWeight, progress);
            secondHug.Fit(firstHug, hugWeight, progress);
        }
        if (firstKiss != null && secondKiss != null)
            SocialKissContact.Fit(firstKiss, secondKiss, SocialKissContact.Weight(progress));
    }

    internal void Stop()
    {
        firstHug?.RestoreFingerPose();
        secondHug?.RestoreFingerPose();
        firstHug?.Dispose();
        secondHug?.Dispose();
        firstHug = secondHug = null;
        firstKiss = secondKiss = null;
        if (graph.IsValid()) graph.Destroy();
        if (first != null) first.SetSocialContactVisual(false);
        if (second != null) second.SetSocialContactVisual(false);
        first = second = null;
    }

    private void OnDestroy() => Stop();
}

}
