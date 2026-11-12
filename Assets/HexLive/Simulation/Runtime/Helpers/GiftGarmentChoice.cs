using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
internal static class GiftGarmentChoice
{
    // Condition is the main contribution; taste can decide between similarly sound pieces.
    internal static float Score(int npcId, ItemInstance item) =>
        .85f * MathUtil.Clamp01(item.Durability) + .15f * ItemAffinity.For(npcId, item.DefinitionId);

    internal static bool CanDecide(WorldState world, NPCState npc) =>
        PlayerLootTargets.CanReactToGift(world, npc) && !ManualControlMath.IsManual(npc) &&
        !npc.IsFighting && !npc.IsBeingCarried &&
        !npc.IsCarryingPerson && !npc.Movement.IsMoving && npc.Memory.Dangers.Count == 0 &&
        npc.Execution.Status != ExecutionStatus.InProgress &&
        world.Tick >= npc.Mind.WakeGraceUntilTick;

    internal static bool ShouldWear(WorldState world, NPCState npc, ItemInstance candidate)
    {
        if (!CanDecide(world, npc) || candidate.Durability <= 0f ||
            !GarmentLibrary.FitsSex(npc.Sex, candidate.DefinitionId) ||
            !world.Content.ObjectDefinitions.TryGetValue(candidate.DefinitionId, out var definition) ||
            definition.Layer is null) return false;
        var score = Score(npc.Id.Value, candidate);
        foreach (var worn in npc.WornItems)
            if (world.Content.ObjectDefinitions.TryGetValue(worn.DefinitionId, out var existing) &&
                WearSlotCatalog.Occupies(definition, existing) &&
                score <= Score(npc.Id.Value, worn) + .0001f) return false;
        return true;
    }
}
}
