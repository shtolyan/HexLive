using HexLive.Simulation.Core;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

// §121.6: авто-нужды ручного персонажа. Без активного приказа аукцион
// разрешает ей ровно две цели — еду и питьё, с теми же порогами и проверками
// доступности, что у ИИ. Намеренно НЕ переиспользует полный конвейер
// ScoreGoals: тот тянет 70 скоринг-блоков и побочные записи, а здесь выбор
// из четырёх кандидатов решается детерминированно остротой нужды.
public sealed partial class DecisionSystem
{
    private static readonly GoalType[] ManualLanesThirstFirst =
        { GoalType.Drink, GoalType.GetWater, GoalType.Eat, GoalType.GetFood };

    private static readonly GoalType[] ManualLanesHungerFirst =
        { GoalType.Eat, GoalType.GetFood, GoalType.Drink, GoalType.GetWater };

    private static void RunManualNeedsAuction(WorldState world, NPCState npc)
    {
        // 1. Sweep завершённой авто-цели (зеркало ManualOrderSystem.
        //    SweepFinishedOrder): план доигран или сорвался — цель снимается,
        //    она стоит и ждёт (или следующего голода, или приказа).
        if (NpcControlPolicy.IsAutoNeed(npc.Mind.CurrentGoal) &&
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

        // 3. Доступность — те же условия, что в большом аукционе (Eat/GetFood
        //    около строки 419, Drink/GetWater около 1033). Разница одна: Eat у
        //    ИИ не имеет порога голода (его балансируют другие ставки), у
        //    ручной других ставок нет — поэтому еда и питьё открываются теми
        //    же порогами, что их fetch-половины.
        var hungry = npc.Needs.Hunger >= SimBalance.GetFoodHungerThreshold;
        var thirsty = npc.Needs.Thirst >= AiBalance.DrinkThirstThreshold;
        if (!hungry && !thirsty)
        {
            return;
        }

        var hasFoodInInventory = npc.Inventory.FindFirstFood(world.Content) != null;
        var hasCoconutMeal = HasCoconutMeal(npc, world);
        var eatAvail = hungry && (hasFoodInInventory || hasCoconutMeal);
        var getFoodAvail = hungry && !hasFoodInInventory && !hasCoconutMeal &&
            (HasReachableFoodForCurrentTools(npc, world) || KnowsReachableProducer(npc, world));

        var hasBottleWater = HasBottleWater(npc);
        var hasCoconutWater = HasCoconutWater(npc, world);
        var collectorDrawSeen = FindDrawableCollector(npc, world) is not null;
        var drinkAvail = thirsty && (hasBottleWater || hasCoconutWater || collectorDrawSeen);
        var getWaterAvail = thirsty && !hasCoconutWater && !hasBottleWater &&
            (collectorDrawSeen ||
             (HasCoconutBlade(npc) &&
              (HasReachableDefinitionWorthCarrying(npc, world, ContentIds.Coconut) ||
               KnowsReachableProducer(npc, world))));

        // 4. Детерминированный выбор: острее нужда — max(Hunger, Thirst), при
        //    равенстве жажда первой (§64.9: жажда убивает быстрее). Кулдаун
        //    провала уважается — иначе PlanFailed молотил бы каждый тик.
        var lanes = npc.Needs.Thirst >= npc.Needs.Hunger
            ? ManualLanesThirstFirst
            : ManualLanesHungerFirst;
        foreach (var goal in lanes)
        {
            var available = goal switch
            {
                GoalType.Eat => eatAvail,
                GoalType.GetFood => getFoodAvail,
                GoalType.Drink => drinkAvail,
                _ => getWaterAvail,
            };
            if (!available || IsOnCooldown(npc, goal, world.Tick))
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
