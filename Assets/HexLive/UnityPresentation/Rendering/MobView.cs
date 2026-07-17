#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

// Generic animated-mob view (wolf today; tiger/crab/anything tomorrow — the
// per-mob numbers come from its MobConfig asset, the code is shared). Drives
// the model's Animator for a sim mob. The renderer moves the root transform
// every frame (InterpolateAnimal); this component derives locomotion speed
// from that motion and maps the sim status onto the animator params:
//   Speed (float)     — idle vs walk gate, from measured planar movement
//   Chasing (bool)    — walk vs run, gated by MEASURED speed (the sim status
//                       alone lies: a chasing mob still steps slowly, and the
//                       run clip on a slow mover reads as a treadmill)
//   Attack (bool)     — bite loop while Fighting
//   Hit (trigger)     — flinch when the quarry's strike lands
//   AnimSpeed (float) — Walk/Run playback multiplier so foot cadence matches
//                       the actual ground speed
// A mob whose prefab has no Animator (primitive fallbacks) is fine — every
// animator write is guarded, only the measurement/FaceOff math runs.
public sealed class MobView : MonoBehaviour
{
    private static readonly int SpeedParam = Animator.StringToHash("Speed");
    private static readonly int ChasingParam = Animator.StringToHash("Chasing");
    private static readonly int AttackParam = Animator.StringToHash("Attack");
    private static readonly int HitParam = Animator.StringToHash("Hit");
    private static readonly int AnimSpeedParam = Animator.StringToHash("AnimSpeed");

    // §29C.3-hit: don't spam the flinch — one per short window even if two
    // strikes land back-to-back (the controller also blocks Hit→Hit).
    private const float HitRetriggerSeconds = 0.35f;

    // Ground speed each clip's cadence represents, in body lengths per second
    // (scale-independent: an upsized mob expects a proportionally faster
    // gait). Defaults = the wolf's numbers; Configure() overrides them from
    // the mob's MobConfig asset.
    public float WalkStrideLengthsPerSecond = 1.1f;
    public float RunStrideLengthsPerSecond = 2.6f;
    // Run only above this measured speed (with hysteresis so the gait doesn't
    // flicker at the boundary). Well above today's junction-step pace: the run
    // clip is earned by actually covering ground fast, not by sim status.
    public float RunEnterLengthsPerSecond = 1.8f;
    public float RunExitLengthsPerSecond = 1.4f;
    public float MinAnimSpeed = 0.25f;
    public float MaxAnimSpeed = 1.75f;
    public bool BloodSplashEnabled = true;

    private Animator? _animator;
    private Vector3 _lastPosition;
    private float _smoothedSpeed;
    private float _bodyLength;
    private bool _running;
    private Transform? _fightTarget;
    private float _lastSignaledHealth = -1f;
    private float _lastHitAt = -10f;

    // Blood spray on a bite/strike — the same RVFX pack the NPC wounds use
    // (Resources/HexLive/VFX/Blood_Splash_0X_URP), loaded once per process.
    private static GameObject?[]? _splashPrefabs;

    /// <summary>Per-mob view numbers from the MobConfig asset (null = the
    /// wolf defaults above, so a config-less mob still animates sanely).</summary>
    public void Configure(Config.MobConfig? config)
    {
        if (config == null)
        {
            return;
        }

        WalkStrideLengthsPerSecond = config.walkStrideLengthsPerSecond;
        RunStrideLengthsPerSecond = config.runStrideLengthsPerSecond;
        RunEnterLengthsPerSecond = config.runEnterLengthsPerSecond;
        RunExitLengthsPerSecond = config.runExitLengthsPerSecond;
        BloodSplashEnabled = config.bloodSplash;
    }

    private void Awake()
    {
        _animator = GetComponentInChildren<Animator>();
        _lastPosition = transform.position;
        MeasureBodyLength();
    }

    // From the BIND-POSE local bounds × lossyScale, NOT renderer.bounds: the
    // world AABB is only refreshed at skinning time, so right after
    // CreateMobView normalizes the prefab's scale it still reports the
    // authored real-world size (metres). That stale length used to scale the
    // blood splash several times too big — bursts streamed a hex and a half
    // away from the wolf.
    private void MeasureBodyLength()
    {
        var renderer = GetComponentInChildren<SkinnedMeshRenderer>();
        if (renderer != null && renderer.sharedMesh != null)
        {
            var local = renderer.sharedMesh.bounds.size;
            var scale = renderer.transform.lossyScale;
            _bodyLength = Mathf.Max(
                Mathf.Abs(local.x * scale.x),
                Mathf.Abs(local.y * scale.y),
                Mathf.Abs(local.z * scale.z));
        }
    }

    public void SetStatus(string status)
    {
        // Kept for callers; the attack animation is no longer glued to the
        // Fighting status (an endless bite loop) — SetAttacking pulses it per
        // actual sim windup instead.
        _ = status;
    }

    // Timed melee: the sim winds up a bite for ~0.1-0.25 s and the damage
    // lands at the end of the pulse — the Attack bool tracks exactly that
    // window, so the snap plays when the bite actually happens and the mob
    // stands recovering between bites (the cooldown).
    public void SetAttacking(bool biting)
    {
        if (_animator == null)
        {
            return;
        }

        _animator.SetBool(AttackParam, biting);
    }

