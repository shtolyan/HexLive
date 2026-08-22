using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

// §72: the SINGLE place hostility is defined. Nothing anywhere may spell
// `a.Faction == b.Faction` by hand — a hand-rolled check is exactly how a
// half-threaded faction turns into an enemy answering his own victim's help cry.
//
// It lives in Runtime (not Agents) so every system file sees it with no extra
// using, and so the §72 kill-switch is a plain field access.
//
// §146.3: different camps on the large islands are openly hostile. The only
// cross-faction alliance is Colony <-> Castaway while the survivor event is
// being rescued. Cooperation asks AreAllies; aggression asks AreHostile; both
// still collapse to the pre-§72 one-family behaviour behind the kill-switch.
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

    public static bool AreHostile(Faction a, Faction b) =>
        Spec72.Enabled && !AreAllies(a, b);

    public static bool AreAllies(NPCState a, NPCState b) => AreAllies(a.Faction, b.Faction);

    public static bool AreHostile(NPCState a, NPCState b) => AreHostile(a.Faction, b.Faction);
}

}
