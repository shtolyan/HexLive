namespace HexLive.Simulation.Runtime
{

// §121.5: почему сносится план. Каждый вызов PlanInterruption обязан назвать
// причину, и NpcControlPolicy решает по ней, проходит ли снос у ручного
// персонажа. Enum НЕ сериализуется (в сейв не пишется), но дисциплина
// append-only сохраняется — имена попадают в трассу.
public enum InterruptionCause
{
    // ── ТЕЛО: проходит всегда, у любого (§121: граница «тело/выбор») ──
    Death,          // MobSystem — погибла
    BodyComa,       // NeedsDecaySystem, кома §60
    Faint,          // NeedsDecaySystem, обморок §40.13
    Crying,         // NeedsDecaySystem, слёзы §110
    Dying,          // MortalityHelpers §105
    PlayDead,       // MortalityHelpers §105.14 — тело ложится
    LimbLost,       // AmputateSystemHelpers §118

    // ── МЕХАНИКА ПЛАНА: проходит всегда (свой план сам себя чинит и
    //    законно проваливает приказ, если цель исчезла) ──
    ExecutionFailure,  // ExecutionSystem.* — цель исчезла, не дошла, занято…
    Replan,            // PlanningSystem — осиротевшее взаимодействие
    PathFailure,       // PathfindingSystem — лимит ретраев пути
    RescueDrop,        // KenshiRescueMath / ExecutionSystem.Rescue

    // ── ИГРОК: проходит всегда ──
    PlayerCommand,     // ManualCommandExecutor / ManualOrderSystem
    ControlReleased,   // тумблер 🎮→🧠 и таймаут §121.7

    // ── САМОЗАЩИТА: на неё напали — проходит всегда (§121.2: бьют —
    //    бросает приказ и дерётся) ──
    CombatVictim,      // ответ на удары: RaidSystem/MobSystem/AnimalCombat
    Predation,         // жертва хищничества §56 встаёт драться
    AbuseMark,         // сцена травли §81 легла на жертву
    CorneredFight,     // загнана групповой охотой §108 — встаёт драться

    // ── ВЫБОР: только у не-ручных (fail-closed) ──
    Auction,           // DecisionSystem — смена цели, talk/aid-ожидания
    ThreatReroute,     // ThreatAlertSystem.AvoidThreat / AvoidHostile
    ThreatFirstStrike, // ThreatAlertSystem.StartFirstStrike
    Flee,              // MobSystem.TryStartFlee/TryFleeToCamp, GroupHunt
    HelpFriend,        // CombatHelpSystem — клич/защита подруги
    ScenePact,         // сговор §108, сцена выгона §115/§117
    SceneInitiator,    // Raid/Abuse/Expel — инициатор бросает дела

    // ── ПОЛИТИКА УПРАВЛЕНИЯ: проходит всегда (append-only, дописано в
    //    конец — группа определяется switch'ом ниже, а не ординалом) ──
    // §121.6 r2: ручному персонажу досталась цель, которой ему иметь не
    // положено (чужая система забыла проверить режим). Планировщик для
    // ручной выключен и такую цель не починит, поэтому её снимает ручной
    // проход — и обязан пройти, иначе колонистка встаёт столбом.
    ManualPolicySweep,

    // ── СОН: принятый ручной приказ прерван конкретной телесной причиной.
    // Имена отдельные, чтобы игрок видел не общее «ошибка выполнения», а
    // опасность, голод или жажду (§49.13 / баг #216). ──
    SleepDanger,
    SleepHunger,
    SleepThirst,
}

public static class InterruptionCauses
{
    // §121.5: единственная точка истины «проходит у ручного всегда или это
    // выбор». Явный switch, не сравнение порядковых номеров: enum растёт, и
    // вставка не по порядку не должна молча менять группу.
    public static bool AlwaysAllowed(InterruptionCause cause)
    {
        switch (cause)
        {
            case InterruptionCause.Death:
            case InterruptionCause.BodyComa:
            case InterruptionCause.Faint:
            case InterruptionCause.Crying:
            case InterruptionCause.Dying:
            case InterruptionCause.PlayDead:
            case InterruptionCause.LimbLost:
            case InterruptionCause.ExecutionFailure:
            case InterruptionCause.Replan:
            case InterruptionCause.PathFailure:
            case InterruptionCause.RescueDrop:
            case InterruptionCause.PlayerCommand:
            case InterruptionCause.ControlReleased:
            case InterruptionCause.CombatVictim:
            case InterruptionCause.Predation:
            case InterruptionCause.AbuseMark:
            case InterruptionCause.CorneredFight:
            case InterruptionCause.ManualPolicySweep:
            case InterruptionCause.SleepDanger:
            case InterruptionCause.SleepHunger:
            case InterruptionCause.SleepThirst:
                return true;
            default:
                return false;
        }
    }
}

}
