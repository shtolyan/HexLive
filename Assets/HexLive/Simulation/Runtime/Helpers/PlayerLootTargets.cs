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
        out NPCState other, out bool carriedBySelf) =>
        TryResolve(world, looter, otherId, InventoryTransferDirection.Take,
            out other, out carriedBySelf);

    /// <summary>
    /// §153.1: НАПРАВЛЕНИЕ решает, кто вообще является целью. Забрать можно
    /// только у лежащего (правило §128 не меняется), а ОТДАТЬ — любому живому
    /// человеку, который в сознании и может принять вещь из рук. Подарок стоит
    /// здесь, а не отдельным предикатом, ровно по той же причине, по которой
    /// §128 свёл два условия в одно: приём приказа и каждый тик его исполнения
    /// обязаны спрашивать ОДИН код, иначе следующая правка доедет до половины.
    /// </summary>
    public static bool TryResolve(
        WorldState world, NPCState looter, EntityId otherId,
        InventoryTransferDirection direction,
        out NPCState other, out bool carriedBySelf)
    {
        other = null;
        carriedBySelf = false;
        if (direction == InventoryTransferDirection.Give &&
            TryResolveGiftRecipient(world, looter, otherId, out other))
        {
            return true;
        }
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

    /// <summary>
    /// §153.1: кому можно ПОДАРИТЬ — живому человеку в сознании, который не
    /// лежит без чувств и не у кого-то на руках. Фракция не важна: подарок
    /// чужачке — законный (и единственный мирный) ход к ней. Вода укрывает по
    /// той же причине, что и в §128: в воде вещь передавать некуда.
    /// <para>
    /// Лежащая целью подарка тоже остаётся — но не здесь: её берёт обычная
    /// ветка обыска ниже, вместе со своей геометрией станции у ног. Реакцию на
    /// подарок она не даёт, потому что не в сознании (§153.3).
    /// </para>
    /// </summary>
    private static bool TryResolveGiftRecipient(
        WorldState world, NPCState giver, EntityId otherId, out NPCState receiver)
    {
        receiver = null;
        if (giver is null || otherId.Equals(giver.Id) ||
            !world.Entities.Npcs.TryGetValue(otherId, out var candidate) ||
            candidate.Health <= 0f ||
            candidate.IsDying ||
            candidate.IsUnconscious(world.Tick) ||
            candidate.IsBeingCarried ||
            candidate.CurrentJunction is null ||
            CombatMedium.IsNpcSwimming(world, candidate))
        {
            return false;
        }

        receiver = candidate;
        return true;
    }

    /// <summary>§153.3: подарок принимают ОСОЗНАННО — и только тогда он что-то
    /// значит для отношений. Зеркало условия выше, но спрашивают его уже после
    /// передачи, когда цель могла успеть уснуть или упасть.</summary>
    public static bool CanReactToGift(WorldState world, NPCState receiver) =>
        receiver is not null &&
        world.Entities.Npcs.ContainsKey(receiver.Id) &&
        receiver.Health > 0f &&
        !receiver.IsDying &&
        !receiver.IsUnconscious(world.Tick) &&
        receiver.Execution.CurrentInteraction != InteractionType.Sleep;

    /// <summary>
    /// §153.1: цель стоит на ногах — станции у ног у неё нет, и мерить надо до
    /// неё самой. Один вопрос вместо повторённого условия: его задают и приём
    /// приказа, и тик исполнения, и оба обязаны выбрать ОДНУ геометрию, иначе
    /// приказ принимается по одной дистанции, а исполняется по другой.
    /// </summary>
    public static bool IsStandingRecipient(WorldState world, NPCState other) =>
        other is not null &&
        other.Health > 0f &&
        !other.IsDying &&
        !other.IsBeingCarried &&
        !IsLyingHelpless(world, other);

    /// <summary>Лежит и не ответит: без сознания, при смерти или спит.</summary>
    public static bool IsLyingHelpless(WorldState world, NPCState npc) =>
        npc.IsUnconscious(world.Tick) ||
        npc.IsDying ||
        npc.Execution.CurrentInteraction == InteractionType.Sleep;
}

}
