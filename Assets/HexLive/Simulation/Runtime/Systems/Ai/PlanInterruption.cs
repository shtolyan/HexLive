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

// Spec 23.17 interrupt semantics: abort an active plan cleanly, releasing
// everything the plan owns (object occupancy, junction occupancy/reservation)
// so the next decision pass can replan without leaks.
//
// §121.5: с введением политики управления снос плана обязан назвать причину.
// Публичны только TryAbort*-входы; охрана NpcControlPolicy решает, проходит
// ли причина у ручного персонажа. Вернувшийся false означает «план жив» —
// вызывающий обязан не трогать ни цель, ни IsFighting, ни сцепку жертвы.
public static class PlanInterruption
{
    public static bool TryAbort(
        WorldState world, NPCState npc, InterruptionCause cause, string reason)
    {
        if (!AllowedBy(world, npc, cause, reason)) return false;
        ReportInterruptedOrder(world, npc, cause);
        Abort(world, npc, reason);
        // A hostile crossing the route or a scene taking ownership is not a
        // failed attempt at the route's target.  Retaining it made five valid
        // external reroutes look like a Sisyphus target and the escape ladder
        // then shunned the unrelated wardrobe/fire/patient.
        if (cause is InterruptionCause.ThreatReroute or
            InterruptionCause.SceneInitiator or InterruptionCause.ScenePact)
        {
            world.IntentLedger.Forget(npc.Id.Value);
        }
        return true;
    }

    /// <summary>§124: новый приказ движения/остановки не роняет тело из рук.
    /// Все владения старого плана освобождаются, но двусторонняя carry-ссылка
    /// остаётся до явного PutDown или опасного прерывания.</summary>
    public static bool TryAbortKeepingCarriedPerson(
        WorldState world, NPCState npc, InterruptionCause cause, string reason)
    {
        if (!AllowedBy(world, npc, cause, reason)) return false;
        ReportInterruptedOrder(world, npc, cause);
        AbortKeepingCarriedPerson(world, npc, reason);
        return true;
    }

    // §118.4: combat is a temporary interruption, not permission to keep a
    // patient glued to the fighter or to forget her. Put her down before any
    // swing can start, reserve that same patient, then let RescueSystem resume
    // the evacuation once the fight/scene releases the rescuer.
    public static bool TryAbortForCombat(
        WorldState world, NPCState npc, InterruptionCause cause, string reason)
    {
        if (!AllowedBy(world, npc, cause, reason)) return false;
        ReportInterruptedOrder(world, npc, cause);
        AbortForCombat(world, npc, reason);
        return true;
    }

    // §121.5: снос ПРИНЯТОГО приказа обязан быть виден. Отказ в момент клика
    // давно тостится (ManualOrderRejected), а приказ, убитый позже — боем,
    // провалом пути, исчезнувшей целью, — гас в debug-трассе, и игрок читал
    // «стоит и не идёт» как поломку. Телесные причины не тостятся: падение
    // тела видно и так; PlayerCommand/ControlReleased — сам игрок.
    private static void ReportInterruptedOrder(
        WorldState world, NPCState npc, InterruptionCause cause)
    {
        if (!ManualControlMath.IsManual(npc) ||
            // Rescue — только ручной §124.1 (гейт IsManual выше отсекает ИИ).
            npc.Plan.Goal is not (GoalType.PlayerOrder or GoalType.PlayerInventory
                or GoalType.Rescue) ||
            (npc.Plan.Status != PlanStatus.Active &&
             npc.Execution.Status != ExecutionStatus.InProgress))
        {
            return;
        }

        switch (cause)
        {
            case InterruptionCause.PlayerCommand:
            case InterruptionCause.ControlReleased:
            case InterruptionCause.Death:
            case InterruptionCause.BodyComa:
            case InterruptionCause.Faint:
            case InterruptionCause.Crying:
            case InterruptionCause.Dying:
            case InterruptionCause.PlayDead:
            case InterruptionCause.LimbLost:
                return;
        }

        Trace.Emit(world, npc.Id, "ManualOrderInterrupted", $"Cause={cause}");
    }

    // §121.5: путь не-ручного персонажа обязан быть байт-в-байт прежним —
    // здесь два булевых чтения и ни одной трассы на разрешённом пути.
    private static bool AllowedBy(
        WorldState world, NPCState npc, InterruptionCause cause, string reason)
    {
        if (NpcControlPolicy.MayInterruptPlan(npc, cause)) return true;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InterruptDenied", $"Cause={cause} {reason}");
        }

