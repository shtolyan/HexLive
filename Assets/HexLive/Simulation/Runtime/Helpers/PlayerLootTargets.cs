using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §128 r2 (баг #164): кого игрок вправе обыскать вручную.
/// <para>
/// Один предикат на всю ручную ветку обмена: его спрашивает и приём приказа
/// (<c>ManualCommandExecutor.ApplyTransferInventory</c>), и каждый тик его
/// исполнения (<c>ExecutionSystem.RunPlayerInventoryTransfer</c>). Раньше это
/// были две копии одного условия в разных файлах — ровно то место, где правка
/// «разрешили спящих» уехала бы в половину пути.
/// </para>
/// <para>
/// Правило игрока: лежит — можно обыскать. Мёртвая, спящая, без сознания —
/// все три случая. Это НЕ то же самое, что §111 (там ИИ сам решает обобрать
/// беспомощного врага и спящих не трогает принципиально): здесь решение
/// принимает человек за пультом, и запрет ему ничего не охраняет.
/// </para>
/// </summary>
internal static class PlayerLootTargets
{
    /// <summary>Найти цель обыска среди живых и среди тел.</summary>
    public static bool TryResolve(
        WorldState world, NPCState looter, EntityId otherId,
        out NPCState other, out bool carriedBySelf)
    {
        other = null;
        carriedBySelf = false;
        if (looter is null || otherId.Equals(looter.Id))
        {
            return false;
        }

        // Тело живёт в отдельном реестре (см. EntityRepository.Corpses) и его
        // никто не тикает — но карманы и надетое остаются при нём, пока
        // CorpseSystem не переложит их в remains.human.
        var dead = false;
        if (!world.Entities.Npcs.TryGetValue(otherId, out other))
        {
            if (!world.Entities.Corpses.TryGetValue(otherId, out other))
            {
                return false;
            }

            dead = true;
        }

        dead = dead || other.Health <= 0f;
        if (!dead && !IsLyingHelpless(world, other))
        {
            return false;
        }

        // §128: несомый — цель, только если он на руках у САМОГО обыскивающего
        // («взял — обыскал»); на чужих руках — по-прежнему нет.
        if (other.IsBeingCarried)
        {
            carriedBySelf = other.CarriedByNpcId?.Equals(looter.Id) ?? false;
            if (!carriedBySelf)
            {
                other = null;
                return false;
            }
        }

        if (CombatMedium.IsNpcSwimming(world, other))
        {
            other = null;
            carriedBySelf = false;
            return false;
        }

        return true;
    }

    /// <summary>Лежит и не ответит: без сознания, при смерти или спит.</summary>
    public static bool IsLyingHelpless(WorldState world, NPCState npc) =>
        npc.IsUnconscious(world.Tick) ||
        npc.IsDying ||
        npc.Execution.CurrentInteraction == InteractionType.Sleep;
}

}
