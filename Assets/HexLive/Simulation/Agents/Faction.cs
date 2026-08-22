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

    // §146: the other hostile girl camps of the large-island modes. Colony-KIND
    // (female roster, no Outsider waves, §53 compassion band), but enemies of
    // the player's camp: no shared beds/sites/help, expulsion may become combat.
    // Worldgen for BigIsland and HugeIsland assigns these.
    Colony2 = 2,

    Colony3 = 3,

    // §146.9: three more independent hostile girl camps on HugeIsland. They
    // keep the same relation as Colony2/3: allies only inside their own camp.
    Colony4 = 4,
    Colony5 = 5,
    Colony6 = 6,

    // §146.10: a wounded woman washed ashore near the player's camp. Colony
    // treats her as an aid/rescue ally, but she is not player-controlled until
    // food, water and treatment bring her above the recruitment thresholds.
    Castaway = 7
}

}
