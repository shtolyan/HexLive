namespace HexLive.Simulation.Runtime
{

// World-clock, light/shadow geometry, weather, moisture, fire fuel and fruit
// rot — environment knobs that used to be consts inside the world systems.
// Static so the WorldBalance config asset can push tuned values at boot;
// systems read them through shims at the old const names.
public static class WorldBalance
{
    // The full day-night cycle in ticks. THE world clock unit — raid dusk
    // offsets, storm surges and the history formatter all count in it.
    public static int DayLengthTicks = 2400;

    // §33 shadow model: how many tiles a shadow ray marches, the renderer's
    // step height in world units, and how many virtual steps indoor walls add.
    public static int ShadowRaySteps = 7;
    public static float ElevationWorldStep = 0.55f;
    public static float CanopyVirtualSteps = 2f;

    // §40 storms: chance per day, raft logs washed away per storm, and when
    // (before dusk) the surge lands.
    public static float StormChancePerDay = 0.08f;
    public static int StormRaftLogLoss = 2;
    public static int StormSurgeOffsetTicks = 1600;

    // Base drying rate per slow tick for soaked garments (worn, no fire).
    public static float MoistureDryBase = 0.02f;

    // Fuel units a lit campfire burns per slow tick (before the §54.14
    // stone-ring multiplier).
    public static float FireBurnPerSlowTick = 16f;

    // Ground fruit (coconuts) rots away after this many ticks unpicked.
    public static int FruitRotTicks = 2400;
}

}
