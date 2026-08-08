using System.Collections.Generic;
using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// §108: арифметика сговора. «Кто стоит рядом», «о ком речь», «согласны ли
// все» — три вопроса, ответы на которые нужны и разговору (выбрать тему), и
// охоте (раздать цель), поэтому они здесь, а не в одном из них.
//
// Собственного объекта «группа» нет НАРОЧНО. Связь держится тем, что у всех
// участниц одна и та же GroupHuntTargetNpcId: тогда распад группы ничего не
// стоит (умерла, отстала, увели — просто выбывает из выборки), а сохранение и
// провод не узнают о новой сущности вовсе.
public static class GroupHuntMath
{
    // Кто стоит в кружке вокруг НЕЁ и способен на сговор. Включает саму npc.
    // Форма — RaidMath.AlliesAround, но с боевыми исключениями: дерущаяся или
    // бегущая в этом разговоре не участвует.
    public static void GatheredGirls(WorldState world, NPCState npc, List<NPCState> into)
    {
        into.Clear();
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Health <= 0f ||
                !FactionRelations.AreAllies(other, npc) ||
                other.IsUnconscious(world.Tick) ||
                other.IsPlayingDead(world.Tick) || // §105.14: лежачую в сговор не берут
                other.Body.IsProne ||
                other.IsFighting ||
                other.Mind.CurrentGoal == GoalType.Flee ||
                other.Execution.CurrentInteraction == InteractionType.Sleep ||
                HexSpatialMath.HexDistance(other.Tile, npc.Tile) >
                    Spec108.GroupHuntGatherRadiusTiles)
            {
                continue;
            }

