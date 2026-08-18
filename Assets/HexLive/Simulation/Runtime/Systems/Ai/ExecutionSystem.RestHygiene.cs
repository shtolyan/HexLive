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

public sealed partial class ExecutionSystem
{
    // Spec 29G: drive a ground rest plan — walk to the reserved spot (the
    // PathfindingSystem does the walking), then rest in place.
    private static void RunGroundRestPlan(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            if (npc.Movement.IsMoving)
            {
                return;
            }

            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Ground rest spot unreachable");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var atTarget = npc.Plan.TargetJunctionId is { } target &&
                npc.CurrentJunction is { } current && current.Equals(target);
            if (!atTarget)
            {
                return;
            }
        }

        if (step.Type == PlanStepType.GroundSit)
        {
            RunGroundRest(world, npc, step, InteractionType.Sit, 70,
                step.TargetJunction is { } lg && PlanningSystem.IsLedgeId(world, lg)
                    ? SimBalance.GroundSitComfortLedge : SimBalance.GroundSitComfort);
        }
        else if (step.Type == PlanStepType.GroundCool)
        {
            // Spec 35.4: dwell in shade/water shedding heat — no comfort/energy
            // gain, the cooling is delivered for free by TemperatureSystem now
            // that she's standing on a genuinely cool tile.
            RunGroundCool(world, npc, step);
        }
        else
        {
            // Sleep restores as well as a bed (a night is a night) — the
            // bed's edge is comfort, not energy. +0.35 energy here produced
            // a poverty trap: 160 naps/soak and no time to live.
            RunGroundRest(world, npc, step, InteractionType.Sleep, 100, 0f); // spec 42
        }
    }

    // Spec 29G: rest on the land — a timed in-place interaction with no
    // object. Lying claims the body's footprint so housemates path around.
    private static void RunGroundRest(
        WorldState world, NPCState npc, PlanStep step,
        InteractionType kind, int durationTicks, float comfort)
    {
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (step.TargetJunction is { } reserved &&
                !SpatialMutations.TryReserveJunction(
                    world, reserved, npc.Id, world.Tick, durationTicks + 8))
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Ground rest edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = kind;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);

                if (kind == InteractionType.Sit && world.Junctions.Items.TryGetValue(spot, out var ledge) &&
                    PlanningSystem.TryGetEdgeSeatGeometry(
                        world, ledge, waterOnly: false, out var standTile, out var facing))
                {
                    PlaceAtEdge(world, npc, ledge, standTile, facing);
                }

                if (kind == InteractionType.Sleep)
                {
                    // §113 r2: voluntary sleep honours the same full-body support
                    // solver as collapse. Unlike an involuntary fall, it can
                    // refuse to start when this tile has no safe footprint.
                    if (!TryLieDownOnGround(world, npc))
                    {
                        SpatialMutations.FreeJunction(world, spot, npc.Id);
                        SpatialMutations.ReleaseJunctionReservation(world, spot, npc.Id);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "No safe ground-lying footprint");
                        npc.Mind.CurrentGoal = GoalType.None;
                        return;
                    }
                }
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"{kind} on the ground Duration={durationTicks}ticks");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // Spec 29C.9 / §42: sitting restores comfort (and the stamina system
        // restores Stamina); Energy belongs exclusively to the slow-tick sleep
        // channel in NeedsDecaySystem. Do not add an energy argument here — it
        // is how ground sitting became a hidden second recovery stream.
        var restShare = durationTicks > 0 ? 1f / durationTicks : 1f;
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfort * restShare);

        var interruptedSleep = kind == InteractionType.Sleep &&
            HasSleepInterrupt(world, npc, alreadyAsleep: true);
        if (!interruptedSleep && npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        if (interruptedSleep)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "SleepInterrupted",
                    $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                    $"Danger={npc.Memory.Dangers.Count}");
            }
        }

        // Spec §49: sleep in ONE continuous lie. Instead of ending the
        // block, standing (wake-grace + get-up clip), re-planning a spot and
        // dropping back down — the "empty get-up" churn that was 57% of night
        // get-ups — re-arm the block in place, holding the footprint claim. She
        // only truly wakes when rested enough or a real need
        // (hunger/thirst/danger) crosses its threshold and the decision
        // system takes over.
        if (kind == InteractionType.Sleep && ShouldKeepSleeping(world, npc))
        {
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "SleepContinued",
                    $"Energy={npc.Needs.Energy:F2} Comfort={npc.Needs.Comfort:F2}");
            }
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, kind == InteractionType.Sleep ? "GroundSleptWell" : "GroundSatDown",
                $"Comfort+{comfort:F2}");
        }

        // Spec 41.5: wake up standing still for a beat — no sprinting off
        // the grass; the get-up clip plays out during the grace.
        if (kind == InteractionType.Sleep)
        {
            npc.Mind.WakeGraceUntilTick = world.Tick + AiBalance.WakeGraceTicks;
        }

        if (kind == InteractionType.Sit)
        {
            npc.Mind.Cooldowns.Add(new GoalCooldown
            {
                Goal = GoalType.Sit,
                EndTick = world.Tick + AiBalance.SitCooldownTicks
            });
        }

        // Canonical cycle reset (same as InteractionCompleted): Execution
        // back to None or the next interaction's start gate never opens.
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (ground rest done)");
        }
    }

    // §137: ПРАЗДНЫЙ ОТДЫХ. Взаимодействие без объекта, без цели и без шага —
    // она просто сидит на земле, пока аукциону нечего ей предложить.
    //
    // Пространственно это НИЧЕГО не занимает и намеренно: сидящая помещается в
    // свой узел ровно так же, как стоящая, поэтому ни брони узла, ни лежачего
    // футпринта §113 здесь нет. Тем отдых сидя и дёшев по сравнению со сном.
    //
    // Подъём стоит времени: на выходе выдаётся та же грация §41.5, что после
    // сна (WakeGraceTicks выведен ИЗ ДЛИНЫ КЛИПА вставания), а сверху ложится
    // колдаун §137, чтобы «встала — села» не превратилось в дрожание.
    private static void RunIdleRest(WorldState world, NPCState npc)
    {
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            // Между планированием и первым тиком исполнения могло случиться
            // что угодно (её позвали, укусили, столкнули в воду) — спрашиваем
            // ещё раз, а не садимся по вчерашнему решению.
            if (IdleRestMath.Blocked(world, npc))
            {
                FinishIdleRest(world, npc, "Blocked", stoodUp: false);
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Rest;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec137.RestBlockTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"Rest on the ground Duration={Spec137.RestBlockTicks}ticks");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // ⭐ Прерывание спрашивается КАЖДЫЙ ТИК, а не на границе такта. Такт
        // закрывает аукцион (общее правило «идёт взаимодействие — не решаем»),
        // так что граница такта — это не «когда она заметит волка», а всего
        // лишь шаг счётчика перевзводов. Заметить обязана сразу.
        if (IdleRestMath.Blocked(world, npc))
        {
            FinishIdleRest(world, npc, "Interrupted", stoodUp: true);
            return;
        }

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Такт вышел, а вставать всё ещё не за чем — перевзвести НА МЕСТЕ, не
        // поднимая её на ноги (тот же приём, что у сна §49 и остывания §35.4:
        // «пустое вставание» было самым частым источником дрожания). Потолок
        // перевзводов — единственное, что заставляет её всё-таки встать и
        // спросить аукцион заново.
        if (npc.Mind.RestRearmCount < Spec137.MaxRearms)
        {
            npc.Mind.RestRearmCount++;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec137.RestBlockTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "RestContinued",
                    $"Rearm={npc.Mind.RestRearmCount}/{Spec137.MaxRearms}");
            }
            return;
        }

        FinishIdleRest(world, npc, "Rested", stoodUp: true);
    }

    /// <param name="stoodUp">
    /// Успела ли она сесть. Если да — вид сейчас играет подъём, и симуляции
    /// нельзя её везти, пока клип не доиграет (§41.5, баг #1: ноги скользят по
    /// земле). Если отдых сорвался ещё до посадки, вставать не с чего, и
    /// красть у неё эти тики было бы враньём.
    /// </param>
    private static void FinishIdleRest(WorldState world, NPCState npc, string reason, bool stoodUp)
    {
        if (stoodUp)
        {
            npc.Mind.WakeGraceUntilTick = System.Math.Max(
                npc.Mind.WakeGraceUntilTick, world.Tick + AiBalance.WakeGraceTicks);
        }

        npc.Mind.RestCooldownUntilTick = world.Tick + Spec137.CooldownTicks;
        npc.Mind.RestRearmCount = 0;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "RestEnded",
                $"Reason={reason} StoodUp={stoodUp} CooldownUntil={npc.Mind.RestCooldownUntilTick}");
        }

        // Канонический сброс цикла — тот же, что у RunGroundRest / RunGroundCool.
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
    }

    // Spec §49/§65.2: should a finished sleep block re-arm in place (keep
    // lying) rather than stand and re-plan? Yes until the energy bar is FULL
    // (day and night alike — one long sleep, not a series of naps) AND no real
    // need has crossed its action threshold — the same thresholds at which
    // Eat/Drink become attractive, so she wakes exactly when there is
    // something to do.
    private static float SleepInterruptHunger => SimBalance.SleepInterruptHunger;

    private static float SleepInterruptThirst => SimBalance.SleepInterruptThirst;

    // §49.9: fuel is time, so reserve TIME rather than an arbitrary pile size.
    // NOTE: cold is deliberately NOT a wake trigger — mild cold at night is the
    // norm and she usually can't fix it, so waking just produced the "empty
    // get-up" churn; sleeping through it is what a real body does (§49.1).
    private static bool ShouldKeepSleeping(WorldState world, NPCState npc)
    {
        if (!Spec49.Rearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the sleep — then the decision
        // system takes over. Everything else: keep lying. NOTE: cold is NOT a
        // wake trigger — a near-naked girl on a 6° night sits at max thermal
        // discomfort she usually can't fix, so waking her only produced the
        // "empty get-up" churn; the cold HP hit lands whether she's up or lying,
        // and lying still conserves. (A fire she could tend is a daytime chore.)
        // §65: already asleep → she sleeps THROUGH moderate hunger/thirst until
        // rested, only waking for a real (starving/dehydrated) or dangerous need.
        if (HasSleepInterrupt(world, npc, alreadyAsleep: true))
        {
            return false;
        }

        // An explicit player order is not a request to refill the energy bar;
        // it is a persistent "stay in bed" order (the same way a Kenshi job
        // remains assigned). A rested manual character therefore keeps lying
        // until Stop/new order interrupts the plan. Real danger and critical
        // hunger/thirst still win through the common interrupt gate above.
        if (npc.Mind.ManualControl && npc.Plan.Goal == GoalType.PlayerOrder &&
            npc.Execution.CurrentInteraction == InteractionType.Sleep)
        {
            return true;
        }

        // Сон КОНЧАЕТСЯ ПО ЭНЕРГИИ, а не по часам — одной строкой для всех.
        // §65.2 r2 дал это дневному сну, §126/§49 r2 убрал ночной затвор,
        // который держал то же правило вторым, отдельным путём: спит до
        // полного, и будит её только настоящая нужда (потолки прерывания выше)
        // или опасность. Пробуждение на старой дневной черте ставило её на
        // ноги всё ещё уставшей — «они постоянно не выспавшиеся».
        return npc.Needs.Energy < Spec49.NightSleepWakeEnergy;
    }

    // §49-parity: the DECISION layer reads this too — going to sleep while an
    // interrupt condition is already true produced the lie-down/stand-up loop
    // (Molly, thirst 0.79 ≥ 0.6: the first sleep tick woke her, the auction
    // put her right back to bed, forever).
    internal static bool HasSleepInterrupt(WorldState world, NPCState npc, bool alreadyAsleep = false)
    {
        // A live threat always ends (or forbids) sleep — never bed down next to
        // a mob, and a fresh scare (adrenaline) keeps her on her feet. §65: by
        // default ANY remembered danger blocks (HasRecentDanger with the window
        // OFF); the optional recency window (SleepDangerRecencyTicks > 0) would
        // let a stale memory age out, but it ships off — see the knob's note.
        // ⭐ §49.10 ДОМА СПЯТ КРЕПКО. Под крышей воспоминание о звере не будит и
        // не мешает лечь: зверь в дом не заходит, и бояться там нечего. Это не
        // поблажка миру — волки, урон и голод не тронуты; тронуто только то,
        // ЧТО СЧИТАТЬ УГРОЗОЙ, когда она за стеной.
        //
        // Замер (seed 476005489, 5 суток): спали 3-8% времени, просыпались с
        // энергией 0.45-0.49, доводили себя до энергии 0.00 и вырубались 31 раз
        // — а в самом §65 записано, что ~60% обмороков это те, кого держал на
        // ногах волк, ушедший треть дня назад. Под крышей этого больше нет.
        //
        // ЖИВОЙ зверь рядом будит и в доме (HasRecentDanger смотрит и на
        // перцепцию, и на свежий адреналин от укуса) — иначе спящую доедали бы
        // прямо в кровати. Голод и жажда тоже будят: потолки ниже нетронуты.
        var roofed = world.Tiles.Items.TryGetValue(npc.Tile, out var restTile) &&
            restTile.Flags.HasFlag(TileFlags.Indoor);
        var scared = world.Tick < npc.Mind.AdrenalineUntilTick;
        if (scared || (HasRecentDanger(world, npc) && !roofed))
        {
            return true;
        }

        // §65: a dead-tired body tolerates MODERATE hunger/thirst rather than
        // being blocked from sleep and grinding to a work-site collapse — the
        // wake ceiling climbs from the normal line to the starving/dehydrated
        // line as she tires. She only bids sleep OVER eating when truly spent
        // (Energy < DeadTiredEnergy); once asleep she sleeps THROUGH to rested
        // (the full-energy line, §65.2 r2) so there is no nap→eat→nap flutter. The
        // cap is the starving line, NOT unbounded: a spent body must still WAKE
        // to eat/drink before hunger/thirst can kill it — a soak with the cap
        // removed let a besieged girl sleep her needs to 1.0 and die (seed 42
        // d7). A reachable meal also still outbids sleep (Eat/Drink carry the
        // StarvingBoost), so this only smooths the moderate band. Danger/
        // adrenaline (above) still forbid lying down.
        var hungerCeiling = SleepInterruptHunger;
        var thirstCeiling = SleepInterruptThirst;
        if (Spec49.DeadTiredSeek)
        {
            var tiredLine = alreadyAsleep ? SimBalance.SleepEnergyThreshold : Spec49.DeadTiredEnergy;
            if (npc.Needs.Energy < tiredLine)
            {
                hungerCeiling = System.Math.Max(hungerCeiling, SimBalance.StarvingEnterThreshold);
                thirstCeiling = System.Math.Max(thirstCeiling, SimBalance.StarvingEnterThreshold);
            }
        }

        return npc.Needs.Hunger >= hungerCeiling || npc.Needs.Thirst >= thirstCeiling;
    }

    // §65: does the girl have a RECENT-enough danger to forbid sleep? The danger
    // memory itself lingers a full day (DecisionSystem prune) so pathing/flee/
    // arm-up keep steering clear of where the wolf prowled — but for SLEEP only
    // a fresh sighting should keep her up. An actively-perceived wolf re-stamps
    // its danger tile every sighting (MobSystem.RememberDangerAt), so it stays
    // inside the window and still blocks; a wolf that wandered off ages out and
    // she can finally rest at the fire. SleepDangerRecencyTicks <= 0 restores
    // the legacy "any remembered danger blocks sleep".
    internal static bool HasRecentDanger(WorldState world, NPCState npc)
    {
        var window = Spec49.SleepDangerRecencyTicks;
        if (window <= 0)
        {
            return npc.Memory.Dangers.Count > 0;
        }

        foreach (var danger in npc.Memory.Dangers)
        {
            if (world.Tick - danger.Tick <= window)
            {
                return true;
            }
        }

        return false;
    }

    // Spec 35.4: dwell in the shade / shallows shedding heat. This is the
    // cool-off twin of RunGroundRest — a timed in-place interaction with no
    // object and no comfort/energy payoff; the cooling itself is delivered by
    // TemperatureSystem because the plan parked her on a genuinely cool tile
    // (shaded or water). At the end of each beat it re-arms in place (like the
    // sleep re-arm) until she has actually cooled, a more urgent need crosses,
    // or the safety cap trips — instead of completing→None and re-winning the
    // goal at zero margin every tick (the old None→CoolOff churn, ~40% of all).
    private static void RunGroundCool(WorldState world, NPCState npc, PlanStep step)
    {
        var bathing = npc.Plan.Goal == GoalType.Bathe;
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"{(bathing ? "Bathe" : "CoolOff")} Duration={Spec49.CoolOffDwellTicks}ticks");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Re-arm the dwell in place (hold the junction occupancy + reservation)
        // while still hot and nothing more urgent calls — bounded by CoolOffMaxRearms
        // so a fallback tile that never actually cools can't freeze her here forever.
        if (Spec49.CoolRearm &&
            npc.Mind.CoolRearmCount < Spec49.CoolOffMaxRearms &&
            (bathing ? ShouldKeepBathing(npc) : ShouldKeepCooling(world, npc)))
        {
            npc.Mind.CoolRearmCount++;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, bathing ? "BatheContinued" : "CoolContinued",
                    $"Rearm={npc.Mind.CoolRearmCount} Hygiene={npc.Needs.Hygiene:F2} " +
                    $"ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2}");
            }
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, bathing ? "Bathed" : "CooledOff",
                $"Hygiene={npc.Needs.Hygiene:F2} ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2} " +
                $"Rearms={npc.Mind.CoolRearmCount}");
        }

        // A short refractory window so she doesn't instantly re-select CoolOff even
        // if discomfort still hovers just under the clear edge (mirrors Sit's 240t).
        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = bathing ? GoalType.Bathe : GoalType.CoolOff,
            EndTick = world.Tick + SimBalance.CoolOffSettleTicks
        });

        // Canonical cycle reset (same as RunGroundRest / InteractionCompleted).
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.CoolRearmCount = 0;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (cool-off done)");
        }
    }

    private static bool ShouldKeepBathing(NPCState npc) =>
        npc.Needs.Hygiene < 0.95f || EquipmentMath.AverageDirtiness(npc) > 0.05f;

    private static void RunPrepareBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } shore || !current.Equals(shore))
        {
            return;
        }

        // §40.6: remember where she is undressing so she can come back for the
        // pile after her swim (the list is filled as each piece drops below).
        // If §133 undresses at home, this is the home return point. A second
        // PrepareBathe at the real shore must not overwrite it.
        npc.Mind.RedressShore ??= shore;

        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.Undress)
        {
            var garment = npc.Execution.HeldGarment;
            if (garment is null && npc.Plan.TargetItemDefinitionId is { } definitionId)
            {
                for (var i = npc.WornItems.Count - 1; i >= 0; i--)
                {
                    if (npc.WornItems[i].DefinitionId == definitionId)
                    {
                        garment = npc.WornItems[i];
                        break;
                    }
                }
            }

            if (garment is null)
            {
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.Bathe);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Bathe: active garment disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            if (world.Tick < npc.Execution.EndTick)
            {
                return;
            }

            // #147: Bathe persists the transaction but not transient hand
            // props. Keep the authoritative item worn until the doff beat is
            // complete; a save/interrupt before that point therefore resumes
            // with the item still owned, never in a non-serialized limbo.
            npc.WornItems.Remove(garment);
            // §133: если раздевается у гардероба/сушилки — вещь вешается на неё,
            // а не падает под ноги. Станция переполнилась (вторая купальщица
            // успела раньше) — тогда честная куча на полу, это всё равно дома.
            // #173: план, ПЕРЕСОБРАННЫЙ после прерывания, приходит без станции
            // в шаге (resume-ветка BuildBathePlan знает только точку возврата) —
            // тогда станцию разрешаем поздно, по факту «стоит рядом». Стирку не
            // трогаем: LaundryBatch раздевается у воды и кладёт вещи под стирку.
            var stowTarget = step.TargetObject;
            if (stowTarget is null &&
                npc.Mind.PersonalCarePhase != PersonalCarePhase.LaundryBatch)
            {
                stowTarget = StowMath.StationBesideNpc(world, npc);
            }

            var doffed = StowGarmentWithContents(world, npc, garment, stowTarget);
            // §40.6: remember this exact ground piece so she re-dons it after
            // the swim (the same clothes she took off, not just any garment).
            if (doffed != null)
            {
                npc.Mind.RedressGarments.Add(doffed.Id);
            }

            npc.Execution.HeldGarment = null;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Plan.TargetItemDefinitionId = null;
            EquipmentMath.Recalculate(world, npc);

            // ⭐ §40.6 r14 (#147 rework): СНЯЛА — СТИРАЕТ ЭТУ ЖЕ ВЕЩЬ, сразу.
            // Прошлая версия снимала весь ворох и стирала его одним общим
            // проходом; игрок на это ответил: «нужно сначала показывать, что
            // она стирает… подошли к краю воды, сняли первую шмотку, начали
            // стирать её… постирали, положили на песок, потом следующую».
            // Поэтому стирка теперь поштучная и с видимым статусом на каждой.
            if (doffed != null && IsLaundry(doffed))
            {
                StartGarmentWash(world, npc, doffed);
            }

            return;
        }

        // §40.6 r14: идёт стирка ОДНОЙ снятой вещи. Пока бьёт этот такт, статус
        // персонажа — «стирает», и это ровно то, что игрок хотел видеть.
        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.WashClothes)
        {
            if (!TickGarmentWash(world, npc))
            {
                return;
            }
        }

        // §52.8: strip CLOTHES for the swim, never gear. A leg holster of tools
        // is not laundry — it stays strapped on, so it can never be the piece
        // left abandoned on the sand when a bathe is cut short.
        var stripIndex = -1;
        for (var i = npc.WornItems.Count - 1; i >= 0; i--)
        {
            var item = npc.WornItems[i];
            var laundryCandidate = MathUtil.Clamp01(item.Dirtiness + item.Bloodiness) > 0.001f;
            if (!HolsterCatalog.IsHolster(item.DefinitionId) &&
                (npc.Mind.PersonalCarePhase != PersonalCarePhase.LaundryBatch || laundryCandidate))
            {
                stripIndex = i;
                break;
            }
        }

        if (stripIndex >= 0)
        {
            var garment = npc.WornItems[stripIndex];
            npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            npc.Execution.HeldGarment = null;
            return;
        }

        // §40.6 r14 (#147 rework): стирка кончилась — грязного на ней больше
        // нет. Дальше развилка, которую игрок и просил РАЗЛИЧАТЬ:
        //
        //   грязна ОНА САМА → складывает чистое на берегу, купается, одевается;
        //   грязна была только одежда → сразу надевает постиранное и уходит.
        //
        // Прежняя версия купалась всегда, потому что стирка и купание были
        // одной неразрывной сделкой.
        if (npc.Mind.PersonalCarePhase == PersonalCarePhase.LaundryBatch)
        {
            var dirtyLeft = CountDirtyRedressGarments(world, npc);
            if (dirtyLeft > 0 && npc.Execution.Status == ExecutionStatus.None)
            {
                // Осталась грязная вещь, но не на ней (сняли раньше и не
                // достирали — прерывание/загрузка). Достирываем поштучно.
                foreach (var id in npc.Mind.RedressGarments)
                {
                    if (world.Entities.Objects.TryGetValue(id, out var pending) &&
                        IsLaundry(pending))
                    {
                        StartGarmentWash(world, npc, pending);
                        return;
                    }
                }
            }

            if (!WantsBodyBath(npc))
            {
                npc.Mind.PersonalCarePhase = PersonalCarePhase.Redress;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LaundryOnly",
                        $"Постирала {npc.Mind.RedressGarments.Count} вещей; " +
                        $"сама чистая (гигиена={npc.Needs.Hygiene:F2}) — одевается без купания");
                }

                // Тот же путь одевания, что после купания: она уже стоит на том
                // самом берегу, где лежит её постиранное, поэтому шаг движения
                // отработает мгновенно. Куча пуста (нечего надевать) — обычное
                // завершение сцены.
                if (!TryBeginPostBatheRedress(world, npc, shore))
                {
                    FinishPersonalCare(world, npc, shore, GoalType.Bathe, "Bathed");
                }

                return;
            }

            // Чистый остаток она носит весь черёд стирки; снимут его только
            // теперь, перед водой, на следующих тиках.
            npc.Mind.PersonalCarePhase = PersonalCarePhase.Bathing;
            return;
        }

        var bathShore = npc.Mind.PersonalCareBathShore ??
            (step.TimeoutEndTick is { } encodedShore &&
                        world.Junctions.Items.ContainsKey(new JunctionId(encodedShore))
            ? new JunctionId(encodedShore)
            : npc.Plan.TargetJunctionId ?? shore);
        if (!current.Equals(bathShore))
        {
            // Undressing happened at home. Release that work point, claim the
            // shore selected by planning (or a fresh exact shore if it became
            // occupied), then perform the ordinary shore preparation there.
            SpatialMutations.ReleaseJunctionReservation(world, shore, npc.Id);
            if (!SpatialMutations.TryReserveJunction(
                    world, bathShore, npc.Id, world.Tick, 96))
            {
                var replacement = HygieneMath.FindReachableBathShore(world, npc);
                if (replacement is null ||
                    !SpatialMutations.TryReserveJunction(
                        world, replacement.Id, npc.Id, world.Tick, 96))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, GoalType.Bathe);
                    PlanInterruption.TryAbort(world, npc,
                        InterruptionCause.ExecutionFailure,
                        "Bathe: shore became occupied after undressing");
                    npc.Mind.CurrentGoal = GoalType.None;
                    return;
                }
                bathShore = replacement.Id;
                npc.Mind.PersonalCareBathShore = replacement.Id;
                npc.Plan.TargetJunctionId = replacement.Id;
                npc.Plan.TargetTile = replacement.Tiles[0];
            }

            npc.Plan.Steps.Clear();
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = bathShore
            });
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.PrepareBathe,
                TargetJunction = bathShore
            });
            npc.Plan.TargetJunctionId = bathShore;
            npc.Plan.TargetTile = world.Junctions.Items[bathShore].Tiles.Count > 0
                ? world.Junctions.Items[bathShore].Tiles[0]
                : null;
            npc.Plan.CurrentStepIndex = 0;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.PathIndex = 0;
            npc.Movement.IsMoving = false;
            return;
        }

        var swim = HygieneMath.FindRoundTripBathWater(world, npc, bathShore);

        if (swim is null)
        {
            // Planning proved a reversible dip when it chose this shore, but
            // topology may change while the NPC walks/undresses. Treat that as
            // a real failed attempt: without the shared cooldown Decision
            // selected Bathe again on the very next medium pass and rebuilt
            // the same impossible plan every four ticks (seed 1104, t28123+).
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Bathe);
            PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Bathe: no reachable water junction");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        SpatialMutations.ReleaseJunctionReservation(world, bathShore, npc.Id);
        npc.Plan.TargetJunctionId = swim.Id;
        npc.Plan.TargetTile = swim.Tiles[0];
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = swim.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.SwimBathe, TargetJunction = swim.Id });
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "BatheReady", $"Naked; swimming to {swim.Id.Value}");

        }
    }

    private static void RunSwimBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target || !current.Equals(target))
        {
            return;
        }

        if (npc.WornItems.Count > 0)
        {
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Bathe);
            PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Bathe requires complete undressing");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // §40.6 r11: hygiene may only advance while the authoritative body tile
        // is actually water. Previously the final-edge arrival guard could
        // leave her on dry land while this timer silently washed her anyway,
        // making the presentation truthfully show a dry actor at the bank.
        if (!world.Tiles.Items.TryGetValue(npc.Tile, out var bathingTile) ||
            !bathingTile.Flags.HasFlag(TileFlags.Water))
        {
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Bathe);
            PlanInterruption.TryAbort(world, npc,
                InterruptionCause.ExecutionFailure,
                "Bathe requires entering the selected water tile");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (npc.Mind.PersonalCarePhase == PersonalCarePhase.None)
                npc.Mind.PersonalCarePhase = PersonalCarePhase.Bathing;
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + SimBalance.BatheDurationTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "BatheStarted",
                    $"Duration={SimBalance.BatheDurationTicks} ticks (25 real seconds)");
            }
            return;
        }

        npc.Needs.Hygiene = MathUtil.Clamp01(npc.Needs.Hygiene +
            1f / SimBalance.BatheDurationTicks);

        // ⭐ §49.11: ВЫМОТАЛАСЬ В ВОДЕ — НА БЕРЕГ, не домываться. Вырубиться на
        // глубине значит утонуть (§60.7), и вытащить её оттуда нельзя: спасение
        // в воду не заходит. Купание обрывается ровно как по таймеру — ниже
        // тот же путь на берег за одеждой, — так что недомытая, но живая.
        var spent = npc.Needs.Energy < Spec49.DeadTiredEnergy;
        if (spent && SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "BatheCutShort",
                $"Выдохлась в воде: энергия={npc.Needs.Energy:F2} — на берег");
        }

        if (!spent && world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        npc.Needs.Hygiene = 1f;
        // §40.8-H r10: осознанное купание домывает кровь начисто.
        WoundMath.WashBloodSoil(npc, 1f);

        // §40.6: she came out of the water naked — walk back to the shore pile
        // and put the same clothes back on. Only falls through to a plain
        // finish when there is nothing left to reclaim.
        npc.Mind.PersonalCarePhase = PersonalCarePhase.Redress;
        if (TryBeginPostBatheRedress(world, npc, target))
        {
            return;
        }

        npc.Mind.PersonalCarePhase = PersonalCarePhase.None;
        npc.Mind.PersonalCareBathShore = null;
        FinishPersonalCare(world, npc, target, GoalType.Bathe, "Bathed");
    }

    // §40.6: after bathing, retarget the plan to walk back to the shore where
    // she left her clothes and re-dress. Returns false (caller finishes normally)
    // when the pile is gone/empty or the shore junction is unknown.
    private static bool TryBeginPostBatheRedress(WorldState world, NPCState npc, JunctionId batheJunction)
    {
        // Forget pieces that no longer exist (taken by someone, despawned).
        npc.Mind.RedressGarments.RemoveAll(id => !world.Entities.Objects.ContainsKey(id));
        if (npc.Mind.RedressGarments.Count == 0 || npc.Mind.RedressShore is not { } shore)
        {
            npc.Mind.RedressShore = null;
            npc.Mind.PersonalCarePhase = PersonalCarePhase.None;
            npc.Mind.PersonalCareBathShore = null;
            return false;
        }

        SpatialMutations.FreeJunction(world, batheJunction, npc.Id);
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = shore;
        npc.Plan.TargetTile = null;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = shore });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.RedressAfterBathe, TargetJunction = shore });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PostBatheRedress",
                $"Returning to {shore.Value} for {npc.Mind.RedressGarments.Count} garments");
        }
        return true;
    }

    // §40.6: back at the shore pile — play a short dressing beat, then put every
    // still-present remembered garment back on (the same clothes she took off).
    private static void RunRedressAfterBathe(WorldState world, NPCState npc, PlanStep step)
    {
        // A blocked search is only one of PathfindingSystem's four transient
        // attempts (an actor may be sealing a one-junction passage).  Do not
        // abort the redress on attempt one: that used to strand the bather in
        // deep water, clear her shore memory and leave Idle as the only bid.
        // PathfindingSystem owns the retry budget and aborts after attempt four;
        // until then the exact shore plan and garment ids remain intact.
        if (!npc.Movement.IsMoving &&
            npc.Movement.Status == MovementStatus.Blocked &&
            (npc.CurrentJunction is not { } atShore || step.TargetJunction is not { } wantShore ||
             !atShore.Equals(wantShore)))
        {
            return;
        }

        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } shore || !current.Equals(shore))
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Dress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            return;
        }

        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        var reworn = 0;
        foreach (var id in npc.Mind.RedressGarments)
        {
            if (!world.Entities.Objects.TryGetValue(id, out var garment) ||
                !world.Content.ObjectDefinitions.TryGetValue(garment.DefinitionId, out var def) ||
                def.Layer is null)
            {
                continue;
            }

            // She is naked out of the water, so there is normally no conflict;
            // ResolveWearConflicts stays as the belt-and-braces layer guard.
            ResolveWearConflicts(world, npc, garment.DefinitionId);
            npc.WornItems.Add(new ItemInstance(garment.DefinitionId)
            {
                Wetness = garment.Wetness,
                Durability = garment.Durability,
                Dirtiness = garment.Dirtiness,
                Bloodiness = garment.Bloodiness,
                // §133: она одевается обратно В СВОЮ одежду — владелец её же.
                OwnerId = ClothingOwnership.ResolveOnTake(world, npc, garment)
            });
            _dressPourScratch.Clear();
            _dressPourScratch.AddRange(garment.Contents);
            garment.Contents.Clear();
            WorldObjectMutations.DespawnObject(world, id);
            EquipmentMath.Recalculate(world, npc);
            foreach (var stashed in _dressPourScratch)
            {
                GiveOrDrop(world, npc, stashed);
            }

            StowDisplacedGarments(world, npc);
            reworn++;
        }

        npc.Mind.RedressGarments.Clear();
        npc.Mind.RedressShore = null;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.None;
        npc.Mind.PersonalCareBathShore = null;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PostBatheDressed",
                $"Re-donned {reworn} garments Warmth={npc.EquippedWarmth:F2}");
        }
        FinishPersonalCare(world, npc, shore, GoalType.Bathe, "Bathed");
    }

    private static int CountDirtyRedressGarments(WorldState world, NPCState npc)
    {
        var count = 0;
        foreach (var id in npc.Mind.RedressGarments)
        {
            if (world.Entities.Objects.TryGetValue(id, out var garment) &&
                MathUtil.Clamp01(garment.Dirtiness + garment.Bloodiness) > 0.001f)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>§40.6 r14: на этой вещи есть что отстирывать.</summary>
    private static bool IsLaundry(WorldObjectState garment) =>
        MathUtil.Clamp01(garment.Dirtiness + garment.Bloodiness) > 0.001f;

    /// <summary>
    /// §40.6 r14 (#147): нужна ли ей самой вода. «Грязна только одежда» — не
    /// повод раздеваться догола и лезть в море: игрок отдельно на этом
    /// настоял. Порог тот же, по которому купание вообще становится нуждой.
    /// </summary>
    private static bool WantsBodyBath(NPCState npc) =>
        1f - npc.Needs.Hygiene >=
            SimBalance.BatheNeedThreshold * TraitMath.GroomingThresholdMult(npc);

    /// <summary>§40.6 r14: начать такт стирки ОДНОЙ лежащей вещи.</summary>
    private static void StartGarmentWash(
        WorldState world, NPCState npc, WorldObjectState garment)
    {
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.WashClothes;
        npc.Execution.TargetObject = garment.Id;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + SimBalance.WashClothesDurationTicks;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "GarmentWashStarted",
                $"Object={garment.Id.Value} Def={garment.DefinitionId} " +
                $"Dirt={garment.Dirtiness:F2} Blood={garment.Bloodiness:F2}");
        }
    }

    /// <summary>
    /// §40.6 r14: один тик стирки одной вещи. true — такт закончен и вещь
    /// осталась лежать на берегу чистой; false — ещё стирает.
    /// </summary>
    private static bool TickGarmentWash(WorldState world, NPCState npc)
    {
        var target = npc.Execution.TargetObject;
        if (target is not { } id || !world.Entities.Objects.TryGetValue(id, out var garment))
        {
            // Вещь исчезла из-под рук (подобрали, смыло) — такт закрываем, но
            // сцену не роняем: остальная стирка её не касается.
            ClearWashBeat(npc);
            return true;
        }

        var completed = world.Tick >= npc.Execution.EndTick;
        var remainingTicks = System.Math.Max(0, npc.Execution.EndTick - world.Tick);
        var retained = completed ? 0f : remainingTicks / (remainingTicks + 1f);
        garment.Dirtiness = MathUtil.Clamp01(garment.Dirtiness * retained);
        garment.Bloodiness = MathUtil.Clamp01(garment.Bloodiness * retained);
        garment.Wetness = 1f;
        if (!completed)
        {
            return false;
        }

        // Тип события — константа: §63/TraceEmitLint запрещает собирать его из
        // данных, иначе множество типов становится бесконечным.
        Trace.Emit(world, npc.Id, "ClothesWashed",
            $"Object={garment.Id.Value} Def={garment.DefinitionId} " +
            $"Remaining={CountDirtyRedressGarments(world, npc)}");
        ClearWashBeat(npc);
        return true;
    }

    private static void ClearWashBeat(NPCState npc)
    {
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
    }

    // §40.6 r2 (laundry-in-hand): the piece is washed IN THE HAND, never in
    // place. A worn source plays the doff beat first (off the body into the
    // hand, warmth drops); a ground source is FETCHED (r4): leg 1 walks to a
    // stand beside the pile, the pick-up happens at arm's reach, and leg 2
    // carries it to the water edge — the old flow teleported the pile into
    // the hand from the edge, up to 2.2R away (spec §26.6A family). The held
    // instance loses dirt/blood over the wash window and is laid back down at
    // the edge fully clean and soaked.
    private static void RunWashClothes(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target)
        {
            return;
        }

        // §40.6 r4 leg 1: a ground source not yet in hand — she is walking to
        // (or standing at) the fetch spot, Plan.TargetJunctionId. Pick up at
        // reach, then re-point the plan at the edge for leg 2.
        if (step.TargetObject is { } fetchId && npc.Execution.HeldGarment is null &&
            npc.Execution.Status == ExecutionStatus.None)
        {
            if (npc.Plan.TargetJunctionId is not { } fetchLeg)
            {
                return;
            }

            if (!current.Equals(fetchLeg))
            {
                // Not there yet: PathfindingSystem routes leg 1. A Blocked
                // route waits SILENTLY (retried every tick; actor jams clear
                // as people move, and a real dead-end is bounded by need
                // preemption + the edge reservation expiring) — aborting here
                // turns transient blocks into a replan loop: soak seed 12345
                // showed 466 futile wash aborts per 40k ticks.
                return;
            }

            if (!world.Entities.Objects.TryGetValue(fetchId, out var pile))
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes garment disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            // Same belt-and-braces guarantee as the generic exec gate (§26.6A
            // r4): standing on the reserved fetch spot must actually put the
            // pile at arm's reach — close enough AND on this side of every
            // cliff face / hut wall, never snatched across one.
            if (!InteractionReach.CheckObjectStart(world, npc, pile,
                    world.Content.ObjectDefinitions.TryGetValue(pile.DefinitionId, out var pileDef)
                        ? pileDef.ObstacleRadius : 0f))
            {
                npc.Memory.Shun(pile.Id, world.Tick + AiBalance.ShunTicks);
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.WashClothes);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes garment not adjacently reachable");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            if (!TryPickGarmentIntoHand(world, npc, fetchId))
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes garment disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            if (!fetchLeg.Equals(target))
            {
                SpatialMutations.ReleaseJunctionReservation(world, fetchLeg, npc.Id);
            }

            npc.Plan.TargetJunctionId = target; // leg 2: carry to the edge
            return;
        }

        if (!current.Equals(target))
        {
            // Walking to the edge (worn source, or leg 2 with the pile in
            // hand). A Blocked route waits silently — see the leg-1 note; an
            // interrupt lays a held garment at her feet, so nothing is lost.
            return;
        }

        if (!world.Junctions.Items.TryGetValue(target, out var edge) ||
            !PlanningSystem.TryGetEdgeSeatGeometry(
                world, edge, waterOnly: true, out var standTile, out var facing))
        {
            SpatialMutations.FreeJunction(world, target, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes edge disappeared");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Hold the exact edge midpoint and its water-facing normal throughout
        // the gathering clip; other systems cannot slowly turn the washer away.
        PlaceAtEdge(world, npc, edge, standTile, facing);

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (!SpatialMutations.TryReserveJunction(world, target, npc.Id, world.Tick,
                    UndressDurationTicks + SimBalance.WashClothesDurationTicks + 8))
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            SpatialMutations.OccupyJunction(world, target, npc.Id);

            if (npc.Execution.HeldGarment is not null)
            {
                // §40.6 r4: the fetched pile already rides in the hand — scrub.
                StartWashBeat(world, npc);
                return;
            }

            // Worn source: the doff beat first — same two-beat undress window
            // the wardrobe verbs use, so the view shows her taking it off.
            if (npc.Plan.TargetItemDefinitionId is not { } wornId ||
                !npc.WornItems.Contains(wornId))
            {
                SpatialMutations.FreeJunction(world, target, npc.Id);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes worn piece disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            npc.Execution.HeldGarment = null;
            npc.Execution.HeldGarmentContents.Clear();
            return;
        }

        if (npc.Execution.CurrentInteraction == InteractionType.Undress)
        {
            var itemId = npc.Plan.TargetItemDefinitionId;
            var garment = npc.Execution.HeldGarment;
            if (garment is null && itemId is not null)
            {
                garment = npc.WornItems.Find(i => i.DefinitionId == itemId);
            }

            if (garment is null)
            {
                SpatialMutations.FreeJunction(world, target, npc.Id);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes worn piece disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)(world.Tick - npc.Execution.StartTick) / total : 1f;
            if (npc.Execution.HeldGarment is null && progress >= WardrobeHandoffFraction)
            {
                npc.WornItems.Remove(garment);
                npc.Execution.HeldGarment = garment;
                EquipmentMath.Recalculate(world, npc);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "GarmentInHand",
                        $"Wash {garment.DefinitionId} doffed to hand " +
                        $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                }
            }

            if (world.Tick < npc.Execution.EndTick)
            {
                return;
            }

            if (npc.Execution.HeldGarment is null)
            {
                npc.WornItems.Remove(garment);
                npc.Execution.HeldGarment = garment;
                EquipmentMath.Recalculate(world, npc);
            }

            StartWashBeat(world, npc);
            return;
        }

        if (npc.Execution.HeldGarment is not { } held)
        {
            SpatialMutations.FreeJunction(world, target, npc.Id);
            PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "WashClothes lost the held garment");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Dirtiness is the combined contamination score. Bloodiness remains a
        // separate visual layer, but fades over the same washing progress.
        var remainingTicks = System.Math.Max(0, npc.Execution.EndTick - world.Tick);
        var retainedContamination = remainingTicks / (remainingTicks + 1f);
        held.Dirtiness = MathUtil.Clamp01(held.Dirtiness * retainedContamination);
        held.Bloodiness = MathUtil.Clamp01(held.Bloodiness * retainedContamination);
        held.Wetness = 1f;
        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        held.Dirtiness = 0f;
        held.Bloodiness = 0f;
        held.Wetness = 1f;

        // §40.6: put the freshly-washed piece straight back ON — she is holding
        // it and (for the worn source) its slot is now empty, so she re-dresses
        // instead of dumping clean laundry on the sand. If the slot is taken
        // (she washed some OTHER ground garment while already dressed there),
        // fall back to laying it at her feet.
        SpatialMutations.FreeJunction(world, target, npc.Id);
        if (TryDonHeldGarment(world, npc, held, npc.Execution.HeldGarmentContents))
        {
            npc.Execution.HeldGarmentContents.Clear();
            npc.Execution.HeldGarment = null;
            FinishPersonalCare(world, npc, step.TargetJunction, GoalType.WashClothes,
                "ClothesWashed", $"{held.DefinitionId} (re-dressed)");
            return;
        }

        var laid = DropItemAtFeet(world, npc, held);
        if (laid != null && npc.Execution.HeldGarmentContents.Count > 0)
        {
            laid.Contents.AddRange(npc.Execution.HeldGarmentContents);
        }

        npc.Execution.HeldGarmentContents.Clear();
        npc.Execution.HeldGarment = null;
        // Jul 2026: the TYPE must stay the constant "ClothesWashed" — the
        // garment id used to be interpolated into it, so every wash produced
        // a unique event type that no whitelist/counter could match.
        FinishPersonalCare(world, npc, step.TargetJunction, GoalType.WashClothes,
            "ClothesWashed", $"{held.DefinitionId} (fully wet)");
    }

    // §40.6: don a garment held in hand IF its (layer, body-part) slot is free.
    // Returns false — leaving the piece in hand for the caller to drop — when
    // wearing it would force stripping a currently-worn garment (never strip
    // yourself to put on laundry). Pocket contents pour back into the pack.
    private static bool TryDonHeldGarment(WorldState world, NPCState npc, ItemInstance held,
        System.Collections.Generic.List<ItemInstance> contents)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(held.DefinitionId, out var def) ||
            def.Layer is null || HasWearConflict(world, npc, def))
        {
            return false;
        }

        npc.WornItems.Add(held);
        EquipmentMath.Recalculate(world, npc);
        if (contents != null)
        {
            foreach (var stashed in contents)
            {
                GiveOrDrop(world, npc, stashed);
            }
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ItemWorn",
                $"Def={held.DefinitionId} (re-dressed) Worn=[{string.Join(",", npc.WornItems)}] " +
                $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
        }
        return true;
    }

    // Would wearing newDef displace an already-worn piece? (same layer +
    // overlapping covered part). Non-destructive — unlike ResolveWearConflicts.
    private static bool HasWearConflict(WorldState world, NPCState npc,
        HexLive.Simulation.Content.ObjectDefinition newDef)
    {
        foreach (var wornId in npc.WornItems)
        {
            // §52.9: the SAME occupancy predicate ResolveWearConflicts uses.
            // This is what decides whether a freshly-washed piece goes back ON —
            // a false conflict here silently leaves clean laundry on the sand.
            if (world.Content.ObjectDefinitions.TryGetValue(wornId, out var wornDef) &&
                WearSlotCatalog.Occupies(newDef, wornDef))
            {
                return true;
            }
        }

        return false;
    }

    // §40.6 r2: lift a ground garment into the washer's hand — the world
    // object despawns for the duration; its pocket contents ride along in the
    // execution state and are restored when the piece is laid back down.
    private static bool TryPickGarmentIntoHand(WorldState world, NPCState npc, ObjectId objectId)
    {
        if (!world.Entities.Objects.TryGetValue(objectId, out var garment))
        {
            return false;
        }

        var held = new ItemInstance(garment.DefinitionId)
        {
            Wetness = garment.Wetness,
            Durability = garment.Durability,
            Dirtiness = garment.Dirtiness,
            Bloodiness = garment.Bloodiness,
            ResourceAmount = garment.ResourceAmount,
            // §133: вещь в руках не теряет хозяйку — иначе постирать чужое
            // значило бы его присвоить.
            OwnerId = ClothingOwnership.OwnerIdOf(garment)
        };
        npc.Execution.HeldGarmentContents.Clear();
        npc.Execution.HeldGarmentContents.AddRange(garment.Contents);
        WorldObjectMutations.DespawnObject(world, objectId);
        npc.Plan.TargetObjectId = null;
        npc.Execution.HeldGarment = held;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "GarmentInHand",
                $"Wash {held.DefinitionId} picked up (Dirt={held.Dirtiness:F2} Blood={held.Bloodiness:F2})");
        }
        return true;
    }

    private static void StartWashBeat(WorldState world, NPCState npc)
    {
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.WashClothes;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + SimBalance.WashClothesDurationTicks;
        if (npc.Execution.HeldGarment is { } held)
        {
            held.Wetness = 1f;
        }
    }

    private static void FinishPersonalCare(WorldState world, NPCState npc, JunctionId? junction,
        GoalType goal, string trace, string detail = null)
    {
        if (junction is { } occupied)
        {
            SpatialMutations.ReleaseJunctionReservation(world, occupied, npc.Id);
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.Cooldowns.Add(new GoalCooldown { Goal = goal, EndTick = world.Tick + AiBalance.FailureCooldownTicks });
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        Trace.Emit(world, npc.Id, trace,
            $"{(detail is null ? string.Empty : detail + " ")}Hygiene={npc.Needs.Hygiene:F2}");
    }

    // Spec 35.4: should the finished cool-off beat re-arm in place? Yes while she
    // is still hot AND no more-urgent need/threat has crossed its threshold —
    // reusing the sleep-interrupt thresholds so she leaves the shade exactly when
    // there's something better to do. Stops once cooled below the clear edge.
    private static bool ShouldKeepCooling(WorldState world, NPCState npc)
    {
        if (!Spec49.CoolRearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the dwell — the decision system
        // then takes over (she'll re-pick CoolOff only if still overheated and the
        // settle cooldown has lapsed).
        if (npc.Memory.Dangers.Count > 0 ||
            npc.Needs.Hunger >= SleepInterruptHunger ||
            npc.Needs.Thirst >= SleepInterruptThirst)
        {
            return false;
        }

        // Cooled enough: both the heat discomfort and the sun-exposure meter have
        // fallen below their clear edges (hysteresis vs the 0.35 entry).
        if (npc.Needs.ThermalDiscomfort < SimBalance.CoolOffClearThreshold &&
            npc.SunExposure < SimBalance.CoolOffSunClear)
        {
            return false;
        }

        return true;
    }
}

}
