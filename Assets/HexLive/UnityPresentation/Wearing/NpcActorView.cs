using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.UI;

namespace HexLive.UnityPresentation.Wearing
{

// Spec 31B.5: the bridge between one NPC's snapshot and her actor body.
// Owns wardrobe sync (sim WornItems -> Equip/TakeOff), the walk/idle
// animator, and the LookAtIK gaze (ported from molly_copy's pattern:
// drive solver.target + weights, ease in/out smoothly).
public sealed class NpcActorView : MonoBehaviour, UI.ISpeechStage
{
    private static readonly int SpeedParam = Animator.StringToHash("Speed");
    private static readonly int HitReactParam = Animator.StringToHash("HitReact");

    private BodyBones _bodyBones;
    private Animator _animator;
    private LookAtIK _lookAtIK;
    private FullBodyBipedIK _fullBodyIK;
    private ActorName _actorMesh;
    private readonly Dictionary<string, int> _equippedSimItems = new();
    private readonly List<string> _removeScratch = new();

    // Spec §52.8: leg-slung tool props parked in the worn holster's tool.*
    // anchors (tool id → the instantiated model). Filled by SyncHolster.
    private readonly Dictionary<string, GameObject> _holsterProps = new();
    private readonly HashSet<string> _holsterWantScratch = new();
    private readonly List<string> _holsterRemoveScratch = new();
    private readonly HashSet<string> _slotClashWarned = new();

    public ActorName ActorMesh => _actorMesh;

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
    // §axe: axe/pickaxe work (Harvest/Process) plays a real looping swing clip
    // (Standing Melee Attack Horizontal) via the Chop clip-state, replacing the
    // old procedural shoulder chop.
    private static readonly int ChoppingParam = Animator.StringToHash("Chopping");
    // §gear-craft v2: the staged in-place craft kneels her into the planting-
    // style work clip (state "CraftWork") instead of the generic crouch.
    private static readonly int CraftingParam = Animator.StringToHash("Crafting");
    private static readonly int SittingParam = Animator.StringToHash("Sitting");
    // Clip-based action states (built by the "HexLive ▸ Build NPC Action States"
    // editor menu). Clips are swapped in via an AnimatorOverrideController.
    private static readonly int TalkingParam = Animator.StringToHash("Talking");
    private static readonly int GatheringParam = Animator.StringToHash("Gathering");
    private static readonly int DrinkingParam = Animator.StringToHash("Drinking");
    // §Wardrobe-anim: the don (Dress) and doff (Undress) beats — each a Loopy
    // clip state whose base clip is swapped from NpcAnimSet.dress / .undress.
    private static readonly int DressingParam = Animator.StringToHash("Dressing");
    private static readonly int UndressingParam = Animator.StringToHash("Undressing");
    // Base-clip KEYS for the override controller (imported takes; see
    // BuildNpcActionStates). Both currently point at the same placeholder FBX.
    private const string DressBaseClip = "X Bot@Dressing";
    private const string UndressBaseClip = "X Bot@Undressing";
    // §Wardrobe-anim: must match ExecutionSystem.WardrobeHandoffFraction — the
    // beat split where the garment changes hands (gather<->don, doff<->gather).
    private const float WardrobeHandoffFraction = 0.5f;
    // Spec 40.x: runtime handedness — drives Humanoid mirror on the one-handed
    // clip states (Drink/Gather/Talk/Attack) so a lefty/lost-right-hand NPC
    // acts with the other hand without any clip rebake.
    private static readonly int MirrorActionParam = Animator.StringToHash("MirrorAction");
    // Inverse mirror for clips whose NATIVE authored hand is the left (the drink
    // clip): a right-handed NPC must mirror it, a lefty plays it native — the
    // opposite polarity to MirrorAction (used by right-native clips). Default 1.
    private static readonly int MirrorActionInvParam = Animator.StringToHash("MirrorActionInv");
    private static readonly int DeadParam = Animator.StringToHash("Dead");
    private static readonly int AttackParam = Animator.StringToHash("Attack");
    // Base-clip take-names each action state plays (the override KEYS).
    private const string TalkBaseClip = "X Bot@Talking";
    private const string AttackBaseClip = "X Bot@Bayonet Stab";
    // One full procedural swing = this many _actionPhase units (the Attack
    // case repeats at phase*cycles) — shared with the sim-window pacing in
    // SetCombat so ONE swing spans exactly the weapon's attack duration.
    private const float AttackSwingCycles = 2.6f;
    private const string ChopBaseClip = "Standing Melee Attack Horizontal";
    private const string DeathBaseClip = "X Bot@Death From Back Headshot";
    private static readonly int LimpingParam = Animator.StringToHash("Limping");
    private static readonly int CrawlingParam = Animator.StringToHash("Crawling"); // §50
    private static readonly int JumpUpParam = Animator.StringToHash("JumpUp");
    private static readonly int JumpDownParam = Animator.StringToHash("JumpDown");
    // §21.21B: drives the JumpUp/JumpDown states' Speed Multiplier so the
    // authored jump clip is COMPRESSED to exactly the sim hop window (one
    // playthrough == HopSeconds), instead of running at its own ~2.6s length
    // and looping/overshooting the window.
    private static readonly int JumpSpeedParam = Animator.StringToHash("JumpSpeed");
    // Authored seconds PER DIRECTION — the JumpUp and JumpDown clips are
    // different lengths (65 vs 50 frames), so compressing both with one shared
    // length played one of them at the wrong speed and the crouch/landing
    // beats drifted off the sim's takeoff/landing windows.
    private float _jumpUpClipLength;
    private float _jumpDownClipLength;

    // §NPC-anim: clip config (talk/death variants + weapon idle/attack) is a
    // shared asset loaded once; the override controller lets each actor swap the
    // action-state clips at runtime (random talk/death, weapon-specific attack).
    private static NpcAnimSet _animSet;
    private static bool _animSetTried;
    private AnimatorOverrideController _animOverride;
    private readonly Dictionary<string, AnimationClip> _clipsByName = new();
    private bool _wantsTalk;          // sim says CurrentInteraction == "Talk"
    private bool _talkTurnOn;         // this NPC's turn to speak right now
    private bool _wasFighting;        // rising-edge detect for the attack trigger
    private bool _bareStance;         // fists fight: Idle swapped for the boxing stance
    private bool _wasSwinging;        // rising edge of the sim's swing window
    private float _attackSpeed = 1f;
    private string _combatWeaponId;
    // §67.10: длиннее реплики (кап 4.2 с) — иначе следующий ход начинается,
    // пока предыдущая ещё звучит, и собеседницы говорят друг поверх друга.
    // Оба актёра считают ход от ОБЩИХ часов без обмена сигналами, поэтому
    // период должен покрывать самую длинную реплику, а не среднюю.
    private const float TalkTurnSeconds = 4.6f; // one speaks, then the other
    private static readonly int SwimmingParam = Animator.StringToHash("Swimming");
    private string _currentPropId;
    // Right-handed by default; flipped at runtime via SetHandedness.
    private bool _leftHanded;
    private GameObject _handProp;
    private Renderer[] _handPropRenderers = new Renderer[0];

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

    // §74: donor actor → its body materials by slot name. Static: the actor
    // prefabs are shared assets, so four maps serve the whole colony.
    private static readonly Dictionary<string, Dictionary<string, Material>> _skinSets = new();

    /// <summary>Spec §50: the owner's current skin tone (tan/sunburn/grime,
    /// no pain flush) — a severed-limb drop bakes it into its material so the
    /// limb matches the body it came off.</summary>
    public Color SkinTint { get; private set; } = Color.white;

    private static readonly string[] NonSkinMaterialHints =
    {
        "cornea", "sclera", "iris", "pupil", "eye", "moist", "socket", "lash",
        "tear", "hair", "tooth", "teeth", "gum", "tongue", "mouth", "nail",
        "lacrimal", "brow"
    };

    /// <summary>Spec 40.8-G: shared skin-slot filter — the PaintPointMap
    /// generator must pick the SAME material slots the runtime paints.
    /// Unnamed slots read as non-skin (Molly's nameless Cornea).</summary>
    public static bool IsSkinMaterialName(string materialName)
    {
        if (string.IsNullOrEmpty(materialName))
        {
            return false;
        }

        var lower = materialName.ToLowerInvariant();
        foreach (var hint in NonSkinMaterialHints)
        {
            if (lower.Contains(hint))
            {
                return false;
            }
        }

        return true;
    }

    // Spec 40.8-D: wounds/bandages are PAINTED into the skin textures (molly
    // bake-raycast placement + stamp records that fade with healing). Flip off
    // to fall back to the decal projectors.
    private const bool PaintWoundsIntoTexture = true;
    // Spec 40.8 v4.3: painted sweat droplets are RETIRED (user verdict: the
    // glassy RAIN decal droplets read right, the painted patches did not —
    // and their refraction lens sampled the ORIGINAL albedo, so over a
    // wound they showed skin-coloured discs). Sweat now reuses the rain
    // droplet decals below — ONE droplet system for rain and sweat. The
    // painted-droplet tech stays in SkinTexturePainter (wounds/bandages
    // still paint; flip this back on to compare).
    private const bool PaintSweatDroplets = false;
    // The v2 decal-projector bubbles ("не идеальные, но пока лучше не
    // получилось") retire while the painted droplets are on — two sweat
    // systems double-coat the skin. The projector RAIN pass stays (streaks
    // on skin + cloth).
    private const bool SweatDropletProjectors = false;
    private SkinTexturePainter _skinPainter;
    private readonly List<(string zone, int seed, float heal)> _woundScratch = new();

    private readonly HashSet<int> _seenWoundSeeds = new();
    private bool _woundVfxPrimed;
    private float _lastSplashTime;

    // Spec 40.8/40.6: skin decal layer (wounds/dirt/sweat on bare zones only).
    private SkinDecals _skinDecals;
    private int _npcId;
    private readonly Dictionary<string, float> _zoneHealthScratch = new();
    private readonly HashSet<string> _uncoveredScratch = new();
    private readonly HashSet<string> _bandagedScratch = new();
    private readonly HashSet<string> _gauzeScratch = new();

    // Spec §50: zones already hidden by amputation (a limb never comes back, so
    // this only grows). Maps a severed BodyPart zone to the DISTAL bone whose
    // sub-tree we collapse — a below-elbow/below-knee cut that leaves a bloody
    // stub rather than deforming the shoulder/hip (and the cloth riding them).
    private readonly HashSet<string> _severedZones = new();

    private static readonly Dictionary<string, string> SeveredDistalBone = new()
    {
        ["ArmL"] = "lForearmBend",
        ["ArmR"] = "rForearmBend",
        ["LegL"] = "lShin",
        ["LegR"] = "rShin"
    };

    // Spec §50: the STUMP (cut) bone where the arterial fountain sprays — the
    // PROXIMAL parent that stays (upper arm / thigh), NOT the collapsed distal
    // bone. Sits at the cut and rides the body as she crawls.
    private static readonly Dictionary<string, string> SeveredStumpBone = new()
    {
        ["ArmL"] = "lShldrBend",
        ["ArmR"] = "rShldrBend",
        ["LegL"] = "lThighBend",
        ["LegR"] = "rThighBend"
    };

    // Spec §50: on sever, ONE gentle fountain at the cut that spurts + drips for
    // SeverFountainSeconds, raining ~SeverDripRate droplets/sec onto the ground
    // (≈3× a normal bleed's dripping). Public statics so a dev scene can dial
    // them live.
    public static float SeverFountainSeconds = 3f;
    public static float SeverDripRate = 15f;

    // First SetBodyCondition seeds already-severed zones WITHOUT a fountain (a
    // save loaded mid-amputation shouldn't spray); real severs after fire.
    private bool _severVfxPrimed;

    // Spec §50: once a leg is gone she can't stand — every standing/idle clip is
    // swapped for the prone idle (and walking for the crawl) via the override
    // controller; sit/sleep/lie/drink keep their own clips.
    // §50-prone («лежит»): ANY lost leg — she crawls; prone = no tools, no
    // weapons, no work/fight poses. Mirrors sim BodyState.IsProne.
    private bool _legless;

    private AnimationClip ProneClip => _animSet != null ? _animSet.proneIdle : null;

    private AnimationClip CrawlClip => _animSet != null ? _animSet.crawl : null;

    // A standing action clip becomes the prone idle while legless.
    private AnimationClip Standing(AnimationClip standing) =>
        _legless && ProneClip != null ? ProneClip : standing;

    // Spec 40.10-C: garment grime + wound blood soak. Each hurt zone is
    // forwarded by NAME + strength; the garment painter maps zone→UV through
    // its editor-baked point map (spec 40.8-G — the old world-space spheres
    // forced garment re-bakes as the animated bones moved). Zones never rip
    // holes — clothing damage is tracked separately (the garment's own
    // durability drives tear). Anchors mirror SkinDecals zones.
    private const int MaxDamageSpheres = 8;
    private const float DamageSphereBite = 0.9f; // zone health below this bleeds through
    private readonly string[] _damageZoneNames = new string[MaxDamageSpheres];
    private readonly float[] _damageZoneStrengths = new float[MaxDamageSpheres];
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

    /// <summary>Spec 40.8-G: the PaintPointMap generator bakes garment
    /// anchor points off the same per-zone bones the blood soak targets.</summary>
    public static IReadOnlyDictionary<string, string> GarmentZoneAnchors => ZoneBoneAnchors;

