using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.HutTest
{

/// <summary>
/// Full vertical-slice test for the real one-hex home: the production bootstrap
/// spawns one finished hut, two usable cots and its compact indoor hearth. Two
/// tired colonists exercise pathing, door use, sleeping, warmth, rain shelter
/// and the selected-NPC camera cutaway without touching the player's save.
/// </summary>
public sealed class HutTestBootstrap : MonoBehaviour
{
    [SerializeField, Range(0f, 10f)] private float _startDelaySeconds = 2f;
    [SerializeField] private bool _forceRain = true;
    [SerializeField] private bool _runSimulationAutomatically;
    // Test invariant, not scene-authored state: existing serialized scenes
    // otherwise deserialize a newly-added bool as false on some Unity versions.
    private const bool PauseWhenBothSleeping = true;

    private SimulationRunnerBehaviour _runner;
    private float _startDelayElapsed;
    private bool _started;
    private bool _sleepingPairCaptured;
    private float _bothSleepingSeconds;
    private const float SleepPoseSettleSeconds = 2.5f;

    private void Awake()
    {
        Application.runInBackground = true;
        // Simulation pause belongs to SimulationClock. GameMenu can leave the
        // process-wide Unity scale at zero across a Play Mode restart, which
        // freezes Animator/camera even though this fixture intentionally keeps
        // only the simulation ticks paused for pose inspection.
        Time.timeScale = 1f;

        var camera = Camera.main;
        var cameraObject = camera != null
            ? camera.gameObject
            : new GameObject("HutTestCamera") { tag = "MainCamera" };
        camera ??= cameraObject.AddComponent<Camera>();
        cameraObject.name = "HutTestCamera";
        camera.fieldOfView = 42f;
        camera.nearClipPlane = 0.05f;

        var root = new GameObject("HexLive HutTest Sim");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        _runner.AutosaveSuppressed = true;
        var worldRenderer = root.AddComponent<HexWorldRenderer>();
        worldRenderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);

        // Use the production camera contract, including selected-NPC follow,
        // right-drag orbit, scroll zoom, target switching and foliage cutout.
        var legacyOrbit = cameraObject.GetComponent<SwimTest.SwimTestOrbitCamera>();
        if (legacyOrbit != null) Destroy(legacyOrbit);
        var rtsCamera = cameraObject.GetComponent<Input.RtsCameraController>();
        if (rtsCamera == null) rtsCamera = cameraObject.AddComponent<Input.RtsCameraController>();
        rtsCamera.SetRunner(_runner);
        if (cameraObject.GetComponent<Rendering.CameraFoliageCuller>() == null)
        {
            cameraObject.AddComponent<Rendering.CameraFoliageCuller>();
        }

        PrepareFinishedHouse();
        PrepareStandingAndSleepingAcceptancePose();
        PushIsolationOverrides();
        // Static selection survives play-mode restarts. Force a real change
        // event so a newly-created production camera always enters Orbit.
        Input.NpcSelection.Clear();
        Input.NpcSelection.Select(1);
        rtsCamera.SnapToSelectedTarget();

        // Default visual acceptance state is deterministic and paused: Marta
        // stands on the real hut floor while Molly is attached to the real bed
        // through the exact production Sleep snapshot path. Space still lets
        // the tester release the fixture into the live simulation.
        if (!_runSimulationAutomatically) _started = true;
    }

    private void PrepareStandingAndSleepingAcceptancePose()
    {
        var world = _runner?.Engine?.World;
        if (world == null) return;

        WorldObjectState? hut = null;
        var beds = new List<WorldObjectState>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.Hut1Hex) hut = obj;
            else if (obj.DefinitionId == ContentIds.BedBasic &&
                     obj.Variant == ContentIds.HutBedVariant)
                beds.Add(obj);
        }

        if (hut == null || beds.Count < 2) return;
        beds.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        if (hut.Junctions.Count == 0) return;
        var standingJunction = hut.Junctions[0];
        if (
            !world.Junctions.Items.TryGetValue(standingJunction, out var center)) return;

        if (world.Entities.Npcs.TryGetValue(
                new HexLive.Simulation.Common.EntityId(1), out var standing))
        {
            standing.Tile = hut.Tile;
            standing.CurrentJunction = standingJunction;
            standing.Position = center.WorldPosition;
            standing.RotationDegrees = hut.RotationDegrees + 180f;
            standing.Needs.Energy = 1f;
            standing.Execution.CurrentInteraction = null;
            standing.Execution.Status = ExecutionStatus.None;
            standing.Execution.TargetObject = null;
        }

        var sleepBed = beds[1];
        if (world.Entities.Npcs.TryGetValue(
                new HexLive.Simulation.Common.EntityId(2), out var sleeping) &&
            sleepBed.Junctions.Count > 0 &&
            world.Junctions.Items.TryGetValue(sleepBed.Junctions[0], out var bedAnchor))
        {
            sleeping.Tile = hut.Tile;
            sleeping.CurrentJunction = sleepBed.Junctions[0];
            sleeping.Position = bedAnchor.WorldPosition;
            sleeping.RotationDegrees = sleepBed.RotationDegrees;
            sleeping.Needs.Energy = 0.05f;
            sleeping.Execution.CurrentInteraction = InteractionType.Sleep;
            sleeping.Execution.Status = ExecutionStatus.InProgress;
            sleeping.Execution.TargetObject = sleepBed.Id;
            sleeping.Execution.StartTick = world.Tick;
            sleeping.Execution.EndTick = world.Tick + 600;
        }
    }

    private void PrepareFinishedHouse()
    {
        var world = _runner?.Engine?.World;
        if (world == null) return;

        // 22:00: both tired colonists should choose the two real hut beds.
        world.Tick = 1320;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.Campfire &&
                obj.Variant == BuildingRules.HutHearthVariant)
            {
                obj.ResourceAmount = 6000f;
            }
        }
    }

    private void PushIsolationOverrides()
    {
        SimBalance.HungerRate = 0f;
        SimBalance.ThirstRate = 0f;
        MobCatalog.For(MobIds.Dog).RaidChancePerDay = 0f;
        SimBalance.BaseTemperature = 10f;
        SimBalance.TemperatureAmplitude = 5f;

        var world = _runner?.Engine?.World;
        if (world == null) return;
        world.NextMobSpawnCheckTick = int.MaxValue;
        world.Mobs.Clear();
    }

    private void Update()
    {
        PushIsolationOverrides();

        if (!_started)
        {
            // Runner pause also sets scaled time to zero; the showcase delay
            // must still elapse without requiring the player to press Space.
            _startDelayElapsed += Time.unscaledDeltaTime;
            if (_startDelayElapsed >= _startDelaySeconds && _runner != null)
            {
                _started = true;
                if (_runner.IsPaused) _runner.TogglePause();
            }
        }

        if (_started && PauseWhenBothSleeping && !_sleepingPairCaptured && !_runner.IsPaused)
        {
            var snapshot = _runner.CreateSnapshot();
            var sleeping = 0;
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress") sleeping++;
            }

            if (sleeping >= 2)
            {
                _bothSleepingSeconds += Time.unscaledDeltaTime;
                if (_bothSleepingSeconds >= SleepPoseSettleSeconds)
                {
                    _sleepingPairCaptured = true;
                    _runner.TogglePause();
                    return;
                }
            }
            else _bothSleepingSeconds = 0f;
        }

        var keyboard = Keyboard.current;
        if (keyboard == null || _runner == null) return;
        if (keyboard.digit1Key.wasPressedThisFrame) _runner.SetSpeed(1f);
        if (keyboard.digit2Key.wasPressedThisFrame) _runner.SetSpeed(3f);
        if (keyboard.digit3Key.wasPressedThisFrame) _runner.SetSpeed(8f);
        if (keyboard.spaceKey.wasPressedThisFrame) _runner.TogglePause();
        if (keyboard.rKey.wasPressedThisFrame) _forceRain = !_forceRain;
    }

    private void LateUpdate()
    {
        var world = _runner?.Engine?.World;
        if (world == null || !_forceRain) return;
        world.Environment.IsRaining = true;
        world.Environment.RainUntilTick = int.MaxValue;
    }

    private void OnGUI()
    {
        var world = _runner?.Engine?.World;
        if (world == null) return;

        var hutTile = TileCoord.Zero;
        var hutFound = false;
        var beds = 0;
        var hearthFuel = 0f;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.Hut1Hex)
            {
                hutTile = obj.Tile;
                hutFound = true;
            }
            else if (obj.DefinitionId == ContentIds.BedBasic &&
                     obj.Variant == ContentIds.HutBedVariant)
            {
                beds++;
            }
            else if (obj.DefinitionId == ContentIds.Campfire &&
                     obj.Variant == BuildingRules.HutHearthVariant)
            {
                hearthFuel = obj.ResourceAmount;
            }
        }

        var indoor = hutFound && world.Tiles.Items.TryGetValue(hutTile, out var tile) &&
            tile.Flags.HasFlag(TileFlags.Indoor);
        GUI.Box(new Rect(12f, 10f, 670f, 112f), string.Empty);
        GUI.Label(new Rect(24f, 18f, 640f, 22f),
            "HUT TEST — Marta стоит на полу, Molly лежит на production bed.basic");
        GUI.Label(new Rect(24f, 42f, 640f, 22f),
            $"hut={hutFound} tile={hutTile} indoor={indoor} beds={beds}/2 hearthFuel={hearthFuel:0.0} rain={world.Environment.IsRaining}");
        GUI.Label(new Rect(24f, 66f, 640f, 22f),
            "Штатная камера: ПКМ орбита · колесо зум · R дождь · 1/2/3 скорость · Space пауза");
        GUI.Label(new Rect(24f, 90f, 640f, 22f),
            !_runSimulationAutomatically && _runner.IsPaused
                ? "Acceptance-поза на паузе: Space — отпустить обеих в живую симуляцию."
                : _sleepingPairCaptured
                    ? "Обе позы сна уложились: тест на паузе. Space — продолжить."
                    : "NPC #1 выбран; production camera/cutaway активны.");
    }

    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>();
        for (var r = 0; r <= 8; r++)
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
            Simulation = new SimulationBootstrapSettings
            {
                Seed = 424261,
                SpawnCompletedTestHut = true
            },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 10f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            FactionHomes =
            {
                new FactionHomeBootstrap
                {
                    Faction = Faction.Colony,
                    TileQ = 0,
                    TileR = 4,
                    StakeCampfireSite = false
                }
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = 1,
                    DisplayName = "Marta",
                    ActorMesh = "Marta",
                    FragmentId = 1,
                    TileQ = 0,
                    TileR = 4,
                    Hunger = 0.05f,
                    Thirst = 0.05f,
                    Energy = 0.08f,
                    Comfort = 0.15f,
                    Social = 0.9f
                },
                new NpcBootstrap
                {
                    Id = 2,
                    DisplayName = "Molly",
                    ActorMesh = "Molly",
                    FragmentId = 1,
                    TileQ = 1,
                    TileR = 4,
                    Hunger = 0.05f,
                    Thirst = 0.05f,
                    Energy = 0.12f,
                    Comfort = 0.2f,
                    Social = 0.9f
                }
            }
        };
    }
}

}
