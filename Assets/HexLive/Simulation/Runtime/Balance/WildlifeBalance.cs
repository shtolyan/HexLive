namespace HexLive.Simulation.Runtime
{

// Wildlife-director knobs: population caps, respawn cadence, raid timing and
// hunt chances that used to be private consts in Mob/Rabbit/SharkSystem.
// Static so the WorldBalance config asset can push tuned values at boot;
// systems read them through shims at the old const names. (Per-mob COMBAT
// stats — bite, HP, windup — stay in MobCatalog/MobConfig assets.)
public static class WildlifeBalance
{
    // §46 dog-pack director.
    public static int MaxDogs = 2;
    public static int DogRespawnCheckTicks = 7200;
    public static int RaidDuskOffsetTicks = 1800;
    public static int DogSpawnMinDistanceFromNpc = 5;

    // Rabbits/crabs (spec 31C).
    public static int MaxRabbits = 4;
    public static int RabbitRespawnCheckTicks = 2400;
    public static int RabbitSpawnMinDistanceFromNpc = 3;
    public static int RabbitFleeRadiusTiles = 2;
    public static float RabbitHopChance = 0.2f;
    public static float RabbitKillChance = 0.5f;
    public static int RabbitSpookTicks = 150;
    // Bow hunt: chance an arrow connects, and chance the arrow is recovered
    // from the kill (§54: the kill drops a carcass, not instant loot).
    public static float RabbitBowHitChance = 0.6f;
    public static float ArrowRecoverChance = 0.4f;

    public static int MaxSharks = 2;
}

}
