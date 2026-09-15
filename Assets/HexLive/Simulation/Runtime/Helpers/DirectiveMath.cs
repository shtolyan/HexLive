using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §167: указания между колонистками — просьба, согласие, тяга и снятие.
///
/// <para>
/// Решение о согласии ДЕТЕРМИНИРОВАННОЕ (форма <see cref="WearPermissionMath"/>):
/// готовность = база + симпатия + доверие + власть + трудолюбие, без броска,
/// чтобы трасса и повтор сейва сходились. Указание — не приказ: оно лишь
/// добавляет <see cref="Pull"/> к ведущим целям своего вида (§23.12
/// <c>GetCommandBoost</c>), и любая нужда по-прежнему сильнее.
/// </para>
/// </summary>
public static class DirectiveMath
{
    // ── Тема ↔ вид ───────────────────────────────────────────────────────

    public static DirectiveKind? KindOf(TalkTopic topic) => topic switch
    {
        TalkTopic.AskBuild => DirectiveKind.Build,
        TalkTopic.AskStockFood => DirectiveKind.StockFood,
        TalkTopic.AskStockWater => DirectiveKind.StockWater,
        TalkTopic.AskFirewood => DirectiveKind.Firewood,
        _ => null
    };

    public static TalkTopic TopicOf(DirectiveKind kind) => kind switch
    {
        DirectiveKind.Build => TalkTopic.AskBuild,
        DirectiveKind.StockFood => TalkTopic.AskStockFood,
        DirectiveKind.StockWater => TalkTopic.AskStockWater,
        DirectiveKind.Firewood => TalkTopic.AskFirewood,
        _ => TalkTopic.SmallTalk
    };

    public static bool IsAsk(TalkTopic topic) => KindOf(topic) is not null;

    /// <summary>
    /// §167.2: ответ слушательницы на просьбу <paramref name="asker"/> — её
    /// собственная тема на весь разговор. Читается из состояния, поэтому
    /// повтор каждые 30 тиков даёт тот же ответ.
    /// </summary>
    public static TalkTopic AnswerTopic(NPCState listener, NPCState asker, DirectiveKind kind)
    {
        var d = listener.Mind.Directive;
        var pending = listener.Mind.PendingDirective;
        if (d is not null && d.Kind == kind && d.FromId is { } from && from.Equals(asker.Id))
        {
            return TalkTopic.DirectiveYes;
        }

        // Агент ещё думает (§167.8) — молча слушает, общая тема остаётся.
        if (pending is not null && pending.FromId.Equals(asker.Id))
        {
            return TopicOf(kind);
        }

        return TalkTopic.DirectiveNo;
    }

    // ── Просьба 1:1 ──────────────────────────────────────────────────────

    /// <summary>
    /// Просьба на старте разговора. Возвращает true, если указание принято.
    /// Отказы «по делу» (ручная, чужая, самой не до того, уже занята) не портят
    /// отношения; «тебе — нет» — личный отказ по готовности.
    /// </summary>
    public static bool Ask(WorldState world, NPCState asker, NPCState target, DirectiveKind kind)
    {
        if (!Spec167.Enabled || kind == DirectiveKind.None)
        {
            return false;
        }

        asker.Mind.LastDirectiveAskTick = world.Tick;
        SocialCueSignals.Stamp(world, asker, "DirectiveAsk", target.Id);
        Trace.Emit(world, asker.Id, "DirectiveAsked",
            $"Target=NPC{target.Id.Value} Kind={kind}");

        return Decide(world, asker, target, kind, shout: false);
    }

