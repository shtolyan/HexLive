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
// §146.3: THREE-valued since the rival girl camps landed. Same faction =>
// allies; girls vs Outsiders => hostile; two different girl camps => RIVALS —
// neither. This is exactly the revisit the old comment promised: `AreAllies`
// and `AreHostile` are no longer complements, so callers must ask the positive
// question they actually mean — cooperation code asks AreAllies, aggression
// code asks AreHostile, and a `!AreHostile` means "ally or rival".
public static class FactionRelations
{
    public static bool AreAllies(Faction a, Faction b)
    {
        // Kill-switch: with §72 off the island is one big family again, so every
        // gate downstream collapses to its pre-§72 answer without a second code
        // path.
        return !Spec72.Enabled || a == b;
    }

    // §146.3: "one of the girls" as opposed to the Outsiders. Worldgen,
    // appearance/trait rolls, raid gates and population caps key on the KIND;
    // camp membership stays the faction itself.
    public static bool IsColonyKind(Faction f) => f != Faction.Outsiders;

    public static bool AreHostile(Faction a, Faction b) =>
        Spec72.Enabled && IsColonyKind(a) != IsColonyKind(b);

    public static bool AreAllies(NPCState a, NPCState b) => AreAllies(a.Faction, b.Faction);

    public static bool AreHostile(NPCState a, NPCState b) => AreHostile(a.Faction, b.Faction);
}

}
