using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §121.5: централизованная таблица разрешений ручного режима — вместо
// рассыпанных по системам проверок IsManual, которые уже породили класс
// багов «забытый иф». Чистая статика: только чтения NPCState, ни одной
// float-операции, ни одной записи состояния. Для НЕ-ручного персонажа
// каждый метод обязан возвращать ровно прежнее поведение — golden trace
// мира без ручных бит-в-бит.
internal static class NpcControlPolicy
{
    /// <summary>§121.6: какие цели аукцион смеет назначить. Не-ручной —
    /// любые; ручной без приказа — только еду и питьё.</summary>
    public static bool MayAuctionGoal(NPCState npc, GoalType goal) =>
        !ManualControlMath.IsManual(npc) || IsAutoNeed(goal);

    /// <summary>§121.6: планировщик строит планы ручному только для
    /// авто-нужд; планами приказов владеют ManualCommandExecutor и
    /// ManualOrderSystem.</summary>
    public static bool MayPlanGoal(NPCState npc, GoalType goal) =>
        !ManualControlMath.IsManual(npc) || IsAutoNeed(goal);

    /// <summary>Белый список §121.6 — еда и питьё, больше ничего.</summary>
    public static bool IsAutoNeed(GoalType goal) =>
        goal is GoalType.Eat or GoalType.Drink
             or GoalType.GetFood or GoalType.GetWater;

    /// <summary>§121.5: охрана точки прерывания. Тело, механика плана,
    /// игрок и самозащита проходят у всех; «выбор» — только у не-ручных.
    /// Fail-closed: новая система, забывшая про ручной режим, не сможет
    /// снести приказ.</summary>
    public static bool MayInterruptPlan(NPCState npc, InterruptionCause cause) =>
        InterruptionCauses.AlwaysAllowed(cause) || !ManualControlMath.IsManual(npc);

    /// <summary>§121.2: побег — решение, у ручной его принимает игрок.</summary>
    public static bool MayFlee(NPCState npc) => !ManualControlMath.IsManual(npc);
}

}
