using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed class MovementSystem : ISimulationSystem
{
    public string Name => nameof(MovementSystem);

    public TickLayer Layer => TickLayer.Fast;

    // §21.21B hex-step hop: all timing lives in HexHopTuning — one number
    // drives the sim traversal AND the presentation's clip speed and arc.

    // §40.18-B swim TUNING KNOBS: plunging into deep water holds the swimmer
    // treading in place for a beat before the strokes start, and deep-water
    // strokes move slower than a walk. Public statics (not consts) so the
    // swim test scene can tune them live from the inspector.
    public static float SwimEntryPauseSeconds = 0.75f;
    public static float SwimSpeedFactor = 0.6f;

    // Deep water = swim tile; walkable river shallows are waded, not swum.
    private static bool IsSwimTile(Tile tile) =>
        (tile.Flags & TileFlags.Water) != 0 && (tile.Flags & TileFlags.Walkable) == 0;

    // §40.18-B: crossing from land INTO deep water treads a beat in place before
    // stroking off. One definition shared by the two tile-switch sites (hop
    // landing + ordinary walk step) so the entry timing/condition lives once.
    private static void TryBeginSwimEntry(
        WorldState world, NPCState npc, TileCoord fromTile, TileCoord toTile)
    {
        if (world.Tiles.Items.TryGetValue(toTile, out var landedTile) &&
            world.Tiles.Items.TryGetValue(fromTile, out var leftTile) &&
            IsSwimTile(landedTile) && !IsSwimTile(leftTile))
        {
            npc.Movement.ClimbPauseTimer = SwimEntryPauseSeconds;
            Trace.Emit(world, npc.Id, "SwimEnter",
                $"Pause={SwimEntryPauseSeconds:F2}s Tile={toTile.Q},{toTile.R}");
        }
    }

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // §71: a girl who is not walking anywhere is, by definition, not
            // running — clear the gait flag before any of the early-outs below,
            // or a stale "running" would keep the run clip playing while she
            // stands still. Standing is also when breath comes back fastest.
            if (!npc.Movement.IsMoving || npc.Movement.JunctionPath.Count == 0)
            {
                npc.Mind.IsRunning = false;
                npc.Needs.Breath = MathUtil.Clamp01(
                    npc.Needs.Breath + SimBalance.BreathIdleRecoverPerTick);
                if (npc.Needs.Breath >= SimBalance.BreathReArm)
                {
                    npc.Mind.BreathSpent = false;
                }

                continue;
            }

            var targetIndex = npc.Movement.PathIndex;
            if (targetIndex >= npc.Movement.JunctionPath.Count)
            {
                npc.Movement.IsMoving = false;
                npc.Movement.Status = MovementStatus.Arrived;
                Trace.Emit(world, npc.Id, "MovementPathExhausted",
                    $"PathIndex={targetIndex} >= PathCount={npc.Movement.JunctionPath.Count}");
                continue;
            }

            var targetJunctionId = npc.Movement.JunctionPath[targetIndex];
            if (!world.Junctions.Items.TryGetValue(targetJunctionId, out var targetJunction))
            {
                npc.Movement.IsMoving = false;
                npc.Movement.Status = MovementStatus.Invalid;
                Trace.Emit(world, npc.Id, "MovementInvalidJunction",
                    $"Junction={targetJunctionId.Value} not found in world");
                continue;
            }

            // Spec 24.3: someone is standing on my next step — wait like a
            // polite housemate; after 40 ticks give up and re-path around.
            var stepOccupied = false;
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id.Value != npc.Id.Value && other.CurrentJunction is { } oj &&
                    oj.Equals(targetJunctionId))
                {
                    stepOccupied = true;
                    break;
                }
            }

            if (!stepOccupied)
            {
                foreach (var mob in world.Mobs)
                {
                    if (mob.Health > 0f && mob.Junction.Equals(targetJunctionId))
                    {
                        stepOccupied = true;
                        break;
                    }
                }
            }

            if (stepOccupied)
            {
                npc.Movement.BlockedWaitTicks++;
                if (npc.Movement.BlockedWaitTicks > 40)
                {
                    npc.Movement.BlockedWaitTicks = 0;
                    npc.Movement.JunctionPath.Clear();
                    npc.Movement.IsMoving = false;
                    npc.Movement.Status = MovementStatus.Waiting;
                    // §21.21B: a hop must not survive its path — stale hop
                    // state over a NEW path is a mid-air teleport waiting to
                    // happen. Clearing these three DISARMS it: HopTimer=0 stops
                    // the flight branch, HopArmed=false stops the walk-to-takeoff,
                    // HopPathIndex=-1 lets the scan re-arm on the new path. The
                    // remaining hop fields (HopFrom/HopTo/HopCrossed/
                    // HopLandingIndex/HopTargetTile/HopUp) are read ONLY by an
                    // active hop, and the next arm+launch overwrites every one of
                    // them, so their stale values can never fire.
                    npc.Movement.HopTimer = 0f;
                    npc.Movement.HopArmed = false;
                    npc.Movement.HopPathIndex = -1;
                    Trace.Emit(world, npc.Id, "MovementRepath",
                        $"Junction={targetJunctionId.Value} held by a housemate");
                }

                continue;
            }

            npc.Movement.BlockedWaitTicks = 0;

            var target = targetJunction.WorldPosition;

            // §21.21B v9 — ONE TARGET. If an elevation-edge wall is close
            // ahead, OVERRIDE the walk target to the fixed TAKEOFF point
            // (EdgePadding before the wall). Everything below — rotation,
            // pacing, arrival — then aims at that single point, so the walk
            // and the hop can never pull her two ways (the v8 freeze/jitter
            // was exactly that tug-of-war). The takeoff/landing are computed
            // from FIXED lattice points, not her live position, so they don't
            // drift as she approaches. hopApproach makes arrival launch the
            // hop instead of a normal junction crossing.
            var hopApproach = npc.Movement.HopArmed;
            if (hopApproach)
            {
                // Already committed — walk to the FIXED takeoff, no rescan.
                target = npc.Movement.HopFrom;
            }
            else if (npc.Movement.HopTimer <= 0f &&
                npc.Movement.HopPathIndex != npc.Movement.PathIndex &&
                world.Tiles.Items.TryGetValue(npc.Tile, out var hopStandTile))
            {
                // NOTE: swimmers are NO LONGER excluded. The water->land seam is
                // an elevation border like any wall, so climbing OUT arms a
                // clearance hop (takeoff EdgePadding out in the water, land on
                // the shore) instead of a flush walk-up — she stops swimming
                // right against the bank. All water is one elevation, so the
                // scan below (directed step tile changes level) can only fire on
                // the climb-out; it never hops WITHIN the water.
                var wallIndex = -1;
                var wallTile = hopStandTile;
                Tile wallNearTile = hopStandTile;
                var scanDist = 0f;
                var scanFrom = npc.Position;
                var scanTile = hopStandTile;
                for (var i = npc.Movement.PathIndex;
                     i < npc.Movement.JunctionPath.Count && scanDist < HexHopTuning.EdgePadding + 1.2f;
                     i++)
                {
                    var toJunctionId = npc.Movement.JunctionPath[i];
                    var fromJunctionId = i > 0
                        ? npc.Movement.JunctionPath[i - 1]
                        : npc.CurrentJunction ?? toJunctionId;

                    if (!world.Junctions.Items.TryGetValue(toJunctionId, out var jn) ||
                        jn.Tiles.Count == 0)
                    {
                        break;
                    }

                    scanDist += HexSpatialMath.Distance(scanFrom, jn.WorldPosition);
                    scanFrom = jn.WorldPosition;

                    // The tile she steps onto is resolved from the direction of
                    // this path edge. Testing every tile of a seam junction fired
                    // when she merely walked ALONG the wall; using Tiles[0] made
                    // the result depend on generation order. This matches normal
                    // walking's tile update below.
                    if (!HexPathfinder.TryGetDirectedStepTile(world, fromJunctionId, toJunctionId, out var jt))
                    {
                        break;
                    }

                    if (jt.Elevation != scanTile.Elevation &&
                        (!IsSwimTile(jt) || jt.Elevation < scanTile.Elevation))
                    {
                        wallIndex = i;
                        wallTile = jt;
                        wallNearTile = scanTile;
                        break;
                    }

                    scanTile = jt;
                }

                if (wallIndex >= 0)
                {
                    // The hop crosses exactly ONE elevation border: she leaves
                    // the tile just BEFORE the wall junction and lands on the
                    // directed wall tile — the same tile normal walking would
                    // put her on — so the sim bookkeeping and the visual arc
                    // agree and she never re-arms the same crossing.
                    var nearCoord = wallNearTile.Coord;

                    // Fly straight across the shared edge, near CENTRE -> wall
                    // CENTRE, symmetric EdgePadding before/after the border.
                    // (Building the direction from consecutive path junctions
                    // zig-zagged at corners and launched her at the wrong hex;
                    // building it from npc.Tile broke when the wall was a step
                    // ahead. Tile centres are robust for both.)
                    var nearCenter = HexSpatialMath.TileToWorld(nearCoord);
                    var targetCenter = HexSpatialMath.TileToWorld(wallTile.Coord);
                    var crossing = (nearCenter + targetCenter) * 0.5f;
                    var flightDir = HexSpatialMath.Normalize(targetCenter - nearCenter);
                    var stepAlong = HexHopTuning.EdgePadding;
                    var takeoff = crossing - flightDir * stepAlong;
                    var landing = crossing + flightDir * stepAlong;

                    target = takeoff;                 // ONE target for the walk
                    hopApproach = true;
                    npc.Movement.HopArmed = true;     // commit — freeze the plan
                    npc.Movement.HopLandingIndex = wallIndex;
                    npc.Movement.HopFrom = takeoff;
                    npc.Movement.HopTo = landing;
                    npc.Movement.HopUp = wallTile.Elevation > wallNearTile.Elevation;
                    npc.Movement.HopTargetTile = wallTile.Coord;
                }
            }

            var delta = new Float2(target.X - npc.Position.X, target.Y - npc.Position.Y);
            var direction = HexSpatialMath.Normalize(delta);
            var turnPerTick = npc.TurnSpeed * SimBalance.BaseTurnSpeedFactor * world.TickDeltaTime;

            // §21.21B v6: while the hop window runs, the HOP owns rotation
            // and pacing — the walk aiming below would re-target the path
            // junction every tick (mid-air spin to -150° and back, measured
            // in the t=46..62 probe trace) and its facing-error gate would
            // freeze flight ticks while the view clock kept running.
            // §71: a turn is a CURVE, not a stop. The old gate froze her the
            // moment the residual error passed 30°, and because the hex lattice
            // cannot bend by less than 60° while the trip point sat at 52.5°,
            // every single corner cost a 3-tick dead stop — the constant
            // stuttering. Now only a near-reversal plants her; anything gentler
            // she takes at speed, with a penalty that fades out below the
            // deadzone (see ambientAlignment below).
            var facingError = 0f;
            var alignmentThreshold = SimBalance.TurnFreezeAngle;
            if (npc.Movement.HopTimer <= 0f)
            {
                npc.Movement.DesiredDirection = direction;
                npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(direction);

                var prevRotation = npc.RotationDegrees;
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees,
                    npc.Movement.DesiredRotationDegrees,
                    turnPerTick);

                facingError = MathUtil.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));

                if (facingError > alignmentThreshold)
                {
                    npc.Movement.Status = MovementStatus.Rotating;
                    npc.Movement.PostTurnTimer = SimBalance.PostTurnPauseSeconds;
                    if (SimTrace.Verbose)
                    {
                        Trace.Emit(world, npc.Id, "MovementRotating",
                            $"Rot={prevRotation:F1}->{npc.RotationDegrees:F1} Desired={npc.Movement.DesiredRotationDegrees:F1} " +
                            $"Error={facingError:F1}>{alignmentThreshold} ToJunction={targetJunctionId.Value} " +
                            $"Step={targetIndex}/{npc.Movement.JunctionPath.Count}");
                    }
                    continue;
                }

                if (npc.Movement.PostTurnTimer > 0f)
                {
                    npc.Movement.PostTurnTimer -= world.TickDeltaTime;
                    npc.Movement.Status = MovementStatus.Rotating;
                    Trace.Emit(world, npc.Id, "MovementPostTurnPause",
                        $"Timer={npc.Movement.PostTurnTimer:F2}s remaining");
                    continue;
                }
            }

            // Standing pause (swim-entry treading, §40.18-B). Deferred while
            // a hop is flying — the hop owns its own timeline; a pending
            // tread pause plays after the landing beat.
            if (npc.Movement.ClimbPauseTimer > 0f && npc.Movement.HopTimer <= 0f)
            {
                npc.Movement.ClimbPauseTimer -= world.TickDeltaTime;
                npc.Movement.Status = MovementStatus.Waiting;
                Trace.Emit(world, npc.Id, "ClimbPause",
                    $"Timer={npc.Movement.ClimbPauseTimer:F2}s remaining");
                continue;
            }

            // §21.21B v6: the hop OWNS its window — it runs BEFORE the walk
            // rotation/alignment code (which would otherwise re-aim her at
            // the excluded edge junction every tick and even SKIP flight
            // ticks through the facing-error gate: the mid-air spinning and
            // the sim-vs-view clock drift the user saw).
            if (npc.Movement.HopTimer > 0f)
            {
                npc.Movement.HopTimer -= world.TickDeltaTime;
                // §21.21B: up and down can run on different windows (down faster).
                // beatScale is 1 for an up-jump (byte-identical) and shrinks the
                // takeoff/landing beats in step with the shorter down window.
                var hopWindow = HexHopTuning.WindowSeconds(npc.Movement.HopUp);
                var beatScale = npc.Movement.HopUp ? 1f : HexHopTuning.DownBeatScale;
                var hopTakeoff = HexHopTuning.TakeoffSeconds * beatScale;
                var hopLanding = HexHopTuning.LandingSeconds * beatScale;
                var hopElapsed = hopWindow - npc.Movement.HopTimer;
                var flightSpan = System.MathF.Max(0.05f, hopWindow - hopTakeoff - hopLanding);

                if (hopElapsed <= hopTakeoff)
                {
                    // Push-off beat: she already STOPPED at the takeoff point
                    // (v8 stop-short) — just hold there and crouch, turning to
                    // face the flight. No gather, no slide.
                    npc.Position = npc.Movement.HopFrom;
                    npc.RotationDegrees = MathUtil.RotateTowards(
                        npc.RotationDegrees, npc.Movement.DesiredRotationDegrees,
                        npc.TurnSpeed * world.TickDeltaTime);
                    npc.Movement.Status = MovementStatus.Waiting;
                    continue;
                }

                // Airborne: straight lattice-point-to-lattice-point flight.
                var flightT = MathUtil.Clamp01(
                    (hopElapsed - hopTakeoff) / flightSpan);
                npc.Position = npc.Movement.HopFrom +
                    (npc.Movement.HopTo - npc.Movement.HopFrom) * flightT;
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees, npc.Movement.DesiredRotationDegrees,
                    npc.TurnSpeed * world.TickDeltaTime);
                npc.Movement.Status = flightT < 1f
                    ? MovementStatus.Moving
                    : MovementStatus.Waiting; // landing beat: feet planting

                // Landing beat: pre-face the NEXT waypoint while the feet
                // plant, so she stands up already in the right turn instead
                // of landing, pausing and spinning afterwards.
                if (flightT >= 1f && npc.Movement.HopCrossed &&
                    npc.Movement.PathIndex < npc.Movement.JunctionPath.Count &&
                    world.Junctions.Items.TryGetValue(
                        npc.Movement.JunctionPath[npc.Movement.PathIndex], out var nextAfterHop))
                {
                    var toNext = nextAfterHop.WorldPosition - npc.Position;
                    if (HexSpatialMath.Distance(nextAfterHop.WorldPosition, npc.Position) > 0.05f)
                    {
                        npc.Movement.DesiredRotationDegrees =
                            HexSpatialMath.AngleDegrees(HexSpatialMath.Normalize(toNext));
                    }
                }

                if (flightT >= 1f && !npc.Movement.HopCrossed)
                {
                    // A re-plan may have REPLACED the path mid-flight — the
                    // landing index then points into a stale list. Land where
                    // she is, close the hop, let pathfinding re-route.
                    if (npc.Movement.HopLandingIndex >= npc.Movement.JunctionPath.Count)
                    {
                        npc.Movement.HopCrossed = true;
                        npc.Movement.HopTimer = 0f;
                        Trace.Emit(world, npc.Id, "HopAborted",
                            "Path replaced mid-flight — landed in place");
                        continue;
                    }

                    // Touched down on the landing lattice point: do the
                    // bookkeeping for it AND the excluded edge junction.
                    npc.Movement.HopCrossed = true;
                    npc.CurrentJunction =
                        npc.Movement.JunctionPath[npc.Movement.HopLandingIndex];

                    // Tile: §21.21B v11 resolves HopTargetTile as the EXACT tile
                    // she flies into (from the wall junction + her path) and lands
                    // HopTo inside it — so commit it directly. Deriving the tile
                    // from the landing junction's Tiles[0] instead picked a
                    // neighbour (often her OWN previous tile), so npc.Tile never
                    // updated, the scan kept seeing the wall ahead and re-armed
                    // the SAME hop — she bounced on the border ("double jump",
                    // and the wrong tile fed the view a wrong ground Y so she
                    // sank into the hex).
                    var hopLandTile = npc.Movement.HopTargetTile;

                    var hopPreviousTile = npc.Tile;
                    if (hopLandTile != hopPreviousTile)
                    {
                        npc.Tile = hopLandTile;
                        SpatialMutations.MoveEntityToTile(world, npc.Id, hopPreviousTile, npc.Tile);
                        Trace.Emit(world, npc.Id, "EnteredTile",
                            $"From={hopPreviousTile.Q},{hopPreviousTile.R} To={npc.Tile.Q},{npc.Tile.R}");

                        // §40.18-B: dove into deep water — tread a beat (plays
                        // after the landing beat; the pause block defers while
                        // the hop window runs).
                        TryBeginSwimEntry(world, npc, hopPreviousTile, npc.Tile);
                    }

                    npc.Movement.PathIndex = npc.Movement.HopLandingIndex + 1;
                    Trace.Emit(world, npc.Id, "HopLanded",
                        $"Tile={npc.Tile.Q},{npc.Tile.R} Pos={Trace.FormatPos(npc.Position)}");
                    if (npc.Movement.PathIndex >= npc.Movement.JunctionPath.Count)
                    {
                        // The path ends on this landing — close the hop window
                        // too, or the (now skipped) movement loop would leave
                        // it dangling and HopKind stuck for the view.
                        npc.Movement.HopTimer = 0f;
                        npc.Movement.ClimbPauseTimer = System.MathF.Max(
                            npc.Movement.ClimbPauseTimer,
                            HexHopTuning.LandingSeconds *
                                (npc.Movement.HopUp ? 1f : HexHopTuning.DownBeatScale));
                        npc.Movement.IsMoving = false;
                        npc.Movement.Status = MovementStatus.Arrived;
                        Trace.Emit(world, npc.Id, "MovementCompleted",
                            $"HopLanding Tile={npc.Tile.Q},{npc.Tile.R} " +
                            $"Pos={Trace.FormatPos(npc.Position)}");
                    }
                }

                continue;
            }


            // §71: a gentle bend costs nothing; the penalty only ramps in past
            // the deadzone and bottoms out at TurnMinSpeedFactor. Beyond
            // TurnFreezeAngle she never gets here — she planted and pivoted.
            var alignmentFactor = 1f;
            if (facingError > SimBalance.TurnFreeAngle)
            {
                var over = (facingError - SimBalance.TurnFreeAngle) /
                    System.MathF.Max(1f, SimBalance.TurnFreezeAngle - SimBalance.TurnFreeAngle);
                alignmentFactor = 1f - MathUtil.Clamp01(over) * (1f - SimBalance.TurnMinSpeedFactor);
            }

            // Spec 19.3C: mauled legs mean hobbling.
            // §71: BaseMoveSpeedFactor is the global walking-pace knob — the
            // per-NPC npc.MoveSpeed has always been a hardcoded 1 that nothing
            // ever assigns, so this is the one place the colony's pace is set.
            var movementPerTick = npc.MoveSpeed * SimBalance.BaseMoveSpeedFactor *
                npc.Body.MobilityFactor() *
                EquipmentMath.WetMovementFactor(world, npc) *
                alignmentFactor * world.TickDeltaTime;

            // §71: GAIT IS A DECISION. She walks unless there is a reason to
            // run, so a running figure always means something happened — the
            // old "faster than a walk therefore jogging" rule had the whole
            // colony permanently at a trot. The reasons do NOT compound: the
            // largest wins, or a defender who had also just been bitten would
            // hit 2.25 x 2.5 and cross the camp in a couple of ticks.
            var urgency = 1f;
            if (DamageReactionSystemHelpers.IsAdrenalineActive(world, npc))
            {
                urgency = SimBalance.AdrenalineMoveSpeedFactor;
            }

            // (§57 help cry / 29C.4B friend guard / §62 first strike): she is
            // running to put herself between a housemate and the animal.
            if (npc.Mind.CurrentGoal == GoalType.Defend)
            {
                urgency = System.MathF.Max(urgency, Spec57.DefendMoveSpeedFactor);
            }

            if (npc.Mind.CurrentGoal == GoalType.Flee)
            {
                urgency = System.MathF.Max(urgency, SimBalance.FleeRunSpeedFactor);
            }

            // A body in real trouble hurries to the food or the water — the
            // only peacetime reason to run, so the run clip is seen without
            // every stroll becoming a jog.
            if ((npc.Mind.IsStarving || npc.Mind.IsDehydrated) &&
                npc.Mind.CurrentGoal is GoalType.GetFood or GoalType.GetWater)
            {
                urgency = System.MathF.Max(urgency, SimBalance.NeedRunSpeedFactor);
            }

            // §71 BREATH: running is rationed. Spent while she runs, refilled
            // while she walks (faster standing). Once it bottoms out she is
            // forced back to a walk until it climbs to the re-arm line, so she
            // recovers ON THE MOVE and never bids for a rest goal — nothing in
            // the decision layer reads Breath.
            var wantsRun = urgency > 1.001f;
            if (wantsRun && npc.Needs.Breath <= 0f)
            {
                npc.Mind.BreathSpent = true;
            }
            else if (npc.Needs.Breath >= SimBalance.BreathReArm)
            {
                npc.Mind.BreathSpent = false;
            }

            var running = wantsRun && !npc.Mind.BreathSpent;
            if (!running)
            {
                urgency = 1f;
            }

            npc.Mind.IsRunning = running;
            npc.Needs.Breath = MathUtil.Clamp01(npc.Needs.Breath + (running
                ? -SimBalance.BreathDrainPerTick
                : SimBalance.BreathWalkRecoverPerTick));

            movementPerTick *= urgency;

            // §40.18-B: deep-water strokes are slower than a walk on land.
            // Keyed off the SWIMMER's tile, so the slowdown starts once she is
            // in the water and ends when she has climbed out.
            if (world.Tiles.Items.TryGetValue(npc.Tile, out var swimStandTile) &&
                IsSwimTile(swimStandTile))
            {
                movementPerTick *= SwimSpeedFactor;
            }

            var distance = HexSpatialMath.Distance(npc.Position, target);

            // §21.21B v9: reached the takeoff point — launch the hop (the
            // flight block owns the window from here). No tile switch / path
            // advance now; that happens on touchdown.
            if (hopApproach && distance <= movementPerTick)
            {
                npc.Position = npc.Movement.HopFrom;
                npc.Movement.HopArmed = false;
                npc.Movement.HopPathIndex = npc.Movement.PathIndex;
                npc.Movement.HopCrossed = false;
                npc.Movement.HopTimer = HexHopTuning.WindowSeconds(npc.Movement.HopUp);
                npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(
                    HexSpatialMath.Normalize(npc.Movement.HopTo - npc.Movement.HopFrom));
                npc.Movement.Status = MovementStatus.Waiting;
                Trace.Emit(world, npc.Id, "HopStarted",
                    $"{(npc.Movement.HopUp ? "Up" : "Down")} " +
                    $"From={Trace.FormatPos(npc.Movement.HopFrom)} To={Trace.FormatPos(npc.Movement.HopTo)}");
                continue;
            }

            if (distance <= movementPerTick)
            {
                var previousJunctionId = targetIndex > 0
                    ? npc.Movement.JunctionPath[targetIndex - 1]
                    : npc.CurrentJunction ?? targetJunctionId;

                npc.Position = target;
                npc.CurrentJunction = targetJunctionId;

                var previousTile = npc.Tile;
                if (HexPathfinder.TryGetDirectedStepTile(
                    world, previousJunctionId, targetJunctionId, out var targetTile))
                {
                    var newTile = targetTile.Coord;
                    if (newTile != previousTile)
                    {
                        npc.Tile = newTile;
                        SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, npc.Tile);
                        Trace.Emit(world, npc.Id, "EnteredTile",
                            $"From={previousTile.Q},{previousTile.R} To={npc.Tile.Q},{npc.Tile.R}");

                        // §40.18-B: plunged from land into deep water — tread in
                        // place for a beat before stroking off (the view plays
                        // the jump-in + treading idle).
                        TryBeginSwimEntry(world, npc, previousTile, newTile);
                    }
                }

                npc.Movement.PathIndex++;

                Trace.Emit(world, npc.Id, "JunctionReached",
                    $"Junction={targetJunctionId.Value} Pos={Trace.FormatPos(target)} " +
                    $"Step={npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}");

                if (npc.Movement.PathIndex >= npc.Movement.JunctionPath.Count)
                {
                    npc.Movement.IsMoving = false;
                    npc.Movement.Status = MovementStatus.Arrived;
                    Trace.Emit(world, npc.Id, "MovementCompleted",
                        $"FinalJunction={targetJunctionId.Value} Tile={npc.Tile.Q},{npc.Tile.R} " +
                        $"Pos={Trace.FormatPos(npc.Position)}");
                }
            }
            else
            {
                npc.Position += direction * movementPerTick;
                npc.Movement.Status = MovementStatus.Moving;
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees,
                    npc.Movement.DesiredRotationDegrees,
                    turnPerTick);
                if (SimTrace.Verbose)
                {
                    Trace.Emit(world, npc.Id, "MovementStep",
                        $"Pos={Trace.FormatPos(npc.Position)} -> Junction={targetJunctionId.Value} " +
                        $"Dist={distance:F3} Speed={movementPerTick:F3} Align={alignmentFactor:F2} " +
                        $"Rot={npc.RotationDegrees:F1}");
                }
            }
        }
    }
}

}
