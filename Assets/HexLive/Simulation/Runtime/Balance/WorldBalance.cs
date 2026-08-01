namespace HexLive.Simulation.Runtime
{

// World-clock, light/shadow geometry, weather, moisture, fire fuel and fruit
// rot — environment knobs that used to be consts inside the world systems.
// Static so the WorldBalance config asset can push tuned values at boot;
// systems read them through shims at the old const names.
public static class WorldBalance
{
    // The full day-night cycle in ticks — THE VISUAL CLOCK. Sun and shadows,
    // the sky, the four day phases, the temperature sinusoid, UV, FormatClock
    // and the "Day N" counter all derive from it, and nothing else does.
    // 24000 ticks = 100 real minutes at 0.25 s/tick.
    // §75: сколько девушек на старте. Раньше состав задавался ДЛИНОЙ списка
    // строк в PrototypeWorldDefinitionFactory, то есть «убавить одну» значило
    // править код. Позиции и профили нужд генерируются от индекса, поэтому
    // число можно крутить, не трогая ничего больше.
    public static int ColonistCount = 3;

    public static int DayLengthTicks = 24000;

    // The GAMEPLAY CADENCE — how often the seeded "once per day" rolls happen
    // (rain, storm surge, surf gift, the dog raid). It used to BE the day: the
    // clock was stretched 10x so the calendar stops racing, but those rolls
    // must keep their real-time frequency, so they index off this instead.
    // The within-cycle offsets (StormSurgeOffsetTicks, SurfGiftOffsetTicks,
    // RaidDuskOffsetTicks, the rain jitter) are positions inside THIS period.
    public static int EventCycleTicks = 2400;

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

    // §63: the surf beaches a random garment on the shoreline — chance per
    // day (0.35 ≈ 2-3 pieces per week) and when in the day the tide drops it
    // (on the Slow 16-tick grid, mid-morning). Keeps the wardrobe (and with
    // it pocket capacity) from wearing down to nothing.
    public static float SurfGiftChancePerDay = 0.35f;
    public static int SurfGiftOffsetTicks = 800;
}

}
