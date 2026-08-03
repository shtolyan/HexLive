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

    // §21.21B v14: the jump is ASYMMETRIC, and which end is which flips with
    // the direction. EdgePadding is the NEAR end (right at the lip),
    // FarPadding the FAR one; both are measured ALONG the flight from the
    // border, so every jump is EdgePadding + FarPadding long regardless of
    // approach angle (v10).
    //   DOWN: takeoff -EdgePadding (pushes off the very edge), land +FarPadding
    //   UP:   takeoff -FarPadding (runs up and leaves early), land +EdgePadding
    // That is how a person actually clears a step, and it fixes both halves of
    // the old symmetric model: a drop that landed 0.1 past the lip read as
    // SLIDING off (flight speed 0.44 wu/s against a 1.2 walk), and a climb that
    // left 0.1 from the wall had no run-up at all. Do NOT confuse this with v7's
    // rejected asymmetry — that one had takeoff flush ON the wall (0.00), so she
    // walked into it; the near end stays non-zero here.
    // The lattice points are only a DIRECTION hint; both ends are placed
    // geometrically from the tile-centre crossing.
    public static float EdgePadding = 0.3f;
    public static float FarPadding = 0.65f;

    // Dropping DOWN: a little UP pop off the edge before the fall (world
    // units) so the feet clear the lip instead of scraping it. Presentation
    // only (the model's drop is flat). 0 = straight gravity.
    public static float DownHopUp = 0.2f;

    // Dropping DOWN: fraction of the FLIGHT she stays LEVEL (no drop) before
    // gravity kicks in. Hold until she is PAST the lip or her feet scrape it.
    // §21.21B v14: the flight is no longer symmetric, so the border is crossed
    // at EdgePadding / (EdgePadding + FarPadding) of the flight — 0.32 on the
    // code defaults, 0.13 on the shipped ones. Set this just above that.
    // Presentation only. 0 = fall immediately (old behaviour), 1 = no fall.
    public static float DownFallStartFrac = 0.35f;

    // Diving into water: the body SPLASHES this many world units BELOW the
    // swim level at the deepest point of the plunge, then bobs back up to it
    // — a real plunge with a resurface, not a hover-stop at the waterline.
    public static float DivePlungeDepth = 0.35f;
}

}
