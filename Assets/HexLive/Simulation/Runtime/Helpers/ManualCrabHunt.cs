using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Wildlife;

namespace HexLive.Simulation.Runtime
{
// §121.1: positive AttackMob ids retain their existing large-mob meaning.
// Crab ids use the same negative namespace as their presentation keys.
public static class ManualCrabHunt
{
    public static int TargetId(int crabId) => -crabId - 1;
    public static bool IsOrdered(NPCState npc, int crabId) =>
        npc.Mind.ManualControl && npc.Mind.CurrentGoal == GoalType.PlayerAttack &&
        npc.Mind.ManualAttackMobId == TargetId(crabId);

    public static bool TryTarget(WorldState world, int targetId, out JunctionId junction, out TileCoord tile)
    {
        junction = default; tile = default;
        if (targetId >= 0) return false;
        var id = -targetId - 1;
        foreach (var crab in world.Rabbits)
            if (crab.Id == id) { junction = crab.Junction; tile = crab.Tile; return true; }
        foreach (var slot in world.MobSpawnSlots)
        {
            if (slot.MobId != MobIds.Crab || slot.ReservedMobId != id ||
                slot.State != MobSlotState.Virtual || slot.Ring.Count == 0) continue;
            var step = MobPreview.ComputeStep(world.Seed, slot.SlotId, slot.CycleIndex,
                slot.Ring.Count, world.Tick, WildlifeBalance.MobPreviewSegmentTicks,
                WildlifeBalance.MobPreviewPauseChance);
            var waypoint = slot.Ring[step.Paused || step.Alpha < .5f ? step.From : step.To];
            junction = waypoint.Junction; tile = waypoint.Tile;
            return true;
        }
        return false;
    }
}
}
