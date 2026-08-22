namespace HexLive.Simulation.Agents
{

// §72: which side an NPC is on. Until now every NPC was implicitly one crowd —
// everyone chatted, nursed and defended everyone. A faction is the ONE bit that
// splits "us" from "them"; everything else about an outsider (needs, GOAP,
// crafting, clothing) stays identical to a colonist.
//
// It is an id, not a bool, on purpose: more outsiders are coming and they are
// allies to EACH OTHER, not a second lone wolf — they all take Outsiders.
//
// Colony = 0 is deliberate: default(Faction), the NPCState field initialiser
// and every pre-§72 save all land on the girls with no migration code.
//
// APPEND-ONLY — the save blob stores the ordinal, exactly like GoalType.
public enum Faction
{
    // The girls — the player's colony.
    Colony = 0,

    // The hostile survivor(s) camped on the far side of the island.
    Outsiders = 1,

    // §146: the other girl camps of the large-island modes. Colony-KIND
    // (female roster, no Outsider waves, §53 compassion band). They are enemies
    // in BigIsland, but neutral neighbours in HugeIsland/Maniac until personal
    // hatred; §146.12 can merge them into one faction and home.
    Colony2 = 2,

    Colony3 = 3,

    // §146.9: three more independent girl camps on HugeIsland. Their relation
    // is mode-aware by the same §146.12 rules as Colony2/3.
    Colony4 = 4,
    Colony5 = 5,
    Colony6 = 6,

    // §146.10: a wounded woman washed ashore near the player's camp. Colony
    // treats her as an aid/rescue ally, but she is not player-controlled until
    // food, water and treatment bring her above the recruitment thresholds.
    Castaway = 7
}

}