    /// <summary>
    /// §167.8: агент (§160.1) отвечает сам. Сим ставит ожидание и ждёт
    /// <see cref="Respond"/>; по таймауту <see cref="Update"/> решает штатно.
    /// </summary>
    private static bool Decide(WorldState world, NPCState asker, NPCState target,
        DirectiveKind kind, bool shout)
    {
        var tick = world.Tick;
        if (target.Mind.ExternalControl?.IsActive == true)
        {
            target.Mind.PendingDirective = new PendingDirective
            {
                Kind = kind, FromId = asker.Id, SinceTick = tick
            };
            Trace.Emit(world, target.Id, "DirectivePending",
                $"From=NPC{asker.Id.Value} Kind={kind}");
            return false;
        }

        var reason = RefusalByRule(world, asker, target, kind);
        if (reason is null)
        {
            var current = target.Mind.Directive;
            if (current is not null && current.Kind == kind &&
                current.FromId is { } from && from.Equals(asker.Id))
            {
                current.UntilTick = tick + Spec167.DirectiveTicks;
                SocialCueSignals.Stamp(world, target, "DirectiveYes", asker.Id);
                Trace.Emit(world, target.Id, "DirectiveAccepted",
                    $"From=NPC{asker.Id.Value} Kind={kind} Reason=Renewed Until={current.UntilTick}");
                return true;
            }

            var willingness = Willingness(target, asker, kind) - (shout ? Spec167.ShoutPenalty : 0f);
            if (willingness >= Spec167.WillingnessThreshold)
            {
                Accept(world, target, asker.Id, kind);
                Trace.Emit(world, target.Id, "DirectiveAccepted",
                    $"From=NPC{asker.Id.Value} Kind={kind} Willingness={willingness:F2} " +
                    $"Until={target.Mind.Directive!.UntilTick}");
                return true;
            }

            reason = "Dislike";
        }

        SocialCueSignals.Stamp(world, target, "DirectiveNo", asker.Id);
        Trace.Emit(world, target.Id, "DirectiveRefused",
            $"From=NPC{asker.Id.Value} Kind={kind} Reason={reason}");
        return false;
    }

    /// <summary>Отказ по правилу, без обиды; null — правил нет, решает готовность.</summary>
    public static string? RefusalByRule(WorldState world, NPCState asker, NPCState target, DirectiveKind kind)
    {
        if (ManualControlMath.IsManual(target)) return "Manual";
        if (!FactionRelations.AreAllies(asker, target)) return "Faction";
        if (target.Health <= 0f || target.IsUnconscious(world.Tick)) return "Incapacitated";
        if (target.Mind.IsStarving || target.Mind.IsDehydrated ||
            System.Math.Max(target.Needs.Hunger, target.Needs.Thirst) >= Spec167.RefuseNeedThreshold ||
            target.Needs.Energy < Spec167.RefuseEnergyThreshold)
        {
            return "Needed";
        }

        var current = target.Mind.Directive;
        if (current is not null && current.Kind != kind) return "Busy";
        return null;
    }

    /// <summary>Готовность слушательницы выполнить просьбу просящей.</summary>
    public static float Willingness(NPCState target, NPCState asker, DirectiveKind kind)
    {
        var rel = target.Social.GetOrCreate(asker.Id);
        return Spec167.WillingnessBase +
            Spec167.AffinityWeight * rel.Affinity +
            Spec167.TrustWeight * rel.Trust +
            Spec167.AuthorityWeight * rel.Authority +
            Spec167.FamiliarityWeight * rel.Familiarity +
            Spec167.IndustryWeight * (TraitMath.IndustryMult(target) - 1f);
    }

    /// <summary>Принять: записать обещание, поднять власть просящей, показать «да».</summary>
    public static void Accept(WorldState world, NPCState target, EntityId? fromId, DirectiveKind kind)
    {
        target.Mind.Directive = new Directive
        {
            Kind = kind,
            FromId = fromId,
            IssuedTick = world.Tick,
            UntilTick = world.Tick + Spec167.DirectiveTicks
        };
        target.Mind.PendingDirective = null;
        if (fromId is { } from)
        {
            var rel = target.Social.GetOrCreate(from);
            rel.Authority = MathUtil.Clamp01(rel.Authority + Spec167.AuthorityOnAccept);
            SocialCueSignals.Stamp(world, target, "DirectiveYes", from);
        }
    }

