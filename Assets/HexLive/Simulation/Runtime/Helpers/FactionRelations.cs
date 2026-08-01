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
// Two-valued today: same faction => allies, different => hostile. If a third,
// NEUTRAL side ever appears (a trader), BOTH helpers must be revisited — at
// that point `AreAllies` and `AreHostile` stop being complements, and any
// `!AreHostile` written in the meantime would silently mean "ally or stranger".
// That is why callers must ask the positive question they actually mean:
// cooperation code asks AreAllies, aggression code asks AreHostile.
public static class FactionRelations
{
    public static bool AreAllies(Faction a, Faction b)
    {
        // Kill-switch: with §72 off the island is one big family again, so every
        // gate downstream collapses to its pre-§72 answer without a second code
        // path.
        return !Spec72.Enabled || a == b;
    }

    public static bool AreHostile(Faction a, Faction b) => !AreAllies(a, b);

    public static bool AreAllies(NPCState a, NPCState b) => AreAllies(a.Faction, b.Faction);

    public static bool AreHostile(NPCState a, NPCState b) => AreHostile(a.Faction, b.Faction);
}

}
