using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace HexLive.UnityPresentation.CarryRescueTest
{

// §116 rescue showcase: the WHOLE carry cycle driven by the real simulation —
// nothing here authors a carry link or a pose. One colonist collapses, the
// other notices, walks over, picks her up, carries her across the island and
// lays her in the bed. Every one of those steps is the shipped game code:
//
//   NeedsDecaySystem  → energy 0 puts the patient into the §60 exhaustion coma
//   RescueSystem      → §116 assigns the one free ally, plans MoveTo+PickUpPerson
//   ExecutionSystem   → RunRescue picks her up and re-plans to the destination
//   KenshiRescueMath  → picks the bed, reserves the approach, PutDownAtDestination
//
// The scene only supplies the STAGE: a flat warm island, a finished bed, a
// healthy rescuer and a patient with an empty energy bar. The camera is the
// game's own OrbitCameraController, so the whole thing can be watched from any
// side while it happens.
public sealed class CarryRescueTestBootstrap : MonoBehaviour
{
    [Tooltip("Задержка старта симуляции (реальные секунды): Unity успевает " +
        "прогрузиться, пока мир стоит на паузе.")]
    [Range(0f, 10f)]
    [SerializeField] private float _startDelaySeconds = 3f;

    private const int RescuerId = 1;
    private const int PatientId = 2;
    private const int BedId = 100;

    private SimulationRunnerBehaviour _runner;
    private float _startDelayElapsed;
    private bool _started;
    private string _phase = "ждём старта";

    private void Awake()
    {
        Application.runInBackground = true;
        // Unity's timeScale survives Enter Play Mode with domain reload off and
        // can be stranded at zero by an in-play script reload (the same trap
        // LoadingScreen guards against). The simulation runs on its own
        // unscaled clock, so a stale zero does not stop the world — it freezes
        // every scaled-time Animator instead, i.e. the colonists slide around
        // in a T-pose. Claim a sane scale for this dev scene.
        Time.timeScale = 1f;

        var cameraObject = new GameObject("CarryRescueTest Camera")
        {
            tag = "MainCamera"
        };
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 42f;
        camera.nearClipPlane = 0.05f;

        var root = new GameObject("HexLive CarryRescueTest Sim");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        // Throwaway world: it must never touch the real hexlive_save.dat.
        _runner.AutosaveSuppressed = true;
        var worldRenderer = root.AddComponent<HexWorldRenderer>();
        worldRenderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        // The game's own orbit camera — right mouse orbits, wheel zooms, and it
        // tracks the first NPC, i.e. the rescuer, all the way through the carry.
        var orbit = cameraObject.AddComponent<Input.OrbitCameraController>();
        orbit.SetRunner(_runner);

        // Start PAUSED: the first seconds otherwise play out while Unity is
        // still compiling shaders. Update() unpauses after the delay.
        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);

        var world = _runner.Engine?.World;
        if (world != null)
        {
            world.Tick = 900; // 15:00 — warm daylight, nothing to see in the dark
        }

        InflictChestWound();
        PushTestOverrides();
    }

    // The scene isolates ONE mechanic — the rescue carry — so every rival
    // pressure is switched off. Pushed every frame because HexTuning.LoadAndApply
    // (AfterSceneLoad, i.e. after Awake) rewrites the SimBalance statics.
    private void PushTestOverrides()
    {
        // Frozen needs keep the rescuer on task and, just as importantly, keep
        // the patient's energy pinned at zero so she stays under until she is
        // actually in the bed — the coma otherwise lifts mid-carry and the
        // scene ends with "patient recovered before pickup".
        SimBalance.HungerRate = 0f;
        SimBalance.ThirstRate = 0f;
        SimBalance.EnergyRate = 0f;
        SimBalance.BaseTemperature = 22f;
        SimBalance.TemperatureAmplitude = 2f;
        MobCatalog.For(MobIds.Dog).RaidChancePerDay = 0f;

        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null)
        {
            return;
        }

        world.NextMobSpawnCheckTick = int.MaxValue;
        if (world.Mobs.Count > 0)
        {
            world.Mobs.Clear();
        }

        // Test-harness guard rails on the patient's blood, active only until
        // she is laid down. The chest wound is real and bleeds by the game's
        // own rules; the clamp just keeps the scene inside the §60 coma band:
        // above 0.15 she cannot slide into the §105 death spiral on a slow
        // rescue, below 0.30 TryWakeFromComa (wake at 0.35) cannot lift her
        // mid-carry. First measured run without this: the exhaustion coma
        // healed itself in ~100 ticks and she walked off before Marta arrived.
        if (world.Entities.Npcs.TryGetValue(
                new HexLive.Simulation.Common.EntityId(PatientId), out var patient) &&
            !IsInBed(world, patient))
        {
            patient.Needs.Blood = Mathf.Clamp(patient.Needs.Blood, 0.15f, 0.30f);
            // Keep the crushed torso CRUSHED. Measured run #2: the medical
            // slow tick knits the zone back over 0 within ~200 ticks, dying
            // ends, and she stands up before the rescue is even assigned. The
            // vital-zero state must outlive the walk over + pickup + carry.
            patient.Body.Parts[BodyPart.Torso] = 0f;
            // §105: dying burns a reserve, and at empty she is dead. Hold the
            // reserve up so a leisurely orbit-camera viewing can never end in
            // an off-screen death — the dying STATE stays, only its clock is
            // parked until she is safely in the bed.
            if (patient.IsDying)
            {
                patient.Mind.DyingReserve = Mathf.Max(patient.Mind.DyingReserve, 0.6f);
            }
        }
    }

    private static bool IsInBed(
        HexLive.Simulation.Core.WorldState world, HexLive.Simulation.Agents.NPCState npc)
    {
        return npc.Execution.TargetObject is { } bedId &&
            world.Entities.Objects.TryGetValue(bedId, out var bed) &&
            ContentIds.IsBed(bed.DefinitionId);
    }

    // The trigger: a real chest wound, authored the way the game's own debug
    // panel ("+ Random wound") authors one — a WoundState record plus the
    // matching body-part HP and blood loss — but driven all the way to ZERO
    // torso HP. First measured run proved a moderate hit is NOT enough: with
    // Spec118.MedicalEnabled the old NeedsDecaySystem blood-coma line never
    // runs, the live path is KenshiMedicalMath.Tick → ResolveTrauma, and THAT
    // only drops a body on a vital zone at zero or a blood crisis. A destroyed
    // torso is the vital-zero branch: next slow tick she gets the §118
    // VitalKnockout faint plus §105 dying — helpless and rescuable, by the
    // shipped rules, with no coma flag written here.
    private void InflictChestWound()
    {
        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null ||
            !world.Entities.Npcs.TryGetValue(
                new HexLive.Simulation.Common.EntityId(PatientId), out var patient))
        {
            return;
        }

        patient.Body.Parts[BodyPart.Torso] = 0f;
        patient.Health = patient.Body.Mean();
        patient.Needs.Blood = 0.22f;
        patient.Wounds.Add(new HexLive.Simulation.Agents.WoundState
        {
            Id = patient.NextWoundId++,
            Zone = BodyPart.Torso,
            Severity = 0.5f,
            Heal01 = 0f,
            Seed = 42_116
        });
    }

    private void Update()
    {
        PushTestOverrides();
        TrackPhase();

        if (!_started)
        {
            // Unscaled on purpose: this is a real-time "let Unity finish
            // loading" delay, and scaled time can be zero here.
            _startDelayElapsed += Time.unscaledDeltaTime;
            if (_startDelayElapsed >= _startDelaySeconds && _runner != null)
            {
                _started = true;
                if (_runner.IsPaused)
                {
                    _runner.TogglePause();
                }
            }

            return;
        }

        var keyboard = Keyboard.current;
        if (keyboard == null || _runner == null)
        {
            return;
        }

        if (keyboard.digit1Key.wasPressedThisFrame)
        {
            _runner.SetSpeed(1f);
        }
        else if (keyboard.digit2Key.wasPressedThisFrame)
        {
            _runner.SetSpeed(3f);
        }
        else if (keyboard.digit3Key.wasPressedThisFrame)
        {
            _runner.SetSpeed(8f);
        }
        else if (keyboard.spaceKey.wasPressedThisFrame)
        {
            _runner.TogglePause();
        }
        else if (keyboard.rKey.wasPressedThisFrame)
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }

    // Read the phase off the WORLD, never off a flag this scene sets itself —
    // the point of the test is that the simulation drives the sequence, so the
    // readout has to be able to show it failing.
    private void TrackPhase()
    {
        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null ||
            !world.Entities.Npcs.TryGetValue(new HexLive.Simulation.Common.EntityId(PatientId),
                out var patient))
        {
            return;
        }

        if (patient.CarriedByNpcId is not null)
        {
            _phase = "НЕСЁТ";
            return;
        }

        if (patient.Execution.TargetObject is { } bedId &&
            world.Entities.Objects.TryGetValue(bedId, out var bed) &&
            ContentIds.IsBed(bed.DefinitionId))
        {
            _phase = "УЛОЖЕНА В КРОВАТЬ ✔";
            return;
        }

        if (patient.IsUnconscious(world.Tick))
        {
            _phase = patient.IsDying ? "умирает, ждёт спасения" : "лежит без сознания";
            return;
        }

        _phase = _started ? "на ногах" : "ждём старта";
    }

    // ---- world ----

    // One flat warm island. A FINISHED bed stands at the east edge; the patient
    // collapses in the middle; the rescuer starts at the west edge, so the walk
    // out, the pickup and the walk back are all plainly visible.
    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>();
        for (var r = -3; r <= 3; r++)
        {
            for (var q = -4; q <= 4; q++)
            {
                tiles.Add(new TileBootstrap
                {
                    Q = q,
                    R = r,
                    Walkable = true,
                    Elevation = 1
                });
            }
        }

        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 424116 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            Objects =
            {
                // The destination. A bed is the only place §116 will lay a
                // patient down (a lit campfire is the fallback, and there is
                // deliberately no fire here — the bed must be the answer).
                new ObjectBootstrap
                {
                    Id = BedId,
                    DefinitionId = ContentIds.BedBasic,
                    FragmentId = 1,
                    TileQ = 3,
                    TileR = 0,
                    JunctionSlots = { 0 }
                }
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = RescuerId,
                    DisplayName = "Marta",
                    ActorMesh = "Marta",
                    FragmentId = 1,
                    TileQ = -3,
                    TileR = 0,
                    // Fed, watered, rested and warm: nothing competes with the
                    // reactive rescue goal.
                    Hunger = 0.05f,
                    Thirst = 0.05f,
                    Energy = 0.95f,
                    Comfort = 0.9f,
                    Social = 0.9f,
                    ThermalDiscomfort = 0f
                },
                new NpcBootstrap
                {
                    Id = PatientId,
                    DisplayName = "Jana",
                    ActorMesh = "Jana",
                    FragmentId = 1,
                    TileQ = 0,
                    TileR = 0,
                    Hunger = 0.05f,
                    Thirst = 0.05f,
                    // Rested: the collapse comes from the authored chest wound
                    // (InflictChestWound), not from exhaustion — a §60 blood
                    // coma holds until blood climbs back to 0.35, which gives
                    // the rescue its whole window.
                    Energy = 0.6f,
                    Comfort = 0.5f,
                    Social = 0.5f,
                    ThermalDiscomfort = 0f
                }
            }
        };
    }

    // ---- readout ----

    private void OnGUI()
    {
        GUI.Box(new Rect(8f, 8f, 940f, 104f), string.Empty);
        GUI.Label(new Rect(18f, 14f, 920f, 22f),
            "CARRY RESCUE TEST (§116) — Marta поднимает Jana и несёт в кровать. " +
            "Всё решает симуляция.");
        GUI.Label(new Rect(18f, 36f, 920f, 22f),
            "ПКМ — орбита камеры, колесо — зум, 1/2/3 — скорость, пробел — пауза, R — заново.");
        GUI.Label(new Rect(18f, 58f, 920f, 22f), "Фаза: " + _phase);

        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null)
        {
            return;
        }

        var y = 80f;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var carry = npc.CarriedNpcId is { } carried
                ? $", несёт NPC{carried.Value}"
                : npc.CarriedByNpcId is { } carrier
                    ? $", её несёт NPC{carrier.Value}"
                    : string.Empty;
            GUI.Label(new Rect(18f, y, 920f, 22f),
                $"{npc.DisplayName}: цель {npc.Mind.CurrentGoal}, кома {npc.Mind.ComaCause}, " +
                $"умирает {(npc.IsDying ? npc.Mind.DyingCause.ToString() : "нет")}, " +
                $"кровь {npc.Needs.Blood:0.00}{carry}");
            y += 22f;
        }
    }
}

}
