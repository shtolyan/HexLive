using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

public enum ItemRequestOutcome
{
    Transferred,
    MissingItem,
    Refused,
    NeededByOwner,
    CannotTalk,
    NoSpace,
    TooFar
}

/// <summary>§153.4: one addressed request, answered and transferred atomically.
/// This is voluntary giving by a conscious owner, never the helpless-loot path.</summary>
public static class ItemRequestMath
{
    public static ItemRequestOutcome Request(
        WorldState world, NPCState requester, EntityId ownerId, string definitionId)
    {
        var outcome = TryTransfer(world, requester, ownerId, definitionId);
        Trace.Emit(world, requester.Id, "ItemRequestResult",
            $"Target=NPC{ownerId.Value} Def={definitionId} Count=1 Outcome={outcome}");
        return outcome;
    }

    private static ItemRequestOutcome TryTransfer(
        WorldState world, NPCState requester, EntityId ownerId, string definitionId)
    {
        if (ownerId == requester.Id ||
            !world.Entities.Npcs.TryGetValue(ownerId, out var owner) ||
            !CanRespond(world, requester) || !CanRespond(world, owner) ||
            owner.Mind.ManualControl)
        {
            return ItemRequestOutcome.CannotTalk;
        }

        // The exact same hand-over reach and barrier check as §153 gifts.
        if (!InteractionReach.CheckPersonStart(world, requester, owner,
                owner.Position, InteractionReach.Aid, "Request item"))
        {
            return ItemRequestOutcome.TooFar;
        }

        var index = owner.Inventory.Items.FindIndex(i =>
            i.DefinitionId == definitionId && MayGive(owner, i));
        if (index < 0)
        {
            if (owner.Inventory.Items.Exists(i => i.DefinitionId == definitionId))
                return ItemRequestOutcome.Refused;
            return owner.WornItems.Exists(i => i.DefinitionId == definitionId)
                ? ItemRequestOutcome.NeededByOwner
                : ItemRequestOutcome.MissingItem;
        }

        var selected = owner.Inventory.Items[index];
        var verdict = Decide(world, owner, requester, selected);
        if (verdict != ItemRequestOutcome.Transferred)
        {
            return verdict;
        }

        var itemRef = new InventoryItemRef(InventoryItemSource.Carried, index, definitionId);
        if (!PlayerInventoryTransferMath.TryResolveTransfer(
                world, owner, itemRef, 1, out var moving, out var contents) ||
            moving.Count != 1 || !ReferenceEquals(moving[0], selected))
        {
            return ItemRequestOutcome.MissingItem;
        }
        if (!PlayerInventoryTransferMath.FitsAfter(world, owner, requester, itemRef, 1))
        {
            return ItemRequestOutcome.NoSpace;
        }

        PlayerInventoryTransferMath.MoveResolved(world, owner, requester, itemRef, moving, contents);
        if (selected.OwnerId != 0 ||
            (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition) &&
             definition.Layer.HasValue))
        {
            selected.OwnerId = requester.Id.Value;
        }
        ExecutionSystem.ReactToGift(world, owner, requester, definitionId, moving);
        return ItemRequestOutcome.Transferred;
    }

    private static bool CanRespond(WorldState world, NPCState npc) =>
        PlayerLootTargets.CanReactToGift(world, npc) &&
        PlayerLootTargets.IsStandingRecipient(world, npc) &&
        npc.CurrentJunction.HasValue && !CombatMedium.IsNpcSwimming(world, npc) &&
        !npc.Movement.IsMoving && !npc.IsFighting &&
        npc.Execution.Status != ExecutionStatus.InProgress &&
        !npc.IsBeingCarried;

    private static bool MayGive(NPCState owner, ItemInstance item) =>
        item.OwnerId == 0 || item.OwnerId == owner.Id.Value;

    private static ItemRequestOutcome Decide(
        WorldState world, NPCState owner, NPCState requester, ItemInstance selected)
    {
        var spares = 0;
        foreach (var item in owner.Inventory.Items)
            if (item.DefinitionId == selected.DefinitionId && MayGive(owner, item) &&
                !ReferenceEquals(item, selected)) spares++;
        var category = ItemCatalog.Resolve(selected.DefinitionId).Category;
        if (ItemCatalog.IsWaterSourceId(selected.DefinitionId)) category = ItemCategory.Water;
        if ((spares == 0 && category is ItemCategory.Tool or ItemCategory.Weapon or
                ItemCategory.Water or ItemCategory.Medicine) ||
            (category == ItemCategory.Food && (owner.Mind.IsStarving ||
                owner.Needs.Hunger >= SimBalance.StarvingEnterThreshold)) ||
            (category == ItemCategory.Water && selected.ResourceAmount > 0f &&
                (owner.Mind.IsDehydrated || owner.Needs.Thirst >= SimBalance.StarvingEnterThreshold)) ||
            (category == ItemCategory.Medicine &&
                (AidAssessment.NeedsDressing(owner) || AidAssessment.NeedsMedicine(owner, world.Tick))) ||
            (category is ItemCategory.Clothing or ItemCategory.Armor &&
             ((owner.Needs.ThermalDiscomfort >= SimBalance.DressThermalThreshold &&
               EquipmentMath.WarmthGainFromWearing(world, owner, selected.DefinitionId) >=
                   SimBalance.DressWarmthGainMin) ||
              (owner.Memory.Dangers.Count > 0 &&
               EquipmentMath.ItemValues(world, selected.DefinitionId).Armor > 0f))))
        {
            return ItemRequestOutcome.NeededByOwner;
        }

        // Read-only on refusal: even an absent relationship stays absent.
        var affinity = owner.Social.Relationships.TryGetValue(requester.Id, out var relationship)
            ? relationship.Affinity : 0f;
        var willingness = WearPermissionMath.WillingnessBase +
            WearPermissionMath.WillingnessPerSpare *
                System.Math.Min(spares, WearPermissionMath.WillingnessSpareCap) +
            WearPermissionMath.WillingnessAffinityWeight * affinity;
        return willingness >= WearPermissionMath.WillingnessThreshold
            ? ItemRequestOutcome.Transferred : ItemRequestOutcome.Refused;
    }
}

}
