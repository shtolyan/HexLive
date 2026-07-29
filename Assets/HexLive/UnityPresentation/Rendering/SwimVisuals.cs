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

    // Spec 31C.4: the sea rises to THIS bank elevation (in WHOLE steps) before
    // SurfaceDropFrac sinks it just below the rim. Water sim-tiles are all
    // elevation 0, but the shores they meet are elevation 1 — so anchoring the
    // surface to the water tile's OWN top left it a full step below the bank,
    // and SurfaceDropFrac (documented as "below the bank") could never lift it
    // there. Referencing the shore height instead makes the drop behave as
    // documented. 1 = lap just under the elevation-1 beaches.
    public static float ShoreLevelSteps = 1f;

    // Spec 31C.4: how far the water SURFACE sits below the SHORE
    // (ShoreLevelSteps), as a FRACTION of one elevation step. Every water
    // height derives from this pair (tile water tops, the merged wave sheet,
    // the sea plane, actor swim/wade/dive/climb-out heights). 0.1 => the
    // surface laps just below the bank.
    public static float SurfaceDropFrac = 0.1f;

    // The signed step offset every water-surface site adds on top of the
    // water tile's own top: lift to the shore, then sink the documented drop.
    // Single source of truth so the sea plane, wave sheet, riverbed, actor
    // swim/wade heights and camera picking never desync.
    public static float SurfaceStepOffset => ShoreLevelSteps - SurfaceDropFrac;
}

}
