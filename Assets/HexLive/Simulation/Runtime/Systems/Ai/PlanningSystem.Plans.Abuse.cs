using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §81: дорога до жертвы. Форма украдена у §53 — у похода помочь, — потому что
// это буквально она со стрелкой в другую сторону: там идут к тому, кому плохо, и
// ОТДАЮТ из рюкзака, тут идут к тому, у кого есть, и ЗАБИРАЮТ.
public sealed partial class PlanningSystem
{
    private static void BuildAbusePlan(WorldState world, NPCState npc)
    {
        // Заявку на нас уже подали — стоим и ждём, идти некуда.
        if (npc.Mind.PendingAbuseFrom is not null)
        {
            npc.Plan.Status = PlanStatus.Completed;
            return;
        }

        var mark = ResolveAbuseMark(world, npc);
        if (mark is null)
        {
            // §90: некого трясти ЗДЕСЬ — значит идём искать.
            //
            // Так он ищет кирку и камни: цель есть, объекта под рукой нет —
            // человек идёт туда, где объект бывает. С приключениями было
            // иначе: не нашёл жертву в этот тик — цель сбрасывалась, и он
            // возвращался к быту. Отсюда и «у него общение ноль, а он идёт
            // присесть».
            //
            // Идти есть куда: якорь чужого лагеря — то самое место, где люди
            // заведомо бывают. Тот же ProwlTarget, которым ходит налёт.
            if (TryBuildProwlPlan(world, npc))
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "AbuseProwl",
                        $"Social={npc.Needs.Social:F2} — идёт искать, кого задеть");
                }
                return;
            }

            npc.Plan.Status = PlanStatus.Failed;
            AbandonAbuse(world, npc, "NoMark");
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Abuse NoMark");

            }
            return;
        }

        if (mark.CurrentJunction is not { } markJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonAbuse(world, npc, "MarkOffGrid");
            return;
        }

        // ⭐ §102 r2: УЖЕ ВПЛОТНУЮ — сцена начинается прямо здесь.
        //
        // Раньше это проверялось только для ОДНОГО И ТОГО ЖЕ узла, а стоящий на
        // СОСЕДНЕМ шёл искать подход — которого не существует: подход ищется
        // среди СВОБОДНЫХ соседей её узла, а тот единственный, что рядом, занят
        // им же самим. План проваливался, цель бралась заново, и так вечно.
        //
        // Наружу это и было тем «зависанием»: стоит вплотную к жертве, цель
        // «хочу докопаться», тяга 3.00, здоровье 1.00 — и полчаса ничего.
        // Мерка та же, что у боя: сцена всё равно требует ударной дистанции.
        if (InteractionReach.CanStrike(world, npc, mark))
        {
            // Взаимодействие происходит там, где он СТОИТ: гнать его на её
            // узел незачем, а «прибытие буквальное» в исполнении сверяется
            // именно с этим узлом.
            var here = npc.CurrentJunction ?? markJunction;
            npc.Plan.TargetAgentId = mark.Id;
            npc.Plan.TargetJunctionId = here;
            npc.Plan.TargetTile = mark.Tile;
            ClaimMark(world, npc, mark);
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetJunction = here,
                Interaction = InteractionType.Abuse
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            return;
        }

        // §97: подход БОЕВОЙ, а не разговорный. Прежде бронировался узел «на
        // расстоянии вытянутой руки» (§28.8) — а он бывает и через узел от неё,
        // тогда как удар достаёт только до СОСЕДНЕГО: InteractionReach.CanStrike меряет
        // соседство узлов, а не метры. Пара сцеплялась, а удары не проходили —
        // сцена шла молча, без единого замаха.
        //
        // Берём тот же выбор подхода, что у налёта: соседний свободный узел.
        var approach = PickApproachJunction(world, npc, markJunction);
        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonAbuse(world, npc, "NoApproach");
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", $"Goal=Abuse Mark={mark.Id.Value} NoApproach");

            }
            return;
        }

        npc.Plan.TargetAgentId = mark.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = mark.Tile;
        ClaimMark(world, npc, mark);

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = InteractionType.Abuse
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=Abuse Mark=NPC{mark.Id.Value} Loot={npc.Mind.AbuseHasLoot} " +
                $"Ratio={AbuseMath.Ratio(world, npc, mark):F2}");
        }
    }

    // Держимся ОДНОЙ жертвы, пока она годится: пересчёт на каждом тике заставлял
    // бы его метаться между двумя девушками, не дойдя ни до одной.
    private static NPCState ResolveAbuseMark(WorldState world, NPCState npc)
    {
        if (npc.Mind.AbuseTargetNpcId is { } chosen &&
            world.Entities.Npcs.TryGetValue(chosen, out var current) &&
            current.Health > 0f &&
            !current.IsUnconscious(world.Tick) &&
            !(Spec81.AbuseRespectsSanctuary && MobSystem.IsNpcInSanctuary(world, current)) &&
            // §106: нырнула — коммит рассыпается, в воду он за ней не идёт.
            !(Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, current)) &&
            HexSpatialMath.HexDistance(npc.Tile, current.Tile) <= Spec81.AbuseScanRadiusTiles)
        {
            return current;
        }

        var mark = AbuseMath.BestMark(world, npc, out var hasLoot);
        if (mark is not null)
        {
            npc.Mind.AbuseTargetNpcId = mark.Id;
            npc.Mind.AbuseHasLoot = hasLoot;
            npc.Mind.AbuseBeat = 0;
            npc.Mind.AbuseBlows = 0;
        }

        return mark;
    }

    private static void ClaimMark(WorldState world, NPCState npc, NPCState mark)
    {
        mark.Mind.PendingAbuseFrom = npc.Id;
        SocialCueSignals.Stamp(world, npc, "AbuseDemand", mark.Id);
        SocialCueSignals.Stamp(world, mark, "AbuseThreatened", npc.Id);
    }

    // Снять заявку ОБЯЗАТЕЛЬНО: иначе жертва остаётся помеченной навсегда и её
    // не сможет выбрать никто, включая её собственных собеседников.
    internal static void AbandonAbuse(WorldState world, NPCState npc, string reason)
    {
        if (npc.Mind.AbuseTargetNpcId is { } markId &&
            world.Entities.Npcs.TryGetValue(markId, out var mark) &&
            mark.Mind.PendingAbuseFrom is { } claimed && claimed.Equals(npc.Id))
        {
            mark.Mind.PendingAbuseFrom = null;
        }

        npc.Mind.AbuseTargetNpcId = null;
        npc.Mind.AbuseBeat = 0;
        npc.Mind.AbuseBlows = 0;
        npc.Mind.AbuseHasLoot = false;
        // §89: кулдаун НЕ вешается за срыв. Раньше любая неудача — она отошла,
        // не нашлось подхода — выключала его на 900 тиков, а срывов десятки за
        // прогон: он был выключен почти всё время. Это то же правило, что уже
        // записано для налёта («кулдаун только на настоящую попытку»), просто
        // сюда его не перенесли.
        //
        // Короткая передышка всё же нужна, иначе он будет молотить планами
        // каждый тик по недостижимой цели.
        npc.Mind.AbuseCooldownUntilTick = world.Tick + Spec81.AbuseRetryTicks;
        if (npc.Mind.CurrentGoal == GoalType.Abuse)
        {
            npc.Mind.CurrentGoal = GoalType.None;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "AbuseAbandoned", $"Reason={reason}");

        }
    }
}

}