    /// <summary>
    /// §167.7 / игрок своим: указание без разговора и без готовности —
    /// «свои принимают всегда». FromId == null, власть не растёт.
    /// </summary>
    public static void Assign(WorldState world, NPCState target, DirectiveKind kind)
    {
        Accept(world, target, null, kind);
        Trace.Emit(world, target.Id, "DirectiveAccepted",
            $"From=Player Kind={kind} Reason=Owned Until={target.Mind.Directive!.UntilTick}");
    }

    /// <summary>§167.8: ответ агента на ожидающую просьбу.</summary>
    public static bool Respond(WorldState world, NPCState target, EntityId fromId,
        DirectiveKind kind, bool accept, out string reason)
    {
        var pending = target.Mind.PendingDirective;
        if (pending is null || pending.Kind != kind || !pending.FromId.Equals(fromId))
        {
            reason = "NoPendingDirective";
            return false;
        }

        target.Mind.PendingDirective = null;
        if (accept)
        {
            Accept(world, target, fromId, kind);
            Trace.Emit(world, target.Id, "DirectiveAccepted",
                $"From=NPC{fromId.Value} Kind={kind} Reason=Agent Until={target.Mind.Directive!.UntilTick}");
        }
        else
        {
            SocialCueSignals.Stamp(world, target, "DirectiveNo", fromId);
            Trace.Emit(world, target.Id, "DirectiveRefused",
                $"From=NPC{fromId.Value} Kind={kind} Reason=Agent");
        }

        reason = string.Empty;
        return true;
    }

    // ── Крик ─────────────────────────────────────────────────────────────

    /// <summary>
    /// §167.2: крик в радиусе — по образцу крика о помощи (§57.9). Каждая
    /// слышащая решает сама, готовность ниже на <see cref="Spec167.ShoutPenalty"/>,
    /// откликов не больше <see cref="Spec167.MaxShoutResponders"/>.
    /// Возвращает число согласившихся.
    /// </summary>
    public static int Shout(WorldState world, NPCState asker, DirectiveKind kind)
    {
        if (!Spec167.Enabled || kind == DirectiveKind.None)
        {
            return 0;
        }

        SocialCueSignals.Stamp(world, asker, "DirectiveShout", null);
        Trace.Emit(world, asker.Id, "DirectiveShout",
            $"Kind={kind} Radius={Spec167.ShoutRadiusTiles}");

        var accepted = 0;
        foreach (var hearer in world.Entities.Npcs.Values)
        {
            if (hearer.Id.Equals(asker.Id) ||
                hearer.Health <= 0f ||
                !FactionRelations.AreAllies(hearer, asker) ||
                hearer.IsUnconscious(world.Tick) ||
                hearer.IsPlayingDead(world.Tick) ||
                hearer.Execution.CurrentInteraction == InteractionType.Sleep ||
                HexSpatialMath.HexDistance(hearer.Tile, asker.Tile) > Spec167.ShoutRadiusTiles)
            {
                continue;
            }

            if (Decide(world, asker, hearer, kind, shout: true))
            {
                accepted++;
                if (accepted >= Spec167.MaxShoutResponders)
                {
                    break;
                }
            }
        }

        return accepted;
    }

    // ── Тяга §23.12 ──────────────────────────────────────────────────────

