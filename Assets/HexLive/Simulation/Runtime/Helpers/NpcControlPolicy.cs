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

    /// <summary>
    /// ⭐ §138: ЗАКАЗАННЫЙ КРАФТ — тоже приказ игрока, просто выраженный не
    /// через <c>GoalType.Player*</c>, а через саму цель рецепта: «свари мясо»
    /// приезжает как <c>CookMeat</c>. Набор берётся из каталога рецептов, а не
    /// перечисляется руками, потому что это ровно те цели, которые принимает
    /// <c>CraftItemCommand</c>: новый рецепт не должен требовать правки этого
    /// списка. Автономии это не открывает — назначать такую цель ручной
    /// по-прежнему нельзя (см. <see cref="MayAuctionGoal"/> и
    /// <see cref="MayPlanGoal"/>), поэтому у ручной она может появиться только
    /// из принятой команды.
    /// </summary>
    public static bool IsManualCraftGoal(GoalType goal) =>
        Content.RecipeCatalog.ByGoal.ContainsKey(goal);

    /// <summary>
    /// ⭐ §121.9: социальные и само-приказы носят РОДНУЮ цель (как крафт §138) —
    /// «поговори с ней» приезжает как <c>Socialize</c>, «перевяжи себя» как
    /// <c>TreatWounds</c>, — чтобы их исполнял штатный executor байт в байт.
    /// Автономии это не открывает: назначить такую цель ручной по-прежнему
    /// нельзя (<see cref="MayAuctionGoal"/>/<see cref="MayPlanGoal"/>), она
    /// появляется только из принятой команды ManualCommandExecutor.
    /// </summary>
    public static bool IsManualSocialOrSelfGoal(GoalType goal) =>
        goal is GoalType.Socialize or GoalType.Romance or GoalType.Aid
             or GoalType.Splint or GoalType.FitProsthetic
             or GoalType.TreatWounds
             or GoalType.Sleep or GoalType.Sit
             or GoalType.Bathe or GoalType.WashClothes
             or GoalType.Homeward or GoalType.Explore ||
        // §121.9: «тёмные» приказы — за своим выключателем. Выключили — цель
        // немедленно перестаёт быть законной, и sweep честно её снимает.
        (Spec121.ManualDarkOrdersEnabled &&
         goal is GoalType.Prey or GoalType.Abuse);

    /// <summary>§121.5/§121.9: цель, стоящая за ПРИНЯТЫМ приказом игрока —
    /// в любой из трёх форм (Player*, заказанный крафт, социальное/само-действие).
    /// Это фильтр видимости: снос такой цели обязан дойти до игрока тостом.</summary>
    public static bool IsManualOrderGoal(GoalType goal) =>
        IsPlayerGoal(goal) ||
        IsManualCraftGoal(goal) ||
        IsManualSocialOrSelfGoal(goal);

    /// <summary>⭐ §121.6 r2, fail-closed: цель, которую ручная имеет право
    /// ДОНОСИТЬ. Всё, что не приказ, не заказанный крафт, не еда/питьё из
    /// рюкзака и не `None`, — протухшая чужая цель. Планировщик для ручной
    /// выключен и её не починит, поэтому такую цель снимает сам ручной проход.
    /// <para>
    /// ⚠️ Крафт попал сюда ценой сломанных тестов: §121.6 r2 и §138 приехали
    /// в мастер разными ветками, и список «что ручной положено» разошёлся с
    /// тем, что ручной УЖЕ умеет заказывать. Приказ принимался, а через
    /// мгновение в том же тике его сносил этот самый sweep — с честной
    /// трассой `ManualForbiddenGoalDropped` и полным молчанием в игре. Любая
    /// новая ручная способность обязана появиться и здесь.
    /// </para></summary>
    public static bool MayRetainGoalWhileManual(NPCState npc, GoalType goal) =>
        !ManualControlMath.IsManual(npc) ||
        goal == GoalType.None ||
        IsPlayerGoal(goal) ||
        IsManualCraftGoal(goal) ||
        IsManualSocialOrSelfGoal(goal) ||
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
