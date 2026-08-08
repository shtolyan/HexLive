using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// §118.4 carried body: the patient wears the WHOLE BeingCarried pose and
/// hangs by her hips from the midpoint of the carrier's hands. Lives on the
/// patient's actor and is bound/unbound by HexWorldRenderer from the carry
/// link in the snapshot. The execution order puts this LateUpdate after every
/// NpcActorView (order 0) — the carrier's clip-sampled arms are final by the
/// time the hip anchor is measured off them, whichever order the two actors
/// spawned in.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(31000)]
public sealed class CarriedPoseFollower : MonoBehaviour
{
    private NpcActorView _self;
    private int _carrierNpcId = -1;
    private PlayableGraph _graph;
    private AnimationClipPlayable _playable;
    private Transform _hips;
    private int _handsCarrierId = -1;
    private Transform _leftHand;
    private Transform _rightHand;

    internal void Bind(int carrierNpcId) => _carrierNpcId = carrierNpcId;

    internal void Unbind() => _carrierNpcId = -1;

    private void Awake()
    {
        _self = GetComponent<NpcActorView>();
    }

    private void LateUpdate()
    {
        if (_carrierNpcId < 0 || _self == null || _self.RagdollActive ||
            !NpcActorView.TryGetLive(_carrierNpcId, out var carrierView))
        {
            return;
        }

        var animator = _self.BodyAnimator;
        var clip = CarryPoseVisuals.BeingCarriedClip;
        if (animator == null || !animator.isHuman ||
            !CarryPoseVisuals.EnsureGraph(animator, clip, ref _graph, ref _playable))
        {
            return;
        }

        // Facing first — the clip lays the hips in a different world spot
        // depending on it, and the alignment below measures that spot.
        var basis = carrierView.transform;
        var rootPosition = animator.transform.position;
        var rootRotation = basis.rotation *
            Quaternion.Euler(CarryPoseVisuals.PatientOffsetEuler);
        animator.transform.rotation = rootRotation;

        // The humanoid clip writes the root too; a sampled POSE is joints
        // only, so the root goes back verbatim before the hip alignment.
        CarryPoseVisuals.EvaluateAt(clip, _graph, _playable);
        animator.transform.SetPositionAndRotation(rootPosition, rootRotation);

        var carrierAnimator = carrierView.BodyAnimator;
        if (carrierAnimator != null && _handsCarrierId != _carrierNpcId)
        {
            _leftHand = carrierAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rightHand = carrierAnimator.GetBoneTransform(HumanBodyBones.RightHand);
            _handsCarrierId = _carrierNpcId;
        }

        if (_hips == null)
        {
            _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        }

        // Anchor: hips to the midpoint of the carrier's hands. The hands come
        // from the Carrying clip, so the body travels with her walk, run and
        // hex-step jump for free; the offset axes stay in the carrier's own
        // facing so a swinging arm cannot spin the passenger.
        var origin = _leftHand != null && _rightHand != null
            ? (_leftHand.position + _rightHand.position) * 0.5f
            : basis.position;
        var target = origin + basis.TransformVector(CarryPoseVisuals.PatientOffsetPosition);
        if (_hips != null)
        {
            animator.transform.position += target - _hips.position;
        }
    }

    private void OnDestroy()
    {
        if (_graph.IsValid())
        {
            _graph.Destroy();
        }
    }
}

}
