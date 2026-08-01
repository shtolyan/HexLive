namespace HexLive.Simulation.Agents
{

// §70: which side an NPC is on. Until now every NPC was implicitly one crowd —
// everyone chatted, nursed and defended everyone. A faction is the ONE bit that
// splits "us" from "them"; everything else about an outsider (needs, GOAP,
// crafting, clothing) stays identical to a colonist.
//
// It is an id, not a bool, on purpose: more outsiders are coming and they are
// allies to EACH OTHER, not a second lone wolf — they all take Outsiders.
//
// Colony = 0 is deliberate: default(Faction), the NPCState field initialiser
// and every pre-§70 save all land on the girls with no migration code.
//
// APPEND-ONLY — the save blob stores the ordinal, exactly like GoalType.
public enum Faction
{
    // The girls — the player's colony.
    Colony = 0,

    // The hostile survivor(s) camped on the far side of the island.
    Outsiders = 1
}

}
