using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Views;
using HexLive.UnityPresentation.Wearing;
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
    [SerializeField] private bool _startInAcceptancePose;
    private bool _forceRain;
    // Test invariant, not scene-authored state: existing serialized scenes
    // otherwise deserialize a newly-added bool as false on some Unity versions.
    private const bool PauseWhenBothSleeping = true;

    private SimulationRunnerBehaviour _runner;
    private float _startDelayElapsed;
    private bool _started;
    private bool _sleepingPairCaptured;
    private float _bothSleepingSeconds;
    private int _scenarioStartTick;
    private string _geometryStatus = "WAIT";
    private string _pathStatus = "WAIT";
    private string _martaStatus = "у лагеря";
    private string _mollyStatus = "у лагеря";
    private string _poseStatus = "WAIT";
    private bool _failureLogged;
    private bool _visualDoorChecked;
    private const float SleepPoseSettleSeconds = 2.5f;
    private const int SleepScenarioTimeoutTicks = 1800;

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
        // right-drag orbit, scroll zoom and target switching.
        var legacyOrbit = cameraObject.GetComponent<SwimTest.SwimTestOrbitCamera>();
        if (legacyOrbit != null) Destroy(legacyOrbit);
        var rtsCamera = cameraObject.GetComponent<Input.RtsCameraController>();
        if (rtsCamera == null) rtsCamera = cameraObject.AddComponent<Input.RtsCameraController>();
        rtsCamera.SetRunner(_runner);
        PrepareFinishedHouse();
        if (_startInAcceptancePose) PrepareStandingAndSleepingAcceptancePose();
        else PrepareAutonomousSleepScenario();
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
        RunStaticAcceptanceChecks();
        _scenarioStartTick = _runner.Engine?.World.Tick ?? 0;
        if (_startInAcceptancePose) _started = true;
    }

    private void PrepareAutonomousSleepScenario()
    {
        var world = _runner?.Engine?.World;
        if (world == null) return;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Needs.Hunger = 0.02f;
            npc.Needs.Thirst = 0.02f;
            npc.Needs.Energy = npc.Id.Value == 1 ? 0.025f : 0.04f;
            npc.Needs.Comfort = 0.95f;
            npc.Needs.Social = 0.95f;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Plan.Goal = GoalType.None;
            npc.Plan.Steps.Clear();
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.None;
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.TargetObject = null;
        }
    }

    private void RunStaticAcceptanceChecks()
    {
        var world = _runner?.Engine?.World;
        if (world == null) return;
        WorldObjectState hut = null;
        var beds = new List<WorldObjectState>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.Hut1Hex) hut = obj;
            else if (obj.DefinitionId == ContentIds.BedBasic &&
                     obj.Variant == ContentIds.HutBedVariant) beds.Add(obj);
        }

        if (hut == null)
        {
            _geometryStatus = "FAIL: no hut";
            return;
        }

        var pieces = BuildingRules.ArchitectureObjects(world, hut);
        var pieceCount = 0;
        var doorObjects = 0;
        foreach (var piece in pieces)
        {
            pieceCount++;
            if (piece.DefinitionId == "architecture.door.wood" && piece.Junctions.Count == 3)
                doorObjects++;
        }
        var boundary = new List<JunctionId>();
        foreach (var id in world.Tiles.Items[hut.Tile].Junctions)
            if (world.Junctions.Items[id].Tiles.Count >= 2) boundary.Add(id);
        var portalCount = 0;
        foreach (var id in boundary)
        {
            var junction = world.Junctions.Items[id];
            if (!junction.Door) continue;
            portalCount++;
        }
        _geometryStatus = pieceCount == 30 && hut.ArchitectureElements.Count == 0 &&
                          doorObjects == 1 && portalCount == 3
            ? $"WAIT: data OK, checking rendered door bay {BuildingRules.HutDoorBay}"
            : $"FAIL: pieces={pieceCount} ownerNested={hut.ArchitectureElements.Count} doorObj={doorObjects} portals={portalCount}";

        var reachable = beds.Count == 2;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var start = npc.CurrentJunction;
            if (start == null && world.Tiles.Items.TryGetValue(npc.Tile, out var npcTile))
            {
                var bestDistance = float.MaxValue;
                foreach (var candidateId in npcTile.Junctions)
                {
                    var candidate = world.Junctions.Items[candidateId];
                    if (candidate.Blocked) continue;
                    var delta = candidate.WorldPosition - npc.Position;
                    var distance = delta.X * delta.X + delta.Y * delta.Y;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    start = candidateId;
                }
            }
            if (start is not { } startId) { reachable = false; continue; }
            var anyBed = false;
            foreach (var bed in beds)
                if (bed.Junctions.Count > 0 &&
                    (startId.Equals(bed.Junctions[0]) ||
                     HexPathfinder.FindPath(world, startId, bed.Junctions[0]).Count > 0))
                    anyBed = true;
            reachable &= anyBed;
        }
        _pathStatus = reachable ? "PASS: обе девушки видят путь через дверь" : "FAIL: bed path unavailable";
        Debug.Log($"[HutTest] geometry={_geometryStatus}; path={_pathStatus}", this);
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
        if (!_visualDoorChecked) ValidateRenderedDoorAgainstPortal();

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
            var distinctBeds = new HashSet<int>();
            foreach (var npc in snapshot.Npcs)
            {
                var inside = false;
                var world = _runner.Engine?.World;
                if (world != null)
                {
                    foreach (var obj in world.Entities.Objects.Values)
                        if (obj.DefinitionId == ContentIds.Hut1Hex && obj.Tile.Equals(npc.Tile)) inside = true;
                }
                var asleep = npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress" &&
                             npc.TargetObjectId.HasValue;
                if (asleep)
                {
                    sleeping++;
                    distinctBeds.Add(npc.TargetObjectId.Value);
                }
                var status = asleep ? $"СПИТЬ на bed #{npc.TargetObjectId.Value}" :
                    inside ? "внутри дома" : npc.MovementStatus == "Moving" ? "идёт к дому" : npc.CurrentGoal;
                if (npc.Id.Value == 1) _martaStatus = status;
                else if (npc.Id.Value == 2) _mollyStatus = status;
            }

            if (sleeping >= 2 && distinctBeds.Count == 2)
            {
                _bothSleepingSeconds += Time.unscaledDeltaTime;
                if (_bothSleepingSeconds >= SleepPoseSettleSeconds)
                {
                    _sleepingPairCaptured = true;
                    _poseStatus = ValidateRenderedSleepPoses(snapshot, out var poseDetail)
                        ? "PASS: body roots pinned to bed points"
                        : $"FAIL: {poseDetail}";
                    _runner.TogglePause();
                    if (_poseStatus.StartsWith("PASS"))
                        Debug.Log("[HutTest][PASS] Both autonomous NPCs entered through the real door and sleep on distinct production beds with valid poses.", this);
                    else
                        Debug.LogError($"[HutTest][FAIL] Both NPCs sleep, but pose acceptance failed: {poseDetail}", this);
                    return;
                }
            }
            else _bothSleepingSeconds = 0f;

            var worldTick = _runner.Engine?.World.Tick ?? _scenarioStartTick;
            if (!_failureLogged && worldTick - _scenarioStartTick > SleepScenarioTimeoutTicks)
            {
                _failureLogged = true;
                Debug.LogError($"[HutTest][FAIL] sleep timeout: Marta={_martaStatus}; Molly={_mollyStatus}", this);
            }
        }

        var keyboard = Keyboard.current;
        if (keyboard == null || _runner == null) return;
        if (keyboard.digit1Key.wasPressedThisFrame) _runner.SetSpeed(1f);
        if (keyboard.digit2Key.wasPressedThisFrame) _runner.SetSpeed(3f);
        if (keyboard.digit3Key.wasPressedThisFrame) _runner.SetSpeed(8f);
        if (keyboard.spaceKey.wasPressedThisFrame) _runner.TogglePause();
        if (keyboard.rKey.wasPressedThisFrame) _forceRain = !_forceRain;
    }

    private void ValidateRenderedDoorAgainstPortal()
    {
        var world = _runner?.Engine?.World;
        var assembly = FindAnyObjectByType<Environment.HutAssembly>();
        if (world == null || assembly == null) return;
        WorldObjectState hut = null;
        foreach (var obj in world.Entities.Objects.Values)
            if (obj.DefinitionId == ContentIds.Hut1Hex) { hut = obj; break; }
        if (hut == null) return;

        Renderer lintel = null;
        foreach (var renderer in assembly.RenderersForElement($"door.{BuildingRules.HutDoorBay}"))
            if (renderer != null && renderer.name.Contains("Door_lintel")) { lintel = renderer; break; }
        if (lintel == null) return;

        var portal = Vector3.zero;
        var portalCount = 0;
        foreach (var id in world.Tiles.Items[hut.Tile].Junctions)
        {
            var junction = world.Junctions.Items[id];
            if (!junction.Door) continue;
            portal += new Vector3(junction.WorldPosition.X, lintel.bounds.center.y, junction.WorldPosition.Y);
            portalCount++;
        }
        if (portalCount != 3) return;
        portal /= portalCount;
        var center2 = HexSpatialMath.TileToWorld(hut.Tile);
        var center = new Vector3(center2.X, portal.y, center2.Y);
        var visualDirection = (lintel.bounds.center - center).normalized;
        var portalDirection = (portal - center).normalized;
        var dot = Vector3.Dot(visualDirection, portalDirection);
        var centerDelta = Vector3.Distance(lintel.bounds.center, portal);
        _visualDoorChecked = true;
        _geometryStatus = dot > 0.95f && centerDelta < 0.55f
            ? $"PASS: rendered door = portal (dot {dot:0.000}, Δ {centerDelta:0.000})"
            : $"FAIL: rendered door != portal (dot {dot:0.000}, Δ {centerDelta:0.000})";
        if (_geometryStatus.StartsWith("PASS")) Debug.Log($"[HutTest] {_geometryStatus}", this);
        else Debug.LogError($"[HutTest] {_geometryStatus}", this);
    }

    private static bool ValidateRenderedSleepPoses(WorldSnapshot snapshot, out string detail)
    {
        foreach (var npc in snapshot.Npcs)
        {
            if (npc.Id.Value is not (1 or 2) || npc.TargetObjectId is not { } bedId) continue;
            WorldObjectView bedView = null;
            foreach (var view in WorldObjectView.All)
                if (view != null && view.ObjectId == bedId) { bedView = view; break; }
            if (bedView == null)
            {
                detail = $"NPC {npc.Id.Value}: bed view #{bedId} missing";
                return false;
            }
            Transform point = null;
            foreach (var candidate in bedView.GetComponentsInChildren<Transform>(true))
                if (candidate.name == "point") { point = candidate; break; }
            if (point == null || !NpcActorView.TryGetLive(npc.Id.Value, out var actor) ||
                actor == null || !actor.IsLyingStill)
            {
                detail = $"NPC {npc.Id.Value}: point/lying actor missing";
                return false;
            }
            var body = NpcActorView.FindLiveBodyRoot(npc.Id.Value);
            var bodyDelta = body == null ? -1f : Vector3.Distance(body.position, point.position);
            var skin = NpcActorView.FindLiveBodySkin(npc.Id.Value);
            var leafTop = 0f;
            var leafCount = 0;
            foreach (var renderer in bedView.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.name.StartsWith("leaf_")) continue;
                leafTop += renderer.bounds.max.y;
                leafCount++;
            }
            if (leafCount > 0) leafTop /= leafCount;
            var contactDelta = skin == null || leafCount == 0 ? -999f : skin.bounds.min.y - leafTop;
            if (body == null || bodyDelta > 0.06f ||
                Quaternion.Angle(body.rotation, point.rotation) > 2f ||
                contactDelta < -0.07f || contactDelta > 0.15f)
            {
                detail = $"NPC {npc.Id.Value}: rootΔ={bodyDelta:0.000}, skin-leaves={contactDelta:0.000} wu";
                return false;
            }
        }
        detail = "ok";
        return true;
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
        GUI.Box(new Rect(12f, 10f, 720f, 184f), string.Empty);
        GUI.Label(new Rect(24f, 18f, 640f, 22f),
            "HUT INTEGRATION TEST — две уставшие NPC сами идут спать");
        GUI.Label(new Rect(24f, 42f, 640f, 22f),
            $"hut={hutFound} tile={hutTile} indoor={indoor} beds={beds}/2 hearthFuel={hearthFuel:0.0} rain={world.Environment.IsRaining}");
        GUI.Label(new Rect(24f, 66f, 640f, 22f),
            $"geometry: {_geometryStatus}");
        GUI.Label(new Rect(24f, 90f, 640f, 22f),
            $"path: {_pathStatus}");
        GUI.Label(new Rect(24f, 114f, 680f, 22f), $"Marta: {_martaStatus} · Molly: {_mollyStatus}");
        GUI.Label(new Rect(24f, 138f, 680f, 22f),
            _sleepingPairCaptured ? "PASS — обе спят на разных кроватях; тест поставлен на паузу."
                : "ПКМ орбита · колесо зум · R дождь · 1/2/3 скорость · Space пауза");
        GUI.Label(new Rect(24f, 162f, 680f, 22f), $"pose: {_poseStatus}");
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
                    Comfort = 0.95f,
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
                    Comfort = 0.95f,
                    Social = 0.9f
                }
            }
        };
    }
}

}
