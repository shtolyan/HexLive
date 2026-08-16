using HexLive.Simulation.Core;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

// §121.6 r2: авто-нужды ручного персонажа. Без активного приказа аукцион
// разрешает ей ровно две цели — съесть и выпить СВОЁ, на месте. Ни одной
// цели добычи (`GetFood`/`GetWater`) здесь больше нет и ни одного запроса к
// миру не делается: пустой рюкзак означает «стоит и ждёт приказа», а не
// «пошла за кокосом». Намеренно НЕ переиспользует полный конвейер
// ScoreGoals: тот тянет 70 скоринг-блоков и побочные записи, а здесь выбор
// из двух кандидатов решается детерминированно остротой нужды.
public sealed partial class DecisionSystem
{
    private static readonly GoalType[] ManualLanesThirstFirst =
        { GoalType.Drink, GoalType.Eat };

    private static readonly GoalType[] ManualLanesHungerFirst =
        { GoalType.Eat, GoalType.Drink };

    private static void RunManualNeedsAuction(WorldState world, NPCState npc)
    {
        // 0. ⭐ Fail-closed (§121.6 r2). Цель, которой ручной иметь не
        //    положено, снимается НЕМЕДЛЕННО: её мог оставить только
        //    «забытый иф» в чужой системе или сейв, сделанный по прежним
        //    правилам, а планировщик для ручной выключен и её не починит —
        //    колонистка встала бы столбом с целью, за которую никто не
        //    отвечает. Причина проходит у ручной всегда (§121.5).
        if (!NpcControlPolicy.MayRetainGoalWhileManual(npc, npc.Mind.CurrentGoal))
        {
            var forbidden = npc.Mind.CurrentGoal;
            PlanInterruption.TryAbort(world, npc, InterruptionCause.ManualPolicySweep,
                $"ManualForbiddenGoal={forbidden}");
            npc.Mind.CurrentGoal = GoalType.None;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ManualForbiddenGoalDropped",
                    $"Goal={forbidden}");
            }
        }

        // 1. Sweep завершённой авто-цели (зеркало ManualOrderSystem.
        //    SweepFinishedOrder): план доигран или сорвался — цель снимается,
        //    она стоит и ждёт (или следующего голода, или приказа).
        if (NpcControlPolicy.IsManualInventoryNeed(npc.Mind.CurrentGoal) &&
            npc.Plan.Status != PlanStatus.Active &&
            npc.Execution.Status != ExecutionStatus.InProgress)
        {
            var outcome = npc.Plan.Status == PlanStatus.Completed ? "Completed" : "Failed";
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Plan.Status = PlanStatus.None;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ManualAutoNeedFinished", $"Outcome={outcome}");
            }
        }

        // 2. Гейт занятости: занята приказом, авто-целью, боем или только
        //    проснулась — авто-нужды молчат. Бой держит гейт так же, как у ИИ:
        //    голод/жажда на пороге смерти пробивают его (besieged-starve).
        if (npc.Mind.CurrentGoal != GoalType.None ||
            npc.Mind.ManualAttackNpcId is not null ||
            npc.Mind.ManualAttackMobId is not null ||
            ManualControlMath.HasActiveOrder(npc) ||
            world.Tick < npc.Mind.WakeGraceUntilTick ||
            (npc.IsFighting && !npc.Mind.IsStarving && !npc.Mind.IsDehydrated))
        {
            return;
        }

        // 3. Пороги — те же, что у ИИ. Доступность — СТРОГО инвентарная:
        //    ⭐ ни `npc.Perception`, ни памяти о продюсерах, ни водосборников.
        //    Это и есть весь слайс: голод не даёт ручной права уйти с места.
        var hungry = npc.Needs.Hunger >= SimBalance.GetFoodHungerThreshold;
        var thirsty = npc.Needs.Thirst >= AiBalance.DrinkThirstThreshold;
        if (!hungry && !thirsty)
        {
            return;
        }

        // Кокос в рюкзаке — законная еда: вскрыть его она может там, где
        // стоит (BuildCoconutInventoryPlan). Кокос на земле или на пальме —
        // уже поход, и его здесь нет.
        var eatAvail = hungry &&
            (npc.Inventory.FindFirstFood(world.Content) is not null ||
             HasInventoryCoconutMeal(npc));

        // HasInventoryCoconutWater покрывает и полную флягу, и продырявленный
        // кокос с водой; FindFirstDrink — всё прочее питьевое в рюкзаке.
        var drinkAvail = thirsty &&
            (HasInventoryCoconutWater(npc) ||
             npc.Inventory.FindFirstDrink(world.Content) is not null);

        // 4. Детерминированный выбор: острее нужда — max(Hunger, Thirst), при
        //    равенстве жажда первой (§64.9: жажда убивает быстрее). Кулдаун
        //    провала уважается — иначе PlanFailed молотил бы каждый тик.
        var lanes = npc.Needs.Thirst >= npc.Needs.Hunger
            ? ManualLanesThirstFirst
            : ManualLanesHungerFirst;
        foreach (var goal in lanes)
        {
            var available = goal == GoalType.Eat ? eatAvail : drinkAvail;
            if (!available ||
                !NpcControlPolicy.MayAuctionGoal(npc, goal) ||
                IsOnCooldown(npc, goal, world.Tick))
            {
                continue;
            }

            npc.Mind.CurrentGoal = goal;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ManualAutoNeed",
                    $"Goal={goal} Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2}");
            }

            return;
        }
    }
}

}
