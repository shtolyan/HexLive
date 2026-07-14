namespace HexLive.Simulation.Navigation
{

// §21.21B hex-step hop: THE single source of truth for jump timing, shared by
// the simulation (traversal speed, landing idle) AND the presentation (jump
// clip playback speed, vertical arc duration). Change a number here and both
// layers stay in sync by construction. Static fields (not consts) so the
// swim/jump test scene can push live values while the game runs; these
// initializers are the shipping defaults.
public static class HexHopTuning
{
    // THE model (§21.21B v3): the jump animation is the master clock. The
    // whole hop — push-off, flight, landing recovery — spans HopSeconds (the
    // clip is compressed to exactly this window). Takeoff/Landing mark up
    // the CLIP's own sections; everything else is derived:
    //   [0 .. Takeoff]              clip: crouch+push  | sim & body: standing
    //   [Takeoff .. Hop-Landing]    clip: airborne     | sim flies the padded
    //                                                    path, body arcs
    //   [Hop-Landing .. Hop]        clip: feet planting| sim & body: standing
    // Walking resumes the moment the window closes. Tune Takeoff/Landing to
    // match where the authored clip actually leaves/touches the ground.
    public static float HopSeconds = 2f;
    public static float TakeoffSeconds = 0.5f;
    public static float LandingSeconds = 0.5f;

    // Down-jumps (sprying off a ledge / into water) can run on their OWN,
    // usually faster, window. HopSeconds is the UP window; DownHopSeconds the
    // DOWN one. Default == HopSeconds (so nothing changes until it's dialed).
    // Takeoff/Landing beats scale with the window (DownBeatScale), so the whole
    // down-jump is uniformly faster — same shape, shorter clock — and the sim
    // window, the body arc and the clip playback all stay in lockstep.
    public static float DownHopSeconds = 2f;

    // The window for a hop in the given direction, and the factor the
    // takeoff/landing beats scale by for a down-jump (1 for up — byte-identical).
    public static float WindowSeconds(bool up) => up ? HopSeconds : DownHopSeconds;
    public static float DownBeatScale => DownHopSeconds / System.MathF.Max(0.0001f, HopSeconds);

    // Symmetric wall clearance (world units, measured PERPENDICULAR to the
    // obstacle border): the jump takes off exactly this far on the stand
    // side of the wall and lands exactly this far on the target side. The
    // lattice-point positions are only a DIRECTION hint — the takeoff/landing
    // are placed geometrically, so a boundary point sitting right on the wall
    // can no longer leave her jumping flush against it.
    // 0.3 => takeoff 0.3 before the wall, land 0.3 past it, jump length 0.6
    // (2*EdgePadding) — "just over the edge". Raise for a bigger leap.
    public static float EdgePadding = 0.3f;

    // Dropping DOWN: a little UP pop off the edge before the fall (world
    // units) so the feet clear the lip instead of scraping it. Presentation
    // only (the model's drop is flat). 0 = straight gravity.
    public static float DownHopUp = 0.2f;

    // Dropping DOWN: fraction of the FLIGHT she stays LEVEL (no drop) before
    // gravity kicks in. The flight is symmetric about the wall border, which
    // she crosses at 0.5 — so holding to ~0.5 means she sails OVER the lip and
    // only falls once she's above the lower ground, never scraping the edge.
    // Presentation only. 0 = fall immediately (old behaviour), 1 = no fall.
    public static float DownFallStartFrac = 0.5f;

    // Diving into water: the body SPLASHES this many world units BELOW the
    // swim level at the deepest point of the plunge, then bobs back up to it
    // — a real plunge with a resurface, not a hover-stop at the waterline.
    public static float DivePlungeDepth = 0.35f;
}

}
