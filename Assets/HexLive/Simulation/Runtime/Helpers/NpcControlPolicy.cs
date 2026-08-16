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
    /// <summary>§121.1/§123: цель пришла из очереди приказов — её ставит
    /// игрок (или контур управления поверх той же очереди), и ни один
    /// автоматический гейт её не трогает.</summary>
    public static bool IsPlayerGoal(GoalType goal) =>
        goal is GoalType.PlayerOrder or GoalType.PlayerAttack
             or GoalType.PlayerInventory;

    /// <summary>⭐ §121.6 r2 — ВЕСЬ белый список самообслуживания ручной:
    /// съесть и выпить СВОЁ, на месте. `GetFood`/`GetWater` здесь нет
    /// намеренно: это добыча в мире (кокос, водосборник, продюсер), то есть
    /// ровно та автономия, ради выключения которой существует §121.</summary>
    public static bool IsManualInventoryNeed(GoalType goal) =>
        goal is GoalType.Eat or GoalType.Drink;

    /// <summary>§121.6: какие цели аукцион смеет назначить. Не-ручной —
    /// любые; ручной без приказа — только еду и питьё из рюкзака.</summary>
    public static bool MayAuctionGoal(NPCState npc, GoalType goal) =>
        !ManualControlMath.IsManual(npc) || IsManualInventoryNeed(goal);

    /// <summary>§121.6: планировщик строит планы ручному только для
    /// авто-нужд; планами приказов владеют ManualCommandExecutor и
    /// ManualOrderSystem.</summary>
    public static bool MayPlanGoal(NPCState npc, GoalType goal) =>
        !ManualControlMath.IsManual(npc) || IsManualInventoryNeed(goal);

    /// <summary>⭐ §121.6 r2: план авто-нужды ручной обязан быть планом «из
    /// своего рюкзака». Один предикат вместо флага в каждой ветке: ветки
    /// добычи (вертел, земля, водосборник, поход за помощью) спрашивают
    /// его, а не выясняют режим сами.</summary>
    public static bool RequiresInventoryOnlySelfCare(NPCState npc) =>
        ManualControlMath.IsManual(npc);

    /// <summary>⭐ §121.6 r2, fail-closed: цель, которую ручная имеет право
    /// ДОНОСИТЬ. Всё, что не приказ, не еда/питьё из рюкзака и не `None`, —
    /// протухшая чужая цель. Планировщик для ручной выключен и её не
    /// починит, поэтому такую цель снимает сам ручной проход.</summary>
    public static bool MayRetainGoalWhileManual(NPCState npc, GoalType goal) =>
        !ManualControlMath.IsManual(npc) ||
        goal == GoalType.None ||
        IsPlayerGoal(goal) ||
        IsManualInventoryNeed(goal);

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
