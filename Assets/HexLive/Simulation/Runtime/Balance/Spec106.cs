namespace HexLive.Simulation.Runtime
{

// §106: вода — убежище. Пловца (глубокая вода, Water && !Walkable) не бьют с
// суши, пловец не бьёт сам, и погоня за нырнувшей жертвой бросается — как у
// двери хижины (§29C.4A), только порогом служит кромка воды. Среда атаки
// остаётся данными (`MobStats.AttackMediums`) для будущих видов.
public static class Spec106
{
    // Один выключатель на весь слой: false — вода снова ничего не значит в
    // бою, поведение возвращается к до-§106 (гейт CanStrike, клапаны Prey /
    // Raid / Abuse / волков и skip в ThreatAlert глохнут разом).
    public static bool WaterSanctuaryEnabled = true;
}

}