            into.Add(other);
        }

        // Порядок — по id, а не по обходу словаря: список попадает в трассу и
        // в сравнение «кто дальше всех», и реплей обязан совпасть.
        into.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
    }

    // О ком тут вообще говорить: живой чужак, которого эти двое знают с худшей
    // стороны. При нескольких чужаках выбирается самый ненавидимый (среднее по
    // паре), ничьи — по меньшему id.
    public static NPCState MostHatedStranger(WorldState world, NPCState a, NPCState b)
    {
        NPCState worst = null;
        var worstAffinity = 0f;
        foreach (var candidate in world.Entities.Npcs.Values)
        {
            if (candidate.Health <= 0f ||
                !FactionRelations.AreHostile(candidate, a))
            {
                continue;
            }

            var mean = 0.5f * (
                a.Social.GetOrCreate(candidate.Id).Affinity +
                b.Social.GetOrCreate(candidate.Id).Affinity);
            if (worst is null || mean < worstAffinity ||
                (mean == worstAffinity && candidate.Id.Value < worst.Id.Value))
            {
                worst = candidate;
                worstAffinity = mean;
            }
        }

        return worst;
    }

    // Может ли тема «чужак» вообще выпасть в этом разговоре. Тема — ещё не
    // сговор: говорить о нём можно и при лёгкой неприязни, идти бить —
    // только единогласно (см. PactHolds).
    public static bool TopicAvailable(
        WorldState world, NPCState npc, NPCState target, List<NPCState> gathered,
        out NPCState stranger, out float hate)
    {
        stranger = null;
        hate = 0f;
        if (!Spec108.GroupHuntEnabled)
        {
            return false;
        }

        stranger = MostHatedStranger(world, npc, target);
        if (stranger is null)
        {
            return false;
        }

        GatheredGirls(world, npc, gathered);
        if (gathered.Count < Spec108.GroupHuntMinGirls)
        {
            return false;
        }

        // Ненависть пары — то, что делает тему вероятной. Тёплая пара о нём
        // тоже может заговорить, просто с базовым весом.
        var mean = 0.5f * (
            npc.Social.GetOrCreate(stranger.Id).Affinity +
            target.Social.GetOrCreate(stranger.Id).Affinity);
        hate = MathUtil.Clamp01(-mean);
        return mean < 0f;
    }

    // Единогласие. Одна тёплая (или просто равнодушная) — и сговора нет:
    // «идём бить его» это не решение большинства, это решение ВСЕХ, кто стоит
    // в кружке, иначе половина группы разойдётся по своим делам на первом же
    // перепланировании и «держаться вместе» станет ложью.
    public static bool PactHolds(
        WorldState world, List<NPCState> gathered, NPCState stranger, out string blocked)
    {
        blocked = string.Empty;
        if (!Spec108.GroupHuntEnabled || stranger is null || stranger.Health <= 0f)
        {
            blocked = "NoStranger";
            return false;
        }

        if (gathered.Count < Spec108.GroupHuntMinGirls)
        {
            blocked = $"TooFew={gathered.Count}";
            return false;
        }

        // §106: в воде его не достать — вода одинаково укрывает и жертву, и
        // обидчика. Сговор просто не складывается; тема выпадет ещё раз.
        if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, stranger))
        {
            blocked = "Swimming";
            return false;
        }

        // Лежачего не бьют. И не только из приличия: без этого сговор против
        // уже отключённого завершался успехом в тот же тик, охота считалась
        // «удавшейся», группа получала расплату и уходила в кулдаун, ни разу
        // никого не ударив (арена 313: пакт на 55983, GroupHuntDone на 55984).
        // §105.14: и притворившийся мёртвым — лежачий; сговор против него не
        // складывается (тот же гейт «не наводиться», что у зверя и налётчика).
        if (stranger.IsUnconscious(world.Tick) || stranger.Body.IsProne ||
            stranger.IsPlayingDead(world.Tick))
        {
            blocked = "AlreadyDown";
            return false;
        }

        foreach (var girl in gathered)
        {
            var affinity = girl.Social.GetOrCreate(stranger.Id).Affinity;
            if (affinity > Spec108.GroupHuntHateThreshold)
            {
                blocked = $"NPC{girl.Id.Value}Aff={affinity:F2}";
                return false;
            }

            if (world.Tick < girl.Mind.GroupHuntCooldownUntilTick)
            {
                blocked = $"NPC{girl.Id.Value}Cooldown";
                return false;
            }

            if (HexSpatialMath.HexDistance(girl.Tile, stranger.Tile) >
                Spec108.GroupHuntMaxPactDistanceTiles)
            {
                blocked = "TooFar";
                return false;
            }

            // Идти бить некому, если руки заняты своей бедой.
            if (girl.Mind.IsStarving || girl.Mind.IsDehydrated ||
                !girl.Body.CanUseToolsOrWeapons)
            {
                blocked = $"NPC{girl.Id.Value}Unfit";
                return false;
            }

            // §121: ручную в сговор не зовут — за неё решает игрок. Проверка
            // стоит в PactHolds, а не в TryFormPact, чтобы сговор не сложился
            // ВООБЩЕ: иначе двое ушли бы бить чужака, а третья, ручная,
            // осталась стоять — группа, рассыпавшаяся в момент рождения.
            if (Runtime.ManualControlMath.IsManual(girl))
            {
                blocked = $"NPC{girl.Id.Value}Manual";
                return false;
            }
        }

        return true;
    }

    // ⭐ Сговор — ПРЕРЫВАНИЕ, а не ставка на аукционе. Ровно та же форма, что у
    // клича §57, первого удара §62, налёта §72.9 и абьюза §87: аукцион
    // переигрывается только между взаимодействиями, а разговор — это как раз
    // взаимодействие, так что ставку никто бы и не спросил.
    public static bool TryFormPact(
        WorldState world, List<NPCState> gathered, NPCState stranger)
    {
        if (!PactHolds(world, gathered, stranger, out var blocked))
        {
            if (world.Tick % 64 == 0 && gathered.Count > 0)
            {
                Trace.Emit(world, gathered[0].Id, "GroupHuntBlocked", $"Reason={blocked}");
            }

            return false;
        }

        var names = string.Empty;
        foreach (var girl in gathered)
        {
            names += (names.Length == 0 ? string.Empty : ",") + "NPC" + girl.Id.Value;
        }

        foreach (var girl in gathered)
        {
            if (girl.Plan.Status == PlanStatus.Active ||
                girl.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, girl,
                    $"Going after NPC{stranger.Id.Value} with the others");
            }

            // Счёт побоев — на НЁМ, и обнуляется он здесь, на сговоре: иначе
            // прошлая расправа засчиталась бы новой (§108.5).
            stranger.Mind.GroupHuntBlowsTaken = 0;

            girl.Mind.CurrentGoal = GoalType.GroupHunt;
            girl.Mind.GroupHuntTargetNpcId = stranger.Id;
            girl.Mind.GroupHuntStartedTick = world.Tick;
            // Мерка отступления — разница, а не абсолют (§108.5).
            girl.Mind.GroupHuntStartHealth = girl.Health;
            girl.Mind.GroupHuntStartWorstPart = MobSystem.WorstPartHealth(girl);
            girl.Mind.GoalLock = new GoalLock
            {
                Goal = GoalType.GroupHunt,
                StartTick = world.Tick,
                EndTick = world.Tick + Spec108.GroupHuntLockTicks
            };
            girl.Mind.PendingTalkFrom = null;
            girl.Mind.PendingAidFrom = null;
            girl.Execution.CurrentTalkTopic = null;
            girl.Execution.CurrentTalkTopicPeerId = null;
            // Портрет ТОГО, о ком сговорились, всплывает над каждой — включая
            // третью, которая ни с кем не разговаривала: канал кьюшек несёт
            // peerId, и вид уже умеет рисовать по нему лицо (§80).
            SocialCueSignals.Stamp(world, girl, "GroupHuntPact", stranger.Id);
            Trace.Emit(world, girl.Id, "GroupHuntPactFormed",
                $"Target=NPC{stranger.Id.Value} " +
                $"Affinity={girl.Social.GetOrCreate(stranger.Id).Affinity:F2} " +
                $"Party={gathered.Count} With=[{names}] " +
                $"Dist={HexSpatialMath.HexDistance(girl.Tile, stranger.Tile)}");
        }

        return true;
    }

    // Сколько участниц сговора ещё на ногах и идут за той же головой.
    public static int PartyAlive(WorldState world, EntityId strangerId)
    {
        var alive = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.CurrentGoal == GoalType.GroupHunt &&
                npc.Mind.GroupHuntTargetNpcId is { } target &&
                target.Equals(strangerId) &&
                npc.Health > 0f &&
                !npc.IsUnconscious(world.Tick) &&
                !npc.Body.IsProne)
            {
                alive++;
            }
        }

        return alive;
    }

    // Насколько далеко от цели САМАЯ ОТСТАВШАЯ. Это и есть темп группы:
    // передние равняются на неё, а она — ни на кого, поэтому взаимного
    // ожидания («после вас») случиться не может.
    public static int RearGuardDistance(WorldState world, EntityId strangerId, TileCoord targetTile)
    {
        var worst = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.CurrentGoal != GoalType.GroupHunt ||
                npc.Mind.GroupHuntTargetNpcId is not { } target ||
                !target.Equals(strangerId) ||
                npc.Health <= 0f ||
                npc.IsUnconscious(world.Tick))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(npc.Tile, targetTile);
            if (distance > worst)
            {
                worst = distance;
            }
        }

        return worst;
    }
}

}
