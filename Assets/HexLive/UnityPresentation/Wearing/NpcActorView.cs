using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Playables;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
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
    // The health doll must clone the body that is actually rendering in the
    // world. Some actor prefabs are skeleton-only shells and receive their
    // body renderer through the runtime composition path, so reloading the
    // prefab makes the doll blank even though the NPC is visible.
    private static readonly Dictionary<int, NpcActorView> LiveByNpcId = new();

    private BodyBones _bodyBones;
    private Animator _animator;
    private LookAtIK _lookAtIK;
    private FullBodyBipedIK _fullBodyIK;
    private ActorName _actorMesh;
    private readonly Dictionary<string, int> _equippedSimItems = new();
    private readonly List<string> _removeScratch = new();
    private int _pendingHairLoads;

    // Spec §52.8: leg-slung tool props parked in the worn holster's tool.*
    // anchors (tool id → the instantiated model). Filled by SyncHolster.
    private readonly Dictionary<string, GameObject> _holsterProps = new();
    private readonly HashSet<string> _holsterWantScratch = new();
    private readonly List<string> _holsterRemoveScratch = new();
    private readonly HashSet<string> _slotClashWarned = new();

    // §52.9 / PERF: sim items whose visual was evicted the instant it was
    // equipped — another garment owns the same (layer, slot). Re-stitching a
    // garment onto the body is Instantiate + ~57 SetParent + a fresh material
    // set, so retrying that every tick is a permanent frame fire (measured at
    // ~100 ms/tick, and it leaked the material instances of every discarded
    // copy). Marked here so the repair in SyncWorn stays a ONE-SHOT; the mark
    // is dropped only on a real wardrobe change, never on a timer.
    private readonly HashSet<string> _clashedSimItems = new();

    public ActorName ActorMesh => _actorMesh;

    public static GameObject FindLiveRoot(int npcId)
    {
        return LiveByNpcId.TryGetValue(npcId, out var view) && view != null
            ? view.gameObject
            : null;
    }

    public static SkinnedMeshRenderer FindLiveBodySkin(int npcId)
    {
        return LiveByNpcId.TryGetValue(npcId, out var view) && view != null
            ? view.PrimaryBodySkin
            : null;
    }

    public static Transform FindLiveBodyRoot(int npcId)
    {
        return LiveByNpcId.TryGetValue(npcId, out var view) && view != null
            ? view._bodyRoot
            : null;
    }

    // Player picking must follow the rendered, animated body rather than a
    // single simulation point at the feet. Renderer.bounds is updated by Unity
    // for skinned meshes, clothing, hair and held props; intersecting the click
    // ray against those bounds costs work only on an actual click and avoids
    // baking animated meshes or maintaining physics colliders every frame.
    private readonly List<Renderer> _clickRendererScratch = new();

    public bool TryRaycastVisibleGeometry(Ray ray, float maxDistance, out float distance)
    {
        distance = maxDistance;
        _clickRendererScratch.Clear();
        GetComponentsInChildren<Renderer>(true, _clickRendererScratch);

        var hit = false;
        for (var i = 0; i < _clickRendererScratch.Count; i++)
        {
            var renderer = _clickRendererScratch[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            var bounds = renderer.bounds;
            if (bounds.size.sqrMagnitude < 0.000001f ||
                !bounds.IntersectRay(ray, out var candidate) ||
                candidate < 0f || candidate >= distance)
            {
                continue;
            }

            distance = candidate;
            hit = true;
        }

        _clickRendererScratch.Clear();
        return hit;
    }

    /// <summary>
    /// True only when the presentation the player will actually see is ready:
    /// the asynchronous hairstyle has finished and every currently worn sim
    /// item has been resolved by SyncWorn (applied, conclusively art-less, or
    /// rejected once by the existing slot-conflict guard).
    /// Merely having an NpcActorView is not enough — that was the startup race
    /// which exposed a naked bind-pose body behind the loading curtain.
    /// </summary>
    public bool IsPresentationReady(
        IReadOnlyList<string> wornDefinitionIds,
        IReadOnlyList<string> severedParts,
        IReadOnlyList<BodyPartConditionSnapshot> partConditions)
    {
        if (_pendingHairLoads > 0 || _bodyBones == null)
        {
            return false;
        }

        // A paused startup world may not produce another render tick. Ensure
        // starting prostheses are requested here, then keep the curtain up
        // until their Addressables callbacks have completed. Failures count
        // as complete and remain visible as explicit loader errors.
        ApplySeveredLimbs(severedParts);
        SyncProstheticVisuals(partConditions);
        foreach (var visual in _prostheticVisuals.Values)
        {
            if (!visual.IsReady)
            {
                return false;
            }
        }

        foreach (var simId in wornDefinitionIds)
        {
            if (!_equippedSimItems.ContainsKey(simId))
            {
                return false;
            }
        }

        return true;
    }

    private Transform _gazeTarget;
    private float _gazeWeight;
    private float _gazeWeightTarget;
    private Transform _gazeProxy;
    private bool _portraitGaze; // §80: взгляд отдан камере портрета
    private bool _cameraGaze;   // §130: на пару секунд смотрит в объектив игрока
    private float _cameraGazeUntil;
    private Transform _bodyRoot;

    // Spec 31B.5: animation follows measured view motion, not sim status —
    // feet move exactly when the body visibly moves.
    private static readonly int TurnDirectionParam = Animator.StringToHash("TurnDirection");
    private static readonly int LayingParam = Animator.StringToHash("Laying");

    // §105: вторая лежачая цепочка — FallDown→FallenIdle→StandUp. Сон остался
    // на Laying (он уходит в кровать), падение живёт здесь.
    private static readonly int FallenParam = Animator.StringToHash("Fallen");
    private static readonly int WorkingParam = Animator.StringToHash("Working");
    // §axe: axe/pickaxe work (Harvest/Process) plays a real looping swing clip
    // (Standing Melee Attack Horizontal) via the Chop clip-state, replacing the
    // old procedural shoulder chop.
    private static readonly int ChoppingParam = Animator.StringToHash("Chopping");
    // §gear-craft v2: the staged in-place craft kneels her into the planting-
    // style work clip (state "CraftWork") instead of the generic crouch.
    private static readonly int CraftingParam = Animator.StringToHash("Crafting");
    // §137: праздный отдых — своя цепочка RestDown → RestIdle → RestUp.
    private static readonly int RestingParam = Animator.StringToHash("Resting");
    // §68/§53: перевязка СТОЯ (клип «шарит по карманам»). Отдельно от
    // Crafting: тот кладёт её на колени работать с землёй.
    private static readonly int TreatingParam = Animator.StringToHash("Treating");
    // §110: утешение над рыдающей — своя коленопреклонённая цепочка
    // (PrayDown → Pray → PrayUp), а не заимствованный крафтовый присед.
    private static readonly int PrayingParam = Animator.StringToHash("Praying");
    // §111.13: сторона станции (−1 / 0 / +1) и «станция у головы».
    private static readonly int StationSideParam = Animator.StringToHash("StationSide");
    private static readonly int StationAtHeadParam = Animator.StringToHash("StationAtHead");
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
    // §77.5: playback speed of the one-gesture work states (Gather, CraftWork),
    // so ONE interaction is ONE playthrough of the clip whatever window the sim
    // hands us. Same idiom as JumpSpeed. Both states read this float; every
    // other state keeps its authored pace.
    private static readonly int ActionSpeedParam = Animator.StringToHash("ActionSpeed");
    // §104 r4: темп клипа УДАРА — свой параметр, чтобы бой и работа не писали
    // в одну ячейку по очереди каждый кадр (SetInteraction идёт перед SetCombat).
    private static readonly int AttackSpeedParam = Animator.StringToHash("AttackSpeed");
    // Base-clip KEYS of the two fitted states, for looking up their live length
    // (the override controller may have swapped in a different take).
    private const string GatherBaseClip = "X Bot@Gathering Objects";
    private const string CraftBaseClip = "X Bot@Plant A Plant";
    // The fit is CLAMPED, and the band is the whole design decision. Below it a
    // long job would crawl in visible slow motion; above it a 1-second pickup
    // would fire the whole 6-second stoop as a twitch. Outside the band we let
    // the clip loop / get cut as before — the fit is a polish, not a contract.
    private const float ActionSpeedMin = 0.6f;
    private const float ActionSpeedMax = 1.6f;
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
    // §81: ключ ОДНОРАЗОВОЙ сценки. Само по себе то, что тут сальса, значения
    // не имеет — это только адрес слота, в который вид заряжает нужный клип
    // перед срабатыванием триггера.
    private const string EmoteBaseClip = "X Bot@Salsa Dancing";

    // §105 / bug #41: оба лежачих idle-слота обязаны получать ОДИН и тот же
    // выбранный по NPC клип. Иначе сон совпадает с модельным футпринтом, а
    // FallenIdle остаётся на другом авторском Sleeping Idle.
    // Это ключи исходных клипов AnimatorOverrideController, не имена states.
    private const string SleepBaseClip = "Sleep";
    // §50: имена СОСТОЯНИЙ контроллера (не клипов) — в них уходит лежачая,
    // минуя переходные LieDown/GetUp. Совпадение с именами клипов случайно.
    private const string SleepStateName = "Sleep";
    private const string IdleStateName = "Idle";
    private const string FallenIdleStateName = "FallenIdle";
    // §50: подъём с земли — состояние StandUp (клип X Bot@Standing Up, 50
    // кадров). Из него граф сам уходит в Idle по exit time.
    private const string StandUpStateName = "StandUp";
    // Вход в подъём смешиваем чуть дольше, чем смену лежачих поз: стартовый
    // кадр клипа не совпадает с позой, в которой она лежала.
    private const float StandUpBlendSeconds = 0.25f;
    // Длина этого перехода, В СЕКУНДАХ (отсюда CrossFadeInFixedTime: у обычного
    // CrossFade длительность нормализована по КЛИПУ-ЦЕЛИ, а Sleep играет на
    // скорости 0.4 — те же «0.2» стали бы там секундами). Не ноль: обе позы
    // наземные, смешивать безопасно, а щелчок кадра заметен.
    private const float LyingPoseSwapSeconds = 0.2f;
    // Какой цепочкой её положили в прошлый раз — чтобы отличить РЕБРО от
    // ежетикового повтора (см. ApplyLying).
    private bool _lyingChainFallen;
    private bool _lyingChainSleepAfter;
    private const string FallenIdleBaseClip = "X Bot@Sleeping Idle";

    // Безделье: сколько молча простоять, прежде чем начать чудить; какой шанс
    // в секунду; и сколько держать паузу между сценками.
    private const float FidgetIdleWarmup = 6f;
    private const float FidgetChancePerSecond = 0.03f;
    private const float FidgetMinGap = 25f;

    private float _idleSince;
    private float _lastEmoteTime = -999f;
    private float _lastFidgetRoll;
    private bool _busyInteraction;
    private bool _combatFighting;
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
    // The sim swing-start tick we have already played. Replaces the old
    // "rising edge of IsSwinging" detect: that window is 1-2 ticks wide and a
    // frame renders only the LAST tick it stepped, so at fast-forward (and over
    // any network that drops a tick) most swings opened and closed unseen and
    // the blow landed on a motionless body. A stamp we have not played yet
    // survives the skip. -1 = nothing seen yet.
    private int _lastSwingStartTick = -1;
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
    private bool _hasUsableHand = true;
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
    private bool _carryingPerson;
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

    // §85: eye colour id → the five eye materials by name. Static for the same
    // reason — the whole island shares one set of eye assets per colour.
    private static readonly Dictionary<string, Dictionary<string, Material>> _eyeSets = new();

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
    // §104 r5: последний отыгранный штамп попадания. -1 = ничего не видели;
    // как и у замаха, сравнение идёт со ЗНАЧЕНИЕМ, а не с фронтом флага.
    private int _lastHitStampTick = -1;
    // Одна брызга на удар: сим дробит попадание на три раны (GashesPerHit), а
    // на перемотке ударов приходит пачками. Гейт — реального времени, поэтому
    // он одинаково работает на любой скорости симуляции.
    private const float SplashGateSeconds = 0.4f;

    // Spec 40.8/40.6: skin decal layer (wounds/dirt/sweat on bare zones only).
    private SkinDecals _skinDecals;
    private int _npcId;
    private readonly Dictionary<string, float> _zoneHealthScratch = new();
    private readonly HashSet<string> _uncoveredScratch = new();
    private readonly HashSet<string> _bandagedScratch = new();
    private readonly HashSet<string> _gauzeScratch = new();
    // Spec 40.8-H r10: per-zone BloodSoil (накопительная кровяная подложка из
    // сима) feeding the skin painter's speckles. When ANY zone is bloodied,
    // every non-severed zone rides along so the painter can bleed colour onto
    // clean neighbours.
    private readonly List<(string zone, float damage01)> _zoneDamageScratch = new();

    // Spec 40.8-H r11: per-zone BluntDamage — фиолетовое поле синяков (тупой
    // урон). Едут только ушибленные зоны: синяк локален, не растекается.
    private readonly List<(string zone, float bruise01)> _zoneBruiseScratch = new();

    // BodyPart → имя зоны painter'а (те же строки, что в снапшотном BodyParts
    // "Zone=hp") без ToString()-аллокаций каждый кадр.
    private static readonly string[] ZoneNames = BuildZoneNames();

    private static string[] BuildZoneNames()
    {
        var values = (BodyPart[])System.Enum.GetValues(typeof(BodyPart));
        var names = new string[values.Length];
        foreach (var part in values)
        {
            names[(int)part] = part.ToString();
        }

        return names;
    }

    private static string ZoneName(BodyPart part)
    {
        var index = (int)part;
        return index >= 0 && index < ZoneNames.Length ? ZoneNames[index] : part.ToString();
    }

    // Spec §50: zones already hidden by amputation (a limb never comes back, so
    // this only grows). Maps a severed BodyPart zone to the DISTAL bone whose
    // sub-tree we collapse — a below-elbow/below-knee cut that leaves a bloody
    // stub rather than deforming the shoulder/hip (and the cloth riding them).
    private readonly HashSet<string> _severedZones = new();

    // Spec §118: fitted devices are view-side objects driven by the still-
    // animated organic bones, but parented outside the collapsed limb subtree.
    private readonly Dictionary<BodyPart, ProstheticVisual> _prostheticVisuals = new();
    private readonly HashSet<BodyPart> _prostheticZones = new();
    private readonly List<BodyPart> _prostheticRemoveScratch = new();
    private readonly Dictionary<BodyPart, Vector3> _severedBoneOriginalScale = new();

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

    // Spec §50: a body with no support on either side cannot stand. Every
    // standing/idle clip is swapped for the existing prone idle (and walking
    // for crawl) via the override controller; no extra leg/knee pose is layered.
    // Driven by the snapshot's LegsLost (BodyState.IsProne — функция ноги в
    // нуле, оторвана она или разбита) плюс PostureHint=Crawl и зоны ампутации,
    // rather than re-deriving it from amputation visuals in this view.
    private bool _legless;

    // §50: «ноги в ноль» прямо из снапшота. Отдельно от _posture, потому что
    // подсказка позы ранжирована и обморок перебивает в ней ползание — см.
    // RefreshLeglessPresentation.
    private bool _legsLost;

    private AnimationClip ProneClip => _animSet != null ? _animSet.proneIdle : null;

    private AnimationClip CrawlClip => _animSet != null ? _animSet.crawl : null;

    // §71.5: what the four locomotion slots are playing RIGHT NOW. The walk
    // slot has four claimants and the cadence math has to know whose stride it
    // is matching, so the resolver records every swap it makes. These are the
    // ONLY writers of the Idle and GaitBlend keys — a direct OverrideClip on
    // one of them would win the frame and then be silently undone.
    private readonly AnimationClip[] _activeGait = new AnimationClip[3];
    private AnimationClip _activeIdle;
    // Resolver inputs: the mood (§81), the tool in hand, and the bare-handed
    // fight stance (§104). Legless (§50) is _legless, read directly.
    private bool _sadWalk;
    private AnimationClip _armedIdleClip;
    private AnimationClip _armedWalkClip;
    private AnimationClip _bareStanceClip;

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
    // §78.5: the seat coordinates above (and the stump's bare anchor) are
    // tuned on the girls' bodies. The male skeleton on the same Sit clip
    // rests his butt a touch too high and too far back, so a MALE actor
    // gets this extra nudge — forward (+Z, the way he faces) and down —
    // on EVERY seat, stump and hex rim alike. Zero for the girls.
    public static float MaleSeatForward = 0.08f;
    public static float MaleSeatDown = 0.06f;
    // Extra lift per elevation step she perches UP from a lower tile (≈ one
    // step high). 0 when she already sits on the higher tile's rim.
    private const float LedgeSeatPerStep = 0.55f;
    private bool _ledgeSit;
    private bool _sitting;

    // Face life (blink + mood expression) on the body blend shapes.
    private NpcFaceAnimator _face;

    private bool _laying;
    private bool _crying; // §110: лежит и рыдает (поза + лицо отличаются от сна)
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
    // How much ground one cycle covers at playback 1× is a property of the
    // CLIP, in body heights per second — the base walk covers ≈ 0.76.
    // §71.5: this is now only the DEFAULT. Every locomotion clip carries its
    // own figure in NpcAnimSet.strides, because the walk slot is shared: the
    // sad walk (a 44-frame take against the base's 30), the armed walk, the
    // male set and the §50 crawl were all played as if they had the base
    // walk's stride, and each slid by exactly the ratio between them.
    public static float FullWalkBodyHeightsPerSec = 0.76f;
    // §71: how much ground each GAIT covers, as a multiple of the walk clip's
    // pace. The girl blends walk -> slow run -> run as she speeds up, and each
    // clip then plays at ~1x its authored rate instead of a walk cycle
    // spinning absurdly fast. Mixamo's takes run roughly walk 1 : jog 2 : run
    // 3.4 — §71.5: defaults for a run clip with no stride row of its own.
    public static float SlowRunCadence = 2.0f;
    public static float RunCadence = 3.4f;
    // Playback still trims a little around the blended gait (a hobbling or
    // soaked girl takes slower steps), but never far from the authored rate.
    public static float MinGaitCadence = 0.35f;
    public static float MaxGaitCadence = 1.25f;
    // §71.6: до какого перегона клипа шага походка остаётся ЧИСТЫМ шагом.
    // Выше — поза начинает подмешивать трусцу вместо того, чтобы гнать плёнку
    // (развёрнутый разбор — в SampleMotion). 1.15 выбрано так, чтобы средняя
    // девушка (1.2 ед/с при базе 1.2 и множителе ловкости 1.0) бленда почти не
    // касалась, а ловкая (1.38 ед/с) вставала примерно на треть к трусце.
    public static float WalkStretchCadence = 1.15f;
    // Насколько далеко ИДУЩАЯ вправе уйти в бленд. Трусца сидит на 0.5, так
    // что 0.35 — «распустившийся размашистый шаг», а не бег.
    public static float MaxWalkGait = 0.35f;
    // §71.5: how hard the measured speed is smoothed before it drives the
    // cadence, as a time constant in SIM seconds. The sim steps at 4 Hz and so
    // does its speed — a graded turn costs TurnMinSpeedFactor for a tick, a
    // planted pivot skips translation outright, a housemate in the doorway
    // blocks one — so the raw frame-to-frame delta pumps the walk cycle four
    // times a second. That pumping is the "jerky" half of the complaint; the
    // feet sliding is the other half, and it is the stride table above.
    public static float SpeedSmoothTau = 0.15f;
    // ...and how long a stop must LAST before the body admits it stopped. One
    // frozen tick (0.25 s) is a corner or a blocked step, not a halt, and
    // flipping to Idle and back across it reads as a stutter. A planted pivot
    // IS a halt and must reach Idle promptly or the turn-on-spot states never
    // fire — that is what the yaw bypass below is for.
    // §71.8: the hold is split by the SIM'S OWN INTENT. Measured over 8 000
    // ticks on three seeds, a walking colonist stalls for TWO OR THREE ticks
    // mid-route several times a minute (a queue at an occupied junction, a
    // re-plan hiccup) — a single 0.3 s hold covers exactly one tick, so every
    // such stall flapped Walk→Idle→Walk. While the sim still reports the
    // journey ON (MovementStatus == Moving, which the §71.3 latch keeps true
    // through blocked-step queues) the walk is held MidJourneyWalkHoldSeconds —
    // long enough to bridge a 3-tick stall. Once the sim says the journey is
    // OVER, only the short WalkHoldSeconds applies, so an arrival no longer
    // ends with half a second of marching on the spot.
    public static float WalkHoldSeconds = 0.3f;
    public static float MidJourneyWalkHoldSeconds = 0.8f;
    public static float PivotYawSpeed = 90f;
    // §50: с какой угловой скорости (град/с) разворот ползущей уже считается
    // движением и включает клип ползания. Порог низкий нарочно: сим крутит
    // ползущую втрое медленнее ходячей (BodyState.MobilityTurnFactor), и
    // «стоящий» порог пивота её разворот просто не заметил бы.
    public static float CrawlTurnAnimYawSpeed = 5f;
    // §71: the three GaitBlend slots, keyed by CLIP name — these are the
    // AnimatorOverrideController keys. Overriding all three with one clip (the
    // §50 crawl) pins her to a single gait that can never blend into a run.
    private static readonly string[] GaitClipKeys = { "Walk", "X Bot@Slow Run", "X Bot@Running" };
    // §78: the Idle and Sit states' clip keys. Sit's take lives in
    // Locomotion/Animations, OUTSIDE the AnimLibrary folder whose import
    // postprocessor renames clips after the file — so it kept Mixamo's own
    // export name, and that string IS the override key. Ugly, but honest:
    // renaming the clip now would repoint the controller's Sit state at
    // nothing.
    private const string IdleClipKey = "Idle";
    private const string SitClipKey = "mixamo.com";
    // §78: the locomotion clips THIS actor plays, keyed by the base-clip name
    // they replace. A male body walking the colonists' authored takes reads as
    // somebody else's gait, so the men get their own set (NpcAnimSet.male).
    // The map doubles as the "restore to base" source: the armed stance hands
    // Idle/Walk back when a tool leaves the hand, and it must hand back HIS
    // clip, not the controller's — otherwise picking up an axe permanently
    // turned him back into a girl.
    private readonly Dictionary<string, AnimationClip> _actorLocomotion = new();
    private static readonly int GaitParam = Animator.StringToHash("Gait");
    private float _gait;

    // §80 r3: ниже этого GaitBlend она считается стоящей и годится для снимка.
    private const float PortraitStillGait = 0.05f;
    // §71: the sim's gait decision (SetRunning). NOT re-derived from speed.
    private bool _running;
    // A brisk walk is allowed to outrun the walk clip a little; a run clip
    // played much above its authored rate just looks frantic.
    public static float MaxWalkCadence = 1.6f;
    // §114 bug #4: hard ceiling on the sim-time cadence (_animSpeed). Nothing
    // legitimate ever asks a clip for more than ~2× (hex-hop compression peaks
    // there); the valve exists so a stale/poisoned cadence can never make the
    // body play "Flash"-fast for the seconds MoveTowards would need to unwind
    // it after a fast-forward drop.
    public static float MaxAnimCadenceCeiling = 4f;
    // §71.5: the measured speed, low-passed (SpeedSmoothTau), and how long she
    // has read as still — the two pieces of state the de-jitter needs.
    private float _smoothedSpeed;
    private float _stillTimer;
    // The renderer supplies the exact horizontal speed between simulation
    // snapshots. Using the interpolated Transform alone made cadence depend on
    // render FPS and alpha; wet drag, Agility, carry and other sim-side
    // movement multipliers could therefore make the feet drift or overcrank.
    private float _simGroundSpeed;
    private bool _simGroundSpeedValid;
    private float _simYawSpeed;
    private bool _simYawSpeedValid;

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

    /// <summary>Feed the ground speed calculated from two simulation poses.
    /// The Transform remains authoritative for visible interpolation and the
    /// stop/turn hysteresis, while cadence uses this deterministic sim sample.
    /// </summary>
    public void SetSimulationGroundSpeed(float worldUnitsPerSecond)
    {
        _simGroundSpeed = Mathf.Max(0f, worldUnitsPerSecond);
        _simGroundSpeedValid = true;
    }

    /// <summary>Feed the signed yaw rate between simulation poses. Turning
    /// decisions must not depend on render interpolation either.</summary>
    public void SetSimulationYawSpeed(float degreesPerSecond)
    {
        _simYawSpeed = degreesPerSecond;
        _simYawSpeedValid = true;
    }

    // §71.8: is the sim still WALKING HER SOMEWHERE right now? Fed per sync
    // from the snapshot's MovementStatus. Selects which walk-hold applies to a
    // zero-speed tick: a stall on an active journey is bridged (she WILL
    // resume), a stop with the journey over is admitted quickly.
    private bool _simMidJourney;

    public void SetSimulationMovementIntent(bool midJourney)
    {
        _simMidJourney = midJourney;
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
    // The sim hop-start tick we have already armed an arc for. Replaces the old
    // "HopKind changed" edge — see SetHopSignal. 0 = nothing seen yet.
    private int _lastHopStartTick;
    // §21.21B v7: the VIEW is vertical-only. The sim already moves the root's
    // XZ perfectly (gather → straight takeoff→landing), so the body follows
    // the root's XZ exactly and adds ONLY a Y arc that smooths the ground-
    // level snap at the tile crossing into a jump. No XZ prediction, no
    // velocity extrapolation, no settle phase — those double-computed the XZ
    // the sim already owns and left the residual offset the user saw.
    private float _jumpStartY;         // world-Y the arc starts from (stand level)
    private float _jumpHeightDelta;    // world units, signed (+ = up)
    private float _jumpTakeoffFrac;    // [0..this] = crouch beat (arc flat)
    private float _jumpFlightEndFrac;  // [this..1] = landing beat (arc = 1)
    private float _jumpDuration;
    private float _jumpTimer;
    private float _jumpRetriggerGuard; // swallows the pose-delta echo at hop end

    // §57.11: спуск-падение раненой (HopKind="Fall"). Раскадровка своя: без
    // отталкивания (takeoff-бит — просто шаг с кромки в текущей позе), клип
    // падения включается на полётном бите, приземление без своего клипа, и
    // после него — подъём Standing Up на сим-паузе FallRecoverSeconds
    // (ползущая не встаёт: у неё crawl-оверрайд держит своё).
    private bool _jumpIsFall;
    private bool _fallClipStarted;
    // Подъём — СУЩЕСТВУЮЩЕЕ состояние §50 StandUp (сам уходит в Idle по exit
    // time), заводить своё не нужно. Новое здесь только состояние полёта.
    private const string FallStateName = "Falling";
    private static readonly int FallStateHash = Animator.StringToHash(FallStateName);
    private static readonly int StandUpStateHash = Animator.StringToHash(StandUpStateName);
    private bool _jumpUp;
    // §21.21B v22, ВОЗВРАЩЁН после v23 — и это НЕ подпорка асимметрии, а
    // компенсация ЛАГА: часы дуги идут по последнему шагнутому тику, а
    // видимый корень интерполируется на тик ПОЗАДИ. С окном v23 (полёт 0.6 с)
    // один тик = 42% полёта, и «гравитация по часам» роняла тело на ЦЕЛУЮ
    // ступень, пока оно зрительно ещё стояло на верхнем гексе — замер
    // JumpDipProbe: body-root = −0.550 на 1.5 с, каждый прыжок вниз. Поэтому
    // вертикаль СПРЫГИВАНИЯ ведётся по ВИДИМОМУ продвижению корня вдоль
    // отрезка полёта (кромка ±EdgePadding), а не по клоку. Подъём остаётся
    // на часах: ранний отрыв вверх — намеренный (v20).
    private Vector3 _jumpEdgePointWorld;
    private Vector3 _jumpFlightDirectionWorld;
    private bool _jumpSyncDownToVisibleXz;
    // §21.21B v15: the vertical offset the body held on the last frame of the
    // window. A well-formed arc ends AT the root (offset 0), so this is normally
    // zero; when it is not — clock drift, a swim lift, a hop cut short — it eases
    // out over a few frames. Cutting it to zero in one frame is what read as the
    // body "teleporting" onto the ledge.
    private float _jumpResidualY;
    private const float JumpSettleSpeed = 5f; // wu/s ≈ one elevation step per 0.11 s
    // §21.21B v23: ПРИШПИЛКА К УРОВНЮ ПОСАДКИ. Если окно дуги закрылось, а
    // корень ещё не получил Y-снап посадочного тайла (кадр-фриз съел запас —
    // снап приходит на тик позже касания), тело держится на АБСОЛЮТНОЙ высоте
    // посадки, а не едет ease'ом к отставшему корню — та езда и была «при
    // запрыгивании проваливается вниз». Грейс ограничен, чтобы обрыв прыжка
    // по любой другой причине не оставил её висеть.
    private const float JumpLandingPinGraceSeconds = 0.6f;
    private const float JumpLandingPinThreshold = 0.1f; // wu; ниже — обычный ease
    private float _jumpEndDesiredY;
    private float _jumpPinGrace;
    private bool _jumpPinToLanding; // только сим-прыжки; водные дуги — нет

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
        SyncHeelPoseTarget();
        RefreshAnimatorCulling();
        if (_animator != null)
        {
            _animator.SetBool(SwimmingParam, swimming);
        }
    }

    // Spec 31C.8 + PERF: a standing/walking animator that scrolled offscreen
    // skips its transform writes (CullUpdateTransforms) — the state machine
    // still advances, so nothing desyncs when she comes back into view. Every
    // pose that leaves the authored skin bounds (lying, swimming, death,
    // ragdoll) keeps AlwaysAnimate: there the renderer's visibility answer
    // cannot be trusted, and a culled animator would freeze the body mid-pose
    // while the root keeps moving.
    private void RefreshAnimatorCulling()
    {
        if (_animator == null)
        {
            return;
        }

        var boundsUntrustworthy = _laying || _swimming || _dead || _ragdollActive;
        _animator.cullingMode = boundsUntrustworthy
            ? UnityEngine.AnimatorCullingMode.AlwaysAnimate
            : UnityEngine.AnimatorCullingMode.CullUpdateTransforms;
    }

    // ⭐ Труп висел над землёй, потому что ЕГО МЕРИЛИ АВТОРСКОЙ КОРОБКОЙ.
    //
    // PlantDeadBodyOnSurface сажает тело по фактическому нижнему краю скина —
    // и это верно ровно до тех пор, пока `bounds` описывают ТЕКУЩИЙ кадр.
    // Скиннер пересчитывает их за кадр только при `updateWhenOffscreen`, а
    // включался он единственной строкой в ApplyLying, по флагу лежания. У
    // свежего трупа (клип смерти, `_laying` остаётся false) он не только не
    // включался — ApplyLying зовётся КАЖДЫЙ ТИК и активно гасил его обратно.
    // Мерка тогда бралась со стоячей авторской коробки: её низ у ступней
    // стоящей, то есть примерно у корня, поправка выходила почти нулевой, и
    // тело оставалось на запечённой в клипе высоте — висеть над поверхностью.
    //
    // Условие то же самое, что у отключения куллинга выше, и по той же
    // причине: в этих позах авторским границам верить нельзя.
    private void RefreshSkinBounds()
    {
        if (_bodySkins == null)
        {
            return;
        }

        var perFrameBounds = _laying || _dead || _ragdollActive;
        foreach (var skin in _bodySkins)
        {
            if (skin != null)
            {
                skin.updateWhenOffscreen = perFrameBounds;
            }
        }
    }

    // §21.21B: sim hop signal ("Up"/"Down"/""), fed every sync. Starts the
    // ballistic arc on a hop the sim began and we have not played yet.
    // heightDeltaWorld is the EXACT signed root-level difference
    // (renderer-computed; water dives include the swim sink depth).
    //
    // startGroundY is the ground level the hop LEAVES (the takeoff tile's actor
    // ground Y). Taking it from the live transform instead was wrong on a late
    // sighting: SetHopSignal runs inside RenderSnapshot, BEFORE this frame's
    // interpolation, so the root still holds the PREVIOUS tick's Y.
    //
    // hopStartTick / ageSeconds: the tick the sim opened the hop, and how long
    // ago that was in world time. Keying on the stamp rather than on HopKind's
    // rising edge matters because a frame renders only the last tick it stepped:
    // fast-forward (and any dropped network tick) can swallow the whole "Up",
    // after which she just glides up the ledge with no jump at all. ageSeconds
    // then places the arc at the phase the hop is ALREADY at.
    public void SetHopSignal(string hopKind, float heightDeltaWorld, float startGroundY,
        bool intoWater = false, int hopStartTick = 0, float ageSeconds = 0f,
        Vector3 edgePointWorld = default, Vector3 flightDirectionWorld = default,
        float visibleInterpolationLagSeconds = 0f)
    {
        hopKind ??= string.Empty;

        var started = hopKind.Length > 0 && hopStartTick > 0 && hopStartTick != _lastHopStartTick;
        _lastHopStartTick = hopStartTick;
        if (!started)
        {
            // §21.21B v23: ПРИСТЁГИВАЕМ часы дуги к сим-фазе ЭТОГО же прыжка.
            // Свободный ход по Time.deltaTime дрейфует на каждом фризе кадра
            // (прогрев шейдеров на старте плея — гарантированный фриз), дуга
            // закрывалась раньше Y-снапа посадочного тайла, и хвостовой ease
            // ронял тело на ступень вниз. ageSeconds — точный сим-прошедший
            // срок на последнем шагнутом тике: держим часы в [age, age+тик].
            if (_jumpTimer > 0f && hopKind.Length > 0)
            {
                var runningElapsed = _jumpDuration - _jumpTimer;
                var simFloor = Mathf.Clamp(ageSeconds, 0f, _jumpDuration);
                var simCeil = Mathf.Min(_jumpDuration,
                    simFloor + Mathf.Max(0.05f, visibleInterpolationLagSeconds));
                _jumpTimer = _jumpDuration -
                    Mathf.Clamp(runningElapsed, simFloor, simCeil);
            }

            return;
        }

        // §21.21B v3: one clock for everything — the body flies exactly the
        // sim's airborne window: [Takeoff .. HopSeconds - Landing]. During
        // the takeoff beat the root stands (offset 0 by construction);
        // during the landing beat the root has stopped at the landing point
        // and the clip plants the feet. §21.21B v23: ONE window both
        // directions, the flight fills the whole airborne beat — the arc, the
        // sim and the clip share the same three numbers.
        var up = hopKind == "Up";
        // §57.11: "Fall" — тот же нижний ход дуги, своя раскадровка клипов.
        _jumpIsFall = hopKind == "Fall";
        _fallClipStarted = false;
        var hop = HexLive.Simulation.Navigation.HexHopTuning.HopSeconds;
        // §21.21B v22: a DROP's vertical follows the RENDERED root, which
        // interpolates one snapshot behind the arc clock — keep the clip and
        // the arc alive for that extra tick or they end while the body is
        // still approaching the lower landing point.
        var presentationWindow = up
            ? hop
            : hop + Mathf.Max(0f, visibleInterpolationLagSeconds);
        var takeoffFrac = HexLive.Simulation.Navigation.HexHopTuning.TakeoffSeconds / hop;
        var flightEndFrac =
            (hop - HexLive.Simulation.Navigation.HexHopTuning.LandingSeconds) / hop;
        var age = Mathf.Clamp(ageSeconds, 0f, hop);

        _jumpEdgePointWorld = edgePointWorld;
        _jumpFlightDirectionWorld = Vector3.ProjectOnPlane(
            flightDirectionWorld, Vector3.up).normalized;
        _jumpSyncDownToVisibleXz = !up && _jumpFlightDirectionWorld.sqrMagnitude > 0.5f;

        // §21.21B v15: a hop first seen AFTER the flight is over has nothing
        // left to play — the sim has already put her on the landing tile and the
        // root has snapped to it. Arcing from here would lift the body back off
        // the ground for the rest of the window.
        if (age >= hop * flightEndFrac)
        {
            return;
        }

        // §21.21B v15: the arc keeps the FULL window and starts at the phase the
        // hop is already at. The old code shortened the window instead while
        // still measuring the beats against the full HopSeconds — so a late arc
        // replayed the crouch and only reached the target level at 75% of what
        // was LEFT, long after the root had snapped up.
        StartJumpArc(up, heightDeltaWorld, startGroundY, presentationWindow,
            takeoffFrac, flightEndFrac, age);
        _jumpPinToLanding = true;
        _jumpPinGrace = JumpLandingPinGraceSeconds;

        // §67: нырок в воду — всплеск на посадочной доле дуги.
        if (intoWater && _simSpeed <= 4.01f)
        {
            Audio.SoundManager.Instance?.PlayDelayed(
                Audio.FmodSfx.Sfx.Splash, transform.position,
                Mathf.Max(0f, hop * 0.6f - age) / Mathf.Max(0.25f, _simSpeed));
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
        // No takeoff tile either, so the live root Y is the only base available.
        _jumpIsFall = false; // §57.11: водный путь — никогда не «падение»
        StartJumpArc(
            heightDeltaWorld > 0f,
            heightDeltaWorld,
            transform.position.y,
            HexLive.Simulation.Navigation.HexHopTuning.HopSeconds * 0.5f,
            0f, 1f, 0f);
        // Water arcs have no sim flight segment to sync against, no tile snap
        // to wait for — and the plunge deliberately ends BELOW the surface.
        _jumpSyncDownToVisibleXz = false;
        _jumpPinToLanding = false;
    }

    private void StartJumpArc(
        bool up, float heightDelta, float startGroundY, float durationSimSeconds,
        float takeoffFrac, float flightEndFrac, float startElapsed)
    {
        if (_laying || _dead || _animator == null)
        {
            return;
        }

        _jumpUp = up;
        _jumpHeightDelta = heightDelta;
        _jumpStartY = startGroundY;
        _jumpEndDesiredY = startGroundY + heightDelta;
        _jumpDuration = Mathf.Max(0.05f, durationSimSeconds);
        _jumpTakeoffFrac = Mathf.Clamp01(takeoffFrac);
        _jumpFlightEndFrac = Mathf.Clamp(flightEndFrac, _jumpTakeoffFrac + 0.05f, 1f);
        if (up)
        {
            // The XZ sync is a drop-only device; the legacy water path and an
            // up-jump must not inherit it from a previous drop.
            _jumpSyncDownToVisibleXz = false;
        }
        // §21.21B v15: seen late? Keep the window, skip to the phase the sim is
        // already at — the beat fractions then still measure against the window
        // they were derived from.
        var elapsed = Mathf.Clamp(startElapsed, 0f, _jumpDuration * 0.95f);
        _jumpTimer = _jumpDuration - elapsed;
        // Compress the authored clip to exactly this window: one playthrough ==
        // durationSimSeconds. Speed = clipLength / window (Unity multiplies this
        // by the global animator.speed, so fast-forward stays in sync with the
        // arc timer). Falls back to 1 if the clip length is unknown.
        // §57.11: у падения нет отталкивания — на takeoff-бите она просто
        // делает шаг с кромки в текущей позе. Клип (луп Falling) включит
        // полётный бит из JumpOffsetWorld; поздний вход — сразу здесь.
        if (_jumpIsFall)
        {
            _animator.SetFloat(JumpSpeedParam, 1f); // луп не сжимается к окну
            if (elapsed / _jumpDuration >= _jumpTakeoffFrac)
            {
                StartFallClip();
            }

            return;
        }

        var jumpClipLength = _jumpUp ? _jumpUpClipLength : _jumpDownClipLength;
        if (jumpClipLength > 0.001f)
        {
            _animator.SetFloat(JumpSpeedParam, jumpClipLength / _jumpDuration);
        }
        _animator.ResetTrigger(_jumpUp ? JumpDownParam : JumpUpParam);
        if (elapsed > 0.1f)
        {
            // Enter the clip at the same phase as the arc — a triggered
            // transition would replay the crouch a late hop is long past.
            _animator.CrossFade(_jumpUp ? "JumpUp" : "JumpDown", 0.05f, 0,
                elapsed / _jumpDuration);
        }
        else
        {
            _animator.SetTrigger(_jumpUp ? JumpUpParam : JumpDownParam);
        }
    }

    // §57.11: клип полёта падения. Состояния Falling/StandUp появляются в
    // контроллере отдельной правкой; пока их нет, честный фолбэк — прежний
    // JumpDown, чтобы падение никогда не выглядело Т-позой.
    private void StartFallClip()
    {
        if (_fallClipStarted || _animator == null)
        {
            return;
        }

        _fallClipStarted = true;
        _animator.CrossFade(
            _animator.HasState(0, FallStateHash) ? FallStateName : "JumpDown",
            0.08f, 0, 0f);
    }

    // Vertical arc height 0..1 over the FLIGHT fraction tf (0 at takeoff-end,
    // 1 at flight-end). > 1 = above the target ledge on the way up.
    // §21.21B v23: one fixed shape per direction, one knob (LipClearance, in
    // world units) instead of apex/overshoot/fall-start sliders.
    private float JumpVerticalEase(float tf)
    {
        // The clearance in EASE units (fraction of the height delta).
        var clearance = HexLive.Simulation.Navigation.HexHopTuning.LipClearance /
            Mathf.Max(0.01f, Mathf.Abs(_jumpHeightDelta));

        if (_jumpUp)
        {
            // Rise past the ledge to a mid-flight peak, then settle onto it —
            // the symmetric flight crosses the border at 0.5, so the peak sits
            // right over the lip.
            var peak = 1f + clearance;
            return tf < 0.5f
                ? Mathf.SmoothStep(0f, peak, tf * 2f)
                : Mathf.Lerp(peak, 1f, Mathf.SmoothStep(0f, 1f, (tf - 0.5f) * 2f));
        }

        // DOWN: stay level (with a small clearing pop) until just past the lip
        // — the symmetric flight crosses the border at 0.5 — then gravity
        // (quadratic) down to touchdown at flight-end. Falling earlier scrapes
        // her feet on the edge.
        const float fallStart = 0.55f;
        var ease = tf <= fallStart
            ? 0f
            : Mathf.InverseLerp(fallStart, 1f, tf) * Mathf.InverseLerp(fallStart, 1f, tf);

        // The pop: ease < 0 = above stand level, since DOWN heightDelta < 0.
        var pop = Mathf.Sin(Mathf.Clamp01(tf / fallStart) * Mathf.PI);
        return ease - pop * clearance;
    }

    // Advances the jump window and returns the world-space offset the body
    // holds relative to the sim root this frame. VERTICAL ONLY (§21.21B v7):
    // the sim owns the root's XZ (gather → straight flight → landing); the
    // body follows it exactly and adds a Y arc that turns the tile-crossing
    // ground snap into a jump. Self-correcting: it reads the live root Y each
    // frame, so a late tile switch just holds the body at arc height until
    // the snap arrives — no dip, no snap-back.
    // §21.21B v15: the arc's BASE (_jumpStartY) is the takeoff tile's ground
    // level, not the live root — on a late sighting the root has already
    // snapped to the landing level, and basing the arc on it pinned the body a
    // whole step off the ground. A tiny settle at window end covers the rest.
    private Vector3 JumpOffsetWorld()
    {
        if (_jumpRetriggerGuard > 0f)
        {
            _jumpRetriggerGuard -= Time.deltaTime * _simSpeed;
        }

        if (_jumpTimer <= 0f)
        {
            // §21.21B v23: окно закрыто, но корень ещё НЕ на уровне посадки
            // (его Y-снап приходит со снапшотом на тик позже касания, а
            // кадр-фриз мог съесть весь запас). Пока разрыв больше порога,
            // тело держится на АБСОЛЮТНОЙ высоте посадки — ease к отставшему
            // корню и был секундным «провалом» при запрыгивании.
            if (_jumpPinToLanding && _jumpPinGrace > 0f)
            {
                _jumpPinGrace -= Time.deltaTime * Mathf.Max(0.25f, _simSpeed);
                var pinOffset = _jumpEndDesiredY - transform.position.y;
                if (Mathf.Abs(pinOffset) > JumpLandingPinThreshold)
                {
                    _jumpResidualY = pinOffset;
                    return new Vector3(0f, pinOffset, 0f);
                }

                // Корень догнал — остаток уходит обычным ease'ом.
                _jumpPinGrace = 0f;
            }

            // §21.21B v15: window closed. A well-formed arc ended level with the
            // root, so there is nothing left; anything that IS left (clock drift
            // between the arc and the sim, a swim lift fading, a hop cut short by
            // a re-plan) eases out. Dropping it in one frame is exactly what read
            // as the body teleporting onto the ledge.
            if (Mathf.Abs(_jumpResidualY) <= 0.001f)
            {
                _jumpResidualY = 0f;
                return Vector3.zero;
            }

            _jumpResidualY = Mathf.MoveTowards(_jumpResidualY, 0f,
                Time.deltaTime * Mathf.Max(1f, _simSpeed) * JumpSettleSpeed);
            return new Vector3(0f, _jumpResidualY, 0f);
        }

        _jumpTimer -= Time.deltaTime * _simSpeed;
        if (_jumpTimer <= 0f)
        {
            _jumpRetriggerGuard = 0.5f;

            // §57.11: падение кончилось — подъём на ноги на сим-паузе
            // FallRecoverSeconds. Ползущей вставать нечем: она выходит из лупа
            // падения обратно в Crawl и долёживает паузу там (одно ребро
            // смены состояния, не покадровый CrossFade — урок §109.16).
            if (_jumpIsFall)
            {
                _jumpIsFall = false;
                if (_animator != null && _fallClipStarted)
                {
                    var standing = !_legless && _posture != "Crawl";
                    if (standing && _animator.HasState(0, StandUpStateHash))
                    {
                        _animator.CrossFade(StandUpStateName, 0.1f, 0, 0f);
                    }
                    else if (!standing)
                    {
                        _animator.CrossFade("Crawl", 0.15f, 0, 0f);
                    }
                }
            }
        }

        // t over the whole window; map to the FLIGHT fraction tf so the arc
        // is flat during the takeoff beat and pinned at 1 during landing.
        var t = 1f - Mathf.Clamp01(_jumpTimer / Mathf.Max(0.0001f, _jumpDuration));

        // §57.11: клип падения — ровно в момент, когда она уже летит (конец
        // takeoff-бита), не раньше: до кромки она идёт своей походкой.
        if (_jumpIsFall && t > _jumpTakeoffFrac)
        {
            StartFallClip();
        }
        float arc; // 0 at stand level, 1 at target level (>1 = overshoot above)
        if (t <= _jumpTakeoffFrac)
        {
            arc = 0f;
        }
        else if (!_jumpUp && _jumpSyncDownToVisibleXz)
        {
            // §21.21B v22: the renderer interpolates the root one snapshot
            // behind the arc clock. Drive a DROP's vertical from the VISIBLE
            // XZ progress along the sim's own padded flight segment
            // (±EdgePadding around the border), so gravity cannot start until
            // the rendered body has really cleared the upper lip — clock-driven
            // it fell a full step while still standing on the upper hex.
            var along = Vector3.Dot(
                transform.position - _jumpEdgePointWorld,
                _jumpFlightDirectionWorld);
            var pad = HexLive.Simulation.Navigation.HexHopTuning.EdgePadding;
            var horizontalTf = Mathf.InverseLerp(-pad, pad, along);
            arc = JumpVerticalEase(horizontalTf);
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
        // Remembered so the frame AFTER the window can ease out whatever is
        // left instead of cutting it (see the timer-expired branch above).
        var desiredY = _jumpStartY + _jumpHeightDelta * arc;
        _jumpResidualY = desiredY - transform.position.y;
        return new Vector3(0f, _jumpResidualY, 0f);
    }

    // Face anchor rig for the portrait camera, calibrated once in the prefab's
    // upright rest pose: face center, face-forward and face-up are captured in
    // HEAD-BONE space, so at runtime they ride the bone through any pose —
    // walking, sitting, lying flat — and the camera stays nailed to the face.
    private Transform _headBone;
    private Transform _lEye;
    private Transform _rEye;
    private Vector3 _faceLocalCenter;
    private Vector3 _faceLocalForward;
    private Vector3 _faceLocalUp;
    private bool _faceCalibrated;

    public bool TryGetFace(out Vector3 faceCenter, out Vector3 faceForward, out Vector3 faceUp, out float scale)
    {
        scale = _bodyRoot != null ? _bodyRoot.lossyScale.y : transform.lossyScale.y;

        // ⭐ §107.4 r2: рамка лица строится по ГЛАЗАМ, а не по осям кости головы.
        // Оси головы приходилось калибровать один раз — «в позе покоя» пекли
        // transform.forward в пространство кости, — и любой перекос в тот
        // момент (или рига, у которого локальные оси кости переставлены при
        // импорте FBX) навсегда уезжал в каждый снимок: лицо выходило чуть
        // повёрнутым. Два глаза задают горизонтальную ось лица однозначно и
        // без всякой калибровки, поэтому камера встаёт строго анфас.
        if (_lEye != null && _rEye != null)
        {
            var left = _lEye.position;
            var right = _rEye.position;
            var across = right - left;
            if (across.sqrMagnitude > 1e-8f)
            {
                across.Normalize();

                // Вертикаль берём МИРОВУЮ — кадр не должен заваливаться вслед
                // за наклоном головы. Если её всё же положило набок (across
                // почти вертикален), падаем на ось головы, иначе векторное
                // произведение вырождается.
                var upRef = Vector3.up;
                if (Mathf.Abs(Vector3.Dot(across, upRef)) > 0.9f)
                {
                    upRef = _faceCalibrated && _headBone != null
                        ? _headBone.TransformDirection(_faceLocalUp).normalized
                        : transform.up;
                }

                faceForward = Vector3.Cross(across, upRef).normalized;
                faceUp = Vector3.Cross(faceForward, across).normalized;
                // Центр кадра чуть НИЖЕ линии глаз: иначе подбородок срезан, а
                // над макушкой пусто.
                faceCenter = (left + right) * 0.5f - faceUp * (0.02f * scale);
                return true;
            }
        }

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

    // §80 r2: годится ли она сейчас для СНИМКА. Лицевой якорь честно едет за
    // костью в любой позе, но фотография лёжа, вплавь или вниз головой — это
    // не портрет, а кадр из падения. Такую съёмку откладываем до следующего
    // захода, благо кадр стоит доли миллисекунды.
    public bool IsPhotogenic
    {
        get
        {
            if (_laying || _swimming || !_faceCalibrated || _headBone == null)
            {
                return false;
            }

            // §80 r3: причёска грузится асинхронно, и снимок, сделанный до её
            // прихода, — это ЛЫСАЯ колонистка во всех списках до конца игровых
            // суток. Кадр стоит доли миллисекунды; дождаться волос дешевле.
            if (_pendingHairLoads > 0)
            {
                return false;
            }

            // Голова держится вертикально: наклон до ~50° прощаем (она может
            // смотреть под ноги), кувырок — нет.
            var up = _headBone.TransformDirection(_faceLocalUp).normalized;
            return Vector3.Dot(up, Vector3.up) > 0.64f;
        }
    }

    // §80 r3: ПОЗА ДЛЯ СНИМКА — стоя, а не в шаге. Фотографии должны быть
    // одинаковыми, а на ходу корпус несёт, голова качается на шаге, и один
    // портрет выходит анфас, другой — в наклоне посреди стрида. Ждать почти
    // ничего не стоит: съёмка идёт раз в игровые сутки и просто откладывается
    // до ближайшей остановки.
    public bool IsPortraitPoseSettled => _gait <= PortraitStillGait;

    // §130: уместно ли ей сейчас стрельнуть глазами в объектив. Фотогеничность
    // отсекает лежащих/плывущих/запрокинутых; сверх того — не мёртвая, не
    // ragdoll, не в бою и не в слезах (улыбка в камеру посреди драки или
    // рыданий читалась бы как сломанное лицо).
    public bool IsCameraGazeEligible =>
        IsPhotogenic && !_dead && !_ragdollActive && !_fighting && !_crying;

    // §131: единая точка ответа «на какой высоте голова» для камеры. Поза уже
    // сведена во флаги (лежит/плывёт/сидит/ragdoll) — камера спрашивает ЗДЕСЬ,
    // а не читает кость головы: кость дышит и шагает, пивот бы качало. Ответ —
    // высота над корнем тела в wu; дискретный на позу, сглаживание делает
    // SmoothDamp пивота в контроллере.
    public float CameraAnchorHeight
    {
        get
        {
            var r = Spatial.SimulationUnityMapper.HexRadius;
            if (_dead || _ragdollActive || _laying)
            {
                return r * 0.16f;
            }

            if (_swimming)
            {
                return r * 0.24f;
            }

            // §137: праздный отдых — сидение НА ЗЕМЛЕ, и оно заметно ниже
            // сидения на мебели ниже. Число не на глаз: в клипе «X Bot@Sitting
            // Idle» голова стоит на 0.642 м против 1.421 м в стоячей позе того
            // же скелета, то есть 0.45 от стоячей — отсюда 0.62 × 0.45 ≈ 0.28.
            // Оказывается между плаванием и стулом, как и должно быть.
            if (_resting)
            {
                return r * 0.28f;
            }

            if (_sitting)
            {
                return r * 0.44f;
            }

            return r * 0.62f; // стоя: шея, прежний _orbitNeckFactor
        }
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
        // Глаза — основная рамка лица (см. TryGetFace); кость головы остаётся
        // запасной, для рига без глаз и для проверки «стоит ли она вообще».
        _lEye = _bodyBones != null ? _bodyBones.GetBone("lEye") : null;
        _rEye = _bodyBones != null ? _bodyBones.GetBone("rEye") : null;
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
        Construct(actorMeshName, npcId, null, null, null, null);
    }

    // §74: a girl is a COMPOSITION. The mesh still decides the body — and with
    // it the garment fits (WearConfig) and the skin paint-point map, both
    // properties of the geometry — but the material set, the hairstyle and the
    // voice bank now come in separately and may belong to someone else.
    //
    // §85 adds a fourth: the iris. It used to ride inside the skin set, so
    // "her mother's face with her father's eyes" was unsayable and the whole
    // island drew from four irises.
    //
    // Every one of the four is optional: null/empty means "the mesh's own",
    // i.e. exactly the pre-§74 body, which is what a test scene and a pre-§74
    // save both get.
    public void Construct(string actorMeshName, int npcId,
        string skinSet, string eyeColor, string hairstyle, string voiceBank)
    {
        _npcId = npcId;
        LiveByNpcId[npcId] = this;
        // §67.6: голосовой банк персонажа = его меш-имя (Molly/Jana/…) —
        // файлы voice_<char>_<emotion>_<n> подхватываются по факту наличия.
        // §74: …если сим не выдал ей ЧУЖОЙ банк — тогда играет он.
        var voice = string.IsNullOrEmpty(voiceBank) ? actorMeshName : voiceBank;
        _voiceChar = (voice ?? string.Empty).Trim().ToLowerInvariant();
        // §67.7: липсинк на реплики — сэмплеру .vis-таймлайнов нужна голова
        // с виземами (у примитивных фолбэк-капсул её нет, там и рта-то нет).
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
            // §78: always index and wrap the AUTHORED controller. A second
            // Construct on the same instance (the test scenes swap the actor in
            // place) would otherwise wrap the previous override, and the clips
            // it had already swapped in would become the new base KEYS — after
            // which nothing could ever hand the original takes back.
            var authored = _animator.runtimeAnimatorController;
            if (authored is AnimatorOverrideController wrapped &&
                wrapped.runtimeAnimatorController != null)
            {
                authored = wrapped.runtimeAnimatorController;
            }

            foreach (var clip in authored.animationClips)
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
            _animOverride = new AnimatorOverrideController(authored);
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

        ApplySleepPose();

        if (System.Enum.TryParse(actorMeshName, out ActorName parsed) == false)
        {
            Debug.LogError($"Unknown actor mesh '{actorMeshName}'. Actor construction aborted: " +
                           "substituting another actor body is forbidden by the FBX contract.", this);
            enabled = false;
            return;
        }

        _actorMesh = parsed;
        // §78: his own walk/idle/sit, now that we know whose body this is.
        ApplyActorLocomotion();
        if (_bodyBones != null)
        {
            _bodyBones.Construct(_actorMesh);
            SyncHeelPoseTarget();
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
            RefreshAnimatorCulling();
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
            // §85: and her eyes after it, because the skin set carries an eye
            // map of its own — applying the eye set first would let the donor's
            // irises overwrite the rolled ones. Both still land BEFORE
            // BuildSkinTintTargets for the reason above.
            ApplyEyeSet(eyeColor);
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
        if (LiveByNpcId.TryGetValue(_npcId, out var registered) && registered == this)
        {
            LiveByNpcId.Remove(_npcId);
        }

        if (_fullBodyIK != null && _fullBodyIK.solver != null)
        {
            _fullBodyIK.solver.OnPreUpdate -= DriveActionTargetIK;
        }

        if (_carryGraph.IsValid())
        {
            _carryGraph.Destroy();
        }
    }

    /// <summary>§118.4: живой актёр по id NPC — для CarriedPoseFollower.</summary>
    internal static bool TryGetLive(int npcId, out NpcActorView view)
    {
        return LiveByNpcId.TryGetValue(npcId, out view) && view != null;
    }

    /// <summary>§118.4: аниматор тела — CarriedPoseFollower целится в кости.</summary>
    internal Animator BodyAnimator => _animator;

    /// <summary>§118.4: рэгдоллом владеет физика — поверх неё не штампуем.</summary>
    internal bool RagdollActive => _ragdollActive;

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
        IReadOnlyList<string> severedParts = null,
        IReadOnlyList<BodyPartConditionSnapshot> partConditions = null)
    {
        // Spec §50: hide any limb that has been severed. The deep stump wound
        // the sim filed on the zone paints itself onto the remaining stub via
        // the normal wound path below — no special stump art needed.
        ApplySeveredLimbs(severedParts);
        SyncProstheticVisuals(partConditions);
        SyncHandedness(partConditions);
        RefreshLeglessPresentation();

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
            // Spec 40.8-H r10: кровяная подложка = грязь. Спеклы кормит
            // накопительный BloodSoil из сима (растёт только от режущего
            // урона в WoundMath.InflictCut, смывается только водой), а не
            // производная от HP зон — умирающая от голода/жажды больше не
            // краснеет, отхил ран кровь не «рассасывает». Onset/кривая/
            // neighbour-bleed по-прежнему живут в painter'е (одно место
            // тюнинга). Severed-зоны исключены: конечности нет, визуал несёт
            // рана культи.
            _zoneDamageScratch.Clear();
            if (partConditions != null)
            {
                // Как и раньше: если кровь есть хоть где-то, едут ВСЕ
                // не-severed зоны (и нулевые) — painter растекает цвет на
                // соседей только по зонам, присутствующим в списке.
                var anyBloodSoil = false;
                foreach (var part in partConditions)
                {
                    if (!part.Severed && part.BloodSoil > 0f)
                    {
                        anyBloodSoil = true;
                        break;
                    }
                }

                if (anyBloodSoil)
                {
                    foreach (var part in partConditions)
                    {
                        if (!part.Severed)
                        {
                            _zoneDamageScratch.Add((ZoneName(part.Part),
                                Mathf.Clamp01(part.BloodSoil)));
                        }
                    }
                }
            }

            // §40.8-H r11: СИНЯКИ — второй слой тех же частиц, фиолетовый и
            // прозрачный. Источник — `BluntDamage` зоны (тупой урон: кулаки,
            // дубина, падение), который сим уже возит в кондициях и сам
            // рассасывает при заживлении (KenshiMedicalMath.TickBluntRecovery).
            // Водой НЕ смывается — это под кожей, не на ней. Список без
            // растекания на соседей: едут только реально ушибленные зоны.
            _zoneBruiseScratch.Clear();
            if (partConditions != null)
            {
                foreach (var part in partConditions)
                {
                    if (!part.Severed && part.BluntDamage > 0f)
                    {
                        _zoneBruiseScratch.Add((ZoneName(part.Part),
                            Mathf.Clamp01(part.BluntDamage)));
                    }
                }
            }

            // The painter needs the current wet-skin gloss: it is the BASE of
            // the painted gloss map, so droplet pixels (0.95) sit on top of
            // the same sheen the rest of the body shows.
            var wetSmoothnessForPaint = Mathf.Lerp(DrySkinSmoothness, WetSkinSmoothness, _skinWetness);
            _skinPainter.Sync(_woundScratch, _bandagedScratch,
                PaintSweatDroplets ? _skinWetness : 0f, _uncoveredScratch, wetSmoothnessForPaint,
                _gauzeScratch, _zoneDamageScratch, _zoneBruiseScratch);
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

            var bone = _bodyBones.GetBone(boneName);
            if (bone != null)
            {
                if (System.Enum.TryParse(zone, out BodyPart part) &&
                    !_severedBoneOriginalScale.ContainsKey(part))
                {
                    _severedBoneOriginalScale[part] = bone.localScale;
                }

                // Not exactly zero — a degenerate scale can NaN the skinning;
                // a tiny non-zero collapses the sub-tree below visible size.
                bone.localScale = new Vector3(1e-4f, 1e-4f, 1e-4f);
            }
        }

        _severVfxPrimed = true;
    }

    private void SyncProstheticVisuals(IReadOnlyList<BodyPartConditionSnapshot> conditions)
    {
        _prostheticZones.Clear();
        var armAttachmentChanged = false;

        if (conditions != null)
        {
            foreach (var condition in conditions)
            {
                if (condition?.Prosthetic == null ||
                    condition.Part is not (BodyPart.ArmL or BodyPart.ArmR or BodyPart.LegL or BodyPart.LegR))
                {
                    continue;
                }

                _prostheticZones.Add(condition.Part);
                if (_prostheticVisuals.TryGetValue(condition.Part, out var existing) &&
                    existing.Matches(condition.Prosthetic))
                {
                    existing.Update(condition);
                    continue;
                }

                if (existing != null)
                {
                    existing.Destroy();
                    _prostheticVisuals.Remove(condition.Part);
                }

                var visual = ProstheticVisual.Create(
                    _bodyRoot != null ? _bodyRoot : transform,
                    _bodyBones,
                    condition,
                    _severedBoneOriginalScale.TryGetValue(condition.Part, out var originalScale)
                        ? originalScale
                        : Vector3.one);
                if (visual != null)
                {
                    _prostheticVisuals[condition.Part] = visual;
                }

                armAttachmentChanged |= condition.Part is BodyPart.ArmL or BodyPart.ArmR;
            }
        }

        _prostheticRemoveScratch.Clear();
        foreach (var pair in _prostheticVisuals)
        {
            if (!_prostheticZones.Contains(pair.Key))
            {
                _prostheticRemoveScratch.Add(pair.Key);
            }
        }

        foreach (var part in _prostheticRemoveScratch)
        {
            if (_prostheticVisuals.TryGetValue(part, out var visual))
            {
                visual.Destroy();
            }

            _prostheticVisuals.Remove(part);
            armAttachmentChanged |= part is BodyPart.ArmL or BodyPart.ArmR;
        }

        if (armAttachmentChanged && !string.IsNullOrEmpty(_currentPropId))
        {
            var held = _currentPropId;
            _currentPropId = null;
            SetHandProp(held);
        }
    }

    private Transform ActingHandPropAnchor()
    {
        if (!_hasUsableHand)
        {
            return null;
        }

        var part = _leftHanded ? BodyPart.ArmL : BodyPart.ArmR;
        if (_prostheticVisuals.TryGetValue(part, out var prosthetic) && prosthetic.Grip != null)
        {
            return prosthetic.Grip;
        }

        return _bodyBones?.GetBone(_leftHanded ? "lHand" : "rHand");
    }

    private void RefreshLeglessPresentation()
    {
        // §50: «вставать нечем» — это ФУНКЦИЯ НОГИ В НУЛЕ, а не подсказка позы.
        //
        // Подсказка ранжирована, и обморок в ней стоит выше ползания: стоило
        // упасть, как PostureHint становился «Faint», и вид забывал про ноги.
        // Оторванную он всё равно узнавал по зонам ампутации, а РАЗБИТУЮ В НОЛЬ,
        // но целую — нет. Получалось, что одна и та же беспомощность падает
        // двумя разными способами в зависимости от того, оторвало ногу или
        // просто добило до нуля. Теперь факт едет своим полем (LegsLost) и
        // обморок его не стирает.
        var missingLeg = _legsLost || _posture == "Crawl" ||
            (_severedZones.Contains("LegL") && !_prostheticZones.Contains(BodyPart.LegL)) ||
            (_severedZones.Contains("LegR") && !_prostheticZones.Contains(BodyPart.LegR));
        if (_legless == missingLeg)
        {
            return;
        }

        _legless = missingLeg;
        if (_legless)
        {
            ApplyLeglessClipOverrides();
        }
        else
        {
            RestoreLeglessClipOverrides();
            ResolveLocomotionSlots();

            // §50: С ЗЕМЛИ ВСТАЮТ, А НЕ ПОЯВЛЯЮТСЯ СТОЯ.
            //
            // Обратный переход — «нога снова держит» (поставили протез, зажила
            // культя) — раньше просто подменял лежачий айдл на стоячий, и тело
            // щёлкало из позы лёжа в стойку одним кадром. Теперь играем клип
            // подъёма (состояние StandUp, X Bot@Standing Up); дальше граф сам
            // уводит её в Idle по exit time, поэтому ничего доигрывать руками
            // не нужно.
            //
            // ⚠️ Только по РЕБРУ: метод выходит выше, если _legless не менялся
            // (см. ранний return), а рендерер зовёт SetPosture каждый тик —
            // покадровый CrossFade это §109.15.
            //
            // Лежащую не поднимаем: она встанет своей цепочкой Sleep -> GetUp,
            // когда сим её разбудит. Мёртвую и плывущую — тем более.
            if (_animator != null && !_laying && !_swimming && !_dead && !_ragdollActive)
            {
                _animator.CrossFadeInFixedTime(StandUpStateName, StandUpBlendSeconds, 0);
            }
        }

        // A repaired leg allows work/weapons again; a newly bare stump removes
        // them immediately. Re-run the normal prop gate in either direction.
        if (!string.IsNullOrEmpty(_currentPropId))
        {
            var held = _currentPropId;
            _currentPropId = null;
            SetHandProp(held);
        }
    }

    private void RestoreLeglessClipOverrides()
    {
        var clips = new[]
        {
            "crouch",
            "TurnOnSpotRightB",
            "TurnOnSpotLeftA",
            "X Bot@Gathering Objects",
            "X Bot@Talking",
            "X Bot@Dressing",
            "X Bot@Drinking"
        };
        foreach (var baseName in clips)
        {
            if (_clipsByName.TryGetValue(baseName, out var authored))
            {
                OverrideClip(baseName, authored);
            }
        }
    }

    // Spec §50: the one-time clip swaps for a legless survivor — the states that
    // carry a FIXED clip (idle, walk, crouch, turn-on-spot). Gather/Talk/Dress
    // re-apply their clip each frame, so those are handled in Standing() at the
    // call sites; Sit/Sleep/LieDown keep their own clips.
    private void ApplyLeglessClipOverrides()
    {
        // §71.5: the idle and ALL THREE gait slots are the resolver's — being
        // legless is simply its top priority, and it fills every gait slot with
        // the crawl so no Gait the view computes can blend her into a run.
        ResolveLocomotionSlots();
        // Everything else standing → the prone idle. (The game leaves these base
        // clips in place — no NpcAnimSet action variants — so overriding the base
        // clip name here is what actually swaps them.)
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

    // Spec §50: all consumers resolve the same complete FBX body. There are
    // no actor-name, renderer-name or vertex-count fallbacks.
    public SkinnedMeshRenderer PrimaryBodySkin => ActorBodyResolver.ResolveOrNull(this);

    // The severed-limb pose baker clones only this transform hierarchy (bones
    // and their current local pose), never the live actor GameObject with its
    // scripts. Keeping the source here makes the bake follow the exact same
    // body root as animation, lying offsets and the one shared FBX flow.
    internal Transform SeveredLimbPoseRoot => _bodyRoot != null ? _bodyRoot : transform;

    // ApplySeveredLimbs remembers the authored scale before collapsing the
    // distal chain. A temporary pose clone restores that scale long enough to
    // bake the limb in the post-fall pose; the rendered actor stays collapsed.
    internal bool TryGetSeveredLimbOriginalScale(string zone, out Vector3 scale)
    {
        scale = Vector3.one;
        if (!System.Enum.TryParse(zone, out BodyPart part) ||
            !SeveredDistalBone.TryGetValue(zone, out var boneName))
        {
            return false;
        }

        if (_severedBoneOriginalScale.TryGetValue(part, out scale))
        {
            return true;
        }

        // This also supports a capture made before the first collapse write.
        // Refuse an already-collapsed scale without a remembered original: a
        // nearly-zero clone would bake another invisible limb.
        var bone = _bodyBones?.GetBone(boneName);
        if (bone == null || bone.localScale.sqrMagnitude < 0.001f)
        {
            return false;
        }

        scale = bone.localScale;
        return true;
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
            _clashedSimItems.Remove(simId);
        }

        // A garment coming OFF can free the slot a clashing one lost, so a real
        // wardrobe change earns everyone exactly one more attempt. Ticking does
        // not — that is the whole point of the mark.
        if (_removeScratch.Count > 0)
        {
            _clashedSimItems.Clear();
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

            // The repair is a ONE-SHOT (see _clashedSimItems). Equipping a
            // garment means instantiating the prefab and stitching every one of
            // its ~57 bones onto the body; when the piece is evicted the instant
            // it lands, repeating that each tick is a permanent ~100 ms/tick
            // fire that the console never mentions (the warning below is
            // once-per-id). The piece then stays MISSING until the data is
            // fixed — a visible, greppable symptom, which is what we want.
            if (_clashedSimItems.Contains(simId))
            {
                continue;
            }

            var isNewItem = !_equippedSimItems.ContainsKey(simId);
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
            // §52.9 r2: reaching this branch at all is now a GATE failure, not a
            // discovery — WearSlotGateTests proves no such pair exists headless
            // (`dotnet test Tests/HexLive.Simulation.Tests`). It stays as the
            // last line of defence for art that never reached the gate.
            if (prefabs.Count > 0 && !_bodyBones.IsEquipped($"{simId}#0"))
            {
                // Do not try again on the next tick — see _clashedSimItems.
                _clashedSimItems.Add(simId);
                if (_slotClashWarned.Add(simId))
                {
                    Debug.LogWarning(
                        $"[Wear] '{simId}' was evicted the instant it was equipped, by " +
                        $"{_bodyBones.DescribeSlotOwners(prefabs[0])} — their visual " +
                        "(layer, slot) collide while the sim allows both to be worn. " +
                        "Sync WearSlotCatalog (and the sim WearLayer) TO this prefab — " +
                        "do not coarsen the prefab's slots to match Covers. " +
                        "The piece stays OFF the body until that is fixed: re-stitching " +
                        "it every tick was ~100 ms/tick and leaked a material set each time. " +
                        "Reproduce and fix it headless: dotnet test Tests/HexLive.Simulation.Tests " +
                        "(WearSlotGateTests names the exact pair and slot).",
                        this);
                }
            }
            else if (isNewItem)
            {
                // A genuinely new garment landed — the slot map moved, so anyone
                // previously blocked deserves one more attempt.
                _clashedSimItems.Clear();
            }
        }

        // Баг #124: ВЗАИМНОЕ вытеснение — A выгоняет B, B выгоняет A. Проверка
        // внутри цикла его не видит В ПРИНЦИПЕ: каждая вещь ПОБЕЖДАЕТ в своём
        // собственном Equip и проигрывает уже потом, чужому. Поэтому пара
        // пересоздавала друг друга каждый тик ВЕЧНО, без единой строки в
        // консоли: ~57 костей и набор материалов на сторону, а с открытым
        // инвентарём сверху ложилась полная пересборка куклы (~21 мс), и кадр
        // уезжал в 4-6 FPS. Приговор выносится ПОСЛЕ всего прохода, когда
        // состав тела уже окончателен, — тем же одноразовым способом, что и
        // выше: проигравшая помечается и больше не пытается.
        foreach (var simId in wornDefinitionIds)
        {
            if (_clashedSimItems.Contains(simId) ||
                !_equippedSimItems.TryGetValue(simId, out var equippedCount) ||
                equippedCount == 0 ||
                _bodyBones.IsEquipped($"{simId}#0"))
            {
                continue;
            }

            _clashedSimItems.Add(simId);
            if (_slotClashWarned.Add(simId))
            {
                var rival = ActorWardrobe.GetVisuals(simId);
                Debug.LogWarning(
                    $"[Wear] '{simId}' and " +
                    (rival.Count > 0 ? _bodyBones.DescribeSlotOwners(rival[0]) : "another garment") +
                    " evict each other every tick — their visual (layer, slot) collide " +
                    "while the sim allows both to be worn at once. The sim must not " +
                    "produce such a pair: EquipmentMath.StripConflictingWorn keeps one " +
                    "of them off the body (§52.9). Until the data is fixed this piece " +
                    "stays MISSING instead of re-stitching ~57 bones every tick.",
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

    // §137: ей нечем заняться — она садится на землю там, где стоит, и встаёт,
    // когда появляется дело. Позу и обе переходные ноги держит контроллер
    // (RestDown → RestIdle → RestUp); отсюда идёт ровно один bool.
    //
    // ⭐ По РЕБРУ, как и лежание: рендерер зовёт это каждый тик, а покадровая
    // запись в аниматор — тот самый капкан §109.15.
    //
    // Ползущая и безногая не садятся вовсе: стойки у них нет, а значит нет и
    // того движения, которое эти клипы изображают. Симуляция их и не сажает
    // (IdleRestMath.Blocked), но вид обязан быть верен сам по себе — он тот же
    // и в тестовых сценах, где симуляции нет.
    private bool _resting;

    public void SetResting(bool resting)
    {
        resting = resting && !_legless && _posture != "Crawl" && !_laying;
        if (_resting == resting)
        {
            return;
        }

        _resting = resting;
        if (_animator != null)
        {
            _animator.SetBool(RestingParam, resting);
        }
    }

    // Spec 31C.2: sleeping snaps the view to the bed's attach point and
    // plays the Laying state; waking releases back to the renderer's flow.
    public void SetLaying(bool laying, Transform attachPoint, float surfaceY = 0f) =>
        ApplyLying(laying, attachPoint, surfaceY, fallenChain: false, sleepAfterFall: false);

    // §105: тело РУХНУЛО — умирает (окно спасения) или лежит без сознания.
    //
    // Вся механика лежания — пиннинг в центр гекса, per-frame bounds против
    // frustum-culling, закрытые глаза, обнулённая скорость — переиспользуется
    // ЦЕЛИКОМ: разница ровно одна, какой цепочкой клипов её положили. Сон
    // уходит в кровать своим LieDown→Sleep→GetUp и держит её позу; падение
    // приходит откуда угодно, включая бег и драку, и играет
    // FallDown→FallenIdle→StandUp. Два bool'а взаимно исключаются здесь, в
    // одном месте, а не в четырёх ветках рендерера.
    /// <param name="sleepAfter">
    /// Чем кончается падение. false — лежачий луп: кома и умирание, тело
    /// безвольно лежит, пока его не поднимут. true — обычный сон: она потеряла
    /// сознание, рухнула так же, но дальше просто спит и встаёт обычным GetUp.
    /// </param>
    public void SetFallen(
        bool fallen, bool sleepAfter = false, float surfaceY = 0f, Transform attachPoint = null) =>
        ApplyLying(fallen, attachPoint, surfaceY, fallenChain: true, sleepAfterFall: sleepAfter);

    // §110: сломалась от стресса — лежит и плачет. Ложится она сонной цепочкой
    // (SetLaying), а этот переключатель добавляет к ней то, чем плач ОТЛИЧАЕТСЯ
    // от сна: вторая поза сна вместо её личной (свернувшийся клубок читается
    // как «плачет», а её привычная поза — как «прилегла») и держащаяся гримаса
    // плача вместо закрытых во сне глаз.
    public void SetCrying(bool crying)
    {
        if (_crying == crying)
        {
            return;
        }

        _crying = crying;
        if (crying)
        {
            ApplyCryingPose();
        }
        else
        {
            ApplySleepPose(); // вернуть её собственную позу сна
        }

        if (_face != null)
        {
            _face.SetCrying(crying);
        }
    }

    private void ApplyLying(
        bool laying, Transform attachPoint, float surfaceY, bool fallenChain, bool sleepAfterFall)
    {
        // ⚠️ РЕНДЕРЕР ЗОВЁТ ЭТО КАЖДЫЙ ТИК, а не на смене позы: ветки
        // SyncActorView разбирают позу заново на каждом снапшоте. Всё, что
        // должно случиться ОДИН РАЗ (в первую очередь любая команда аниматору),
        // обязано смотреть на этот флаг, иначе получится покадровый CrossFade
        // из §109.15 — вечный переход с весом ~0.04 и таз под полом.
        var chainChanged = _laying != laying ||
            _lyingChainFallen != fallenChain ||
            _lyingChainSleepAfter != sleepAfterFall;
        _lyingChainFallen = fallenChain;
        _lyingChainSleepAfter = sleepAfterFall;

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
        // §137: легла — значит уже не сидит. Снимается ЗДЕСЬ, а не в
        // рендерере: лежачих веток четыре, и забыть флаг в одной из них
        // означало бы цепочку отдыха, спорящую с цепочкой сна за одно тело.
        if (laying && _resting)
        {
            _resting = false;
            if (_animator != null)
            {
                _animator.SetBool(RestingParam, false);
            }
        }

        SyncHeelPoseTarget();
        RefreshAnimatorCulling();
        _layingAttach = attachPoint;
        _layingSurfaceY = surfaceY;
        // Spec 31C.8: lying poses stretch outside the authored skin bounds and
        // get frustum-culled; per-frame bounds while sleeping keep her visible.
        // Труп держит их по своей причине (см. RefreshSkinBounds), поэтому
        // решение принимается там, а не флагом лежания здесь.
        RefreshSkinBounds();

        if (_animator != null)
        {
            // §105: Fallen решает, ПАДАЕТ ли она; Laying — чем это кончится.
            //
            // У потерявшей сознание подняты ОБА: она валится клипом падения и
            // приземляется в сон. Поэтому вход в сонную цепочку в контроллере
            // дополнительно требует «Fallen == false» — иначе она уходила бы в
            // аккуратное LieDown вместо падения.
            _animator.SetBool(LayingParam, laying && (!fallenChain || sleepAfterFall));
            _animator.SetBool(FallenParam, laying && fallenChain);

            // §50: ПОЛЗУЩАЯ НЕ ВСТАЁТ, ЧТОБЫ ЛЕЧЬ.
            //
            // Сонная цепочка контроллера — LieDown -> Sleep -> GetUp — писана
            // для ходячей: она опускается из стойки и поднимается обратно в
            // стойку. У безногой стойки нет вовсе, поэтому эти два клипа
            // читались как «встала на ноги, легла» и «встала с кровати»: ровно
            // то, чего она физически не может. Она уже на земле — значит вход
            // в сон и выход из него для неё не движение, а смена позы.
            //
            // Прыгаем сразу в конечное состояние, минуя переходный клип: в сон
            // при укладывании, в Idle (у неё это лежачий айдл, §50-подмена) при
            // подъёме. Переход короткий, но не мгновенный — обе позы наземные,
            // так что смешивать их безопасно, а щелчок кадра в глаза бьёт.
            //
            // ⚠️ ТОЛЬКО ПО РЕБРУ (chainChanged). Рендерер зовёт ApplyLying
            // каждый тик, а покадровый CrossFade (§109.15) навсегда застревает
            // в переходе с весом ~0.04 и роняет таз под пол.
            if (chainChanged && (_legless || _posture == "Crawl"))
            {
                // Куда именно — решает ТА ЖЕ пара флагов, что и цепочка выше,
                // иначе §105 (кома/умирание) уехала бы в сон вместо лежачего
                // FallenIdle: у неё Fallen поднят, а Laying нет.
                var target = laying
                    ? (fallenChain && !sleepAfterFall ? FallenIdleStateName : SleepStateName)
                    : IdleStateName;
                _animator.CrossFadeInFixedTime(target, LyingPoseSwapSeconds, 0);
            }
        }

        if (_face != null)
        {
            _face.SetSleeping(laying);
        }
    }

    // Face mood: aggregate wellbeing (0..1) + combat flag from the snapshot.
    public void SetFaceMood(float wellbeing, bool fighting)
    {
        _fighting = fighting; // §130: в бою в объектив не смотрим
        if (_face != null)
        {
            _face.SetMood(wellbeing, fighting);
        }
    }

    private bool _fighting;

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

    // §28.15C v3: СМЕРТЬ — она падает, и на этом всё.
    //
    // Раньше смерть была тихим «лечь спать»: тело уходило в обычный LieDown →
    // Sleep и там замирало, потому что клипов падения в наборе не лежало вовсе
    // (`death: []`). Теперь их два (death2/death3), состояние Death в
    // контроллере не имеет выхода, а клипы импортированы с Loop Time OFF — то
    // есть падение проигрывается ОДИН раз и держит последний кадр.
    //
    // Дальше аниматор ВЫКЛЮЧАЕТСЯ совсем. Не «speed = 0», а enabled = false:
    // поза остаётся ровно той, на которой кончился клип, и её больше некому
    // сдвинуть — ни дыханию, ни взгляду, ни фиджетам. При переносе включается
    // только BeingCarried, после укладки запомненная мёртвая поза снова замирает.
    private bool _dead;
    private bool _deadWasAlreadyLying;
    private bool _corpseCarried;
    private float _deathFreezeAt = -1f; // Time.time, когда клип докрутится
    private float _deathSurfaceY;
    private static readonly int DeathStateHash = Animator.StringToHash("Death");
    private static readonly int FallenIdleStateHash = Animator.StringToHash("FallenIdle");

    /// <summary>#79: true when death should be silent and preserve this pose.</summary>
    public bool IsLyingStill => _laying;

    /// <param name="variant">Какой из клипов падения — число из СИМУЛЯЦИИ, а
    /// не Random: иначе одно и то же тело лежало бы по-разному у каждого
    /// зрителя и после каждой перезагрузки.</param>
    /// <param name="fresh">Она упала прямо сейчас (проиграть) или лежит с
    /// прошлой сессии (сразу последний кадр). Второе — это загрузка сейва и
    /// подключение зрителя к идущему миру: там никто не должен увидеть, как
    /// давно погибшая падает заново.</param>
    public void SetDead(float surfaceY, int variant = 0, bool fresh = true)
    {
        if (_dead)
        {
            return; // односторонний переход; повторный вызов ничего не значит
        }

        _dead = true;
        _deadWasAlreadyLying = variant < 0;
        _deathSurfaceY = surfaceY;
        RefreshAnimatorCulling();
        RefreshSkinBounds();
        // §50: a corpse never crawls — clear the flag so the Crawl loop yields
        // to the death/laying pose (the Crawl transition also guards on !Dead).
        if (_animator != null)
        {
            _animator.SetBool(CrawlingParam, false);
        }

        // #79: the simulation persists a negative variant when this character
        // died already lying still. A live view freezes the exact current pose;
        // a restored corpse reconstructs the same id-stable FallenIdle pose and
        // freezes it immediately. Neither path enters the Death state.
        if (variant < 0)
        {
            if (!_laying)
            {
                SetFallen(true, sleepAfter: false, surfaceY: surfaceY);
                if (_animator != null)
                {
                    _animator.Play(FallenIdleStateHash, 0, 0f);
                    _animator.Update(0f);
                }
            }

            FreezeDeathPose();
            return;
        }

        var clips = _animSet != null ? _animSet.death : null;
        if (_animator == null || clips == null || clips.Length == 0)
        {
            // Клипов не назначили — старое поведение: замереть лёжа. Заметно
            // хуже, но это поза, а не дыра в кадре.
            SetLaying(true, null, surfaceY);
            return;
        }

        var clip = clips[((variant % clips.Length) + clips.Length) % clips.Length];
        OverrideClip(DeathBaseClip, clip);
        _animator.SetBool(DeadParam, true);

        if (fresh)
        {
            // Дать переходу отыграть, а потом заморозить. Длина берётся у
            // САМОГО клипа: захардкоженная секунда разошлась бы с ним при
            // первой же замене (ровно эта грабля — §104 r4).
            _deathFreezeAt = Time.time + clip.length + 0.35f;
            return;
        }

        // Загруженное тело: без перехода, сразу конец клипа, и заморозить в
        // этом же кадре — падения никто не увидит.
        _animator.Play(DeathStateHash, 0, 1f);
        _animator.Update(0f);
        FreezeDeathPose();
    }

    // Выключить аниматор насовсем. Поза остаётся той, что в костях сейчас.
    private void FreezeDeathPose()
    {
        _deathFreezeAt = -1f;
        if (_animator != null)
        {
            _animator.enabled = false;
        }
    }

    /// <summary>§124: PlayableGraph переносимой позы требует включённый
    /// Animator. После выкладывания восстанавливаем авторский последний кадр
    /// смерти, а не замораживаем вертикальную позу из рук носильщика.</summary>
    internal void SetCorpseCarried(bool carried)
    {
        if (!_dead || _animator == null || _corpseCarried == carried)
        {
            return;
        }

        _corpseCarried = carried;
        if (carried)
        {
            _deathFreezeAt = -1f;
            _animator.enabled = true;
            return;
        }

        _animator.enabled = true;
        _animator.Play(_deadWasAlreadyLying ? FallenIdleStateHash : DeathStateHash,
            0, _deadWasAlreadyLying ? 0f : 1f);
        _animator.Update(0f);
        FreezeDeathPose();
    }

    // Клип смерти авторский, а земля — свойство мира. У одних наборов костей
    // последний кадр уже лежит на нуле, у других содержит небольшой baked Y
    // offset: без этой привязки такой труп после падения «улетает» или висит
    // над склоном. Берём фактический нижний край скина, поэтому правило верно
    // для любой одежды, позы и высоты тайла.
    private void PlantDeadBodyOnSurface()
    {
        if (!_dead || _bodyRoot == null)
        {
            return;
        }

        var lowestY = float.PositiveInfinity;
        if (_bodySkins != null)
        {
            foreach (var skin in _bodySkins)
            {
                if (skin != null && skin.enabled && skin.gameObject.activeInHierarchy)
                {
                    lowestY = Mathf.Min(lowestY, skin.bounds.min.y);
                }
            }
        }

        if (float.IsPositiveInfinity(lowestY))
        {
            _bodyRoot.position = new Vector3(
                _bodyRoot.position.x, _deathSurfaceY, _bodyRoot.position.z);
            return;
        }

        _bodyRoot.position += Vector3.up * (_deathSurfaceY - lowestY);
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

        // §104 r9: ФОЛБЭК для урона НЕ от удара — падение, акула, огонь: там
        // хит-штампа нет, и о том, откуда прилетело, сказать нечего. Толкаем
        // корпус назад от её же взгляда. Настоящий удар сюда не доходит: он
        // уже отыгран по штампу, со своей зоной и своим направлением.
        if (_hitRecoilAge < HitRecoilSeconds)
        {
            return;
        }

        var root = _bodyRoot != null ? _bodyRoot : transform;
        StartHitRecoil("Torso", root.position + root.forward);
    }

    /// <summary>
    /// ⭐ §104 r5: ПО НЕЙ ПОПАЛИ ПРЯМО СЕЙЧАС — кровь, вздрагивание и звук
    /// удара, всё в один кадр.
    ///
    /// <para>
    /// Раньше вид узнавал о попадании двумя косвенными путями, и оба врали.
    /// Флинч висел на падении здоровья мимо порога 0.02 по СРЕДНЕМУ — а кулак
    /// снимает <c>landed/7</c> ≈ 0.015, то есть вздрагивания от кулака не было
    /// никогда. Брызга висела на появлении новой раны с гейтом в 0.4 с
    /// реального времени, так что часть ударов проходила вовсе без картинки.
    /// Звука удара по человеку не существовало.
    /// </para>
    /// <para>
    /// Теперь сим отмечает сам момент (<c>HitStampTick</c>), а вид ловит смену
    /// штампа — тем же приёмом, которым ловит начало замаха, и по той же
    /// причине: событие живёт один тик, а кадр рисует только последний тик.
    /// Порог здоровья остаётся ФОЛБЭКОМ для урона не от удара (падение,
    /// акула, огонь) — там штампа нет.
    /// </para>
    /// </summary>
    public void SignalHit(int hitStampTick, string hitWeaponId, string hitPart,
        Vector3 hitFrom)
    {
        var fresh = hitStampTick > 0 && hitStampTick != _lastHitStampTick;
        _lastHitStampTick = hitStampTick;
        if (!fresh)
        {
            return;
        }

        StartHitRecoil(hitPart, hitFrom);
        SpawnHitBlood(hitPart);

        if (_simSpeed <= 4.01f)
        {
            Audio.FmodSfx.Play(ImpactSfxFor(hitWeaponId), transform.position);
        }
    }

    /// <summary>Чем ударили — тем и звучит. Пустой id это кулаки.</summary>
    private static string ImpactSfxFor(string weaponId)
    {
        if (string.IsNullOrEmpty(weaponId))
        {
            return Audio.FmodSfx.Sfx.HitPunch;
        }

        if (weaponId == HexLive.Simulation.Content.GearCatalog.Bite)
        {
            return Audio.FmodSfx.Sfx.WolfBite;
        }

        // Клинковое — режет; всё прочее (молоток, кирка, копьё) — тупой удар.
        return weaponId is HexLive.Simulation.Content.GearCatalog.Knife
            or HexLive.Simulation.Content.GearCatalog.Machete
            or HexLive.Simulation.Content.GearCatalog.Axe
            or HexLive.Simulation.Content.GearCatalog.Saw
            ? Audio.FmodSfx.Sfx.HitBlade
            : Audio.FmodSfx.Sfx.HitPunch;
    }

    // ── §104 r9: ОТБОЙ ТЕЛА ОТ УДАРА ──────────────────────────────────────
    //
    // ⭐ Клипа реакции здесь БОЛЬШЕ НЕТ, и это осознанно. Клип — это состояние
    // аниматора: он спорит с тем, что играет сейчас (шаг, работа, замах),
    // требует, чтобы она стояла, и всё равно опаздывает на переход. Удар же
    // должен читаться В ТОТ ЖЕ КАДР и из любой позы.
    //
    // Поэтому реакция процедурная: кость ЗОНЫ, в которую попали, толкается
    // прочь от бьющего поверх любой анимации — как §71 дыхание и §40.9 хромота,
    // тем же слоем и по тем же правилам (при рагдолле не писать: там кости
    // принадлежат физике).
    private string _hitRecoilBone;
    private Vector3 _hitRecoilDir;      // мировое направление «прочь от удара»
    private float _hitRecoilAge = 999f; // секунд с момента удара
    private const float HitRecoilSeconds = 0.34f;
    // Доля роста актёра: смещение считается от него, потому что актёры
    // отнормированы (~0.35 от человеческого) и константа в метрах читалась бы
    // на них как вывих.
    private const float HitRecoilTorso = 0.055f;
    private const float HitRecoilHead = 0.075f;
    private const float HitRecoilLimb = 0.045f;

    private void StartHitRecoil(string hitPart, Vector3 hitFrom)
    {
        var zone = string.IsNullOrEmpty(hitPart) ? "Torso" : hitPart;
        if (_bodyBones == null || !ZoneBoneAnchors.TryGetValue(zone, out var boneName))
        {
            return;
        }

        var bone = _bodyBones.GetBone(boneName);
        if (bone == null)
        {
            return;
        }

        // Прочь от бьющего: горизонтально, чтобы удар не подбрасывал и не
        // вдавливал в землю. Если бьют в упор (позиции совпали) — толкаем
        // назад от её собственного взгляда.
        var away = bone.position - hitFrom;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f)
        {
            var root = _bodyRoot != null ? _bodyRoot : transform;
            away = -root.forward;
        }

        _hitRecoilBone = boneName;
        _hitRecoilDir = away.normalized;
        _hitRecoilAge = 0f;
    }

    /// <summary>
    /// Толкает задетую кость прочь от удара и отпускает обратно. Зовётся из
    /// LateUpdate вместе с остальными процедурными позами — то есть ПОСЛЕ
    /// аниматора, поверх любого клипа и в любом состоянии.
    /// </summary>
    private void ApplyHitRecoil()
    {
        if (_hitRecoilAge >= HitRecoilSeconds || _hitRecoilBone == null || _bodyBones == null)
        {
            return;
        }

        _hitRecoilAge += Time.deltaTime * Mathf.Max(0.01f, _simSpeed);
        var u = Mathf.Clamp01(_hitRecoilAge / HitRecoilSeconds);

        var bone = _bodyBones.GetBone(_hitRecoilBone);
        if (bone == null)
        {
            return;
        }

        // Резкий выброс и медленный возврат: пик на ~пятой части окна. Ровная
        // синусоида читалась бы как покачивание, а удар — это толчок.
        var impulse = Mathf.Sin(Mathf.PI * Mathf.Pow(u, 0.38f));

        var root = _bodyRoot != null ? _bodyRoot : transform;
        var scale = Mathf.Max(0.01f, root.lossyScale.y);
        var reach = _hitRecoilBone switch
        {
            "head" => HitRecoilHead,
            "abdomenUpper" or "pelvis" => HitRecoilTorso,
            _ => HitRecoilLimb,
        };

        bone.position += _hitRecoilDir * (reach * scale * impulse);
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
        RefreshAnimatorCulling();
        RefreshSkinBounds();
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
            EndCameraGaze(); // §130: обмякшая в объектив не смотрит
            ClearActionTarget();
        }

        foreach (var rb in _ragdollBodies)
        {
            if (rb != null)
            {
                if (active)
                {
                    rb.isKinematic = false;
                }
                else
                {
                    // Unity 6 rejects velocity writes after a body becomes
                    // kinematic. Stop the live ragdoll first, then hand it
                    // back to the animator. A body that was already kinematic
                    // needs no reset and must not receive either write.
                    if (!rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }

                    rb.isKinematic = true;
                }
            }
        }
    }

    // §111.13: слоты 1 и 3 стоят с одного борта тела, 2 и 4 — с другого.
    // Таблица короткая нарочно: раскладка станций — правило симуляции, вид
    // только читает её номер и не имеет права выводить сторону из позиций.
    private static float LyingStationSide(int slot) => slot switch
    {
        1 or 3 => 1f,
        2 or 4 => -1f,
        _ => 0f
    };

    // Spec 31C.6: interaction poses — crouch while gathering/working, sit
    // on Sit, and hold the relevant item in the currently functional hand.
    public void SetInteraction(string interaction, string heldItemId, bool aidTargetLying = false,
        float interactionSeconds = 0f, int lyingStationSlot = -1)
    {
        if ((_legless || !_hasUsableHand) && IsToolOrWeapon(heldItemId))
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
        // §111.8: searching a helpless body uses the same two-hands-working
        // kneel as crafting. The sim deliberately keeps InteractionType.Loot;
        // this visual alias is all the scene needs.
        var looting = !_legless && interaction == "Loot";
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
        // §110: утешение над ЛЕЖАЩЕЙ — не крафтовый присед, а МОЛИТВА: она
        // опускается на колени рядом и просит за неё. Остальные виды помощи
        // (накормить, напоить, перевязать) остались на «садовничьем» приседе —
        // там руки и правда работают.
        var praying = aidingOther && aidTargetLying && interaction == "ConsoleOther";
        // The solo craft always kneels; an aid kneels only over a lying ward.
        var kneelingCraft = crafting || looting ||
            (aidingOther && aidTargetLying && !praying);
        // §68/§53: ПЕРЕВЯЗКА. Раньше и своя (TreatSelf), и чужая над стоячей
        // не играли ничего вовсе — ActionFromInteraction возвращал на них
        // None, и бинт наматывался в позе покоя. Клип «шарит по карманам»
        // читается ровно как «достала бинт и мотает».
        //
        // ⭐ ТОЛЬКО СТОЯ, как просил игрок. Лежачая, ползущая и безногая
        // остаются на прежнем поведении: клип писан из стойки, и в лежачей
        // цепочке он не «перевязывает», а поднимает тело с земли. Над ЛЕЖАЩЕЙ
        // подопечной перевязка тоже не сюда — там свой присед (kneelingCraft).
        var treating = !_legless && !_laying && _posture != "Crawl" &&
            !kneelingCraft && !praying &&
            interaction is "TreatSelf" or "TreatOther";
        _wantsTalk = interaction == "Talk"; // the Talk bool is driven by turn-taking
        _sitting = interaction == "Sit";   // §78.5: LateUpdate nudges a male seat
        SyncHeelPoseTarget();

        // Which procedural/clip action this verb wants (before touching the
        // animator, so the axe-chop clip-state can pre-empt the crouch Working pose).
        var actionKind = (gathering || drinking || kneelingCraft || praying || treating || _wantsTalk)
            ? ActionKind.None
            : ActionFromInteraction(interaction, heldItemId);
        // §axe: chopping/mining with an axe or pickaxe now plays the looping Chop
        // clip instead of the crouch Working pose + procedural shoulder swing.
        var chopping = actionKind == ActionKind.Chop && !_legless && _hasUsableHand;
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
            _animator.SetBool(WorkingParam, !_legless && _hasUsableHand && !chopping &&
                interaction is "Harvest" or "BuildRaft");
            _animator.SetBool(CraftingParam, kneelingCraft);
            _animator.SetBool(PrayingParam, praying); // §110
            _animator.SetBool(TreatingParam, treating); // §68/§53
            // §111.13: с какой станции лежащего тела она работает. Слот 0 (ноги)
            // — прежнее поведение бит-в-бит: сторона 0, у головы нет. Боковые
            // дают клипу зеркало и «поза у головы»; сам слот вид не вычисляет,
            // он приезжает числом из симуляции.
            _animator.SetFloat(StationSideParam, LyingStationSide(lyingStationSlot));
            _animator.SetBool(StationAtHeadParam, lyingStationSlot is 3 or 4);
            _animator.SetBool(ChoppingParam, chopping);
            _animator.SetBool(SittingParam, interaction == "Sit");
            // Clip source: config override if present, else the state's base clip.
            if (gathering && _animSet != null) OverrideClip("X Bot@Gathering Objects", Standing(_animSet.gather));
            if (drinking && _animSet != null) OverrideClip("X Bot@Drinking", Standing(_animSet.drink)); // legless drinks prone
            // §77.5: fit the work clip to the sim's window — one interaction,
            // one playthrough. Set AFTER the clip swap above, because the length
            // we divide by is the length of whatever take is actually bound.
            var actionSpeed = 1f;
            if (gathering)
            {
                actionSpeed = FitClipSpeed(GatherBaseClip, interactionSeconds);
            }
            // LootHelpless exports its 60-second SAFETY timeout as the
            // interaction window. Fitting one planting gesture to that value
            // slowed the clip to near-zero and looked like a frozen idle. Loot
            // is a repeated search, so keep authored cadence; ordinary
            // craft/aid still fit one gesture to their duration.
            else if (kneelingCraft && !looting)
            {
                actionSpeed = FitClipSpeed(CraftBaseClip, interactionSeconds);
            }
            _animator.SetFloat(ActionSpeedParam, actionSpeed);
        }

        // A full-body clip now covers these (incl. the axe swing and the craft
        // kneel) — suppress the procedural shoulder pose so it doesn't fight the
        // clip. Eat keeps its own raise-to-mouth — except legless, where the
        // prone idle carries eat/drink.
        _action = _legless || !_hasUsableHand || chopping || kneelingCraft || praying
            ? ActionKind.None
            : actionKind;
        // A solo craft puts both hands to work (tool goes down). An aid keeps
        // the mediator prop in hand (coconut to feed/water; empty to treat/
        // console). Everything else holds whatever the sim says.
        SetHandProp(crafting || looting ? string.Empty
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
        if (!_hasUsableHand)
        {
            garmentId = null;
        }

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

        var hand = ActingHandPropAnchor();
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
    private NpcWorldProgressBar _worldProgressBar;

    public void SetWorldProgress(float progress, bool visible)
    {
        if (_worldProgressBar == null && _headBone != null)
        {
            var go = new GameObject("WorldProgressBar");
            go.transform.SetParent(transform, false);
            _worldProgressBar = go.AddComponent<NpcWorldProgressBar>();
            _worldProgressBar.Initialize(_headBone);
        }

        _worldProgressBar?.SetProgress(progress, visible);
    }

    public void SetTalkTopic(string topicName) => SetTalkTopic(topicName, null);

    /// <summary>§108: тема с ЛИЦОМ — когда разговор про человека, в пузыре его
    /// круглый портрет вместо значка.</summary>
    public void SetTalkTopic(string topicName, Sprite subjectFace)
    {
        EnsureSpeechBubble();
        _speech?.SetConversationTopic(topicName, subjectFace);
        // §127.8: тема красит и ЛИЦО — флирт кокетничает, жалоба дуется,
        // шутка смеётся (вариант выбирается на фронте смены темы).
        _face?.SetTalkTopic(topicName);
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
        // §81: «занята» для безделья — это ЛЮБОЕ взаимодействие. Пустая строка
        // = сим не дал ей дела, и вот тогда она и может поплясать.
        _busyInteraction = !string.IsNullOrEmpty(interaction);
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
        // §127.8: исход разговора вспыхивает и на лице — тёплая улыбка на
        // плюс, поджатые губы/злость на минус.
        _face?.FlashRelationship(affinityDelta);

        // §67.6/§67.10: исход беседы озвучивается той же эмоцией, что и «+/-»
        // поп — ссора злит, удачный разговор радует, и в бабле висит своя
        // картинка. Не каждый раз, чтобы финалы бесед не превращались в хор.
        if (Random.value < 0.6f)
        {
            Say(affinityDelta < 0f ? "angry_bond_minus" : "happy_bond_plus");
        }
    }

    // §80/§107.5: portrait — лицо того, о ком кьюшка (страх перед конкретным
    // человеком). Голос от него не зависит: реплика привязана к ВИДУ кьюшки.
    // Своего пузыря у кьюшки больше НЕТ — она встаёт в общую очередь к тому
    // единственному, что висит над головой, и выигрывает его по рангу.
    public void PopSocialCue(string cueKind, Sprite picture = null)
    {
        EnsureSpeechBubble();
        _speech?.OnCue(cueKind, picture);

        // §81: такты сцены абьюза играются телом, а не только эмодзи. Кьюшка
        // уже приходит ровно в нужный момент и ровно тому, кого касается, —
        // отдельного сигнала для анимации заводить незачем.
        if (_animSet == null)
        {
            return;
        }

        switch (cueKind)
        {
            // §81.13: отшатывается ТОЛЬКО решившая не сопротивляться — это
            // кьюшка её решения (сим шлёт AbuseCowed вместо AbuseThreatened,
            // когда она не отвечает). Решившая драться встаёт в боевую стойку.
            case "AbuseCowed":
                PlayEmote(_animSet.rejected);
                break;
            // §81.13: плачет ПРОИГРАВШИЙ, в развязке, перед бегством домой.
            // AbuseCry сим больше не шлёт (ранний плач выглядел заученным),
            // ветка оставлена на случай старых реплеев.
            case "AbuseCry":
            case "AbuseFledHome":
                PlayEmote(_animSet.crying);
                // Понурая походка ставится НЕ здесь: с §81.10 это состояние
                // сима (оно же режет скорость вдвое), приезжает флагом снапшота
                // в SetSadWalk. Кьюшка отвечает только за одноразовый плач.
                break;
        }
    }

    public bool TryRefreshSocialCuePicture(string cueKind, Sprite picture)
    {
        EnsureSpeechBubble();
        return _speech != null && _speech.TryRefreshCuePicture(cueKind, picture);
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

    void UI.ISpeechStage.ShowSpeechIcon(string iconKey, float seconds, bool alarm)
        => _speechBubble?.ShowIcon(iconKey, seconds, alarm);

    void UI.ISpeechStage.ShowSpeechImage(Sprite image, float seconds, bool alarm)
        => _speechBubble?.ShowIcon(image, seconds, alarm);

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
    // §81: сыграть одноразовую сценку. Клип заряжается в общий слот и тут же
    // запускается триггером — так же, как выбирается случайная смерть или
    // случайная реплика разговора.
    private void PlayEmote(AnimationClip clip)
    {
        if (_animator == null || clip == null)
        {
            return;
        }

        OverrideClip(EmoteBaseClip, Standing(clip));
        _animator.SetTrigger("Emote");
        _lastEmoteTime = Time.time;
    }

    // §81: «просто существует». Когда делать нечего, изредка — сплясать,
    // присесть, пошарить по карманам.
    //
    // Решает ВИД, а не симуляция, и намеренно: у безделья нет последствий —
    // ни расхода сил, ни изменения нужд, ни следа в сейве, — а всё, что сим
    // решает, он обязан решать детерминированно и хранить. Платить полем в
    // снапшоте за то, что ничего не меняет, незачем.
    private void UpdateIdleFidget(bool busy)
    {
        if (_animSet == null || _animSet.idleFidgets == null || _animSet.idleFidgets.Length == 0 ||
            _animator == null || _legless || busy)
        {
            _idleSince = 0f;
            return;
        }

        if (_idleSince <= 0f)
        {
            _idleSince = Time.time;
            return;
        }

        // Постоять молча хотя бы FidgetIdleWarmup, и не чаще FidgetMinGap —
        // иначе это не «иногда», а тик.
        if (Time.time - _idleSince < FidgetIdleWarmup ||
            Time.time - _lastEmoteTime < FidgetMinGap)
        {
            return;
        }

        // Бросок раз в секунду, а не каждый кадр: иначе частота зависела бы от
        // FPS, и на быстрой машине она плясала бы вдвое чаще.
        if (Time.time - _lastFidgetRoll < 1f)
        {
            return;
        }

        _lastFidgetRoll = Time.time;
        if (Random.value > FidgetChancePerSecond)
        {
            return;
        }

        PlayEmote(_animSet.idleFidgets[Random.Range(0, _animSet.idleFidgets.Length)]);
        _idleSince = 0f;
    }

    /// <summary>§81.10: понурая походка после сцены. Приезжает флагом снапшота
    /// (`NpcSnapshot.IsSadWalk`) — раньше это был таймер внутри вида, и он
    /// врал во всех трёх случаях, когда вид не совпадает с симом: у удалённого
    /// зрителя, на перемотке и после загрузки сейва. Сим в это же время режет
    /// ей скорость вдвое, поэтому широкий понурый шаг совпадает с землёй.
    /// Тюнинг-сцена §71.5 дёргает этот же вход напрямую.</summary>
    public void SetSadWalk(bool sad)
    {
        if (sad == _sadWalk)
        {
            return;
        }

        _sadWalk = sad;
        ResolveLocomotionSlots();
    }

    private void UpdateArmedStance(string itemId)
    {
        var armed = !string.IsNullOrEmpty(itemId) &&
                    itemId.StartsWith("tool.", System.StringComparison.Ordinal);

        // Per-gear SO clips first; NpcAnimSet's shared armed set as fallback.
        var gearIdle = Config.GearLibrary.ArmedIdleFor(itemId);
        var gearWalk = Config.GearLibrary.ArmedWalkFor(itemId);

        // §71.5: hand the clips to the resolver instead of writing the slots.
        // The empty hand needs no restore branch here — dropping the claim IS
        // the restore, and what it falls back to (his §78 take, the §50 crawl,
        // the §81 sad walk) is the resolver's business, not this function's.
        _armedIdleClip = armed ? (gearIdle != null ? gearIdle : _animSet?.armedIdle) : null;
        _armedWalkClip = armed ? (gearWalk != null ? gearWalk : _animSet?.armedWalk) : null;
        ResolveLocomotionSlots();

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

    // §78: give the actor the locomotion authored for HIS body. The animator is
    // one controller for everyone and its takes are the colonists' — a man
    // walking them reads instantly as a woman's gait — so the male set from
    // NpcAnimSet swaps the five locomotion slots: the idle, the three §71
    // GaitBlend slots, and the sit (one clip for both seats, a stump and the
    // hex rim are the same pose).
    //
    // Called ONCE from Construct, after the override controller exists and the
    // mesh is known. Slots the set leaves empty keep the controller's take, and
    // a female actor clears the map back to it — a re-Construct that swaps the
    // sex on one instance must not leave his walk on her body. The §50 legless
    // overrides run later and win: a man who lost a leg crawls like anyone.
    private void ApplyActorLocomotion()
    {
        _actorLocomotion.Clear();
        var set = _animSet != null && ActorSex.Of(_actorMesh) == VisualGender.Male
            ? _animSet.male
            : null;

        // §71.5: his takes are the resolver's FALLBACK, not a write — the four
        // blended slots have other claimants (a tool, the mood, a lost leg) and
        // whoever writes last would win. Clearing the map above is what turns a
        // re-Constructed female actor back to the authored takes.
        RememberLocomotionClip(IdleClipKey, set?.idle);
        RememberLocomotionClip(GaitClipKeys[0], set?.walk);
        RememberLocomotionClip(GaitClipKeys[1], set?.slowRun);
        RememberLocomotionClip(GaitClipKeys[2], set?.run);
        ResolveLocomotionSlots();
        // Sitting has exactly one claimant, so it is still set straight.
        SetLocomotionClip(SitClipKey, set?.sit);
    }

    // His own take for a slot, kept as the resolver's fallback. No clip = the
    // slot falls through to the controller's authored one (BaseLocomotionClip).
    private void RememberLocomotionClip(string baseName, AnimationClip clip)
    {
        if (clip != null)
        {
            _actorLocomotion[baseName] = clip;
        }
    }

    // §71.5: ONE place decides what each locomotion slot plays.
    //
    // Four systems claim the walk slot — the §50 crawl, the §81 sad walk, the
    // armed walk, and the actor's own take (§78) — and each used to write it
    // directly behind its own latch, so whoever moved last won. Picking up an
    // axe while sad ate the sad walk for good (its latch still read "applied",
    // so it never re-asserted), and the sadness timing out handed the slot back
    // to the base walk with the axe still in hand. A priority chain resolved in
    // one function cannot have that bug: read the state, pick the winner, and
    // touch the slot only when the winner actually changed.
    private void ResolveLocomotionSlots()
    {
        if (_animOverride == null)
        {
            return;
        }

        var crawling = _legless && CrawlClip != null;

        // Idle: no legs > fists up (§104 bare-handed) > tool in hand > her own.
        var idle = _legless && ProneClip != null ? ProneClip
            : _bareStance && _bareStanceClip != null ? _bareStanceClip
            : _armedIdleClip != null ? _armedIdleClip
            : BaseLocomotionClip(IdleClipKey);

        // Walk: crawling beats everything — she has no legs to be sad or armed
        // with — then the mood, then the tool, then her own take.
        var walk = crawling ? CrawlClip
            : _sadWalk && _animSet != null && _animSet.sadWalk != null ? _animSet.sadWalk
            : _armedWalkClip != null ? _armedWalkClip
            : BaseLocomotionClip(GaitClipKeys[0]);

        ApplyLocomotionSlot(IdleClipKey, idle, -1);
        ApplyLocomotionSlot(GaitClipKeys[0], walk, 0);
        ApplyLocomotionSlot(GaitClipKeys[1],
            crawling ? CrawlClip : BaseLocomotionClip(GaitClipKeys[1]), 1);
        ApplyLocomotionSlot(GaitClipKeys[2],
            crawling ? CrawlClip : BaseLocomotionClip(GaitClipKeys[2]), 2);
    }

    // One resolved slot. Remembers what is playing there — the §71.5 cadence
    // math reads it back to find that clip's authored stride.
    private void ApplyLocomotionSlot(string baseName, AnimationClip clip, int gaitSlot)
    {
        if (clip == null || (gaitSlot < 0 ? _activeIdle : _activeGait[gaitSlot]) == clip)
        {
            return;
        }

        OverrideClip(baseName, clip);
        if (gaitSlot < 0)
        {
            _activeIdle = clip;
        }
        else
        {
            _activeGait[gaitSlot] = clip;
        }
    }

    // §71.5: how much ground the clip currently in a gait slot covers at 1×
    // playback (body heights/sec). No stride row for it — or no anim set at all
    // — means the slot's old single-constant figure, i.e. the pre-§71.5 result.
    private float StrideOf(int gaitSlot, float fallback) =>
        _animSet != null ? _animSet.StrideFor(_activeGait[gaitSlot], fallback) : fallback;

    /// <summary>§71.5: the clip a gait slot is playing — the tuning scene shows the
    /// name and calibrates that clip's stride.</summary>
    public AnimationClip ActiveGaitClip(int gaitSlot) =>
        gaitSlot >= 0 && gaitSlot < _activeGait.Length ? _activeGait[gaitSlot] : null;

    // One locomotion slot: remember the actor's clip and play it, or hand the
    // slot back to the controller's authored take when he has none.
    private void SetLocomotionClip(string baseName, AnimationClip clip)
    {
        if (clip != null)
        {
            _actorLocomotion[baseName] = clip;
            OverrideClip(baseName, clip);
        }
        else if (_clipsByName.TryGetValue(baseName, out var authored))
        {
            OverrideClip(baseName, authored);
        }
    }

    // What a locomotion slot falls back to for THIS actor: his own take when he
    // has one, the controller's otherwise.
    private AnimationClip BaseLocomotionClip(string baseName)
    {
        if (_actorLocomotion.TryGetValue(baseName, out var mine))
        {
            return mine;
        }

        return _clipsByName.TryGetValue(baseName, out var authored) ? authored : null;
    }

    // §77.5: how fast a one-gesture work state must run so its clip plays
    // EXACTLY ONCE across the sim's interaction window. Long job → slow motion,
    // short job → brisk, and the §76/§78 multipliers that stretch a job no
    // longer show up as a clip restarting and being cut off mid-stoop.
    //
    // Length comes from the LIVE clip (the override controller may have swapped
    // the authored take for an NpcAnimSet one, or the §50 prone clip), so the
    // fit follows whatever is actually playing. Unknown length or no window →
    // 1 = authored pace, i.e. exactly the pre-§77.5 behaviour.
    private float FitClipSpeed(string baseName, float windowSeconds)
    {
        if (windowSeconds <= 0.01f)
        {
            return 1f;
        }

        var length = EffectiveClipLength(baseName);
        return length <= 0.01f
            ? 1f
            : Mathf.Clamp(length / windowSeconds, ActionSpeedMin, ActionSpeedMax);
    }

    // The clip a base-clip KEY currently plays: the override if one was set,
    // else the authored take. Mirrors how OverrideClip writes the same slot.
    private float EffectiveClipLength(string baseName)
    {
        if (!_clipsByName.TryGetValue(baseName, out var baseClip) || baseClip == null)
        {
            return 0f;
        }

        var live = _animOverride != null ? _animOverride[baseClip] : null;
        return live != null ? live.length : baseClip.length;
    }

    // §NPC-anim: swap the clip a base-clip key plays, via the override controller.
    // §105: у каждой девушки СВОЯ поза сна. Четыре тела в одинаковой позе у
    // костра читались как копипаста.
    //
    // Вариант берётся от её id и держится всю жизнь — это её привычка, а не
    // украшение кадра. Поэтому НЕ Random: тот дал бы разную позу на сервере и у
    // каждого зрителя, и новую после каждой перезагрузки. Поле на провод при
    // этом не нужно — id есть у обоих концов, и одинаковое число выводится из
    // него на месте (в отличие от позы СМЕРТИ, которую сим считает сам: та
    // выпадает один раз в момент падения и обязана пережить сохранение).
    private void ApplySleepPose()
    {
        var poses = _animSet != null ? _animSet.sleep : null;
        if (poses == null || poses.Length == 0)
        {
            return; // вариантов не назначили — у всех авторский клип состояния
        }

        var clip = poses[((_npcId % poses.Length) + poses.Length) % poses.Length];
        OverrideClip(SleepBaseClip, clip);
        OverrideClip(FallenIdleBaseClip, clip);
    }

    // §110: поза плача — ВТОРАЯ поза сна (свернулась на боку), одна на всех.
    // Она читается как «плачет», в отличие от привычной позы конкретной
    // девушки, и потому НЕ выводится из id: тут важно узнавание состояния, а
    // не характер. Если вариантов ещё не назначили — остаётся авторский клип.
    private void ApplyCryingPose()
    {
        var poses = _animSet != null ? _animSet.sleep : null;
        if (poses == null || poses.Length < 2)
        {
            return;
        }

        OverrideClip(SleepBaseClip, poses[1]);
    }

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
            // §67.7: рот проговаривает реплику (.vis-таймлайн по часам канала).
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
                // §84: the yucca cut is blade work — a knife hacking the stalk
                // swings the same Chop loop as tree felling (Cut joins
                // ChopWood/Mine; the crouched "Working" pose read as if she
                // weren't cutting anything at all).
                var chopping = gear.Id == heldItemId &&
                    (gear.Has(HexLive.Simulation.Content.GearCapability.ChopWood) ||
                     gear.Has(HexLive.Simulation.Content.GearCapability.Mine) ||
                     gear.Has(HexLive.Simulation.Content.GearCapability.Cut));
                return chopping ? ActionKind.Chop : ActionKind.Work;
            case "Process": // spec §54: splitting a log — an axe chop motion
                return ActionKind.Chop;
            case "Butcher": // spec §54: knifing a carcass — a crouched working motion
                return ActionKind.Work;
            case "PickUp":
            case "BuildRaft":
            case "Craft":
            case "Loot":
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
    // the whole attack-animation window — GearStats.StrikeTimings of the PICKED
    // strike variant, counted from the swing start; the clip/procedural swing
    // plays exactly then, the damage lands mid-window (HitDelaySeconds,
    // sim-side), and between swings she stands recovering — no more view-local
    // attack timer drifting out of sync with the actual blows.
    // strikeIndex: the sim's picked strike variant for THIS swing (fists:
    // punches/kicks — GearConfig.strikes order); -1 = single-timing gear,
    // the view rolls a random clip like before.
    // swingStartTick: the tick the sim opened THIS swing's window (0 = never
    // swung). The clip fires on a stamp we have not played yet — see
    // _lastSwingStartTick for why the old boolean edge was not enough.
    // §96: одиночный замах вне боя — сцена абьюза бьёт, не заводя боевой пары.
    // Клип выбирается тем же способом, что и в бою (оружие + вариант удара),
    // поэтому со стороны удар неотличим от настоящего: тот же размах, та же
    // кровь на попадании.
    private void PlayLooseSwing(string weaponId, int strikeIndex)
    {
        // Клип выбирается ТЕМ ЖЕ способом, что и в бою: сначала лист снаряжения
        // (данные), потом набор анимаций как запасной вариант. Кулаки — это
        // пустой id (fists.asset), поэтому у безоружного удара клипы тоже есть.
        var attackClips = Config.GearLibrary.AttackClipsFor(weaponId ?? string.Empty);
        if (attackClips == null)
        {
            var wa = _animSet != null ? _animSet.WeaponFor(weaponId) : null;
            attackClips = wa != null && wa.attacks != null && wa.attacks.Length > 0
                ? wa.attacks
                : null;
        }

        if (attackClips != null && attackClips.Length > 0)
        {
            var clip = strikeIndex >= 0 && strikeIndex < attackClips.Length
                ? attackClips[strikeIndex]
                : null;
            clip ??= attackClips[Random.Range(0, attackClips.Length)];
            if (clip != null)
            {
                OverrideClip(AttackBaseClip, Standing(clip));
            }
        }

        // Триггер ставится в любом случае: без клипа отыграет базовый замах —
        // всё лучше, чем неподвижная фигура при летящем уроне.
        _animator.SetTrigger(AttackParam);
        if (_simSpeed <= 4.01f)
        {
            Audio.FmodSfx.Play(Audio.FmodSfx.Sfx.Swing, transform.position);
        }
    }

    /// <summary>
    /// ⭐ НОВЫЙ ЛИ ЭТО УДАР — единственное место, где вид отвечает на вопрос.
    ///
    /// <para>
    /// Опознаётся по СМЕНЕ штампа, а не по флагу <c>IsSwinging</c>: окно живёт
    /// один-два тика, кадр рисует только последний из шагнутых тиков, и на
    /// перемотке (или через сеть, потерявшую тик) большинство замахов
    /// открывалось и закрывалось незамеченными — удар прилетал в неподвижное
    /// тело. Непроигранный штамп такой пропуск переживает.
    /// </para>
    /// <para>
    /// Штамп УСВАИВАЕТСЯ в любом случае, даже когда играть нечего: сим не
    /// обнуляет <c>SwingStartTick</c> по окончании боя, и несброшенный кэш
    /// показал бы старый штамп новым в НАЧАЛЕ следующего боя — фантомный удар
    /// до первого замаха.
    /// </para>
    /// <para>
    /// §104 r3: правило жило в двух копиях (бой и одиночный замах), и вторая
    /// уже разошлась — гейтилась на <c>swinging</c> и теряла удар. Копия правила
    /// о том, как ловить пропуск, сама была пропуском.
    /// </para>
    /// </summary>
    private bool ConsumeSwingStamp(int swingStartTick)
    {
        var started = swingStartTick > 0 && swingStartTick != _lastSwingStartTick;
        _lastSwingStartTick = swingStartTick;
        return started;
    }

    /// <summary>
    /// ⭐ ОКНО ЗАМАХА В СЕКУНДАХ — ровно то, что открыл сим.
    ///
    /// <para>
    /// Спрашивается у ТОГО ЖЕ расчёта (<c>GearStats.StrikeTimings</c>) и с тем
    /// же вариантом удара. Здесь стояла базовая длительность оружия, а сим
    /// открывает окно по варианту: у кулака это разные листы, и совпадали они
    /// только случайно. Две мерки на одно расстояние — та же болезнь, что дала
    /// мёртвую зону §102, только во времени.
    /// </para>
    /// </summary>
    private static float SwingWindowSeconds(string weaponId, int strikeIndex)
    {
        HexLive.Simulation.Content.GearCatalog.For(weaponId ?? string.Empty)
            .StrikeTimings(strikeIndex, out _, out var clipSeconds, out _);
        return clipSeconds;
    }

    public void SetCombat(bool fighting, string weaponId, bool swinging, int strikeIndex = -1,
        int swingStartTick = 0)
    {
        // §81: дерущаяся не пляшет.
        _combatFighting = fighting;
        if (_legless || !_hasUsableHand)  // prone/armless: no weapon or fight pose
        {
            fighting = false;
            _combatFighting = false;
            weaponId = null;
            SetHandProp(null);
        }

        if (!fighting)
        {
            // §96: ⭐ ЗАМАХ БЕЗ БОЯ. Раньше здесь стоял безусловный выход, и
            // весь путь анимации удара висел за боевым флагом. Бьющий не всегда
            // в паре: жертва, которая не отвечает, IsFighting не получает
            // (FightScene.Latch зажигает её, только если она сама сцепилась), а
            // до §103 r3 сцена абьюза не зажигала и нападающего. Тогда её удары
            // ландили невидимо: урон есть, рана есть, кровь есть, а замаха нет.
            //
            // Окно замаха самодостаточно: сим уже сказал «сейчас бьют», и
            // рисовать это не требует боевой пары. Поэтому одиночный удар
            // проигрывается ДО выхода, а всё остальное (стойка, скорость,
            // прицел) по-прежнему только для настоящего боя.
            //
            // §104 r3: фронт штампа опознаётся ТЕМ ЖЕ хелпером, что и в бою.
            // Здесь стояло своё условие с гейтом на `swinging` — то есть ровно
            // тот баг, который штамп и заводился лечить: окно живёт один-два
            // тика, кадр его проскакивает, флаг уже опущен, а else тихо
            // усваивал штамп, и удар терялся навсегда.
            var looseSwingStarted = ConsumeSwingStamp(swingStartTick);
            if (looseSwingStarted && _hasUsableHand && !_legless && _animator != null)
            {
                PlayLooseSwing(weaponId, strikeIndex);
            }

            _wasFighting = false;
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

        var swingStarted = ConsumeSwingStamp(swingStartTick);

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
                _bareStanceClip = stance;
                _bareStance = true;
                ResolveLocomotionSlots();
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
            // Deliberately NOT gated on `swinging`: when the frame skipped the
            // whole 1-2 tick window the flag is already false, and the clip must
            // still play — a landed blow with no swing is the bug this fixes.
            if (swingStarted)
            {
                var clip = strikeIndex >= 0 && strikeIndex < attackClips.Length
                    ? attackClips[strikeIndex]
                    : null;
                clip ??= attackClips[Random.Range(0, attackClips.Length)];
                if (clip != null)
                {
                    OverrideClip(AttackBaseClip, clip);
                    // §104 r4: и сразу подогнать ТЕМП под окно, которое открыл
                    // сим для ИМЕННО ЭТОГО варианта удара — иначе авторская
                    // длина клипа и окно расходятся (кулак: 2.17 с против 1.5),
                    // и удар либо обрывается, либо доигрывает поверх
                    // следующего. Ставится ПОСЛЕ подмены клипа: делим на длину
                    // того, что реально забиндено (идиом §77.5).
                    _animator.SetFloat(AttackSpeedParam,
                        FitClipSpeed(AttackBaseClip, SwingWindowSeconds(weaponId, strikeIndex)));
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
            if (swingStarted)
            {
                _actionPhase = 0f;
                // §67: процедурный замах тоже свистит.
                if (_simSpeed <= 4.01f)
                {
                    Audio.FmodSfx.Play(Audio.FmodSfx.Sfx.Swing, transform.position);
                }
            }

            var duration = Mathf.Max(0.25f, SwingWindowSeconds(weaponId, strikeIndex));
            _attackSpeed = 1f / (AttackSwingCycles * duration);
        }
        else
        {
            // Between swings: arms rest — she stands out the recovery.
            _action = ActionKind.None;
        }

        _wasFighting = true;
        // §104 r3: пустой id — это КУЛАКИ, а не «оружие неизвестно». Условие
        // стояло на непустой строке, и предмет, вложенный в руку предыдущим
        // занятием (SetInteraction), оставался в кулаке всю кулачную драку.
        SetHandProp(string.IsNullOrEmpty(weaponId) ? null : weaponId);
    }

    private void SetHandProp(string itemId)
    {
        if ((_legless || !_hasUsableHand) && IsToolOrWeapon(itemId))
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

        var hand = ActingHandPropAnchor();
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

        // Some native mirrors keep the source-mesh axis correction on the
        // prefab root. Applying the common grip used to overwrite that root
        // rotation, making a correct ground model turn wrong only in hand.
        var prefabAxisCorrection = _handProp.transform.localRotation;
        var keepPrefabAxisCorrection =
            Config.GearLibrary.ConfigFor(itemId)?.preservePrefabRotationInHand == true;
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
            _handProp.transform.localRotation = keepPrefabAxisCorrection
                ? cfgRot * prefabAxisCorrection
                : cfgRot;
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

    private void SyncHandedness(IReadOnlyList<BodyPartConditionSnapshot> partConditions)
    {
        var hasUsableHand = BodyPartFunctionSnapshotMath.TryGetActingHand(
            partConditions, out var actingHand);
        SetHandedness(actingHand == BodyPart.ArmL, hasUsableHand);
    }

    // Spec 40.x: switch the acting hand at runtime — props, the one-handed
    // action CLIPS (via the Animator's MirrorAction bool → Humanoid mirror) and
    // the procedural action arm all follow. Right is preferred while usable;
    // left takes over when right is lost/broken. No usable hand suppresses
    // held props and one-handed actions until a functional prosthesis appears.
    public void SetHandedness(bool leftHanded, bool hasUsableHand = true)
    {
        if (_leftHanded == leftHanded && _hasUsableHand == hasUsableHand)
        {
            return;
        }

        _leftHanded = leftHanded;
        _hasUsableHand = hasUsableHand;

        if (_animator != null)
        {
            _animator.SetBool(MirrorActionParam, leftHanded);
            _animator.SetBool(MirrorActionInvParam, !leftHanded);
        }

        // Re-seat whatever is currently held into the new hand.
        var held = _currentPropId;
        _currentPropId = null;
        SetHandProp(held);

        var heldGarment = _handGarmentId;
        _handGarmentId = null;
        SetHandGarment(heldGarment);
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
            // tool.bottle used to bake an ABSOLUTE scale here, tuned against the
            // old procedural bottle mesh; the AI plastic bottle is 2.3x taller,
            // so that number was a lie. Its pose now lives in the gear asset
            // (bottle.asset, «Хват в руке»), where the scale is a MULTIPLIER
            // over ObjectFit and the model can change size freely.
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

        // Native wrapper prefabs may carry the axis conversion on their root
        // (the machete does). Preserve it after applying the shared slot pose;
        // otherwise the semantic +Y handle axis is no longer the visible one.
        var prefabAxisCorrection = _backProp.transform.localRotation;

        // Bug #87 rework: back, hand and ground must not invent three physical
        // sizes for the same prefab. ObjectFit measures renderer bounds in world
        // space, so the same factor works here even below a scaled animated bone.
        _backProp.transform.localScale *= ObjectFit.FitScaleFactor(_backProp, itemId);

        // One shared slot for every weapon. Rotate first, then align the centre
        // of the FINAL fitted render bounds — prefab roots/pivots may live at a
        // grip, blade tip or arbitrary authoring origin and must not move the
        // visible weapon away from the body slot.
        _backProp.transform.localPosition = Vector3.zero;

        // Shared placement tune from BackWeaponPlacementTest. This is an
        // additive chestUpper-local offset, so every back-mounted tool uses
        // the same corrected slot while ObjectFit still owns scale.
        var slotLocal = new Vector3(0.105f, -0.200f, -0.078f) +
            new Vector3(-0.09f, 0.15f, -0.03f);
        var renderers = _backProp.GetComponentsInChildren<Renderer>(true);
        var slotRotation = Quaternion.Euler(-3.335f, -0.358f, 18.524f);
        var config = Config.GearLibrary.ConfigFor(itemId);
        var twoHanded = config != null
            ? config.twoHanded
            : GearCatalog.For(itemId).TwoHanded;
        if (renderers.Length == 0)
        {
            _backProp.transform.localRotation = slotRotation *
                (twoHanded ? Quaternion.identity : Quaternion.Euler(0f, 0f, 180f)) *
                prefabAxisCorrection;
            _backProp.transform.localPosition = slotLocal;
            return;
        }

        // The visible working-end direction is geometric: prefab wrappers can
        // remap the authored +Y axis (machete: FBX +Z -> Unity +Y). The vector
        // from the grip-root/pivot to the rendered centre survives that axis
        // correction and points toward the blade/head/tip for every tool made
        // to the shared pivot convention. Align that vector, not a guessed id
        // axis, with the up/down direction selected by the canonical hand count.
        var initialBounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
        {
            initialBounds.Encapsulate(renderers[i].bounds);
        }

        var workingWorld = initialBounds.center - _backProp.transform.position;
        var workingLocal = back.InverseTransformDirection(workingWorld);
        var desiredWorkingLocal = slotRotation *
            (twoHanded ? Vector3.up : Vector3.down);
        _backProp.transform.localRotation = workingLocal.sqrMagnitude > 0.000001f
            ? Quaternion.FromToRotation(workingLocal.normalized, desiredWorkingLocal.normalized) *
              prefabAxisCorrection
            : slotRotation *
              (twoHanded ? Quaternion.identity : Quaternion.Euler(0f, 0f, 180f)) *
              prefabAxisCorrection;

        var combined = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
        {
            combined.Encapsulate(renderers[i].bounds);
        }

        var boundsCentreLocal = back.InverseTransformPoint(combined.center);
        _backProp.transform.localPosition = slotLocal - boundsCentreLocal;
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
        else if (_thermal > 0.4f && _rShldr != null && StandingIdle && !HurtReachActive)
        {
            // §82 r2: обмахивание уступает боли и не запускается на ходу и в
            // работе. Прежде оно шло всегда — в том числе поверх тянущейся к
            // голове руки и поверх рабочих клипов.
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
    public void SetPosture(string postureHint, bool winded, bool legsLost = false)
    {
        _posture = string.IsNullOrEmpty(postureHint) ? "Upright" : postureHint;
        _winded = winded;
        _legsLost = legsLost; // §50: до RefreshLeglessPresentation — он это читает
        RefreshLeglessPresentation();

        // Spec 40.9 r2: the dedicated limp pose was removed by player decision.
        // Damaged and prosthetic legs use ordinary authored locomotion;
        // mobility remains simulation-owned, but presentation never twists a
        // knee or parks the body in a special stride state.
        // Spec §50: the authoritative prone hint is handled by the
        // AnimatorOverrideController instead (Idle→prone, gait→crawl, standing
        // actions→prone), so Sit/Sleep/Drink keep their own clips.
        // The old dedicated Crawl state is NOT used (Crawling stays off), so it
        // can never hijack sitting/sleeping/drinking.
        if (_animator != null)
        {
            _animator.SetBool(CrawlingParam, false);
            _animator.SetBool(LimpingParam, false);
        }
    }

    /// <summary>§118: carry arms over normal walking (Carrying.fbx sample).</summary>
    public void SetCarryingPerson(bool carrying) => _carryingPerson = carrying;

    // §118.4: the carrier's arms come straight from the imported Carrying clip
    // — an avatar-masked layer done by hand. Snapshot the live pose of every
    // humanoid bone OUTSIDE the two arm chains, let the clip overwrite the
    // whole body, then put the kept bones (and the root) back. Her own
    // idle/walk/run/jump keeps the body; only the arms cradle the passenger.
    // The old ±58°/82° procedural elbows this replaces never matched the
    // patient's authored pose.
    private PlayableGraph _carryGraph;
    private UnityEngine.Animations.AnimationClipPlayable _carryPlayable;
    private Transform[] _carryKeptBones;
    private Vector3[] _carryKeptPositions;
    private Quaternion[] _carryKeptRotations;

    private void ApplyCarryPose()
    {
        if (!_carryingPerson || _laying || _swimming || _bodyRoot == null ||
            _animator == null || !_animator.isHuman)
        {
            return;
        }

        var clip = CarryPoseVisuals.CarryingClip;
        if (!CarryPoseVisuals.EnsureGraph(_animator, clip, ref _carryGraph, ref _carryPlayable))
        {
            return;
        }

        if (_carryKeptBones == null)
        {
            BuildCarryKeptBones();
        }

        for (var i = 0; i < _carryKeptBones.Length; i++)
        {
            var bone = _carryKeptBones[i];
            _carryKeptPositions[i] = bone.localPosition;
            _carryKeptRotations[i] = bone.localRotation;
        }

        var rootPosition = _animator.transform.position;
        var rootRotation = _animator.transform.rotation;

        CarryPoseVisuals.EvaluateAt(clip, _carryGraph, _carryPlayable);

        for (var i = 0; i < _carryKeptBones.Length; i++)
        {
            var bone = _carryKeptBones[i];
            bone.localPosition = _carryKeptPositions[i];
            bone.localRotation = _carryKeptRotations[i];
        }

        _animator.transform.SetPositionAndRotation(rootPosition, rootRotation);
    }

    // Everything in the humanoid skeleton EXCEPT the two arm chains. A
    // humanoid clip only ever writes humanoid bones, so this small set is the
    // complete list of what has to be restored after the clip stamps the body.
    private void BuildCarryKeptBones()
    {
        var maskedRoots = new[]
        {
            FirstMappedBone(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm),
            FirstMappedBone(HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm)
        };

        var kept = new List<Transform>();
        for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
        {
            var bone = _animator.GetBoneTransform((HumanBodyBones)i);
            if (bone != null && !IsUnderAnyBone(bone, maskedRoots))
            {
                kept.Add(bone);
            }
        }

        _carryKeptBones = kept.ToArray();
        _carryKeptPositions = new Vector3[_carryKeptBones.Length];
        _carryKeptRotations = new Quaternion[_carryKeptBones.Length];
    }

    // An unmapped humanoid bone comes back as a null Transform, and Unity's
    // overloaded == is the only reliable way to test that — ?? would slip a
    // dead object through.
    private Transform FirstMappedBone(HumanBodyBones preferred, HumanBodyBones fallback)
    {
        var bone = _animator.GetBoneTransform(preferred);
        return bone != null ? bone : _animator.GetBoneTransform(fallback);
    }

    private static bool IsUnderAnyBone(Transform bone, Transform[] roots)
    {
        foreach (var root in roots)
        {
            if (root == null)
            {
                continue;
            }

            for (var walk = bone; walk != null; walk = walk.parent)
            {
                if (walk == root)
                {
                    return true;
                }
            }
        }

        return false;
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

        if (splashZone == null || !SplashAt(splashZone, splashSeed))
        {
            return;
        }

        // §67/§67.10: свежая рана — вскрик, личный для персонажа (§67.6), с
        // общим женским фолбэком, и бабл с раной/кровью над головой. Alarm-ранг
        // перебивает болтовню; гейт 0.4 s от брызг не даёт хора из одного рта.
        Say("hurt_wound");
    }

    /// <summary>
    /// Брызга крови у кости зоны. Общая для двух поводов: НОВАЯ РАНА (её ищет
    /// SyncWoundSplashVfx по seed'ам) и ХИТ-ШТАМП (§104 r5) — удар мог не
    /// оставить раны вовсе, попав в броню или в пощаду, но видно его быть
    /// обязано. Возвращает false, когда спавнить нечего или ещё рано.
    /// </summary>
    private bool SplashAt(string zone, int seed)
    {
        if (Time.time - _lastSplashTime < SplashGateSeconds || _bodyBones == null ||
            !ZoneBoneAnchors.TryGetValue(zone, out var boneName))
        {
            return false;
        }

        var bone = _bodyBones.GetBone(boneName);
        if (bone == null)
        {
            return false;
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
            bone.position, outward, root.lossyScale.y, seed);
        return true;
    }

    /// <summary>
    /// Кровь по хит-штампу. Зона неизвестна — сим шлёт момент, а не место, —
    /// поэтому берём торс: он у любой раны рядом, а точное место всё равно
    /// докрасит рана, когда появится.
    /// </summary>
    private void SpawnHitBlood(string hitPart)
    {
        SplashAt(string.IsNullOrEmpty(hitPart) ? "Torso" : hitPart, _lastHitStampTick);
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
        var tint = SkinWeatheringTone.Compose(tanLevel, sunburn, SimBalance.TanStrength);
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
            // A scheduled painter rebuild updates material slots over several
            // frames. Until this particular slot has the new tone baked into
            // its texture, compensate with _BaseColor so painted limbs never
            // show the previous tan beside an already-updated torso/head.
            var baseColor = painted
                ? CompensatePaintedSkinTone(SkinTint, _skinPainter.PaintedSkinTone(index))
                : tint;
            _skinMpb.SetColor(BaseColorId, baseColor);
            renderer.SetPropertyBlock(_skinMpb, index);
        }
    }

    private static Color CompensatePaintedSkinTone(Color desired, Color baked)
    {
        const float epsilon = 0.001f;
        return new Color(
            desired.r / Mathf.Max(epsilon, baked.r),
            desired.g / Mathf.Max(epsilon, baked.g),
            desired.b / Mathf.Max(epsilon, baked.b),
            1f);
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
        if (catalog == null || !catalog.Has(hairstyle))
        {
            Debug.LogWarning(
                $"[§74] hairstyle '{hairstyle}' is not in the appearance catalog — " +
                "run HexLive ▸ Actors ▸ Rebuild Appearance Catalog. Keeping the prefab hair.", this);
            return;
        }

        // Register readiness BEFORE starting the coroutine. StartCoroutine runs
        // immediately until its first yield, so doing this inside SpawnHair
        // leaves a small but real frame-order hole for the loading screen.
        _pendingHairLoads++;
        StartCoroutine(SpawnHair(hairstyle));
    }

    // Причёска и её цвет едут ПО АДРЕСУ (Addressables), а значит приезжают не
    // мгновенно. Пока едет — на голове та причёска, что авторская на префабе: лучше чужая
    // причёска на кадр, чем лысая голова.
    //
    // Цвет ставится ПОСЛЕ SetHair и по живому экземпляру: материал в Unity
    // общий, и запись в ассет перекрасила бы эту причёску у всех сразу.
    private System.Collections.IEnumerator SpawnHair(string hairstyle)
    {
        Wear prefab = null;
        yield return HairContent.LoadHair(hairstyle, found => prefab = found);

        if (prefab == null || _bodyBones == null)
        {
            _pendingHairLoads--;
            yield break;
        }

        _bodyBones.SetHair(prefab);

        var colour = HairColourApplier.Choose(hairstyle, _npcId);
        if (colour == null)
        {
            _pendingHairLoads--;
            yield break;
        }

        System.Collections.Generic.Dictionary<string, Material> materials = null;
        yield return HairContent.LoadColour(hairstyle, colour, loaded => materials = loaded);

        var live = _bodyBones != null ? _bodyBones.HairInstance : null;
        if (live != null && materials != null)
        {
            HairColourApplier.Apply(live.gameObject, materials);
        }

        _pendingHairLoads--;
    }

    // §74: wear another actress's face. All four girls are Genesis3Female with
    // the SAME 17 material slot names (Torso/Face/Arms/Legs/Cornea/… — §31B.1a
    // regenerated Jolly's from Molly's precisely so the sets stay parallel), so
    // the swap is a lookup BY NAME and never depends on submesh order.
    private void ApplySkinSet(string skinSet)
    {
        if (string.IsNullOrEmpty(skinSet) || _bodySkins == null ||
            string.Equals(skinSet, _actorMesh.ToString(), System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReplaceBodyMaterials(LoadSkinSet(skinSet));
    }

    // §85: the iris, split out of the skin set above.
    //
    // The four actresses ship the same five eye materials with the same five
    // names and byte-identical parameters, differing only in which 2048² Daz
    // eye map they point at — so a colour is one shared set of materials with
    // one recoloured map, and swapping it is the same replace-by-NAME as the
    // skin set. Two of the five (Irises/Sclera) carry the map and live under
    // the colour; the other three (Pupils/Cornea/EyeMoisture) have no texture
    // at all and are literally the same assets for everyone.
    //
    // EyeSocket is deliberately NOT in the set: it is shaded from the FACE
    // texture, so it belongs to the skin and follows her complexion, not her
    // iris.
    private void ApplyEyeSet(string eyeColor)
    {
        if (string.IsNullOrEmpty(eyeColor) || _bodySkins == null)
        {
            return;
        }

        ReplaceBodyMaterials(LoadEyeSet(eyeColor));
    }

    // Swap in every material of `donor` whose NAME matches one already on the
    // body. By name, never by submesh index: the four bodies are separate Daz
    // exports and nothing promises they ordered their 17 slots alike (§74.2).
    //
    // sharedMaterials is the right handle: SkinTexturePainter instantiates its
    // own copies from whatever it finds (`body.materials`), and the tan on
    // un-painted slots rides a MaterialPropertyBlock — so nothing here leaks
    // one girl's wounds onto another's shared asset.
    private void ReplaceBodyMaterials(Dictionary<string, Material> donor)
    {
        if (donor == null || donor.Count == 0)
        {
            return;
        }

        foreach (var skin in _bodySkins)
        {
            // Hair and garments carry their own materials and their own donor
            // logic — these sets are the BODY only.
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

    // Eye colour id → the five eye materials by name, merged from the shared
    // folder and the colour's own (the colour wins, though today they do not
    // overlap). Built once per colour for the whole run.
    private static Dictionary<string, Material> LoadEyeSet(string eyeColor)
    {
        if (_eyeSets.TryGetValue(eyeColor, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<string, Material>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var mat in Resources.LoadAll<Material>("HexLive/Eyes/Common"))
        {
            if (mat != null)
            {
                map[mat.name] = mat;
            }
        }

        var tinted = Resources.LoadAll<Material>($"HexLive/Eyes/{eyeColor}");
        if (tinted == null || tinted.Length == 0)
        {
            // No iris under that id: keep the body's own eyes rather than fit
            // her with a shared sclera and someone else's iris colour.
            Debug.LogWarning($"[§85] eye colour '{eyeColor}' has no materials at " +
                $"Resources/HexLive/Eyes/{eyeColor} — the body keeps its own eyes.");
            map.Clear();
        }
        else
        {
            foreach (var mat in tinted)
            {
                if (mat != null)
                {
                    map[mat.name] = mat;
                }
            }
        }

        _eyeSets[eyeColor] = map;
        return map;
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
    // already down); Limp has no presentation pose. ArmHang, HeadClutch and
    // winded breathing are authored here. Crawl is entirely handled by the
    // prone locomotion override selected from the authoritative sim hint.
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
            // §82: «рука к разбитой голове» ВЫКЛЮЧЕНА по решению пользователя —
            // реализация плохая. Это поворот плеча на -95° и предплечья на -120°
            // поверх любого клипа, без учёта того, где рука сейчас находится и
            // куда смотрит тело: со стороны это читается не как «схватился за
            // голову», а как непрерывное сгибание руки в пустоту.
            //
            // Сигнал симуляции при этом жив и приходит по-прежнему — выключен
            // только рисунок. Чтобы вернуть, сделать надо иначе: тянуть кисть в
            // точку у виска через IK (FullBodyBipedIK уже на теле и уже умеет
            // вести руку в мировую точку — см. DriveActionTargetIK), а не
            // крутить кости на фиксированный угол.
            // §82: «держится за то, что болит» переехало на IK
            // (DriveHurtReachIK). Прежний вариант крутил кости на фиксированный
            // угол поверх любого клипа и читался как сгибание руки в пустоту.
            case "HeadClutch":
                break;
            // Spec §50: Crawl has no procedural bone pose here; the locomotion
            // override supplies the imported clip. Limp intentionally has no
            // special pose at all.
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

            // §114 bug #4: _animSpeed is the SIM-TIME cadence — the ×_simSpeed
            // factor is folded in only at the animator.speed write in the
            // walking branch. Mirroring animator.speed here (it already
            // contains the multiplier) poisoned the cadence with the
            // fast-forward factor: an NPC who stood up after the player
            // dropped 50×→1× then spent many seconds (MoveTowards at 3/s)
            // walking the ×50 back down — animations played "Flash"-fast.
            _animSpeed = _animator.speed > 0f ? 1f : 0f;
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
            _smoothedSpeed = 0f;
            _stillTimer = 0f;
            return;
        }

        var delta = position - _lastPosition;
        delta.y = 0f;
        var linearSpeed = delta.magnitude / Time.deltaTime;
        var yawSpeed = Mathf.DeltaAngle(_lastYaw, yaw) / Time.deltaTime;
        _lastPosition = position;
        _lastYaw = yaw;

        // §71.5: low-pass the measured speed before anything reads it. The
        // position it is differenced from is already interpolated between two
        // 4 Hz sim poses, so it is a staircase, and every step of that
        // staircase used to land straight on the walk cycle's playback rate.
        // Fast-forward divides the time constant out, so a 4× world settles 4×
        // sooner in wall-clock and the smoothing never lags the world.
        var cadenceSpeed = _simGroundSpeedValid ? _simGroundSpeed : linearSpeed;
        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, cadenceSpeed,
            1f - Mathf.Exp(-Time.deltaTime * _simSpeed / Mathf.Max(0.01f, SpeedSmoothTau)));

        // Hysteresis: harder to START walking than to KEEP walking, so the
        // stop-start junction gait doesn't flicker the walk/idle blend. The
        // signal is the simulation pose delta when available; using the
        // interpolated Transform here made production frames at a tick
        // boundary look like Walk→Idle→Walk even while the sim was stopped.
        var motionSpeed = _simGroundSpeedValid ? _simGroundSpeed : linearSpeed;
        var motionYawSpeed = _simYawSpeedValid ? _simYawSpeed : yawSpeed;
        var threshold = _wasWalking ? _moveEpsilon * 0.6f : _moveEpsilon * 1.3f;
        var moving = motionSpeed > threshold;
        if (moving)
        {
            _stillTimer = 0f;
        }
        else
        {
            _stillTimer += Time.deltaTime * _simSpeed;
        }

        // §71.5: ...and hold the walk across a stop too short to be one. A
        // corner, a blocked step or a tick of turn penalty freezes her for a
        // single 0.25 s tick, and dropping to Idle and back over it is a visible
        // stutter. A planted pivot is exempt: it slews the body at hundreds of
        // degrees a second, and it MUST reach Idle, because the turn-on-spot
        // states can only be entered from there.
        // A render frame can briefly report a tiny raw delta while the 4 Hz
        // snapshot interpolation is still carrying visible motion. Once a
        // walk has started, let the filtered signal bridge that single sample
        // so the Animator does not fall through Walk -> Idle -> Walk and reset
        // the step phase. The raw signal still controls the stop timer, and a
        // real halt therefore becomes Idle after the hold as before.
        // §71.8: the hold length follows the sim's intent (see the knob). A
        // test scene that never feeds the intent behaves like mid-journey —
        // without the sim's word, the flicker-safe side is to bridge. The
        // filtered continuation is gated the same way: its exponential tail
        // (~0.47 s from walking speed) was what made every ARRIVAL end with
        // marching on the spot, so it only bridges while the journey is on.
        var midJourney = !_simGroundSpeedValid || _simMidJourney;
        var holdSeconds = midJourney ? MidJourneyWalkHoldSeconds : WalkHoldSeconds;
        var filteredWalkContinuation = _wasWalking && midJourney &&
            _smoothedSpeed > threshold * 0.65f;
        // §50: ПОЛЗУЩАЯ, КОТОРАЯ ПОВОРАЧИВАЕТСЯ, ПРОДОЛЖАЕТ ГРЕСТИ.
        //
        // Разворот у всех остальных — планированный пивот: тело стоит, играет
        // Idle либо turn-on-spot, и это честно, потому что стоящая переступает
        // на месте. У лежащей «стоять на месте» = поза лёжа плашмя, и тело
        // просто ПРОВОРАЧИВАЕТСЯ на земле как стрелка компаса. Поэтому в позе
        // Crawl вращение само по себе считается движением: клип ползания идёт,
        // руки перебирают, и разворот читается как разворот, а не как вращение
        // бревна. Каденс при этом падает до MinGaitCadence (скорость по земле
        // ~0), то есть гребёт она медленно — ровно то, что нужно.
        var crawlTurning = _posture == "Crawl" &&
            Mathf.Abs(motionYawSpeed) > CrawlTurnAnimYawSpeed;
        var walking = moving || crawlTurning ||
            (_wasWalking &&
             (_stillTimer < holdSeconds || filteredWalkContinuation) &&
             Mathf.Abs(motionYawSpeed) <= PivotYawSpeed);
        _wasWalking = walking;
        _animator.SetFloat(SpeedParam, walking ? 1f : 0f, 0.05f, Time.deltaTime);

        // §109.15: ⛔ ЗДЕСЬ СТОЯЛ ДОСРОЧНЫЙ ВЫХОД ИЗ ПОЗЫ УДАРА
        // (`CrossFade(Idle)` при «пошла»). Он ломал тела: SampleMotion идёт
        // КАЖДЫЙ КАДР, в бою «идёт» истинно почти всегда (стойку подталкивает
        // отжим §72.5), и переход стартовал заново каждый кадр — вес блендa
        // навсегда застревал на 0.04, аниматор не покидал Attack, а ретаргет
        // недосмешанной позы ронял таз: тело уходило в землю. В паузе это
        // видно прямо: двое дерущихся одновременно в переходе с весом 0.04 и
        // 0.05, чего при одном вызове не бывает.
        //
        // Урок: команду аниматора нельзя ставить в покадровый расчёт без
        // фронта. Если возвращать эту идею — только по РЕБРУ («в этом кадре
        // впервые пошла») либо переходом по Speed в самом контроллере.

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
            // §71.5: each slot's ground pace comes from the clip ACTUALLY in it
            // (the resolver above put it there), so a sad walk, an armed walk, a
            // male take or the §50 crawl each play at their own authored stride
            // instead of being forced through the base walk's. A clip with no
            // row in the table falls back to the old single-constant figures,
            // which is exactly the behaviour this replaced.
            var bodyScale = 1.7f * _bodyRoot.lossyScale.y;
            var walkGround = bodyScale * StrideOf(0, FullWalkBodyHeightsPerSec);
            var slowGround = bodyScale * StrideOf(1, FullWalkBodyHeightsPerSec * SlowRunCadence);
            var runGround = bodyScale * StrideOf(2, FullWalkBodyHeightsPerSec * RunCadence);
            // Judged in SIM time — the fast-forward multiplier is divided out
            // here and multiplied back into animator.speed below, so a 4× world
            // steps exactly 4× faster instead of gliding.
            var speedSim = _smoothedSpeed / Mathf.Max(0.0001f, _simSpeed);

            // §71: the SIM owns the gait. Deriving it from measured speed (the
            // first cut) meant any pace above a walk read as a jog, so a colony
            // walking at BaseMoveSpeedFactor 1.5 was permanently trotting. Now
            // _running is the sim's decision and speed only picks HOW HARD she
            // runs, plus the playback rate that keeps the feet on the ground.
            float gaitGround;
            if (!_running)
            {
                // §71.6: БОДРЫЙ ШАГ РАСПУСКАЕТСЯ В ПОЗУ, А НЕ В ПЕРЕМОТКУ.
                // Здесь стояло «шаг остаётся шагом, темп доберёт каденс» — и
                // на среднем теле это честно (1.2 ед/с ≈ 1.2× клипа). Но §76
                // даёт ловкости ±15%, и у девушки с ловкостью 10 шаг 1.38 ед/с
                // гнал клип на 1.38× — ступни по земле попадали (стрид на
                // цикл фиксирован, цикл едет быстрее), а читалось как ускоренная
                // плёнка: мелкая семенящая походка.
                //
                // Ни один клип в одиночку эту скорость не берёт: шагу нужно
                // 1.38×, трусце — 0.69×. Поэтому берём то, ради чего дерево
                // блендов и существует, — СМЕСЬ поз, и играем её на скорости
                // смеси. Порог WalkStretchCadence оставляет обычному шагу его
                // законный запас (средняя девушка едва трогает бленд), а
                // потолок MaxWalkGait держит идущую заметно ниже трусцы (0.5):
                // §71.1 требует, чтобы БЕГУЩАЯ фигура читалась как ЧП, и это
                // требование к силуэту, а не к параметру.
                //
                // ⚠️ Только для БЕЗОРУЖНОГО шага. Слоты 1-2 держат безоружную
                // трусцу: инструмент подменяет один слот 0 (GearLibrary), и
                // подмешать к нему четверть беговой позы значило бы уводить
                // руку с топором к «пустой». Ползание сюда не доходит вовсе —
                // оно занимает все три слота, — а грустная походка не доходит
                // по скорости (§81.10 режет её вдвое, это ниже порога).
                var stretched = walkGround * WalkStretchCadence;
                var blendEnd = Mathf.Lerp(walkGround, slowGround, MaxWalkGait / 0.5f);
                if (_armedWalkClip != null || speedSim <= stretched || blendEnd <= stretched)
                {
                    targetGait = 0f;
                    gaitGround = walkGround;
                }
                else
                {
                    targetGait = Mathf.InverseLerp(stretched, blendEnd, speedSim) * MaxWalkGait;
                    gaitGround = Mathf.Lerp(walkGround, slowGround, targetGait / 0.5f);
                }
            }
            else if (speedSim <= slowGround)
            {
                targetGait = 0.5f;
                gaitGround = slowGround;
            }
            else
            {
                targetGait = 0.5f + Mathf.InverseLerp(slowGround, runGround, speedSim) * 0.5f;
                gaitGround = Mathf.Lerp(slowGround, runGround, (targetGait - 0.5f) * 2f);
            }

            // A brisk walk may legitimately outrun the clip's authored pace, so
            // the walk ceiling is looser than the run's (a run clip playing 25%
            // fast already looks frantic).
            var maxCadence = _running ? MaxGaitCadence : MaxWalkCadence;
            targetAnimSpeed = Mathf.Clamp(speedSim / Mathf.Max(0.0001f, gaitGround),
                MinGaitCadence, maxCadence);
        }

        _animSpeed = Mathf.Min(
            Mathf.MoveTowards(_animSpeed, targetAnimSpeed, Time.deltaTime * 3f * _simSpeed),
            MaxAnimCadenceCeiling);
        _animator.speed = _animSpeed * _simSpeed;

        // §71: ease into the gait so a sprint starting mid-stride ramps rather
        // than snapping from walk to run on one frame.
        _gait = Mathf.MoveTowards(_gait, targetGait, Time.deltaTime * 2.5f * _simSpeed);
        _animator.SetFloat(GaitParam, _gait);

        // §21.21B v19: JumpSpeed already compresses the authored clip to the
        // hop window (StartJumpArc). The global Animator speed must therefore
        // contain ONLY the world speed. AnimatorStateInfo.length is evaluated
        // after the state's JumpSpeed and the current Animator.speed; deriving
        // a new speed from it fed Animator.speed back into itself every frame.
        // At 0.08x the measured JumpUp raced to normalizedTime 0.71 by 12.5%
        // of the hop and exited to Idle before the sim even left HopFrom.
        // Force this for the whole window, including the trigger transition —
        // waiting until JumpUp/JumpDown became current left the first frames at
        // the incoming gait cadence.
        if (_jumpTimer > 0f)
        {
            _animSpeed = 1f;
            _animator.speed = _simSpeed;
        }

        // Turning on the spot: meaningful yaw rate while standing.
        var turn = 0f;
        if (!walking && Mathf.Abs(motionYawSpeed) > 25f)
        {
            turn = Mathf.Sign(motionYawSpeed);
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
        if (_portraitGaze || _cameraGaze)
        {
            return;
        }

        _gazeTarget = target;
        _gazeWeightTarget = target != null ? 1f : 0f;
    }

    public void ClearGaze()
    {
        // §80/§130: пока идёт съёмка портрета или взгляд отдан игровой камере,
        // обычный выбор цели (собеседник, объект работы, точка пути) её не
        // перебивает.
        if (_portraitGaze || _cameraGaze)
        {
            return;
        }

        _gazeWeightTarget = 0f;
    }

    // §80: смотреть В КАМЕРУ на время съёмки портрета. Раньше снимок ловил её
    // с глазами, уведёнными на мировую цель (eyesWeight 0.2 и жёсткий клэмп),
    // и на фото она смотрела мимо. Голову ведём слабо НАМЕРЕННО: камера едет по
    // осям кости головы, и сильный поворот гнался бы сам за собой.
    public bool BeginPortraitGaze(Vector3 eyeWorldPos)
    {
        if (_gazeProxy == null || _lookAtIK == null || !_lookAtIK.enabled)
        {
            return false;
        }

        // §130: портретная съёмка главнее взгляда в игровую камеру.
        EndCameraGaze();
        _portraitGaze = true;
        _gazeProxy.position = eyeWorldPos;
        _gazeTarget = _gazeProxy;
        _gazeWeightTarget = 1f;
        _gazeWeight = 1f; // без разгона: съёмка длится несколько кадров
        _face?.SetEyesHold(true);
        return true;
    }

    public void EndPortraitGaze()
    {
        if (!_portraitGaze)
        {
            return;
        }

        _portraitGaze = false;
        _gazeWeightTarget = 0f;
        _face?.SetEyesHold(false);
    }

    // §130: камера подъехала вплотную — на несколько секунд посмотреть в
    // объектив и чуть улыбнуться, потом вернуться к обычной логике взгляда.
    // В отличие от портрета (§80) вес НЕ снапится: штатный разгон
    // MoveTowards(dt*2.5) и даёт то самое «плавно подняла глаза». Портретная
    // съёмка главнее и не тревожится (no-op, пока она идёт).
    public bool BeginCameraGaze(Vector3 lensWorldPos, float seconds)
    {
        if (_portraitGaze || _gazeProxy == null ||
            _lookAtIK == null || !_lookAtIK.enabled)
        {
            return false;
        }

        _cameraGaze = true;
        _cameraGazeUntil = Time.time + seconds;
        _gazeProxy.position = lensWorldPos;
        _gazeTarget = _gazeProxy;
        _gazeWeightTarget = 1f;
        _face?.SetCameraAttention(true);
        return true;
    }

    /// <summary>Идёт ли ещё §130-взгляд в объектив (таймер гасит его сам).</summary>
    public bool HasCameraGaze => _cameraGaze;

    // Объектив каждый кадр в новом месте — цель едет за ним.
    public void UpdateCameraGaze(Vector3 lensWorldPos)
    {
        if (_cameraGaze && _gazeProxy != null)
        {
            _gazeProxy.position = lensWorldPos;
        }
    }

    public void EndCameraGaze()
    {
        if (!_cameraGaze)
        {
            return;
        }

        _cameraGaze = false;
        _gazeWeightTarget = 0f;
        _face?.SetCameraAttention(false);
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

        var weight = _hasUsableHand && _actionTargetActive && !_ragdollActive &&
            !_laying && !_swimming && hand != null
            ? CurrentActionTargetIKWeight()
            : 0f;
        if (weight <= 0.001f)
        {
            solver.IKPositionWeight = 0f;
            effector.positionWeight = 0f;
            effector.rotationWeight = 0f;
            // §82: рабочий IK молчит — можно подержаться за больное место.
            // Порядок именно такой: solver один на всё тело, и замах всегда
            // главнее, чем баюканье царапины.
            DriveHurtReachIK(_fullBodyIK);
            return;
        }

        _hurtReachWeight = 0f;

        // §104 r11: рука не резиновая. Прицел (3.5 wu в HexWorldRenderer)
        // решает, ЕСТЬ ли цель; ЭТОТ предел — как далеко тело может за ней
        // потянуться. Раньше предела не было вовсе, и IK честно дотягивался
        // до края прицела: красиво, но нечеловечески. Точка дальше предела
        // прижимается ПО НАПРАВЛЕНИЮ на цель — замах целится туда же, просто
        // не достаёт, как и положено настоящей руке.
        // §109.14: прижимать ВЕСЬ вектор, а не только горизонталь. Первая
        // версия резала XZ и оставляла высоту цели как есть — направление на
        // цель заваливалось круто ВНИЗ (противник на гекс ниже — обычное
        // дело), а solver.pullBodyHorizontal тянет за рукой корпус: тело
        // уходило в землю, стоило встать в стойку. Пропорциональное
        // сокращение сохраняет НАПРАВЛЕНИЕ удара и просто укорачивает вылет.
        var targetPoint = _actionTargetPoint;
        var reach = targetPoint - transform.position;
        var reachLength = reach.magnitude;
        if (reachLength > ActionTargetMaxReachWorldUnits)
        {
            targetPoint = transform.position +
                reach * (ActionTargetMaxReachWorldUnits / reachLength);
        }

        var contact = ActionPropContactPoint(targetPoint, hand);
        solver.IKPositionWeight = 1f;
        solver.pullBodyHorizontal = ActionTargetIkBodyLean;
        effector.target = null;
        effector.position = targetPoint - (contact - hand.position);
        effector.positionWeight = weight;
        effector.rotationWeight = 0f;
    }

    // §104 r11: потолок вытяжения замаха в мировых единицах (те же, что у
    // прицела StrikeReachWorldUnits = 3.5 в HexWorldRenderer). 2.45 = минус
    // ~30% от прежнего фактического предела: бой в упор и по соседнему узлу
    // не задет, а дотяг через границу тайла заметно прижат к телу.
    private const float ActionTargetMaxReachWorldUnits = 2.45f;

    // §82: «держится за то, что болит» — через Final IK, а не поворотом костей.
    //
    // Прежний вариант доворачивал плечо на −95°, а предплечье на −120° поверх
    // любого клипа, не зная, где рука сейчас находится: со стороны это читалось
    // не как «схватился за голову», а как непрерывное сгибание руки в пустоту.
    // IK ведёт кисть В ТОЧКУ, поэтому рука идёт к больному месту из своего
    // текущего положения — и по дороге ничего не выворачивает.
    //
    // ⭐ Тянется ТОЛЬКО когда человек не занят ничем вообще. Любое дело, шаг,
    // драка, вода, лежание — вес мгновенно в ноль. Так рука не может влезть в
    // клип рубки или в замах: у боевого/рабочего IK и у этого один и тот же
    // solver, и делить его между ними нельзя.
    // §82 r2: ЕДИНЫЙ ПУЛ жестов безделья. Раньше каждый жест решал сам за
    // себя, и они накладывались: рука одновременно тянулась к больной голове и
    // махала от жары — со стороны выходило непрерывное сгибание руки в никуда.
    //
    // Слот один и он занимается по приоритету:
    //   1. боль — держится за то, что болит (IK);
    //   2. жара — обмахивается.
    // Больно важнее, чем жарко: за разбитую голову хватаются и в пекло.
    //
    // И то, и другое — ТОЛЬКО пока человек стоит и не делает ничего вообще.
    private bool StandingIdle =>
        !_actionTargetActive && !_ragdollActive && !_laying && !_swimming &&
        !_combatFighting && !_busyInteraction && !_wasWalking && !_dead &&
        _posture != "Crawl";

    private bool HurtReachActive => _hurtReachBone != null && StandingIdle;

    // Кисть, которая тянется, и кость, к которой тянется. Больную руку держит
    // ДРУГАЯ рука — своей же за неё не схватишься.
    private Transform _hurtReachBone;
    private bool _hurtReachUseLeftHand;
    private float _hurtReachWeight;

    // Зона раны -> кость, за которую хватаются. Имена зон совпадают с частями
    // тела симуляции (карта покраски кожи собрана по тем же именам).
    private Transform HurtBoneForZone(string zone, out bool useLeftHand)
    {
        useLeftHand = false;
        if (_bodyBones == null)
        {
            return null;
        }

        switch (zone)
        {
            case "Head":
                return _bodyBones.GetBone("head");
            case "Torso":
                return _bodyBones.GetBone("chestUpper");
            case "Pelvis":
                return _bodyBones.GetBone("hip") ?? _bodyBones.GetBone("pelvis");
            case "ArmR":
                useLeftHand = true;                    // правую держит левая
                return _bodyBones.GetBone("rForearmBend");
            case "ArmL":
                return _bodyBones.GetBone("lForearmBend");
            case "LegR":
                return _bodyBones.GetBone("rThighBend");
            case "LegL":
                useLeftHand = true;
                return _bodyBones.GetBone("lThighBend");
            default:
                return null;
        }
    }

    // Что болит сильнее всего. Берётся самая свежая рана (heal ближе к 0):
    // затянувшуюся царапину не баюкают.
    private void RefreshHurtReachTarget()
    {
        _hurtReachBone = null;
        if (_woundScratch.Count == 0)
        {
            return;
        }

        var freshest = 1f;
        string zone = null;
        foreach (var (z, _, heal) in _woundScratch)
        {
            if (heal < freshest)
            {
                freshest = heal;
                zone = z;
            }
        }

        if (zone == null || freshest > HurtReachHealCeiling)
        {
            return;
        }

        _hurtReachBone = HurtBoneForZone(zone, out _hurtReachUseLeftHand);
    }

    // Свежее этого порога рану ещё баюкают, выше — уже нет.
    private const float HurtReachHealCeiling = 0.6f;
    // Насколько кисть не доходит до самой кости — иначе она уезжает ВНУТРЬ тела.
    private const float HurtReachSurfaceOffset = 0.11f;
    private const float HurtReachEaseSpeed = 2.5f;

    // §82: решатель по умолчанию СПИТ и просыпается только под удар
    // (SetActionTargetPoint). Баюканье раны — второй повод его разбудить, иначе
    // OnPreUpdate просто не вызовется и рука никуда не потянется.
    //
    // Выбирает цель тут же: список ран обновляет рендерер, и дёргать его на
    // каждый кадр солвера незачем.
    private void UpdateHurtReach()
    {
        RefreshHurtReachTarget();
        if (_fullBodyIK == null)
        {
            return;
        }

        var wants = HurtReachActive || _actionTargetActive;
        if (_fullBodyIK.enabled != wants)
        {
            _fullBodyIK.enabled = wants;
            if (!wants)
            {
                _hurtReachWeight = 0f;
                ResetActionTargetIKWeights();
            }
        }
    }

    private void DriveHurtReachIK(FullBodyBipedIK ik)
    {
        var solver = ik.solver;
        var want = HurtReachActive ? 1f : 0f;
        // Вниз — мгновенно: начал что-то делать, рука обязана освободиться в
        // тот же кадр. Вверх — плавно, иначе рука дёргается к голове рывком.
        _hurtReachWeight = want <= 0f
            ? 0f
            : Mathf.MoveTowards(_hurtReachWeight, 1f, Time.deltaTime * HurtReachEaseSpeed);

        var hand = _hurtReachUseLeftHand ? _lHand : _rHand;
        var effector = _hurtReachUseLeftHand ? solver.leftHandEffector : solver.rightHandEffector;
        if (_hurtReachWeight <= 0.001f || hand == null || _hurtReachBone == null)
        {
            effector.positionWeight = 0f;
            effector.rotationWeight = 0f;
            solver.IKPositionWeight = 0f;
            return;
        }

        // Точка чуть ПЕРЕД костью, по взгляду тела: ладонь ложится на больное
        // место снаружи, а не проваливается в него.
        var forward = _bodyRoot != null ? _bodyRoot.forward : transform.forward;
        effector.target = null;
        effector.position = _hurtReachBone.position + forward * HurtReachSurfaceOffset;
        effector.positionWeight = _hurtReachWeight;
        effector.rotationWeight = 0f;
        solver.IKPositionWeight = 1f;
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

        if (!_hasUsableHand || _action == ActionKind.None || _laying || _swimming ||
            actShldr == null || _bodyRoot == null)
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

    private void SyncHeelPoseTarget()
    {
        // Feed posture as soon as its state changes, before BodyBones applies
        // the post-Animator heel correction. Beds are covered by _laying.
        _bodyBones?.SetHeelPoseSuppressed(_sitting || _laying || _swimming);
    }

    private void LateUpdate()
    {
        // §28.15C v3: клип падения докрутился — выключить аниматор. Первым
        // делом в кадре: всё, что ниже, тело уже не касается.
        if (_deathFreezeAt >= 0f && Time.time >= _deathFreezeAt)
        {
            FreezeDeathPose();
        }

        SampleMotion();
        PollActionSounds();
        UpdateTalkTurns();
        // §81: безделье. Идёт ПОСЛЕ SampleMotion, потому что «стоит на месте»
        // берётся из только что посчитанной скорости. (Грустная походка сюда
        // больше не ходит: с §81.10 она приезжает флагом снапшота, а не тикает
        // тут таймером.)
        UpdateIdleFidget(_busyInteraction || _combatFighting || _wasWalking || _dead || _laying);
        UpdateHurtReach();
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
                // §78.5: seat coordinates are per-sex. The values above are
                // the girls'; a man on the same clip needs his butt a touch
                // forward and down — on the stump (rest is zero there) and
                // on the rim alike. Wash (§40.6) is not a Sit and stays put.
                if (_sitting && ActorSex.Of(_actorMesh) == VisualGender.Male)
                {
                    rest += new Vector3(0f, -MaleSeatDown, MaleSeatForward);
                }
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
            ApplyCarryPose();

            // §104 r9: и отбой от удара — поверх всего, в любой позе.
            ApplyHitRecoil();

            // Spec 40.7 / 33: thermal body language — shiver when cold, fan when hot.
            ApplyThermalPose();

            // Spec 40.9 / 40.1: injury posture + winded breathing.
            ApplyPosturePose();
        }

        // Animator, procedural actions and ragdoll all write bones. The fitted
        // device follows only after those writers have produced the final pose.

        // После Animator и всех позовых слоёв: именно теперь bounds описывают
        // кадр, который игрок увидит. Так труп остаётся на земле и во время
        // падения, и после выключения Animator.
        PlantDeadBodyOnSurface();

        if (_lookAtIK == null)
        {
            return;
        }

        // §130: взгляд в объектив кончается по своим часам — дальше первый же
        // тик SyncActorView вернёт обычную цель (собеседник/путь/Clear).
        if (_cameraGaze && Time.time >= _cameraGazeUntil)
        {
            EndCameraGaze();
        }

        _gazeWeight = Mathf.MoveTowards(_gazeWeight, _gazeWeightTarget, Time.deltaTime * 2.5f);
        _lookAtIK.solver.IKPositionWeight = _gazeWeight;
        if (_gazeTarget != null)
        {
            _lookAtIK.solver.target = _gazeTarget;
            if (_portraitGaze)
            {
                // §80 r2: на фото решают ГЛАЗА, и ТОЛЬКО глаза. Голову не ведём
                // совсем — камера прибита к кости головы, поэтому любой её
                // доворот утаскивает за собой кадр, и объектив гонится за
                // лицом, которое сам же и двигает. С нулевым весом головы
                // обратной связи нет: кость стоит как стояла, камера висит
                // перед ней, а в объектив смотрят зрачки.
                _lookAtIK.solver.headWeight = 0f;
                _lookAtIK.solver.eyesWeight = 1f;
                _lookAtIK.solver.bodyWeight = 0f;
                _lookAtIK.solver.clampWeight = 0.5f;
                _lookAtIK.solver.clampWeightEyes = 0.2f;
                return;
            }

            if (_cameraGaze)
            {
                // §130: здесь, в отличие от портрета (§80 r2), голову вести
                // МОЖНО — игровая камера не прибита к кости головы, обратной
                // связи нет. Глаза решают, голова доворачивает, корпус едва.
                _lookAtIK.solver.headWeight = 0.7f;
                _lookAtIK.solver.eyesWeight = 1f;
                // §114 баг #116: тот же корпус, та же беда — см. ниже.
                _lookAtIK.solver.bodyWeight = _posture == "Crawl" ? 0f : 0.15f;
                _lookAtIK.solver.clampWeight = 0.5f;
                _lookAtIK.solver.clampWeightEyes = 0.3f;
                return;
            }

            _lookAtIK.solver.headWeight = 0.8f;
            _lookAtIK.solver.eyesWeight = 0.2f;
            // §114 баг #116: ПОЛЗУЩАЯ НЕ ДОВОРАЧИВАЕТ КОРПУС ЗА ВЗГЛЯДОМ.
            //
            // bodyWeight крутит ПОЗВОНОЧНИК, а таз оставляет на месте. Стоящей
            // это ничего не стоит, а у ползущей на этом позвоночнике висят руки:
            // замер на живой игре (Iris, posture=Crawl, один кадр, один клип) —
            // ладонь на экране +0.317 wu над землёй, тот же клип напрямую кладёт
            // её на 0.000, с выключенным LookAtIK экран сходится с клипом
            // побайтово. Цель взгляда стоит (y≈2.13), поэтому корпус тянуло
            // вверх, и руки «махали по воздуху» вместо опоры о землю.
            //
            // Голову и глаза оставляем: ползущая вполне может поднять взгляд.
            _lookAtIK.solver.bodyWeight = _posture == "Crawl" ? 0f : 0.3f;
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
}
