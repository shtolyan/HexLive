using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.LocomotionTest
{

/// <summary>
/// §71.7 locomotion laboratory. This is deliberately not a Transform mover:
/// one real NPC walks real hex-junction paths through MovementSystem, snapshot
/// export, HexWorldRenderer interpolation and NpcActorView. DecisionSystem is
/// muted, but every part of locomotion under test is the shipped game flow.
/// </summary>
public sealed class LocomotionTestBootstrap : MonoBehaviour
{
    private const int NpcId = 1;
    private const float StartDelaySeconds = 1f;
    private const float RoutePauseSeconds = 1.1f;
    private static readonly float[] Speeds = { 0.30f, 0.60f, 1.20f, 1.80f, 2.40f };
    private static readonly float[] RequestedTurns = { 0f, 60f, 90f, 120f, 180f };

    [SerializeField] private string _actor = "Jana";
    [SerializeField] private float _speed = 1.2f;
    [SerializeField] private bool _fixedCamera = true;
    [SerializeField] private bool _autoSpeedSweep;
    [SerializeField] private bool _startInOrbit = true;

    private SimulationRunnerBehaviour _runner;
    private HexWorldRenderer _renderer;
    private NpcAnimSet _animSet;
    private GameObject _modelMarker;
    private Vector2 _scroll;
    private float _worldSpeed = 1f;
    private float _injuredLegFunction = 0.2f;
    private bool _limping;
    private bool _running;
    private bool _autoTurns = true;
    private bool _orbitRequested;
    private bool _started;
    private float _startDelay;
    private float _routeDelay;
    private int _speedIndex = 2;
    private int _turnIndex = 1;
    private float _actualPivotAngle;
    private string _routeMode = "поворот";
    private int _routeBlockedJunctions;
    private int _routeForbiddenSeams;
    private string _routeStatus = "Гекс-мир загружается…";

    private int _lastModelTick = -1;
    private Float2 _lastModelPosition;
    private bool _hasModelSample;
    private float _modelTickSpeed;
    private float _viewSpeed;
    private Vector3 _lastViewPosition;
    private bool _hasViewSample;
    private float _largestPivotSlip;
    private float _largestFrameSpeedError;
    private float _minimumSeamClearance = float.MaxValue;
    private int _modelTicksOnSeam;
    private bool _measureUpperSeamClearance;

    private int _savedVSyncCount;
    private int _savedTargetFrameRate;
    private float _savedBaseMoveSpeed;

    private void Awake()
    {
        Application.runInBackground = true;
        Time.timeScale = 1f;

        _savedVSyncCount = QualitySettings.vSyncCount;
        _savedTargetFrameRate = Application.targetFrameRate;
        _savedBaseMoveSpeed = SimBalance.BaseMoveSpeedFactor;
        _animSet = Resources.Load<NpcAnimSet>("HexLive/NpcAnimSet");

        var root = new GameObject("MovementSmoothnessTest — real hex flow");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        _runner.AutosaveSuppressed = true;
        _renderer = root.AddComponent<HexWorldRenderer>();
        _renderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: _worldSpeed);
        BuildCamera();
        BuildModelMarker();
        BuildObstacleVisuals();
        PrepareNpc();
        StartRoute(_turnIndex);
        _orbitRequested = !_fixedCamera && _startInOrbit;
    }

    private void OnDestroy()
    {
        SimBalance.BaseMoveSpeedFactor = _savedBaseMoveSpeed;
        QualitySettings.vSyncCount = _savedVSyncCount;
        Application.targetFrameRate = _savedTargetFrameRate;
    }

    private WorldBootstrapDefinition BuildWorldDefinition()
    {
        // Radius 3 = 37 ordinary flat game hexes. That includes tile seams,
        // shared junctions and the exact graph used by the colony.
        var tiles = new List<TileBootstrap>();
        const int radius = 3;
        for (var q = -radius; q <= radius; q++)
        {
            var rMin = System.Math.Max(-radius, -q - radius);
            var rMax = System.Math.Min(radius, -q + radius);
            for (var r = rMin; r <= rMax; r++)
            {
                var raisedStep = (q == -1 || q == 0) && r == 1;
                tiles.Add(new TileBootstrap
                {
                    Q = q,
                    R = r,
                    Walkable = true,
                    Elevation = raisedStep ? 2 : 1
                });
            }
        }

        var npc = new NpcBootstrap
        {
            Id = NpcId,
            DisplayName = "Locomotion Tester",
            ActorMesh = _actor,
            FragmentId = 1,
            TileQ = -2,
            TileR = 0,
            Hunger = 0.1f,
            Thirst = 0.1f,
            Energy = 0.95f,
            Comfort = 0.95f,
            Social = 0.95f,
            ThermalDiscomfort = 0f
        };
        npc.Attributes[AttributeKind.Agility] = 0.5f;
        npc.Attributes[AttributeKind.Endurance] = 0.5f;

        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 71058 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
            Fragments = { new FragmentBootstrap { Id = 1, Tiles = tiles } },
            Objects =
            {
                Boulder(101, -1, -1, 0),
                Boulder(102, -1, -1, 3),
                Boulder(103, 0, -1, 1),
                Boulder(104, 0, -1, 4),
                Boulder(105, 1, -1, 2),
                Boulder(106, 1, -1, 5)
            },
            Npcs = { npc }
        };
    }

    private static ObjectBootstrap Boulder(int id, int q, int r, int slot) => new()
    {
        Id = id,
        DefinitionId = "rock.boulder",
        FragmentId = 1,
        TileQ = q,
        TileR = r,
        JunctionSlots = { slot }
    };

    private void PrepareNpc()
    {
        var world = _runner?.Engine?.World;
        var npc = Npc();
        if (world == null || npc == null)
        {
            return;
        }

        // WorldStateFactory creates the normal ambient fauna. It is useful in
        // game, but not in a locomotion ruler: no animal may occupy a route or
        // draw the eye away from the only tested body.
        world.Mobs.Clear();
        world.Rabbits.Clear();
        world.Sharks.Clear();
        world.NextMobSpawnCheckTick = int.MaxValue;
        world.NextRabbitSpawnCheckTick = int.MaxValue;

        // This is the requested "dumb" model. No goal auction or autonomous
        // planning; only our explicit MoveToJunction plan is allowed to live.
        npc.Mind.WakeGraceUntilTick = int.MaxValue;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Needs.Breath = 1f;
    }

    private void Update()
    {
        // Wait until the camera's Start() has established its initial rig
        // state; selecting during Awake would be overwritten by the start
        // pitch and leave the orbit almost top-down.
        if (_orbitRequested)
        {
            _orbitRequested = false;
            Input.NpcSelection.Select(NpcId);
        }

        ApplyLiveControls();
        UpdateTelemetry();

        if (!_started)
        {
            _startDelay += Time.unscaledDeltaTime;
            if (_startDelay >= StartDelaySeconds)
            {
                _started = true;
                _runner.Resume();
                _routeStatus = RouteLabel();
            }
            return;
        }

        var npc = Npc();
        if (npc == null || npc.Movement.IsMoving)
        {
            _routeDelay = 0f;
            return;
        }

        _routeDelay += Time.unscaledDeltaTime;
        // Auto speed sweep is deliberately subordinate to the turn cycle. If
        // the player selected a single route (for example the 180° U-turn)
        // and only the speed checkbox happens to remain on, restarting that
        // same path at the endpoint makes the actor appear to step backwards,
        // pivot, then step forwards again. A manual route must finish and stay
        // finished until the player presses a route button.
        if (_routeDelay < RoutePauseSeconds || !_autoTurns)
        {
            return;
        }

        if (_autoTurns)
        {
            _turnIndex = (_turnIndex + 1) % RequestedTurns.Length;
        }
        if (_autoSpeedSweep && _turnIndex == 0)
        {
            _speedIndex = (_speedIndex + 1) % Speeds.Length;
            _speed = Speeds[_speedIndex];
        }
        StartRoute(_turnIndex);
    }

    private void ApplyLiveControls()
    {
        SimBalance.BaseMoveSpeedFactor = _speed;
        if (_runner != null && Mathf.Abs(_runner.SpeedMultiplier - _worldSpeed) > 0.001f)
        {
            _runner.SetSpeed(_worldSpeed);
        }

        var npc = Npc();
        if (npc == null)
        {
            return;
        }

        // MobSystem is a shared system and its normal ambient spawner runs in
        // this throwaway world too. Keep the lab deterministic even after a
        // tick: no wolf/rabbit/shark is allowed to reach the renderer or route
        // occupancy while we inspect one NPC's feet.
        var world = _runner.Engine.World;
        world.Mobs.Clear();
        world.Rabbits.Clear();
        world.Sharks.Clear();
        world.NextMobSpawnCheckTick = int.MaxValue;
        world.NextRabbitSpawnCheckTick = int.MaxValue;

        npc.Body.Parts[BodyPart.LegL] = _limping ? _injuredLegFunction : 1f;
        npc.Body.Parts[BodyPart.LegR] = 1f;
        // A Hurry goal affects only the MovementSystem urgency/gait decision;
        // the deterministic path below remains ours and no AI plan is created.
        npc.Mind.CurrentGoal = _running ? GoalType.Defend : GoalType.None;
        if (_running)
        {
            npc.Needs.Breath = 1f;
            npc.Mind.BreathSpent = false;
        }
    }

    private void StartRoute(int requestedTurnIndex)
    {
        var world = _runner?.Engine?.World;
        var npc = Npc();
        if (world == null || npc == null)
        {
            _routeStatus = "Модель ещё не готова.";
            return;
        }

        var requested = RequestedTurns[Mathf.Clamp(requestedTurnIndex, 0, RequestedTurns.Length - 1)];
        _routeMode = "контроль поворота";
        _measureUpperSeamClearance = false;
        var pivot = ClosestJunction(world, Float2.Zero);
        var start = ClosestJunction(world, new Float2(-5.2f, 0f));
        var radians = requested * (System.MathF.PI / 180f);
        var target = ClosestJunction(world, new Float2(
            System.MathF.Cos(radians) * 5.2f,
            System.MathF.Sin(radians) * 5.2f));
        if (pivot is not { } pivotId || start is not { } startId || target is not { } targetId ||
            !world.Junctions.Items.TryGetValue(startId, out var startJunction))
        {
            _routeStatus = "Не удалось выбрать точки маршрута.";
            return;
        }

        var entry = NeighborForHeading(world, pivotId, 0f, incoming: true);
        var exitNeighbor = NeighborForHeading(world, pivotId, requested, incoming: false);
        if (entry is not { } entryId || exitNeighbor is not { } exitId)
        {
            _routeStatus = "У центрального junction не нашлись контрольные рёбра.";
            return;
        }

        // Pathfinder remains real on both long legs. The two central edges are
        // explicit so equal-cost tie breaking cannot silently turn a requested
        // 0° control into a 120° bend, as the first live run demonstrated.
        var approach = HexPathfinder.FindPath(world, startId, entryId, null,
            weightClimb: false, canJump: false);
        var exit = HexPathfinder.FindPath(world, exitId, targetId, null,
            weightClimb: false, canJump: false);
        if (approach.Count < 1 || exit.Count < 1)
        {
            _routeStatus = "Pathfinder не собрал обе стороны поворота.";
            return;
        }

        _actualPivotAngle = MeasurePivotAngle(world, entryId, pivotId, exitId);
        ResetMovement(npc);
        var previousTile = npc.Tile;
        var startTile = TileForJunction(world, startId, previousTile);
        npc.Tile = startTile;
        npc.CurrentJunction = startId;
        npc.Position = startJunction.WorldPosition;
        if (previousTile != startTile)
        {
            SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, startTile);
        }

        var first = world.Junctions.Items[approach[1]].WorldPosition - npc.Position;
        npc.RotationDegrees = HexSpatialMath.AngleDegrees(first);
        npc.Movement.DesiredRotationDegrees = npc.RotationDegrees;
        npc.Mind.WakeGraceUntilTick = int.MaxValue;
        npc.Plan.Goal = GoalType.None;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.TargetJunctionId = targetId;
        npc.Plan.TargetTile = TileForJunction(world, targetId, startTile);
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = targetId
        });

        npc.Movement.JunctionPath.AddRange(approach);
        if (npc.Movement.JunctionPath[npc.Movement.JunctionPath.Count - 1] != pivotId)
        {
            npc.Movement.JunctionPath.Add(pivotId);
        }
        npc.Movement.JunctionPath.Add(exitId);
        for (var i = 1; i < exit.Count; i++)
        {
            npc.Movement.JunctionPath.Add(exit[i]);
        }
        ValidateInstalledRoute(world, npc.Movement.JunctionPath, canJump: false);
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Moving);
        ResetTelemetryForRoute();
        _routeStatus = RouteLabel();
    }

    private void StartBypassRoute(bool rocks)
    {
        var world = _runner?.Engine?.World;
        var npc = Npc();
        if (world == null || npc == null)
        {
            _routeStatus = "Модель ещё не готова.";
            return;
        }

        var startCoord = rocks ? new TileCoord(-3, -1) : new TileCoord(-3, 1);
        var targetCoord = rocks ? new TileCoord(2, -1) : new TileCoord(2, 1);
        var start = ClosestJunction(world, HexSpatialMath.TileToWorld(startCoord));
        var target = ClosestJunction(world, HexSpatialMath.TileToWorld(targetCoord));
        if (start is not { } startId || target is not { } targetId ||
            !world.Junctions.Items.TryGetValue(startId, out var startJunction))
        {
            _routeStatus = "Не удалось выбрать обходной маршрут.";
            return;
        }

        // canJump=false is the actual acceptance rule under test: an elevation
        // seam is a wall for this route, exactly like a boulder-blocked node.
        var path = HexPathfinder.FindPath(world, startId, targetId, null,
            weightClimb: false, canJump: false);
        if (path.Count < 2)
        {
            _routeStatus = "Pathfinder не нашёл обход без запрещённого шва.";
            return;
        }

        _autoTurns = false;
        _measureUpperSeamClearance = false;
        _routeMode = rocks ? "обход непроходимых скал" : "обход ступенчатой гряды снизу";
        _actualPivotAngle = 0f;
        ResetMovement(npc);
        var previousTile = npc.Tile;
        var startTile = TileForJunction(world, startId, previousTile);
        npc.Tile = startTile;
        npc.CurrentJunction = startId;
        npc.Position = startJunction.WorldPosition;
        if (previousTile != startTile)
        {
            SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, startTile);
        }

        var first = world.Junctions.Items[path[1]].WorldPosition - npc.Position;
        npc.RotationDegrees = HexSpatialMath.AngleDegrees(first);
        npc.Movement.DesiredRotationDegrees = npc.RotationDegrees;
        npc.Mind.WakeGraceUntilTick = int.MaxValue;
        npc.Plan.Goal = GoalType.None;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.TargetJunctionId = targetId;
        npc.Plan.TargetTile = TileForJunction(world, targetId, startTile);
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = targetId
        });
        npc.Movement.JunctionPath.AddRange(path);
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Moving);
        ValidateInstalledRoute(world, path, canJump: false);
        ResetTelemetryForRoute();
        _routeStatus = $"{_routeMode}: path {path.Count}, blocked {_routeBlockedJunctions}, forbidden seams {_routeForbiddenSeams}.";
    }

    private void StartUpperSeamRoute()
    {
        var world = _runner?.Engine?.World;
        var npc = Npc();
        if (world == null || npc == null)
        {
            _routeStatus = "Модель ещё не готова.";
            return;
        }

        JunctionId? start = null;
        JunctionId? target = null;
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        foreach (var pair in world.Junctions.Items)
        {
            var junction = pair.Value;
            if (junction.Blocked || world.ClimbSeams.Contains(pair.Key) || junction.Tiles.Count == 0)
            {
                continue;
            }

            var allUpper = true;
            foreach (var coord in junction.Tiles)
            {
                if (!world.Tiles.Items.TryGetValue(coord, out var tile) || tile.Elevation != 2)
                {
                    allUpper = false;
                    break;
                }
            }
            if (!allUpper)
            {
                continue;
            }

            if (junction.WorldPosition.X < minX)
            {
                minX = junction.WorldPosition.X;
                start = pair.Key;
            }
            if (junction.WorldPosition.X > maxX)
            {
                maxX = junction.WorldPosition.X;
                target = pair.Key;
            }
        }

        if (start is not { } startId || target is not { } targetId || startId == targetId ||
            !world.Junctions.Items.TryGetValue(startId, out var startJunction))
        {
            _routeStatus = "На верхней площадке недостаточно внутренних точек.";
            return;
        }

        var path = HexPathfinder.FindPath(world, startId, targetId, null,
            weightClimb: false, canJump: false);
        if (path.Count < 2)
        {
            _routeStatus = "Верхний путь без выхода на шов не найден.";
            return;
        }

        _autoTurns = false;
        _measureUpperSeamClearance = true;
        _routeMode = "верх площадки — проверка отступа от шва";
        _actualPivotAngle = 0f;
        ResetMovement(npc);
        var previousTile = npc.Tile;
        var startTile = TileForJunction(world, startId, previousTile);
        npc.Tile = startTile;
        npc.CurrentJunction = startId;
        npc.Position = startJunction.WorldPosition;
        if (previousTile != startTile)
        {
            SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, startTile);
        }

        var first = world.Junctions.Items[path[1]].WorldPosition - npc.Position;
        npc.RotationDegrees = HexSpatialMath.AngleDegrees(first);
        npc.Movement.DesiredRotationDegrees = npc.RotationDegrees;
        npc.Mind.WakeGraceUntilTick = int.MaxValue;
        npc.Plan.Goal = GoalType.None;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.TargetJunctionId = targetId;
        npc.Plan.TargetTile = TileForJunction(world, targetId, startTile);
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = targetId
        });
        npc.Movement.JunctionPath.AddRange(path);
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Moving);
        ValidateInstalledRoute(world, path, canJump: false);
        ResetTelemetryForRoute();
        _routeStatus = $"{_routeMode}: path {path.Count}, seam nodes in path 0 expected.";
    }

    private void ValidateInstalledRoute(WorldState world, List<JunctionId> path, bool canJump)
    {
        _routeBlockedJunctions = 0;
        _routeForbiddenSeams = 0;
        foreach (var id in path)
        {
            if (!world.Junctions.Items.TryGetValue(id, out var junction) || junction.Blocked)
            {
                _routeBlockedJunctions++;
            }
        }

        if (canJump)
        {
            return;
        }
        for (var i = 1; i < path.Count; i++)
        {
            var delta = HexPathfinder.ResolveStepDelta(world, path[i - 1], path[i]);
            if (delta != 0 ||
                (world.ClimbSeams.Contains(path[i - 1]) && world.ClimbSeams.Contains(path[i])))
            {
                _routeForbiddenSeams++;
            }
        }
    }

    private void ResetTelemetryForRoute()
    {
        _routeDelay = 0f;
        _largestPivotSlip = 0f;
        _largestFrameSpeedError = 0f;
        _hasModelSample = false;
        _hasViewSample = false;
        _minimumSeamClearance = float.MaxValue;
        _modelTicksOnSeam = 0;
    }

    private static void ResetMovement(NPCState npc)
    {
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Idle);
        npc.Movement.PostTurnDelay = 0f;
        npc.Movement.PostTurnTimer = 0f;
        npc.Movement.ClimbPauseTimer = 0f;
        npc.Movement.HopTimer = 0f;
        npc.Movement.HopArmed = false;
        npc.Movement.HopPathIndex = -1;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
    }

    private void UpdateTelemetry()
    {
        var npc = Npc();
        if (npc == null)
        {
            return;
        }

        if (_runner.CurrentTick != _lastModelTick)
        {
            if (_hasModelSample)
            {
                var distance = HexSpatialMath.Distance(_lastModelPosition, npc.Position);
                _modelTickSpeed = distance / 0.25f;
                var residual = Mathf.Abs(Mathf.DeltaAngle(
                    npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));
                if (npc.Movement.Status == MovementStatus.Rotating ||
                    npc.Movement.PostTurnDelay > 0f || residual > SimBalance.TurnFreezeAngle)
                {
                    _largestPivotSlip = Mathf.Max(_largestPivotSlip, distance);
                }
            }
            _lastModelPosition = npc.Position;
            _lastModelTick = _runner.CurrentTick;
            _hasModelSample = true;

            if (_measureUpperSeamClearance)
            {
                var nearest = float.MaxValue;
                foreach (var seamId in _runner.Engine.World.ClimbSeams)
                {
                    if (_runner.Engine.World.Junctions.Items.TryGetValue(seamId, out var seam))
                    {
                        nearest = System.MathF.Min(nearest,
                            HexSpatialMath.Distance(npc.Position, seam.WorldPosition));
                    }
                }
                _minimumSeamClearance = System.MathF.Min(_minimumSeamClearance, nearest);
                if (nearest <= 0.02f)
                {
                    _modelTicksOnSeam++;
                }
            }
        }

        if (_renderer != null && _renderer.TryGetNpcViewPosition(NpcId, out var viewPosition))
        {
            if (_hasViewSample && Time.unscaledDeltaTime > 0f)
            {
                _viewSpeed = Vector3.Distance(viewPosition, _lastViewPosition) / Time.unscaledDeltaTime;
                var expected = _speed * npc.Body.MobilityFactor() *
                    (_running ? Spec57.DefendMoveSpeedFactor : 1f);
                _largestFrameSpeedError = Mathf.Max(
                    _largestFrameSpeedError, Mathf.Abs(_viewSpeed - expected));
            }
            _lastViewPosition = viewPosition;
            _hasViewSample = true;
        }

        if (_modelMarker != null)
        {
            var elevation = _runner.Engine.World.Tiles.Items.TryGetValue(npc.Tile, out var tile)
                ? tile.Elevation : 1;
            var groundY = SimulationUnityMapper.TileHeight + elevation * 0.55f;
            _modelMarker.transform.position = SimulationUnityMapper.ToUnityPosition(
                npc.Position, groundY + 0.14f);
        }
    }

    private NPCState Npc()
    {
        var world = _runner?.Engine?.World;
        return world != null && world.Entities.Npcs.TryGetValue(
            new HexLive.Simulation.Common.EntityId(NpcId), out var npc)
            ? npc : null;
    }

    private static JunctionId? ClosestJunction(WorldState world, Float2 point)
    {
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var pair in world.Junctions.Items)
        {
            if (pair.Value.Blocked)
            {
                continue;
            }
            var dx = pair.Value.WorldPosition.X - point.X;
            var dy = pair.Value.WorldPosition.Y - point.Y;
            var sq = dx * dx + dy * dy;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = pair.Key;
            }
        }
        return best;
    }

    private static TileCoord TileForJunction(WorldState world, JunctionId junction, TileCoord fallback)
    {
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (tile.Junctions.Contains(junction))
            {
                return tile.Coord;
            }
        }
        return fallback;
    }

    private static JunctionId? NeighborForHeading(
        WorldState world, JunctionId pivotId, float heading, bool incoming)
    {
        if (!world.Junctions.Items.TryGetValue(pivotId, out var pivot))
        {
            return null;
        }

        JunctionId? best = null;
        var bestError = float.MaxValue;
        foreach (var neighborId in pivot.Neighbors)
        {
            if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor) || neighbor.Blocked)
            {
                continue;
            }
            var direction = incoming
                ? pivot.WorldPosition - neighbor.WorldPosition
                : neighbor.WorldPosition - pivot.WorldPosition;
            var angle = HexSpatialMath.AngleDegrees(direction);
            var error = System.MathF.Abs(MathUtil.DeltaAngle(angle, heading));
            if (error < bestError)
            {
                bestError = error;
                best = neighborId;
            }
        }
        return best;
    }

    private static float MeasurePivotAngle(
        WorldState world, JunctionId entryId, JunctionId pivotId, JunctionId exitId)
    {
        var pivot = world.Junctions.Items[pivotId].WorldPosition;
        var before = world.Junctions.Items[entryId].WorldPosition;
        var after = world.Junctions.Items[exitId].WorldPosition;
        var incoming = HexSpatialMath.AngleDegrees(pivot - before);
        var outgoing = HexSpatialMath.AngleDegrees(after - pivot);
        return System.MathF.Abs(MathUtil.DeltaAngle(incoming, outgoing));
    }

    private string RouteLabel() =>
        $"{_routeMode}: запрошено {RequestedTurns[_turnIndex]:0}°; " +
        $"локальный поворот {_actualPivotAngle:0.0}°; blocked {_routeBlockedJunctions}; " +
        $"forbidden seams {_routeForbiddenSeams}.";

    private void BuildCamera()
    {
        var cameraObject = new GameObject(
            _fixedCamera ? "MovementSmoothness Fixed Camera" : "MovementSmoothness Orbit Camera")
        {
            tag = "MainCamera"
        };
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 42f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 150f;
        cameraObject.AddComponent<AudioListener>();
        if (_fixedCamera)
        {
            cameraObject.transform.position = new Vector3(10.8f, 12.5f, -10.8f);
            cameraObject.transform.LookAt(new Vector3(0f, 0.45f, 0f));
            return;
        }

        var orbit = cameraObject.AddComponent<Input.RtsCameraController>();
        orbit.SetRunner(_runner);
    }

    private void BuildModelMarker()
    {
        _modelMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _modelMarker.name = "RAW MODEL POSITION (4 Hz)";
        _modelMarker.transform.localScale = Vector3.one * 0.2f;
        Destroy(_modelMarker.GetComponent<Collider>());
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { color = new Color(1f, 0.25f, 0.02f) };
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", new Color(1f, 0.06f, 0f) * 2f);
        _modelMarker.GetComponent<MeshRenderer>().material = material;
    }

    private void BuildObstacleVisuals()
    {
        var root = new GameObject("Locomotion test obstacles").transform;
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var rockMaterial = new Material(shader) { color = new Color(0.24f, 0.25f, 0.28f) };
        rockMaterial.SetFloat("_Smoothness", 0f);
        var rockCoords = new[]
        {
            new TileCoord(-1, -1), new TileCoord(0, -1), new TileCoord(1, -1)
        };
        for (var i = 0; i < rockCoords.Length; i++)
        {
            var coord = rockCoords[i];
            var sim = HexSpatialMath.TileToWorld(coord);
            var rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rock.name = $"Impassable boulder {i + 1}";
            rock.transform.SetParent(root);
            rock.transform.position = SimulationUnityMapper.ToUnityPosition(
                sim, SimulationUnityMapper.TileHeight + 0.40f);
            rock.transform.localScale = new Vector3(0.72f, 0.48f, 0.72f);
            rock.GetComponent<MeshRenderer>().sharedMaterial = rockMaterial;
            Destroy(rock.GetComponent<Collider>());
        }
    }

    private void OnGUI()
    {
        var width = Mathf.Min(470f, Screen.width - 24f);
        GUILayout.BeginArea(new Rect(12f, 12f, width, Screen.height - 24f), GUI.skin.box);
        _scroll = GUILayout.BeginScrollView(_scroll);
        GUILayout.Label("ЛАБОРАТОРИЯ ЛОКОМОЦИИ НА РЕАЛЬНЫХ ГЕКСАХ");
        GUILayout.Label(_routeStatus);
        GUILayout.Label("37 гексов · штатные junction/pathfinder/model/snapshot/view · AI выключен");

        GUILayout.Space(6f);
        GUILayout.Label("Скорость тела");
        GUILayout.BeginHorizontal();
        foreach (var speed in Speeds)
        {
            if (GUILayout.Button(speed.ToString("0.00")))
            {
                _speed = speed;
                _speedIndex = System.Array.IndexOf(Speeds, speed);
                StartRoute(_turnIndex);
            }
        }
        GUILayout.EndHorizontal();
        _speed = Slider("BaseMoveSpeed", _speed, 0.1f, 4f);
        _worldSpeed = Slider("Медленный просмотр мира", _worldSpeed, 0.05f, 1f);
        _autoSpeedSweep = GUILayout.Toggle(_autoSpeedSweep,
            " Автопрогон скоростей после полного цикла поворотов");

        GUILayout.Space(6f);
        GUILayout.Label("Повороты реального junction-маршрута");
        GUILayout.BeginHorizontal();
        for (var i = 0; i < RequestedTurns.Length; i++)
        {
            var index = i;
            if (GUILayout.Button($"{RequestedTurns[i]:0}°"))
            {
                _turnIndex = index;
                StartRoute(index);
            }
        }
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("обход СТУПЕНИ снизу")) StartBypassRoute(rocks: false);
        if (GUILayout.Button("обход СКАЛ")) StartBypassRoute(rocks: true);
        GUILayout.EndHorizontal();
        if (GUILayout.Button("ВЕРХ площадки · не вставать на шов")) StartUpperSeamRoute();
        _autoTurns = GUILayout.Toggle(_autoTurns, " Автоматически менять поворот");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("↻ повторить")) StartRoute(_turnIndex);
        if (GUILayout.Button(_runner != null && _runner.IsPaused ? "▶ play" : "Ⅱ pause"))
            _runner?.TogglePause();
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        GUILayout.Label("Поза и модельное замедление");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_running ? "БЕЖИТ (Hurry)" : "ИДЁТ")) _running = !_running;
        if (GUILayout.Button(_limping ? "ХРОМАЕТ" : "ЗДОРОВА")) _limping = !_limping;
        GUILayout.EndHorizontal();
        if (_limping)
        {
            _injuredLegFunction = Slider("Функция левой ноги", _injuredLegFunction, 0.01f, 0.39f);
        }

        GUILayout.Space(6f);
        GUILayout.Label("Калибровка клипов (ростов тела/с при 1×)");
        NpcActorView view = null;
        _renderer?.TryGetActorView(NpcId, out view);
        StrideRow("Шаг", view?.ActiveGaitClip(0), NpcActorView.FullWalkBodyHeightsPerSec);
        StrideRow("Трусца", view?.ActiveGaitClip(1),
            NpcActorView.FullWalkBodyHeightsPerSec * NpcActorView.SlowRunCadence);
        StrideRow("Бег", view?.ActiveGaitClip(2),
            NpcActorView.FullWalkBodyHeightsPerSec * NpcActorView.RunCadence);
        StrideRow("Хромота (отдельный state)", view?.ActiveLimpClip(),
            NpcActorView.LimpBodyHeightsPerSec);

        GUILayout.Space(6f);
        GUILayout.Label("Сглаживание вьюхи");
        NpcActorView.SpeedSmoothTau = Slider("SpeedSmoothTau", NpcActorView.SpeedSmoothTau, 0f, 0.6f);
        NpcActorView.WalkHoldSeconds = Slider("WalkHoldSeconds", NpcActorView.WalkHoldSeconds, 0f, 1f);
        NpcActorView.MidJourneyWalkHoldSeconds = Slider("MidJourneyWalkHold",
            NpcActorView.MidJourneyWalkHoldSeconds, 0f, 2f);
        NpcActorView.PivotYawSpeed = Slider("PivotYawSpeed", NpcActorView.PivotYawSpeed, 20f, 400f);
        var vSync = QualitySettings.vSyncCount > 0;
        var requestedVSync = GUILayout.Toggle(vSync, " VSync 1 (только этот Play Mode)");
        if (requestedVSync != vSync)
        {
            QualitySettings.vSyncCount = requestedVSync ? 1 : 0;
            Application.targetFrameRate = -1;
        }

        DrawTelemetry(view);

