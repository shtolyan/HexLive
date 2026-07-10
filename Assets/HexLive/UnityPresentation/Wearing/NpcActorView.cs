using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Spec 31B.5: the bridge between one NPC's snapshot and her actor body.
// Owns wardrobe sync (sim WornItems -> Equip/TakeOff), the walk/idle
// animator, and the LookAtIK gaze (ported from molly_copy's pattern:
// drive solver.target + weights, ease in/out smoothly).
public sealed class NpcActorView : MonoBehaviour
{
    private static readonly int SpeedParam = Animator.StringToHash("Speed");

    private BodyBones _bodyBones;
    private Animator _animator;
    private LookAtIK _lookAtIK;
    private ActorName _actorMesh;
    private readonly Dictionary<string, int> _equippedSimItems = new();
    private readonly List<string> _removeScratch = new();

    private Transform _gazeTarget;
    private float _gazeWeight;
    private float _gazeWeightTarget;
    private Transform _gazeProxy;
    private Transform _bodyRoot;

    // Spec 31B.5: animation follows measured view motion, not sim status —
    // feet move exactly when the body visibly moves.
    private static readonly int TurnDirectionParam = Animator.StringToHash("TurnDirection");
    private static readonly int LayingParam = Animator.StringToHash("Laying");
    private static readonly int WorkingParam = Animator.StringToHash("Working");
    private static readonly int SittingParam = Animator.StringToHash("Sitting");
    private string _currentPropId;
    private GameObject _handProp;
    private bool _laying;
    private Transform _layingAttach;
    private Vector3 _lastPosition;
    private float _lastYaw;
    private float _moveEpsilon = 0.01f;
    private bool _motionSampleValid;

    public void Construct(string actorMeshName)
    {
        _bodyBones = GetComponentInChildren<BodyBones>();
        _animator = GetComponentInChildren<Animator>();
        _lookAtIK = GetComponentInChildren<LookAtIK>();

        if (System.Enum.TryParse(actorMeshName, out ActorName parsed) == false)
        {
            Debug.LogWarning($"Unknown actor mesh '{actorMeshName}', defaulting to Marta", this);
            parsed = ActorName.Marta;
        }

        _actorMesh = parsed;
        if (_bodyBones != null)
        {
            _bodyBones.Construct(_actorMesh);
        }

        // Spec 31B.5: the simulation is the only mover — the Animator must
        // never drag the body child away from its view root (root-motion
        // walk clips did exactly that on turns).
        if (_animator != null)
        {
            _animator.applyRootMotion = false;
            // Spec 31C.8: baked lying poses confuse skinned bounds — culled
            // animators freeze mid-pose while the root keeps moving.
            _animator.cullingMode = UnityEngine.AnimatorCullingMode.AlwaysAnimate;
            _bodyRoot = _animator.transform;
            // Spec 31B.5: 10 % of body height per second separates
            // "standing" from "walking" at any view scale.
            _moveEpsilon = 1.7f * _bodyRoot.lossyScale.y * 0.1f;
        }

        // Spec 31B.5: FBBIK stays dormant — its effector targets lived on
        // stripped components; an unfed solver freezes the whole pose.
        var fbbik = GetComponentInChildren<FullBodyBipedIK>();
        if (fbbik != null)
        {
            fbbik.enabled = false;
        }

        if (_lookAtIK != null)
        {
            _lookAtIK.solver.IKPositionWeight = 0f;
        }

        _gazeProxy = new GameObject("GazeTarget").transform;
        _gazeProxy.SetParent(transform.parent, false);
    }

    // World-space gaze point (talk partner's head, a spot down the path).
    public void LookAtPoint(Vector3 worldPoint)
    {
        if (_gazeProxy == null)
        {
            return;
        }

        _gazeProxy.position = worldPoint;
        LookAt(_gazeProxy);
    }

    // Sim worn list -> visual garments. A sim item may map to several wear
    // prefabs (underwear = bra + panties); each equips under its own key.
    public void SyncWorn(IReadOnlyList<string> wornDefinitionIds)
    {
        if (_bodyBones == null)
        {
            return;
        }

        _removeScratch.Clear();
        foreach (var equipped in _equippedSimItems.Keys)
        {
            if (Contains(wornDefinitionIds, equipped) == false)
            {
                _removeScratch.Add(equipped);
            }
        }

        foreach (var simId in _removeScratch)
        {
            for (var i = 0; i < _equippedSimItems[simId]; i++)
            {
                _bodyBones.TakeOff($"{simId}#{i}");
            }

            _equippedSimItems.Remove(simId);
        }

        foreach (var simId in wornDefinitionIds)
        {
            if (_equippedSimItems.ContainsKey(simId))
            {
                continue;
            }

            var prefabs = ActorWardrobe.GetVisuals(simId);
            for (var i = 0; i < prefabs.Count; i++)
            {
                _bodyBones.Equip($"{simId}#{i}", prefabs[i]);
            }

            _equippedSimItems[simId] = prefabs.Count;
        }
    }

    // Spec 31C.2: sleeping snaps the view to the bed's attach point and
    // plays the Laying state; waking releases back to the renderer's flow.
    public void SetLaying(bool laying, Transform attachPoint)
    {
        _laying = laying;
        _layingAttach = attachPoint;
        if (_animator != null)
        {
            _animator.SetBool(LayingParam, laying);
        }
    }

