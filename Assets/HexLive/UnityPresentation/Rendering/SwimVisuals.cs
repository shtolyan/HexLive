namespace HexLive.UnityPresentation.Rendering
{

// §40.18-B swim presentation TUNING KNOBS. Statics (not consts) so the swim
// test scene can drive them live from inspector sliders; the game scene runs
// on these defaults.
public static class SwimVisuals
{
    // How far below the water SURFACE the actor's root hangs while swimming
    // (world units). 0 = feet snapped to the surface; bigger = deeper plunge.
    public static float SinkDepth = 0.6f;

    // Wading (spec 31C.4 walkable shallows — the river): the root sinks this
    // far below the water surface, so she crosses knee-deep instead of
    // walking ON the water like a saint.
    public static float WadeDepth = 0.2f;

    // Spec 31C.4: how far the water SURFACE sits below its tile top, as a
    // FRACTION of one elevation step. Every water height derives from this
    // one number (tile water tops, the merged wave sheet, the sea plane,
    // actor swim/wade/dive/climb-out heights), so raising the water level is
    // this single knob. 0.1 => the surface laps just below the bank.
    public static float SurfaceDropFrac = 0.1f;
}

}
