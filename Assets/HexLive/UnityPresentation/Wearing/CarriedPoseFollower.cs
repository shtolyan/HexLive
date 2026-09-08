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
    private Animator _handsCarrierAnimator;
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
        if (_self == null)
        {
            return;
        }

        if (_carrierNpcId < 0 || _self.RagdollActive ||
            !NpcActorView.TryGetLive(_carrierNpcId, out var carrierView))
        {
            // Streaming can temporarily remove/recreate the carrier view.
            // A previously confirmed passenger must not remain visibly frozen
            // at the old hands while there is no current attachment owner.
            _self.DeferCorpseCarriedPose();
            return;
        }

        if (!_self.PrepareCorpseCarriedPose())
        {
            return;
        }

        var animator = _self.BodyAnimator;
        var clip = CarryPoseVisuals.BeingCarriedClip;
        if (animator == null || !animator.isHuman ||
            !CarryPoseVisuals.EnsureGraph(animator, clip, ref _graph, ref _playable))
        {
            _self.DeferCorpseCarriedPose();
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
        if (_handsCarrierAnimator != carrierAnimator)
        {
            // The streamed carrier can be destroyed and recreated with the
            // same simulation id in one frame. Unity keeps the old transforms
            // alive until end-of-frame, so npcId is not a sufficient cache
            // key: never combine the new root basis with the old hands.
            _handsCarrierAnimator = carrierAnimator;
            _leftHand = null;
            _rightHand = null;
        }

        if (carrierAnimator != null && (_leftHand == null || _rightHand == null))
        {
            _leftHand = carrierAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rightHand = carrierAnimator.GetBoneTransform(HumanBodyBones.RightHand);
        }

        if (_hips == null)
        {
            _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        }

        if (_hips == null || _leftHand == null || _rightHand == null)
        {
            // A transform/basis fallback is not a carried-pose confirmation:
            // it puts the passenger near the carrier, not in her hands.
            _self.DeferCorpseCarriedPose();
            return;
        }

        // Anchor: hips to the midpoint of the carrier's hands. The hands come
        // from the Carrying clip, so the body travels with her walk, run and
        // hex-step jump for free; the offset axes stay in the carrier's own
        // facing so a swinging arm cannot spin the passenger.
        var origin = (_leftHand.position + _rightHand.position) * 0.5f;
        var target = origin + basis.TransformVector(CarryPoseVisuals.PatientOffsetPosition);
        animator.transform.position += target - _hips.position;

        // Bug #354: SetCorpseCarried keeps a dead passenger hidden until this
        // exact point. Animator.enabled only proves that a controller exists;
        // this callback proves that the BeingCarried graph really wrote its
        // final pose and attached it to the carrier.
        _self.ConfirmCorpseCarriedPose();
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
