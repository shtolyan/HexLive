using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

// §72: the SINGLE place hostility is defined. Nothing anywhere may spell
// `a.Faction == b.Faction` by hand — a hand-rolled check is exactly how a
// half-threaded faction turns into an enemy answering his own victim's help cry.
//
// It lives in Runtime (not Agents) so every system file sees it with no extra
// using, and so the §72 kill-switch is a plain field access.
//
// §146.3/§146.12: the faction-only overload is the legacy/open-war matrix.
// Callers that own a WorldState must use the world-aware overload: solo camps
// start neutral there, while personal hate can make one direction hostile.
public static class FactionRelations
{
    public static bool AreAllies(Faction a, Faction b)
    {
        // Kill-switch: with §72 off the island is one big family again, so every
        // gate downstream collapses to its pre-§72 answer without a second code
        // path.
        if (!Spec72.Enabled || a == b)
        {
            return true;
        }

        // §146.10: the player's camp can tend and carry the event castaway
        // before she joins. Other hostile camps do not acquire her by passing.
        return (a == Faction.Colony && b == Faction.Castaway) ||
               (a == Faction.Castaway && b == Faction.Colony);
    }

    // §146.3: "one of the girls" as opposed to the Outsiders. Worldgen,
    // appearance/trait rolls, raid gates and population caps key on the KIND;
    // camp membership stays the faction itself.
    public static bool IsColonyKind(Faction f) => f != Faction.Outsiders;

    public static bool IsGirlCamp(Faction f) =>
        f is Faction.Colony or Faction.Colony2 or Faction.Colony3 or
            Faction.Colony4 or Faction.Colony5 or Faction.Colony6;

    public static bool AreHostile(Faction a, Faction b) =>
        Spec72.Enabled && !AreAllies(a, b);

    public static bool AreHostile(WorldState world, Faction a, Faction b)
    {
        if (!Spec72.Enabled || AreAllies(a, b))
        {
            return false;
        }

        return !CampDiplomacyMath.IsSoloCampMode(world.Mode) ||
               !IsGirlCamp(a) || !IsGirlCamp(b);
    }

    public static bool AreHostile(WorldState world, NPCState actor, NPCState target)
    {
        if (!Spec72.Enabled || AreAllies(actor, target))
        {
            return false;
        }

        // §146.12: in a solo-camp world a human is an enemy because THIS
        // observer hates them, never merely because their faction label says
        // "outsider". This deliberately includes Outsiders.
        if (CampDiplomacyMath.IsSoloCampMode(world.Mode))
        {
            return actor.Faction != target.Faction &&
                   actor.Social.GetOrCreate(target.Id).Affinity <=
                       CampDiplomacyMath.HatredAffinityThreshold;
        }

        return AreHostile(world, actor.Faction, target.Faction);
    }

    public static bool AreNeutral(WorldState world, NPCState actor, NPCState target) =>
        !AreAllies(actor, target) && !AreHostile(world, actor, target);

    public static bool AreAllies(NPCState a, NPCState b) => AreAllies(a.Faction, b.Faction);

    public static bool AreHostile(NPCState a, NPCState b) => AreHostile(a.Faction, b.Faction);
}

}