    /// <summary>
    /// Слагаемое к оценке цели за принятое указание. Ведущие цели — полная
    /// тяга, цепочка сырья — доля; остальным 0f (бит-в-бит как раньше).
    /// </summary>
    public static float Pull(DirectiveKind kind, GoalType goal)
    {
        if (kind == DirectiveKind.None)
        {
            return 0f;
        }

        var feeder = Spec167.Pull * Spec167.FeederPullShare;
        switch (kind)
        {
            case DirectiveKind.Build:
                switch (goal)
                {
                    case GoalType.Build:
                    case GoalType.BuildFurniture:
                        return Spec167.Pull;
                    case GoalType.HarvestTree:
                    case GoalType.SplitLog:
                    case GoalType.ChopCrown:
                    case GoalType.GatherLeaves:
                    case GoalType.GatherFiber:
                    case GoalType.CraftRope:
                    case GoalType.GatherStone:
                    case GoalType.MineBoulder:
                    case GoalType.GatherWood:
                        return feeder;
                }
                break;
            case DirectiveKind.StockFood:
                if (goal is GoalType.GetFood or GoalType.HaulToFire) return Spec167.Pull;
                break;
            case DirectiveKind.StockWater:
                if (goal is GoalType.GetWater or GoalType.StowBottle or GoalType.HaulToFire) return Spec167.Pull;
                break;
            case DirectiveKind.Firewood:
                if (goal is GoalType.GatherWood or GoalType.SplitLog or GoalType.HaulToFire) return Spec167.Pull;
                if (goal == GoalType.HarvestTree) return feeder;
                break;
        }

        return 0f;
    }

    /// <summary>Вид принятого и ещё живого указания; None — нет.</summary>
    public static DirectiveKind Current(NPCState npc) => npc.Mind.Directive?.Kind ?? DirectiveKind.None;

    public static bool IsStocking(DirectiveKind kind) =>
        kind is DirectiveKind.StockFood or DirectiveKind.StockWater or DirectiveKind.Firewood;

