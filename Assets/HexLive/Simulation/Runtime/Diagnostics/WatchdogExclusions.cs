using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Кого сторожа поведения не трогают. Общее для §30.15 (застой) и §122 (петля).
///
/// <para>
/// Вынесено из <see cref="StuckDiagnosticSystem"/>, когда сторожей стало два.
/// Причина та же, по которой в §63 разъехались фильтры кокоса, а в AI_REVIEW —
/// пять несовместимых мерок дистанции: одинаковое правило, скопированное в два
/// места, правится в одном из них. Здесь цена расхождения конкретная — сторож
/// начнёт жаловаться на спящую или на жертву в идущей сцене, и настоящие находки
/// утонут в этом шуме.
/// </para>
/// </summary>
public static class WatchdogExclusions
{
    /// <summary>
    /// Неподвижность ПО СЮЖЕТУ, а не по поломке: без сознания, рыдает (§110),
    /// притворяется мёртвой (§105.14), мертва. Спящая и лежащая в коме обязаны
    /// быть неподвижны, и жаловаться на это значит утопить настоящие находки.
    /// </summary>
    public static bool IsAuthoredStillness(WorldState world, NPCState npc) =>
        npc.IsUnconscious(world.Tick) || npc.IsCrying(world.Tick) ||
        npc.IsPlayingDead(world.Tick) || npc.Health <= 0f;

    /// <summary>
    /// Стоит, потому что так и надо: ждёт партнёра, помощницу, держит роль в
    /// сцене или дерётся вне аукциона.
    /// </summary>
    public static bool IsIntentionalHold(WorldState world, NPCState npc)
    {
        // Reactive combat owns the body without an auction goal. Its own
        // contact/standoff valves diagnose failure; Goal=None here is not an
        // auction crisis and must not be double-reported as one.
        if (npc.IsFighting)
        {
            return true;
        }

        // §29C.4B: a defender already beside the attacker is deliberately
        // stationary; the fast combat system owns her strikes while the
        // bounded Wait plan keeps the assist intent alive.
        if (npc.Mind.CurrentGoal == GoalType.Defend &&
            npc.Mind.AssistHoldSinceTick > 0 &&
            (npc.Mind.CombatAssistDogId.HasValue ||
             npc.Mind.CombatAssistAttackerNpcId.HasValue))
        {
            return true;
        }

        if (npc.Mind.PendingTalkFrom is { } talkerId &&
            world.Entities.Npcs.TryGetValue(talkerId, out var talker) &&
            talker.Plan.TargetAgentId == npc.Id)
        {
            return true;
        }

        if (npc.Mind.PendingAidFrom is { } helperId &&
            world.Entities.Npcs.TryGetValue(helperId, out var helper) &&
            helper.Plan.TargetAgentId == npc.Id &&
            helper.Mind.CurrentGoal is GoalType.Aid or GoalType.Rescue or
                GoalType.Splint or GoalType.FitProsthetic)
        {
            return true;
        }

        if (npc.Mind.PendingAbuseFrom is { } abuserId &&
            world.Entities.Npcs.TryGetValue(abuserId, out var abuser) &&
            (abuser.Execution.CurrentInteraction == InteractionType.Abuse ||
             HexSpatialMath.HexDistance(npc.Tile, abuser.Tile) <=
                Spec57.AnswerReadyRadiusTiles))
        {
            return true;
        }

        // §117: the challenged outsider waits through the demand, and the
        // owner holds once approach phase has ended.  Both are authored scene
        // roles.  The phase-0 owner is deliberately NOT excluded: she must
        // really close the distance, so a broken approach remains visible.
        if (npc.Mind.CurrentGoal == GoalType.Expel &&
            (npc.Mind.PendingExpulsionFrom.HasValue ||
             (npc.Mind.ExpulsionTargetNpcId.HasValue && npc.Mind.ExpulsionPhase > 0)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// §121: ручная колонистка стоит без цели, пока игрок не прикажет — это не
    /// «застряла», а ровно то, что он велел.
    /// </summary>
    public static bool IsPlayerDriven(NPCState npc) =>
        Spec121.ManualControlEnabled && npc.Mind.ManualControl;
}

}