    // 29C.3 v2: while fighting, the pair squares up — the renderer hands us the
    // quarry's transform and both fighters turn to face each other.
    public void SetFightTarget(Transform? target)
    {
        _fightTarget = target;
    }

    // §29C.3-hit: the renderer feeds every snapshot's Health; a drop means the
    // quarry's strike landed — fire the flinch. The controller keeps it SHORT
    // (Hit exits into Attack a third of the way in), so the bite loop resumes
    // immediately instead of the animator sitting in a long stagger.
    public void SignalHealth(float health)
    {
        var previous = _lastSignaledHealth;
        _lastSignaledHealth = health;
        if (previous < 0f || health >= previous - 0.01f || health <= 0f)
        {
            return;
        }

        // The blood spray fires on every real damage drop (no retrigger gate —
        // each landed hit should spurt); the flinch anim is rate-limited.
        if (BloodSplashEnabled)
        {
            SpawnBloodSplash();
        }

        if (_animator == null || Time.time - _lastHitAt < HitRetriggerSeconds)
        {
            return;
        }

        _lastHitAt = Time.time;
        _animator.SetTrigger(HitParam);
    }

    // A blood burst at the mob's body, thrown outward+up from mid-body —
    // mirrors NpcActorView's wound splash (same prefabs, same Hierarchy
    // scaling so velocities shrink with the model).
    private void SpawnBloodSplash()
    {
        _splashPrefabs ??= new[]
        {
            Resources.Load<GameObject>("HexLive/VFX/Blood_Splash_01_URP"),
            Resources.Load<GameObject>("HexLive/VFX/Blood_Splash_02_URP"),
            Resources.Load<GameObject>("HexLive/VFX/Blood_Splash_03_URP")
        };

        var prefab = _splashPrefabs[(int)(Time.time * 7f) % _splashPrefabs.Length]
            ?? _splashPrefabs[0];
        if (prefab == null)
        {
            return;
        }

        if (_bodyLength <= 0.001f)
        {
            MeasureBodyLength();
        }

        // Spray origin: mid-body over the view ROOT — exactly where the mob
        // is drawn. (The old skinned bounds.center lags a frame behind the
        // interpolated root; at 8× sim speed that lag put the burst tiles
        // away from the sprinting wolf.)
        var origin = transform.position + Vector3.up * Mathf.Max(_bodyLength * 0.4f, 0.05f);

        // Outward = away from the attacker (the fight target) if we have one,
        // else the mob's own forward — the spray reads as blood flying off
        // the wound, not straight up.
        var outward = _fightTarget != null
            ? origin - _fightTarget.position
            : transform.forward;
        outward.y = 0f;
        if (outward.sqrMagnitude < 0.0001f)
        {
            outward = transform.forward;
        }

        var vfx = Instantiate(prefab, origin,
            Quaternion.LookRotation(outward.normalized + Vector3.up * 0.4f));
        var scale = _bodyLength > 0.01f ? _bodyLength : transform.lossyScale.y;
        vfx.transform.localScale = Vector3.one * scale;
        foreach (var ps in vfx.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        }

        Destroy(vfx, 8f);
    }

    private void Update()
    {
        if (_animator == null || Time.deltaTime <= 0f)
        {
            return;
        }

        var delta = transform.position - _lastPosition;
        delta.y = 0f;
        _lastPosition = transform.position;
        var speed = delta.magnitude / Time.deltaTime;
        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, speed, 1f - Mathf.Exp(-10f * Time.deltaTime));
        _animator.SetFloat(SpeedParam, _smoothedSpeed);

        if (_bodyLength <= 0.001f)
        {
            MeasureBodyLength();
        }

        if (_bodyLength > 0.001f)
        {
            var lengthsPerSecond = _smoothedSpeed / _bodyLength;
            var gate = _running ? RunExitLengthsPerSecond : RunEnterLengthsPerSecond;
            _running = lengthsPerSecond >= gate;
        }
        _animator.SetBool(ChasingParam, _running);

        var strideRate = _running ? RunStrideLengthsPerSecond : WalkStrideLengthsPerSecond;
        var referenceSpeed = _bodyLength * strideRate;
        var animSpeed = referenceSpeed > 0.001f
            ? Mathf.Clamp(_smoothedSpeed / referenceSpeed, MinAnimSpeed, MaxAnimSpeed)
            : 1f;
        _animator.SetFloat(AnimSpeedParam, animSpeed);

        FaceOff();
    }

    // Turn the mob toward its quarry (Y only) so it never bites from behind.
    // The QUARRY's facing is owned by the sim (AnimalCombatSystem.FaceDog
    // rotates her RotationDegrees) — writing her transform here would fight
    // the renderer's pose interpolation every frame.
    private void FaceOff()
    {
        if (_fightTarget == null)
        {
            return;
        }

        var t = 1f - Mathf.Exp(-10f * Time.deltaTime);
        var toTarget = _fightTarget.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.Slerp(
                transform.rotation, Quaternion.LookRotation(toTarget), t);
        }
    }
}

}
