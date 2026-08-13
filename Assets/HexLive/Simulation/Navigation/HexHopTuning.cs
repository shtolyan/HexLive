namespace HexLive.Simulation.Navigation
{

// §21.21B hex-step hop: THE single source of truth for jump timing, shared by
// the simulation (traversal speed, landing idle) AND the presentation (jump
// clip playback speed, vertical arc duration). Change a number here and both
// layers stay in sync by construction. Static fields (not consts) so the
// swim/jump test scenes can push live values while the game runs; these
// initializers are the shipping defaults.
//
// §21.21B v23 — НАРОЧНО ПРОСТАЯ модель. Пять поколений ручек (асимметричные
// отступы v14, отдельное быстрое окно вниз, «доля полёта» v16, овершут дуги,
// защита подошвы) в сумме дали прыжок, который читался как телепорт в точку и
// постоянно рождал пограничные баги. Правила v23:
//   - прыжок СИММЕТРИЧЕН: взлёт EdgePadding до кромки, посадка EdgePadding
//     после — никакого дополнительного отступа при приземлении;
//   - ОДНО окно на оба направления (HopSeconds), никаких beat-scale;
//   - сим летит с ПОСТОЯННОЙ скоростью весь полётный бит (никаких settle);
//   - перед отрывом она ДОВОРАЧИВАЕТСЯ до направления полёта
//     (MovementSystem, гейт до открытия окна) — прыжок всегда лицом вперёд.
// Новую ручку сюда добавлять только после того, как доказано, что поведение
// нельзя получить из этих пяти.
public static class HexHopTuning
{
    // The model (§21.21B v3): the jump animation is the master clock. The
    // whole hop — push-off, flight, landing recovery — spans HopSeconds (the
    // clip is compressed to exactly this window):
    //   [0 .. Takeoff]              clip: crouch+push  | sim & body: standing
    //   [Takeoff .. Hop-Landing]    clip: airborne     | sim flies the padded
    //                                                    path, body arcs
    //   [Hop-Landing .. Hop]        clip: feet planting| sim & body: standing
    // Walking resumes the moment the window closes. Tune Takeoff/Landing to
    // match where the authored clip actually leaves/touches the ground.
    // Defaults give a flight of 0.6 s over 0.6 wu — ровно темп шага, поэтому
    // прыжок не читается ни рывком, ни зависанием.
    public static float HopSeconds = 1.2f;
    public static float TakeoffSeconds = 0.25f;
    public static float LandingSeconds = 0.35f;

    // §57.11: спуск-падение раненой (HopKind="Fall") — НЕ шестая ручка прыжка,
    // а отдельный ход: без отталкивания, клип падения в полёте, и после
    // приземления она ПОДНИМАЕТСЯ — эту паузу и задаёт ручка. Сим держит
    // ClimbPauseTimer на это время, вид в ту же паузу играет Standing Up;
    // ползущая не встаёт и просто лежит её до конца.
    public static float FallRecoverSeconds = 1.6f;

    // Symmetric flight geometry: takeoff EdgePadding BEFORE the elevation
    // border, landing EdgePadding AFTER it — the same number both ends, both
    // directions, measured along the flight from the tile-centre crossing, so
    // every jump is exactly 2×EdgePadding long regardless of approach angle.
    public static float EdgePadding = 0.3f;

    // How high the body clears the LIP (world units, presentation only).
    // Up: the arc peaks this far above the upper level mid-flight.
    // Down: a small pop of the same height before gravity takes over.
    public static float LipClearance = 0.2f;

    // Diving into water: the body SPLASHES this many world units BELOW the
    // swim level at the deepest point of the plunge, then bobs back up to it
    // — a real plunge with a resurface, not a hover-stop at the waterline.
    public static float DivePlungeDepth = 0.35f;
}

}