        return false;
    }

    private static void Abort(WorldState world, NPCState npc, string reason)
    {
        CancelInterruptedRescue(world, npc);
        AbortCore(world, npc, reason, keepCarriedPerson: false);
    }

    private static void AbortKeepingCarriedPerson(WorldState world, NPCState npc, string reason)
    {
        CancelInterruptedRescue(world, npc);
        AbortCore(world, npc, reason, keepCarriedPerson: true);
    }

    private static void AbortForCombat(WorldState world, NPCState npc, string reason)
    {
        var remembered = npc.Mind.InterruptedRescuePatientId;
        var dropped = AbortCore(world, npc, reason, keepCarriedPerson: false);
        // A fight is an external interruption, not evidence that the previous
        // target is a Sisyphus loop. Keeping pre-combat Aid/Gather attempts in
        // the ledger made two legitimate friend-guard reactions trip the loop
        // ladder and impose a 900-tick cooldown on medical help.
        world.IntentLedger.Forget(npc.Id.Value);
        var resumePatientId = dropped ?? remembered;
        if (resumePatientId is not { } patientId ||
            !world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
            !FactionRelations.AreAllies(npc, patient) ||
            !KenshiRescueMath.NeedsRescue(world, patient))
        {
            CancelInterruptedRescue(world, npc);
            return;
        }

        npc.Mind.InterruptedRescuePatientId = patientId;
        patient.Mind.PendingAidFrom = npc.Id;
        patient.Mind.PendingAidSinceTick = world.Tick;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "RescuePausedForCombat",
                $"NPC{patientId.Value} Reason={reason}");
        }
    }

    private static EntityId? AbortCore(
        WorldState world, NPCState npc, string reason, bool keepCarriedPerson)
    {
        // §138/§121.7: an interrupted long manual craft is still player
        // activity. Restart the idle-release window before the plan fields are
        // cleared, otherwise a prosthetic order older than five minutes would
        // drop straight back to AI on the interruption tick.
        if (npc.Mind.ManualControl &&
            Content.RecipeCatalog.IsItemOutputGoal(npc.Plan.Goal))
        {
            npc.Mind.LastManualInputTick = world.Tick;
        }

        var droppedPatientId = keepCarriedPerson
            ? KenshiRescueMath.DetachRescueDestinationForManualCarry(world, npc)
            : KenshiRescueMath.PutDownForPlanInterruption(
                world, npc, $"Plan interrupted: {reason}");

        // A bed pose is not just an animation flag: TryEnter moved the body to
        // the furniture centre while retaining a legal wake junction. Clearing
        // Sleep without using that junction leaves a standing actor inside the
        // bed until some later movement happens to pull her out.
        LyingSpot.ReleaseRestSurfaceOnRise(world, npc);
        CraftProjectMath.ReleaseWorker(world, npc);
        ExecutionSystem.ReleaseClaims(world, npc);
        // §111.13: место у лежащего освобождается там же, где освобождается всё
        // остальное занятое планом. Предикат живости — страховка, а не замена
        // этой строки: сорванный поход не должен занимать станцию до тех пор,
        // пока кто-то не заметит, что заявка протухла.
        LyingStations.ReleaseStation(npc);
        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Plan.TargetObjectId is { } objId &&
            world.Entities.Objects.TryGetValue(objId, out var worldObject) &&
            worldObject.CurrentUser == npc.Id)
        {
            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
        }

        // §84: the craft layout claims its pieces (ground-claimed fiber and
        // pack-laid inputs alike are marked occupied against mid-work theft) —
        // an aborted craft leaves them LYING, takeable by anyone again.
        foreach (var laidId in npc.Execution.CraftLayout)
        {
            if (world.Entities.Objects.TryGetValue(laidId, out var laidPiece) &&
                laidPiece.CurrentUser == npc.Id)
            {
                laidPiece.IsOccupied = false;
                laidPiece.CurrentUser = null;
            }
        }

        // Release a talk invitation this plan placed on its target (spec 28.8).
        if (npc.Plan.TargetAgentId is { } invitedId &&
            world.Entities.Npcs.TryGetValue(invitedId, out var invited) &&
            invited.Mind.PendingTalkFrom is { } inviter && inviter.Equals(npc.Id))
        {
            invited.Mind.PendingTalkFrom = null;
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        // §40.6 r4: multi-leg plans hold reservations beyond the current walk
        // target (the wash edge stays reserved while she fetches the pile) —
        // release everything the remaining steps point at, owner-guarded.
        foreach (var planStep in npc.Plan.Steps)
        {
            if (planStep.TargetJunction is { } stepJunction)
            {
                SpatialMutations.ReleaseJunctionReservation(world, stepJunction, npc.Id);
            }
        }

        // A garment mid-carry (doffed for an undress, or picked up for a wash)
        // must not vanish with the plan — lay it at her feet, pockets intact.
        if (npc.Execution.HeldGarment is { } held)
        {
            // §123 player inventory animation keeps the authoritative item in
            // its source list until the final tick so a mid-action save is
            // self-contained. Abort must therefore not duplicate that visual
            // hand reference onto the ground.
            var stillOwned = npc.Mind.CurrentGoal == GoalType.PlayerInventory &&
                (npc.Inventory.Items.Exists(item => ReferenceEquals(item, held)) ||
                 npc.WornItems.Exists(item => ReferenceEquals(item, held)));
            var dropped = stillOwned ? null : ExecutionSystem.DropItemAtFeet(world, npc, held);
            if (dropped != null && npc.Execution.HeldGarmentContents.Count > 0)
            {
                dropped.Contents.AddRange(npc.Execution.HeldGarmentContents);
            }

            npc.Execution.HeldGarment = null;
        }

        npc.Execution.HeldGarmentContents.Clear();

        // §137: отдых сидя обрывают И ОТСЮДА — например, ThreatAlertSystem,
        // когда в поле зрения появился зверь. Взять с собой надо ровно одно:
        // колдаун, чтобы она не плюхнулась обратно на первом же тике, когда
        // повод встать исчезнет.
        //
        // ⭐ Грация подъёма здесь НЕ выдаётся, и это осознанно. Через Abort
        // проходит срочное — бой, испуг, приказ, — а держать тело на месте
        // четыре с половиной секунды ради красивого клипа значит скормить её
        // волку. Вид от этого не ломается: у всех трёх состояний отдыха есть
        // клапан «Speed > 0.1 → Idle», так что пошедшая просто встаёт рывком —
        // это и есть подскочить. Спокойный выход живёт в FinishIdleRest, и
        // грацию выдаёт он.
        if (npc.Execution.CurrentInteraction == InteractionType.Rest)
        {
            npc.Mind.RestCooldownUntilTick = world.Tick + Spec137.CooldownTicks;
            npc.Mind.RestRearmCount = 0;
        }

        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        npc.Plan.Status = PlanStatus.Invalid;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Plan.RunRequested = false;

        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Idle);
        npc.Movement.StopReason = reason;
        npc.Movement.ClimbPauseTimer = 0f;
        npc.Movement.HopArmed = false;
        npc.Movement.HopPathIndex = -1;

        // §21.21B v17: a hop ALREADY IN THE AIR is not interruptible. Killing the
        // window here left her hanging between two levels — position half-way,
        // npc.Tile still the takeoff tile — and since the view draws her at the
        // ground height of npc.Tile, she snapped back onto the ledge she had just
        // jumped off ("спрыгнула, развернулась — телепнуло наверх") or back onto
        // the bank after a dive ("прыгнула в воду без плюха, отшвырнуло назад").
        // The window keeps running (MovementSystem.RunHopWindow is called before
        // any path check), lands her properly, and the new plan starts from solid
        // ground. Only the pre-flight commitment is cancelled, above.
        if (npc.Movement.HopTimer <= 0f)
        {
            npc.Movement.HopCrossed = false;
            npc.Movement.HopLandingIndex = 0;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "GoalInterrupted", reason);

        }
        return droppedPatientId;
    }

    private static void CancelInterruptedRescue(WorldState world, NPCState npc)
    {
        if (npc.Mind.InterruptedRescuePatientId is { } patientId &&
            world.Entities.Npcs.TryGetValue(patientId, out var patient) &&
            patient.Mind.PendingAidFrom == npc.Id)
        {
            patient.Mind.PendingAidFrom = null;
        }

        npc.Mind.InterruptedRescuePatientId = null;
    }
}

}