    // Spec 31C.6: interaction poses — crouch while gathering/working, sit
    // on Sit, and hold the relevant item in the right hand.
    public void SetInteraction(string interaction, string heldItemId)
    {
        if (_animator != null)
        {
            var working = interaction is "PickUp" or "Harvest" or "Build" or "Craft"
                or "Fuel" or "Bury" or "Hang";
            _animator.SetBool(WorkingParam, working);
            _animator.SetBool(SittingParam, interaction == "Sit");
        }

        SetHandProp(heldItemId);
    }

    private void SetHandProp(string itemId)
    {
        if (_currentPropId == itemId)
        {
            return;
        }

        _currentPropId = itemId;
        if (_handProp != null)
        {
            Destroy(_handProp);
            _handProp = null;
        }

        if (string.IsNullOrEmpty(itemId) || _bodyBones == null)
        {
            return;
        }

        var hand = _bodyBones.GetBone("rHand");
        var model = Resources.Load<GameObject>($"HexLive/Objects/{itemId}");
        if (hand == null || model == null)
        {
            return;
        }

        _handProp = Instantiate(model, hand);
        _handProp.name = $"HandProp {itemId}";

        // Normalize to a palm-sized prop regardless of source model size.
        var renderers = _handProp.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            var biggest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            var target = 1.7f * _bodyRoot.lossyScale.y * 0.12f;
            if (biggest > 0.0001f)
            {
                _handProp.transform.localScale *= target / biggest;
            }
        }

        _handProp.transform.localPosition = Vector3.zero;
        _handProp.transform.localRotation = Quaternion.identity;
    }

    private void SampleMotion()
    {
        if (_animator == null)
        {
            return;
        }

        if (_laying)
        {
            _animator.SetFloat(SpeedParam, 0f);
            _animator.SetFloat(TurnDirectionParam, 0f);
            _motionSampleValid = false;
            return;
        }

        var position = transform.position;
        var yaw = transform.eulerAngles.y;
        if (!_motionSampleValid || Time.deltaTime <= 0f)
        {
            _lastPosition = position;
            _lastYaw = yaw;
            _motionSampleValid = true;
            return;
        }

        var delta = position - _lastPosition;
        delta.y = 0f;
        var linearSpeed = delta.magnitude / Time.deltaTime;
        var yawSpeed = Mathf.DeltaAngle(_lastYaw, yaw) / Time.deltaTime;
        _lastPosition = position;
        _lastYaw = yaw;

        var walking = linearSpeed > _moveEpsilon;
        _animator.SetFloat(SpeedParam, walking ? 1f : 0f, 0.05f, Time.deltaTime);

        // Turning on the spot: meaningful yaw rate while standing.
        var turn = 0f;
        if (!walking && Mathf.Abs(yawSpeed) > 25f)
        {
            turn = Mathf.Sign(yawSpeed);
        }

        _animator.SetFloat(TurnDirectionParam, turn, 0.05f, Time.deltaTime);
    }

    // Gaze: talkers look at each other, walkers glance down the path.
    public void LookAt(Transform target)
    {
        _gazeTarget = target;
        _gazeWeightTarget = target != null ? 1f : 0f;
    }

    public void ClearGaze()
    {
        _gazeWeightTarget = 0f;
    }

    private void LateUpdate()
    {
        SampleMotion();

        // Belt and braces for the single movement flow: whatever animation
        // or IK nudged, the body sits exactly on its root (scale is the
        // renderer's normalization — preserved).
        if (_bodyRoot != null)
        {
            var attachSane = _layingAttach != null &&
                (_layingAttach.position - transform.position).magnitude < _moveEpsilon * 30f;
            if (_laying && attachSane)
            {
                // Lie on the bed: the attach point owns the pose.
                _bodyRoot.position = _layingAttach.position;
                _bodyRoot.rotation = _layingAttach.rotation;
            }
            else
            {
                _bodyRoot.localPosition = Vector3.zero;
                _bodyRoot.localRotation = Quaternion.identity;
            }
        }

        if (_lookAtIK == null)
        {
            return;
        }

        _gazeWeight = Mathf.MoveTowards(_gazeWeight, _gazeWeightTarget, Time.deltaTime * 2.5f);
        _lookAtIK.solver.IKPositionWeight = _gazeWeight;
        if (_gazeTarget != null)
        {
            _lookAtIK.solver.target = _gazeTarget;
            _lookAtIK.solver.headWeight = 0.8f;
            _lookAtIK.solver.eyesWeight = 1f;
            _lookAtIK.solver.bodyWeight = 0.2f;
            _lookAtIK.solver.clampWeight = 0.5f;
        }
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return true;
            }
        }

        return false;
    }
}

// Spec 31B.4: Resources-convention wardrobe — no inspector wiring.
internal static class ActorWardrobe
{
    private static readonly Dictionary<string, List<Wear>> _cache = new();

    public static IReadOnlyList<Wear> GetVisuals(string simDefinitionId)
    {
        if (_cache.TryGetValue(simDefinitionId, out var cached))
        {
            return cached;
        }

        var result = new List<Wear>();
        foreach (var prefab in Resources.LoadAll<GameObject>($"HexLive/Wear/{simDefinitionId}"))
        {
            var wear = prefab.GetComponent<Wear>();
            if (wear != null)
            {
                result.Add(wear);
            }
        }

        _cache[simDefinitionId] = result;
        return result;
    }
}

}