#if UNITY_EDITOR
        GUILayout.Space(8f);
        if (GUILayout.Button("СОХРАНИТЬ stride + tuning в игру"))
        {
            var tuning = Resources.Load<Config.HexTuningConfig>(Config.HexTuning.ResourcePath);
            if (tuning != null)
            {
                Config.HexTuning.Capture(tuning);
                UnityEditor.EditorUtility.SetDirty(tuning);
            }
            if (_animSet != null) UnityEditor.EditorUtility.SetDirty(_animSet);
            UnityEditor.AssetDatabase.SaveAssets();
            _routeStatus = "Калибровка сохранена в NpcAnimSet и HexTuningConfig.";
        }
#endif
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawTelemetry(NpcActorView view)
    {
        GUILayout.Space(8f);
        GUILayout.Label("Телеметрия");
        var npc = Npc();
        if (npc == null)
        {
            GUILayout.Label("NPC ещё загружается.");
            return;
        }
        var residual = Mathf.Abs(Mathf.DeltaAngle(
            npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));
        var animator = view != null ? view.GetComponentInChildren<Animator>() : null;
        GUILayout.Label(
            $"tick {_runner.CurrentTick} · model {_modelTickSpeed:0.000} · view {_viewSpeed:0.000} wu/s\n" +
            $"status {npc.Movement.Status} · path {npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}\n" +
            $"rotation {npc.RotationDegrees:0.0}° → {npc.Movement.DesiredRotationDegrees:0.0}° · residual {residual:0.0}°\n" +
            $"requested {RequestedTurns[_turnIndex]:0}° · actual junction bend {_actualPivotAngle:0.0}°\n" +
            $"route {_routeMode} · blocked {_routeBlockedJunctions} · forbidden seams {_routeForbiddenSeams}\n" +
            $"mobility {npc.Body.MobilityFactor():0.000} · running {npc.Mind.IsRunning} · " +
            $"Animator.speed {(animator != null ? animator.speed : 0f):0.00}\n" +
            $"max planted-pivot slip {_largestPivotSlip:0.0000} wu · max frame-speed error {_largestFrameSpeedError:0.000}\n" +
            $"upper seam clearance {(_minimumSeamClearance < float.MaxValue ? _minimumSeamClearance : 0f):0.000} wu · " +
            $"model ticks exactly on seam {_modelTicksOnSeam}");
        GUILayout.Label("Оранжевая сфера = сырая точка модели 4 Гц; персонаж = штатная интерполированная вьюха.");
    }

    private void StrideRow(string label, AnimationClip clip, float fallback)
    {
        if (clip == null || _animSet == null)
        {
            GUILayout.Label($"{label}: —");
            return;
        }
        var current = _animSet.StrideFor(clip, fallback);
        var edited = Slider($"{label}: {clip.name}", current, 0.1f, 4f);
        if (!Mathf.Approximately(edited, current)) _animSet.SetStride(clip, edited);
    }

    private static float Slider(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(260f));
        GUILayout.Label(value.ToString("0.00"), GUILayout.Width(55f));
        GUILayout.EndHorizontal();
        return GUILayout.HorizontalSlider(value, min, max);
    }
}

}
