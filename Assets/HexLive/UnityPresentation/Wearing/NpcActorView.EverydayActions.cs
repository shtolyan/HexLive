using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using RootMotion.FinalIK;
using HexLive.Simulation.Content;

namespace HexLive.UnityPresentation.Wearing
{
    public sealed partial class NpcActorView
    {
        private AnimationClip _bedSleepClip;
        private Pose _bedEntry;
        private bool _pillowBed;
        private Transform _sleepHips;
        private Transform _sleepSpine;
        private Transform _sleepRig;
        private IKSolverLimb _sleepLeftArm;
        private IKSolverLimb _sleepRightArm;
        private EverydayActionClips _bedClips;
        private AnimationClip _everydayClip;
        private AnimationClip _requestedEverydayClip;
        private bool _everydayDrinking;
        private Vector3 _standingSipHips;
        private readonly Quaternion[] _standingSipLegs = new Quaternion[8];
        private bool _standingSipCaptured;
        private static readonly HumanBodyBones[] StandingSipBones =
        {
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes
        };
        private bool _supportDrinker;
        private GameObject _drinkContactProp;
        private Transform _drinkContactHand;
        private Vector3 _drinkOpeningInHand;
        private Vector3 _drinkAxisInHand;
        private float _aidHeadWeight;
        private float _aidHeadSampledAt;
        private bool _seatedEveryday;
        private BodyPart? _bandagePart;
        private PlayableGraph _everydayGraph;
        private AnimationClipPlayable _everydayPlayable;
        private float _everydayPhase;
        private float _everydaySampledAt;
        private float _everydayRate;
        private float _everydayDuration;
        private float _kneelingWorkSeconds;
        private float _sawContact;
        private Vector3? _sawLogTop;
        private Transform _sawFingerRig;
        private readonly Transform[,] _sawFingerJoints = new Transform[2, 12];
        private float _everydayWeight;
        private EverydayActionClips _kneelingWorkClips;
        private Transform[] _everydayBones;
        private Vector3[] _everydayPositions;
        private Quaternion[] _everydayRotations;
        private Transform _everydayAnimatorRoot;
        private int _shoreHopTick;
        private Vector3 _shoreDirection;
        private float _shoreStartY;
        private float _shoreWaterY;
        private float _shoreHeight;
        private Vector3 _shoreEdge;
        private IKSolverLimb _shoreLeftLeg;
        private IKSolverLimb _shoreRightLeg;
        private NpcActorView _feedingRecipient;
        private float _feedingContact;
        private float _eatingContact;
        private Transform _upperLip;
        private Transform _lowerLip;

        internal bool CanKneelWithTool => !_legless && _hasUsableHand && !_laying && !_swimming;

        internal void SetKneelingWork(EverydayActionClips clips, float phase, float rate, float duration,
            Vector3? sawLogTop = null)
        {
            SetEverydayAction(null, phase, rate, duration);
            _kneelingWorkClips = clips;
            _sawLogTop = sawLogTop;
        }

        private void CloseSawGrip(Vector3 handle)
        {
            if (_animator == null || !_animator.isHuman) return;
            if (_sawFingerRig != _animator.transform)
            {
                _sawFingerRig = _animator.transform;
                // The four fingers are consecutive groups of three in the
                // humanoid map. Leave the thumb to the authored kneeling pose.
                for (var joint = 0; joint < 12; joint++)
                {
                    _sawFingerJoints[0, joint] = _animator.GetBoneTransform(HumanBodyBones.RightIndexProximal + joint);
                    _sawFingerJoints[1, joint] = _animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal + joint);
                }
            }

            var row = _leftHanded ? 1 : 0;
            for (var finger = 0; finger < 12; finger += 3)
            {
                var knuckle = _sawFingerJoints[row, finger];
                var middle = _sawFingerJoints[row, finger + 1];
                var end = _sawFingerJoints[row, finger + 2];
                if (knuckle == null || middle == null || end == null) continue;
                var downFinger = middle.position - knuckle.position;
                var intoPalm = Vector3.ProjectOnPlane(handle - knuckle.position, downFinger);
                var bend = Vector3.Cross(downFinger, intoPalm);
                if (bend.sqrMagnitude <= .00000001f) continue;
                knuckle.rotation = Quaternion.AngleAxis(36f * _sawContact, bend) * knuckle.rotation;
                middle.rotation = Quaternion.AngleAxis(43f * _sawContact, bend) * middle.rotation;
                end.rotation = Quaternion.AngleAxis(25f * _sawContact, bend) * end.rotation;
            }
        }

