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

// Spec §62: far threat detection. Dogs smell a girl at 2 tiles; she SEES the
// dog at Spec62.SpotRadiusTiles and reacts before contact. On a fresh sighting
// a ⚠️ cue pops over her head, then one of two branches:
//   FIT (every body part >= 80%, nothing severed, a real melee weapon she can
//   swing, the threat is alone) — she attacks first: the existing Defend
//   machinery walks her to the mob and the ordinary melee exchange resolves
//   the fight, with her at full strength instead of ambushed mid-haul.
//   UNFIT (wounded, prone, bare-handed, starving, or it's a pack) — the mob's
//   tile goes into danger memory (§29C.4A producer bias) and a route that
//   passes the §62 danger ring is torn up; the rebuild detours via the soft
//   ring cost in HexPathfinder.
// Reactive melee, flee assessment and help cries are untouched — this system
// only ever acts BEFORE the chase starts.
public sealed class ThreatAlertSystem : ISimulationSystem
{
    public string Name => nameof(ThreatAlertSystem);

    public TickLayer Layer => TickLayer.Medium;

    // Re-warn gate per (girl, mob). Deliberately transient system state, NOT
    // NPCState — the save format stays untouched; a loaded save at worst
    // re-pops one ⚠️ per pair.
    private readonly System.Collections.Generic.Dictionary<long, int> _lastCueTick = new();

    // §72: a SECOND cache for hostile people. The mob key packs the mob id into
    // the low word, so dog 3 and NPC 3 would collide and one sighting would
    // silently suppress the other's warning.
    private readonly System.Collections.Generic.Dictionary<long, int> _lastHostileCueTick = new();

    public void Run(WorldState world)
    {
        if (!Spec62.ThreatAlertEnabled)
        {
            return;
        }

        // §72: hostile PEOPLE are spotted by the same layer, so an empty island
        // of dogs is no longer a reason to skip the pass.
        var watchesHostiles = Spec72.Enabled;
        if (world.Mobs.Count == 0 && !watchesHostiles)
        {
            if (_lastCueTick.Count > 0)
            {
                _lastCueTick.Clear();
            }

            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f ||
                npc.IsUnconscious(world.Tick) ||
                npc.IsPlayingDead(world.Tick) || // §105.14: она уже «мертва» — не реагирует
                npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                npc.IsFighting ||
                npc.Mind.CurrentGoal == GoalType.Flee ||
                npc.Mind.CurrentGoal == GoalType.Defend ||
                MobSystem.IsNpcInSanctuary(world, npc) ||
                // §106: из воды не бывает ни attack-first, ни обхода — она в
                // убежище, и Defend-план лишь вытащил бы её из него на клыки.
                (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, npc)))
            {
                continue;
            }

