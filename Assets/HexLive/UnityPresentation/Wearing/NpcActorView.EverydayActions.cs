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
        private EverydayActionClips _bedClips;
        private AnimationClip _everydayClip;
        private AnimationClip _requestedEverydayClip;
        private bool _everydayDrinking;
        private bool _supportDrinker;
        private bool _seatedEveryday;
        private BodyPart? _bandagePart;
        private PlayableGraph _everydayGraph;
        private AnimationClipPlayable _everydayPlayable;
        private float _everydayPhase;
        private float _everydaySampledAt;
        private float _everydayRate;
        private float _everydayWeight;
        private bool _everydayServing;
        private EverydayActionClips _servingClips;
        private Transform[] _everydayBones;
        private Vector3[] _everydayPositions;
        private Quaternion[] _everydayRotations;
        private Transform _everydayAnimatorRoot;
        private int _shoreHopTick;
        private Vector3 _shoreDirection;
        private float _shoreStartY;
        private float _shoreHeight;
        private Vector3 _shoreEdge;
        private IKSolverLimb _shoreLeftLeg;
        private IKSolverLimb _shoreRightLeg;
        private NpcActorView _feedingRecipient;
        private float _feedingContact;
        private float _eatingContact;
        private Transform _upperLip;
        private Transform _lowerLip;

        internal void SetEverydayServing(EverydayActionClips clips, float phase, float rate)
        {
            _everydayServing = true;
            _servingClips = clips;
            SetEverydayClock(phase, rate, 1f);
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

        private void SetBedTransition(AnimationClip clip, string baseName)
        {
            if (_clipsByName.TryGetValue(baseName, out var authored))
                OverrideClip(baseName, clip != null ? clip : authored);
        }

        internal void SetEverydayAction(AnimationClip clip, float phase, float rate, float weight = 1f)
        {
            _everydayServing = false;
            _feedingRecipient = null;
            _everydayDrinking = false;
            _supportDrinker = false;
            _seatedEveryday = false;
            _bandagePart = null;
            _requestedEverydayClip = clip;
            SetEverydayClock(phase, rate, weight);
        }

        internal void SetEverydayDrinking(AnimationClip clip, float phase, float rate)
        {
            SetEverydayAction(clip, phase, rate);
            _everydayDrinking = clip != null;
        }

        internal void SetEverydaySeat(bool seated) => _seatedEveryday = seated;

        internal void SetFeedingAction(AnimationClip clip, NpcActorView recipient, float phase, float rate,
            bool supportDrinker = false)
        {
            SetEverydayAction(clip, phase, rate);
            _feedingRecipient = recipient;
            _supportDrinker = supportDrinker;
        }

        internal void SetBandagingAction(NpcActorView recipient, BodyPart part, float phase, float rate)
        {
            SetFeedingAction(null, recipient, phase, rate);
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
            solver.pullBodyHorizontal = 0f;
            solver.IKPositionWeight = 1f;
            return true;
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
                _shoreHeight = rise;
            }
            var clip = EverydayActionClips.Load()?.climbFromWater;
            if (clip == null)
            {
                _shoreHopTick = 0;
                return false;
            }
            var duration = HexLive.Simulation.Navigation.HexHopTuning.HopSeconds;
            SetEverydayAction(clip, age / duration, simRate / duration);
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

        private void SetEverydayClock(float phase, float rate, float weight)
        {
            _everydayPhase = phase;
            _everydaySampledAt = Time.unscaledTime;
            _everydayRate = rate;
            _everydayWeight = weight;
        }

        private bool EvaluateEverydayAction()
        {
            _feedingContact = 0f;
            _eatingContact = 0f;
            if (_animator == null || !_animator.isActiveAndEnabled || _dead || _laying || _ragdollActive ||
                _romanceVisual || _socialContactVisual)
            {
                StopEverydayAction();
                return false;
            }

            var phase = Mathf.Clamp01(_everydayPhase +
                Mathf.Min(.25f, Time.unscaledTime - _everydaySampledAt) * _everydayRate);
            var weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .12f, phase)) *
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f, .88f, phase)) * _everydayWeight;
            if (_seatedEveryday) weight = _everydayWeight;
            if (_everydayServing)
            {
                // The three takes meet at the kneeling pose. The work clip
                // starts with the same posture and leans into the cut.
                const float workStart = .23f;
                const float riseStart = .79f;
                var clip = phase < workStart ? _servingClips.serveKneel
                    : phase < riseStart ? _servingClips.serveWork : _servingClips.serveRise;
                BindEverydayClip(clip);
                phase = phase < workStart ? phase / workStart
                    : phase < riseStart ? Mathf.InverseLerp(workStart, riseStart, phase)
                    : Mathf.InverseLerp(riseStart, 1f, phase);
            }
            else BindEverydayClip(_requestedEverydayClip);
            if (_feedingRecipient != null)
                _feedingContact = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.3f, .48f, phase)) *
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.75f, .56f, phase)) * weight;
            if (!_everydayGraph.IsValid()) return false;
            if (_action == ActionKind.RaiseToMouth || _everydayDrinking)
            {
                var time = phase * _everydayClip.length;
                _eatingContact = _everydayDrinking
                    ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.44f, .77f, time)) *
                      Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.65f, 1.32f, time)) * weight
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
            _everydayPlayable.SetTime(phase * _everydayClip.length);
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
            if (_shoreHopTick != 0)
            {
                var y = _shoreStartY + _shoreHeight * Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(.27f, .88f, phase));
                var current = _animator.transform.position;
                current.y = Mathf.Lerp(current.y, y, weight);
                var ahead = Vector3.Dot(current - _shoreEdge, _shoreDirection);
                var cross = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.57f, .94f, phase));
                current -= _shoreDirection * (Mathf.Max(0f, ahead) * (1f - cross) * weight);
                _animator.transform.SetPositionAndRotation(current,
                    Quaternion.Slerp(rotation, Quaternion.LookRotation(_shoreDirection), weight));
                FitShoreFeet(phase, weight);
            }
            return true;
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