        private void RefreshBedSleepClip(bool onBed)
        {
            if (!onBed && _bedSleepClip != null && !_laying && _wakeBlend > 0f) return;
            var clips = onBed ? EverydayActionClips.Load() : null;
            var clip = clips?.bedSleep;
            if (_bedSleepClip == clip) return;
            _bedSleepClip = clip;
            _bedClips = clips;
            if (clip != null) OverrideClip(SleepBaseClip, clip);
            else if (_animSet != null && _animSet.sleep != null && _animSet.sleep.Length > 0)
                ApplySleepPose();
            else if (_clipsByName.TryGetValue(SleepBaseClip, out var authored))
                OverrideClip(SleepBaseClip, authored);
            SetBedTransition(_bedClips?.bedEnter, "LieDown");
            SetBedTransition(_bedClips?.bedExit, "Situp To Idle");
        }

        private Pose FitBedSleepRoot(Pose anchor)
        {
            if (_bedSleepClip == null || _animator == null) return anchor;
            var state = _animator.GetCurrentAnimatorStateInfo(0);
            var blend = state.IsName("Sleep") ? 1f : state.IsName("LieDown")
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.09f, .83f, state.normalizedTime))
                : 0f;
            // The sleeping-bag take starts with feet on the floor. Its rest
            // pose was authored around the bag origin, with hips above it and
            // shifted toward the headboard. Fit that pose to the mattress.
            var scale = _bodyRoot.lossyScale.y;
            // Measured in world units on the production models. The quilt
            // and pillow need a higher pose and more clearance from the
            // headboard than the palm mattress.
            var fit = _pillowBed
                ? new Vector3(0f, -.018f, .246f)
                : new Vector3(0f, -.024f, .108f);
            var mattressRoot = anchor.position + anchor.rotation * fit *
                (scale / Rendering.HexWorldRenderer.ActorScale);
            var entry = (_bedEntry.position - anchor.position).sqrMagnitude <= 4f
                ? _bedEntry : new Pose(transform.position, transform.rotation);
            return new Pose(Vector3.Lerp(entry.position, mattressRoot, blend),
                Quaternion.Slerp(entry.rotation, anchor.rotation, blend));
        }

        private void FitSleepingBody(Quaternion bedRotation)
        {
            if (_bedSleepClip == null || _animator == null || !_animator.isHuman) return;
            var state = _animator.GetCurrentAnimatorStateInfo(0);
            if (_animator.IsInTransition(0) && _animator.GetNextAnimatorStateInfo(0).IsName("GetUp"))
                state = _animator.GetNextAnimatorStateInfo(0);
            var weight = state.IsName("Sleep") ? 1f :
                state.IsName("LieDown") ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.63f, .91f, state.normalizedTime)) :
                state.IsName("GetUp") ? Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(.04f, .32f, state.normalizedTime)) :
                0f;
            if (weight <= .001f) return;

            if (_sleepRig != _animator.transform)
            {
                _sleepRig = _animator.transform;
                _sleepHips = _animator.GetBoneTransform(HumanBodyBones.Hips);
                _sleepSpine = _animator.GetBoneTransform(HumanBodyBones.Spine);
                _sleepLeftArm = null;
                _sleepRightArm = null;
            }
            if (_sleepHips == null || _sleepSpine == null) return;
            var leftLeg = _animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            var rightLeg = _animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            var leftArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var rightArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (leftLeg == null || rightLeg == null || leftArm == null || rightArm == null) return;

            var axis = bedRotation * Vector3.forward;
            var pelvis = Vector3.ProjectOnPlane(rightLeg.position - leftLeg.position, axis);
            var shoulders = Vector3.ProjectOnPlane(rightArm.position - leftArm.position, axis);
            if (pelvis.sqrMagnitude < .0001f || shoulders.sqrMagnitude < .0001f) return;

            // Keep the authored side-lying shape while easing excessive twist
            // between the hips and the shoulders on a wider retargeted body.
            var twist = Vector3.SignedAngle(pelvis, shoulders, axis);
            var correction = Mathf.Sign(twist) * Mathf.Clamp(Mathf.Abs(twist) - 36f, 0f, 42f) * weight;
            var spineRotation = _sleepSpine.rotation;
            _sleepHips.rotation = Quaternion.AngleAxis(correction, axis) * _sleepHips.rotation;
            _sleepSpine.rotation = spineRotation;
            // Retain some of the original leg roll so the lower knee doesn't
            // rotate through the quilt while the hips settle toward the chest.
            var legCounterRoll = Quaternion.AngleAxis(-correction * .45f, axis);
            leftLeg.rotation = legCounterRoll * leftLeg.rotation;
            rightLeg.rotation = legCounterRoll * rightLeg.rotation;

            _sleepLeftArm ??= CreateSleepArm(true);
            _sleepRightArm ??= CreateSleepArm(false);
            var torso = (leftArm.position + rightArm.position + _sleepSpine.position * 2f) * .25f;
            var torsoClearance = Vector3.Distance(leftArm.position, rightArm.position) * .42f;
            var mattressUp = bedRotation * Vector3.up;
            KeepSleepingHandClear(_sleepLeftArm, torso, torsoClearance, mattressUp);
            KeepSleepingHandClear(_sleepRightArm, torso, torsoClearance, mattressUp);
        }

        private IKSolverLimb CreateSleepArm(bool left)
        {
            var shoulder = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var elbow = _animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var hand = _animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (shoulder == null || elbow == null || hand == null) return null;

            var solver = new IKSolverLimb { goal = left ? AvatarIKGoal.LeftHand : AvatarIKGoal.RightHand };
            solver.SetChain(shoulder, elbow, hand, _animator.transform);
            return solver;
        }

        private static void KeepSleepingHandClear(IKSolverLimb solver, Vector3 torso, float clearance, Vector3 mattressUp)
        {
            if (solver == null || !solver.initiated) return;
            var hand = solver.bone3.transform;
            var offset = hand.position - torso;
            if (offset.sqrMagnitude >= clearance * clearance) return;

            solver.IKPosition = hand.position + mattressUp * (clearance - Vector3.Dot(offset, mattressUp));
            solver.IKRotation = hand.rotation;
            solver.IKPositionWeight = .85f;
            solver.IKRotationWeight = 1f;
            solver.MaintainBend();
            solver.Update();
        }

        private bool FitBedWakeRoot()
        {
            if (_bedSleepClip == null || _wakeBlend <= 0f || _animator == null) return false;
            var state = _animator.GetCurrentAnimatorStateInfo(0);
            if (!state.IsName("GetUp") && _animator.IsInTransition(0))
                state = _animator.GetNextAnimatorStateInfo(0);
            if (!state.IsName("GetUp") && !state.IsName("Sleep") && !state.IsName("LieDown"))
            {
                _wakeBlend = 0f;
                return true;
            }
            var progress = state.IsName("GetUp")
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.13f, .92f, state.normalizedTime)) : 0f;
            _bodyRoot.SetPositionAndRotation(Vector3.Lerp(_wakeFromPos, _bodyRoot.position, progress),
                Quaternion.Slerp(_wakeFromRot, _bodyRoot.rotation, progress));
            FitSleepingBody(_wakeFromRot);
            return true;
        }

        private void SetBedTransition(AnimationClip clip, string baseName)
        {
            if (_clipsByName.TryGetValue(baseName, out var authored))
                OverrideClip(baseName, clip != null ? clip : authored);
        }

        internal void SetEverydayAction(AnimationClip clip, float phase, float rate, float duration, float weight = 1f)
        {
            _kneelingWorkClips = null;
            _sawLogTop = null;
            _feedingRecipient = null;
            _everydayDrinking = false;
            _supportDrinker = false;
            _seatedEveryday = false;
            _bandagePart = null;
            _requestedEverydayClip = clip;
            SetEverydayClock(phase, rate, duration, weight);
        }

        internal void SetEverydayDrinking(AnimationClip clip, float phase, float rate, float duration)
        {
            SetEverydayAction(clip, phase, rate, duration);
            _everydayDrinking = clip != null;
        }

        internal void SetEverydaySeat(bool seated) => _seatedEveryday = seated;

        internal void SetFeedingAction(AnimationClip clip, NpcActorView recipient, float phase, float rate, float duration,
            bool supportDrinker = false)
        {
            SetEverydayAction(clip, phase, rate, duration);
            _feedingRecipient = recipient;
            _supportDrinker = supportDrinker;
        }

        internal void SetBandagingAction(NpcActorView recipient, BodyPart part, float phase, float rate, float duration)
        {
            SetFeedingAction(null, recipient, phase, rate, duration);
            _bandagePart = part;
        }

        private bool TryGetMouth(out Vector3 mouth)
        {
            if (_upperLip == null) _upperLip = _bodyBones?.GetBone("LipUpperMiddle");
            if (_lowerLip == null) _lowerLip = _bodyBones?.GetBone("LipLowerMiddle");
            if (_upperLip != null && _lowerLip != null)
            {
                mouth = (_upperLip.position + _lowerLip.position) * .5f;
                return true;
            }
            if (TryGetFace(out var center, out var forward, out var up, out var scale))
            {
                mouth = center - up * (.05f * scale) + forward * (.01f * scale);
                return true;
            }
            mouth = default;
            return false;
        }

        private bool TryDriveMealContactIK(IKSolverFullBodyBiped solver, Transform hand, IKEffector effector,
            IKEffector supportHand)
        {
            var eating = _eatingContact > .001f;
            var target = eating ? this : _feedingRecipient;
            var weight = eating ? _eatingContact : _feedingContact;
            if (weight <= .001f || target == null || hand == null || !_hasUsableHand ||
                _laying || _swimming || _ragdollActive)
                return false;

            Vector3 mouth;
            if (_bandagePart is { } part)
            {
                if (!target.TryGetBandagePosition(part, out mouth)) return false;
            }
            else if (!target.TryGetMouth(out mouth)) return false;
            var drinking = _everydayDrinking || _supportDrinker ||
                !eating && _feedingRecipient != null && _currentPropId is
                    (ContentIds.Bottle or ContentIds.LeatherFlask or ContentIds.DrinkingGlass or ContentIds.CoconutPierced);
            if (_supportDrinker && target._laying && target._headBone != null && !target._ragdollActive)
            {
                target._aidHeadWeight = weight;
                target._aidHeadSampledAt = Time.unscaledTime;
            }
            var offset = hand.position - mouth;
            if (offset.sqrMagnitude > 1.21f) return false;
            if (_bandagePart.HasValue || _supportDrinker)
            {
                supportHand.target = null;
                supportHand.position = _supportDrinker && target._headBone != null
                    ? target._headBone.position - Vector3.up * .025f
                    : mouth - transform.right * .06f + Vector3.up * .015f;
                supportHand.positionWeight = weight * .78f;
                supportHand.rotationWeight = 0f;
            }
            var contact = _bandagePart.HasValue
                ? mouth + transform.right * (.035f * Mathf.Sin(_everydayPhase * 22f)) + Vector3.up * .045f
                : mouth + offset.normalized * .025f;
            effector.target = null;
            effector.position = contact - (ActionPropContactPoint(contact, hand) - hand.position);
            effector.positionWeight = weight;
            effector.rotationWeight = 0f;
            if (drinking && TryGetDrinkOpening(hand) &&
                target.TryGetFace(out _, out var faceForward, out var faceUp, out var scale))
            {
                // An upright eye frame uses world up. The head's calibrated
                // frame follows a sleeper or a person tilting back to swallow.
                if (target._faceCalibrated && target._headBone != null)
                {
                    faceForward = target._headBone.TransformDirection(target._faceLocalForward);
                    faceUp = target._headBone.TransformDirection(target._faceLocalUp);
                }
                var opening = mouth + faceForward * (.014f * scale);
                // The vessel's body points away from the face while the
                // mouth meets the lips. Coconut and flask need less tilt.
                var pitch = _currentPropId == ContentIds.Bottle ? .38f : .22f;
                var axis = (-faceForward - faceUp * pitch).normalized;
                var handRotation = Quaternion.FromToRotation(hand.rotation * _drinkAxisInHand, axis) * hand.rotation;
                effector.rotation = handRotation;
                effector.rotationWeight = weight;
                var visibleRotation = Quaternion.Slerp(hand.rotation, handRotation, weight);
                effector.position = opening - visibleRotation * _drinkOpeningInHand;
            }
            solver.pullBodyHorizontal = 0f;
            solver.IKPositionWeight = 1f;
            return true;
        }

        private bool TryGetDrinkOpening(Transform hand)
        {
            if (_handProp == null ||
                _currentPropId is not (ContentIds.Bottle or ContentIds.LeatherFlask or ContentIds.CoconutPierced))
                return false;
            if (_drinkContactProp == _handProp && _drinkContactHand == hand) return true;
            var mesh = _handProp.GetComponentInChildren<MeshFilter>();
            if (mesh == null || mesh.sharedMesh == null) return false;

            // These three source meshes put the mouth of the vessel on +Z.
            // The wrapper and the hand grip may rotate that axis independently.
            var bounds = mesh.sharedMesh.bounds;
            var localOpening = bounds.center;
            localOpening.z = _currentPropId switch
            {
                ContentIds.Bottle => bounds.center.z + bounds.extents.z * .94f,
                ContentIds.LeatherFlask => Mathf.Lerp(bounds.min.z, bounds.max.z, .92f),
                _ => bounds.max.z
            };
            var axis = mesh.transform.TransformDirection(Vector3.forward);
            var opening = mesh.transform.TransformPoint(localOpening);
            _drinkOpeningInHand = Quaternion.Inverse(hand.rotation) * (opening - hand.position);
            _drinkAxisInHand = Quaternion.Inverse(hand.rotation) * axis;
            _drinkContactProp = _handProp;
            _drinkContactHand = hand;
            return true;
        }

        private void ApplySupportedSip()
        {
            if (!_laying || _ragdollActive || _headBone == null || !_faceCalibrated ||
                Time.unscaledTime - _aidHeadSampledAt > .12f || _aidHeadWeight <= 0f) return;
            var right = Vector3.Cross(_faceLocalUp, _faceLocalForward).normalized;
            _headBone.localRotation *= Quaternion.AngleAxis(-11f * _aidHeadWeight, right);
        }

        private bool TryGetBandagePosition(BodyPart part, out Vector3 position)
        {
            position = default;
            if (_animator == null || !_animator.isHuman) return false;
            var (start, end) = part switch
            {
                BodyPart.ArmL => (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
                BodyPart.ArmR => (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
                BodyPart.LegL => (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                BodyPart.LegR => (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
                BodyPart.Head => (HumanBodyBones.Head, HumanBodyBones.Head),
                BodyPart.Pelvis => (HumanBodyBones.Hips, HumanBodyBones.Hips),
                _ => (HumanBodyBones.Chest, HumanBodyBones.Spine)
            };
            var a = _animator.GetBoneTransform(start);
            var b = _animator.GetBoneTransform(end);
            if (a == null || b == null) return false;
            position = Vector3.Lerp(a.position, b.position, .62f) + Vector3.up * .045f;
            return true;
        }

        internal bool SyncShoreClimb(string hop, bool fromDeepWater, bool toWater,
            int startTick, float age, float startY, float rise, Vector3 edge, Vector3 direction, float simRate)
        {
            if (hop != "Up" || !fromDeepWater || toWater || startTick <= 0 ||
                _legless || _posture == "Crawl" ||
                _animator == null || !_animator.isHuman || direction.sqrMagnitude < .5f)
            {
                _shoreHopTick = 0;
                return false;
            }
            if (_shoreHopTick != startTick)
            {
                _shoreHopTick = startTick;
                _shoreDirection = direction;
                _shoreEdge = edge;
                _shoreStartY = startY;
                _shoreWaterY = startY + SwimBodyLift * _swimBlend;
                _shoreHeight = rise;
            }
            var clip = EverydayActionClips.Load()?.climbFromWater;
            if (clip == null)
            {
                _shoreHopTick = 0;
                return false;
            }
            var duration = HexLive.Simulation.Navigation.HexHopTuning.ShoreClimbSeconds;
            SetEverydayAction(clip, age / duration, simRate / duration, duration);
            return true;
        }

        private void BindEverydayClip(AnimationClip clip)
        {
            if (_everydayClip == clip) return;
            StopEverydayAction();
            if (clip == null || !clip.humanMotion || _animator == null || !_animator.isHuman) return;
            _everydayClip = clip;
            _everydayGraph = PlayableGraph.Create("NPC action");
            _everydayGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _everydayPlayable = AnimationClipPlayable.Create(_everydayGraph, clip);
            _everydayPlayable.SetApplyFootIK(false);
            _everydayPlayable.SetSpeed(0);
            var output = AnimationPlayableOutput.Create(_everydayGraph, "NPC action", _animator);
            output.SetSourcePlayable(_everydayPlayable);
            _everydayGraph.Play();
        }

        private void SetEverydayClock(float phase, float rate, float duration, float weight)
        {
            _everydayPhase = phase;
            _everydaySampledAt = Time.unscaledTime;
            _everydayRate = rate;
            _everydayDuration = duration;
            _everydayWeight = weight;
        }

        private bool EvaluateEverydayAction()
        {
            _feedingContact = 0f;
            _eatingContact = 0f;
            _kneelingWorkSeconds = 0f;
            _sawContact = 0f;
            if (_animator == null || !_animator.isActiveAndEnabled || _dead || _laying || _ragdollActive ||
                _romanceVisual || _socialContactVisual)
            {
                StopEverydayAction();
                return false;
            }

            var phase = Mathf.Clamp01(_everydayPhase +
                Mathf.Min(.25f, Time.unscaledTime - _everydaySampledAt) * _everydayRate);
            var elapsed = phase * _everydayDuration;
            var weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .28f, elapsed)) *
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .28f, _everydayDuration - elapsed)) * _everydayWeight;
            if (_seatedEveryday) weight = _everydayWeight;

            var sampleTime = elapsed;
            if (_kneelingWorkClips != null)
            {
                var kneel = _kneelingWorkClips.serveKneel;
                var work = _kneelingWorkClips.serveWork;
                var rise = _kneelingWorkClips.serveRise;
                var riseAt = Mathf.Max(kneel.length, _everydayDuration - rise.length);
                if (elapsed < kneel.length) BindEverydayClip(kneel);
                else if (elapsed < riseAt)
                {
                    BindEverydayClip(work);
                    _kneelingWorkSeconds = elapsed - kneel.length;
                    if (_sawLogTop.HasValue)
                    {
                        // Hold the forward-leaning kneel while the arm solver
                        // moves the saw. Keep the approach and rise at clip pace.
                        sampleTime = Mathf.Min(_kneelingWorkSeconds, work.length);
                        _sawContact = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .55f, _kneelingWorkSeconds)) *
                            Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .42f, riseAt - elapsed));
                    }
                    else sampleTime = Mathf.PingPong(_kneelingWorkSeconds, work.length);
                }
                else
                {
                    BindEverydayClip(rise);
                    sampleTime = elapsed - riseAt;
                }
            }
            else
            {
                BindEverydayClip(_requestedEverydayClip);
                // Eating and drinking retain their gradual simulation
                // consumption window. Each full gesture uses its native pace.
                if (_everydayClip != null && (_action == ActionKind.RaiseToMouth || _everydayDrinking))
                {
                    var repetitions = Mathf.Max(1, Mathf.FloorToInt(_everydayDuration / _everydayClip.length));
                    var available = repetitions * _everydayClip.length;
                    sampleTime = elapsed < available
                        ? Mathf.Repeat(elapsed, _everydayClip.length) : _everydayClip.length;
                }
            }

            var contactPhase = _everydayClip == null ? phase :
                Mathf.Clamp01(sampleTime / Mathf.Max(.01f, _everydayClip.length));
            if (_feedingRecipient != null)
                _feedingContact = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.3f, .48f, contactPhase)) *
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.75f, .56f, contactPhase)) * weight;
            if (!_everydayGraph.IsValid())
            {
                _standingSipCaptured = false;
                return false;
            }
            var time = Mathf.Clamp(sampleTime, 0f, _everydayClip.length);
            if (_action == ActionKind.RaiseToMouth || _everydayDrinking)
            {
                _eatingContact = _everydayDrinking
                    ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.44f, .77f, time)) *
                      Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                          _currentPropId == ContentIds.Bottle ? 2.35f : 1.65f,
                          _currentPropId == ContentIds.Bottle ? 1.91f : 1.32f, time)) * weight
                    : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.42f, .68f, time)) *
                      Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.17f, .86f, time)) * weight;
            }
            if (_everydayAnimatorRoot != _animator.transform)
            {
                _everydayAnimatorRoot = _animator.transform;
                var bones = new System.Collections.Generic.List<Transform>();
                for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    var bone = _animator.GetBoneTransform((HumanBodyBones)i);
                    if (bone != null) bones.Add(bone);
                }
                _everydayBones = bones.ToArray();
                _everydayPositions = new Vector3[bones.Count];
                _everydayRotations = new Quaternion[bones.Count];
                _shoreLeftLeg = _shoreRightLeg = null;
            }
            for (var i = 0; i < _everydayBones.Length; i++)
            {
                _everydayPositions[i] = _everydayBones[i].localPosition;
                _everydayRotations[i] = _everydayBones[i].localRotation;
            }
            var standingSip = _everydayDrinking && !_seatedEveryday && !_legless && _posture != "Crawl";
            if (standingSip && !_standingSipCaptured) CaptureStandingSip();
            if (!standingSip) _standingSipCaptured = false;

            _everydayPlayable.SetTime(time);
            var position = _animator.transform.position;
            var rotation = _animator.transform.rotation;
            _everydayGraph.Evaluate(0f);
            _animator.transform.SetPositionAndRotation(position, rotation);
            for (var i = 0; i < _everydayBones.Length; i++)
            {
                var bone = _everydayBones[i];
                bone.localPosition = Vector3.Lerp(_everydayPositions[i], bone.localPosition, weight);
                bone.localRotation = Quaternion.Slerp(_everydayRotations[i], bone.localRotation, weight);
            }
            if (standingSip) ApplyStandingSip();
            if (_shoreHopTick != 0)
            {
                var rise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.27f, .88f, phase));
                var current = _animator.transform.position;
                current.y = Mathf.Lerp(_shoreWaterY, _shoreStartY + _shoreHeight, rise);
                var ahead = Vector3.Dot(current - _shoreEdge, _shoreDirection);
                var cross = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                    HexLive.Simulation.Navigation.HexHopTuning.ShoreClimbCrossStart,
                    HexLive.Simulation.Navigation.HexHopTuning.ShoreClimbCrossEnd, elapsed));
                current -= _shoreDirection * (Mathf.Max(0f, ahead) * (1f - cross) * weight);
                _animator.transform.SetPositionAndRotation(current,
                    Quaternion.Slerp(rotation, Quaternion.LookRotation(_shoreDirection), weight));
                FitShoreFeet(phase, weight);
            }
            return true;
        }

        private void CaptureStandingSip()
        {
            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return;
            _standingSipHips = hips.localPosition;
            for (var i = 0; i < StandingSipBones.Length; i++)
            {
                var bone = _animator.GetBoneTransform(StandingSipBones[i]);
                if (bone != null) _standingSipLegs[i] = bone.localRotation;
            }
            _standingSipCaptured = true;
        }

        private void ApplyStandingSip()
        {
            if (!_standingSipCaptured) return;
            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null) hips.localPosition = _standingSipHips;
            for (var i = 0; i < StandingSipBones.Length; i++)
            {
                var bone = _animator.GetBoneTransform(StandingSipBones[i]);
                if (bone != null) bone.localRotation = _standingSipLegs[i];
            }
        }

        private void FitShoreFeet(float phase, float weight)
        {
            _shoreLeftLeg ??= ShoreLeg(AvatarIKGoal.LeftFoot,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
            _shoreRightLeg ??= ShoreLeg(AvatarIKGoal.RightFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
            FitShoreFoot(_shoreRightLeg, Mathf.InverseLerp(.48f, .72f, phase), weight);
            FitShoreFoot(_shoreLeftLeg, Mathf.InverseLerp(.65f, .86f, phase), weight);
        }

        private IKSolverLimb ShoreLeg(AvatarIKGoal goal, HumanBodyBones thigh, HumanBodyBones calf, HumanBodyBones foot)
        {
            var solver = new IKSolverLimb { goal = goal };
            solver.SetChain(_animator.GetBoneTransform(thigh), _animator.GetBoneTransform(calf),
                _animator.GetBoneTransform(foot), _animator.transform);
            return solver;
        }

        private void FitShoreFoot(IKSolverLimb solver, float transfer, float weight)
        {
            if (!solver.initiated || weight <= .001f) return;
            var foot = solver.bone3.transform;
            var target = foot.position;
            var bankY = _shoreStartY + _shoreHeight;
            var ahead = Vector3.Dot(target - _shoreEdge, _shoreDirection);
            if (target.y < bankY && ahead > -.06f)
                target -= _shoreDirection * ((ahead + .06f) * (1f - transfer));
            if (transfer > 0f)
                target.y = Mathf.Lerp(target.y, Mathf.Max(target.y, bankY + .07f), transfer);
            solver.IKPosition = target;
            solver.IKRotation = foot.rotation;
            solver.IKPositionWeight = weight;
            solver.IKRotationWeight = weight;
            solver.MaintainBend();
            solver.Update();
        }

        private void StopEverydayAction()
        {
            if (_everydayGraph.IsValid()) _everydayGraph.Destroy();
            _everydayClip = null;
        }
    }
}
