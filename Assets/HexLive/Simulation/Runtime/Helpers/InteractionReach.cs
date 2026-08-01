using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

// Spec §26.3 r3: the ONE table of "how close is close enough" for starting an
// interaction. The recurring "acts from a whole hex away" family (build /
// craft / harvest / feed / treat at range) always traces back to some site
// rolling its own tolerance constant; every start gate must read this class,
// and every start must be measured through CheckStart so a soak run can count
// violations ("InteractionTooFar" events) instead of waiting for a screenshot.
internal static class InteractionReach
{
    // Object work (harvest/craft/build/pickup/sit...): the object's physical
    // footprint plus one sub-grid step — see SpatialQueries.BesideReach.
    public static float ForObject(float obstacleRadius) =>
        SpatialQueries.BesideReach(obstacleRadius);

    // Person-to-person care (feed/hydrate/treat/medicate/console): the plan
    // walks to arm's length (0.9*R beside her); junction snap can push the
    // legit spot out to ~1.3*R. Anything past that reads as feeding from
    // across the camp.
    public static float Aid => HexSpatialMath.HexRadius * 1.3f;

    // Talking carries a little farther than touch — but 4*R (a hex and a
    // half) read as chatting across the camp. The planner reserves the same
    // arm's-length approach as aid; 2*R is drift slack, not a target.
    public static float Talk => HexSpatialMath.HexRadius * 2f;

    // True when the NPC stands close enough to the anchor to begin; otherwise
    // emits the standardized InteractionTooFar trace (the caller aborts with
    // its own cleanup — shun/claim-release/cooldown differ per kind).
    public static bool CheckStart(
        WorldState world, NPCState npc, Float2 anchor, float reach, string what)
    {
        var distance = HexSpatialMath.Distance(npc.Position, anchor);
        if (distance <= reach)
        {
            return true;
        }

        Trace.Emit(world, npc.Id, "InteractionTooFar",
            $"{what} at {distance:F2}wu > reach {reach:F2}wu");
        return false;
    }

    // Spec §26.6A r4: the whole gate for WORLD-OBJECT work (harvest, chop,
    // craft, build, pick-up, sit, sleep, fuel, draw...). Distance alone was
    // never enough: a hex border that stops the feet — a cliff face, a hut
    // wall — sits well inside BesideReach, so an NPC one sub-grid step BELOW a
    // ledge could pierce the coconut / fell the palm / sit on the stump THROUGH
    // it. Reach is therefore measured twice: straight-line, and along the
    // ground (CanTouchAcross). Both failures emit the same InteractionTooFar
    // trace, so the §26.6A soak counter covers this family too.
    public static bool CheckObjectStart(
        WorldState world, NPCState npc, WorldObjectState worldObject, float obstacleRadius)
    {
        var what = worldObject.DefinitionId;
        var reach = ForObject(obstacleRadius);

        // The anchor must NEVER be unavailable — an object with no linked
        // junction (edge case) falls back to its tile centre with a hex of
        // slack, so no interaction kind can slip past the gate entirely. That
        // fallback has no junction to walk from, so it stays distance-only.
        if (worldObject.Junctions.Count == 0 ||
            !world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchorJunction))
        {
            return CheckStart(world, npc, HexSpatialMath.TileToWorld(worldObject.Tile),
                reach + HexSpatialMath.HexRadius, what);
        }

        if (!CheckStart(world, npc, anchorJunction.WorldPosition, reach, what))
        {
            return false;
        }

        if (npc.CurrentJunction is not { } standJunction)
        {
            return true; // off-grid (mid-hop): distance is all we can honestly measure
        }

        if (SpatialQueries.CanTouchAcross(world, standJunction, anchorJunction.Id, reach))
        {
            return true;
        }

        Trace.Emit(world, npc.Id, "InteractionTooFar",
            $"{what} at {HexSpatialMath.Distance(npc.Position, anchorJunction.WorldPosition):F2}wu " +
            $"is across an impassable border (cliff/wall) from j{standJunction.Value}");
        return false;
    }
}

}