    /// <summary>
    /// Несёт ли она в рюкзаке то, что просили запасти. Для воды это ТОЛЬКО
    /// целый кокос: проколотый/вскрытый в руке — её собственное питьё (замер:
    /// стокерша носила свой проколотый кокос как «запас» и пила его).
    /// </summary>
    public static ItemInstance? CarriedStock(WorldState world, NPCState npc, DirectiveKind kind)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (kind == DirectiveKind.StockWater
                    ? item.DefinitionId == ContentIds.Coconut
                    : IsStockOf(world, item.DefinitionId, kind))
            {
                return item;
            }
        }

        return null;
    }

    public static bool IsStockOf(WorldState world, string definitionId, DirectiveKind kind)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def))
        {
            return false;
        }

        return kind switch
        {
            DirectiveKind.StockFood => def.HasTag("Food"),
            DirectiveKind.StockWater => definitionId == ContentIds.Coconut ||
                definitionId == ContentIds.CoconutPierced ||
                definitionId == ContentIds.CoconutOpen,
            DirectiveKind.Firewood => def.HasTag("Wood"),
            _ => false
        };
    }

    public static int StockTarget(DirectiveKind kind) => kind switch
    {
        DirectiveKind.StockFood => Spec167.StockFoodTarget,
        DirectiveKind.StockWater => Spec167.StockWaterTarget,
        DirectiveKind.Firewood => Spec167.StockWoodTarget,
        _ => 0
    };

    /// <summary>Запас лагеря ещё ниже цели — есть смысл нести.</summary>
    public static bool StockWanted(WorldState world, NPCState npc, DirectiveKind kind) =>
        IsStocking(kind) &&
        ColonyQueries.CampStock(world, npc.Faction, kind) < StockTarget(kind);

    /// <summary>
    /// Лежит ли предмет уже «в лагере» — стокерша не должна поднимать то,
    /// что сама только что положила у очага (антипетля §167.5).
    /// </summary>
    public static bool InCampStock(WorldState world, NPCState npc, TileCoord tile) =>
        ColonyQueries.Home(world, npc.Faction) is { } home &&
        HexSpatialMath.HexDistance(home, tile) <= Spec167.StockRadiusTiles;

    // ── Инициатива §167.7 ────────────────────────────────────────────────

    /// <summary>
    /// Сама видит нехватку в лагере и решает попросить. Детерминизм: доля
    /// инициативных — хэш (сид, id, 167, 16701) против InitiativeShare.
    /// Слушательница — воспринятая союзница без указания, к которой можно
    /// подойти, с максимальной Authority ко мне (сначала те, кто уже слушал).
    /// Возвращает false мгновенно, пока выключатель выключен.
    /// </summary>
    public static bool WantsToAsk(WorldState world, NPCState npc,
        out DirectiveKind kind, out EntityId listener) =>
        Initiative(world, npc, out kind, out listener) == AskDecision.Talk;

    public enum AskDecision { None, Talk, Shout }

    /// <summary>
    /// §167.7: Talk — есть свободная слушательница, идём просить лично;
    /// Shout — все заняты (в прототипном мире почти всегда: 92% «своя нужда»,
    /// остальное «нет свободной»), кричим в радиусе прямо из преамбулы
    /// решений, без плана. Кричать можно и занятым — крик не требует, чтобы
    /// её слушали стоя.
    /// </summary>
    public static AskDecision Initiative(WorldState world, NPCState npc,
        out DirectiveKind kind, out EntityId listener)
    {
        kind = DirectiveKind.None;
        listener = default;
        if (!Spec167.AutonomousAsk || !Spec167.Enabled)
        {
            return AskDecision.None;
        }

        // Почему она сейчас НЕ просит — раз в 64 тика, как GroupHuntBlocked:
        // без этого «инициатива молчит» в соаке неотличима от «выключена».
        // Просит только колонистка девичьего лагеря, у которой есть кому
        // просить: чужак-одиночка (Outsiders) кричал в пустоту 4 раза за соак.
        string? blocked =
            !FactionRelations.IsGirlCamp(npc.Faction) || ColonyQueries.LivingCount(world, npc.Faction) < 2 ? "NoCamp" :
            npc.Mind.Directive is not null ? "HoldsDirective" :
            npc.Mind.PendingTalkFrom is not null ? "TalkIncoming" :
            world.Tick - npc.Mind.LastDirectiveAskTick < Spec167.AskCooldownTicks ? "Cooldown" :
            npc.Needs.Hunger >= Spec167.RefuseNeedThreshold || npc.Needs.Thirst >= Spec167.RefuseNeedThreshold ||
                npc.Needs.Energy <= Spec167.RefuseEnergyThreshold ? "OwnNeed" :
            npc.IsFighting ? "Fighting" :
            ManualControlMath.IsManual(npc) ? "Manual" :
            MathUtil.Hash01(world.Seed, npc.Id.Value, 167, 16701) >= Spec167.InitiativeShare ? "NotInitiative" :
            null;

        // Что нужно лагерю: вода важнее еды, еда важнее дров.
        var wanted = DirectiveKind.None;
        if (blocked is null)
        {
            if (ColonyQueries.CampStock(world, npc.Faction, DirectiveKind.StockWater) < Spec167.StockWaterTarget)
                wanted = DirectiveKind.StockWater;
            else if (ColonyQueries.CampStock(world, npc.Faction, DirectiveKind.StockFood) < Spec167.StockFoodTarget)
                wanted = DirectiveKind.StockFood;
            else if (DecisionSystem.FindBuildSite(npc, world) is not null && !IsBuilding(npc))
                wanted = DirectiveKind.Build;
            if (wanted == DirectiveKind.None) blocked = "NothingWanted";
            else if (Holders(world, npc.Faction, wanted) >= Spec167.MaxHoldersPerKind) blocked = "EnoughHolders";
        }

        if (blocked is not null)
        {
            if (SimTrace.Enabled && world.Tick % 64 == 0)
            {
                Trace.Debug(world, npc.Id, "DirectiveAskBlocked", $"Reason={blocked} Wanted={wanted}");
            }

            return AskDecision.None;
        }

        NPCState? best = null;
        var bestAuthority = -1f;
        foreach (var agent in npc.Perception.Agents)
        {
            if (!agent.IsReachable || agent.IsBusy || agent.IsMoving || agent.IsUnconscious ||
                agent.Junction is not { } junction ||
                !world.Entities.Npcs.TryGetValue(agent.Id, out var candidate) ||
                candidate.Mind.Directive is not null ||
                candidate.Mind.PendingTalkFrom is not null ||
                ManualControlMath.IsManual(candidate) ||
                !FactionRelations.AreAllies(npc, candidate) ||
                !PlanningSystem.HasAvailableArmsLengthApproach(world, npc, candidate, junction))
            {
                continue;
            }

            var authority = candidate.Social.Relationships.TryGetValue(npc.Id, out var rel)
                ? rel.Authority
                : 0f;
            if (best is null || authority > bestAuthority ||
                (authority == bestAuthority && candidate.Id.Value < best.Id.Value))
            {
                best = candidate;
                bestAuthority = authority;
            }
        }

        kind = wanted;
        if (best is null)
        {
            return AskDecision.Shout;
        }

        listener = best.Id;
        return AskDecision.Talk;
    }

    private static bool IsBuilding(NPCState npc) =>
        npc.Mind.CurrentGoal is GoalType.Build or GoalType.BuildFurniture;

    /// <summary>Сколько союзниц в лагере уже держат указание этого вида.</summary>
    public static int Holders(WorldState world, Faction faction, DirectiveKind kind)
    {
        var count = 0;
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Health > 0f && other.Faction == faction &&
                other.Mind.Directive is { } d && d.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    // ── Снятие ───────────────────────────────────────────────────────────

    /// <summary>
    /// §167.6: один вызов в преамбуле решений. Снимает указание по времени,
    /// по уходу просившей, по цензу запаса; тайм-аут ожидания агента решает
    /// штатно. Возвращает живой вид (None — нечего смещать).
    /// </summary>
    public static DirectiveKind Update(WorldState world, NPCState npc)
    {
        if (npc.Mind.PendingDirective is { } pending &&
            world.Tick - pending.SinceTick >= Spec167.PendingTimeoutTicks)
        {
            npc.Mind.PendingDirective = null;
            if (world.Entities.Npcs.TryGetValue(pending.FromId, out var asker))
            {
                Decide(world, asker, npc, pending.Kind, shout: false);
            }
        }

        var directive = npc.Mind.Directive;
        if (directive is null)
        {
            return DirectiveKind.None;
        }

        string? done = null;
        string? expired = null;
        if (world.Tick >= directive.UntilTick)
        {
            expired = "TimedOut";
        }
        else if (directive.FromId is { } from &&
                 (!world.Entities.Npcs.TryGetValue(from, out var asker) || asker.Health <= 0f))
        {
            expired = "AskerGone";
        }
        else if (IsStocking(directive.Kind) &&
                 ColonyQueries.CampStock(world, npc.Faction, directive.Kind) >= StockTarget(directive.Kind))
        {
            done = "StockReached";
        }
        else if (directive.Kind == DirectiveKind.Build &&
                 DecisionSystem.FindBuildSite(npc, world) is null &&
                 world.Project is null)
        {
            done = "Built";
        }

        if (done is null && expired is null)
        {
            return directive.Kind;
        }

        npc.Mind.Directive = null;
        if (done is not null)
        {
            if (directive.FromId is { } from)
            {
                var rel = npc.Social.GetOrCreate(from);
                rel.Authority = MathUtil.Clamp01(rel.Authority + Spec167.AuthorityOnDone);
            }

            SocialCueSignals.Stamp(world, npc, "DirectiveDone", directive.FromId);
            Trace.Emit(world, npc.Id, "DirectiveDone",
                $"Kind={directive.Kind} Reason={done}{FromToken(directive)}");
        }
        else
        {
            Trace.Emit(world, npc.Id, "DirectiveExpired",
                $"Kind={directive.Kind} Reason={expired}{FromToken(directive)}");
        }

        return DirectiveKind.None;
    }

    private static string FromToken(Directive d) =>
        d.FromId is { } from ? $" From=NPC{from.Value}" : " From=Player";
}

}