    // Spec 20.16: procedural action motion layered on top of the Animator.
    private enum ActionKind { None, Chop, Work, RaiseToMouth, BowDraw, SpearThrust, Attack }
    private Transform _rShldr;
    private Transform _rForearm;
    private Transform _rHand;
    // Spec 40.7: the cold shiver is a two-sided hug — left arm + a spine curl
    // and head tuck, not just the right arm (the old one-armed hunch read as a
    // bug). Bound alongside the right arm in Construct.
    private Transform _lShldr;
    private Transform _lForearm;
    private Transform _lHand;
    private Transform _thermalChest;
    private Transform _thermalHead;
    private bool _actionTargetActive;
    private Vector3 _actionTargetPoint;
    private const float ActionTargetIkCenter = 0.5f;
    private const float ActionTargetIkWidth = 0.4f;
    private const float ActionTargetIkMaxHandWeight = 0.9f;
    private const float ActionTargetIkBodyLean = 0.35f;

    // Spec 40.7: cold-shiver knobs, exposed static so the ShiverTest dev scene
    // can tune them live with sliders (all amplitudes in degrees; Frequency is
    // the shared sine rate). Defaults are deliberately small — a fine shiver,
    // not a seizure.
    public static class ShiverTuning
    {
        // Tuned live in the ShiverTest scene (2026-07-13).
        public static float Frequency = 38f;
        public static float ShoulderTremble = 0.8f;
        public static float ForearmTremble = 1.3f;
        public static float SpineTremble = 0.7f;
        public static float HeadTremble = 0.58f;
        public static float SpineCurl = 0f;     // no static spine curl — tremble only
        public static float HeadTuck = 17.13f;  // static FORWARD head tuck
    }
    private ActionKind _action;
    private float _actionPhase;

    // Iter 28: ledge seat — while sitting at a one-step seam the body is
    // lifted so the butt rests ON the upper step (feet reach the lower one)
    // and tucked back onto the edge. She faces straight out into the drop
    // (§29G), so local -Z points back at the HIGH tile — LedgeSeatBack slides
    // her butt back ONTO the rim. TUNING KNOBS (config-driven so they can be
    // dialed without editing code — the step is ~0.55 world high):
    //   LedgeSeatLift — DIRECT Y offset, ALWAYS applied (negative lowers her);
    //   LedgeSeatBack — slide back onto the rim (toward the high tile).
    public static float LedgeSeatLift = 0.40f;
    public static float LedgeSeatBack = 0.45f;
    // Extra lift per elevation step she perches UP from a lower tile (≈ one
    // step high). 0 when she already sits on the higher tile's rim.
    private const float LedgeSeatPerStep = 0.55f;
    private bool _ledgeSit;

    // Face life (blink + mood expression) on the body blend shapes.
    private NpcFaceAnimator _face;

    private bool _laying;
    private Transform _layingAttach;
    // Wake-up ease: while asleep the body is pinned to the bed attach point
    // while the actor root stands on the beside-junction — releasing the pin
    // in one frame teleported her sideways off the bed. On wake the body
    // starts where it lay and catches up to the root over this many seconds.
    private const float WakeEaseSeconds = 0.6f;
    private Vector3 _wakeFromPos;
    private Quaternion _wakeFromRot;
    private float _wakeBlend;
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
    // Healthy full speed is 1.0 world units/s ≈ 0.76 body heights/s. This is a
    // property of the CLIP (how much ground one cycle covers at rate 1.0), so
    // it must NOT be retuned when the sim's pace changes — the cadence is what
    // moves.
    private const float FullWalkBodyHeightsPerSec = 0.76f;
    // §71: how much ground each GAIT covers, as a multiple of the walk clip's
    // pace. The girl blends walk -> slow run -> run as she speeds up, and each
    // clip then plays at ~1x its authored rate instead of a walk cycle
    // spinning absurdly fast. Mixamo's takes run roughly walk 1 : jog 2 : run
    // 3.4 — if the feet slide at a sprint, these are the two numbers to tune.
    private const float SlowRunCadence = 2.0f;
    private const float RunCadence = 3.4f;
    // Playback still trims a little around the blended gait (a hobbling or
    // soaked girl takes slower steps), but never far from the authored rate.
    private const float MinGaitCadence = 0.35f;
    private const float MaxGaitCadence = 1.25f;
    // §71: the three GaitBlend slots, keyed by CLIP name — these are the
    // AnimatorOverrideController keys. Overriding all three with one clip (the
    // §50 crawl) pins her to a single gait that can never blend into a run.
    private static readonly string[] GaitClipKeys = { "Walk", "X Bot@Slow Run", "X Bot@Running" };
    private static readonly int GaitParam = Animator.StringToHash("Gait");
    private float _gait;
    // §71: the sim's gait decision (SetRunning). NOT re-derived from speed.
    private bool _running;
    // A brisk walk is allowed to outrun the walk clip a little; a run clip
    // played much above its authored rate just looks frantic.
    private const float MaxWalkCadence = 1.6f;

    /// <summary>§71: the sim says whether she is running — walk is the default,
    /// and running always means a reason (defend, flee, adrenaline, or a body
    /// desperate for food or water).</summary>
    public void SetRunning(bool running) => _running = running;
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
    // Seconds to EASE the swim body-lift in/out. Diving in used to add the full
    // SwimBodyLift the instant she entered the water tile — a hard snap up. Now
    // it ramps, so the plunge resolves smoothly into the floating swim pose.
    private const float SwimLiftEaseSeconds = 0.4f;
    private bool _swimming;
    private float _swimBlend; // 0..1 eased weight on SwimBodyLift

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
        // §21.21B: down-jumps can run on their own (faster) window; the beat
        // FRACTIONS stay tied to HopSeconds (same shape), only the total window
        // changes, so the sim window, the arc and the clip scale together.
        var hop = HexLive.Simulation.Navigation.HexHopTuning.HopSeconds;
        var window = HexLive.Simulation.Navigation.HexHopTuning.WindowSeconds(up);
        // Same jump for everything — water or land (no special water handling);
        // only up vs down differs, via the window.
        StartJumpArc(
            up,
            heightDeltaWorld,
            window,
            HexLive.Simulation.Navigation.HexHopTuning.TakeoffSeconds / hop,
            (hop - HexLive.Simulation.Navigation.HexHopTuning.LandingSeconds) / hop);

        // §67: нырок в воду — всплеск на посадочной доле дуги.
        if (intoWater && _simSpeed <= 4.01f)
        {
            Audio.SoundManager.Instance?.PlayDelayed(
                Audio.FmodSfx.Sfx.Splash, transform.position,
                window * 0.6f / Mathf.Max(0.25f, _simSpeed));
        }
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
        // Compress the authored clip to exactly this window: one playthrough ==
        // durationSimSeconds. Speed = clipLength / window (Unity multiplies this
        // by the global animator.speed, so fast-forward stays in sync with the
        // arc timer). Falls back to 1 if the clip length is unknown.
        var jumpClipLength = _jumpUp ? _jumpUpClipLength : _jumpDownClipLength;
        if (jumpClipLength > 0.001f)
        {
            _animator.SetFloat(JumpSpeedParam, jumpClipLength / _jumpDuration);
        }
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

        // One drop for EVERYTHING (water or land — no special plunge): stay
        // LEVEL across the lip and only fall once she has
        // crossed to above the lower ground. Falling early scrapes her feet on
        // the edge — so hold to DownFallStartFrac of the flight, then gravity
        // (quadratic) from there down to touchdown at flight-end.
        var fallStart = Mathf.Clamp(
            HexLive.Simulation.Navigation.HexHopTuning.DownFallStartFrac, 0f, 0.95f);
        float ease;
        if (tf <= fallStart)
        {
            ease = 0f; // level flight — above the lower tile, not yet falling
        }
        else
        {
            var f = Mathf.InverseLerp(fallStart, 1f, tf);
            ease = f * f; // gravity from the crossing point to the target
        }

        // A little UP pop off the edge during the level phase (feet clear the
        // lip). ease < 0 = above stand level, since DOWN heightDelta < 0.
        var pop = Mathf.Sin(Mathf.Clamp01(tf / Mathf.Max(0.01f, fallStart)) * Mathf.PI);
        ease -= pop * (HexLive.Simulation.Navigation.HexHopTuning.DownHopUp /
            Mathf.Max(0.01f, Mathf.Abs(_jumpHeightDelta)));

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

    // Pre-§74 signature: body, materials, hair and voice all implied by the one
    // mesh name. Kept for the seven test-scene bootstraps and the health doll,
    // which cast a fixed girl on purpose.
    public void Construct(string actorMeshName, int npcId = 0)
    {
        Construct(actorMeshName, npcId, null, null, null);
    }

    // §74: a girl is a COMPOSITION. The mesh still decides the body — and with
    // it the garment fits (WearConfig) and the skin paint-point map, both
    // properties of the geometry — but the material set, the hairstyle and the
    // voice bank now come in separately and may belong to someone else.
    //
    // Every one of the three is optional: null/empty means "the mesh's own",
    // i.e. exactly the pre-§74 body, which is what a test scene and a pre-§74
    // save both get.
    public void Construct(string actorMeshName, int npcId,
        string skinSet, string hairstyle, string voiceBank)
    {
        _npcId = npcId;
        // §67.6: голосовой банк персонажа = его меш-имя (Molly/Jana/…) —
        // файлы voice_<char>_<emotion>_<n> подхватываются по факту наличия.
        // §74: …если сим не выдал ей ЧУЖОЙ банк — тогда играет он.
        var voice = string.IsNullOrEmpty(voiceBank) ? actorMeshName : voiceBank;
        _voiceChar = (voice ?? string.Empty).Trim().ToLowerInvariant();
        // §67.7: липсинк на реплики — анализатору нужна голова с виземами
        // (у примитивных фолбэк-капсул её нет, там и рта-то нет).
        foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh != null && smr.sharedMesh.blendShapeCount > 50)
            {
                _voiceLipSync = gameObject.AddComponent<Audio.NpcVoiceLipSync>();
                _voiceLipSync.Construct(smr);
                break;
            }
        }
        _bodyBones = GetComponentInChildren<BodyBones>();
        _animator = GetComponentInChildren<Animator>();
        _lookAtIK = GetComponentInChildren<LookAtIK>();
        _fullBodyIK = GetComponentInChildren<FullBodyBipedIK>();

        // Cache the authored length of the jump clip so StartJumpArc can scale
        // it to the sim hop window (§21.21B). Both JumpUp/JumpDown share one
        // clip whose name contains "Jump".
        if (_animator != null && _animator.runtimeAnimatorController != null)
        {
            foreach (var clip in _animator.runtimeAnimatorController.animationClips)
            {
                if (clip == null)
                {
                    continue;
                }
                // §NPC-anim: index every clip by name (the override KEYS).
                _clipsByName[clip.name] = clip;
                if (clip.name.IndexOf("JumpDown", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _jumpDownClipLength = clip.length;
                }
                else if (clip.name.IndexOf("Jump", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _jumpUpClipLength = clip.length;
                }
            }

            // Wrap the controller so action-state clips can be swapped live.
            _animOverride = new AnimatorOverrideController(_animator.runtimeAnimatorController);
            _animator.runtimeAnimatorController = _animOverride;

            // Sync the runtime mirror params to the current handedness so a
            // pooled/reused instance never starts on the wrong hand.
            _animator.SetBool(MirrorActionParam, _leftHanded);
            _animator.SetBool(MirrorActionInvParam, !_leftHanded);
        }

        // Shared clip config (talk/death variants, per-weapon idle/attack).
        if (!_animSetTried)
        {
            _animSetTried = true;
            _animSet = Resources.Load<NpcAnimSet>("HexLive/NpcAnimSet");
            if (_animSet == null)
            {
                Debug.LogWarning("[NpcAnim] NpcAnimSet not found at Resources/HexLive/NpcAnimSet " +
                    "— talk/death variants and weapon attacks fall back to the base clips.");
            }
        }

        if (System.Enum.TryParse(actorMeshName, out ActorName parsed) == false)
        {
            Debug.LogWarning($"Unknown actor mesh '{actorMeshName}', defaulting to Marta", this);
            parsed = ActorName.Marta;
        }

        _actorMesh = parsed;
        if (_bodyBones != null)
        {
            _bodyBones.Construct(_actorMesh);
            // BodyBones.Construct wipes its worn-visual state; the worn-item
            // cache must reset with it or a pooled/reused actor (or any
            // re-Construct) starts thinking garments are equipped that no
            // longer exist on the body — SyncWorn would then skip re-equipping
            // and the girl stands nude while the sim still lists her clothes.
            _equippedSimItems.Clear();
            // Right-arm bones for procedural action motion (all three actors
            // share the Genesis naming). Rotated in world space around the
            // body's right axis, so their local orientation doesn't matter.
            _rShldr = _bodyBones.GetBone("rShldrBend");
            _rForearm = _bodyBones.GetBone("rForearmBend");
            _rHand = _bodyBones.GetBone("rHand");
            // Spec 40.7: left arm + upper spine + head for the two-sided cold
            // shiver (all three actors share the Genesis naming).
            _lShldr = _bodyBones.GetBone("lShldrBend");
            _lForearm = _bodyBones.GetBone("lForearmBend");
            _lHand = _bodyBones.GetBone("lHand");
            _thermalChest = _bodyBones.GetBone("chestUpper");
            _thermalHead = _bodyBones.GetBone("head");

            // §74: her own hairstyle, not the one authored on the prefab.
            // AFTER BodyBones.Construct, which has just spawned the authored
            // one — SetHair tears that down properly (it destroys the stitched
            // bones, not just the root, or each swap strands a dead skeleton).
            // The FIT still keys off _actorMesh: heightOffset/scale answer the
            // question "how does this hair sit on THIS head", and the head is
            // the mesh's, whoever's skin is painted on it.
            ApplyHairstyle(hairstyle);

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
            // §74: her face BEFORE anything reads the materials. Order is not
            // stylistic — BuildSkinTintTargets classifies slots by material
            // NAME, and SkinTexturePainter below snapshots `body.materials`
            // plus their albedo/normal maps once and never looks again. Swap
            // after either of them and the girl wears her donor's skin with the
            // previous body's paint targets, which reads as a shader bug.
            ApplySkinSet(skinSet);
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
                        _bodyRoot != null ? _bodyRoot : transform, _npcId,
                        _actorMesh.ToString());
                }
            }

