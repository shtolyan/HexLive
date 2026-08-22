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

    // §132: жёсткий потолок живых людей в мире и квоты двух лагерей.
    // Трупы не считаются: потеря освобождает место для будущего прибытия,
    // но не воскрешает пропущенную неделю. Квоты отдельны от общего потолка намеренно:
    // так балансер может сделать асимметричный сценарий, не меняя код.
    public static int MaxLivingNpcs = 10;
    public static int MaxColonyNpcs = 5;
    public static int MaxOutsiderNpcs = 5;

    // §146.6: потолки большого острова — ПАРНЫЕ ручки, не мутация верхних:
    // статики глобальны на процесс, а клиент перезапускается в другой режим
    // без рестарта, поэтому один simdata.json несёт оба режима, а селекторы
    // ниже выбирают по world.Mode. 6 стартовых девушек + по одной в неделю
    // в КАЖДЫЙ из трёх лагерей упёрлись бы в Feud-потолки немедленно.
    // Селекторы по режиму живут в PopulationArrivalMath (не здесь: гейт
    // «у ручки есть читатель» не считает Balance/ читателем самого себя).
    public static int BigIslandMaxLivingNpcs = 24;
    public static int BigIslandMaxCampNpcs = 6;

    // §146.9: six camps may each grow from one to six women; five outsider
    // seats remain inside the shared cap (6×6 + 5 = 41, one spare event seat).
    public static int HugeIslandMaxLivingNpcs = 42;
    public static int HugeIslandMaxCampNpcs = 6;

    // Начало календарных дней 7, 14, 21…: одна новая девушка, если оба
    // потолка оставляют ей место. 0 выключает пополнение.
    public static int ColonyArrivalIntervalDays = 7;

    public static int DayLengthTicks = 24000;

    // The GAMEPLAY CADENCE — how often the seeded "once per day" rolls happen
    // (rain, storm surge, the dog raid). It used to BE the day: the
    // clock was stretched 10x so the calendar stops racing, but those rolls
    // must keep their real-time frequency, so they index off this instead.
    // The within-cycle offsets (StormSurgeOffsetTicks, RaidDuskOffsetTicks,
    // the rain jitter) are positions inside THIS period. The §63 surf gift is
    // intentionally calendar-driven and therefore does not use this cadence.
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

    // §35.5 / bug #160: ordinary ambient drying of CLOTHING only. Body and
    // non-clothing pickups keep the base rate; fire/rack factors bypass this
    // scale so their established absolute speeds do not move.
    public static float ClothingNaturalDryMultiplier = 0.1f;

    // Fuel units a lit campfire burns per slow tick (before the §54.14
    // stone-ring multiplier).
    public static float FireBurnPerSlowTick = 16f;

    // Ground fruit (coconuts) rots away after this many ticks unpicked.
    public static int FruitRotTicks = 2400;

    // §29A r2: produce lands no closer than this many trunk radii
    // (ObstacleRadius) from its producer. The blocked ring is one radius; the
    // drop keeps one more, so a nut always has walkable ground on every side.
    // 0 (or a radius-0 producer) = no clearance, pre-r2 behaviour.
    public static float FruitDropClearanceFactor = 2f;

    // §63: at 06:00 after this many complete visual days, the surf beaches one
    // random girl-compatible garment per living girl in the player's colony.
    public static int SurfGiftIntervalDays = 5;
}

}
