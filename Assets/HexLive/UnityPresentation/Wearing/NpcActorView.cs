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
    private const float WetSkinSmoothness = 0.85f;

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

    // Spec 40.8/40.6: skin decal layer (wounds/dirt/sweat on bare zones only).
    private SkinDecals _skinDecals;
    private int _npcId;
    private readonly Dictionary<string, float> _zoneHealthScratch = new();
    private readonly HashSet<string> _uncoveredScratch = new();

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
        IReadOnlyList<string> wounds = null)
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

        _skinDecals.Sync(wounds, _uncoveredScratch, hygiene, thermal, _skinWetness);

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

            renderer.GetPropertyBlock(_skinMpb, index);
            _skinMpb.SetFloat(SmoothnessId, smoothness);
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

            _bodyBones.SetWearGrime(Mathf.Clamp01(1f - hygiene), _damageSpheres, sphereCount);
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
        if (_laying || _bodyRoot == null)
        {
            return;
        }

        _thermalPhase += Time.deltaTime;

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
    }

    // Spec 40.10: erode a worn garment by its durability (1 = pristine, 0 =
    // rags). The renderer feeds this per worn item from the snapshot's
    // WornDurability; low durability tears the garment (alpha-clip cutoff).
    public void SetGarmentWear(string definitionId, float durability01)
    {
        _bodyBones?.SetWearErosion(definitionId, durability01);
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
        // Spec 40.7: full tan is a deep brown — multiplies the skin texture, so
        // at TanLevel 1 the skin goes markedly dark, not just a light bronze.
        var tint = Color.Lerp(Color.white, new Color(0.40f, 0.27f, 0.18f), Mathf.Clamp01(tanLevel));
        tint = Color.Lerp(tint, new Color(0.95f, 0.50f, 0.42f), Mathf.Clamp01(sunburn) * 0.75f);
        // Spec 40.6: grime — the filthier the skin (low hygiene), the more it
        // muddies toward a dull earthy brown. Applied before the injury flush so
        // wounds still read on a dirty body.
        // Kept subtle: dirt is primarily the projected smudge decals now; the
        // tint only dims truly filthy skin a little.
        var grime = Mathf.Clamp01(1f - Mathf.Clamp01(hygiene));
        tint = Color.Lerp(tint, new Color(0.42f, 0.37f, 0.30f), grime * 0.25f);
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
    // already down), so it's skipped here. ArmHang, HeadClutch and the winded
    // breathing are authored to spec; Limp/Crawl are subtle placeholders until
    // the walk cycle can be tuned against a live screenshot (flip/scale the
    // amplitudes in the editor — the sim signal is authoritative).
    private void ApplyPosturePose()
    {
        if (_laying || _bodyRoot == null || _rShldr == null)
        {
            return;
        }

        _posturePhase += Time.deltaTime;
        var right = _bodyRoot.right;
        var fwd = _bodyRoot.forward;

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
            case "Limp": // placeholder: a slight favoured-side list; tune in editor
                _bodyRoot.rotation = Quaternion.AngleAxis(4f * Mathf.Sin(_posturePhase * 2f), fwd) * _bodyRoot.rotation;
                break;
            case "Crawl": // placeholder: deep forward hunch; real all-fours needs the rig
                _rShldr.rotation = Quaternion.AngleAxis(-30f, right) * _rShldr.rotation;
                _bodyRoot.rotation = Quaternion.AngleAxis(35f, right) * _bodyRoot.rotation;
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
            _animator.speed = 1f; // sleep clips run at authored pace
            _animSpeed = 1f;
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
        // speed = half the cadence. Clamped so extreme crawling still reads
        // as steps and healthy walking never overclocks.
        var targetAnimSpeed = 1f;
        if (walking && _bodyRoot != null)
        {
            var fullSpeed = 1.7f * _bodyRoot.lossyScale.y * FullWalkBodyHeightsPerSec;
            targetAnimSpeed = Mathf.Clamp(linearSpeed / Mathf.Max(0.0001f, fullSpeed), 0.35f, 1.15f);
        }

        _animSpeed = Mathf.MoveTowards(_animSpeed, targetAnimSpeed, Time.deltaTime * 3f);
        _animator.speed = _animSpeed;

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
        if (_action == ActionKind.None || _laying || _rShldr == null || _bodyRoot == null)
        {
            _actionPhase = 0f;
            return;
        }

        _actionPhase += Time.deltaTime;
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
                _bodyRoot.localPosition = _ledgeSit
                    ? new Vector3(0f, LedgeSeatLift, -LedgeSeatBack)
                    : Vector3.zero;
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