            // Nearest live threat in sight, plus how many share the radius —
            // a pack in view is never a first-strike target.
            Wildlife.MobState threat = null;
            var bestDistance = int.MaxValue;
            var pack = 0;
            foreach (var mob in world.Mobs)
            {
                if (mob.Health <= 0f)
                {
                    continue;
                }

                // §125.4: далеко ли она замечает зверя — её собственный
                // радиус восприятия, а не общая для всех константа.
                var distance = HexSpatialMath.HexDistance(npc.Tile, mob.Tile);
                if (distance > PerceptionMath.RadiusTiles(npc))
                {
                    continue;
                }

                pack++;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    threat = mob;
                }
            }

            // §72: the same look-out sees an approaching STRANGER. Deliberately
            // resolved after the mob scan and handled separately: a wolf can be
            // charged first (§62), a person never is.
            // §108: охотница идёт к нему НАРОЧНО — ей не от кого уворачиваться.
            // Без этого её же собственный дозор гнул бы ей маршрут вокруг цели
            // (DangerRing) и группа кружила бы рядом, не доходя.
            var hunting = npc.Mind.CurrentGoal == GoalType.GroupHunt;
            if (watchesHostiles && !hunting && ScanForHostile(world, npc) is { } hostile)
            {
                var hostileKey = ((long)npc.Id.Value << 32) | (uint)hostile.Id.Value;
                if (!_lastHostileCueTick.TryGetValue(hostileKey, out var hostileSeenTick) ||
                    world.Tick - hostileSeenTick >= Spec72.StrangerCueCooldownTicks)
                {
                    _lastHostileCueTick[hostileKey] = world.Tick;
                    // §80: свой вид кьюшки и id ТОГО, КОГО она увидела. Раньше
                    // здесь стояло `npc.Id` — её собственный id, — и канал «о ком
                    // кьюшка» на страхе был пуст, хотя `hostile` лежит рядом.
                    // Отдельный вид от звериного нужен и виду (над головой
                    // всплывает лицо чужака, а у зверя иконка), и голосу:
                    // «человек на горизонте» звучит не так, как «волк».
                    SocialCueSignals.Stamp(world, npc, "DangerStranger", hostile.Id);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "HostileSpotted",
                            $"Npc={hostile.Id.Value} " +
                            $"Dist={HexSpatialMath.HexDistance(npc.Tile, hostile.Tile)} " +
                            $"Fit={IsFitToFight(npc)} FirstStrike=suppressed");
                    }

                    // NEVER StartFirstStrike. The girls do not open hostilities:
                    // partly because the rally scene only lands if he is
                    // unambiguously the aggressor, and partly because the whole
                    // §57 assist machinery keys on "who is the attacker" — if she
                    // swings first, SHE is, and once the outsiders are more than
                    // one THEIR rally would fire against her.
                    //
                    // §108 is the ONE sanctioned exception, and it buys its way
                    // out of both objections: it is a deliberate collective
                    // decision (so nobody needs rallying to it), and it accepts
                    // the consequence — with more than one outsider, his side
                    // rallying against the party is correct §57 semantics, not
                    // a bug. A girl on that goal never reaches this branch.
                    AvoidHostile(world, npc, hostile);
                }
            }

            if (threat is null)
            {
                continue;
            }

            var key = ((long)npc.Id.Value << 32) | (uint)threat.Id;
            if (_lastCueTick.TryGetValue(key, out var lastTick) &&
                world.Tick - lastTick < Spec62.CueCooldownTicks)
            {
                continue;
            }

            _lastCueTick[key] = world.Tick;
            PruneStaleCues(world.Tick);

            // §80: зверь — не человек, лица у него в кэше портретов нет, и его
            // id живёт в другом пространстве (3 — это волк, а не Марта).
            // Раньше сюда шёл id самой кричащей, что прочиталось бы как «боится
            // себя»; null оставляет прежнюю иконку зверя.
            SocialCueSignals.Stamp(world, npc, "DangerSpotted:" + threat.MobId, null);
            // §126: черта сдвигает планку «достаточно ли она цела» (внутри
            // IsFitToFight), но НЕ трогает потолок стаи: «первой бьём только
            // одиночку» — правило выживания §62, и храбрость его не отменяет.
            // Соак, где отменяла, стоил колонии всех четверых.
            var fit = IsFitToFight(npc) && pack <= Spec62.AttackMaxPack;
            Trace.Emit(world, npc.Id, "ThreatSpotted",
                $"Mob={threat.Id} Dist={bestDistance} Pack={pack} Fit={fit} " +
                $"WorstPart={MobSystem.WorstPartHealth(npc):F2}");

            if (fit)
            {
                StartFirstStrike(world, npc, threat);
            }
            else
            {
                AvoidThreat(world, npc, threat);
            }
        }
    }

    // §62 fitness: "no significant wounds" = every body part at 80%+ and no
    // stump, "armed" = a melee weapon she can actually swing right now (fists
    // never qualify; a spear with one hand doesn't either — BestMeleeWeapon
    // already skips two-handed gear she can't hold). Starving or dehydrated
    // girls have bigger problems than picking fights.
    internal static bool IsFitToFight(NPCState npc)
    {
        if (npc.Body.AnySevered ||
            !npc.Body.CanUseToolsOrWeapons ||
            npc.Mind.IsStarving ||
            npc.Mind.IsDehydrated ||
            // §126: у храброй планка «достаточно цела, чтобы драться» ниже.
            MobSystem.WorstPartHealth(npc) < TraitMath.FitBoneHealth(npc))
        {
            return false;
        }

        var weaponId = SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.WeaponHands);
        return Content.GearCatalog.For(weaponId).MeleePriority > 0;
    }

    // The pre-emptive attack rides the help-cry Defend machinery unchanged:
    // BuildDefendPlan walks her to a junction adjacent to the mob, the dog
    // aggros on approach, and the standard melee exchange (where she is the
    // healthy, armed side) settles it.
    private static void StartFirstStrike(WorldState world, NPCState npc, Wildlife.MobState threat)
    {
        // §121: напасть первой на замеченного зверя — решение, и у ручной его
        // принимает игрок (он видит собаку раньше и решает, драться или уйти).
        if (ManualControlMath.IsManual(npc))
        {
            return;
        }

        if (!CombatHelpSystem.CanReachAttacker(world, npc, threat.Id, null))
        {
            AvoidThreat(world, npc, threat);
            return;
        }

        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress ||
            npc.IsCarryingPerson)
        {
            PlanInterruption.TryAbortForCombat(
                world, npc, InterruptionCause.ThreatFirstStrike, $"Attacking spotted dog {threat.Id} first");
        }

        npc.Mind.CurrentGoal = GoalType.Defend;
        npc.Mind.GoalLock = new GoalLock
        {
            Goal = GoalType.Defend,
            StartTick = world.Tick,
            EndTick = world.Tick + Spec62.AttackLockTicks
        };
        npc.Mind.CombatAssistDogId = threat.Id;
        npc.Mind.CombatAssistAttackerNpcId = null;
        npc.Mind.PendingTalkFrom = null;
        npc.Mind.PendingAidFrom = null;

        // §52: both hands on the spear before the charge, not at first blood.
        if (npc.Body.IntactHands >= 2 &&
            Content.GearCatalog.For(SimBalance.BestMeleeWeapon(
                npc.Inventory.Items, npc.Body.WeaponHands)).TwoHanded)
        {
            MobSystem.ReadySpearHands(world, npc);
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ThreatAttack",
                $"Mob={threat.Id} first strike (fit and armed)");
        }
    }

    private static void AvoidThreat(WorldState world, NPCState npc, Wildlife.MobState threat)
    {
        MobSystem.RememberDangerAt(world, npc, threat.Tile);
        if (npc.Plan.Status != PlanStatus.Active)
        {
            return;
        }

        // Tear up a route that passes the danger ring; the rebuild pathfinds
        // with the §62 soft cost and detours. Only on the fresh sighting (the
        // cue gate above), so a genuinely unavoidable crossing is not aborted
        // again every medium tick.
        var ring = PathfindingSystem.DangerRing(world);
        for (var i = npc.Movement.PathIndex; i < npc.Movement.JunctionPath.Count; i++)
        {
            if (!ring.Contains(npc.Movement.JunctionPath[i]))
            {
                continue;
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ThreatAvoid",
                    $"Mob={threat.Id} Tile={threat.Tile.Q},{threat.Tile.R} rerouting");
            }
            // §121.5: у ручной обход угрозы не смеет рвать приказ — политика
            // откажет. Перестраиваем маршрут мягко, как у несущей человека
            // (§124): пустой JunctionPath — просьба к PathfindingSystem
            // проложить путь заново, обход зверя у него уже в цене (§62).
            if (!PlanInterruption.TryAbort(world, npc, InterruptionCause.ThreatReroute,
                    $"Route passes spotted dog {threat.Id} — rerouting"))
            {
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;
                npc.Movement.IsMoving = false;
                npc.Movement.SetStatus(MovementStatus.Idle);
            }

            return;
        }
    }

    // The nearest live hostile within sight. Sanctuary is not consulted: seeing
    // him from indoors is exactly when you most want the warning.
    //
    // §125.6: берётся ИЗ ВОСПРИЯТИЯ, а не перебором острова. Список Hostiles
    // уже отфильтрован её радиусом и её фракцией — PerceptionSystem прошёл
    // кольцо в начале этого же medium-тика. Свой замер дистанции здесь был бы
    // вторым мнением о том же вопросе.
    private static NPCState ScanForHostile(WorldState world, NPCState npc)
    {
        NPCState nearest = null;
        var bestDistance = int.MaxValue;
        foreach (var seen in npc.Perception.Hostiles)
        {
            if (!world.Entities.Npcs.TryGetValue(seen.Id, out var other) ||
                other.Health <= 0f)
            {
                continue;
            }

            // Ничьи решает id, а не порядок обхода: список отсортирован по нему.
            var distance = HexSpatialMath.HexDistance(npc.Tile, other.Tile);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = other;
            }
        }

        return nearest;
    }

    private static void AvoidHostile(WorldState world, NPCState npc, NPCState hostile)
    {
        MobSystem.RememberDangerAt(world, npc, hostile.Tile);
        if (npc.Plan.Status != PlanStatus.Active)
        {
            return;
        }

        var ring = PathfindingSystem.HostileRing(world, npc.Faction);
        for (var i = npc.Movement.PathIndex; i < npc.Movement.JunctionPath.Count; i++)
        {
            if (!ring.Contains(npc.Movement.JunctionPath[i]))
            {
                continue;
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "HostileAvoid",
                    $"Npc={hostile.Id.Value} Tile={hostile.Tile.Q},{hostile.Tile.R} rerouting");
            }
            // ⭐ НЕСУЩУЮ ЧЕЛОВЕКА НЕ СНОСИМ. Abort — это полный разбор плана, а
            // его разбор КЛАДЁТ ношу на землю. Получалось: колонистка несёт
            // подругу в кровать, её маршрут задевает кольцо вокруг чужака —
            // она роняет подругу и планирует заново, на следующем тике
            // поднимает и роняет опять. Замер (seed 476005489, 16 000 тиков):
            // 4 переноски, 0 донесённых, и ДВЕ обронены этой строкой — ровно
            // то «тупят на ровном месте у дома», на что жаловался игрок.
            // Перестроить маршрут можно и не разбирая план: пустой
            // JunctionPath — это и есть просьба к PathfindingSystem проложить
            // путь заново, а обход чужака у него уже в цене (HostileRing как
            // danger). Для не несущей поведение прежнее, байт-в-байт.
            if (npc.IsCarryingPerson)
            {
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;
                npc.Movement.IsMoving = false;
                npc.Movement.SetStatus(MovementStatus.Idle);
                return;
            }

            // §121.5: и у ручной без ноши приказ не рвём — тот же мягкий
            // перепроклад, что строкой выше у несущей.
            if (!PlanInterruption.TryAbort(world, npc, InterruptionCause.ThreatReroute,
                    $"Route passes the outsider NPC{hostile.Id.Value} — rerouting"))
            {
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;
                npc.Movement.IsMoving = false;
                npc.Movement.SetStatus(MovementStatus.Idle);
            }

            return;
        }
    }

    private void PruneStaleCues(int tick)
    {
        if (_lastCueTick.Count <= 64)
        {
            return;
        }

        var stale = new System.Collections.Generic.List<long>();
        foreach (var pair in _lastCueTick)
        {
            if (tick - pair.Value >= Spec62.CueCooldownTicks)
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var key in stale)
        {
            _lastCueTick.Remove(key);
        }
    }
}

}
