using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
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
}

}
