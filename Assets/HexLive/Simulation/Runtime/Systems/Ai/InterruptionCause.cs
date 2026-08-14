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
                return true;
            default:
                return false;
        }
    }
}

}
