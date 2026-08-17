using HexLive.Simulation.Core;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §121: два вопроса, которые боевые системы задают про ручного персонажа, и
// один поиск зверя. Вынесены в отдельное место, потому что спрашивают их в
// восьми файлах, и разъехавшийся ответ читался бы как «иногда правило Кенши
// работает, иногда нет».
internal static class ManualControlMath
{
    /// <summary>Ею управляет игрок?</summary>
    public static bool IsManual(NPCState npc) =>
        Spec121.ManualControlEnabled && npc.Mind.ManualControl;

    /// <summary>
    /// У неё есть невыполненный приказ: план активен или взаимодействие идёт.
    /// Это гейт ЗАНЯТОСТИ — его читают авто-нужды §121.6 и таймаут простоя
    /// §121.7. Боевую самозащиту он НЕ отменяет: «правило Кенши» отменено
    /// §121.2 — атакованная ручная бросает приказ и дерётся (причины
    /// самозащиты проходят через <c>InterruptionCauses.AlwaysAllowed</c>).
    /// </summary>
    public static bool HasActiveOrder(NPCState npc) =>
        npc.Plan.Status == PlanStatus.Active ||
        npc.Execution.Status == ExecutionStatus.InProgress;

    /// <summary>Ручная И при исполнении — то есть неприкосновенная для систем,
    /// раздающих боевые цели.</summary>
    public static bool IsOrderedManual(NPCState npc) => IsManual(npc) && HasActiveOrder(npc);

    /// <summary>§121.7: renew the inactivity lease against monotonic wall time.</summary>
    public static void RenewInactivityLease(WorldState world, NPCState npc) =>
        npc.Mind.ManualControlLeaseRenewedAtSeconds = world.RuntimeClock.RealtimeSeconds;

    /// <summary>
    /// §121.7: a loaded/manual NPC has no process-relative timestamp, so its
    /// first idle pass starts a fresh lease instead of expiring immediately.
    /// </summary>
    public static bool InactivityLeaseExpired(WorldState world, NPCState npc)
    {
        var now = world.RuntimeClock.RealtimeSeconds;
        if (npc.Mind.ManualControlLeaseRenewedAtSeconds is not { } renewedAt)
        {
            npc.Mind.ManualControlLeaseRenewedAtSeconds = now;
            return false;
        }

        return now - renewedAt >= Spec121.ManualIdleReleaseSeconds;
    }

    public static void ClearInactivityLease(NPCState npc) =>
        npc.Mind.ManualControlLeaseRenewedAtSeconds = null;

    /// <summary>Зверь по номеру. Мобы лежат списком, а не словарём.</summary>
    public static bool TryGetMob(WorldState world, int mobId, out Wildlife.MobState mob)
    {
        foreach (var candidate in world.Mobs)
        {
            if (candidate.Id == mobId && candidate.Health > 0f)
            {
                mob = candidate;
                return true;
            }
        }

        mob = null;
        return false;
    }
}

}
