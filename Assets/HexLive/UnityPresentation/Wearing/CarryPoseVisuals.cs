using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// §118/§116 carry visuals — one shared definition of the Mixamo carry pair.
/// The CARRIER keeps every clip she normally plays (idle/walk/run/jump) and
/// takes only her ARMS from Carrying.fbx (NpcActorView.ApplyCarryPose); the
/// PATIENT wears the whole BeingCarried pose and hangs by her hips from the
/// midpoint of the carrier's hands (CarriedPoseFollower). Both bodies sample
/// the same fixed frame through a manually evaluated PlayableGraph — no IK,
/// no procedural pose authoring. Tuned in the CarryPoseTest scene.
/// </summary>
internal static class CarryPoseVisuals
{
    // Resources/HexLive/CarryPoses/*.fbx, imported Humanoid. Resources keeps
    // them in every player build with no addressables work.
    private const string CarryingClipResource = "HexLive/CarryPoses/Carrying";
    private const string BeingCarriedClipResource = "HexLive/CarryPoses/BeingCarried";

    /// <summary>Кадр пары (нормированное время обоих клипов).</summary>
    internal const float SampleNormalizedTime = 0.3f;

    /// <summary>
    /// Где таз несомой относительно середины кистей несущей, в осях несущей
    /// (x — вправо, y — вверх, z — вперёд). Подобрано в CarryPoseTest.
    /// </summary>
    internal static readonly Vector3 PatientOffsetPosition = Vector3.zero;

    /// <summary>Разворот несомой относительно направления несущей.</summary>
    internal static readonly Vector3 PatientOffsetEuler = new(0f, 90f, 0f);

    private static AnimationClip _carryingClip;
    private static AnimationClip _beingCarriedClip;
    private static bool _loaded;

    internal static AnimationClip CarryingClip
    {
        get
        {
            EnsureLoaded();
            return _carryingClip;
        }
    }

    internal static AnimationClip BeingCarriedClip
    {
        get
        {
            EnsureLoaded();
            return _beingCarriedClip;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded && _carryingClip != null && _beingCarriedClip != null)
        {
            return;
        }

        _loaded = true;
        _carryingClip = LoadFirstClip(CarryingClipResource);
        _beingCarriedClip = LoadFirstClip(BeingCarriedClipResource);
        if (_carryingClip == null || _beingCarriedClip == null)
        {
            Debug.LogWarning("CarryPoseVisuals: atomic carry clips are not ready: " +
                CarryingClipResource + " / " + BeingCarriedClipResource);
        }
    }

    private static AnimationClip LoadFirstClip(string resourcePath)
    {
        foreach (var clip in HexLive.UnityPresentation.Content.AtomicResources.LoadAll<AnimationClip>(resourcePath))
        {
            if (!clip.name.StartsWith("__preview"))
            {
                return clip;
            }
        }

        return null;
    }

    /// <summary>
    /// Lazily builds the one-clip graph bound to the animator and returns
    /// true when it is ready to evaluate. Manual update mode: the pose is
    /// stamped only when EvaluateAt is called, never by Unity's own player.
    /// </summary>
    internal static bool EnsureGraph(
        Animator animator, AnimationClip clip,
        ref PlayableGraph graph, ref AnimationClipPlayable playable)
    {
        if (animator == null || clip == null)
        {
            return false;
        }

        if (!graph.IsValid())
        {
            graph = PlayableGraph.Create("CarryPose " + animator.gameObject.name);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetApplyFootIK(false);
            playable.SetApplyPlayableIK(false);
            var output = AnimationPlayableOutput.Create(graph, "pose", animator);
            output.SetSourcePlayable(playable);
        }

        return true;
    }

    internal static void EvaluateAt(
        AnimationClip clip, PlayableGraph graph, AnimationClipPlayable playable)
    {
        playable.SetTime(clip.length * SampleNormalizedTime);
        graph.Evaluate(0f);
    }
}

}