            // Face life: blinking + mood-driven expression on the blend shapes.
            _face = gameObject.AddComponent<NpcFaceAnimator>();
            _face.Construct(_bodySkins);
        }

        // Spec 31B.5: FBBIK stays dormant until the renderer feeds an action
        // target. An unfed solver can freeze the pose; tool/weapon actions
        // enable it only for the weighted hit window.
        if (_fullBodyIK != null)
        {
            _fullBodyIK.enabled = false;
            if (_fullBodyIK.solver != null)
            {
                _fullBodyIK.solver.OnPreUpdate += DriveActionTargetIK;
                ResetActionTargetIKWeights();
            }
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

    private void OnDestroy()
    {
        if (_fullBodyIK != null && _fullBodyIK.solver != null)
        {
            _fullBodyIK.solver.OnPreUpdate -= DriveActionTargetIK;
        }
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
        float rainWet = 0f, float waterWet = 0f, IReadOnlyList<string> wornWetness = null,
        IReadOnlyList<string> wornDirtiness = null,
        IReadOnlyList<string> wornBloodiness = null,
        IReadOnlyList<string> wounds = null, IReadOnlyList<string> bandagedZones = null,
        IReadOnlyList<string> severedParts = null)
    {
        if (_skinDecals == null)
        {
            return;
        }

        // Spec §50: hide any limb that has been severed. The deep stump wound
        // the sim filed on the zone paints itself onto the remaining stub via
        // the normal wound path below — no special stump art needed.
        ApplySeveredLimbs(severedParts);

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
        if (waterWet > 0.5f)
        {
            // A water tile dunks the body: the sim snaps BodyWetness to 1
            // there (MoistureSystem) and this pool mirrors that — she climbs
            // out fully glistening and dries through the drain branch below.
            _skinWetness = 1f;
        }
        else if (wetTarget > _skinWetness)
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
        // Water snaps it like the skin — the sim soaks worn items on its Slow
        // tick, this bridges the seconds until that value arrives.
        if (waterWet > 0.5f)
        {
            _clothRainWetness = 1f;
        }
        else if (rainWet > 0.5f)
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

        // Spec 44: dressed zones hide wound marks. A "|g" suffix marks a MEDKIT
        // gauze wrap; a bare zone name marks a HERBAL leaf wrap (gathered
        // plantain). Split them so presentation draws the matching decal.
        _bandagedScratch.Clear();
        _gauzeScratch.Clear();
        if (bandagedZones != null)
        {
            foreach (var zone in bandagedZones)
            {
                if (zone.EndsWith("|g", System.StringComparison.Ordinal))
                {
                    _gauzeScratch.Add(zone.Substring(0, zone.Length - 2));
                }
                else
                {
                    _bandagedScratch.Add(zone);
                }
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
                PaintSweatDroplets ? _skinWetness : 0f, _uncoveredScratch, wetSmoothnessForPaint,
                _gauzeScratch);
            // v4.3: the projector RAIN droplets serve rain AND sweat — the
            // unified wetness pool (whichever of rain/sweat is stronger)
            // feeds the rain pass, so a sweating body beads exactly like a
            // rained-on one. The old sweat-cluster projector pass stays
            // retired (SweatDropletProjectors).
            _skinDecals.Sync(null, _uncoveredScratch, hygiene,
                SweatDropletProjectors ? thermal : 0f,
                Mathf.Max(_clothRainWetness, _skinWetness), null);
        }
        else
        {
            _skinDecals.Sync(wounds, _uncoveredScratch, hygiene, thermal, _skinWetness, _bandagedScratch,
                _gauzeScratch);
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

        // Spec 40.10-C: cloth soaks blood over hurt zones and soils as hygiene
        // drops. Spec 40.8-G: zones travel by NAME — the garment painter
        // resolves them through its editor-baked point map, so no bone
        // positions (and no per-frame garment re-bakes) are involved.
        if (_bodyBones != null)
        {
            var zoneCount = 0;
            foreach (var pair in _zoneHealthScratch)
            {
                if (pair.Value >= DamageSphereBite || zoneCount >= MaxDamageSpheres)
                {
                    continue;
                }

                _damageZoneNames[zoneCount] = pair.Key;
                _damageZoneStrengths[zoneCount] = Mathf.Clamp01(1f - pair.Value);
                zoneCount++;
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

            if (wornDirtiness != null)
            {
                foreach (var entry in wornDirtiness)
                {
                    var tab = entry.IndexOf('\t');
                    if (tab > 0 && float.TryParse(entry.Substring(tab + 1),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var dirt))
                    {
                        var definitionId = entry.Substring(0, tab);
                        var storedBlood = FindWearValue(wornBloodiness, definitionId);
                        _bodyBones.SetWearGrime(definitionId, Mathf.Clamp01(dirt),
                            _damageZoneNames, _damageZoneStrengths, zoneCount,
                            Mathf.Max(storedBlood, Mathf.Clamp01(bloodSoak * 0.7f)));
                    }
                }
            }
        }
    }

    private static float FindWearValue(IReadOnlyList<string> entries, string definitionId)
    {
        if (entries == null)
        {
            return 0f;
        }

        foreach (var entry in entries)
        {
            var tab = entry.IndexOf('\t');
            if (tab > 0 && entry.Substring(0, tab) == definitionId &&
                float.TryParse(entry.Substring(tab + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return Mathf.Clamp01(value);
            }
        }

        return 0f;
    }

    // Spec §50: collapse the bone sub-tree of every severed limb to nothing, so
    // the forearm+hand (or shin+foot) — and the sleeve/trouser riding those
    // bones — vanish, leaving a stub. Permanent and idempotent: once hidden a
    // zone stays hidden (a limb never grows back). Cheap enough to re-assert
    // each tick, which also survives a late Animator write reviving the scale.
    private void ApplySeveredLimbs(IReadOnlyList<string> severedParts)
    {
        if (_bodyBones == null || severedParts == null)
        {
            return;
        }

        foreach (var zone in severedParts)
        {
            if (!SeveredDistalBone.TryGetValue(zone, out var boneName))
            {
                continue;
            }

            // A zone newly appearing in the set is a fresh sever THIS tick —
            // spray the arterial fountain (unless we're just priming on load).
            var isNew = _severedZones.Add(zone);
            if (isNew && _severVfxPrimed)
            {
                SpawnSeverFountain(zone);
            }

            // Spec §50: a lost LEG means she can't stand — swap the fixed
            // standing/turn/crouch/idle clips for prone, and walk for crawl,
            // once (the dynamic action clips are gated in Standing()).
            if ((zone == "LegL" || zone == "LegR") && !_legless)
            {
                _legless = true;
                ApplyLeglessClipOverrides();
            }

            var bone = _bodyBones.GetBone(boneName);
            if (bone != null)
            {
                // Not exactly zero — a degenerate scale can NaN the skinning;
                // a tiny non-zero collapses the sub-tree below visible size.
                bone.localScale = new Vector3(1e-4f, 1e-4f, 1e-4f);
            }
        }

        _severVfxPrimed = true;
        if (_legless)
        {
            ApplyLeglessClipOverrides();
        }
    }

    // Spec §50: the one-time clip swaps for a legless survivor — the states that
    // carry a FIXED clip (idle, walk, crouch, turn-on-spot). Gather/Talk/Dress
    // re-apply their clip each frame, so those are handled in Standing() at the
    // call sites; Sit/Sleep/LieDown keep their own clips.
    private void ApplyLeglessClipOverrides()
    {
        // §71: ALL THREE gait slots become the crawl — a legless girl has one
        // speed, and filling every slot means whatever Gait the view computes
        // she keeps crawling and can never blend into a run.
        foreach (var gaitKey in GaitClipKeys)
        {
            OverrideClip(gaitKey, CrawlClip);
        }
        // Everything else standing → the prone idle. (The game leaves these base
        // clips in place — no NpcAnimSet action variants — so overriding the base
        // clip name here is what actually swaps them.)
        OverrideClip("Idle", ProneClip);
        OverrideClip("crouch", ProneClip);
        OverrideClip("TurnOnSpotRightB", ProneClip);
        OverrideClip("TurnOnSpotLeftA", ProneClip);
        OverrideClip("X Bot@Gathering Objects", ProneClip);
        OverrideClip("X Bot@Talking", ProneClip);
        OverrideClip("X Bot@Dressing", ProneClip);
        OverrideClip("X Bot@Drinking", ProneClip);           // she drinks lying too
    }

    // Spec §50: ONE gentle blood fountain at the cut — a softer version of the
    // wound splash that spurts and DRIPS for SeverFountainSeconds, raining
    // droplets to the ground (gravity), then subsides. Parented to the stump
    // bone so it rides the body as she crawls away.
    private void SpawnSeverFountain(string zone)
    {
        if (_bodyBones == null || !SeveredStumpBone.TryGetValue(zone, out var boneName))
        {
            return;
        }

        var bone = _bodyBones.GetBone(boneName);
        if (bone == null)
        {
            return;
        }

        var prefab = Rendering.BloodSplashVfx.Pick(zone.GetHashCode());
        if (prefab == null)
        {
            return;
        }

        var root = _bodyRoot != null ? _bodyRoot : transform;

        // Point the whole system straight DOWN from the cut, parented to the
        // stump bone so it tracks it. localScale 1 keeps the bone's actor scale.
        var vfx = Instantiate(prefab, bone.position,
            Quaternion.LookRotation(Vector3.down, root.forward), bone);
        vfx.transform.localScale = Vector3.one;

        foreach (var ps in vfx.GetComponentsInChildren<UnityEngine.ParticleSystem>(true))
        {
            ps.Stop(true, UnityEngine.ParticleSystemStopBehavior.StopEmitting);

            var main = ps.main;
            main.scalingMode = UnityEngine.ParticleSystemScalingMode.Hierarchy;
            main.loop = true;
            main.duration = SeverFountainSeconds;
            // Kill the omnidirectional spray: barely any launch speed, and let
            // strong gravity pull the droplets STRAIGHT DOWN from the cut so it
            // reads as blood running down / dripping, not a splash in all dirs.
            main.startSpeedMultiplier *= 0.12f;
            main.gravityModifier = Mathf.Max(main.gravityModifier.constant, 3f);

            // Narrow the emitter to a tight downward stream at the stump.
            var shape = ps.shape;
            if (shape.enabled)
            {
                shape.angle = Mathf.Min(shape.angle, 5f);
                shape.radius = Mathf.Min(shape.radius, 0.02f);
            }

            // A steady, moderate drip for the fountain's life; authored bursts
            // stay as the initial spurt (now also slow → they fall, not spray).
            var emission = ps.emission;
            emission.rateOverTime = SeverDripRate;

            ps.Play();
        }

        StartCoroutine(StopFountain(vfx, SeverFountainSeconds));
    }

    private static System.Collections.IEnumerator StopFountain(GameObject vfx, float after)
    {
        yield return new WaitForSeconds(after);
        if (vfx == null)
        {
            yield break;
        }

        foreach (var ps in vfx.GetComponentsInChildren<UnityEngine.ParticleSystem>(true))
        {
            ps.Stop(true, UnityEngine.ParticleSystemStopBehavior.StopEmitting);
        }

        Destroy(vfx, 4f); // let the last particles fall and fade
    }

    // Spec §50: the body skin a severed-limb drop bakes from — the skinned
    // renderer with the most bones (the actual body, not eyes/lashes/brows).
    public SkinnedMeshRenderer PrimaryBodySkin
    {
        get
        {
            if (_bodySkins == null)
            {
                return null;
            }

            // The Daz FIGURE body is the "Genesis…" skinned mesh — the only one
            // whose geometry covers arms and legs. The actor also carries hair
            // (far MORE verts, so vertex-count picks it wrong), eyes/eyelashes
            // (same bone count, so bone-count picks them wrong) and clothing —
            // so match the body by name.
            foreach (var skin in _bodySkins)
            {
                if (skin != null && skin.sharedMesh != null && skin.bones != null &&
                    skin.name.IndexOf("Genesis", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return skin;
                }
            }

            // Fallback: the most-detailed readable skin that isn't hair/lashes.
            SkinnedMeshRenderer best = null;
            var bestVerts = -1;
            foreach (var skin in _bodySkins)
            {
                if (skin == null || skin.sharedMesh == null || skin.bones == null ||
                    !skin.sharedMesh.isReadable)
                {
                    continue;
                }

                var n = skin.name.ToLowerInvariant();
                if (n.Contains("hair") || n.Contains("eyelash") || n.Contains("brow") || n.Contains("eye"))
                {
                    continue;
                }

                if (skin.sharedMesh.vertexCount > bestVerts)
                {
                    bestVerts = skin.sharedMesh.vertexCount;
                    best = skin;
                }
            }

            return best;
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

    public void SetActionTargetPoint(Vector3 worldPoint)
    {
        _actionTargetPoint = worldPoint;
        _actionTargetActive = true;
        if (_fullBodyIK != null)
        {
            ResetActionTargetIKWeights();
            _fullBodyIK.enabled = true;
        }
    }

    public void ClearActionTarget()
    {
        _actionTargetActive = false;
        ResetActionTargetIKWeights();
        if (_fullBodyIK != null)
        {
            _fullBodyIK.enabled = false;
        }
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
            // Trust BodyBones (the real render state), NOT just the cache. A
            // cached item whose visuals BodyBones no longer holds — its wear
            // state was wiped by a re-Construct, or an Equip that never
            // stitched — is re-equipped here. Without this repair a view↔sim
            // desync leaves the body stuck NUDE while the sim still lists the
            // garment (panel shows "Надето", model is bare) until WornItems
            // next changes. `count == 0` = a sim item with no visual prefab
            // (e.g. a necklace): nothing to render, already in sync — skip so
            // it never re-loads its (empty) visuals every frame.
            if (_equippedSimItems.TryGetValue(simId, out var count) &&
                (count == 0 || _bodyBones.IsEquipped($"{simId}#0")))
            {
                continue;
            }

            var prefabs = ActorWardrobe.GetVisuals(simId);
            for (var i = 0; i < prefabs.Count; i++)
            {
                _bodyBones.Equip($"{simId}#{i}", prefabs[i]); // no-op if already on the body
            }

            _equippedSimItems[simId] = prefabs.Count;

            // Spec 40.10-D guard: the repair above re-equips whatever the body is
            // missing. If the piece is STILL missing, another garment claims the
            // same visual (layer, slot) while the sim considers the two
            // compatible — they then evict each other every tick, and the wear
            // painter re-rolls its stains on each fresh instance, which reads as
            // the cloth's dirt flickering.
            // §52.9 — the fix direction was written BACKWARDS here and cost real
            // data (a shirt lost its wrist slots to match a coarse `Covers`).
            // The PREFAB is the authority on where a garment sits; the sim now
            // mirrors it in `WearSlotCatalog`. So the repair is: bring the SIM
            // table (and, if the layer itself is wrong, the garment's sim
            // WearLayer) into line with the prefab — never coarsen the prefab.
            if (prefabs.Count > 0 && !_bodyBones.IsEquipped($"{simId}#0") &&
                _slotClashWarned.Add(simId))
            {
                Debug.LogWarning(
                    $"[Wear] '{simId}' was evicted the instant it was equipped, by " +
                    $"{_bodyBones.DescribeSlotOwners(prefabs[0])} — their visual " +
                    "(layer, slot) collide while the sim allows both to be worn. " +
                    "Sync WearSlotCatalog (and the sim WearLayer) TO this prefab — " +
                    "do not coarsen the prefab's slots to match Covers.",
                    this);
            }
        }
    }

    // Spec §52.8: pin holstered tools to the leg. The worn holster prefab
    // carries empty child anchors named exactly by tool id (tool.axe_stone /
    // tool.knife / tool.hammer) under its thigh bones; every tool the sim parked
    // in a typed slot (holsteredIds) is shown snapped into its anchor at local
    // zero. The tool currently drawn in the acting hand (heldItemId) is skipped
    // so it never doubles — it shows on the thigh only when NOT in use. Props
    // are cached, so the anchor search runs on change, not every frame.
    public void SyncHolster(IReadOnlyList<string> holsteredIds, string heldItemId)
    {
        if (_bodyBones == null)
        {
            return;
        }

        _holsterWantScratch.Clear();
        if (holsteredIds != null)
        {
            for (var i = 0; i < holsteredIds.Count; i++)
            {
                var id = holsteredIds[i];
                if (!string.IsNullOrEmpty(id) && id != heldItemId)
                {
                    _holsterWantScratch.Add(id);
                }
            }
        }

        // Drop props that are no longer wanted: the tool moved to her hand, left
        // the pack, or the holster came off (its anchors died with it).
        _holsterRemoveScratch.Clear();
        foreach (var kv in _holsterProps)
        {
            if (!_holsterWantScratch.Contains(kv.Key))
            {
                _holsterRemoveScratch.Add(kv.Key);
            }
        }

        for (var i = 0; i < _holsterRemoveScratch.Count; i++)
        {
            var id = _holsterRemoveScratch[i];
            if (_holsterProps[id] != null)
            {
                Destroy(_holsterProps[id]);
            }

            _holsterProps.Remove(id);
        }

        if (_holsterWantScratch.Count == 0)
        {
            return;
        }

        foreach (var id in _holsterWantScratch)
        {
            if (_holsterProps.TryGetValue(id, out var existing) && existing != null)
            {
                continue; // already parked and alive
            }

            // The holster's leg bones are stitched onto the body skeleton, so its
            // tool.* anchors hang under SkeletonRoot; the wear container is a
            // fallback in case a build leaves them unstitched.
            var anchor = FindDescendantNamed(_bodyBones.SkeletonRoot, id)
                ?? FindDescendantNamed(_bodyBones.WearTransform, id);
            if (anchor == null)
            {
                continue; // holster not on the body (yet) — retry next frame
            }

            var model = Config.GearLibrary.LoadPrefab(id);
            GameObject prop;
            if (model != null)
            {
                prop = Instantiate(model, anchor);
            }
            else
            {
                prop = HexLive.UnityPresentation.Environment.LowPolyToolFactory.Build(id);
                if (prop == null)
                {
                    continue;
                }

                prop.transform.SetParent(anchor, false);
            }

            prop.name = $"HolsterProp {id}";
            // Size: the SAME world size the hand and the ground use (ObjectFit),
            // so a tool reads identical holstered, held and dropped. Position and
            // orientation are the authored anchor's — the tool sits at local 0.
            var fit = ObjectFit.FitScaleFactor(prop, id);
            prop.transform.localPosition = Vector3.zero;
            prop.transform.localRotation = Quaternion.identity;
            prop.transform.localScale = Vector3.one * fit;
            _holsterProps[id] = prop;
        }
    }

    private static Transform FindDescendantNamed(Transform root, string name)
    {
        if (root == null)
        {
            return null;
        }

        var all = root.GetComponentsInChildren<Transform>(true);
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].name == name)
            {
                return all[i];
            }
        }

        return null;
    }

    // Spec 31C.2: sleeping snaps the view to the bed's attach point and
    // plays the Laying state; waking releases back to the renderer's flow.
    public void SetLaying(bool laying, Transform attachPoint, float surfaceY = 0f)
    {
        if (_laying && !laying && _bodyRoot != null)
        {
            // Getting up: remember where the body actually lay so LateUpdate
            // can ease it back to the root instead of snapping in one frame.
            _wakeFromPos = _bodyRoot.position;
            _wakeFromRot = _bodyRoot.rotation;
            _wakeBlend = 1f;
        }
        else if (laying)
        {
            _wakeBlend = 0f; // lying back down cancels any in-flight ease
        }

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

    // Face pain: 0 none .. 1 writhing, from fresh bleeding wounds. Composited
    // into a wince over the mood (see NpcFaceAnimator.SetPain).
    public void SetFacePain(float pain01)
    {
        if (_face != null)
        {
            _face.SetPain(pain01);
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
        // §50: a corpse never crawls — clear the flag so the Crawl loop yields
        // to the death/laying pose (the Crawl transition also guards on !Dead).
        if (_animator != null)
        {
            _animator.SetBool(CrawlingParam, false);
        }

        // Random death clip (config) that plays once and holds on the last
        // frame (Death state has no exit; clip must be Loop Time OFF). Falls
        // back to the frozen laying pose when no death clips are configured.
        if (_animator != null && _animSet != null && _animSet.death != null && _animSet.death.Length > 0)
        {
            OverrideClip(DeathBaseClip, _animSet.death[Random.Range(0, _animSet.death.Length)]);
            _animator.SetBool(DeadParam, true);
            return;
        }

        SetLaying(true, null, surfaceY);
    }

    // §29C.3-hit: a standing damage stagger. The renderer feeds every snapshot's
    // Health here; a drop past the DoT noise floor (a real bite/strike, not the
    // slow sick/starve drain) fires the HitReact one-shot — but only while she
    // stands still: walking/swimming/lying bodies keep their own motion.
    private const float HitReactMinDrop = 0.02f;
    private float _lastSignaledHealth = -1f;

    public void SignalHealth(float health)
    {
        var previous = _lastSignaledHealth;
        _lastSignaledHealth = health;
        if (previous < 0f || health >= previous - HitReactMinDrop)
        {
            return;
        }

        if (_dead || _laying || _swimming || _ragdollActive || _wasWalking || _animator == null)
        {
            return;
        }

        _animator.SetTrigger(HitReactParam);
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

        if (active)
        {
            ClearActionTarget();
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
    public void SetInteraction(string interaction, string heldItemId, bool aidTargetLying = false)
    {
        if (_legless && IsToolOrWeapon(heldItemId))
        {
            heldItemId = string.Empty;
        }

        // Which imported full-body clip-state covers this verb, if any:
        //   gather (pick up / scoop / deposit at a build-site) and drink play
        //   real clips; Craft kneels into the planting-style CraftWork clip
        //   (§gear-craft v2); Harvest stays on the crouch "Working" state; Sit
        //   as-is. Build joined the gather set (§54.12): laying a bed piece
        //   reads as the stooping gather motion, not the generic crouch.
        var gathering = interaction is "PickUp" or "FillBottle" or "Fuel" or "Bury" or "Hang" or "Build";
        var drinking = interaction == "Drink";
        var crafting = !_legless && interaction == "Craft";
        // §53: tending a suffering housemate — the helper holds the mediator
        // item (feed → whole coconut, water → the pierced drink coconut;
        // treat/medicate/console tend bare-handed). She kneels into the
        // planting-style CraftWork clip ONLY when the ward is LYING DOWN
        // (coma/faint/asleep/prone); over a STANDING ward she just stands and
        // shows the item, exactly as before.
        var aidingOther = !_legless &&
            interaction is "FeedOther" or "HydrateOther" or
                           "TreatOther" or "MedicateOther" or "ConsoleOther";
        var aidPropId = interaction switch
        {
            "FeedOther" => "food.coconut",
            "HydrateOther" => "food.coconut_pierced",
            _ => string.Empty
        };
        // The solo craft always kneels; an aid kneels only over a lying ward.
        var kneelingCraft = crafting || (aidingOther && aidTargetLying);
        _wantsTalk = interaction == "Talk"; // the Talk bool is driven by turn-taking

        // Which procedural/clip action this verb wants (before touching the
        // animator, so the axe-chop clip-state can pre-empt the crouch Working pose).
        var actionKind = (gathering || drinking || kneelingCraft || _wantsTalk)
            ? ActionKind.None
            : ActionFromInteraction(interaction, heldItemId);
        // §axe: chopping/mining with an axe or pickaxe now plays the looping Chop
        // clip instead of the crouch Working pose + procedural shoulder swing.
        var chopping = actionKind == ActionKind.Chop && !_legless;
        // §67: рубящий звук выбирает инструмент — кирка о камень, нож о
        // кокос, топор о ствол (PollActionSounds бьёт его в такт клипу).
        _chopSfxId = !chopping ? string.Empty
            : heldItemId != null && heldItemId.Contains("pickaxe") ? Audio.FmodSfx.Sfx.MineStone
            : heldItemId != null && heldItemId.Contains("knife") ? Audio.FmodSfx.Sfx.ChopCoco
            : Audio.FmodSfx.Sfx.ChopWood;

        if (_animator != null)
        {
            _animator.SetBool(GatheringParam, gathering);
            _animator.SetBool(DrinkingParam, drinking);
            _animator.SetBool(WorkingParam, !_legless && !chopping &&
                interaction is "Harvest" or "BuildRaft");
            _animator.SetBool(CraftingParam, kneelingCraft);
            _animator.SetBool(ChoppingParam, chopping);
            _animator.SetBool(SittingParam, interaction == "Sit");
            // Clip source: config override if present, else the state's base clip.
            if (gathering && _animSet != null) OverrideClip("X Bot@Gathering Objects", Standing(_animSet.gather));
            if (drinking && _animSet != null) OverrideClip("X Bot@Drinking", Standing(_animSet.drink)); // legless drinks prone
        }

        // A full-body clip now covers these (incl. the axe swing and the craft
        // kneel) — suppress the procedural shoulder pose so it doesn't fight the
        // clip. Eat keeps its own raise-to-mouth — except legless, where the
        // prone idle carries eat/drink.
        _action = _legless || chopping || kneelingCraft
            ? ActionKind.None
            : actionKind;
        // A solo craft puts both hands to work (tool goes down). An aid keeps
        // the mediator prop in hand (coconut to feed/water; empty to treat/
        // console). Everything else holds whatever the sim says.
        SetHandProp(crafting ? string.Empty
            : aidingOther ? aidPropId
            : heldItemId);
    }

    // §Wardrobe-anim: drive the two-beat dress/undress sequence. Called every
    // snapshot AFTER SetInteraction (which it overrides for these verbs).
    //   Dress   beat A (progress < handoff): gather pose, empty hands, the
    //           garment still lies on the ground (renderer hides it in beat B);
    //           beat B: don clip + the garment carried in hand; on completion
    //           the sim's WornItems gains it and SyncWorn puts it on the body.
    //   Undress beat A: doff clip while the piece is still worn (SyncWorn shows
    //           it); at the handoff the sim moves it off the body into the hand;
    //           beat B: gather pose + the garment in hand; on completion the sim
    //           drops it and the renderer spawns the ground garment.
    // garmentId is the snapshot's HeldGarmentId (empty until the piece is in
    // hand). Progress is the sim interaction fraction 0..1.
    public void SetWardrobeAction(string interaction, float progress, string garmentId,
        float garmentDurability = 1f, float garmentDirt = 0f, float garmentBlood = 0f,
        float garmentWet = 0f)
    {
        var dressing = interaction == "Dress";
        var undressing = interaction == "Undress";
        var washing = interaction == "WashClothes";
        if (!dressing && !undressing && !washing)
        {
            if (_animator != null)
            {
                _animator.SetBool(DressingParam, false);
                _animator.SetBool(UndressingParam, false);
            }

            SetHandGarment(null);
            return;
        }

        var afterHandoff = progress >= WardrobeHandoffFraction;
        // Dress: gather then don. Undress: doff then gather.
        var showGather = washing || (dressing ? !afterHandoff : afterHandoff);
        var showDon = dressing && afterHandoff;
        var showDoff = undressing && !afterHandoff;
        var showGarment = (washing || afterHandoff) && !string.IsNullOrEmpty(garmentId);

        if (_animator != null)
        {
            _animator.SetBool(GatheringParam, showGather);
            _animator.SetBool(DressingParam, showDon);
            _animator.SetBool(UndressingParam, showDoff);
            // Clip source: config override if present, else the state's base clip.
            if (showGather && _animSet != null && _animSet.gather != null)
                OverrideClip("X Bot@Gathering Objects", Standing(_animSet.gather));
            if (showDon && _animSet != null && _animSet.dress != null)
                OverrideClip(DressBaseClip, Standing(_animSet.dress));
            if (showDoff && _animSet != null && _animSet.undress != null)
                OverrideClip(UndressBaseClip, Standing(_animSet.undress));
        }

        // Full-body clips own the pose here — kill the procedural action arm.
        _action = ActionKind.None;
        SetHandGarment(showGarment ? garmentId : null);
        // §40.6 r2 (laundry-in-hand): the hand prop mirrors the live item
        // condition, so the dirt visibly washes OUT of the piece as she scrubs.
        _handGarmentCondition?.Sync(garmentDurability, garmentDirt, garmentBlood, garmentWet);
    }

    private GameObject _handGarment;
    private string _handGarmentId;
    private GarmentWorldCondition _handGarmentCondition;

    // §Wardrobe-anim: a folded-garment prop in the acting hand (the real cloth
    // mesh, reusing the ground-drop builder). Separate from the tool _handProp
    // so a wardrobe action and a held tool never clobber each other.
    private void SetHandGarment(string garmentId)
    {
        if (_handGarmentId == garmentId)
        {
            return;
        }

        _handGarmentId = garmentId;
        if (_handGarment != null)
        {
            Destroy(_handGarment);
            _handGarment = null;
            _handGarmentCondition = null;
        }

        if (string.IsNullOrEmpty(garmentId) || _bodyBones == null)
        {
            return;
        }

        var hand = _bodyBones.GetBone(_leftHanded ? "lHand" : "rHand");
        if (hand == null)
        {
            return;
        }

        var built = GarmentDropFactory.Build(garmentId);
        if (built == null)
        {
            return;
        }

        built.transform.SetParent(hand, false);
        _handGarment = built;
        _handGarment.name = $"HandGarment {garmentId}";

        // Normalize to a palm-sized folded bundle regardless of the mesh size.
        var renderers = _handGarment.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            var biggest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            var target = 1.7f * _bodyRoot.lossyScale.y * 0.18f;
            if (biggest > 0.0001f)
            {
                _handGarment.transform.localScale *= target / biggest;
            }
        }

        _handGarment.transform.localPosition = Vector3.zero;
        _handGarment.transform.localRotation = Quaternion.identity;

        // §40.6 r2 (laundry-in-hand): the same tear/dirt/blood/wet paint the
        // ground drops use, driven per snapshot from the held item's condition.
        _handGarmentCondition = _handGarment.AddComponent<GarmentWorldCondition>();
        _handGarmentCondition.Construct(_handGarment);
    }

    // Spec 28.15E: the overhead chat bubble. The renderer pushes the current
    // conversation topic every snapshot ("" = not talking → the bubble hides),
    // and fires a one-shot "+/-" relationship pop when a talk resolves.
    // §67.10: the bubble is no longer driven from here directly — every
    // utterance (chat turn, complaint, scream) goes through the speech director,
    // which plays the voice and shows the matching picture in one call.
    private NpcSpeechBubble _speechBubble;
    private UI.NpcSpeechDirector _speech;

    public void SetTalkTopic(string topicName)
    {
        EnsureSpeechBubble();
        _speech?.SetConversationTopic(topicName);
    }

    // §67.10: her body, for the ambient self-talk layer (hungry/parched/cold…).
    // Pushed every snapshot; the director decides if and when she says anything.
    public void SetSpeechState(in UI.SpeechCatalog.BodyState state)
    {
        EnsureSpeechBubble();
        _speech?.SetState(state);
    }

    // §67.10: the verb she is doing right now — a work beat on the rising edge.
    public void SetSpeechInteraction(string interaction)
    {
        EnsureSpeechBubble();
        _speech?.OnInteraction(interaction);
    }

    /// <summary>§67.10: make her say a catalog line (voice + bubble + face).</summary>
    public bool Say(string speechId)
    {
        EnsureSpeechBubble();
        return _speech != null && _speech.Say(speechId);
    }

    public void PopRelationship(float affinityDelta)
    {
        EnsureSpeechBubble();
        _speechBubble?.PopRelationship(affinityDelta);

        // §67.6/§67.10: исход беседы озвучивается той же эмоцией, что и «+/-»
        // поп — ссора злит, удачный разговор радует, и в бабле висит своя
        // картинка. Не каждый раз, чтобы финалы бесед не превращались в хор.
        if (Random.value < 0.6f)
        {
            Say(affinityDelta < 0f ? "angry_bond_minus" : "happy_bond_plus");
        }
    }

    // §77: portrait — лицо того, о ком кьюшка (страх перед конкретным
    // человеком). Голос от него не зависит: реплика привязана к ВИДУ кьюшки.
    public void PopSocialCue(string cueKind, Sprite portrait = null)
    {
        EnsureSpeechBubble();
        _speechBubble?.PopSocialCue(cueKind, portrait);
        _speech?.OnCue(cueKind);
    }

    private void EnsureSpeechBubble()
    {
        // Anchor to the head bone (calibrated in Construct); until it exists the
        // NPC can't have started talking yet, so deferring is harmless.
        if (_speechBubble != null || _headBone == null)
        {
            return;
        }

        var go = new GameObject("SpeechBubble");
        go.transform.SetParent(transform, false);
        _speechBubble = go.AddComponent<NpcSpeechBubble>();
        _speechBubble.Initialize(_headBone, 5000 + _npcId * 4);
        _speech = new UI.NpcSpeechDirector(this);
    }

    // ---- §67.10 ISpeechStage: the mouth the director drives ----------------

    float UI.ISpeechStage.PlayVoiceLine(string speechId)
        => PlayVoiceLine(speechId, null, speechId == "hurt_wound" || speechId == "hurt_bitten"
            ? Audio.FmodSfx.Sfx.HurtF
            : speechId == "hurt_death" ? Audio.FmodSfx.Sfx.DeathF : null);

    void UI.ISpeechStage.StopVoiceLine() => Audio.FmodSfx.StopLoop(ref _voiceChannel);

    void UI.ISpeechStage.ShowSpeechIcon(string iconKey, float seconds)
        => _speechBubble?.ShowIcon(iconKey, seconds);

    void UI.ISpeechStage.HideSpeechIcon() => _speechBubble?.HideIcon();

    // The death cry is the one line a corpse is allowed (it IS the death), and
    // fast-forward stays mute so a 8× catch-up doesn't shout.
    bool UI.ISpeechStage.CanSpeak(bool alarm)
        => (alarm || !_dead) && _simSpeed <= 4.01f;

    // §armed-stance: while ANY tool/weapon (tool.*) is in the hand, the base Idle
    // and Walk clips are swapped for weapon-ready versions (NpcAnimSet.armedIdle /
    // .armedWalk); empty hand or a non-tool restores the defaults. Standing() keeps
    // a legless NPC on the prone idle; Walk is left to the crawl override for the
    // legless (it re-applies "Walk"->CrawlClip every frame and wins). Applied on
    // prop change (SetHandProp early-returns when the id is unchanged).
    private void UpdateArmedStance(string itemId)
    {
        if (_animSet == null)
        {
            return;
        }

        var armed = !string.IsNullOrEmpty(itemId) &&
                    itemId.StartsWith("tool.", System.StringComparison.Ordinal);

        // Per-gear SO clips first; NpcAnimSet's shared armed set as fallback.
        var gearIdle = Config.GearLibrary.ArmedIdleFor(itemId);
        var gearWalk = Config.GearLibrary.ArmedWalkFor(itemId);
        var idleClip = gearIdle != null ? gearIdle : _animSet.armedIdle;
        var walkClip = gearWalk != null ? gearWalk : _animSet.armedWalk;

        if (idleClip != null)
        {
            if (armed)
            {
                OverrideClip("Idle", Standing(idleClip));
            }
            else if (_clipsByName.TryGetValue("Idle", out var baseIdle))
            {
                OverrideClip("Idle", Standing(baseIdle)); // legless stays prone
            }
        }

        // Walk swap is skipped while legless — the crawl system owns "Walk".
        if (walkClip != null && !_legless)
        {
            if (armed)
            {
                OverrideClip("Walk", walkClip);
            }
            else if (_clipsByName.TryGetValue("Walk", out var baseWalk))
            {
                OverrideClip("Walk", baseWalk);
            }
        }

        // Work clip (рубка/добыча/стройка): the gear SO can swap the Chop
        // state's clip; restore the base take when the item declares none.
        var workClip = Config.GearLibrary.WorkClipFor(itemId);
        if (workClip != null)
        {
            OverrideClip(ChopBaseClip, workClip);
        }
        else if (_clipsByName.TryGetValue(ChopBaseClip, out var baseChop))
        {
            OverrideClip(ChopBaseClip, baseChop);
        }
    }

    // §NPC-anim: swap the clip a base-clip key plays, via the override controller.
    private void OverrideClip(string baseName, AnimationClip with)
    {
        if (_animOverride == null || with == null ||
            !_clipsByName.TryGetValue(baseName, out var baseClip))
        {
            return;
        }
        _animOverride[baseClip] = with;
    }

    // §NPC-anim: turn-taking talk. While the sim has this NPC socialising,
    // alternate speaking turns with its partner — staggered by npcId over a
    // shared clock, so one speaks a beat while the other listens, then swap —
    // and pick a random talk clip each turn. A short natural back-and-forth,
    // entirely view-side (the sim has no per-turn talk signal).
    private void UpdateTalkTurns()
    {
        if (_animator == null)
        {
            return;
        }

        var on = _wantsTalk && (((int)(Time.time / TalkTurnSeconds) + _npcId) & 1) == 0;
        if (on && !_talkTurnOn && _animSet != null && _animSet.talk != null && _animSet.talk.Length > 0)
        {
            // §50: legless → she talks lying (prone idle), not standing gestures.
            OverrideClip(TalkBaseClip, Standing(_animSet.talk[Random.Range(0, _animSet.talk.Length)]));
        }

        // §67.6/§67.10: моя очередь говорить — реплика ПО ТЕМЕ, которую сим
        // выбрал для меня (голод/жажда/рана/костёр/шутка…), и в бабле висит её
        // же картинка. Тема своя у каждой — беседа читается как обмен.
        if (on && !_talkTurnOn)
        {
            _speech?.OnTalkTurn();
        }

        _talkTurnOn = on;
        _animator.SetBool(TalkingParam, on);
    }

    private string _voiceChar = string.Empty;
    private Audio.FmodSfx.Loop _voiceChannel;
    private Audio.NpcVoiceLipSync _voiceLipSync;

    // §67.10: сыграть реплику каталога. Три ступени фолбэка, чтобы каталог
    // можно было наполнять по одной группе, ничего не ломая:
    //   1. voice_<char>_<emotion>_<slug> — записанная реплика под этот повод;
    //   2. voice_<char>_<emotion>        — общий банк эмоции (§67.6);
    //   3. общий sfx (вскрик боли/смерти) — если голоса нет вовсе.
    // Возврат: длительность в секундах (0 = ничего не прозвучало). Бабл всё
    // равно покажет картинку — визуальная половина работает до озвучки.
    private float PlayVoiceLine(string speechId, Vector3? at = null, string fallbackId = null)
    {
        if (_voiceChar.Length == 0)
        {
            return 0f;
        }

        // Одна реплика за раз на персонажа (иначе хор из одного рта).
        if (Audio.FmodSfx.IsPlaying(ref _voiceChannel))
        {
            return 0f;
        }

        var pos = at ?? (TryGetBodyCenter(out var center) ? center : transform.position);
        var emotion = UI.SpeechCatalog.EmotionOf(speechId);
        var id = "voice_" + _voiceChar + "_" + speechId;
        if (!Audio.FmodSfx.HasSound(id))
        {
            id = "voice_" + _voiceChar + "_" + emotion;
        }

        if (Audio.FmodSfx.HasSound(id))
        {
            _voiceChannel = Audio.FmodSfx.PlayTracked(id, pos);
            // §67.7: рот проговаривает реплику (uLipSync по PCM файла).
            if (_voiceLipSync != null)
            {
                _voiceLipSync.Speak(ref _voiceChannel);
            }

            var lengthMs = Audio.FmodSfx.GetLengthMs(ref _voiceChannel);
            // §67.8: лицо держит эмоцию реплики, пока она звучит. Боль
            // (hurt) не трогаем — гримасу уже ведёт wound-канал SetPain.
            if (_face != null && emotion != "hurt")
            {
                _face.FlashTalkEmotion(emotion, lengthMs > 0 ? lengthMs / 1000f : 2.5f);
            }

            return lengthMs > 0 ? lengthMs / 1000f : 0f;
        }

        if (fallbackId != null)
        {
            Audio.FmodSfx.Play(fallbackId, pos);
        }

        return 0f;
    }

    // Iter 28: the renderer flags a ledge sit (sim IsLedgeSit) so LateUpdate
    // lifts the body onto the upper step instead of pinning it to the root.
    // stepsUp = how many steps her butt must rise to reach the seat surface;
    // 0 = she's already on the higher tile (a land/water rim), so NO lift —
    // she sits right on her own edge instead of floating above it.
    public void SetLedgeSit(bool ledgeSit, int stepsUp = 1, bool washAtShore = false)
    {
        _ledgeSit = ledgeSit;
        _ledgeSeatStepsUp = stepsUp;
        _washAtShore = washAtShore;
    }

    private int _ledgeSeatStepsUp = 1;
    // §40.6: shore washing stands at the BANK (water now laps just below it) —
    // NOT the sit-on-rim dangle (LedgeSeatLift is negative, tuned to swing the
    // feet over the old low water and would sink the washer into the raised
    // sea). Kept as its own knob so the crouch height can be tuned apart.
    private bool _washAtShore;
    public static float WashSeatLift = 0f;

    private static ActionKind ActionFromInteraction(string interaction, string heldItemId)
    {
        switch (interaction)
        {
            case "Harvest":
                var gear = HexLive.Simulation.Content.GearCatalog.For(heldItemId);
                var chopping = gear.Id == heldItemId &&
                    (gear.Has(HexLive.Simulation.Content.GearCapability.ChopWood) ||
                     gear.Has(HexLive.Simulation.Content.GearCapability.Mine));
                return chopping ? ActionKind.Chop : ActionKind.Work;
            case "Process": // spec §54: splitting a log — an axe chop motion
                return ActionKind.Chop;
            case "Butcher": // spec §54: knifing a carcass — a crouched working motion
                return ActionKind.Work;
            case "PickUp":
            case "BuildRaft":
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

    private static bool IsToolOrWeapon(string itemId) =>
        !string.IsNullOrEmpty(itemId) &&
        itemId.StartsWith("tool.", System.StringComparison.Ordinal);

    // Spec 20.16: combat/hunt overrides the idle interaction — the weapon
    // appears in hand and drives a draw (bow) or thrust (spear) motion.
    // Timed melee: the SIM owns the attack cadence. `swinging` is true across
    // the whole attack-animation window (GearCatalog.AttackDurationSeconds
    // from the swing start); the clip/procedural swing plays exactly then, the
    // damage lands mid-window (HitDelaySeconds, sim-side), and between swings
    // she stands recovering — no more view-local attack timer drifting out of
    // sync with the actual blows.
    // strikeIndex: the sim's picked strike variant for THIS swing (fists:
    // punches/kicks — GearConfig.strikes order); -1 = single-timing gear,
    // the view rolls a random clip like before.
    public void SetCombat(bool fighting, string weaponId, bool swinging, int strikeIndex = -1)
    {
        if (_legless)  // §50-prone: lying — no weapons, no fight pose
        {
            fighting = false;
            weaponId = null;
            SetHandProp(null);
        }

        if (!fighting)
        {
            _wasFighting = false;
            _wasSwinging = false;
            _attackSpeed = 1f;
            _combatWeaponId = null;
            _action = ActionKind.None;
            if (_bareStance)
            {
                // Fight over: drop the boxing stance, restore the prop-driven idle.
                _bareStance = false;
                UpdateArmedStance(_currentPropId);
            }

            return;
        }

        var weaponChanged = _combatWeaponId != weaponId;
        if (!_wasFighting || weaponChanged)
        {
            _actionPhase = 0f;
            _combatWeaponId = weaponId;
        }

        // Кулачная стойка: bare-handed fighting swaps the Idle for the fist
        // config's armedIdle (Boxing Stance) — hands up between swings. A
        // weapon appearing mid-fight hands the idle back to the prop stance.
        var bareHanded = string.IsNullOrEmpty(weaponId);
        if (bareHanded && !_bareStance)
        {
            var stance = Config.GearLibrary.ArmedIdleFor(string.Empty);
            if (stance != null)
            {
                OverrideClip("Idle", Standing(stance));
                _bareStance = true;
            }
        }
        else if (!bareHanded && _bareStance)
        {
            _bareStance = false;
            UpdateArmedStance(_currentPropId);
        }

        // Attack clips: the gear SO first (data-driven), the NpcAnimSet
        // weapon row as fallback — empty both = the procedural swing below.
        // Fists are the empty-id gear sheet (fists.asset, gearId "") — a null
        // weapon still finds its strike clips.
        var attackClips = Config.GearLibrary.AttackClipsFor(weaponId ?? string.Empty);
        if (attackClips == null)
        {
            var wa = _animSet != null ? _animSet.WeaponFor(weaponId) : null;
            attackClips = wa != null && wa.attacks != null && wa.attacks.Length > 0
                ? wa.attacks
                : null;
        }

        if (attackClips != null && _animator != null)
        {
            // Clip-based attack: fire the one-shot at the sim's swing start.
            // The sim's strike pick (fists: which punch/kick) wins; -1 or a
            // stale index = the old random roll.
            if (swinging && !_wasSwinging)
            {
                var clip = strikeIndex >= 0 && strikeIndex < attackClips.Length
                    ? attackClips[strikeIndex]
                    : null;
                clip ??= attackClips[Random.Range(0, attackClips.Length)];
                if (clip != null)
                {
                    OverrideClip(AttackBaseClip, clip);
                    _animator.SetTrigger(AttackParam);
                }

                // §67: вжух в начале замаха; сам удар озвучит DogFight-событие.
                if (_simSpeed <= 4.01f)
                {
                    Audio.FmodSfx.Play(Audio.FmodSfx.Sfx.Swing, transform.position);
                }
            }

            _action = ActionKind.None;
        }
        else if (swinging)
        {
            // No config row -> the procedural swing/draw/thrust, paced so ONE
            // full swing spans the sim's attack window (knife: 2 s).
            _action = weaponId == "tool.bow" ? ActionKind.BowDraw
                : weaponId == "tool.spear" ? ActionKind.SpearThrust
                : ActionKind.Attack;
            if (!_wasSwinging)
            {
                _actionPhase = 0f;
                // §67: процедурный замах тоже свистит.
                if (_simSpeed <= 4.01f)
                {
                    Audio.FmodSfx.Play(Audio.FmodSfx.Sfx.Swing, transform.position);
                }
            }

            var duration = Mathf.Max(0.25f,
                HexLive.Simulation.Content.GearCatalog.AttackDurationSeconds(weaponId));
            _attackSpeed = 1f / (AttackSwingCycles * duration);
        }
        else
        {
            // Between swings: arms rest — she stands out the recovery.
            _action = ActionKind.None;
        }

        _wasSwinging = swinging;
        _wasFighting = true;
        if (!string.IsNullOrEmpty(weaponId))
        {
            SetHandProp(weaponId);
        }
    }

    private void SetHandProp(string itemId)
    {
        if (_legless && IsToolOrWeapon(itemId))
        {
            itemId = null;
        }

        if (_currentPropId == itemId)
        {
            return;
        }

        _currentPropId = itemId;
        UpdateArmedStance(itemId);
        if (_handProp != null)
        {
            Destroy(_handProp);
            _handProp = null;
        }
        _handPropRenderers = new Renderer[0];

        if (string.IsNullOrEmpty(itemId) || _bodyBones == null)
        {
            return;
        }

        var hand = _bodyBones.GetBone(_leftHanded ? "lHand" : "rHand");
        if (hand == null)
        {
            return;
        }

        // Real prefab first; otherwise a procedural low-poly model so tools
        // are visible in hand (spec 20.16 — no prefab wiring required).
        var model = Config.GearLibrary.LoadPrefab(itemId);
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
        _handPropRenderers = _handProp.GetComponentsInChildren<Renderer>();

        // Size: normalize to the SAME world size the ground uses (ObjectFit), so a
        // tool/coconut is identical in hand and on the ground. The gear asset's
        // hand scale is a fine MULTIPLIER on top of this (default 1), not absolute.
        var fit = ObjectFit.FitScaleFactor(_handProp, itemId);

        // Placement priority (each higher tier wins): the gear asset's tuned
        // hand pose (edited live in the AxeChopTest scene) → an "AttachPoint"
        // child authored into the model → a built-in default table → the
        // automatic palm-fit below. All in the acting hand's local space.
        if (Config.GearLibrary.TryGetHandPose(
                itemId, _leftHanded, out var cfgPos, out var cfgRot, out var cfgScale))
        {
            _handProp.transform.localPosition = cfgPos;
            _handProp.transform.localRotation = cfgRot;
            _handProp.transform.localScale = cfgScale * fit; // config scale = multiplier
            return;
        }

        // §54.12: RESOURCES (stick / leaf / log / fiber / rope…) ride in hand
        // 1:1 — the prefab's NATIVE scale, exactly the size of the same piece
        // on the ground or in the assembled bed, no palm-fit enlargement. One
        // shared grip transform for all of them (dialed in the inspector on
        // the stick), mirrored for the off hand. Tools and weapons are NOT
        // touched — they keep their tuned gear-asset placements above.
        if (itemId.StartsWith("resource.", System.StringComparison.Ordinal))
        {
            var gripPos = new Vector3(0.069f, -0.063f, 0.007f);
            var gripRot = Quaternion.Euler(0f, -95.855f, 0f);
            if (_leftHanded)
            {
                gripPos.x = -gripPos.x;
                var e = gripRot.eulerAngles;
                gripRot = Quaternion.Euler(e.x, -e.y, -e.z);
            }

            _handProp.transform.localPosition = gripPos;
            _handProp.transform.localRotation = gripRot;
            // localScale stays as instantiated (the prefab/factory's own) — 1:1.
            return;
        }

        if (TryAlignByAttachPoint(_handProp))
        {
            return;
        }

        // Hand-tuned placement for specific props (baked in code) wins over the
        // automatic palm-fit — exact position/rotation/scale in the hand's space.
        if (TryGetHandPropTransform(itemId, _leftHanded, out var tunedPos, out var tunedRot, out var tunedScale))
        {
            _handProp.transform.localPosition = tunedPos;
            _handProp.transform.localRotation = tunedRot;
            _handProp.transform.localScale = tunedScale;
            return;
        }

        // No tuned placement: use the shared ObjectFit size (same as the ground)
        // so e.g. a picked-up coconut is the same size it was lying on the ground.
        _handProp.transform.localScale = Vector3.one * fit;
        _handProp.transform.localPosition = Vector3.zero;
        _handProp.transform.localRotation = Quaternion.identity;
    }

    // Spec 40.x: switch the acting hand at runtime — props, the one-handed
    // action CLIPS (via the Animator's MirrorAction bool → Humanoid mirror) and
    // the procedural action arm all follow. Called by the sim when handedness
    // changes (a lefty, or an NPC who lost the right hand). Right-handed default.
    public void SetHandedness(bool leftHanded)
    {
        if (_leftHanded == leftHanded)
        {
            return;
        }

        _leftHanded = leftHanded;

        if (_animator != null)
        {
            _animator.SetBool(MirrorActionParam, leftHanded);
            _animator.SetBool(MirrorActionInvParam, !leftHanded);
        }

        // Re-seat whatever is currently held into the new hand.
        var held = _currentPropId;
        _currentPropId = null;
        SetHandProp(held);
    }

    // Attach-point authoring: if the model carries a direct child transform
    // named "AttachPoint", seat the prop so that point lands at the hand origin
    // (identity). Author the AttachPoint at the item's hand scale — no palm-fit
    // is applied on this path. Returns false when there is no such child.
    private static bool TryAlignByAttachPoint(GameObject prop)
    {
        Transform attach = null;
        foreach (Transform child in prop.transform)
        {
            if (child.name == "AttachPoint")
            {
                attach = child;
                break;
            }
        }

        if (attach == null)
        {
            return false;
        }

        var localPos = attach.localPosition;
        var propRot = Quaternion.Inverse(attach.localRotation);
        prop.transform.localScale = Vector3.one;
        prop.transform.localRotation = propRot;
        prop.transform.localPosition = -(propRot * localPos);
        return true;
    }

    // Per-prop hand placement, tuned in the editor and baked here. Baked values
    // are RIGHT-hand (rHand local space). For a left-handed hold we use a
    // hand-tuned left override if one exists, else mirror the right-hand pose
    // across the body's sagittal plane (negate local X + mirror the rotation).
    private static bool TryGetHandPropTransform(string itemId, bool leftHanded,
        out Vector3 localPosition, out Quaternion localRotation, out Vector3 localScale)
    {
        switch (itemId)
        {
            case "tool.bottle":
                localPosition = new Vector3(0.0543f, -0.0236f, -0.051f);
                localRotation = Quaternion.Euler(91.974f, 0.001007f, -6.520996f);
                localScale = new Vector3(0.349494f, 0.349494f, 0.349494f);
                break;
            case "food.coconut":
                localPosition = new Vector3(0.061f, -0.142f, 0.001f);
                localRotation = Quaternion.Euler(0.808f, 0f, 0f);
                localScale = new Vector3(0.7718072f, 0.9210232f, 0.7718072f);
                break;
            case "food.coconut_pierced":
                localPosition = new Vector3(0.09f, -0.111f, -0.089f);
                localRotation = Quaternion.Euler(-24.896f, 16.767f, 16.891f);
                localScale = new Vector3(0.7718072f, 0.9210232f, 0.7718072f);
                break;
            case "food.coconut_open":
                localPosition = new Vector3(0.041f, -0.107f, -0.034f);
                localRotation = Quaternion.Euler(1.22f, 5.477f, 32.526f);
                localScale = new Vector3(0.7718072f, 0.9210232f, 0.7718072f);
                break;
            case "tool.spear":
                localPosition = new Vector3(0.058f, -0.025f, -0.078f);
                localRotation = Quaternion.Euler(-1.544f, -263.963f, 90.255f);
                localScale = new Vector3(0.405947f, 0.405947f, 0.405947f);
                break;
            default:
                localPosition = Vector3.zero;
                localRotation = Quaternion.identity;
                localScale = Vector3.one;
                return false;
        }

        if (leftHanded && !TryGetLeftHandPropTransform(itemId, ref localPosition, ref localRotation))
        {
            localPosition = new Vector3(-localPosition.x, localPosition.y, localPosition.z);
            var e = localRotation.eulerAngles;
            localRotation = Quaternion.Euler(e.x, -e.y, -e.z);
        }

        return true;
    }

    // Hand-tuned LEFT-hand placements (lHand local space). Add a case here once a
    // prop is tuned for the off hand; anything missing falls back to a mirror.
    private static bool TryGetLeftHandPropTransform(string itemId,
        ref Vector3 localPosition, ref Quaternion localRotation)
    {
        switch (itemId)
        {
            case "tool.bottle":
                localPosition = new Vector3(-0.196f, -0.032f, -0.024f);
                localRotation = Quaternion.Euler(91.974f, 0.001007f, -6.520996f);
                return true;
            default:
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

        var model = Config.GearLibrary.LoadPrefab(itemId);
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
            var right = _bodyRoot.right;
            var ph = _thermalPhase * ShiverTuning.Frequency;

            // Whole-body huddle: the upper spine curls forward and the head
            // tucks in, each with its own slow tremble so the shiver reads as a
            // body, not a hinge. (Chest/head bones are oriented so a POSITIVE
            // rotation around the body's right axis pitches them forward — the
            // opposite sign to the arm bones.)
            if (_thermalChest != null)
            {
                var t = Mathf.Sin(ph * 0.9f + 1.3f) * ShiverTuning.SpineTremble * intensity;
                _thermalChest.rotation =
                    Quaternion.AngleAxis((ShiverTuning.SpineCurl * intensity) + t, right) * _thermalChest.rotation;
            }
            if (_thermalHead != null)
            {
                var t = Mathf.Sin(ph * 1.1f + 2.1f) * ShiverTuning.HeadTremble * intensity;
                _thermalHead.rotation =
                    Quaternion.AngleAxis((ShiverTuning.HeadTuck * intensity) + t, right) * _thermalHead.rotation;
            }

            // Arms KEEP their idle pose — no hug, no raise. Only a light tremble
            // is layered on, same as the rest of the body. Each joint runs on
            // its own phase so the two sides don't shake as one unit.
            _rShldr.rotation =
                Quaternion.AngleAxis(Mathf.Sin(ph) * ShiverTuning.ShoulderTremble * intensity, right) * _rShldr.rotation;
            if (_rForearm != null)
            {
                _rForearm.rotation =
                    Quaternion.AngleAxis(Mathf.Sin(ph + 1.0f) * ShiverTuning.ForearmTremble * intensity, right) * _rForearm.rotation;
            }
            if (_lShldr != null)
            {
                _lShldr.rotation =
                    Quaternion.AngleAxis(Mathf.Sin(ph + 0.7f) * ShiverTuning.ShoulderTremble * intensity, right) * _lShldr.rotation;
            }
            if (_lForearm != null)
            {
                _lForearm.rotation =
                    Quaternion.AngleAxis(Mathf.Sin(ph + 1.7f) * ShiverTuning.ForearmTremble * intensity, right) * _lForearm.rotation;
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

        // Spec 40.9: a leg wound swaps the Walk cycle for the imported Limp clip.
        // Spec §50: a LOST leg is handled by the AnimatorOverrideController
        // instead (Idle→prone, Walk→crawl, standing actions→prone) — so the
        // normal states play as usual and Sit/Sleep/Drink keep their own clips.
        // The old dedicated Crawl state is NOT used (Crawling stays off), so it
        // can never hijack sitting/sleeping/drinking.
        if (_animator != null)
        {
            _animator.SetBool(CrawlingParam, false);
            _animator.SetBool(LimpingParam, _posture == "Limp");
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

        _lastSplashTime = Time.time;
        var root = _bodyRoot != null ? _bodyRoot : transform;
        var outward = bone.position - root.position;
        outward.y = 0f;
        if (outward.sqrMagnitude < 0.0001f)
        {
            outward = root.forward;
        }

        // The pack is authored for a full-size human; our actors are ~0.35
        // scale — the shared spawner scales sizes AND velocities together.
        Rendering.BloodSplashVfx.SpawnHitSplash(
            bone.position, outward, root.lossyScale.y, splashSeed);

        // §67/§67.10: свежая рана — вскрик, личный для персонажа (§67.6), с
        // общим женским фолбэком, и бабл с раной/кровью над головой. Alarm-ранг
        // перебивает болтовню; гейт 0.4 s от брызг не даёт хора из одного рта.
        Say("hurt_wound");
    }

    // Spec 40.7: paint the bare skin from tan (0..1) and acute sunburn (0..1).
    // Tan multiplies the skin toward a weathered brown; sunburn layers red on
    // top. Values are the exported Needs.TanLevel / Needs.Sunburn. First pass —
    // tune the target colours against a screenshot; if the skin shader isn't
    // URP _BaseColor the property block is a harmless no-op.
    public void SetSkinWeathering(float tanLevel, float sunburn, float hurt = 0f, float hygiene = 1f)
    {
        // Spec 40.7: tanning eases IN — pale skin warms gently at the low end,
        // then deepens toward a MID warm-brown at full tan. Two things changed
        // on user note ("они слишком чёрные, и по низу пусть дольше/слабее"):
        //   • the low end is softer and slower — the warm phase now needs tan
        //     up to 0.5 (was 0.35) to fully arrive, so a fresh tan barely reads;
        //   • the full-tan target is much LIGHTER (was 0.40,0.27,0.18 ≈ near
        //     black on skin) so a maxed tan reads "bronzed/weathered", not dark.
        var tan = Mathf.Clamp01(tanLevel);
        var redPhase = Mathf.Clamp01(tan / 0.5f);
        var brownPhase = Mathf.Clamp01((tan - 0.5f) / 0.5f);
        var tint = Color.Lerp(Color.white, new Color(0.90f, 0.75f, 0.66f), redPhase);
        tint = Color.Lerp(tint, new Color(0.66f, 0.50f, 0.38f), brownPhase);
        // Overall tan DARKNESS knob — CharacterBalance.asset (tanStrength) →
        // SimBalance.TanStrength. Scales the whole tan back toward bare skin, so
        // темноту можно крутить из конфига без пересборки (1 = как выше, 0 = без
        // загара). Sunburn/grime ниже идут отдельно, на полную силу.
        tint = Color.Lerp(Color.white, tint, Mathf.Clamp01(SimBalance.TanStrength));
        tint = Color.Lerp(tint, new Color(0.95f, 0.50f, 0.42f), Mathf.Clamp01(sunburn) * 0.75f);
        // Spec 40.6: grime — the filthier the skin (low hygiene), the more it
        // muddies toward a dull earthy brown. Applied before the injury flush so
        // wounds still read on a dirty body.
        // At Hygiene 0 the WHOLE skin must read dirty (user: "вся кожа должна
        // быть грязная") — the smudge decals give texture, this tint carries
        // the overall filth. Half-strength earthy brown at zero hygiene.
        var grime = Mathf.Clamp01(1f - Mathf.Clamp01(hygiene));
        tint = Color.Lerp(tint, new Color(0.42f, 0.37f, 0.30f), grime * 0.5f);
        // Spec §50: a severed-limb drop bakes this tone (tan/sunburn/grime,
        // but NOT the live-pain flush below — a dropped limb no longer hurts).
        SkinTint = tint;
        // Spec 40.8: a badly hurt body flushes bruised red-purple. First-pass
        // whole-body tint (per-zone wound decals need texture work); driven by
        // 1 - Health so it only shows when genuinely wounded. (hurt is 0 in the
        // live path — the flush was retired — so tint == SkinTint here.)
        tint = Color.Lerp(tint, new Color(0.62f, 0.24f, 0.28f), Mathf.Clamp01(hurt) * 0.6f);

        // Spec 40.7 (user fix): wounds and bandages must NOT be tinted by the
        // tan. The painter bakes this same skin tone into the BASE layer of
        // the paint target, UNDER the wound/bandage stamps — so on any slot it
        // is actively painting, the tan lives in the texture and we pin
        // _BaseColor white below (painting it twice would re-darken the marks).
        // Un-painted skin keeps the cheap _BaseColor tint — no extra RT cost.
        _skinPainter?.SetSkinTone(SkinTint);

        if (_skinTintTargets.Count == 0)
        {
            return;
        }

        _skinMpb ??= new MaterialPropertyBlock();
        // Per-submesh: only the skin material slots, never the eyes/lashes/etc.
        foreach (var (renderer, index) in _skinTintTargets)
        {
            if (renderer == null)
            {
                continue;
            }

            // Slots the painter is currently drawing marks into already carry
            // the tan baked into their texture — tint them white so it isn't
            // multiplied in a second time; elsewhere the _BaseColor multiply IS
            // the tan (mirrors the gloss-map pin in SetBodyCondition).
            var painted = _skinPainter != null &&
                          ReferenceEquals(renderer, _skinPainter.Body) &&
                          _skinPainter.SlotHasAlbedoPaint(index);
            renderer.GetPropertyBlock(_skinMpb, index);
            _skinMpb.SetColor(BaseColorId, painted ? Color.white : tint);
            renderer.SetPropertyBlock(_skinMpb, index);
        }
    }

    // Classify each body-renderer material slot as skin (tintable) or not.
    // §74: swap the hairstyle to the one the simulation rolled for her.
    // ColonistAppearance.NoHair ("none") is the explicit bald case; an id the
    // catalog doesn't know is a content bug, so it warns and keeps the prefab
    // hair rather than silently shaving her.
    private void ApplyHairstyle(string hairstyle)
    {
        if (_bodyBones == null || string.IsNullOrEmpty(hairstyle))
        {
            return;
        }

        if (string.Equals(hairstyle, Simulation.Content.ColonistAppearance.NoHair,
                System.StringComparison.OrdinalIgnoreCase))
        {
            _bodyBones.SetHair(null);
            return;
        }

        var catalog = ActorAppearanceCatalog.Instance;
        var prefab = catalog != null ? catalog.Find(hairstyle) : null;
        if (prefab == null)
        {
            Debug.LogWarning(
                $"[§74] hairstyle '{hairstyle}' is not in the appearance catalog — " +
                "run HexLive ▸ Actors ▸ Rebuild Appearance Catalog. Keeping the prefab hair.", this);
            return;
        }

        _bodyBones.SetHair(prefab);
    }

    // §74: wear another actress's face. All four girls are Genesis3Female with
    // the SAME 17 material slot names (Torso/Face/Arms/Legs/Cornea/… — §31B.1a
    // regenerated Jolly's from Molly's precisely so the sets stay parallel), so
    // the swap is a lookup BY NAME and never depends on submesh order.
    //
    // sharedMaterials is the right handle: SkinTexturePainter instantiates its
    // own copies from whatever it finds (`body.materials`), and the tan on
    // un-painted slots rides a MaterialPropertyBlock — so nothing here leaks
    // one girl's wounds onto another's shared asset.
    private void ApplySkinSet(string skinSet)
    {
        if (string.IsNullOrEmpty(skinSet) || _bodySkins == null ||
            string.Equals(skinSet, _actorMesh.ToString(), System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var donor = LoadSkinSet(skinSet);
        if (donor == null || donor.Count == 0)
        {
            return;
        }

        foreach (var skin in _bodySkins)
        {
            // Hair and garments carry their own materials and their own donor
            // logic — the skin set is the BODY only.
            if (skin == null || skin.GetComponentInParent<Wear>() != null)
            {
                continue;
            }

            var mats = skin.sharedMaterials;
            var changed = false;
            for (var i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || !donor.TryGetValue(mats[i].name, out var replacement) ||
                    replacement == null || ReferenceEquals(replacement, mats[i]))
                {
                    continue;
                }

                mats[i] = replacement;
                changed = true;
            }

            if (changed)
            {
                skin.sharedMaterials = mats;
            }
        }
    }

    // Donor actor prefab → its body materials by name. Loading a prefab is not
    // instantiating it, and the map is built once per donor for the whole run.
    private static Dictionary<string, Material> LoadSkinSet(string actor)
    {
        if (_skinSets.TryGetValue(actor, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<string, Material>(System.StringComparer.OrdinalIgnoreCase);
        var prefab = Resources.Load<GameObject>($"HexLive/Actors/{actor}");
        if (prefab == null)
        {
            Debug.LogWarning($"[§74] skin set '{actor}' has no actor prefab at " +
                $"Resources/HexLive/Actors/{actor} — the body keeps its own materials.");
        }
        else
        {
            foreach (var skin in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin == null || skin.GetComponentInParent<Wear>() != null)
                {
                    continue;
                }

                foreach (var mat in skin.sharedMaterials)
                {
                    if (mat != null)
                    {
                        map[mat.name] = mat;
                    }
                }
            }
        }

        _skinSets[actor] = map;
        return map;
    }

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

                if (IsSkinMaterialName(mats[i].name))
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
            // Spec §50: "Limp" and "Crawl" are now fully animator-driven (the
            // Limp walk state via LimpingParam; the real Crawl clip via
            // CrawlingParam) — no procedural pose on top, which would corrupt
            // the all-fours clip.
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
        var targetGait = 0f;
        // §40.18-B: swim clips play at their authored pace — the walk-cadence
        // ground-matching would crawl the strokes (deep water is slow by sim).
        if (walking && !_swimming && _bodyRoot != null && _jumpTimer <= 0f)
        {
            var fullSpeed = 1.7f * _bodyRoot.lossyScale.y * FullWalkBodyHeightsPerSec;
            var simCadence = linearSpeed / Mathf.Max(0.0001f, fullSpeed * _simSpeed);

            // §71: the SIM owns the gait. Deriving it from measured speed (the
            // first cut) meant any pace above a walk read as a jog, so a colony
            // walking at BaseMoveSpeedFactor 1.5 was permanently trotting. Now
            // _running is the sim's decision and speed only picks HOW HARD she
            // runs, plus the playback rate that keeps the feet on the ground.
            float gaitGround;
            if (!_running)
            {
                // Walking: stay on the walk clip however brisk the pace, and
                // let the cadence carry the speed.
                targetGait = 0f;
                gaitGround = 1f;
            }
            else if (simCadence <= SlowRunCadence)
            {
                targetGait = 0.5f;
                gaitGround = SlowRunCadence;
            }
            else
            {
                targetGait = 0.5f + Mathf.InverseLerp(SlowRunCadence, RunCadence, simCadence) * 0.5f;
                gaitGround = Mathf.Lerp(SlowRunCadence, RunCadence, (targetGait - 0.5f) * 2f);
            }

            // A brisk walk may legitimately outrun the clip's authored pace, so
            // the walk ceiling is looser than the run's (a run clip playing 25%
            // fast already looks frantic).
            var maxCadence = _running ? MaxGaitCadence : MaxWalkCadence;
            targetAnimSpeed = Mathf.Clamp(simCadence / gaitGround, MinGaitCadence, maxCadence);
        }

        _animSpeed = Mathf.MoveTowards(_animSpeed, targetAnimSpeed, Time.deltaTime * 3f * _simSpeed);
        _animator.speed = _animSpeed * _simSpeed;

        // §71: ease into the gait so a sprint starting mid-stride ramps rather
        // than snapping from walk to run on one frame.
        _gait = Mathf.MoveTowards(_gait, targetGait, Time.deltaTime * 2.5f * _simSpeed);
        _animator.SetFloat(GaitParam, _gait);

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

    // ------------------------------------------------------------------
    // Spec §67: per-view continuous sounds — footsteps matched to the walk
    // cadence and surface, work "thock"s synced to the Chop clip's impact
    // frame. Everything scales with the animator speed (which already folds
    // in the fast-forward multiplier) and goes mute above 4× sim speed.
    // ------------------------------------------------------------------
    public enum GroundSurface { Grass, Sand, Water }

    private GroundSurface _groundSurface;
    private float _stepClock;
    private float _nextStepGap = 0.48f;
    private float _swimStrokeClock;
    private string _chopSfxId = string.Empty;
    private int _lastChopBeat = int.MinValue;

    /// <summary>Renderer per-tick: what the feet land on (wade/sand/grass).</summary>
    public void SetGroundSurface(GroundSurface surface) => _groundSurface = surface;

    private void PollActionSounds()
    {
        if (_animator == null || _dead || _laying || _simSpeed > 4.01f)
        {
            _lastChopBeat = int.MinValue;
            return;
        }

        // -- шаги / гребки: тикает в темпе анимации, не реального времени --
        if (_wasWalking && _jumpTimer <= 0f)
        {
            if (_swimming)
            {
                _swimStrokeClock += Time.deltaTime * Mathf.Max(0.2f, _animator.speed);
                if (_swimStrokeClock >= 1.15f)
                {
                    _swimStrokeClock = 0f;
                    Audio.FmodSfx.Play(Audio.FmodSfx.Sfx.StepWater, transform.position, 0.7f);
                }
            }
            else
            {
                _stepClock += Time.deltaTime * Mathf.Max(0.2f, _animator.speed);
                if (_stepClock >= _nextStepGap)
                {
                    _stepClock = 0f;
                    _nextStepGap = Random.Range(0.44f, 0.52f);
                    var stepId = _groundSurface switch
                    {
                        GroundSurface.Water => Audio.FmodSfx.Sfx.StepWater,
                        GroundSurface.Sand => Audio.FmodSfx.Sfx.StepSand,
                        _ => Audio.FmodSfx.Sfx.StepGrass,
                    };
                    Audio.FmodSfx.Play(stepId, transform.position);
                }
            }
        }
        else
        {
            // Стоя — почти взведён: первый шаг звучит сразу, не через полцикла.
            _stepClock = 0.35f;
            _swimStrokeClock = 0.7f;
        }

        // -- удар рубки: "тук" на ударном кадре каждого цикла Chop-клипа --
        if (_chopSfxId.Length > 0)
        {
            var state = _animator.GetCurrentAnimatorStateInfo(0);
            if (state.IsName("Chop"))
            {
                // normalizedTime растёт монотонно по циклам — целая часть
                // (со сдвигом на ударную фазу) меняется ровно раз за взмах.
                const float impactPhase = 0.45f;
                var beat = Mathf.FloorToInt(state.normalizedTime - impactPhase);
                if (_lastChopBeat != int.MinValue && beat > _lastChopBeat)
                {
                    Audio.FmodSfx.Play(_chopSfxId,
                        transform.position + transform.forward * 0.3f);
                }

                _lastChopBeat = beat;
            }
            else
            {
                _lastChopBeat = int.MinValue;
            }
        }
        else
        {
            _lastChopBeat = int.MinValue;
        }
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

    private void ResetActionTargetIKWeights()
    {
        if (_fullBodyIK == null || _fullBodyIK.solver == null)
        {
            return;
        }

        var solver = _fullBodyIK.solver;
        solver.IKPositionWeight = 0f;
        ResetEffector(solver.bodyEffector);
        ResetEffector(solver.leftShoulderEffector);
        ResetEffector(solver.rightShoulderEffector);
        ResetEffector(solver.leftThighEffector);
        ResetEffector(solver.rightThighEffector);
        ResetEffector(solver.leftHandEffector);
        ResetEffector(solver.rightHandEffector);
        ResetEffector(solver.leftFootEffector);
        ResetEffector(solver.rightFootEffector);
    }

    private static void ResetEffector(IKEffector effector)
    {
        if (effector == null)
        {
            return;
        }

        effector.target = null;
        effector.positionWeight = 0f;
        effector.rotationWeight = 0f;
        effector.positionOffset = Vector3.zero;
    }

    private void DriveActionTargetIK()
    {
        if (_fullBodyIK == null || _fullBodyIK.solver == null)
        {
            return;
        }

        var solver = _fullBodyIK.solver;
        var hand = _leftHanded ? _lHand : _rHand;
        var effector = _leftHanded ? solver.leftHandEffector : solver.rightHandEffector;
        var otherEffector = _leftHanded ? solver.rightHandEffector : solver.leftHandEffector;
        otherEffector.positionWeight = 0f;
        otherEffector.rotationWeight = 0f;

        var weight = _actionTargetActive && !_ragdollActive && !_laying && !_swimming && hand != null
            ? CurrentActionTargetIKWeight()
            : 0f;
        if (weight <= 0.001f)
        {
            solver.IKPositionWeight = 0f;
            effector.positionWeight = 0f;
            effector.rotationWeight = 0f;
            return;
        }

        var contact = ActionPropContactPoint(_actionTargetPoint, hand);
        solver.IKPositionWeight = 1f;
        solver.pullBodyHorizontal = ActionTargetIkBodyLean;
        effector.target = null;
        effector.position = _actionTargetPoint - (contact - hand.position);
        effector.positionWeight = weight;
        effector.rotationWeight = 0f;
    }

    private float CurrentActionTargetIKWeight()
    {
        var phase = ActionTargetPhase();
        if (phase < 0f)
        {
            return 0f;
        }

        var d = Mathf.Repeat(phase - (ActionTargetIkCenter - ActionTargetIkWidth * 0.5f), 1f);
        if (d > ActionTargetIkWidth)
        {
            return 0f;
        }

        var t = Mathf.Clamp01(d / ActionTargetIkWidth);
        var peak = 1f - Mathf.Abs(t * 2f - 1f);
        return Mathf.SmoothStep(0f, 1f, peak) * ActionTargetIkMaxHandWeight;
    }

    private float ActionTargetPhase()
    {
        if (_animator != null)
        {
            var state = _animator.GetCurrentAnimatorStateInfo(0);
            if (state.IsName("Chop") || state.IsName("Attack"))
            {
                return Mathf.Repeat(state.normalizedTime, 1f);
            }
        }

        if (_action == ActionKind.None)
        {
            return -1f;
        }

        return Mathf.Repeat(_actionPhase, 1f);
    }

    private Vector3 ActionPropContactPoint(Vector3 target, Transform hand)
    {
        if (_handProp == null)
        {
            return hand.position;
        }

        if (_handPropRenderers == null || _handPropRenderers.Length == 0)
        {
            return _handProp.transform.position;
        }

        var bounds = _handPropRenderers[0].bounds;
        for (var i = 1; i < _handPropRenderers.Length; i++)
        {
            bounds.Encapsulate(_handPropRenderers[i].bounds);
        }

        return bounds.ClosestPoint(target);
    }

    // Spec 20.16: a procedural arm swing for the current action, layered on
    // top of the animated pose. Rotations are applied in WORLD space around
    // the body's right axis (negative = swing forward/up), so the actor rig's
    // per-bone local orientation is irrelevant. Amplitudes are deliberately
    // moderate; flip the sign if a motion reads backwards.
    private void ApplyActionPose()
    {
        // The acting arm follows handedness; the world-space right-axis swing
        // mirrors cleanly, so a lefty runs the same math on the left arm.
        var actShldr = _leftHanded ? _lShldr : _rShldr;
        var actForearm = _leftHanded ? _lForearm : _rForearm;

        if (_action == ActionKind.None || _laying || _swimming || actShldr == null || _bodyRoot == null)
        {
            _actionPhase = 0f;
            return;
        }

        var actionSpeed = (_action == ActionKind.Attack || _action == ActionKind.SpearThrust)
            ? _attackSpeed
            : 1f;
        _actionPhase += Time.deltaTime * _simSpeed * actionSpeed;
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
                var t = Mathf.Repeat(_actionPhase * AttackSwingCycles, 1f);
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

        actShldr.rotation = Quaternion.AngleAxis(-shldr, right) * actShldr.rotation;
        if (actForearm != null)
        {
            actForearm.rotation = Quaternion.AngleAxis(-fore, right) * actForearm.rotation;
        }
    }

    private void LateUpdate()
    {
        SampleMotion();
        PollActionSounds();
        UpdateTalkTurns();
        // §67.10: hands the bubble back to a running conversation once a line
        // fades, and paces the ambient self-talk layer.
        _speech?.Tick();

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
                // Iter 28: ledge seat. LedgeSeatLift is a DIRECT Y offset that
                // ALWAYS applies (negative lowers her — needed when she sits at
                // a rim on the HIGHER tile, stepsUp==0, where the old
                // `Lift * stepsUp` zeroed any value out). The per-step perch is
                // a SEPARATE term that lifts her ~one elevation step when she
                // perches UP onto a taller ledge from below (stepsUp>0), tucked
                // back against the edge (local -Z = toward the high tile).
                var rest = _ledgeSit
                    ? _washAtShore
                        // §40.6 shore wash: sit at the bank (no rim-dangle, no
                        // back-tuck) — she crouches at the raised waterline.
                        ? new Vector3(
                            0f,
                            WashSeatLift + LedgeSeatPerStep * _ledgeSeatStepsUp,
                            0f)
                        : new Vector3(
                            0f,
                            LedgeSeatLift + LedgeSeatPerStep * _ledgeSeatStepsUp,
                            -LedgeSeatBack)
                    : Vector3.zero;
                // §21.21B hex-step jump: the ballistic trajectory rides this
                // local offset (world delta -> local handles root rotation
                // and scale in one go).
                var jumpOffset = JumpOffsetWorld();
                if (jumpOffset != Vector3.zero)
                {
                    rest += transform.InverseTransformVector(jumpOffset);
                }

                // §40.18-B: one shared body-height lift while swimming, EASED
                // in/out so diving in (or climbing out) doesn't snap the body up
                // by SwimBodyLift in a single frame. Suppressed WHILE a hop is
                // flying (the climb-out hop still reads _swimming because the tile
                // flips to land only on landing — the arc owns the vertical, so
                // the lift must not stack on top and pop her up). It eases back in
                // once the hop closes if she is still in the water.
                var swimLiftTarget = _swimming && _jumpTimer <= 0f ? 1f : 0f;
                _swimBlend = Mathf.MoveTowards(
                    _swimBlend, swimLiftTarget,
                    Time.deltaTime * Mathf.Max(1f, _simSpeed) / SwimLiftEaseSeconds);
                if (_swimBlend > 0f)
                {
                    rest.y += SwimBodyLift * _swimBlend /
                        Mathf.Max(0.0001f, transform.lossyScale.y);
                }

                _bodyRoot.localPosition = rest;
                _bodyRoot.localRotation = Quaternion.identity;

                // Wake-up ease: blend from the remembered on-bed pose to the
                // freshly computed rest pose, so she rises where she slept and
                // glides onto her feet instead of teleporting sideways off the
                // bed. Skipped when the gap is huge (an intended teleport).
                if (_wakeBlend > 0f)
                {
                    _wakeBlend = Mathf.MoveTowards(
                        _wakeBlend, 0f,
                        Time.deltaTime * Mathf.Max(1f, _simSpeed) / WakeEaseSeconds);
                    if ((_wakeFromPos - _bodyRoot.position).magnitude < _moveEpsilon * 300f)
                    {
                        var t = _wakeBlend * _wakeBlend * (3f - 2f * _wakeBlend);
                        _bodyRoot.position = Vector3.Lerp(_bodyRoot.position, _wakeFromPos, t);
                        _bodyRoot.rotation = Quaternion.Slerp(_bodyRoot.rotation, _wakeFromRot, t);
                    }
                    else
                    {
                        _wakeBlend = 0f;
                    }
                }
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
