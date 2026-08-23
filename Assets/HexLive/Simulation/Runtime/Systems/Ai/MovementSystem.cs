using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
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

    // §21.21B v14: how far off the flight axis a lattice point may sit and still
    // count as "flown over" (world units). Below the 0.375 lattice pitch, so a
    // point in the NEXT column — i.e. the route turning away — is never skipped.
    private const float FlightCorridorHalfWidth = 0.3f;

    // §21.21B v23: перед отрывом она обязана ДОВЕРНУТЬСЯ до направления полёта.
    // Окно прыжка не открывается, пока остаточная ошибка курса больше этого
    // порога — стоя в точке взлёта она крутится обычной скоростью поворота, и
    // только потом прыгает. Порог маленький: RotateTowards дошагивает до цели
    // точно, так что это один-два тика доворота, а не вечное ожидание.
    private const float LaunchAlignDegrees = 1f;

    // §24.16 r3: autonomous errands must yield quickly to congestion. The
    // global stuck detector calls 64 unchanged ticks a stall, so the old
    // 41+40 actor-wait windows could diagnose a perfectly known obstruction
    // before movement finally abandoned it. Seventeen ticks gives a brief
    // courtesy pause; thirty-three bounds one repath plus the second wait well
    // below the detector. Manual routes and plans following a live patient or
    // opponent retain the longer windows because their destination carries
    // player/reactive intent and has separate recovery rules.
    private const int AutonomousOccupiedRepathTicks = 17;
    private const int AutonomousOccupiedAbortTicks = 33;
    private const int DirectedOccupiedRepathTicks = 41;
    private const int DirectedOccupiedAbortTicks = 81;


    // Deep water = swim tile; the definition moved to SpatialQueries.IsSwimTile
    // (§106) so combat gates and movement can never disagree about who swims.

    // §40.18-B: crossing from land INTO deep water treads a beat in place before
    // stroking off. One definition shared by the two tile-switch sites (hop
    // landing + ordinary walk step) so the entry timing/condition lives once.
    private static void TryBeginSwimEntry(
        WorldState world, NPCState npc, TileCoord fromTile, TileCoord toTile)
    {
        if (world.Tiles.Items.TryGetValue(toTile, out var landedTile) &&
            world.Tiles.Items.TryGetValue(fromTile, out var leftTile) &&
            SpatialQueries.IsSwimTile(landedTile) && !SpatialQueries.IsSwimTile(leftTile))
        {
            npc.Movement.ClimbPauseTimer = SwimEntryPauseSeconds;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "SwimEnter",
                    $"Pause={SwimEntryPauseSeconds:F2}s Tile={toTile.Q},{toTile.R}");
            }
        }
    }

    // §21.21B v25 / §40.6 r11: a final shoreline junction normally means
    // "stand on the near side" (§21 v24). Bathe is the deliberate exception:
    // RunPrepareBathe records the exact water side in Plan.TargetTile, so the
    // last node is an instruction to cross the edge, not merely approach it.
    // Keep this narrowly tied to Bathe; TargetTile is also used by object and
    // patient interactions whose actors must remain beside their target.
    private static bool TryGetExplicitBathWaterArrival(
        WorldState world, NPCState npc, int targetIndex,
        Junction targetJunction, out Tile waterTile)
    {
        waterTile = default;
        return targetIndex == npc.Movement.JunctionPath.Count - 1 &&
            npc.Plan.Goal == GoalType.Bathe &&
            npc.Plan.TargetTile is { } targetTile &&
            targetJunction.Tiles.Contains(targetTile) &&
            world.Tiles.Items.TryGetValue(targetTile, out waterTile) &&
            waterTile.Flags.HasFlag(TileFlags.Water);
    }

    // §71.2: она стоит НЕ ПО СВОЕЙ ВОЛЕ — упёрлась в товарку, планово
    // разворачивается на месте (только около-разворот, §71.3), переводит дух
    // после него, вылезает из воды. Все эти ветки уходят через `continue`
    // МИМО блока походки и дыхания, и это стоило двух ошибок сразу.
    //
    // ⭐ ФЛАГ БЕГА ЗАВИСАЛ. Он остаётся с прошлого тика, а стоящая на месте
    // не бежит — вид держал бы беговую походку на развороте. В затяжном случае
    // это врёт часами: замер на сиде 12345 поймал чужака с целью `Abuse`,
    // который 870 тиков подряд (3.6 минуты) провисел в `Rotating` с поднятым
    // флагом бега. Сама эта вечная прокрутка — отдельная болезнь поворота, но
    // ПОХОДКА обязана быть честной независимо от неё.
    //
    // ⭐ ДЫХАНИЕ НЕ СЧИТАЛОСЬ вовсе: заминка была для него дырой во времени.
    // Теперь она стоит — значит восстанавливается по стоячей ставке. Порядок
    // важен: флаг гасится ПЕРЕД восстановлением, иначе стоячая ставка чинила
    // бы дыхание «на бегу» и рывок на извилистом маршруте не кончался бы
    // (промежуточная версия так и делала — максимум рывка 108 с вместо 56 с).
    private static void PauseGaitAndBreath(NPCState npc)
    {
        npc.Mind.IsRunning = false;
        npc.Needs.Breath = MathUtil.Clamp01(
            npc.Needs.Breath + SimBalance.BreathIdleRecoverPerTick);
        npc.EffectImpacts.Record(
            NeedKind.Breath,
            EffectKind.BreathRecovery,
            EffectImpactDirection.Positive,
            EffectImpactCadence.Fast);
        if (npc.Needs.Breath >= SimBalance.BreathReArm)
        {
            npc.Mind.BreathSpent = false;
        }
    }

    // §21.21B v17: ОДНО окно прыжка, и оно доигрывает ВСЕГДА.
    //
    // Раньше эта логика жила внутри Run, ниже проверок пути, — а значит
    // прыжок молча умирал, стоило плану оборваться посреди полёта
    // (PlanInterruption чистит путь и IsMoving, и ранний выход в начале Run
    // больше сюда не пускал). Она оставалась ВИСЕТЬ между уровнями: позиция
    // на полпути, npc.Tile ещё взлётный. Вид рисует актёра по высоте
    // npc.Tile — отсюда «спрыгнула, развернулась, и её телепнуло наверх» и
    // «прыгнула в воду без плюха и отшвырнуло обратно на берег».
    // Прыжок — атомарное действие: раз оторвалась, обязана приземлиться.
    // Возвращает true, если тик принадлежит прыжку (шагать в этом тике нельзя).
    private static bool RunHopWindow(WorldState world, NPCState npc)
    {
        if (npc.Movement.HopTimer <= 0f)
        {
            return false;
        }

        npc.Movement.HopTimer -= world.TickDeltaTime;
            // §21.21B v23: ONE window, one set of beats, both directions.
            var hopWindow = HexHopTuning.HopSeconds;
            var hopTakeoff = HexHopTuning.TakeoffSeconds;
            var hopLanding = HexHopTuning.LandingSeconds;
            var hopElapsed = hopWindow - npc.Movement.HopTimer;
            var flightSpan = System.MathF.Max(0.05f, hopWindow - hopTakeoff - hopLanding);

            if (hopElapsed <= hopTakeoff)
            {
                // Push-off beat: she already STOPPED at the takeoff point
                // (v8 stop-short) and already FACES the flight (v23 pre-launch
                // turn) — just hold there and crouch. No gather, no slide.
                npc.Position = npc.Movement.HopFrom;
                npc.Movement.SetStatus(MovementStatus.Waiting);
                return true;
            }

            // Airborne: straight constant-speed flight over the whole beat.
            // §21.21B v23: the v16 "settle fraction" (finish the distance
            // early, then stand) is gone — it read as a snap to the landing
            // point. Constant speed over the full beat is what the view's
            // clip expects anyway.
            var flightT = MathUtil.Clamp01((hopElapsed - hopTakeoff) / flightSpan);
            npc.Position = npc.Movement.HopFrom +
                (npc.Movement.HopTo - npc.Movement.HopFrom) * flightT;
            npc.RotationDegrees = MathUtil.RotateTowards(
                npc.RotationDegrees, npc.Movement.DesiredRotationDegrees,
                npc.TurnSpeed * world.TickDeltaTime);
            npc.Movement.SetStatus(flightT < 1f
                ? MovementStatus.Moving
                : MovementStatus.Waiting); // landing beat: feet planting

            // Landing beat: pre-face the NEXT waypoint while the feet
            // plant, so she stands up already in the right turn instead
            // of landing, pausing and spinning afterwards.
            // §109.13: ⭐ ДОВОРОТА НА ПРИЗЕМЛЕНИИ БОЛЬШЕ НЕТ, и это лечение,
            // а не потеря. Он целился в УЗЕЛ ПУТИ, а ходьба на следующем же
            // тике целится в ТОЧКУ ВЗЛЁТА следующего прыжка (§21.21B v9
            // «ONE TARGET» — она перекрывает цель, когда впереди стена).
            // Два прицела расходились на десятки градусов, и замер по сейву
            // игрока показал ровно то, на что он жаловался: три тика доворота
            // в никуда (+45°), затем рывок обратно (−24.7°) — «прыгнул,
            // спрыгнул, повернулся зачем-то лишний раз».
            //
            // Прицел теперь ОДИН — тот, что ведёт шаг. Обещанного «встала уже
            // в нужном развороте» это не отнимает: поворот идёт 54° за тик,
            // то есть один тик после посадки против трёх тиков верчения не в
            // ту сторону.

            if (flightT >= 1f && !npc.Movement.HopCrossed)
            {
                // A re-plan may have REPLACED (or cleared) the path
                // mid-flight, so the landing index can point into a stale
                // list. §21.21B v17: that is NOT a reason to skip the
                // bookkeeping. It used to bail out here with "landed in
                // place", leaving her POSITION between the two levels and her
                // TILE still the takeoff one — and the view draws her at the
                // ground height of npc.Tile, so she snapped back up onto the
                // ledge she had just dropped off ("её телепает наверх, она
                // уже не прыгает"), or back onto the bank after a dive
                // ("прыгнула в воду без плюха и отшвырнуло обратно").
                // She has landed: commit the landing, whatever the path is
                // doing. Only the path-relative half is skipped.
                var pathLandingValid =
                    npc.Movement.HopLandingIndex < npc.Movement.JunctionPath.Count;

                npc.Movement.HopCrossed = true;
                npc.CurrentJunction = pathLandingValid
                    ? npc.Movement.JunctionPath[npc.Movement.HopLandingIndex]
                    // No path to read it from: take the lattice point she
                    // actually stands on, or the next pathfinding call would
                    // route her from the junction she took off at — a second
                    // way to teleport her back across the border.
                    : SpatialQueries.FindNearestJunction(world, npc.Position)
                        ?? npc.CurrentJunction;

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
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "EnteredTile",
                            $"From={hopPreviousTile.Q},{hopPreviousTile.R} To={npc.Tile.Q},{npc.Tile.R}");
                    }

                    // §40.18-B: dove into deep water — tread a beat (plays
                    // after the landing beat; the pause block defers while
                    // the hop window runs).
                    TryBeginSwimEntry(world, npc, hopPreviousTile, npc.Tile);
                }

                // §21.21B v14: the flight can OVERSHOOT lattice points — the
                // landing sits EdgePadding past the border while a junction
                // just past it can sit closer than that, and the takeoff
                // leaves EdgePadding before it — either way the next path step
                // can be a point she has physically already flown over, and
                // walking to it means stepping backwards. Skip exactly those:
                // a junction whose projection lands INSIDE the flight segment
                // and close to its axis. CurrentJunction rides to the last one
                // skipped — the next step's previousJunctionId, its directed
                // tile and other NPCs' occupancy checks all read it.
                //
                // The bounds matter more than they look. "Anything not ahead
                // of the landing" seems equivalent and is not: when the route
                // turns back along the wall, EVERY remaining point fails that
                // test and the whole path gets eaten (measured: one skip of
                // 118 junctions — she abandoned the route entirely).
                var flight = npc.Movement.HopTo - npc.Movement.HopFrom;
                var flightLength = HexSpatialMath.Distance(
                    npc.Movement.HopFrom, npc.Movement.HopTo);
                var landDir = HexSpatialMath.Normalize(flight);
                var nextIndex = pathLandingValid
                    ? npc.Movement.HopLandingIndex + 1
                    : npc.Movement.JunctionPath.Count; // no path — nothing to advance into
                var eaten = 0;
                while (nextIndex < npc.Movement.JunctionPath.Count &&
                    world.Junctions.Items.TryGetValue(
                        npc.Movement.JunctionPath[nextIndex], out var passedJunction))
                {
                    // §129: a closed-door portal is never "flown over" — the
                    // door gate must see it as the next step. Hops cannot cross
                    // the flat hut perimeter today; this is the cheap belt.
                    if (Spec129.Enabled && passedJunction.Door &&
                        DoorTopology.IsClosedDoorPortal(
                            world, npc.Movement.JunctionPath[nextIndex]))
                    {
                        break;
                    }

                    var rel = passedJunction.WorldPosition - npc.Movement.HopFrom;
                    var along = rel.X * landDir.X + rel.Y * landDir.Y;
                    var across = System.MathF.Abs(rel.X * landDir.Y - rel.Y * landDir.X);
                    // Off the flight line (the route turns), before the
                    // takeoff, or beyond the landing — a real next step.
                    if (across > FlightCorridorHalfWidth ||
                        along < -0.01f || along > flightLength + 0.01f)
                    {
                        break;
                    }

                    npc.CurrentJunction = npc.Movement.JunctionPath[nextIndex];
                    nextIndex++;
                    eaten++;
                }

                npc.Movement.PathIndex = nextIndex;

                // §57.11: спуск-падение кончается не шагом, а подъёмом на ноги
                // (ползущая — просто лежит). Пауза ставится на приземлении и
                // начинает тикать после закрытия окна (см. гейт ClimbPauseTimer
                // ниже по файлу) — и в середине маршрута тоже, не только на
                // последнем узле: упала, отлежалась, поползла дальше.
                if (!npc.Body.CanJump && !npc.Movement.HopUp)
                {
                    npc.Movement.ClimbPauseTimer = System.MathF.Max(
                        npc.Movement.ClimbPauseTimer, HexHopTuning.FallRecoverSeconds);
                }

                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "HopLanded",
                        $"Tile={npc.Tile.Q},{npc.Tile.R} Pos={Trace.FormatPos(npc.Position)} " +
                        $"Step={npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count} Eaten={eaten}");
                }
                if (npc.Movement.PathIndex >= npc.Movement.JunctionPath.Count)
                {
                    // The path ends on this landing — close the hop window
                    // too, or the (now skipped) movement loop would leave
                    // it dangling and HopKind stuck for the view.
                    npc.Movement.HopTimer = 0f;
                    npc.Movement.ClimbPauseTimer = System.MathF.Max(
                        npc.Movement.ClimbPauseTimer, HexHopTuning.LandingSeconds);
                    npc.Movement.IsMoving = false;
                    npc.Movement.SetStatus(MovementStatus.Arrived);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "MovementCompleted",
                            $"HopLanding Tile={npc.Tile.Q},{npc.Tile.R} " +
                            $"Pos={Trace.FormatPos(npc.Position)}");
                    }
                }
            }

        return true;
    }

    public void Run(WorldState world)
    {
        KenshiRescueMath.SyncAll(world);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // §48.7: Movement is the first fast owner and starts the fast
            // influence frame; ExecutionSystem appends timed-interaction rows
            // later in this same layer.
            npc.EffectImpacts.Clear(EffectImpactCadence.Fast);

            // §21.21B v17: a hop in the air outranks EVERYTHING below, including
            // the early-outs. A plan change clears the path and IsMoving, and
            // while this ran further down she was simply abandoned mid-flight —
            // position between the levels, tile still the takeoff one, which the
            // view renders as a snap back onto the ledge. Once she is off the
            // ground she lands, whatever the planner has decided since.
            if (RunHopWindow(world, npc))
            {
                continue;
            }

            // §71: a girl who is not walking anywhere is, by definition, not
            // running — clear the gait flag before any of the early-outs below,
            // or a stale "running" would keep the run clip playing while she
            // stands still. Standing is also when breath comes back fastest.
            if (!npc.Movement.IsMoving || npc.Movement.JunctionPath.Count == 0)
            {
                npc.Mind.IsRunning = false;
                npc.Needs.Breath = MathUtil.Clamp01(
                    npc.Needs.Breath + SimBalance.BreathIdleRecoverPerTick);
                npc.EffectImpacts.Record(
                    NeedKind.Breath,
                    EffectKind.BreathRecovery,
                    EffectImpactDirection.Positive,
                    EffectImpactCadence.Fast);
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
                npc.Movement.SetStatus(MovementStatus.Arrived);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "MovementPathExhausted",
                        $"PathIndex={targetIndex} >= PathCount={npc.Movement.JunctionPath.Count}");
                }
                continue;
            }

            var targetJunctionId = npc.Movement.JunctionPath[targetIndex];
            if (!world.Junctions.Items.TryGetValue(targetJunctionId, out var targetJunction))
            {
                npc.Movement.IsMoving = false;
                npc.Movement.SetStatus(MovementStatus.Invalid);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "MovementInvalidJunction",
                        $"Junction={targetJunctionId.Value} not found in world");
                }
                continue;
            }

            // §129: следующий шаг — портал ЗАКРЫТОЙ двери. Своя открывает
            // створку и пережидает распах (ClimbPauseTimer, как вход в воду);
            // чужая сюда попадает только с протухшим путём (роутер банит
            // закрытые чужие порталы через hardAvoid) — путь сбрасывается, и
            // следующий тик PathfindingSystem строит обход или честный
            // PathFailed. Гейт стоит ДО петли занятости, чтобы не делить с ней
            // BlockedWaitTicks, и ДО carrier-проверки §116.
            if (Spec129.Enabled && targetJunction.Door &&
                DoorTopology.IsClosedDoorPortal(world, targetJunctionId))
            {
                if (DoorTopology.TryGetDoorAt(world, targetJunctionId, out var closedDoor) &&
                    FactionRelations.AreAllies(
                        npc.Faction, DoorTopology.OwnerFaction(world, closedDoor)) &&
                    BuildingDoorRules.TryOpen(world, closedDoor.Id))
                {
                    npc.Movement.ClimbPauseTimer = System.MathF.Max(
                        npc.Movement.ClimbPauseTimer, Spec129.DoorSwingSeconds);
                    npc.Movement.SetStatus(MovementStatus.Waiting);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "DoorOpened",
                            $"Door={closedDoor.Id.Value} Portal={targetJunctionId.Value}");
                    }

                    PauseGaitAndBreath(npc);
                    continue;
                }

                // Не своя (или дверь не резолвится): дословно блок
                // occupancy-repath — сброс пути + разоружение hop-тройки.
                npc.Movement.BlockedWaitTicks = 0;
                npc.Movement.JunctionPath.Clear();
                npc.Movement.IsMoving = false;
                npc.Movement.SetStatus(MovementStatus.Waiting);
                npc.Movement.HopTimer = 0f;
                npc.Movement.HopArmed = false;
                npc.Movement.HopPathIndex = -1;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "DoorRefused",
                        $"Portal={targetJunctionId.Value}");
                }

                PauseGaitAndBreath(npc);
                continue;
            }

            // §118.4: a carrier may use an ordinary hop while keeping the
            // patient linked, but still cannot swim. A stale route that enters
            // deep water releases the patient safely on this side.
            if (npc.IsCarryingPerson)
            {
                var previousJunctionId = targetIndex > 0
                    ? npc.Movement.JunctionPath[targetIndex - 1]
                    : npc.CurrentJunction ?? targetJunctionId;
                if (HexPathfinder.TryGetDirectedStepTile(
                        world, previousJunctionId, targetJunctionId, out var carryStepTile) &&
                    SpatialQueries.IsSwimTile(carryStepTile))
                {
                    KenshiRescueMath.DropSafely(world, npc, "carrier cannot swim");
                    continue;
                }
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
                npc.Movement.StopReason =
                    $"Occupied actor at junction {targetJunctionId.Value}";
                var quickAutonomousWait =
                    !ManualControlMath.IsManual(npc) &&
                    npc.Plan.TargetAgentId is null;
                var repathTicks = quickAutonomousWait
                    ? AutonomousOccupiedRepathTicks
                    : DirectedOccupiedRepathTicks;
                var abortTicks = quickAutonomousWait
                    ? AutonomousOccupiedAbortTicks
                    : DirectedOccupiedAbortTicks;
                if (npc.Movement.BlockedWaitTicks == repathTicks)
                {
                    // §24.16 r2: there is no alternate route around an
                    // occupied FINAL ground-work point.  CraftInPlace owns no
                    // station or patient whose approach must be preserved, so
                    // keeping the same target and rebuilding [from->target]
                    // only repeats the identical wait for another 40 ticks.
                    // Return to the auction now; FindGroundInputPile will pick
                    // another free cluster (or withhold the bid until this one
                    // is free).  Congestion is external to the craft intent,
                    // therefore it is not a failed-attempt rung in the loop
                    // ledger and does not impose a medical-craft cooldown.
                    var finalCraftPoint =
                        targetIndex == npc.Movement.JunctionPath.Count - 1 &&
                        npc.Plan.Steps.Exists(step =>
                            step.Type == PlanStepType.CraftInPlace);
                    var finalAutonomousDestination =
                        !ManualControlMath.IsManual(npc) &&
                        targetIndex == npc.Movement.JunctionPath.Count - 1 &&
                        npc.Plan.TargetAgentId is null;
                    if (finalCraftPoint || finalAutonomousDestination)
                    {
                        var pointKind = finalCraftPoint
                            ? "ground-work point"
                            : npc.Plan.TargetObjectId.HasValue
                                ? "object-work point"
                                : "autonomous destination";
                        if (SimTrace.Enabled)
                        {
                            Trace.Debug(world, npc.Id, "PathOccupied",
                                $"Goal={npc.Plan.Goal} Junction={targetJunctionId.Value} " +
                                $"replan occupied {pointKind}");
                        }
                        if (PlanInterruption.TryAbort(
                                world, npc, InterruptionCause.PathFailure,
                                $"{pointKind} junction {targetJunctionId.Value} occupied"))
                        {
                            npc.Mind.CurrentGoal = GoalType.None;
                            npc.Movement.BlockedWaitTicks = 0;
                            npc.Movement.StopReason = string.Empty;
                            world.IntentLedger.Forget(npc.Id.Value);
                        }

                        PauseGaitAndBreath(npc);
                        continue;
                    }

                    npc.Movement.JunctionPath.Clear();
                    npc.Movement.IsMoving = false;
                    npc.Movement.SetStatus(MovementStatus.Waiting);
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
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "MovementRepath",
                            $"Junction={targetJunctionId.Value} held by a housemate");
                    }
                }
                else if (npc.Movement.BlockedWaitTicks >= abortTicks)
                {
                    // A successful graph search to an actor-occupied FINAL
                    // node is not progress. Previously every rebuild reset the
                    // wait counter, so two actors exchanging targets could
                    // repeat the same valid-looking path forever. After one
                    // alternate-route attempt and another full wait window,
                    // fail this concrete intent and shun its object.
                    var failedGoal = npc.Plan.Goal != GoalType.None
                        ? npc.Plan.Goal
                        : npc.Mind.CurrentGoal;
                    if (npc.Plan.TargetObjectId is { } occupiedTarget)
                    {
                        npc.Memory.Shun(
                            occupiedTarget, world.Tick + AiBalance.ShunTicks);
                    }

                    PlanningSystem.SetGoalCooldown(world, npc, failedGoal);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PathOccupied",
                            $"Goal={failedGoal} Junction={targetJunctionId.Value} " +
                            $"TargetObject={npc.Plan.TargetObjectId?.Value.ToString() ?? "-"} " +
                            "gave up after alternate route");
                    }
                    PlanInterruption.TryAbort(
                        world, npc, InterruptionCause.PathFailure,
                        $"Actor occupied junction {targetJunctionId.Value} after repath");
                    npc.Mind.CurrentGoal = GoalType.None;
                    npc.Movement.BlockedWaitTicks = 0;
                    npc.Movement.StopReason = string.Empty;
                }

                PauseGaitAndBreath(npc);
                continue;
            }

            npc.Movement.BlockedWaitTicks = 0;
            if (npc.Movement.StopReason.StartsWith(
                    "Occupied actor at junction ", System.StringComparison.Ordinal))
            {
                npc.Movement.StopReason = string.Empty;
            }

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
                // Spot the wall at least a padding out, or the takeoff point
                // lands behind her and the clamp below has to salvage it
                // (the climb degrades to a flush walk-up).
                var scanReach = HexHopTuning.EdgePadding + 1.2f;
                for (var i = npc.Movement.PathIndex;
                     i < npc.Movement.JunctionPath.Count && scanDist < scanReach;
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
                        (!SpatialQueries.IsSwimTile(jt) || jt.Elevation < scanTile.Elevation))
                    {
                        wallIndex = i;
                        wallTile = jt;
                        wallNearTile = scanTile;
                        break;
                    }

                    scanTile = jt;
                }

                // ⭐ ПРИХОД — НЕ ПЕРЕСЕЧЕНИЕ (§21.21B v24). Последний узел пути —
                // это место, где надо ВСТАТЬ, а не граница, которую надо
                // перейти. Прыжок по построению перелетает границу на
                // EdgePadding, то есть ЗА точку назначения, и следом резолвер
                // направленного шага записывает её на ДАЛЬНЮЮ сторону.
                //
                // Из-за этого «пошла стирать»: план стирки целится в узел на
                // кромке берега (StandTile = сухой тайл), сим видел границу
                // земля/вода на ПОСЛЕДНЕМ шаге, прыгал в воду (SwimEnter, 2 с
                // барахтанья, мокрая), а исполнитель тут же вытаскивал её
                // обратно на берег через PlaceAtEdge — «спрыгнула и
                // телепортировалась назад», каждый раз.
                //
                // Граничный узел принадлежит обеим сторонам, поэтому стоять на
                // нём можно с ближней: прыжок не нужен вовсе.
                if (wallIndex == npc.Movement.JunctionPath.Count - 1 &&
                    world.Junctions.Items.TryGetValue(
                        npc.Movement.JunctionPath[wallIndex], out var arrivalJunction) &&
                    arrivalJunction.Tiles.Contains(wallNearTile.Coord) &&
                    !TryGetExplicitBathWaterArrival(
                        world, npc, wallIndex, arrivalJunction, out _))
                {
                    wallIndex = -1;
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
                    // CENTRE. (Building the direction from consecutive path
                    // junctions zig-zagged at corners and launched her at the
                    // wrong hex; building it from npc.Tile broke when the wall
                    // was a step ahead. Tile centres are robust for both.)
                    var nearCenter = HexSpatialMath.TileToWorld(nearCoord);
                    var targetCenter = HexSpatialMath.TileToWorld(wallTile.Coord);
                    var crossing = (nearCenter + targetCenter) * 0.5f;
                    var flightDir = HexSpatialMath.Normalize(targetCenter - nearCenter);

                    // §21.21B v23: SYMMETRIC. Takeoff EdgePadding before the
                    // border, landing EdgePadding after — no extra offset at
                    // the landing end, both directions identical.
                    var hopUp = wallTile.Elevation > wallNearTile.Elevation;
                    var landing = crossing + flightDir * HexHopTuning.EdgePadding;

                    // A wall spotted late can put the takeoff point BEHIND
                    // her. Walking back to it is the v9 death: the walk aims
                    // one way, the rescan the other, and she oscillates in
                    // place until she starves. So clamp along the flight axis
                    // to where she actually is, never past the lip.
                    // Clamping the PROJECTION, not taking her position outright:
                    // approaching off-axis, her raw position stretched the jump
                    // to a measured 2.4 wu and sent her flying diagonally
                    // across the corner.
                    var fromCrossing = npc.Position - crossing;
                    var npcAlong = fromCrossing.X * flightDir.X + fromCrossing.Y * flightDir.Y;
                    var takeoff = crossing + flightDir *
                        MathUtil.Clamp(npcAlong, -HexHopTuning.EdgePadding, -0.02f);

                    target = takeoff;                 // ONE target for the walk
                    hopApproach = true;
                    npc.Movement.HopArmed = true;     // commit — freeze the plan
                    npc.Movement.HopLandingIndex = wallIndex;
                    npc.Movement.HopFrom = takeoff;
                    npc.Movement.HopTo = landing;
                    npc.Movement.HopUp = hopUp;
                    npc.Movement.HopTargetTile = wallTile.Coord;
                    // §21.21B v15: the tile she leaves FROM. The view builds the
                    // arc's height delta as target - from and so no longer cares
                    // whether the sim has already committed npc.Tile (it does so
                    // mid-window, which zeroed the delta and left the body
                    // hanging a full step off the ground).
                    npc.Movement.HopFromTile = nearCoord;
                }
            }

            var delta = new Float2(target.X - npc.Position.X, target.Y - npc.Position.Y);
            var direction = HexSpatialMath.Normalize(delta);
            // §21.21B v23: стоя В точке взлёта (доворот перед прыжком) delta —
            // нулевой вектор, и «направление на цель» из него — мусор (0°).
            // Блок прицеливания ниже в этом состоянии пропускается: курсом
            // владеет гейт запуска прыжка.
            var standingAtTakeoff = hopApproach &&
                HexSpatialMath.Distance(npc.Position, target) <= 0.0001f;
            // §76: a nimble girl also pivots faster (same reasoning as the pace
            // above — multiplied in, never stored on npc.TurnSpeed).
            var turnPerTick = npc.TurnSpeed * SimBalance.BaseTurnSpeedFactor *
                AttributeMath.TurnSpeedMult(npc) * world.TickDeltaTime *
                // §50: ползущая и разворачивается по-ползучьи. Тем же множителем,
                // что и шаг (CrawlSpeedFactor): на локтях тело не крутится вокруг
                // оси со скоростью стоящей — оно перебирает руками.
                npc.Body.MobilityTurnFactor();

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
            var initialFacingError = 0f;
            var facingError = 0f;
            if (npc.Movement.HopTimer <= 0f && !standingAtTakeoff)
            {
                npc.Movement.DesiredDirection = direction;
                npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(direction);

                var prevRotation = npc.RotationDegrees;
                initialFacingError = MathUtil.Abs(MathUtil.DeltaAngle(
                    npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));

                // §71.7: decide from the angle REQUESTED at tick start, not the
                // already-reduced residual after turning. At 216°/s the old
                // order converted a 120° command into 66° before testing the
                // 100° pivot threshold, so she translated visibly sideways.
                if (npc.Movement.PostTurnTimer <= 0f &&
                    npc.Movement.PostTurnDelay <= 0f &&
                    LocomotionTurnPolicy.RequiresPlantedPivot(initialFacingError))
                {
                    npc.Movement.PostTurnDelay = 1f;
                }

                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees,
                    npc.Movement.DesiredRotationDegrees,
                    turnPerTick);

                facingError = MathUtil.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));

                // A planted pivot is latched until FULL alignment. Falling
                // below the threshold is not permission to run: that was the
                // one-tick sideways slide at the end of every reversal.
                if (npc.Movement.PostTurnDelay > 0f)
                {
                    npc.Movement.SetStatus(MovementStatus.Rotating);
                    if (facingError <= 0.01f)
                    {
                        npc.Movement.PostTurnDelay = 0f;
                        npc.Movement.PostTurnTimer = SimBalance.PostTurnPauseSeconds;
                    }

                    if (SimTrace.Enabled)
                    {
                        if (SimTrace.Enabled)
                        {
                            Trace.Debug(world, npc.Id, "MovementRotating",
                                $"Rot={prevRotation:F1}->{npc.RotationDegrees:F1} Desired={npc.Movement.DesiredRotationDegrees:F1} " +
                                $"Initial={initialFacingError:F1} Residual={facingError:F1} Planted=1 " +
                                $"ToJunction={targetJunctionId.Value} " +
                                $"Step={targetIndex}/{npc.Movement.JunctionPath.Count}");
                        }
                    }

                    PauseGaitAndBreath(npc);
                    continue;
                }

                if (npc.Movement.PostTurnTimer > 0f)
                {
                    npc.Movement.PostTurnTimer = System.MathF.Max(
                        0f, npc.Movement.PostTurnTimer - world.TickDeltaTime);
                    npc.Movement.SetStatus(MovementStatus.Rotating);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "MovementPostTurnPause",
                            $"Timer={npc.Movement.PostTurnTimer:F2}s remaining");
                    }
                    PauseGaitAndBreath(npc);
                    continue;
                }
            }

            // Standing pause (swim-entry treading, §40.18-B). Deferred while
            // a hop is flying — the hop owns its own timeline; a pending
            // tread pause plays after the landing beat.
            if (npc.Movement.ClimbPauseTimer > 0f && npc.Movement.HopTimer <= 0f)
            {
                npc.Movement.ClimbPauseTimer -= world.TickDeltaTime;
                npc.Movement.SetStatus(MovementStatus.Waiting);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ClimbPause",
                        $"Timer={npc.Movement.ClimbPauseTimer:F2}s remaining");
                }
                PauseGaitAndBreath(npc);
                continue;
            }

            // §21.21B v6 + v17: the hop window ran at the TOP of this loop, before
            // any path check — see RunHopWindow. It has to be up there and not
            // here: down here it sat below the early-out on `!IsMoving || path
            // empty`, so a plan change mid-flight abandoned her in the air.

            // §71: a gentle bend costs nothing; the penalty only ramps in past
            // the deadzone and bottoms out at TurnMinSpeedFactor. Beyond
            // TurnFreezeAngle she never gets here — she planted and pivoted.
            var alignmentFactor = LocomotionTurnPolicy.AlignmentFactor(
                initialFacingError, facingError);

            // Spec 19.3C: mauled legs mean hobbling.
            // §71: BaseMoveSpeedFactor is the global walking-pace knob — the
            // per-NPC npc.MoveSpeed has always been a hardcoded 1 that nothing
            // ever assigns, so this is the one place the colony's pace is set.
            // §76: Agility is her personal share of that pace. It is applied
            // HERE and deliberately NOT written into npc.MoveSpeed: that field
            // is persisted, so parking the attribute in it would store the same
            // number twice and desync every pre-§76 save.
            // §105: «едва живая» — несколько часов после спасения она ходит
            // вдвое медленнее. Ещё один множитель РОВНО в этой цепочке, рядом
            // с мокрой одеждой: «состояние тела режет скорость» — прецедент,
            // а не новая ветка (и npc.MoveSpeed по-прежнему не трогаем).
            var convalescentMove = Spec105.DyingEnabled && world.Tick < npc.Mind.ConvalescentUntilTick
                ? Spec105.ConvalescentMoveFactor
                : 1f;
            var movementPerTick = npc.MoveSpeed * SimBalance.BaseMoveSpeedFactor *
                AttributeMath.MoveSpeedMult(npc) *
                npc.Body.MobilityFactor() *
                EquipmentMath.WetMovementFactor(world, npc) *
                convalescentMove *
                alignmentFactor * world.TickDeltaTime;

            if (npc.IsCarryingPerson)
            {
                var carryFactor = MathUtil.Clamp(
                    Spec118.CarrySpeedMin +
                    Spec118.CarrySpeedStrengthGain * MathUtil.Clamp01(npc.Attributes.Strength),
                    Spec118.CarrySpeedMin, Spec118.CarrySpeedMax);
                movementPerTick *= carryFactor;
                AttributeMath.Train(npc, AttributeKind.Strength,
                    Spec76.AttributeTrainPerRunTick * 2f);
            }

            // §71: GAIT IS A DECISION. Routine autonomous travel may spend the
            // finite breath reserve on a calm sprint; emergencies still carry
            // their stronger pace. Reasons do NOT compound: the largest wins,
            // or a defender who had also just been bitten would hit 2.25 x 2.5
            // and cross the camp in a couple of ticks.
            var manualMoveOrder = npc.Mind.ManualControl &&
                npc.Mind.CurrentGoal == GoalType.PlayerOrder &&
                npc.Plan.Goal == GoalType.PlayerOrder;
            var manualRun = manualMoveOrder && npc.Plan.RunRequested;

            var urgency = 1f;
            // §121.1: the player's click owns the pace of a manual move.
            // A single click must remain a walk even while an old adrenaline
            // timer is live; combat orders use PlayerAttack and stay urgent.
            if (!manualMoveOrder && DamageReactionSystemHelpers.IsAdrenalineActive(world, npc))
            {
                urgency = SimBalance.AdrenalineMoveSpeedFactor;
            }

            // Насколько цель торопит — колонка Urgency в GoalCatalog. Здесь
            // стояли четыре отдельных сравнения с GoalType, и каждая новая
            // спешащая цель требовала не забыть дописать пятое.
            //
            // (§57 клич о помощи / 29C.4B защита подруги / §62 первый удар):
            // бежит встать между подругой и зверем. §89: он ДОГОНЯЕТ — пока он
            // шёл прогулочным шагом, она успевала отойти снова, и сцена не
            // начиналась никогда; гопник не провожает жертву взглядом. Эти
            // причины получают срочный темп, а обычный путь ниже — спокойные
            // дыхательные рывки.
            //
            // Множитель РАЗРЕШАЕТСЯ ЗДЕСЬ, а не хранится в таблице: ручки
            // баланса тюнятся, а таблица строится один раз и заморозила бы их
            // значения на момент своей постройки.
            // Считается ОДИН раз: ниже §71.4 спрашивает тот же класс, а второй
            // вызов — это второй шанс разойтись, когда таблица поменяется.
            var goalUrgency = AI.GoalCatalog.UrgencyFor(npc.Mind.CurrentGoal);
            var routineRun = goalUrgency == AI.UrgencyClass.Stroll &&
                !npc.Mind.ManualControl &&
                npc.Mind.CurrentGoal is not GoalType.None and not GoalType.Idle &&
                !npc.Body.IsProne &&
                !npc.IsCarryingPerson &&
                world.Tick >= npc.Mind.ConvalescentUntilTick &&
                world.Tick >= npc.Mind.SadWalkUntilTick &&
                (!world.Tiles.Items.TryGetValue(npc.Tile, out var routineRunTile) ||
                 !SpatialQueries.IsSwimTile(routineRunTile));
            if (routineRun)
            {
                urgency = System.MathF.Max(urgency, SimBalance.RoutineRunSpeedFactor);
            }
            if (manualRun)
            {
                urgency = System.MathF.Max(urgency, SimBalance.RoutineRunSpeedFactor);
            }

            switch (goalUrgency)
            {
                case AI.UrgencyClass.Hurry:
                    urgency = System.MathF.Max(urgency, Spec57.DefendMoveSpeedFactor);
                    break;
                case AI.UrgencyClass.Flee:
                    urgency = System.MathF.Max(urgency, SimBalance.FleeRunSpeedFactor);
                    break;
            }

            // A body in real trouble hurries to food or water at the same
            // 1.6 pace even if another routine-run condition later changes.
            if ((npc.Mind.IsStarving || npc.Mind.IsDehydrated) &&
                npc.Mind.CurrentGoal is GoalType.GetFood or GoalType.GetWater)
            {
                urgency = System.MathF.Max(urgency, SimBalance.NeedRunSpeedFactor);
            }

            // §71.4: ПРИБЕЖАЛА И ПЕРЕШЛА НА ШАГ. Бегущая упиралась в цель на
            // полном ходу и вставала как вкопанная — торможения в системе нет
            // вообще, скорость на последнем шаге ровно та же, что на первом.
            // Последний метр она проходит шагом, и приход читается как приход.
            //
            // ⭐ ПОГОНЮ НЕ ОСАЖИВАЕМ. Цель-АГЕНТ уходит сама, и «сбавить у
            // цели» здесь значит «никогда не догнать» — ровно то, на чём
            // §89 (гопник) не начинался месяцами. Побег — тоже нет: страх не
            // выдыхается у двери. Прыжок владеет своим окном.
            //
            // Целочисленный гейт по остатку пути стоит ПЕРЕД дистанцией
            // намеренно: пока до конца больше трёх джанкшенов, ни одной новой
            // операции с float не выполняется, и трасса не шевелится там, где
            // поведение не менялось.
            if (urgency > 1.001f &&
                !manualRun &&
                goalUrgency != AI.UrgencyClass.Flee &&
                npc.Plan.TargetAgentId is null &&
                !hopApproach && npc.Movement.HopTimer <= 0f &&
                npc.Movement.JunctionPath.Count - targetIndex <= 3)
            {
                var lastId = npc.Movement.JunctionPath[npc.Movement.JunctionPath.Count - 1];
                if (world.Junctions.Items.TryGetValue(lastId, out var lastJunction) &&
                    HexSpatialMath.Distance(npc.Position, lastJunction.WorldPosition)
                        <= SimBalance.ArrivalWalkDistance)
                {
                    urgency = 1f;
                }
            }

            // §71 BREATH: running is rationed. Spent while she runs, refilled
            // while she walks (faster standing). Once it bottoms out she is
            // forced back to a walk until it climbs to the re-arm line, so she
            // recovers ON THE MOVE and never bids for a rest goal — nothing in
            // the decision layer reads Breath.
            var wantsRun = urgency > 1.001f;
            if (npc.IsCarryingPerson)
            {
                urgency = 1f;
                wantsRun = false;
            }
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
            // §76: Endurance is the sprint reserve — she spends breath slower,
            // so she can hold a run further. Recovery is left alone on purpose:
            // the attribute buys stamina in the chase, not a shorter breather.
            npc.Needs.Breath = MathUtil.Clamp01(npc.Needs.Breath + (running
                ? -SimBalance.BreathDrainPerTick * AttributeMath.BreathDrainMult(npc)
                : SimBalance.BreathWalkRecoverPerTick));
            npc.EffectImpacts.Record(
                NeedKind.Breath,
                running ? EffectKind.Sprinting : EffectKind.BreathRecovery,
                running ? EffectImpactDirection.Negative : EffectImpactDirection.Positive,
                EffectImpactCadence.Fast);

            // §76.13: wind is built by running out of it. Only while actually
            // sprinting — a walk costs her nothing and teaches her nothing.
            if (running)
            {
                AttributeMath.Train(npc, AttributeKind.Endurance,
                    Spec76.AttributeTrainPerRunTick);
            }

            movementPerTick *= urgency;

            // §81.10: понурая походка режет скорость — но ТОЛЬКО когда она
            // ИДЁТ. Понурый клип живёт в нулевом слоте блендера походки, то
            // есть на бегу его не видно вовсе: замедлять бегущую значило бы
            // платить за то, чего не показывают. И платить дорого — 2.25 × 0.5
            // = 1.125, и уходящая от гопника перестаёт уходить: замер поймал
            // ровно это, жертва не добегала до лагеря и погибала (сценарный
            // гейт §104 не находил её в снапшоте).
            if (!running && world.Tick < npc.Mind.SadWalkUntilTick)
            {
                movementPerTick *= Spec81.SadWalkMoveFactor;
            }

            // §40.18-B: deep-water strokes are slower than a walk on land.
            // Keyed off the SWIMMER's tile, so the slowdown starts once she is
            // in the water and ends when she has climbed out.
            if (world.Tiles.Items.TryGetValue(npc.Tile, out var swimStandTile) &&
                SpatialQueries.IsSwimTile(swimStandTile))
            {
                movementPerTick *= SwimSpeedFactor;
            }

            var distance = HexSpatialMath.Distance(npc.Position, target);

            // §21.21B v9: reached the takeoff point — snap onto it, then §21.21B
            // v18: ДОВЕРНУТЬСЯ ДО ОТРЫВА. The hop window opens only once she
            // faces the flight; until then she stands on the takeoff point and
            // pivots at normal turn speed (the view plays the ordinary turn,
            // not the jump clip). No tile switch / path advance now; that
            // happens on touchdown.
            if (hopApproach && distance <= movementPerTick)
            {
                npc.Position = npc.Movement.HopFrom;
                npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(
                    HexSpatialMath.Normalize(npc.Movement.HopTo - npc.Movement.HopFrom));
                var launchError = MathUtil.Abs(MathUtil.DeltaAngle(
                    npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));
                if (launchError > LaunchAlignDegrees)
                {
                    npc.RotationDegrees = MathUtil.RotateTowards(
                        npc.RotationDegrees, npc.Movement.DesiredRotationDegrees,
                        turnPerTick);
                    npc.Movement.SetStatus(MovementStatus.Rotating);
                    PauseGaitAndBreath(npc);
                    continue;
                }

                npc.Movement.HopArmed = false;
                npc.Movement.HopPathIndex = npc.Movement.PathIndex;
                npc.Movement.HopCrossed = false;
                npc.Movement.HopTimer = HexHopTuning.HopSeconds;
                npc.Movement.HopStartTick = world.Tick;
                npc.Movement.SetStatus(MovementStatus.Waiting);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "HopStarted",
                        $"{(npc.Movement.HopUp ? "Up" : "Down")} " +
                        $"From={Trace.FormatPos(npc.Movement.HopFrom)} To={Trace.FormatPos(npc.Movement.HopTo)}");
                }
                continue;
            }

            // §71.3: ОНА ИДЁТ — и статус ставится здесь, до разбора «дошла ли
            // на этом тике до джанкшена». Он стоял только в ветке «шагнула, но
            // не дошла», а шаг НА БЕГУ (0.675..0.75 за тик) длиннее расстояния
            // между точками решётки (0.375): бегущая берёт джанкшен КАЖДЫЙ тик,
            // ветка с присвоением не выполняется ни разу, и статус залипает на
            // том, чем был — обычно `Rotating` от разворота в начале пути.
            //
            // Это не косметика. От статуса зависит скорость заживления
            // (`NeedsDecaySystem`: на ходу раны затягиваются вдвое медленнее),
            // так что бегущая раненая лечилась ВДВОЕ БЫСТРЕЕ положенного —
            // ровно наоборот к замыслу. Плюс статус уезжает в снапшот, и
            // отладка читает «крутится на месте» у той, кто спокойно бежит:
            // на этом уже один раз построили ложный диагноз.
            //
            // Ветка прибытия ниже перепишет его на `Arrived`, прыжок — на свой:
            // оба идут после и выигрывают.
            npc.Movement.Status = MovementStatus.Moving;

            if (distance <= movementPerTick)
            {
                ArriveAtJunction(world, npc, targetIndex, targetJunctionId, targetJunction);

                // §71.9: ПЕРЕНОС ОСТАТКА ШАГА ЧЕРЕЗ УЗЕЛ. Раньше остаток тика
                // здесь выбрасывался: при звене решётки 0.375 wu и шаге
                // 0.345 wu/тик тело чередовало полный шаг с шагом 0.030 —
                // пила 2 Гц, фактическая скорость 54% от заданной, а бегун
                // (шаг длиннее звена) был квантован потолком «узел за тик».
                // Замерено рекордером локомоции (запись 000322). Остаток
                // доезжает по следующим звеньям в ЭТОМ же тике; всё, чему
                // положен собственный тик — крутой поворот, прыжок через шов,
                // занятый узел, вход в воду, — обрывает перенос и разбирается
                // штатными гейтами со следующего тика.
                CarryLeftoverAcrossJunctions(
                    world, npc, movementPerTick - distance, movementPerTick, turnPerTick);
            }
            else
            {
                npc.Position += direction * movementPerTick;
                // §71.3: здесь стояла ЕДИНСТВЕННАЯ запись `Status = Moving`, и
                // легаси-латч заживления обязан взводиться ровно тут же — в
                // ветке неполного шага, куда бегун (шаг длиннее звена решётки)
                // не попадает никогда. Честная запись статуса уехала выше и
                // латч не трогает; SetStatus здесь повторяет старую запись
                // дословно (статус она уже не меняет, латч — да).
                npc.Movement.SetStatus(MovementStatus.Moving);
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees,
                    npc.Movement.DesiredRotationDegrees,
                    turnPerTick);
                if (SimTrace.Enabled)
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "MovementStep",
                            $"Pos={Trace.FormatPos(npc.Position)} -> Junction={targetJunctionId.Value} " +
                            $"Dist={distance:F3} Speed={movementPerTick:F3} Align={alignmentFactor:F2} " +
                            $"Rot={npc.RotationDegrees:F1}");
                    }
                }
            }
        }

        KenshiRescueMath.SyncCarriedPositionsAfterMovement(world);
    }

    // The single arrival primitive: snap to the node, resolve the directed
    // step tile (with the §21.21B v24 "приход — не пересечение" exception for
    // the final node), advance the path index and finish the move when the
    // path is exhausted. Shared verbatim by the normal per-tick arrival and
    // the §71.9 leftover carry — two editions of this block would drift.
    private static void ArriveAtJunction(
        WorldState world, NPCState npc, int targetIndex,
        JunctionId targetJunctionId, Junction targetJunction)
    {
        var previousJunctionId = targetIndex > 0
            ? npc.Movement.JunctionPath[targetIndex - 1]
            : npc.CurrentJunction ?? targetJunctionId;

        var target = targetJunction.WorldPosition;
        npc.Position = target;
        npc.CurrentJunction = targetJunctionId;

        var previousTile = npc.Tile;
        // ⭐ Вторая половина правила «приход — не пересечение»: дойдя до
        // ПОСЛЕДНЕГО узла пути, который граничит и с её собственным
        // тайлом, она остаётся на СВОЁЙ стороне. Резолвер направленного
        // шага смотрит «вперёд по вектору движения» — это верно для
        // узла, который проходят насквозь, и неверно для того, на
        // котором останавливаются: иначе стирающая, дойдя до кромки,
        // числилась бы в воде (мокрая, барахтается), и её приходилось
        // бы вытаскивать назад.
        var entersBathWater = TryGetExplicitBathWaterArrival(
            world, npc, targetIndex, targetJunction, out var bathWaterTile);
        var arrivalStep = targetIndex == npc.Movement.JunctionPath.Count - 1 &&
            targetJunction.Tiles.Contains(previousTile) && !entersBathWater;

        TileCoord? enteredTile = entersBathWater ? bathWaterTile.Coord : null;
        if (enteredTile is null && !arrivalStep &&
            HexPathfinder.TryGetDirectedStepTile(
                world, previousJunctionId, targetJunctionId, out var targetTile))
        {
            enteredTile = targetTile.Coord;
        }

        if (enteredTile is { } newTile)
        {
            if (newTile != previousTile)
            {
                npc.Tile = newTile;
                SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, npc.Tile);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "EnteredTile",
                        $"From={previousTile.Q},{previousTile.R} To={npc.Tile.Q},{npc.Tile.R}");
                }

                // §40.18-B: plunged from land into deep water — tread in
                // place for a beat before stroking off (the view plays
                // the jump-in + treading idle).
                TryBeginSwimEntry(world, npc, previousTile, newTile);
            }
        }

        npc.Movement.PathIndex++;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "JunctionReached",
                $"Junction={targetJunctionId.Value} Pos={Trace.FormatPos(target)} " +
                $"Step={npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}");
        }

        if (npc.Movement.PathIndex >= npc.Movement.JunctionPath.Count)
        {
            npc.Movement.IsMoving = false;
            npc.Movement.SetStatus(MovementStatus.Arrived);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "MovementCompleted",
                    $"FinalJunction={targetJunctionId.Value} Tile={npc.Tile.Q},{npc.Tile.R} " +
                    $"Pos={Trace.FormatPos(npc.Position)}");
            }
        }

        // §129: закрыть за собой. Только что ПОКИНУТЫЙ узел был открытым
        // дверным порталом своей двери — прикрыть, если следом не идёт
        // союзница (портал в ближайших шагах её пути) и в проёме никто не
        // стоит (это второй ремень: TryClose сам отказывает при занятом
        // портале). Путь, ЗАКАНЧИВАЮЩИЙСЯ на портале, сюда не попадает —
        // previousJunctionId не равен порталу, и дверь остаётся открытой.
        if (Spec129.Enabled && Spec129.CloseBehind &&
            !previousJunctionId.Equals(targetJunctionId) &&
            world.Junctions.Items.TryGetValue(previousJunctionId, out var leftJunction) &&
            leftJunction.Door &&
            DoorTopology.TryGetDoorAt(world, previousJunctionId, out var passedDoor) &&
            passedDoor.IsDoorOpen &&
            FactionRelations.AreAllies(
                npc.Faction, DoorTopology.OwnerFaction(world, passedDoor)) &&
            !AllyImminentAtPortal(world, npc, previousJunctionId) &&
            BuildingDoorRules.TryClose(world, passedDoor.Id))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "DoorClosed",
                    $"Door={passedDoor.Id.Value} Portal={previousJunctionId.Value}");
            }
        }
    }

    // §129: идёт ли СОЮЗНИЦА следом через этот проём — стоит на портале или
    // держит его в ближайших CloseBehindLookaheadSteps шагах своего пути.
    // Враждебный протухший маршрут дверь не придерживает: перед его носом
    // закрыться — ровно желаемое поведение.
    private static bool AllyImminentAtPortal(
        WorldState world, NPCState self, JunctionId portalId)
    {
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Value == self.Id.Value ||
                !FactionRelations.AreAllies(self, other))
            {
                continue;
            }

            if (other.CurrentJunction is { } standing && standing.Equals(portalId))
            {
                return true;
            }

            if (!other.Movement.IsMoving)
            {
                continue;
            }

            var path = other.Movement.JunctionPath;
            var from = other.Movement.PathIndex;
            var limit = System.Math.Min(
                path.Count, from + Spec129.CloseBehindLookaheadSteps);
            for (var i = from; i < limit; i++)
            {
                if (path[i].Equals(portalId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // §71.9: spend the remainder of this tick's step budget on the NEXT path
    // segments instead of discarding it at the node. Every situation that
    // deserves its own tick breaks the carry and is handled by the normal
    // top-of-tick gates: a planted pivot, any elevation change (the hop scan
    // must arm a takeoff), deep water for a carrier, an occupied node, a
    // swim-entry tread pause. Bends inside the free/freeze corridor pay the
    // same AlignmentFactor toll as a fresh tick, and rotation continues at
    // the tick-fraction of the normal turn rate — the carry may not turn the
    // body faster than a whole tick could.
    private static void CarryLeftoverAcrossJunctions(
        WorldState world, NPCState npc, float leftover, float movementPerTick, float turnPerTick)
    {
        while (leftover > 0.0005f && npc.Movement.IsMoving &&
               npc.Movement.ClimbPauseTimer <= 0f &&
               npc.Movement.PathIndex < npc.Movement.JunctionPath.Count)
        {
            var nextIndex = npc.Movement.PathIndex;
            var nextJunctionId = npc.Movement.JunctionPath[nextIndex];
            if (!world.Junctions.Items.TryGetValue(nextJunctionId, out var nextJunction))
            {
                return;
            }

            // §129: шаг в портал закрытой двери — дело дверного гейта в начале
            // следующего тика; бегунья не проскакивает створку внутри переноса.
            if (Spec129.Enabled && nextJunction.Door &&
                DoorTopology.IsClosedDoorPortal(world, nextJunctionId))
            {
                return;
            }

            // Someone already standing there — the polite-wait bookkeeping
            // (BlockedWaitTicks, repath) belongs to the next full tick.
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id.Value != npc.Id.Value && other.CurrentJunction is { } oj &&
                    oj.Equals(nextJunctionId))
                {
                    return;
                }
            }

            foreach (var mob in world.Mobs)
            {
                if (mob.Health > 0f && mob.Junction.Equals(nextJunctionId))
                {
                    return;
                }
            }

            var previousJunctionId = nextIndex > 0
                ? npc.Movement.JunctionPath[nextIndex - 1]
                : npc.CurrentJunction ?? nextJunctionId;
            if (HexPathfinder.TryGetDirectedStepTile(
                    world, previousJunctionId, nextJunctionId, out var stepTile))
            {
                // An elevation border is the hop scan's business; deep water
                // is a dead end for occupied hands and a tread pause for
                // everyone else — all of it next tick.
                if (world.Tiles.Items.TryGetValue(npc.Tile, out var standTile) &&
                    stepTile.Elevation != standTile.Elevation)
                {
                    return;
                }

                if (npc.IsCarryingPerson && SpatialQueries.IsSwimTile(stepTile))
                {
                    return;
                }
            }

            var segmentDelta = new Float2(
                nextJunction.WorldPosition.X - npc.Position.X,
                nextJunction.WorldPosition.Y - npc.Position.Y);
            var segmentDistance = HexSpatialMath.Distance(npc.Position, nextJunction.WorldPosition);
            if (segmentDistance <= 0.0001f)
            {
                ArriveAtJunction(world, npc, nextIndex, nextJunctionId, nextJunction);
                continue;
            }

            var segmentDirection = HexSpatialMath.Normalize(segmentDelta);
            var desiredRotation = HexSpatialMath.AngleDegrees(segmentDirection);
            var bend = MathUtil.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, desiredRotation));
            if (LocomotionTurnPolicy.RequiresPlantedPivot(bend))
            {
                return;
            }

            npc.Movement.DesiredDirection = segmentDirection;
            npc.Movement.DesiredRotationDegrees = desiredRotation;
            npc.RotationDegrees = MathUtil.RotateTowards(
                npc.RotationDegrees, desiredRotation,
                turnPerTick * (leftover / movementPerTick));
            var residual = MathUtil.Abs(MathUtil.DeltaAngle(
                npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));
            var alignment = LocomotionTurnPolicy.AlignmentFactor(bend, residual);
            var reach = leftover * alignment;

            if (segmentDistance <= reach)
            {
                // Budget is spent in TIME, not distance: crossing a bend at
                // reduced speed consumes proportionally more of the tick.
                leftover -= segmentDistance / alignment;
                ArriveAtJunction(world, npc, nextIndex, nextJunctionId, nextJunction);
                continue;
            }

            npc.Position += segmentDirection * reach;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "MovementStepCarry",
                    $"Pos={Trace.FormatPos(npc.Position)} -> Junction={nextJunctionId.Value} " +
                    $"Leftover={leftover:F3} Align={alignment:F2}");
            }

            return;
        }
    }
}

}
