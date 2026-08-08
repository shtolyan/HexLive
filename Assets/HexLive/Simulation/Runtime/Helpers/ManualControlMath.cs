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
    /// ⭐ ПРАВИЛО КЕНШИ. У неё сейчас есть невыполненный приказ — значит,
    /// самозащита отменяется: она идёт и терпит удары. Именно так игрок
    /// выводит раненую из драки, и именно это отличает «стоит без дела»
    /// (отвечает на удары штатным §109) от «выполняет приказ».
    /// </summary>
    public static bool HasActiveOrder(NPCState npc) =>
        npc.Plan.Status == PlanStatus.Active ||
        npc.Execution.Status == ExecutionStatus.InProgress;

    /// <summary>Ручная И при исполнении — то есть неприкосновенная для систем,
    /// раздающих боевые цели.</summary>
    public static bool IsOrderedManual(NPCState npc) => IsManual(npc) && HasActiveOrder(npc);

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
