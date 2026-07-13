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
    private static readonly int LimpingParam = Animator.StringToHash("Limping");
    private static readonly int JumpUpParam = Animator.StringToHash("JumpUp");
    private static readonly int JumpDownParam = Animator.StringToHash("JumpDown");
    private static readonly int SwimmingParam = Animator.StringToHash("Swimming");
    private string _currentPropId;
    private GameObject _handProp;

    // Spec 33.1 (iter 33): a slung weapon rides on the back when carried but
    // not in the hand — a knife/spear/bow/sword mounted like equipment.
    private GameObject _backProp;
    private string _currentBackId;

    // Spec 40.7: signed thermal comfort (-1 cold .. +1 hot) drives shiver/fan.
    private float _thermal;
    private float _thermalPhase;

    // Spec 40.9/40.1: injury posture (from the snapshot PostureHint) + winded
    // (stamina spent). Drive procedural body language layered on the animation.
    private string _posture = "Upright";
    private bool _winded;
    private float _posturePhase;

    // Spec 40.7: skin weathering — tan browns the skin, sunburn reddens it. The
    // tint rides the base-skin renderers only (captured before clothing, so
    // garments are untouched; covered skin is occluded, so only bare skin
    // shows). Applied via a property block — no material instancing.
    private MaterialPropertyBlock _skinMpb;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

    // Wet-skin sheen: sweat is sold by GLOSS, not色 — the decals are
    // albedo-only, so the actual shine comes from raising the skin's
    // smoothness while hot. 0.32 is the authored dry value on all actors.
    private const float DrySkinSmoothness = 0.32f;
    // 0.85 read as plastic — pulled ~15% down: still a clear wet sheen,
    // but skin, not vinyl.
    private const float WetSkinSmoothness = 0.72f;

    // Spec 35.5: unified inertial skin wetness. Rain soaks fast (fully wet in
    // ~4 s), sweat builds slower (~12 s to its level), and skin dries in
    // ~45 s — same idea as the sim's garment wetness, just quicker (clothes
    // take minutes). TUNING KNOBS.
    private const float RainSoakPerSecond = 0.25f;
    private const float SweatSoakPerSecond = 0.08f;
    private const float SkinDryPerSecond = 0.022f;
    private float _skinWetness;
    private float _clothRainWetness;
    private float _wetnessLastTime;

    // The body is a single SkinnedMeshRenderer with many material submeshes
    // (skin zones + eyes + lashes + …). The weathering tint must touch only the
    // bare-skin submeshes — tinting the eye submeshes turned them white in-game
    // (the portrait never tans, hence its eyes stayed correct). These are the
    // (renderer, materialIndex) pairs that are skin; everything eye/hair/mouth
    // related is excluded.
    private readonly List<(SkinnedMeshRenderer renderer, int index)> _skinTintTargets = new();

    private static readonly string[] NonSkinMaterialHints =
    {
        "cornea", "sclera", "iris", "pupil", "eye", "moist", "socket", "lash",
        "tear", "hair", "tooth", "teeth", "gum", "tongue", "mouth", "nail",
        "lacrimal", "brow"
    };

    // Spec 40.8-D: wounds/bandages are PAINTED into the skin textures (molly
    // bake-raycast placement + stamp records that fade with healing). Flip off
    // to fall back to the decal projectors.
    private const bool PaintWoundsIntoTexture = true;
    // Spec 40.8 v4: sweat/rain as painted WATER DROPLETS — few large drops,
    // each stamped into all three skin channels: dome relief in the normal
    // map, refraction + wet darkening + meniscus rim baked into the albedo,
    // and near-1 smoothness in a painted gloss map (per-pixel — the uniform
    // smoothness bump alone could never make a discrete drop). This is the
    // v3 normal-relief tech un-parked: the pox read came from the DENSE BEAD
    // SPRAY sheet, not the relief itself, so v4 stamps single exaggerated
    // drops and skips the face. (v3's spray sheet stays for future
    // pox/insect-bite visuals — "оставим для болезней или укусов насекомых".)
    private const bool PaintSweatDroplets = true;
    // The v2 decal-projector bubbles ("не идеальные, но пока лучше не
    // получилось") retire while the painted droplets are on — two sweat
    // systems double-coat the skin. The projector RAIN pass stays (streaks
    // on skin + cloth).
    private const bool SweatDropletProjectors = false;
    private SkinTexturePainter _skinPainter;
    private readonly List<(string zone, int seed, float heal)> _woundScratch = new();

    // Spec 40.8-F: a FRESH wound sprays a short RVFX Blood Effects Pack
    // splash from the hit zone (prefabs moved under Resources/HexLive/VFX).
    private static GameObject[] _splashPrefabs;

    // No-domain-reload runs keep statics between plays — a pre-import null
    // load must not stick forever.
    [UnityEngine.RuntimeInitializeOnLoadMethod(
        UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticPrefabCache()
    {
        _splashPrefabs = null;
    }
    private readonly HashSet<int> _seenWoundSeeds = new();
    private bool _woundVfxPrimed;
    private float _lastSplashTime;

    // Spec 40.8/40.6: skin decal layer (wounds/dirt/sweat on bare zones only).
    private SkinDecals _skinDecals;
    private int _npcId;
    private readonly Dictionary<string, float> _zoneHealthScratch = new();
    private readonly HashSet<string> _uncoveredScratch = new();
    private readonly HashSet<string> _bandagedScratch = new();

    // Spec 40.10-C: garment grime + zone-damage rips. Each hurt zone plants a
    // world-space damage sphere at its bone anchor (knees/elbows — where cloth
    // really rips) so the covering garment tears exactly there; dirt follows
    // hygiene. Anchors mirror the SkinDecals zone segments.
    private const int MaxDamageSpheres = 8;
    private const float DamageSphereBite = 0.9f; // zone health below this rips
    private readonly Vector4[] _damageSpheres = new Vector4[MaxDamageSpheres];
    private static readonly Dictionary<string, string> ZoneBoneAnchors = new()
    {
        ["Head"] = "head",
        ["Torso"] = "abdomenUpper",
        ["Pelvis"] = "pelvis",
        ["ArmL"] = "lForearmBend",
        ["ArmR"] = "rForearmBend",
        ["LegL"] = "lShin",
        ["LegR"] = "rShin",
    };

    // Spec 20.16: procedural action motion layered on top of the Animator.
    private enum ActionKind { None, Chop, Work, RaiseToMouth, BowDraw, SpearThrust, Attack }
    private Transform _rShldr;
    private Transform _rForearm;
    private ActionKind _action;
    private float _actionPhase;

    // Iter 28: ledge seat — while sitting at a one-step seam the body is
    // lifted so the butt rests ON the upper step (feet reach the lower one)
    // and tucked slightly back against the edge. TUNING KNOBS:
    //   LedgeSeatLift — vertical lift (step is 0.55 world units high);
    //   LedgeSeatBack — shift toward the step behind the back.
    private const float LedgeSeatLift = 0.40f;
    private const float LedgeSeatBack = 0.12f;
    private bool _ledgeSit;

    // Face life (blink + mood expression) on the body blend shapes.
    private NpcFaceAnimator _face;

    private bool _laying;
    private Transform _layingAttach;
    // Spec 31C.2: world Y of the sleep surface (bed top / ground) passed from
    // the renderer. The LieDown/Sleep/GetUp clips are ground-authored with Y
    // baked into the pose, so the root is pinned to this height directly — no
    // per-frame correction.
    private float _layingSurfaceY;
    private SkinnedMeshRenderer[] _bodySkins;
    private Vector3 _lastPosition;
    private float _lastYaw;
    private float _moveEpsilon = 0.01f;
    private bool _motionSampleValid;

    // Feet match the ground: the walk cycle plays at the body's ACTUAL pace,
    // so a hobbling (mauled legs), soaked, or turning character takes slow
    // weighty steps instead of pattering in place at full cadence.
    // Healthy full speed is 1.0 world units/s ≈ 0.76 body heights/s.
    private const float FullWalkBodyHeightsPerSec = 0.76f;
    private bool _wasWalking;
    private float _animSpeed = 1f;
    // Fast-forward: the sim's speed multiplier scales every clip's playback
    // (walk cadence, sleep, limp — all of it), fed per-sync by the renderer.
    private float _simSpeed = 1f;

    public void SetSimSpeed(float multiplier)
    {
        _simSpeed = Mathf.Max(0.01f, multiplier);
    }

    // Hex-step jump (§21.21B). Timing comes from HexHopTuning — the single
    // source the sim and the view share. Two entry points, one arc engine:
    //  - SetHopSignal: the SIM says "walking the jump path now" (snapshot
    //    HopKind). The body flies a BALLISTIC arc — linear XZ progress toward
    //    the predicted landing point, vertical ease with overshoot (up) or
    //    gravity (down) — and touches down LandingSeconds BEFORE the sim
    //    window closes (§21.21B v3: Takeoff/Landing mark up the clip): the clip's
    //    landing frames play already planted while the sim root slides the
    //    last stretch underneath. Landing is exact by construction: the
    //    planted point IS where the root arrives at window end.
    //  - TriggerHexStepJump: legacy pose-delta path, now only water dive-in /
    //    climb-out (§40.18-B): vertical-only arc over the treading pause, no
    //    lead. Guarded so the root snap at a land-hop's end can't echo one.
    // The clips are imported "_noy" (authored root height stripped), so this
    // arc is the ONLY root-level motion.
    // The up-arc apex rises this fraction ABOVE the target ledge before the
    // body drops onto it (0.3 => ~0.17 wu over a 0.55 step).
    private const float JumpUpOvershoot = 1.3f;
    private string _hopKind = string.Empty;
    // §21.21B v7: the VIEW is vertical-only. The sim already moves the root's
    // XZ perfectly (gather → straight takeoff→landing), so the body follows
    // the root's XZ exactly and adds ONLY a Y arc that smooths the ground-
    // level snap at the tile crossing into a jump. No XZ prediction, no
    // velocity extrapolation, no settle phase — those double-computed the XZ
    // the sim already owns and left the residual offset the user saw.
    private float _jumpStartY;         // root world-Y at arc start (stand level)
    private bool _jumpPlunge;          // dive into water: splash below, bob up
    private float _jumpHeightDelta;    // world units, signed (+ = up)
    private float _jumpTakeoffFrac;    // [0..this] = crouch beat (arc flat)
    private float _jumpFlightEndFrac;  // [this..1] = landing beat (arc = 1)
    private float _jumpDuration;
    private float _jumpTimer;
    private float _jumpRetriggerGuard; // swallows the pose-delta echo at hop end
    private bool _jumpUp;

    // §40.18-B: deep-water locomotion — the renderer flags the tile. While
    // swimming the animator runs TreadWater (still) / Swim (moving); the
    // procedural arm layers (actions, thermal, posture) stand down so they
    // don't fight the stroke.
    // ONE shared body-height lift for swimming (world units, + = up) — the
    // whole body sits this far off the root while in water, same for tread and
    // stroke. Live-tunable so the swim test can dial it in.
    public static float SwimBodyLift = 0.45f;
    private bool _swimming;

    public void SetSwimming(bool swimming)
    {
        if (_swimming == swimming)
        {
            return;
        }

        _swimming = swimming;
        if (_animator != null)
        {
            _animator.SetBool(SwimmingParam, swimming);
        }
    }

    // §21.21B: sim hop signal ("Up"/"Down"/""), fed every sync. Starts the
    // ballistic arc on the rising edge. heightDeltaWorld is the EXACT signed
    // root-level difference (renderer-computed; water dives include the swim
    // sink depth).
    public void SetHopSignal(string hopKind, float heightDeltaWorld, bool intoWater = false)
    {
        hopKind ??= string.Empty;
        if (hopKind == _hopKind)
        {
            return;
        }

        _hopKind = hopKind;
        if (hopKind.Length == 0)
        {
            return;
        }

        // §21.21B v3: one clock for everything — the body flies exactly the
        // sim's airborne window: [Takeoff .. HopSeconds - Landing]. During
        // the takeoff beat the root stands (offset 0 by construction);
        // during the landing beat the root has stopped at the landing point
        // and the clip plants the feet.
        var up = hopKind == "Up";
        _jumpPlunge = intoWater && !up;
        var hop = HexLive.Simulation.Navigation.HexHopTuning.HopSeconds;
        StartJumpArc(
            up,
            heightDeltaWorld,
            hop,
            HexLive.Simulation.Navigation.HexHopTuning.TakeoffSeconds / hop,
            (hop - HexLive.Simulation.Navigation.HexHopTuning.LandingSeconds) / hop);
    }

    // Legacy pose-delta path — water dive-in / climb-out only.
    public void TriggerHexStepJump(float heightDeltaWorld)
    {
        // A land hop in ANY phase (flight or the grace window after release)
        // owns the body — the root's tile-switch snap must not echo a second
        // arc.
        if (_jumpTimer > 0f || _jumpRetriggerGuard > 0f)
        {
            return;
        }

        _jumpPlunge = false;
        // Water dives/climb-outs have no sim hop window — half the hop clock
        // covers the treading pause they play over; no takeoff/landing beats.
        StartJumpArc(
            heightDeltaWorld > 0f,
            heightDeltaWorld,
            HexLive.Simulation.Navigation.HexHopTuning.HopSeconds * 0.5f,
            0f, 1f);
    }

    private void StartJumpArc(
        bool up, float heightDelta, float durationSimSeconds,
        float takeoffFrac, float flightEndFrac)
    {
        if (_laying || _dead || _animator == null)
        {
            return;
        }

        _jumpUp = up;
        _jumpHeightDelta = heightDelta;
        _jumpStartY = transform.position.y;
        _jumpDuration = Mathf.Max(0.05f, durationSimSeconds);
        _jumpTakeoffFrac = Mathf.Clamp01(takeoffFrac);
        _jumpFlightEndFrac = Mathf.Clamp(flightEndFrac, _jumpTakeoffFrac + 0.05f, 1f);
        _jumpTimer = _jumpDuration;
        _animator.ResetTrigger(_jumpUp ? JumpDownParam : JumpUpParam);
        _animator.SetTrigger(_jumpUp ? JumpUpParam : JumpDownParam);
    }

    // Vertical arc height 0..1 over the FLIGHT fraction tf (0 at takeoff-end,
    // 1 at flight-end). > 1 = above the target ledge on the way up.
    private float JumpVerticalEase(float tf)
    {
        if (_jumpUp)
        {
            // Launch fast, fly PAST the ledge height, drop onto it.
            return tf < 0.5f
                ? Mathf.SmoothStep(0f, JumpUpOvershoot, Mathf.InverseLerp(0f, 0.5f, tf))
                : Mathf.Lerp(JumpUpOvershoot, 1f,
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1f, tf)));
        }

        // Hold through the step-off, then gravity (quadratic) to touchdown.
        var ease = Mathf.Pow(Mathf.Clamp01(Mathf.InverseLerp(0.1f, 1f, tf)), 2f);

        // §40.18-B splash: a dive doesn't hover-stop at the waterline — it
        // sails DivePlungeDepth UNDER the swim level and bobs back up to it
        // (ease > 1 = below the target; back to exactly 1 by the end).
        if (_jumpPlunge && _jumpHeightDelta < -0.01f)
        {
            var bob = Mathf.Sin(Mathf.Clamp01(Mathf.InverseLerp(0.62f, 1f, tf)) * Mathf.PI);
            ease += bob * (HexLive.Simulation.Navigation.HexHopTuning.DivePlungeDepth /
                Mathf.Abs(_jumpHeightDelta));
        }

        return ease;
    }

    // Advances the jump window and returns the world-space offset the body
    // holds relative to the sim root this frame. VERTICAL ONLY (§21.21B v7):
    // the sim owns the root's XZ (gather → straight flight → landing); the
    // body follows it exactly and adds a Y arc that turns the tile-crossing
    // ground snap into a jump. Self-correcting: it reads the live root Y each
    // frame, so a late tile switch just holds the body at arc height until
    // the snap arrives — no dip, no snap-back, no settle machinery.
    private Vector3 JumpOffsetWorld()
    {
        if (_jumpRetriggerGuard > 0f)
        {
            _jumpRetriggerGuard -= Time.deltaTime * _simSpeed;
        }

        if (_jumpTimer <= 0f)
        {
            return Vector3.zero;
        }

        _jumpTimer -= Time.deltaTime * _simSpeed;
        if (_jumpTimer <= 0f)
        {
            _jumpRetriggerGuard = 0.5f;
            return Vector3.zero;
        }

        // t over the whole window; map to the FLIGHT fraction tf so the arc
        // is flat during the takeoff beat and pinned at 1 during landing.
        var t = 1f - Mathf.Clamp01(_jumpTimer / Mathf.Max(0.0001f, _jumpDuration));
        float arc; // 0 at stand level, 1 at target level (>1 = overshoot above)
        if (t <= _jumpTakeoffFrac)
        {
            arc = 0f;
        }
        else if (t >= _jumpFlightEndFrac)
        {
            arc = 1f;
        }
        else
        {
            var tf = Mathf.InverseLerp(_jumpTakeoffFrac, _jumpFlightEndFrac, t);
            arc = JumpVerticalEase(tf);
        }

        // Desired body world-Y minus the actual (live) root Y = the offset.
        var desiredY = _jumpStartY + _jumpHeightDelta * arc;
        return new Vector3(0f, desiredY - transform.position.y, 0f);
    }

    // Face anchor rig for the portrait camera, calibrated once in the prefab's
    // upright rest pose: face center, face-forward and face-up are captured in
    // HEAD-BONE space, so at runtime they ride the bone through any pose —
    // walking, sitting, lying flat — and the camera stays nailed to the face.
    private Transform _headBone;
    private Vector3 _faceLocalCenter;
    private Vector3 _faceLocalForward;
    private Vector3 _faceLocalUp;
    private bool _faceCalibrated;

    public bool TryGetFace(out Vector3 faceCenter, out Vector3 faceForward, out Vector3 faceUp, out float scale)
    {
        scale = _bodyRoot != null ? _bodyRoot.lossyScale.y : transform.lossyScale.y;

        if (_faceCalibrated && _headBone != null)
        {
            faceCenter = _headBone.TransformPoint(_faceLocalCenter);
            faceForward = _headBone.TransformDirection(_faceLocalForward).normalized;
            faceUp = _headBone.TransformDirection(_faceLocalUp).normalized;
            return true;
        }

        faceCenter = transform.position + Vector3.up * (1.55f * scale);
        faceForward = transform.forward;
        faceUp = Vector3.up;
        return true;
    }

    // Orbit-camera pivot: the visual center of the body in ANY pose — a point
    // between the head and the hip bones, biased toward the head so the face
    // keeps priority. Standing it sits at the chest; lying it follows the body
    // down to the ground, so the camera stays centered on the character.
    public bool TryGetBodyCenter(out Vector3 center)
    {
        var hip = _bodyBones != null
            ? (_bodyBones.GetBone("hip") ?? _bodyBones.GetBone("pelvis"))
            : null;

        if (_headBone != null && hip != null)
        {
            center = Vector3.Lerp(hip.position, _headBone.position, 0.6f);
            return true;
        }

        var scale = _bodyRoot != null ? _bodyRoot.lossyScale.y : transform.lossyScale.y;
        center = transform.position + Vector3.up * (0.9f * scale);
        return true;
    }

    private void CalibrateFaceAnchor()
    {
        _headBone = _bodyBones != null ? _bodyBones.GetBone("head") : null;
        if (_headBone == null)
        {
            return;
        }

        // Rest pose: the root faces transform.forward and stands upright, so
        // world directions map cleanly into head-bone space.
        var scale = _bodyRoot != null ? _bodyRoot.lossyScale.y : _headBone.lossyScale.y;
        _faceLocalForward = _headBone.InverseTransformDirection(transform.forward);
        _faceLocalUp = _headBone.InverseTransformDirection(transform.up);
        // The head bone pivots at the neck — the face center sits above it
        // (0.07 m on the 1.7 m source model, scaled to world size).
        _faceLocalCenter = _headBone.InverseTransformPoint(
            _headBone.position + transform.up * (0.07f * scale));
        _faceCalibrated = true;
    }

    public void Construct(string actorMeshName, int npcId = 0)
    {
        _npcId = npcId;
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
            // Right-arm bones for procedural action motion (all three actors
            // share the Genesis naming). Rotated in world space around the
            // body's right axis, so their local orientation doesn't matter.
            _rShldr = _bodyBones.GetBone("rShldrBend");
            _rForearm = _bodyBones.GetBone("rForearmBend");

            // Spec 40.8/40.6: bone-riding skin decals (wounds/dirt/sweat).
            _skinDecals = gameObject.AddComponent<SkinDecals>();
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
            // Underside reference for sleep-planting (worn garments hug the body).
            _bodySkins = _bodyRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            BuildSkinTintTargets();
            // NOTE: an experiment swapping the SKIN to the GarmentTear paint
            // shader was reverted — Cull Off + the AlphaTest queue flickered on
            // the skinned body and the Daz skin lost its depth (looked flat
            // white). Skin stays on URP Lit; wounds/bandages AND water
            // droplets (40.8 v4) paint INTO the skin textures
            // (SkinTexturePainter) — droplet water shading is baked at stamp
            // time precisely so no custom skin shader is needed.
            if (PaintWoundsIntoTexture && _bodyBones != null)
            {
                SkinnedMeshRenderer bodyRenderer = null;
                var slotScratch = new List<int>();
                foreach (var (renderer, index) in _skinTintTargets)
                {
                    if (renderer == null)
                    {
                        continue;
                    }

                    if (bodyRenderer == null)
                    {
                        bodyRenderer = renderer;
                    }

                    if (ReferenceEquals(renderer, bodyRenderer))
                    {
                        slotScratch.Add(index);
                    }
                }

                if (bodyRenderer != null)
                {
                    _skinPainter = gameObject.AddComponent<SkinTexturePainter>();
                    _skinPainter.Construct(bodyRenderer, slotScratch, _bodyBones,
                        _bodyRoot != null ? _bodyRoot : transform, _npcId);
                }
            }

            // Face life: blinking + mood-driven expression on the blend shapes.
            _face = gameObject.AddComponent<NpcFaceAnimator>();
            _face.Construct(_bodySkins);
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

        // Decals need the (possibly scaled) body root — wire after it's known.
        if (_skinDecals != null && _bodyBones != null)
        {
            // Distinct bare-skin renderers (from the tint classification) opt
            // into the skin rendering layer so projectors hit only them.
            var skinRenderers = new List<SkinnedMeshRenderer>();
            foreach (var (renderer, _) in _skinTintTargets)
            {
                if (renderer != null && !skinRenderers.Contains(renderer))
                {
                    skinRenderers.Add(renderer);
                }
            }

            _skinDecals.Construct(_bodyBones, _bodyRoot != null ? _bodyRoot : transform, _npcId, skinRenderers);
        }

        // After _bodyRoot is known and the rig still stands in its rest pose.
        CalibrateFaceAnchor();

        _gazeProxy = new GameObject("GazeTarget").transform;
        _gazeProxy.SetParent(transform.parent, false);
    }

    // The whole actor hierarchy lives on the "Actors" layer so the portrait
    // camera can render the character (clothes, props, decals — anything that
    // spawned under her) with zero environment. Re-applied every tick because
    // garments/props/decals spawn at runtime with the default layer.
    private static int _actorsLayer = -1;

    public void EnsureActorLayer()
    {
        // Keep retrying while unresolved: the layer table may load AFTER the
        // domain (TagManager edited externally) — a cached -1 would otherwise
        // disable portrait isolation for the whole session.
        if (_actorsLayer < 0)
        {
            _actorsLayer = LayerMask.NameToLayer("Actors");
            if (_actorsLayer < 0)
            {
                return;
            }
        }

        ApplyLayerRecursive(transform, _actorsLayer);
    }

    private static void ApplyLayerRecursive(Transform node, int layer)
    {
        if (node.gameObject.layer != layer)
        {
            node.gameObject.layer = layer;
        }

        for (var i = 0; i < node.childCount; i++)
        {
            ApplyLayerRecursive(node.GetChild(i), layer);
        }
    }

    // Debug: strip the visuals only (sim wardrobe untouched) so skin effects —
    // tan, sunburn, wounds, dust, sweat — can be inspected on the full body.
    // While hidden, re-hide every tick (SyncWorn may equip new garments); when
    // visible, do NOTHING per-tick — a blanket Show() every frame was undoing
    // the equip logic's hide-underwear-beneath-outerwear bookkeeping (bras
    // popped through tops). Restoring runs once, on the actual toggle.
    private bool _clothingHidden;

    public void SetClothingHidden(bool hidden)
    {
        if (hidden)
        {
            _clothingHidden = true;
            _bodyBones?.SetAllWearsVisible(false);
            return;
        }

        if (!_clothingHidden)
        {
            return; // already visible — leave the layered visibility alone
        }

        _clothingHidden = false;
        _bodyBones?.SetAllWearsVisible(true);
    }

    // Spec 40.8/40.6: forward the tick's body condition to the decal layer.
    // bodyParts entries are "Zone=0.85" strings straight from the snapshot.
    public void SetBodyCondition(IReadOnlyList<string> bodyParts,
        IReadOnlyList<string> uncoveredParts, float hygiene, float thermal,
        float rainWet = 0f, IReadOnlyList<string> wornWetness = null,
        IReadOnlyList<string> wounds = null, IReadOnlyList<string> bandagedZones = null)
    {
        if (_skinDecals == null)
        {
            return;
        }

        _zoneHealthScratch.Clear();
        foreach (var entry in bodyParts)
        {
            var eq = entry.IndexOf('=');
            if (eq > 0 && float.TryParse(entry.Substring(eq + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                _zoneHealthScratch[entry.Substring(0, eq)] = value;
            }
        }

        _uncoveredScratch.Clear();
        foreach (var part in uncoveredParts)
        {
            _uncoveredScratch.Add(part);
        }

        // Rain wetness is inertial: the renderer feeds a binary "in the rain
        // right now" flag (it flips on tile/indoor boundaries), but a body
        // SOAKS in seconds and DRIES in tens of seconds. Integrating here
        // kills the wet/dry flicker (Marta walking past the house) and keeps
        // her visibly wet after stepping under a roof or when the rain stops.
        var now = Time.time;
        var dt = _wetnessLastTime > 0f ? Mathf.Max(0f, now - _wetnessLastTime) : 0f;
        _wetnessLastTime = now;

        // Unified skin-wetness pool (clothes-like drying, a touch faster):
        // rain fills it toward 1 fast; sweat fills it toward the current
        // sweat level slower; it always DRAINS gradually — rain stopping
        // leaves her glistening for ~a minute, cooling down doesn't
        // instantly dry the sweat, and while she stays hot the wetness
        // never drains below her sweat level.
        var sweatLevel = Mathf.Clamp01(thermal / 0.6f);
        var wetTarget = Mathf.Max(rainWet > 0.5f ? 1f : 0f, sweatLevel);
        if (wetTarget > _skinWetness)
        {
            var rise = rainWet > 0.5f ? RainSoakPerSecond : SweatSoakPerSecond;
            _skinWetness = Mathf.Min(wetTarget, _skinWetness + dt * rise);
        }
        else
        {
            _skinWetness = Mathf.Max(wetTarget, _skinWetness - dt * SkinDryPerSecond);
        }

        // Cloth rain sheen is inertial too (rain-only — sweat doesn't soak the
        // shirt): fabric visibly darkens within seconds of standing in rain.
        if (rainWet > 0.5f)
        {
            _clothRainWetness = Mathf.Min(1f, _clothRainWetness + dt * RainSoakPerSecond);
        }
        else
        {
            _clothRainWetness = Mathf.Max(0f, _clothRainWetness - dt * SkinDryPerSecond);
        }

        // Spec 35.5: each worn garment shows max(sim wetness, quick rain
        // sheen). The sim value is the slow gameplay truth (soaks over game
        // minutes, dries at fire/rack — so she STAYS wet after the storm);
        // the inertial part makes cloth react to rain as fast as the skin.
        if (_bodyBones != null && wornWetness != null)
        {
            foreach (var entry in wornWetness)
            {
                var tab = entry.IndexOf('\t');
                if (tab > 0 && float.TryParse(entry.Substring(tab + 1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var wetValue))
                {
                    _bodyBones.SetWearWetness(entry.Substring(0, tab),
                        Mathf.Max(wetValue, _clothRainWetness));
                }
            }
        }

        // Spec 44: bandaged zones show the leaf wrap instead of wound marks.
        _bandagedScratch.Clear();
        if (bandagedZones != null)
        {
            foreach (var zone in bandagedZones)
            {
                _bandagedScratch.Add(zone);
            }
        }

        // Spec 40.8-D: wounds/bandages paint into the skin textures; the decal
        // projectors then skip them (dirt/sweat/rain stay projector-based).
        if (PaintWoundsIntoTexture && _skinPainter != null)
        {
            _woundScratch.Clear();
            if (wounds != null)
            {
                foreach (var entry in wounds)
                {
                    var parts = entry.Split('|');
                    if (parts.Length >= 3 && int.TryParse(parts[1], out var woundSeed) &&
                        float.TryParse(parts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var woundHeal))
                    {
                        _woundScratch.Add((parts[0], woundSeed, woundHeal));
                    }
                }
            }

            SyncWoundSplashVfx();
            // The painter needs the current wet-skin gloss: it is the BASE of
            // the painted gloss map, so droplet pixels (0.95) sit on top of
            // the same sheen the rest of the body shows.
            var wetSmoothnessForPaint = Mathf.Lerp(DrySkinSmoothness, WetSkinSmoothness, _skinWetness);
            _skinPainter.Sync(_woundScratch, _bandagedScratch,
                PaintSweatDroplets ? _skinWetness : 0f, _uncoveredScratch, wetSmoothnessForPaint);
            // Projector sweat is retired (v4 paints droplets instead); the
            // projector "rain" pass keys off the RAIN-only inertial wetness —
            // the unified pool made sweat spawn whitish rain rings.
            _skinDecals.Sync(null, _uncoveredScratch, hygiene,
                SweatDropletProjectors ? thermal : 0f, _clothRainWetness, null);
        }
        else
        {
            _skinDecals.Sync(wounds, _uncoveredScratch, hygiene, thermal, _skinWetness, _bandagedScratch);
        }

        // Wet sheen: hot skin glistens — and rain-soaked skin the same way
        // (spec 35.5: rain reuses the sweat tech). The droplet decals are
        // albedo-only, so the visible wetness is the skin's smoothness
        // climbing toward a wet gloss — sunlight then pings off the body.
        // The gloss reads straight from the unified wetness pool — rain and
        // sweat both feed it, so whichever is stronger wins naturally.
        _skinMpb ??= new MaterialPropertyBlock();
        var sweat01 = _skinWetness;
        var smoothness = Mathf.Lerp(DrySkinSmoothness, WetSkinSmoothness, sweat01);
        foreach (var (renderer, index) in _skinTintTargets)
        {
            if (renderer == null)
            {
                continue;
            }

            // Spec 40.8 v4: slots carrying the painted gloss map hold their
            // per-pixel ABSOLUTE smoothness in the map alpha — URP Lit
            // multiplies it by this scalar, so the scalar must be 1 there.
            // Missing one slot here is the "whole body vinyl" failure mode
            // (the 0.85-plastic scar): everywhere else keeps the wetness lerp.
            var glossMapped = _skinPainter != null &&
                              ReferenceEquals(renderer, _skinPainter.Body) &&
                              _skinPainter.SlotHasGlossMap(index);
            renderer.GetPropertyBlock(_skinMpb, index);
            _skinMpb.SetFloat(SmoothnessId, glossMapped ? 1f : smoothness);
            renderer.SetPropertyBlock(_skinMpb, index);
        }

        // Spec 40.10-C: garments rip at hurt zones and soil as hygiene drops.
        // Spheres are rebuilt every sync so they ride the animated bones.
        if (_bodyBones != null)
        {
            var sphereCount = 0;
            foreach (var pair in _zoneHealthScratch)
            {
                if (pair.Value >= DamageSphereBite || sphereCount >= MaxDamageSpheres ||
                    !ZoneBoneAnchors.TryGetValue(pair.Key, out var boneName))
                {
                    continue;
                }

                var bone = _bodyBones.GetBone(boneName);
                if (bone == null)
                {
                    continue;
                }

                var position = bone.position;
                _damageSpheres[sphereCount++] = new Vector4(
                    position.x, position.y, position.z, Mathf.Clamp01(1f - pair.Value));
            }

            // Spec 40.8-C: blood soaks the covering cloth over FRESH wounds —
            // intensity from the unhealed hostage across all wound records
            // (fades as they close). Sweat damp = the thermal sweat drive.
            var bloodSoak = 0f;
            if (wounds != null)
            {
                foreach (var entry in wounds)
                {
                    var sep = entry.LastIndexOf('|');
                    if (sep > 0 && float.TryParse(entry.Substring(sep + 1),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var heal))
                    {
                        bloodSoak += 1f - heal;
                    }
                }
            }

            _bodyBones.SetWearGrime(Mathf.Clamp01(1f - hygiene), _damageSpheres, sphereCount,
                Mathf.Clamp01(bloodSoak * 0.7f), Mathf.Clamp01(thermal / 0.6f));
        }
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
    public void SetLaying(bool laying, Transform attachPoint, float surfaceY = 0f)
    {
        _laying = laying;
        _layingAttach = attachPoint;
        _layingSurfaceY = surfaceY;
        // Spec 31C.8: lying poses stretch outside the authored skin bounds and
        // get frustum-culled; per-frame bounds while sleeping keep her visible.
        if (_bodySkins != null)
        {
            foreach (var skin in _bodySkins)
            {
                if (skin != null)
                {
                    skin.updateWhenOffscreen = laying;
                }
            }
        }

        if (_animator != null)
        {
            _animator.SetBool(LayingParam, laying);
        }

        if (_face != null)
        {
            _face.SetSleeping(laying);
        }
    }

    // Face mood: aggregate wellbeing (0..1) + combat flag from the snapshot.
    public void SetFaceMood(float wellbeing, bool fighting)
    {
        if (_face != null)
        {
            _face.SetMood(wellbeing, fighting);
        }
    }

    // Spec 40.13: a physics ragdoll for faint / collapse-from-exhaustion. The
    // 11-bone rig (verified: the skeleton flops limp to the ground) is built
    // lazily on the Daz Genesis3 skeleton and toggled kinematic. Active =>
    // animator off + bodies dynamic (limp); inactive => bodies kinematic +
    // animator re-drives the pose (stands back up on wake).
    private Rigidbody[] _ragdollBodies;
    private bool _ragdollActive;

    private static readonly (string bone, string child)[] RagdollLimbs =
    {
        ("lShldrBend", "lForearmBend"), ("lForearmBend", "lHand"),
        ("rShldrBend", "rForearmBend"), ("rForearmBend", "rHand"),
        ("lThighBend", "lShin"), ("lShin", "lFoot"),
        ("rThighBend", "rShin"), ("rShin", "rFoot"),
    };
    private static readonly (string child, string parent)[] RagdollJoints =
    {
        ("chestUpper", "hip"), ("head", "chestUpper"),
        ("lShldrBend", "chestUpper"), ("lForearmBend", "lShldrBend"),
        ("rShldrBend", "chestUpper"), ("rForearmBend", "rShldrBend"),
        ("lThighBend", "hip"), ("lShin", "lThighBend"),
        ("rThighBend", "hip"), ("rShin", "rThighBend"),
    };
    private static readonly string[] RagdollBones =
    {
        "hip", "chestUpper", "head", "lShldrBend", "lForearmBend",
        "rShldrBend", "rForearmBend", "lThighBend", "lShin", "rThighBend", "rShin",
    };

    private void BuildRagdollIfNeeded()
    {
        if (_ragdollBodies != null || _bodyBones == null)
        {
            return;
        }

        var childFor = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var (bone, child) in RagdollLimbs)
        {
            childFor[bone] = child;
        }

        var bodies = new System.Collections.Generic.List<Rigidbody>();
        foreach (var name in RagdollBones)
        {
            var t = _bodyBones.GetBone(name);
            if (t == null)
            {
                continue;
            }

            var go = t.gameObject;
            // Unity-aware null (a destroyed component is a "fake null" that the
            // ?? operator would wrongly keep) — GetComponent, then add if absent.
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = go.AddComponent<Rigidbody>();
            }
            rb.mass = (name == "hip" || name == "chestUpper") ? 3f : 1f;
            rb.useGravity = true;
            rb.isKinematic = true; // animation-driven until faint
            bodies.Add(rb);

            if (go.GetComponent<Collider>() == null)
            {
                if (name == "head")
                {
                    go.AddComponent<SphereCollider>().radius = 0.09f;
                }
                else if (childFor.TryGetValue(name, out var childName))
                {
                    var ch = _bodyBones.GetBone(childName);
                    var len = ch != null ? Vector3.Distance(t.position, ch.position) : 0.2f;
                    var c = go.AddComponent<CapsuleCollider>();
                    c.height = Mathf.Max(len, 0.06f);
                    c.radius = Mathf.Max(len * 0.22f, 0.03f);
                    c.direction = 0;
                    var mid = ch != null ? (t.position + ch.position) * 0.5f : t.position;
                    c.center = t.InverseTransformPoint(mid);
                }
                else
                {
                    go.AddComponent<BoxCollider>().size = new Vector3(0.16f, 0.20f, 0.13f);
                }
            }
        }

        foreach (var (child, parent) in RagdollJoints)
        {
            var ct = _bodyBones.GetBone(child);
            var pt = _bodyBones.GetBone(parent);
            if (ct == null || pt == null || ct.GetComponent<CharacterJoint>() != null)
            {
                continue;
            }

            var pb = pt.GetComponent<Rigidbody>();
            if (pb == null)
            {
                continue;
            }

            var j = ct.gameObject.AddComponent<CharacterJoint>();
            j.connectedBody = pb;
            j.swing1Limit = new SoftJointLimit { limit = 40f };
            j.swing2Limit = new SoftJointLimit { limit = 40f };
            j.lowTwistLimit = new SoftJointLimit { limit = -25f };
            j.highTwistLimit = new SoftJointLimit { limit = 25f };
            j.enableProjection = true;
        }

        _ragdollBodies = bodies.ToArray();
    }

    // Spec 40.13 v2: death — the body lies down (the normal LieDown → Sleep
    // flow) and then freezes in one pose; a corpse doesn't breathe the sleep
    // loop. One-way: corpse views are destroyed, never revived.
    private bool _dead;

    public void SetDead(float surfaceY)
    {
        _dead = true;
        SetLaying(true, null, surfaceY);
    }

    // Spec 40.13: collapse (faint) => go limp; wake => animator takes over.
    public void SetRagdoll(bool active)
    {
        if (active == _ragdollActive && _ragdollBodies != null)
        {
            return;
        }

        BuildRagdollIfNeeded();
        if (_ragdollBodies == null || _ragdollBodies.Length == 0)
        {
            return; // no skeleton — fall back to the baked laying clip
        }

        _ragdollActive = active;
        if (_animator != null)
        {
            _animator.enabled = !active;
        }

        // A limp body neither tracks gazes nor gets posed — LookAtIK writes
        // to the head bone in LateUpdate and would fight the physics.
        if (_lookAtIK != null)
        {
            _lookAtIK.enabled = !active;
        }

        foreach (var rb in _ragdollBodies)
        {
            if (rb != null)
            {
                rb.isKinematic = !active;
                if (!active)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
        }
    }

    // Spec 31C.6: interaction poses — crouch while gathering/working, sit
    // on Sit, and hold the relevant item in the right hand.
    public void SetInteraction(string interaction, string heldItemId)
    {
        if (_animator != null)
        {
            var working = interaction is "PickUp" or "Harvest" or "Build" or "Craft"
                or "Fuel" or "Bury" or "Hang" or "FillBottle";
            _animator.SetBool(WorkingParam, working);
            _animator.SetBool(SittingParam, interaction == "Sit");
        }

        _action = ActionFromInteraction(interaction, heldItemId);
        SetHandProp(heldItemId);
    }

    // Iter 28: the renderer flags a ledge sit (sim IsLedgeSit) so LateUpdate
    // lifts the body onto the upper step instead of pinning it to the root.
    public void SetLedgeSit(bool ledgeSit)
    {
        _ledgeSit = ledgeSit;
    }

    private static ActionKind ActionFromInteraction(string interaction, string heldItemId)
    {
        switch (interaction)
        {
            case "Harvest":
                var chopping = heldItemId == "tool.axe_stone" || heldItemId == "tool.pickaxe_stone";
                return chopping ? ActionKind.Chop : ActionKind.Work;
            case "PickUp":
            case "Build":
            case "Craft":
            case "Fuel":
            case "Bury":
            case "Hang":
            case "FillBottle": // spec 29H: crouch and scoop water
                return ActionKind.Work;
            case "Eat":
            case "Drink":
                return ActionKind.RaiseToMouth;
            default:
                return ActionKind.None;
        }
    }

    // Spec 20.16: combat/hunt overrides the idle interaction — the weapon
    // appears in hand and drives a draw (bow) or thrust (spear) motion.
    public void SetCombat(bool fighting, string weaponId)
    {
        if (!fighting)
        {
            return;
        }

        _action = weaponId == "tool.bow" ? ActionKind.BowDraw
            : weaponId == "tool.spear" ? ActionKind.SpearThrust
            : ActionKind.Attack;

        if (!string.IsNullOrEmpty(weaponId))
        {
            SetHandProp(weaponId);
        }
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
        if (hand == null)
        {
            return;
        }

        // Real prefab first; otherwise a procedural low-poly model so tools
        // are visible in hand (spec 20.16 — no prefab wiring required).
        var model = Resources.Load<GameObject>($"HexLive/Objects/{itemId}");
        if (model != null)
        {
            _handProp = Instantiate(model, hand);
        }
        else
        {
            var built = HexLive.UnityPresentation.Environment.LowPolyToolFactory.Build(itemId);
            if (built == null)
            {
                return;
            }

            built.transform.SetParent(hand, false);
            _handProp = built;
        }

        _handProp.name = $"HandProp {itemId}";

        // Hand-tuned placement for specific props (baked from the editor) wins
        // over the automatic palm-fit — exact position/rotation/scale in the
        // rHand's local space.
        if (TryGetHandPropTransform(itemId, out var tunedPos, out var tunedRot, out var tunedScale))
        {
            _handProp.transform.localPosition = tunedPos;
            _handProp.transform.localRotation = tunedRot;
            _handProp.transform.localScale = tunedScale;
            return;
        }

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

    // Per-prop hand placement, tuned in the editor and baked here. Values are
    // in the rHand bone's local space.
    private static bool TryGetHandPropTransform(string itemId, out Vector3 localPosition,
        out Quaternion localRotation, out Vector3 localScale)
    {
        switch (itemId)
        {
            case "tool.bottle":
                localPosition = new Vector3(0.0543f, -0.0236f, -0.051f);
                localRotation = Quaternion.Euler(91.974f, 0.001007f, -6.520996f);
                localScale = new Vector3(0.349494f, 0.349494f, 0.349494f);
                return true;
            case "food.coconut":
                localPosition = new Vector3(0.061f, -0.142f, 0.001f);
                localRotation = Quaternion.Euler(0.808f, 0f, 0f);
                localScale = new Vector3(0.7718072f, 0.9210232f, 0.7718072f);
                return true;
            case "tool.spear":
                localPosition = new Vector3(0.058f, -0.025f, -0.078f);
                localRotation = Quaternion.Euler(-1.544f, -263.963f, 90.255f);
                localScale = new Vector3(0.405947f, 0.405947f, 0.405947f);
                return true;
            default:
                localPosition = Vector3.zero;
                localRotation = Quaternion.identity;
                localScale = Vector3.one;
                return false;
        }
    }

    // Spec 33.1: mount a carried weapon on the upper back (slung diagonally),
    // hidden while it is in the hand (fighting/hunting) so it isn't doubled.
    public void SetBackWeapon(string itemId)
    {
        // Don't sling what's already in the hand.
        if (!string.IsNullOrEmpty(_currentPropId) && itemId == _currentPropId)
        {
            itemId = null;
        }

        if (_currentBackId == itemId)
        {
            return;
        }

        _currentBackId = itemId;
        if (_backProp != null)
        {
            Destroy(_backProp);
            _backProp = null;
        }

        if (string.IsNullOrEmpty(itemId) || _bodyBones == null)
        {
            return;
        }

        // Genesis3 upper-spine bone; fall back through the chain if renamed.
        var back = _bodyBones.GetBone("chestUpper") ?? _bodyBones.GetBone("chestLower")
            ?? _bodyBones.GetBone("spine2") ?? _bodyBones.GetBone("abdomenUpper");
        if (back == null)
        {
            return;
        }

        var model = Resources.Load<GameObject>($"HexLive/Objects/{itemId}");
        _backProp = model != null
            ? Instantiate(model, back)
            : HexLive.UnityPresentation.Environment.LowPolyToolFactory.Build(itemId);
        if (_backProp == null)
        {
            return;
        }

        if (_backProp.transform.parent != back)
        {
            _backProp.transform.SetParent(back, false);
        }

        _backProp.name = $"BackProp {itemId}";

        var renderers = _backProp.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            var biggest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            var target = 1.7f * _bodyRoot.lossyScale.y * 0.5f; // weapon-length
            if (biggest > 0.0001f)
            {
                _backProp.transform.localScale *= target / biggest;
            }
        }

        // Sit it behind the shoulders, slung on a diagonal (hand-tuned offset).
        _backProp.transform.localPosition = new Vector3(0.105f, -0.413f, -0.078f);
        _backProp.transform.localRotation = Quaternion.Euler(-3.335f, -0.358f, 18.524f);
    }

    // Spec 40.7: how hot/cold the NPC feels (-1..+1); the renderer feeds it.
    public void SetThermal(float signedComfort)
    {
        _thermal = signedComfort;
    }

    // Cold (< -0.4): a fast fine tremble of the whole upper body, arms drawn
    // in. Hot (> 0.4): the right hand rises and fans by the face. Layered on
    // top of the animated pose; skipped while lying down.
    private void ApplyThermalPose()
    {
        if (_laying || _swimming || _bodyRoot == null)
        {
            return;
        }

        _thermalPhase += Time.deltaTime * _simSpeed;

        if (_thermal < -0.4f && _rShldr != null)
        {
            var intensity = Mathf.InverseLerp(-0.4f, -1f, _thermal); // 0..1
            var tremble = Mathf.Sin(_thermalPhase * 38f) * 3.5f * intensity;
            var hunch = 22f * intensity; // arms hug in
            var right = _bodyRoot.right;
            _rShldr.rotation = Quaternion.AngleAxis(-hunch + tremble, right) * _rShldr.rotation;
            if (_rForearm != null)
            {
                _rForearm.rotation = Quaternion.AngleAxis(-40f * intensity, right) * _rForearm.rotation;
            }
        }
        else if (_thermal > 0.4f && _rShldr != null)
        {
            var intensity = Mathf.InverseLerp(0.4f, 1f, _thermal);
            var wave = Mathf.Sin(_thermalPhase * 7f) * 20f * intensity;
            var right = _bodyRoot.right;
            _rShldr.rotation = Quaternion.AngleAxis(-(55f * intensity), right) * _rShldr.rotation;
            if (_rForearm != null)
            {
                _rForearm.rotation = Quaternion.AngleAxis(-(70f * intensity) + wave, right) * _rForearm.rotation;
            }
        }
    }

    // Spec 40.9/40.1: the renderer feeds the injury-locomotion hint
    // (Faint/Crawl/Limp/ArmHang/HeadClutch/Upright) and the winded flag.
    public void SetPosture(string postureHint, bool winded)
    {
        _posture = string.IsNullOrEmpty(postureHint) ? "Upright" : postureHint;
        _winded = winded;

        // Spec 40.9: a leg wound swaps the Walk cycle for the imported Limp
        // clip (animator state) — replaces the old procedural body sway.
        // Crawl (both legs) rides the same clip until a real crawl exists;
        // the old 35° forward pitch read as a bug, not an injury.
        if (_animator != null)
        {
            _animator.SetBool(LimpingParam, _posture is "Limp" or "Crawl");
        }
    }

    // Spec 40.10: erode a worn garment by its durability (1 = pristine, 0 =
    // rags). The renderer feeds this per worn item from the snapshot's
    // WornDurability; low durability tears the garment (alpha-clip cutoff).
    public void SetGarmentWear(string definitionId, float durability01)
    {
        _bodyBones?.SetWearErosion(definitionId, durability01);
    }

    // Spec 40.8-F: fresh wounds spray blood. Compares this sync's wound seeds
    // against everything seen before; the FIRST unseen fresh wound (heal ≈ 0)
    // fires one RVFX splash at its zone bone — one spray per sync, so a
    // 3-gash bite reads as one hit, and a 0.4 s real-time gate keeps fast
    // sim-speeds from hosing the screen. The first sync after spawn/load is
    // silent: those wounds are history, not fresh hits.
    private void SyncWoundSplashVfx()
    {
        if (!_woundVfxPrimed)
        {
            foreach (var (_, seed, _) in _woundScratch)
            {
                _seenWoundSeeds.Add(seed);
            }

            _woundVfxPrimed = true;
            return;
        }

        string splashZone = null;
        var splashSeed = 0;
        foreach (var (zone, seed, heal) in _woundScratch)
        {
            if (_seenWoundSeeds.Add(seed) && splashZone == null && heal < 0.05f)
            {
                splashZone = zone;
                splashSeed = seed;
            }
        }

        // Long runs accumulate healed-away seeds — rebuild when it bloats.
        if (_seenWoundSeeds.Count > 128)
        {
            _seenWoundSeeds.Clear();
            foreach (var (_, seed, _) in _woundScratch)
            {
                _seenWoundSeeds.Add(seed);
            }
        }

        if (splashZone == null || Time.time - _lastSplashTime < 0.4f ||
            _bodyBones == null || !ZoneBoneAnchors.TryGetValue(splashZone, out var boneName))
        {
            return;
        }

        var bone = _bodyBones.GetBone(boneName);
        if (bone == null)
        {
            return;
        }

        _splashPrefabs ??= new[]
        {
            Resources.Load<GameObject>("HexLive/VFX/Blood_Splash_01_URP"),
            Resources.Load<GameObject>("HexLive/VFX/Blood_Splash_02_URP"),
            Resources.Load<GameObject>("HexLive/VFX/Blood_Splash_03_URP")
        };
        var prefab = _splashPrefabs[(splashSeed & int.MaxValue) % _splashPrefabs.Length];
        if (prefab == null)
        {
            return;
        }

        _lastSplashTime = Time.time;
        var root = _bodyRoot != null ? _bodyRoot : transform;
        var outward = bone.position - root.position;
        outward.y = 0f;
        if (outward.sqrMagnitude < 0.0001f)
        {
            outward = root.forward;
        }

        var vfx = Instantiate(prefab, bone.position,
            UnityEngine.Quaternion.LookRotation(outward.normalized + UnityEngine.Vector3.up * 0.35f));
        // The pack is authored for a full-size human; our actors are ~0.35
        // scale — Hierarchy scaling shrinks sizes AND velocities together.
        vfx.transform.localScale = UnityEngine.Vector3.one * root.lossyScale.y;
        foreach (var ps in vfx.GetComponentsInChildren<UnityEngine.ParticleSystem>(true))
        {
            var main = ps.main;
            main.scalingMode = UnityEngine.ParticleSystemScalingMode.Hierarchy;
        }

        // The pack's KillEffect self-destroys in 3-5 s; this is the backstop.
        Destroy(vfx, 8f);
    }

    // Spec 40.7: paint the bare skin from tan (0..1) and acute sunburn (0..1).
    // Tan multiplies the skin toward a weathered brown; sunburn layers red on
    // top. Values are the exported Needs.TanLevel / Needs.Sunburn. First pass —
    // tune the target colours against a screenshot; if the skin shader isn't
    // URP _BaseColor the property block is a harmless no-op.
    public void SetSkinWeathering(float tanLevel, float sunburn, float hurt = 0f, float hygiene = 1f)
    {
        if (_skinTintTargets.Count == 0)
        {
            return;
        }

        _skinMpb ??= new MaterialPropertyBlock();
        // Spec 40.7: tanning goes THROUGH red — pale skin first flushes like a
        // fresh burn (the retired low-HP red, reused: it read exactly like
        // "just caught the sun"), then the red deepens into the brown. Full
        // tan is a deep brown — multiplies the skin texture, so at TanLevel 1
        // the skin goes markedly dark, not just a light bronze.
        var tan = Mathf.Clamp01(tanLevel);
        var redPhase = Mathf.Clamp01(tan / 0.35f);
        var brownPhase = Mathf.Clamp01((tan - 0.35f) / 0.65f);
        var tint = Color.Lerp(Color.white, new Color(0.79f, 0.55f, 0.57f), redPhase);
        tint = Color.Lerp(tint, new Color(0.40f, 0.27f, 0.18f), brownPhase);
        tint = Color.Lerp(tint, new Color(0.95f, 0.50f, 0.42f), Mathf.Clamp01(sunburn) * 0.75f);
        // Spec 40.6: grime — the filthier the skin (low hygiene), the more it
        // muddies toward a dull earthy brown. Applied before the injury flush so
        // wounds still read on a dirty body.
        // At Hygiene 0 the WHOLE skin must read dirty (user: "вся кожа должна
        // быть грязная") — the smudge decals give texture, this tint carries
        // the overall filth. Half-strength earthy brown at zero hygiene.
        var grime = Mathf.Clamp01(1f - Mathf.Clamp01(hygiene));
        tint = Color.Lerp(tint, new Color(0.42f, 0.37f, 0.30f), grime * 0.5f);
        // Spec 40.8: a badly hurt body flushes bruised red-purple. First-pass
        // whole-body tint (per-zone wound decals need texture work); driven by
        // 1 - Health so it only shows when genuinely wounded.
        tint = Color.Lerp(tint, new Color(0.62f, 0.24f, 0.28f), Mathf.Clamp01(hurt) * 0.6f);

        // Per-submesh: only the skin material slots, never the eyes/lashes/etc.
        foreach (var (renderer, index) in _skinTintTargets)
        {
            if (renderer == null)
            {
                continue;
            }

            renderer.GetPropertyBlock(_skinMpb, index);
            _skinMpb.SetColor(BaseColorId, tint);
            renderer.SetPropertyBlock(_skinMpb, index);
        }
    }

    // Classify each body-renderer material slot as skin (tintable) or not.
    private void BuildSkinTintTargets()
    {
        _skinTintTargets.Clear();
        if (_bodySkins == null)
        {
            return;
        }

        foreach (var skin in _bodySkins)
        {
            if (skin == null)
            {
                continue;
            }

            // Anything living under a Wear is hair or a garment, never skin —
            // material-name hints can't catch hair strands named "Bangs1"/"Cap".
            // Keeps hair off the tan tint AND off the decal rendering layer.
            if (skin.GetComponentInParent<Wear>() != null)
            {
                continue;
            }

            var mats = skin.sharedMaterials;
            for (var i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null)
                {
                    continue;
                }

                var name = mats[i].name.ToLowerInvariant();

                // Some actor materials (e.g. Molly's Cornea) ship with an empty
                // m_Name, so a name-based eye check can't recognise them. Treat
                // any unnamed slot as non-skin — better to skip tinting a skin
                // zone than to whiten an unidentifiable eye material.
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var nonSkin = false;
                foreach (var hint in NonSkinMaterialHints)
                {
                    if (name.Contains(hint))
                    {
                        nonSkin = true;
                        break;
                    }
                }

                if (!nonSkin)
                {
                    _skinTintTargets.Add((skin, i));
                }
            }
        }
    }

    // Layered injury body language. Faint is handled by SetLaying (the body is
    // already down) and Limp by the animator's Limp walk state (LimpingParam),
    // so both are skipped here. ArmHang, HeadClutch and the winded breathing
    // are authored to spec; Crawl is a subtle placeholder until the rig can do
    // real all-fours (the sim signal is authoritative).
    private void ApplyPosturePose()
    {
        if (_laying || _swimming || _bodyRoot == null || _rShldr == null)
        {
            return;
        }

        _posturePhase += Time.deltaTime * _simSpeed;
        var right = _bodyRoot.right;

        switch (_posture)
        {
            case "ArmHang": // a mauled arm hangs limp at the side
                _rShldr.rotation = Quaternion.AngleAxis(12f, right) * _rShldr.rotation;
                if (_rForearm != null)
                {
                    _rForearm.rotation = Quaternion.AngleAxis(10f, right) * _rForearm.rotation;
                }
                break;
            case "HeadClutch": // hand up to a hurt head
                _rShldr.rotation = Quaternion.AngleAxis(-95f, right) * _rShldr.rotation;
                if (_rForearm != null)
                {
                    _rForearm.rotation = Quaternion.AngleAxis(-120f, right) * _rForearm.rotation;
                }
                break;
            // "Limp" and "Crawl" are animator-driven (the Limp walk state via
            // LimpingParam), not procedural poses — no cases here. Crawl keeps
            // only a slumped shoulder on top until a real all-fours clip lands.
            case "Crawl":
                _rShldr.rotation = Quaternion.AngleAxis(-30f, right) * _rShldr.rotation;
                break;
        }

        // Spec 40.1: winded — a spent body heaves for breath (shoulders bob).
        if (_winded && _posture != "Crawl")
        {
            var breath = Mathf.Sin(_posturePhase * 5.5f) * 6f;
            _rShldr.rotation = Quaternion.AngleAxis(-breath - 6f, right) * _rShldr.rotation;
        }
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
            if (_dead)
            {
                // Spec 40.13 v2: a corpse lies down once and then holds a
                // single pose — freeze the animator as soon as the lie-down
                // settles into the Sleep state (no breathing loop forever).
                if (_animator.speed != 0f &&
                    _animator.GetCurrentAnimatorStateInfo(0).IsName("Sleep"))
                {
                    _animator.speed = 0f;
                }
            }
            else
            {
                // Sleep clips run at authored pace, scaled by fast-forward.
                _animator.speed = _simSpeed;
            }

            _animSpeed = _animator.speed;
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

        // Hysteresis: harder to START walking than to KEEP walking, so the
        // stop-start junction gait doesn't flicker the walk/idle blend.
        var threshold = _wasWalking ? _moveEpsilon * 0.6f : _moveEpsilon * 1.3f;
        var walking = linearSpeed > threshold;
        _wasWalking = walking;
        _animator.SetFloat(SpeedParam, walking ? 1f : 0f, 0.05f, Time.deltaTime);

        // Scale the walk-cycle playback to the measured speed: half the
        // speed = half the cadence. The cadence is judged in SIM time (the
        // measured view speed divided by the fast-forward multiplier), clamped
        // so extreme crawling still reads as steps and healthy walking never
        // overclocks — then the multiplier scales the playback back up, so a
        // 4× world steps exactly 4× faster instead of gliding.
        var targetAnimSpeed = 1f;
        // §40.18-B: swim clips play at their authored pace — the walk-cadence
        // ground-matching would crawl the strokes (deep water is slow by sim).
        if (walking && !_swimming && _bodyRoot != null && _jumpTimer <= 0f)
        {
            var fullSpeed = 1.7f * _bodyRoot.lossyScale.y * FullWalkBodyHeightsPerSec;
            var simCadence = linearSpeed / Mathf.Max(0.0001f, fullSpeed * _simSpeed);
            targetAnimSpeed = Mathf.Clamp(simCadence, 0.35f, 1.15f);
        }

        _animSpeed = Mathf.MoveTowards(_animSpeed, targetAnimSpeed, Time.deltaTime * 3f * _simSpeed);
        _animator.speed = _animSpeed * _simSpeed;

        // §21.21B: while a hex-step jump is flying, compress the jump clip so
        // its authored length fits the arc window exactly — the same
        // HexHopTuning number that paces the sim traversal.
        if (_jumpTimer > 0f)
        {
            var jumpState = _animator.GetCurrentAnimatorStateInfo(0);
            if (jumpState.IsName("JumpUp") || jumpState.IsName("JumpDown"))
            {
                _animator.speed = jumpState.length * _simSpeed /
                    Mathf.Max(0.05f, _jumpDuration);
                _animSpeed = _animator.speed;
            }
        }

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

    // Spec 20.16: a procedural arm swing for the current action, layered on
    // top of the animated pose. Rotations are applied in WORLD space around
    // the body's right axis (negative = swing forward/up), so the actor rig's
    // per-bone local orientation is irrelevant. Amplitudes are deliberately
    // moderate; flip the sign if a motion reads backwards.
    private void ApplyActionPose()
    {
        if (_action == ActionKind.None || _laying || _swimming || _rShldr == null || _bodyRoot == null)
        {
            _actionPhase = 0f;
            return;
        }

        _actionPhase += Time.deltaTime * _simSpeed;
        var right = _bodyRoot.right;

        float shldr;
        float fore;
        switch (_action)
        {
            case ActionKind.Chop:
            {
                var t = Mathf.Repeat(_actionPhase * 2.2f, 1f);
                shldr = t < 0.65f
                    ? Mathf.Lerp(-15f, 55f, t / 0.65f)            // wind up
                    : Mathf.Lerp(55f, -15f, (t - 0.65f) / 0.35f); // strike
                fore = Mathf.Max(0f, shldr) * 0.6f;
                break;
            }
            case ActionKind.Work:
            {
                var s = Mathf.Sin(_actionPhase * 6f);
                shldr = 18f + s * 10f;
                fore = 25f + s * 12f;
                break;
            }
            case ActionKind.SpearThrust:
            {
                var t = Mathf.Repeat(_actionPhase * 1.6f, 1f);
                var jab = t < 0.3f
                    ? Mathf.Lerp(20f, -10f, t / 0.3f)
                    : Mathf.Lerp(-10f, 20f, (t - 0.3f) / 0.7f);
                shldr = 70f + jab;
                fore = 10f;
                break;
            }
            case ActionKind.BowDraw:
            {
                shldr = 75f + Mathf.Sin(_actionPhase * 3f) * 4f;
                fore = 10f;
                break;
            }
            case ActionKind.Attack:
            {
                var t = Mathf.Repeat(_actionPhase * 2.6f, 1f);
                shldr = t < 0.5f
                    ? Mathf.Lerp(-10f, 50f, t / 0.5f)
                    : Mathf.Lerp(50f, -10f, (t - 0.5f) / 0.5f);
                fore = Mathf.Max(0f, shldr) * 0.5f;
                break;
            }
            case ActionKind.RaiseToMouth:
            {
                shldr = 35f;
                fore = 75f + Mathf.Sin(_actionPhase * 4f) * 5f; // hand to face
                break;
            }
            default:
                return;
        }

        _rShldr.rotation = Quaternion.AngleAxis(-shldr, right) * _rShldr.rotation;
        if (_rForearm != null)
        {
            _rForearm.rotation = Quaternion.AngleAxis(-fore, right) * _rForearm.rotation;
        }
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
                // Lie on the bed: attach owns XZ + orientation, the sleep
                // surface owns Y. The clips are ground-authored (Y baked into
                // the pose), so the root is simply pinned — one fixed point,
                // no per-frame bounds correction.
                _bodyRoot.position = new Vector3(
                    _layingAttach.position.x, _layingSurfaceY, _layingAttach.position.z);
                _bodyRoot.rotation = _layingAttach.rotation;
            }
            else if (_laying)
            {
                // Sleeping without a bed: the actor root already sits on the
                // ground, and the clip keeps the body on it.
                _bodyRoot.localPosition = Vector3.zero;
                _bodyRoot.localRotation = Quaternion.identity;
            }
            else
            {
                // Iter 28: ledge seat — lift the butt onto the upper step and
                // tuck it back against the edge (local -Z = behind the back).
                var rest = _ledgeSit
                    ? new Vector3(0f, LedgeSeatLift, -LedgeSeatBack)
                    : Vector3.zero;
                // §21.21B hex-step jump: the ballistic trajectory rides this
                // local offset (world delta -> local handles root rotation
                // and scale in one go).
                var jumpOffset = JumpOffsetWorld();
                if (jumpOffset != Vector3.zero)
                {
                    rest += transform.InverseTransformVector(jumpOffset);
                }

                // §40.18-B: one shared body-height lift while swimming.
                if (_swimming)
                {
                    rest.y += SwimBodyLift / Mathf.Max(0.0001f, transform.lossyScale.y);
                }

                _bodyRoot.localPosition = rest;
                _bodyRoot.localRotation = Quaternion.identity;
            }
        }

        // Spec 40.13: while ragdolled (faint/corpse) the bones belong to
        // physics — no procedural pose layer may write over them.
        if (!_ragdollActive)
        {
            // Layer the current action's arm swing over the animated pose.
            ApplyActionPose();

            // Spec 40.7 / 33: thermal body language — shiver when cold, fan when hot.
            ApplyThermalPose();

            // Spec 40.9 / 40.1: injury posture + winded breathing.
            ApplyPosturePose();
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
            _lookAtIK.solver.eyesWeight = 0.2f;
            _lookAtIK.solver.bodyWeight = 0.3f;
            _lookAtIK.solver.clampWeight = 0.5f;
            // Clamp eye rotation hard so a wide gaze never rolls the eyes back
            // to the whites (0 = free, 1 = fully clamped). The head carries the
            // rest of the turn.
            _lookAtIK.solver.clampWeightEyes = 0.4f;
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
