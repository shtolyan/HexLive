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

public sealed partial class DecisionSystem : ISimulationSystem
{
    public string Name => nameof(DecisionSystem);

    public TickLayer Layer => TickLayer.Medium;

    // Spec 23.17 / 23.8: Starving hysteresis and emergency boost.
    private static float StarvingEnterThreshold => SimBalance.StarvingEnterThreshold;

    private static float StarvingClearThreshold => SimBalance.StarvingClearThreshold;

    private static float StarvingBoost => SimBalance.StarvingBoost;

    // Spec 23.8–23.10 (iteration 3): goal stability.
    private static int GoalLockTicks => AiBalance.GoalLockTicks;

    private static float LockOverrideDelta => AiBalance.LockOverrideDelta;

    private static float SwitchDelta => AiBalance.SwitchDelta;

    // Spec 28.8/28.15A: how long an invited NPC waits for the initiator.
    private static int TalkWaitTimeoutTicks => AiBalance.TalkWaitTimeoutTicks;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // Besieged-starve fix (Jul 2026): refresh the starvation/dehydration
            // flags at the TOP of the loop, BEFORE the IsFighting gate below.
            // They used to be set further down (after the gate), so a girl pinned
            // IsFighting by a wolf that can't close the last junction never
            // re-flagged as starving — the escape valve read a stale `false` and
            // she starved/bled where she stood (seed 308477163: Jana, day 4.79,
            // Hunger=Thirst=1.00, wolf still at full health). The blunt
            // "IsFighting -> continue" gate that used to sit here is GONE; the
            // refined gate further down (which lets a starving/dehydrated fighter
            // reopen her survival-only auction) now governs on its own.
            UpdateStarvingStatus(world, npc);
            UpdateDehydratedStatus(world, npc);

            // Spec §60: comatose — the body lies as if dead; recovery runs in
            // NeedsDecaySystem (sleep rules) and the wake check lives there
            // too. No decisions of any kind while out.
            // §105: то же и для умирающей — она лежит и ждёт, спасут её или
            // нет; решать ей нечего. Обратный отсчёт крутит NeedsDecaySystem.
            if (npc.Mind.ComaCause != ComaCause.None || npc.IsDying)
            {
                continue;
            }

            // Spec 40.13: unconscious — lie helpless, recovering a little
            // stamina, until the body comes to. No decisions while out.
            if (world.Tick < npc.Mind.FaintedUntilTick)
            {
                npc.Needs.Stamina = MathUtil.Clamp01(npc.Needs.Stamina + 0.02f);
                continue;
            }

            // Spec §110: crying her heart out — conscious, but no decisions
            // until she is done. The body rests exactly like the faint above.
            //
            // ⭐ Стресс здесь НЕ трогаем. Первая редакция вычитала
            // StressDownRate прямо тут — и это была ошибка масштаба: ставка
            // отмерена НА МЕДЛЕННЫЙ ТИК (16 обычных), а решения крутятся каждый
            // тик, так что спад шёл в 16 раз быстрее задуманного и сбрасывал
            // стресс с 0.95 в ноль за восемь секунд. Наблюдалось это как «она
            // лежит и рыдает, а полоска стресса пустая и зелёная» — вид не
            // врал, стресса действительно уже не было. Спад ей и так идёт: пока
            // она лежит, ни одного повода из stressUp нет, и NeedsDecaySystem
            // сама снимает по StressDownRate за медленный тик — за 240 тиков
            // плача это примерно −0.45, то есть «потихонечку приходит в норму».
            if (world.Tick < npc.Mind.CryingUntilTick)
            {
                npc.Needs.Stamina = MathUtil.Clamp01(npc.Needs.Stamina + 0.02f);
                continue;
            }

            // Spec 41.5: just woke up — stand where you slept and come to
            // your senses; goals wait out the grace.
            if (world.Tick < npc.Mind.WakeGraceUntilTick)
            {
                continue;
            }

            // Spec 29C.4A: nothing outbids running for your life.
            if (npc.Mind.CurrentGoal == GoalType.Flee && npc.Plan.Status == PlanStatus.Active)
            {
                continue;
            }

            // Behavior audit (Jul 2026): while a mob is actively trading blows
            // with her, the errand auction stays CLOSED — the old code re-picked
            // GatherTools mid-mauling and she walked off collecting a hammer
            // while the dog ate her (seed 999: 12 wounds, head destroyed, goal
            // never left GatherTools). IsFighting is recomputed by MobSystem
            // every medium tick, so the gate can't stick after the fight ends;
            // the flee assessment (health/pack thresholds) still runs there.
            // Starvation/dehydration crack the gate back open — a standoff
            // against a dog that can't close (ledge, blocked path) must never
            // out-starve the girl it besieges (iter-4 freeze, seed 999). This is
            // now the SOLE IsFighting gate (the blunt duplicate above was removed)
            // and it reads freshly-updated flags, so the exception actually fires
            // instead of being dead code behind the blunt gate + a stale flag.
            if (npc.IsFighting && !npc.Mind.IsStarving && !npc.Mind.IsDehydrated)
            {
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Defend &&
                (npc.Plan.Status == PlanStatus.Active ||
                 npc.Mind.CombatAssistDogId.HasValue ||
                 npc.Mind.CombatAssistAttackerNpcId.HasValue))
            {
                continue;
            }

            // §108: сговор держится, пока цель жива и не вышел бюджет. Аукцион
            // закрыт для НЕЁ, а не для группы: снять её оттуда может только
            // конец охоты (GroupHuntSystem.EndHunt) — иначе трое разошлись бы
            // по своим делам поодиночке и «идут вместе» распалось бы на первом
            // же перепланировании. Голод и жажда всё равно рвут охоту раньше:
            // их проверяет PactHolds на входе и EndHunt по бюджету.
            if (npc.Mind.CurrentGoal == GoalType.GroupHunt &&
                npc.Mind.GroupHuntTargetNpcId.HasValue)
            {
                continue;
            }

            // Spec 29C.9: while an action is genuinely underway, don't
            // re-decide. Gradual needs (iter 30) drain the executing goal's
            // OWN score every tick (eating lowers Hunger -> Eat's score
            // falls), which otherwise flips the goal mid-action and explodes
            // the interrupt count (seed 31337: 782). Real emergencies still
            // break in: a threatening dog sets Flee reactively via the fear
            // path (handled above), and starvation/dehydration interrupt on
            // the very next decision once the short action completes. Actions
            // are <= 100 ticks, so deferring is imperceptible.
            if (npc.Execution.Status == ExecutionStatus.InProgress)
            {
                continue;
            }

            var previousGoal = npc.Mind.CurrentGoal;
            npc.Mind.LastScores.Clear();
            npc.Mind.Cooldowns.RemoveAll(c => c.EndTick <= world.Tick);
            // The danger mark ages out on the same TTL as the rest of the
            // memory (was a bare 2400 literal that silently duplicated it —
            // MeatRawSpoilTicks outlives it by design, see SimBalance).
            npc.Memory.Dangers.RemoveAll(d => world.Tick - d.Tick > AiBalance.MemoryTtlTicks);

            // (UpdateStarvingStatus/UpdateDehydratedStatus now run at the top of
            // the loop — see the besieged-starve fix — so the IsFighting escape
            // valve reads fresh flags.)
            UpdateOverheatedStatus(world, npc);
            var bleedingCrisis = IsBleedingCrisis(npc);
            var emergencyBoost = npc.Mind.IsStarving ? StarvingBoost : 0f;
            var drinkBoost = npc.Mind.IsDehydrated ? StarvingBoost : 0f;
            // §65: dead-tired → a decisive pull to bed down at the fireside
            // before the body collapses at the work site (§60). Gated to genuine
            // exhaustion so it never outbids ordinary chores until she truly
            // needs sleep; sleepAvail (below) opens the moderate-hunger gate in
            // the same regime, and starving/dehydrated/danger still block it.
            var deadTiredBoost = Spec49.DeadTiredSeek && npc.Needs.Energy < Spec49.DeadTiredEnergy
                ? Spec49.DeadTiredSleepBoost : 0f;

            // Spec 28.15C: discovering a body triggers grief on sight.
            foreach (var perceived in npc.Perception.Objects)
            {
                if (!perceived.FromMemory &&
                    world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var perceivedDef) &&
                    perceivedDef.Tags.Contains("Corpse") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var corpseObject))
                {
                    GriefSystemHelpers.TriggerGrief(world, npc, corpseObject);
                }
            }
            var isGrieving = world.Tick < npc.Mind.GrievingUntilTick;

            // Self-heal a stale talk invitation: valid only while the initiator
            // still exists and still targets this NPC.
            if (npc.Mind.PendingTalkFrom is { } fromId &&
                (!world.Entities.Npcs.TryGetValue(fromId, out var initiator) ||
                 initiator.Plan.TargetAgentId is not { } initiatorTarget ||
                 !initiatorTarget.Equals(npc.Id)))
            {
                npc.Mind.PendingTalkFrom = null;
            }

            // Spec 28.8: an accepted invitation means waiting in place until
            // the initiator arrives. Emergencies and timeouts break the wait.
            if (npc.Mind.PendingTalkFrom is { } waitingFor)
            {
                // §64.9: thirst kills faster than hunger on this island, and the
                // wait only ever broke on IsStarving — a girl at Thirst 1.00
                // stood still for the invite. Both life bands break it now.
                if (npc.Mind.IsStarving || npc.Mind.IsDehydrated)
                {
                    npc.Mind.PendingTalkFrom = null;
                }
                else if (world.Tick - npc.Mind.PendingTalkSinceTick > TalkWaitTimeoutTicks)
                {
                    npc.Mind.PendingTalkFrom = null;
                    Trace.Emit(world, npc.Id, "TalkWaitTimeout",
                        $"Gave up waiting for NPC{waitingFor.Value} after {TalkWaitTimeoutTicks} ticks");
                }
                else if (npc.Execution.Status != ExecutionStatus.InProgress ||
                         npc.Execution.CurrentInteraction != InteractionType.Talk)
                {
                    if (npc.Plan.Status == PlanStatus.Active ||
                        npc.Execution.Status == ExecutionStatus.InProgress)
                    {
                        PlanInterruption.Abort(world, npc,
                            $"Accepting talk from NPC{waitingFor.Value}, waiting in place");
                    }

                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }
            }

            // Spec §53: self-heal a stale aid claim — valid only while the helper
            // still exists, still targets this NPC, and is still on an Aid goal.
            if (npc.Mind.PendingAidFrom is { } aidFromId &&
                (!world.Entities.Npcs.TryGetValue(aidFromId, out var aider) ||
                 aider.Plan.TargetAgentId is not { } aiderTarget ||
                 !aiderTarget.Equals(npc.Id) ||
                 aider.Mind.CurrentGoal != GoalType.Aid))
            {
                npc.Mind.PendingAidFrom = null;
            }

            // Spec §53: a sufferer being helped holds still until the helper
            // arrives — but danger (a fight, a flee) or a timeout breaks the wait.
            // Unlike a talk invite, hunger does NOT break it: she needs the help.
            if (npc.Mind.PendingAidFrom is { } aidWaitingFor)
            {
                // §64.9: ordinary hunger still does NOT break the aid wait (she
                // needs the help more than the meal — §53's original intent), but
                // the LIFE bands do. Idle probe: waiting-for-helper was 8 220
                // npc-ticks in 10 days on seed 31337 — 35% of all "standing
                // around doing nothing" — and the 30-day soak killed girls at
                // Thirst 1.00 who had no goal at all while they waited.
                if (npc.IsFighting || npc.Mind.CurrentGoal == GoalType.Flee ||
                    npc.Mind.IsStarving || npc.Mind.IsDehydrated)
                {
                    npc.Mind.PendingAidFrom = null;
                }
                else if (world.Tick - npc.Mind.PendingAidSinceTick > TalkWaitTimeoutTicks)
                {
                    npc.Mind.PendingAidFrom = null;
                    Trace.Emit(world, npc.Id, "AidWaitTimeout",
                        $"Gave up waiting for helper NPC{aidWaitingFor.Value}");
                }
                else if (npc.Execution.Status != ExecutionStatus.InProgress)
                {
                    if (npc.Plan.Status == PlanStatus.Active)
                    {
                        PlanInterruption.Abort(world, npc,
                            $"Awaiting help from NPC{aidWaitingFor.Value}, waiting in place");
                    }

                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }
            }

            // Eat normally consumes from inventory. Coconuts are the exception:
            // whole/pierced/split states are processed and consumed on the ground.
            var hasFoodInInventory = npc.Inventory.FindFirstFood(world.Content) != null;
            var hasCoconutMeal = HasCoconutMeal(npc, world);
            var hasCoconutWater = HasCoconutWater(npc, world);
            var hasCoconutBlade = HasCoconutBlade(npc);
            var canUseToolsOrWeapons = npc.Body.CanUseToolsOrWeapons;
            // Restraint gate (spec 29B.2): don't harvest food you don't need,
            // or the ground stock never survives until the productionless night.
            var getFoodHungerThreshold = SimBalance.GetFoodHungerThreshold;

            var eatAvail = hasFoodInInventory || hasCoconutMeal;
            // §53.7: split the "is there food to fetch at all" half out of the
            // gate — an aid errand fetches food for a STARVING HOUSEMATE, so
            // neither the helper's own (satisfied) hunger nor a coconut lying
            // on the ground beside her may veto it. What matters for an errand
            // is what she can HAND OVER, i.e. what is in the pack.
            var foodSourceReachable =
                HasReachableFoodForCurrentTools(npc, world) || KnowsReachableProducer(npc, world);
            var getFoodAvail = !hasFoodInInventory && !hasCoconutMeal &&
                npc.Needs.Hunger >= getFoodHungerThreshold && foodSourceReachable;
            var foodFetchPossible = !hasFoodInInventory &&
                !HasInventoryCoconutMeal(npc) && foodSourceReachable;
            // Spec 29G: the land itself is furniture — a bed is better, but
            // sleep never blocks on owning one. Still, nobody naps at noon
            // out of boredom: sleep is for the tired or for the dark hours
            // (without this gate the first soak showed 73 ground naps eating
            // every idle minute — no explores, the fire never lit).
            // §49-parity: never LIE DOWN when the sleep interrupt would fire on
            // the first tick (hunger/thirst over the wake threshold, danger
            // remembered) — the same condition execution wakes on. Without
            // this the thirsty-and-tired girl loops lie-down→wake forever.
            var sleepAvail = (npc.Needs.Energy < SimBalance.SleepEnergyThreshold ||
                    world.Environment.Phase is DayPhase.Night or DayPhase.Evening) &&
                !ExecutionSystem.HasSleepInterrupt(world, npc);
            // Spec 31C.7A: sit because you need it — and never settle into a
            // chair on an empty stomach. Sitting yields to sleep hours (the
            // Sit->Sleep churn was 47 interrupts/soak before this gate).
            // §54.12: sitting needs a REAL seat — Sit-furniture in view (stump/
            // chair/bed) or a ledge step within plan range. Flat ground is not
            // a seat (the old ground-sit fallback is gone), so without one Sit
            // doesn't even bid — else she'd churn Sit→NoLedge→cooldown forever
            // on a flat island.
            var sitAvail = npc.Needs.Comfort < SimBalance.SitComfortThreshold &&
                npc.Needs.Hunger < SimBalance.SitNeedGate && npc.Needs.Thirst < SimBalance.SitNeedGate &&
                !sleepAvail &&
                // Sitting is worth it only for a seat she's essentially next to —
                // a ledge within ~2 hexes, not one hiked to across the map (user:
                // walk right up to it, never sit "from afar"). Was 8R.
                (HasPerceivedSeat(npc) || AnyLedgeNear(world, npc, HexSpatialMath.HexRadius * 2f));
            // Spec 29C.4 restraint: dress only against cold — an overheated
            // NPC reaching for more clothes is a doom loop.
            // Spec 29C.4A: fresh danger overrides the weather — arm up.
            var effectiveTemp = world.Environment.GlobalTemperature + npc.EquippedWarmth * 10f;
            // Jul 2026: never reach for the wardrobe while a mob is actively
            // hunting her — armor-up is for the lull AFTER the scare, not
            // mid-chase (iter-5: Flee↔Dress churn under bites, seed 12345).
            // "Hunted" = a live mob has HER as its target AND is close enough
            // to matter (≤3 tiles). Distance cap added after iter-8: a wolf
            // circling out of reach kept a girl's whole auction suppressed all
            // night — no Sleep, no chores — and she stood by the fire until
            // thirst took her (seed 42 d6.5).
            var activelyHunted = npc.IsFighting;
            if (!activelyHunted)
            {
                foreach (var mob in world.Mobs)
                {
                    if (mob.Health > 0f && mob.TargetNpc is { } hunted &&
                        hunted.Equals(npc.Id) &&
                        HexSpatialMath.HexDistance(mob.Tile, npc.Tile) <= 3)
                    {
                        activelyHunted = true;
                        break;
                    }
                }
            }

            // §72: a man stalking her counts exactly the same. Without this she
            // keeps re-picking Dress/GatherWood between his blows — the iter-5
            // churn the comment above describes, with a knife instead of teeth.
            if (!activelyHunted && Spec72.Enabled)
            {
                foreach (var other in world.Entities.Npcs.Values)
                {
                    if (other.Health <= 0f)
                    {
                        continue;
                    }

                    // §89: трясут — это то же самое, что охотятся. Без этой
                    // половины сцена НЕ ДОИГРЫВАЛА ни разу: он подходил, она
                    // спокойно выбирала «пойду постираю» и уходила, он догонял
                    // и начинал заново — и так по кругу. Стоять и слушать её
                    // никто не заставляет: она вольна испугаться и убежать,
                    // но не уйти по делам, будто ничего не происходит.
                    var stalked =
                        (other.Mind.RaidTargetNpcId is { } raidTarget && raidTarget.Equals(npc.Id)) ||
                        (other.Mind.AbuseTargetNpcId is { } abuseTarget && abuseTarget.Equals(npc.Id));

                    if (stalked && HexSpatialMath.HexDistance(other.Tile, npc.Tile) <= 3)
                    {
                        activelyHunted = true;
                        break;
                    }
                }
            }

            var wantsArmor = !activelyHunted &&
                npc.Memory.Dangers.Count > 0 && npc.EquippedArmor < 0.3f &&
                KnowsReachableArmor(npc, world);
            // §40.6: while she owes clothes to the post-bathe shore pile, the
            // Bathe goal's redress plan re-dresses her (ALL the pieces she took
            // off) — suppress the ordinary warmth-filtered Dress so it can't
            // hijack her and put back only the one warming garment.
            var pendingRedress = npc.Mind.RedressGarments.Count > 0;
            var dressAvail = !pendingRedress && (wantsArmor ||
                (npc.Needs.ThermalDiscomfort >= SimBalance.DressThermalThreshold &&
                 effectiveTemp < SimBalance.DressColdTemp && // spec 42: dress against REAL cold only —
                 // a merely-cool girl (14..16) must not circle the wardrobe all
                 // day while the fire/water chain starves (worn=183/soak once)
                 npc.EquippedWarmth < SimBalance.DressWarmthCeiling && // already bundled up: more cloth
                 // won't fix 10°C — the campfire will (stops armor-swap churn)
                 HasInteraction(npc, InteractionType.Dress) &&
                 // §52.7: ...and only when something in reach is a REAL warmth
                 // upgrade — no trek to an equal/worse shirt (the girl's own
                 // example: a top over an identical top warms her by nothing).
                 KnowsReachableWarmthUpgrade(npc, world)));
            // §82: обгорела — прикройся. Раньше одеваться заставляла ТОЛЬКО
            // температура, поэтому в жаркий комфортный полдень девушка ходила
            // раздетой и горела, не понимая, что с ней происходит: краснота
            // росла, части тела теряли здоровье, а в аукционе это не значило
            // ничего. Теперь краснота — такая же причина одеться, как холод.
            //
            // Через MAX, а не сложением: холод и солнце требуют одного и того
            // же действия, и складывать их значило бы гнать одеваться вдвое
            // сильнее, когда человеку просто очень плохо.
            var sunPressure = npc.Needs.Sunburn * Spec82.SunburnDressWeight;
            var dressNeed = System.Math.Max(
                wantsArmor
                    ? System.Math.Max(npc.Needs.ThermalDiscomfort, 0.6f)
                    : npc.Needs.ThermalDiscomfort,
                sunPressure);

            // Spec 28.6 / 28.15A: Socialize needs a reachable non-busy agent;
            // affinity toward the best target feeds the score back positively.
            var socializeAvail = false;
            float? bestAffinity = null;
            foreach (var agent in npc.Perception.Agents)
            {
                if (!agent.IsReachable || agent.IsBusy || agent.IsMoving ||
                    agent.IsUnconscious) // §60: a comatose girl is no company
                {
                    continue;
                }

                socializeAvail = true;
                if (bestAffinity is null || agent.Relationship.Affinity > bestAffinity)
                {
                    bestAffinity = agent.Relationship.Affinity;
                }
            }

            // Spec §49: don't START a chat on an empty stomach or a dry throat.
            // With talks now longer (up to 90 ticks), a moderately thirsty/hungry
            // girl who talks instead of drinking climbs toward dehydration during
            // the conversation — the soak's dominant new death. Gating INITIATION
            // (an in-flight talk below still finishes) decouples talk length from
            // the water economy, so the "linger and chat" feel is safe. Mirrors
            // the Sit gate (comfort leisure yields to real needs).
            if (npc.Needs.Hunger >= Spec49.SocializeNeedGate ||
                npc.Needs.Thirst >= Spec49.SocializeNeedGate)
            {
                socializeAvail = false;
            }

            // Spec 28.8 handshake: while someone is coming over to talk,
            // don't initiate a talk yourself.
            if (npc.Mind.PendingTalkFrom is not null)
            {
                socializeAvail = false;
            }

            // An in-flight talk (walking to the target or already talking) keeps
            // its own goal available: the per-tick availability scan must not
            // zero out a plan that validates its target at arrival anyway.
            // Transient target movement is not "target gone" (spec 28.15A).
            if ((npc.Plan.Status == PlanStatus.Active && npc.Plan.TargetAgentId is not null) ||
                (npc.Execution.Status == ExecutionStatus.InProgress &&
                 npc.Execution.CurrentInteraction == InteractionType.Talk))
            {
                socializeAvail = true;
            }

            // Spec 19.7A: sleeping is more attractive after dark.
            var sleepEnvironmentBonus = world.Environment.Phase switch
            {
                DayPhase.Night => 0.25f,
                DayPhase.Evening => 0.10f,
                _ => 0f
            };


            // §5-фаза: всё, что скоринг читает из подготовки, собрано в один
            // контекст. Двадцать две переменные объявлялись за сотни строк до
            // места чтения, и понять, что именно видит скоринг, можно было только
            // прочитав подготовку целиком. Теперь это список полей.
            var ctx = new DecisionContext(
                previousGoal,
                bleedingCrisis,
                emergencyBoost,
                drinkBoost,
                deadTiredBoost,
                isGrieving,
                hasCoconutWater,
                hasCoconutBlade,
                canUseToolsOrWeapons,
                getFoodHungerThreshold,
                eatAvail,
                getFoodAvail,
                foodFetchPossible,
                sleepAvail,
                sitAvail,
                effectiveTemp,
                activelyHunted,
                dressAvail,
                dressNeed,
                pendingRedress,
                socializeAvail,
                sleepEnvironmentBonus,
                bestAffinity);
            ScoreGoals(world, npc, in ctx,
                out var aidErrandGoal, out var aidErrandKindNow,
                out var aidErrandTargetNow, out var aidErrandBid);

            if (ctx.BleedingCrisis)
            {
                SuppressPeacetimeDuringBleeding(npc);
            }

            // Jul 2026: the starving/dehydrated exception that reopens the
            // auction mid-fight is for SURVIVAL moves only — without this
            // filter cold-Dress (1.1 at night, near-naked) kept winning the
            // auction BETWEEN bites (IsFighting flickers while the dog closes
            // again) and the girl re-dressed while being eaten (seed 12345).
            // activelyHunted covers the whole chase, not just the melee ticks.
            if (ctx.ActivelyHunted)
            {
                foreach (var score in npc.Mind.LastScores)
                {
                    var allowed = score.Goal is GoalType.Drink or GoalType.GetWater
                        or GoalType.Eat or GoalType.GetFood or GoalType.Idle or GoalType.None;
                    if (!allowed)
                    {
                        score.FinalScore = 0f;
                    }
                }
            }

            ChooseGoal(world, npc, ctx.PreviousGoal,
                new AvailabilityTrace(ctx.EatAvail, ctx.GetFoodAvail, ctx.SleepAvail,
                    ctx.SitAvail, ctx.DressAvail, ctx.SocializeAvail),
                aidErrandGoal, aidErrandKindNow, aidErrandTargetNow, aidErrandBid);
        }
    }


    /// <summary>
    /// Что скоринг знает о колонистке на этом проходе.
    ///
    /// <para>
    /// Двадцать две величины, которые подготовка вычисляет, а семьдесят
    /// скоринг-блоков читают — иногда за восемьсот строк от места вычисления.
    /// Пока это были локалы одного метода, вопрос «что вообще влияет на
    /// оценку» не имел короткого ответа: надо было прочесть четыреста строк
    /// подготовки и держать их в голове.
    /// </para>
    /// <para>
    /// Структура readonly и заполняется один раз: ни одна из этих величин в
    /// скоринге не переприсваивалась (проверено), и запрет на это теперь
    /// выражен типом, а не дисциплиной.
    /// </para>
    /// </summary>
    private readonly struct DecisionContext
    {
        public DecisionContext(
            GoalType previousGoal,
            bool bleedingCrisis,
            float emergencyBoost,
            float drinkBoost,
            float deadTiredBoost,
            bool isGrieving,
            bool hasCoconutWater,
            bool hasCoconutBlade,
            bool canUseToolsOrWeapons,
            float getFoodHungerThreshold,
            bool eatAvail,
            bool getFoodAvail,
            bool foodFetchPossible,
            bool sleepAvail,
            bool sitAvail,
            float effectiveTemp,
            bool activelyHunted,
            bool dressAvail,
            float dressNeed,
            bool pendingRedress,
            bool socializeAvail,
            float sleepEnvironmentBonus,
            float? bestAffinity)
        {
            PreviousGoal = previousGoal;
            BleedingCrisis = bleedingCrisis;
            EmergencyBoost = emergencyBoost;
            DrinkBoost = drinkBoost;
            DeadTiredBoost = deadTiredBoost;
            IsGrieving = isGrieving;
            HasCoconutWater = hasCoconutWater;
            HasCoconutBlade = hasCoconutBlade;
            CanUseToolsOrWeapons = canUseToolsOrWeapons;
            GetFoodHungerThreshold = getFoodHungerThreshold;
            EatAvail = eatAvail;
            GetFoodAvail = getFoodAvail;
            FoodFetchPossible = foodFetchPossible;
            SleepAvail = sleepAvail;
            SitAvail = sitAvail;
            EffectiveTemp = effectiveTemp;
            ActivelyHunted = activelyHunted;
            DressAvail = dressAvail;
            DressNeed = dressNeed;
            PendingRedress = pendingRedress;
            SocializeAvail = socializeAvail;
            SleepEnvironmentBonus = sleepEnvironmentBonus;
            BestAffinity = bestAffinity;
        }

        public GoalType PreviousGoal { get; }
        public bool BleedingCrisis { get; }
        public float EmergencyBoost { get; }
        public float DrinkBoost { get; }
        public float DeadTiredBoost { get; }
        public bool IsGrieving { get; }
        public bool HasCoconutWater { get; }
        public bool HasCoconutBlade { get; }
        public bool CanUseToolsOrWeapons { get; }
        public float GetFoodHungerThreshold { get; }
        public bool EatAvail { get; }
        public bool GetFoodAvail { get; }
        public bool FoodFetchPossible { get; }
        public bool SleepAvail { get; }
        public bool SitAvail { get; }
        public float EffectiveTemp { get; }
        public bool ActivelyHunted { get; }
        public bool DressAvail { get; }
        public float DressNeed { get; }
        public bool PendingRedress { get; }
        public bool SocializeAvail { get; }
        public float SleepEnvironmentBonus { get; }

        /// <summary>Приязнь к лучшему собеседнику — подсказка скорингу
        /// разговора. Nullable: «никого подходящего рядом» это не ноль.</summary>
        public float? BestAffinity { get; }
    }
    /// <summary>Что было доступно на этом проходе — только для строки трассы
    /// <c>DecisionInput</c>. Отдельными параметрами эти шесть флагов ничего не
    /// объясняли бы, а сигнатуру раздували.</summary>
    private readonly struct AvailabilityTrace
    {
        public AvailabilityTrace(bool eat, bool getFood, bool sleep,
            bool sit, bool dress, bool socialize)
        {
            EatAvail = eat;
            GetFoodAvail = getFood;
            SleepAvail = sleep;
            SitAvail = sit;
            DressAvail = dress;
            SocializeAvail = socialize;
        }

        public bool EatAvail { get; }
        public bool GetFoodAvail { get; }
        public bool SleepAvail { get; }
        public bool SitAvail { get; }
        public bool DressAvail { get; }
        public bool SocializeAvail { get; }
    }

    /// <summary>
    /// Ставки по всем целям. Семьдесят блоков: каждый решает, доступна ли его
    /// цель прямо сейчас и насколько сильно её хочется.
    ///
    /// <para>
    /// Всё, что блоки знают о колонистке, приходит одним <see cref="DecisionContext"/>,
    /// а не сотней локалов объемлющего метода.
    /// </para>
    /// <para>
    /// Досрочных выходов здесь НЕТ: три `continue` в этом коде относятся к
    /// вложенным циклам (перебор соседок, перебор материалов стройки), а не к
    /// проходу по колонистке. Первый заход распила превратил их в `return`, и
    /// решения пропали целиком — поймала golden-трасса.
    /// </para>
    /// </summary>
    private static void ScoreGoals(
        WorldState world, NPCState npc, in DecisionContext ctx,
        out GoalType? aidErrandGoal, out AidKind aidErrandKindNow,
        out EntityId? aidErrandTargetNow, out float aidErrandBid)
    {
        // Значения по умолчанию: из метода можно выйти досрочно, а out-параметры
        // обязаны быть заполнены на каждом пути.
        aidErrandGoal = null;
        aidErrandKindNow = AidKind.None;
        aidErrandTargetNow = null;
        aidErrandBid = 0f;

        AddGoalScore(npc, world.Tick, GoalType.Eat, npc.Needs.Hunger, ctx.EatAvail, ctx.EmergencyBoost);
        AddGoalScore(npc, world.Tick, GoalType.GetFood, npc.Needs.Hunger, ctx.GetFoodAvail, ctx.EmergencyBoost);
        AddGoalScore(npc, world.Tick, GoalType.Sleep, 1f - npc.Needs.Energy, ctx.SleepAvail,
            emergency: ctx.DeadTiredBoost, environment: ctx.SleepEnvironmentBonus);
        // Sitting anywhere is leisure, not survival: half-weight keeps it
        // an idle-time filler instead of outbidding fire and food chores
        // (full 1-Comfort made Sit >= 0.4 by construction of its gate).
        // Spec 40.1: low stamina adds a gentle pull toward sitting to
        // recover (score only — availability unchanged, so the economy
        // isn't reshaped, just the timing of an already-available rest).
        AddGoalScore(npc, world.Tick, GoalType.Sit,
            (1f - npc.Needs.Comfort) * 0.5f + (1f - npc.Needs.Stamina) * 0.25f, ctx.SitAvail);
        AddGoalScore(npc, world.Tick, GoalType.Dress, ctx.DressNeed, ctx.DressAvail);
        // Spec 28.15B: dislike lowers the urge, embarrassment causes
        // post-quarrel withdrawal.
        AddGoalScore(npc, world.Tick, GoalType.Socialize, 1f - npc.Needs.Social, ctx.SocializeAvail,
            social: (ctx.BestAffinity ?? 0f) * 0.1f - npc.Social.Embarrassment * 0.3f -
                (ctx.IsGrieving ? 0.2f : 0f));

        ScoreCompassion(world, npc, in ctx,
            out var aidSelfOk, out var errandKind,
            out var errandTarget, out var errandFullBid);

        // Spec 35.3: what the communal hut needs next (null = done/absent).
        // Construction is peacetime work: material hauling pauses while
        // hungry, thirsty, or while the fire is starving (soak lesson —
        // logs feed walls AND the hearth, and the hearth comes first).
        var piece = NextBuildPiece(world);
        if (piece is not null &&
            (npc.Needs.Hunger >= 0.5f || npc.Needs.Thirst >= 0.5f))
        {
            piece = null;
        }
        // Spec §54: logs frame the builds/raft/premium bed; sticks are the
        // fuel + hand-tool currency (split from logs, or picked from deadfall).
        var carriedLogs = CountInventory(npc, ContentIds.Log);
        var carriedSticks = CountInventory(npc, ContentIds.Stick);
        var carriedLeaves = CountInventory(npc, ContentIds.PalmLeaf);
        // Spec §54: cordage chain + knife.
        var carriedFiber = CountInventory(npc, ContentIds.Fiber);
        var carriedRope = CountInventory(npc, ContentIds.Rope);
        var carriedCloth = CountInventory(npc, ContentIds.Cloth);
        var hasKnife = Content.GearCatalog.HasCapability(
            npc.Inventory.Items, Content.GearCapability.Cut);
        // The knife is today's only Butcher tool, but the CAPABILITY is the
        // truth: an axe cuts yucca (Cut) yet can't dress a carcass, so the
        // knife stays craftable until Butcher is covered too.
        var hasButcherTool = Content.GearCatalog.HasCapability(
            npc.Inventory.Items, Content.GearCapability.Butcher);

        // Spec §52: the furniture build-site chain. A site is an intent point
        // every NPC knows; materials are hauled in (deposited into it) over
        // many trips, then a builder with a hammer raises the piece. Build
        // work is peacetime (like the hut): it pauses when hungry/thirsty.
        var buildSite = FindBuildSite(npc, world);
        var hasHammer = Content.GearCatalog.HasCapability(
            npc.Inventory.Items, Content.GearCapability.Hammer);
        // §54.13: the old 0.55/any-danger gate held the window open only
        // ~1-36% of npc-ticks (10-day soak) — thirst equilibrates at
        // 0.5-0.6 and a day-old wolf memory froze building colony-wide.
        // Pause for real pressure (0.65, the lifeThreatened bar) and for
        // FRESH danger only.
        var freshDanger = false;
        foreach (var danger in npc.Memory.Dangers)
        {
            if (world.Tick - danger.Tick <= SimBalance.BuildDangerFreshTicks)
            {
                freshDanger = true;
                break;
            }
        }

        var buildPeacetime = npc.Needs.Hunger < SimBalance.BuildNeedGate &&
            npc.Needs.Thirst < SimBalance.BuildNeedGate && !freshDanger;
        // Spec §54 cold start: raising the FIRST hearth is survival-critical
        // (no fire ⇒ no warmth, no cooking, no crafting), so building the
        // campfire-site bypasses the peacetime gate and outranks everything —
        // the colony must pile the stones and light up before it can thrive.
        var noCampfireYet = !HasReachableWithTag(npc, world, "Campfire");
        var siteIsHearth = buildSite != null && buildSite.BuildProduct == ContentIds.Campfire;
        // §54.14 (r2): the bypass stops at the LIFE-critical band — the
        // bare-site cold start made the first hearth a longer project, and
        // a girl must never starve to death with the pile sticks in her
        // pack (probe: Marta dead at hunger 1.0 carrying 8/9 sticks).
        var hearthUrgent = siteIsHearth && noCampfireYet &&
            npc.Needs.Hunger < 0.8f && npc.Needs.Thirst < 0.8f;
        var buildWindow = buildPeacetime || hearthUrgent;
        // §64.9: two readings of the same bill. siteWantsLogs = "the site's
        // current stage is short of logs" (drives the pulls and the
        // don't-split reservation, and must NOT lapse the moment she picks
        // one up); siteNeedsLogs adds "…and my hands are empty of them",
        // which is the gather-availability half it always was.
        var siteWantsLogs = buildSite != null && buildWindow &&
            BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialLogs);
        var siteNeedsLogs = siteWantsLogs && carriedLogs < 1;
        var siteNeedsStones = buildSite != null && buildWindow &&
            BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialStones);
        // Spec §54.2: a bed build-site also pulls leaves + sticks — the gather
        // feeders (ChopCrown/GatherLeaves, SplitLog) fetch them so BuildFurniture
        // can haul each piece over to the growing mat.
        var siteNeedsLeaves = buildSite != null && buildWindow &&
            BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialLeaves);
        var siteNeedsSticks = buildSite != null && buildWindow &&
            BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialSticks);
        var siteNeedsRope = buildSite != null && buildWindow &&
            BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialRope);
        // §54.10: when a staked bed site is waiting on materials, its gather +
        // deliver chain outranks peacetime leisure (Sit/Socialize/idle) so the
        // mat actually finishes — still peacetime-gated, so hunger/thirst/danger
        // always preempt it (survival is never traded for a bed).
        var bedLeafPull = siteNeedsLeaves ? AiBalance.BuildSiteMaterialPull : 0f;
        var bedStickPull = siteNeedsSticks ? AiBalance.BuildSiteMaterialPull : 0f;
        // §64.9: LOGS were the one bill material with no pull anywhere —
        // bed.basic's stage 1 is four side rails, and §64.8's PremiumBedChance
        // stakes a bed.basic as some girls' FIRST bed. Soak (6 seeds x 10
        // days): four seeds staked a bed.basic and every one of them sat at
        // log 0/4 for ~22 000 ticks. GatherWood carried siteNeedsLogs in its
        // availability but nothing in its score, so it lost every auction.
        var bedLogPull = siteWantsLogs ? AiBalance.BuildSiteMaterialPull : 0f;
        var bedRopePull = siteNeedsRope ? AiBalance.BuildSiteMaterialPull : 0f;
        // BuildFurniture fires when I can advance the site: bring a material
        // it still needs, or raise it once stocked — with a hammer, except a
        // §54 campfire (piled from stones) and the leaf mat (hand-lashed).
        var siteIsBed = buildSite?.BuildProduct is ContentIds.BedLeaf or ContentIds.BedBasic;
        // §35.5B: the rack is lashed sticks like the leaf mat — no hammer.
        var siteWaivesHammer = siteIsHearth ||
            buildSite?.BuildProduct is ContentIds.BedLeaf or ContentIds.DryingRack;
        // §54.13: this is only the RAISE half. The deliver half is decided
        // next to the BuildFurniture score, where the gather flags exist —
        // staged sites take bundles, not single pieces (see below).
        var buildFurnitureRaise = buildSite != null && buildWindow &&
            BuildSiteMath.IsStocked(buildSite) &&
            (siteWaivesHammer || (ctx.CanUseToolsOrWeapons && hasHammer));

        // Spec 29E: the fire chain still needs these — a pot/lighter/wood
        // and a seen campfire drive the fuel/craft goals further below.
        var hasLighter = Content.GearCatalog.HasCapability(
            npc.Inventory.Items, Content.GearCapability.Ignite);
        var hasPot = Content.GearCatalog.HasCapability(
            npc.Inventory.Items, Content.GearCapability.Boil);
        // Spec §54: "wood in hand" for fire/craft now means a STICK.
        var hasWood = npc.Inventory.Items.Contains(ContentIds.Stick);
        var (campfireSeen, campfireFuel, campfireObj) = FindCampfire(npc, world);
        // §gear-craft: a recipe with NO station crafts in place — its
        // availability must not demand a campfire in view.
        bool CraftPlaceOk(GoalType craftGoal)
        {
            var station = Content.RecipeCatalog.StationOf(craftGoal);
            if (string.IsNullOrEmpty(station))
            {
                return true;
            }

            return station == "Campfire"
                ? campfireSeen
                : HasReachableWithTag(npc, world, station);
        }
        var hasBottleWater = HasBottleWater(npc);
        // Jul 2026: water forages like food — GetFood always had the
        // "walk to a remembered palm" fallback, GetWater didn't, so once
        // the camp's ground coconuts were eaten the colony sat at Thirst
        // 1.0 with groves rotting 6 tiles away (whole-colony thirst wipes,
        // seeds 42/999/2024).
        // §54.15: rain water waiting in a collector is a first-class thirst
        // answer — GetWater may fire on it even bladeless and coconut-less
        // (the planner prefers the collector draw over foraging).
        var collectorDrawSeen = FindDrawableCollector(npc, world) is not null;
        // §54.15 r2: the collector belongs to the DRINK lane, not just the
        // forage lane. GetWater is gated on !hasCoconutWater, and on this
        // island a drinkable coconut is nearly always in view — so the full
        // bottle standing under the funnel was unreachable by any goal and
        // simply never used (save 574386721: vessel at 1.00 for 6 000 ticks,
        // zero VesselTaken, four girls circling at thirst ~0.5). Drink now
        // sees it too; the planner draws from it once nothing drinkable is
        // in hand, before walking to a coconut.
        var drinkAvail = npc.Needs.Thirst >= AiBalance.DrinkThirstThreshold &&
            (hasBottleWater || ctx.HasCoconutWater || collectorDrawSeen);
        var waterSourceReachable = collectorDrawSeen ||
            (ctx.HasCoconutBlade &&
             (HasReachableDefinitionWorthCarrying(npc, world, ContentIds.Coconut) ||
              KnowsReachableProducer(npc, world)));
        var getWaterAvail = npc.Needs.Thirst >= AiBalance.DrinkThirstThreshold && !ctx.HasCoconutWater && !hasBottleWater &&
            waterSourceReachable;
        // §53.7: the errand half — fetching water to CARRY to a parched
        // housemate cares only about what is in hand (bottle / pierced
        // coconut), never about her own thirst or a nut on the ground.
        var waterFetchPossible = !HasInventoryCoconutWater(npc) && waterSourceReachable;
        // Costs come from the RECIPES (asset-overridable), not constants —
        // an asset that reprices a tool re-prices its gathering too.
        var axeStoneCost = Content.RecipeCatalog.InputCount(GoalType.CraftAxe, ContentIds.Stone);
        var pickaxeStoneCost = Content.RecipeCatalog.InputCount(GoalType.CraftPickaxe, ContentIds.Stone);
        var knifeStickCost = Content.RecipeCatalog.InputCount(GoalType.CraftKnife, ContentIds.Stick);
        var knifeStoneCost = Content.RecipeCatalog.InputCount(GoalType.CraftKnife, ContentIds.Stone);
        var coconutToolPressure = !ctx.HasCoconutBlade &&
            (npc.Needs.Thirst >= AiBalance.DrinkThirstThreshold || npc.Needs.Hunger >= ctx.GetFoodHungerThreshold) &&
            HasCoconutOpportunity(npc, world);
        var coconutToolBoost = coconutToolPressure
            ? System.MathF.Max(npc.Needs.Thirst, npc.Needs.Hunger)
            : 0f;
        var coconutEmergencyBoost =
            coconutToolPressure &&
            (npc.Needs.Thirst >= SimBalance.SleepInterruptThirst ||
             npc.Needs.Hunger >= SimBalance.SleepInterruptHunger ||
             npc.Mind.IsDehydrated ||
             npc.Mind.IsStarving)
                ? SimBalance.StarvingBoost
                : 0f;
        // §55: boiling water is retired — the fire chain no longer earns a
        // "boil" bonus, only warmth/cooking motivate it now.
        var wantsBoil = false;
        // Spec 35.2: any reachable Tool not carried (saw, dropped gear).
        var gatherToolsAvail = HasMissingToolReachable(npc, world);
        var fuelLow = campfireSeen && campfireFuel < 600f;
        // Balance audit (Jul 2026): the raft chain's gate — shared by
        // raftWoodDemand here and buildRaftAvail below — used to demand
        // needs < 0.55 and an EMPTY danger memory. Needs equilibrate at
        // ~0.55-0.7 on coconuts and §62 sightings restamp danger daily,
        // so the gate held 0.0-2.8% of NPC-slow-ticks and 40-day colonies
        // finished at raft 0/10. Moderate needs are fine for a coast walk;
        // only danger remembered NEAR HER cancels it (the §62 danger-ring
        // detours the route itself).
        var raftDangerNear = false;
        foreach (var dangerMemory in npc.Memory.Dangers)
        {
            if (HexSpatialMath.HexDistance(npc.Tile, dangerMemory.Tile) <= SimBalance.RaftDangerRadiusTiles)
            {
                raftDangerNear = true;
                break;
            }
        }

        // Spec 40.15 r3: the raft is a wood SINK of its own — with only
        // fire/build demand the endgame rode on leftover logs and crawled
        // (15-day soaks: 3 deposits). A settled girl who knows the raft
        // stocks up to 3 logs before the coast run so each trip counts.
        var raftWoodDemand = SimBalance.RaftEnabled &&
            carriedLogs < 3 &&
            world.RaftProgress < WorldState.RaftTarget &&
            npc.Needs.Hunger < SimBalance.RaftNeedGate &&
            npc.Needs.Thirst < SimBalance.RaftNeedGate &&
            !raftDangerNear &&
            KnowsReachableWithTag(npc, world, "Raft");
        // Spec §54: fetch wood off the ground when the fire is dying and
        // there's nothing burnable in hand (no stick AND no log to split), or
        // when a build/site/raft still wants logs. Targets any "Wood" — a
        // stick fuels directly, a log gets split (or built with).
        // §54.12: a site's stick stage pulls loose ground sticks too — SplitLog
        // only covers the log-rich camp; with a fed fire and no logs around,
        // nothing else ever picked a scattered stick up for the bed.
        var gatherWoodAvail = ((fuelLow && carriedSticks == 0 && carriedLogs == 0) ||
                (piece is { } pLog && carriedLogs < pLog.Logs) ||
                siteNeedsLogs ||
                (siteNeedsSticks &&
                 carriedSticks < BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialSticks)) ||
                raftWoodDemand ||
                (coconutToolPressure && carriedSticks < knifeStickCost)) &&
            npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Wood");
        // §45 r5: a genuinely cold girl can start the fire WITHOUT the
        // lighter (friction/hand-drill). The freeze probe showed 60-75%
        // of all freezing npc-ticks were "dead fire + wood in hand + no
        // lighter" — one lighter per colony and a half-day burn time
        // meant the carrier was almost never the one freezing at the
        // pit, and seed 777 died of hypothermia around that lock. The
        // threshold (-0.35, before the -0.85 damage band) keeps the
        // lighter meaningful in mild weather.
        // §54.14 (r2) hysteresis: once she's been that cold, the drill
        // stays in her hands for a grace window — the probe showed the
        // walk to the pit warming her past the gate, the goal collapsing
        // mid-route and the fire never lighting (cold start regression).
        if (npc.Needs.ThermalComfort < AiBalance.FreezingComfortThreshold)
        {
            npc.Mind.LastFreezingTick = world.Tick;
        }

        var canFrictionLight = npc.Needs.ThermalComfort < AiBalance.FreezingComfortThreshold ||
            world.Tick - npc.Mind.LastFreezingTick < SimBalance.FrictionLightGraceTicks;
        var tendFireAvail = hasWood && fuelLow &&
            (campfireFuel > 0f || canFrictionLight || (ctx.CanUseToolsOrWeapons && hasLighter));

        var drinkNeedScore = npc.Needs.Thirst +
            (npc.Needs.Thirst >= npc.Needs.Hunger ? 0.05f : 0f);
        AddGoalScore(npc, world.Tick, GoalType.Drink, drinkNeedScore, drinkAvail, ctx.DrinkBoost);
        AddGoalScore(npc, world.Tick, GoalType.GetWater, drinkNeedScore, getWaterAvail, ctx.DrinkBoost);
        // Spec 42: cold drives the WHOLE fire chain, not just the last
        // link — a freezing girl fetches the lighter and hauls wood with
        // fire-priority, otherwise the chain never outbids water/food and
        // the pit stays cold forever (goal histogram: fire goals absent).
        var coldChain = npc.Needs.ThermalComfort < -0.15f
            ? 0.4f * npc.Needs.ThermalDiscomfort
            : 0f;
        // Spec §49 (Tier C): once she's decided to boil rather than gamble on
        // raw, push the fire chain so the pit actually gets lit — otherwise
        // the suppressed raw goal just leaves her thirsty by a dead fire.
        var boilChain = wantsBoil ? Spec49.BoilChainWeight : 0f;
        AddGoalScore(npc, world.Tick, GoalType.GatherTools,
            0.25f + 0.2f * npc.Needs.Thirst + coldChain + boilChain,
            gatherToolsAvail, coconutEmergencyBoost);
        // The raft pull mirrors BuildRaft's weight: stocking logs for the
        // coast run must win the auction as often as the run itself, or
        // the demand flag never turns into wood in hand (soak: GatherWood
        // won 8-10 times in 15 days while the raft starved).
        AddGoalScore(npc, world.Tick, GoalType.GatherWood,
            0.2f + 0.3f * npc.Needs.Thirst + coldChain + boilChain +
            (raftWoodDemand ? 0.3f : 0f) + coconutToolBoost + bedStickPull +
            bedLogPull,
            gatherWoodAvail, coconutEmergencyBoost);
        // Spec 42: cold is the second reason to light the fire — a
        // freezing girl with wood and a lighter prioritizes the flame
        // over almost everything (this is THE way to warm up now).
        var freezing = npc.Needs.ThermalComfort < -0.15f;
        // Fire-priority probe (6 seeds x 4 days): when freezing AND the fire
        // was fully lightable (wood in hand, pit seen, friction unlocked),
        // TendFire still LOST the auction 82% of the time — beaten by Dress
        // and Sleep, which both carry the FULL cold weight (Dress = 0.1+TD).
        // At 0.45*TD the fire chain could never outbid "put on clothes" on
        // the cold axis, so a near-naked girl circled the empty wardrobe
        // while the pit stayed dark (fire uptime ~20%, lit ~2x/seed/4d).
        // Raised 0.45 -> 0.9*TD: lighting a DEAD fire is at least as urgent
        // as re-dressing (which can't fix a 6C night) — clears Dress (~1.1)
        // and Sleep (~1.1) plus the 0.15 switch margin at full cold.
        AddGoalScore(npc, world.Tick, GoalType.TendFire,
            0.25f + 0.3f * npc.Needs.Thirst + boilChain +
            (freezing ? 0.9f * npc.Needs.ThermalDiscomfort : 0f), tendFireAvail);

        // §54.15: park the empty bottle under the collector's funnel so any
        // rain — now or tonight — turns into clean drinking water. A quiet
        // chore in the TendFire band; nudged while it actually rains, and
        // by thirst (an empty bottle at 0.5 thirst is a plan, not clutter).
        var stowAvail = FindStowableCollector(npc, world) is not null;
        AddGoalScore(npc, world.Tick, GoalType.StowBottle,
            0.22f + 0.2f * npc.Needs.Thirst +
            (world.Environment.IsRaining ? 0.15f : 0f), stowAvail);

        // Spec 42: WarmUp — go stand by the burning fire until the chill
        // lifts. Available while genuinely cold and a lit fire is known;
        // the thermal system does the rest (fire is a real heat source).
        var warmUpAvail = freezing && npc.Needs.ThermalDiscomfort >= 0.35f &&
            campfireSeen && campfireFuel > 0f;
        AddGoalScore(npc, world.Tick, GoalType.WarmUp,
            0.35f + 0.55f * npc.Needs.ThermalDiscomfort, warmUpAvail);

        // Spec 44: the herbal first-aid chain — gather leaves, craft a
        // bandage at the fire. Urgency scales with how hurt anyone is.
        var herbLeaves = CountInventory(npc, ContentIds.HerbLeaf);
        // §68: the resupply half of self first-aid. A flat 0.3 step at
        // Health < 0.7 barely moved the herb run, and now that she SPENDS
        // her own dressings the pouch has to be refilled — so how badly she
        // is hurt (the same whole-body burden the treat goal reads) feeds
        // the fetch/craft urgency directly.
        var woundBurden = SelfTreatBurden(npc);
        var hurtUrgency = System.MathF.Max(
            npc.Health < 0.7f ? 0.3f : 0f,
            woundBurden * 0.6f);
        // §53.7: the "can this chain run at all" halves, free of her OWN
        // stock gate — an aid errand brews a dressing for someone else's
        // wound, so a full med pouch of her own must not veto it.
        var herbFetchPossible = herbLeaves < 2 &&
            npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Herb");
        var bandageCraftPossible = herbLeaves >= 2 && CraftPlaceOk(GoalType.CraftBandage);
        var gatherHerbAvail = herbFetchPossible && npc.Needs.Bandages < 2;
        AddGoalScore(npc, world.Tick, GoalType.GatherHerb,
            0.22f + hurtUrgency, gatherHerbAvail);
        var craftBandageAvail = bandageCraftPossible && npc.Needs.Bandages < 2;
        AddGoalScore(npc, world.Tick, GoalType.CraftBandage,
            0.3f + hurtUrgency, craftBandageAvail);

        // §68: patch YOURSELF up. Until now the only active wound care was
        // Aid(Treat) — someone ELSE walking over — while the hurt girl's own
        // first aid was the passive last resort in NeedsDecaySystem (worst
        // zone < 0.4 AND blood < 0.35). A mauling of many shallow bites never
        // trips that gate: save 604905660 had Marta at HP 0.61 / worst zone
        // 0.55 / blood 0.48 carrying TWO unusable bandages, and the auction
        // gave the evening to laundry. Now the burden reads the WHOLE body,
        // and a bleeding girl treats before she does chores.
        var treatBurdenGate = npc.Needs.Bandages > 1
            ? Spec53.SelfTreatBurdenThreshold
            : Spec53.SelfTreatLastBandageBurden;
        var treatWoundsAvail = Spec53.SelfTreatEnabled &&
            npc.Needs.Bandages > 0 &&
            woundBurden >= treatBurdenGate &&
            npc.Body.CanUseToolsOrWeapons && // a hand is needed to wind it
            !npc.IsFighting;                 // not mid-bite: fight or flee first
        // Deliberately NOT gated on remembered danger: that is the §65 trap
        // that already forbids sleep for a day after a wolf walks past, and a
        // dressing is exactly what she needs AFTER the fight.
        AddGoalScore(npc, world.Tick, GoalType.TreatWounds,
            Spec53.SelfTreatBase + woundBurden * Spec53.SelfTreatWeight,
            treatWoundsAvail,
            emergency: npc.Needs.Blood < Spec53.SelfTreatBleedBlood
                ? Spec53.SelfTreatBleedEmergency
                : 0f);

        // Spec 29F: hunting & crafting.
        var hasSpear = npc.Inventory.Items.Contains(ContentIds.Spear);
        var hasRawMeat = npc.Inventory.Items.Contains(ContentIds.MeatRaw);
        var hideCount = CountInventory(npc, ContentIds.Hide);
        // Spec 35.6: ranged hunters need no spear.
        var hasBow = npc.Inventory.Items.Contains(ContentIds.Bow);
        var arrowCount = CountInventory(npc, ContentIds.Arrow);
        // Spec §52: the spear is two-handed — wielding it needs both hands,
        // so a one-armed survivor (§50) can't spear-hunt. The bow is likewise
        // two-handed. Lose an arm and hunting is off the table.
        var canWield2Handed = ctx.CanUseToolsOrWeapons && npc.Body.IntactHands >= 2;
        var armed = (hasSpear || (hasBow && arrowCount > 0)) && canWield2Handed;
        // A carried coconut does not block the hunt — meat is also hide,
        // and hide is pants and a bow; the old any-food gate left the
        // whole leather/bow tier dormant (4 seeds, ~0 hunts). But the
        // window closes at 0.55: a truly hungry NPC takes the sure meal,
        // not a chase with a 50% roll (starving storms otherwise).
        // Spec 29F.4 (iter 32): window widened 0.55 -> 0.8. With coconuts
        // scarce, hunger climbs past 0.55 often and GetFood may find no
        // fruit — hunting must stay available as the real meat/hide source
        // rather than ceding to a starve.
        var huntAvail = armed && !hasRawMeat &&
            npc.Needs.Hunger >= 0.3f && npc.Needs.Hunger < 0.8f &&
            NearestVisibleRabbit(npc, world) is not null;
        var craftSpearAvail = ctx.CanUseToolsOrWeapons && !hasSpear && hasWood && CraftPlaceOk(GoalType.CraftSpear);
        // §54.14 (r2): cooking is the SPIT's job — CookMeat now
        // HANGS a raw chunk on the crossbar of a lit fire; the roast itself
        // runs in FireSystem over ~MeatRoastDurationTicks. No spit (or a
        // full crossbar) = no cooking, whatever else the fire can do.
        var spitHooksFree = campfireObj != null && FoodMath.SpitHasFreeHook(campfireObj);
        var cookAvail = hasRawMeat && campfireSeen && campfireFuel > 0f && spitHooksFree;
        var craftLeatherAvail = hideCount >= 1 && CraftPlaceOk(GoalType.CraftLeather) &&
            !npc.WornItems.Contains(ContentIds.LeatherPants);

        // Inside the peckish window the hunt genuinely outbids GetFood
        // (0.3+0.5h > h for h < 0.6); the availability window above is
        // what protects mealtimes, not the curve.
        AddGoalScore(npc, world.Tick, GoalType.Hunt,
            0.3f + 0.5f * npc.Needs.Hunger, huntAvail, ctx.EmergencyBoost);
        AddGoalScore(npc, world.Tick, GoalType.CraftSpear,
            0.2f + 0.2f * npc.Needs.Hunger, craftSpearAvail);
        // §54.17: when cooking is actually possible, hanging the chunk must
        // outbid GetFood (= Hunger) at EVERY hunger level — the old
        // 0.3 + 0.4·H curve lost to GetFood on the whole domain where both
        // were available, so she fetched forever and never hung the meat.
        AddGoalScore(npc, world.Tick, GoalType.CookMeat,
            SimBalance.CookMeatBase + SimBalance.CookMeatHungerWeight * npc.Needs.Hunger,
            cookAvail, ctx.EmergencyBoost);
        AddGoalScore(npc, world.Tick, GoalType.CraftLeather, 0.35f, craftLeatherAvail);

        // Spec 35.6: bow & arrows — pants outrank the first hide (0.35).
        // §gear: the bow is RETIRED pending the hunting rework — never
        // crafted, so the whole archery path stays dormant.
        var craftBowAvail = false && !hasBow && carriedSticks >= 2 && hideCount >= 1 &&
            carriedRope >= 1 && CraftPlaceOk(GoalType.CraftBow) && !craftLeatherAvail;
        var craftArrowsAvail = false && hasBow && arrowCount == 0 && hasWood && CraftPlaceOk(GoalType.CraftArrows); // §gear: bow retired
        AddGoalScore(npc, world.Tick, GoalType.CraftBow, 0.3f, craftBowAvail);
        AddGoalScore(npc, world.Tick, GoalType.CraftArrows, 0.3f, craftArrowsAvail);

        // Spec 28.15C: a griever visits the body for closure.
        //
        // §28.15C v3: могил больше нет — тело не закапывают, и «сходить к
        // могиле от одиночества» ушло вместе с ними. Прощаться теперь ходят к
        // самому телу, и оно никуда не денется: раньше скорбящая не успевала
        // дойти, потому что труп истлевал по дороге.
        var corpseReachable = HasReachableWithTag(npc, world, "Corpse");
        AddGoalScore(npc, world.Tick, GoalType.Mourn, 0.7f, ctx.IsGrieving && corpseReachable);

        // §28.15F: обобрать тело. Хозяйственная работа, а не нужда: ставка ниже
        // и еды, и воды, и сна, и — намеренно — ниже прощания (0.7). Пока она
        // скорбит, она не мародёрствует; горе проходит, вещи остаются.
        //
        // Гейт на свободные руки обязателен: без него цель выигрывает, NPC
        // доходит до тела и разворачивается ни с чем — и так каждый проход.
        AddGoalScore(npc, world.Tick, GoalType.LootCorpse, 0.34f,
            !ctx.IsGrieving && npc.Inventory.HasSpace &&
            CorpseMath.HasLootableCorpse(npc, world));

        if (piece is not null && fuelLow)
        {
            piece = null; // the hearth outranks the walls
        }

        // Spec 35.2: the tool & harvest chain.
        // Capability-driven (gear unification): "axe" here means ANY
        // wood-chopper and "pickaxe" any miner — a new chopping/mining
        // tool asset satisfies these decisions with no code change.
        var hasAxe = Content.GearCatalog.HasCapability(
            npc.Inventory.Items, Content.GearCapability.ChopWood);
        var hasSaw = false; // folded into the ChopWood capability above
        var hasPickaxe = Content.GearCatalog.HasCapability(
            npc.Inventory.Items, Content.GearCapability.Mine);
        var stoneCount = CountInventory(npc, ContentIds.Stone);
        var stonesNeeded = (!hasAxe && !hasSaw ? axeStoneCost : 0) + (!hasPickaxe ? pickaxeStoneCost : 0);
        // §63 r2: a stone-hungry site (the fire's 18-stone ring) is worth
        // a real armful, not one pebble per round trip — carry up to 3.
        var siteStoneWant = siteNeedsStones
            ? System.Math.Min(3, BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialStones))
            : 0;
        var gatherStoneAvail = (stoneCount < stonesNeeded ||
                (coconutToolPressure && stoneCount < knifeStoneCost) ||
                (piece is { } pStone && stoneCount < pStone.Stones) ||
                (siteNeedsStones && stoneCount < siteStoneWant)) &&
            npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Stone");
        var craftAxeAvail = ctx.CanUseToolsOrWeapons && !hasAxe && !hasSaw && hasWood &&
            stoneCount >= axeStoneCost && CraftPlaceOk(GoalType.CraftAxe);
        var craftPickaxeAvail = ctx.CanUseToolsOrWeapons && !hasPickaxe && hasWood &&
            stoneCount >= pickaxeStoneCost && CraftPlaceOk(GoalType.CraftPickaxe);
        var canChop = ctx.CanUseToolsOrWeapons && (hasAxe || hasSaw);
        // §47 comfort: a bed per girl, not per colony. bedDeficit drives
        // both the leaf supply (chop a palm when short) and CraftBed.
        // §72: a bed per girl of HER OWN camp. Counting the whole island
        // would demand a bed for the outsider too — which he never owns, so
        // the §64 OwnBed dream would sit at the head of the queue forever
        // and hold BuildPull on a project that cannot finish.
        var bedDeficit = CountReachableWithTag(npc, world, "Bed") <
            ColonyQueries.LivingCount(world, npc.Faction);
        // §54.2 fix: don't fell a NEW palm while the LAST one's harvest still
        // lies unprocessed on the ground. A felled palm scatters its CROWN
        // (the leaf source) + logs instead of filling the pack, so the
        // leaf/wood gates below stay "satisfied by 0 carried" and re-fire on
        // the next-nearest palm — the NPC mowed the whole grove and left a
        // trail of un-worked crowns/leaves. Finish one tree's output first: an
        // un-chopped crown OR loose leaves already on the ground block the leaf
        // motive here (chop-crown/gather-leaves take over); the wood/fuel motive
        // is already blocked by a reachable "Wood" (logs carry that tag).
        var pendingLeafSource = HasReachableWithTag(npc, world, "PalmCrown") ||
            HasReachableWithTag(npc, world, "PalmLeaf");
        // §64.9: a palm is the colony's WATER (coconuts), and felling is
        // permanent — building takes the grove's surplus, never its seed
        // stock. See SimBalance.PalmGroveReserve for the soak that killed a
        // colony by chopping the last seven palms for bed rails.
        var groveHasSurplus =
            CountReachableWithTag(npc, world, "Palm") > SimBalance.PalmGroveReserve;
        var harvestTreeAvail = canChop && npc.Inventory.HasSpace &&
            ((fuelLow && !HasReachableWithTag(npc, world, "Wood") &&
              HasReachableWithTag(npc, world, "Palm")) ||
             // §64.9: a build's LOG bill deliberately does NOT fell a palm.
             // It was tried (bed.basic's four side rails were otherwise
             // unobtainable once the camp's loose logs ran out) and it cost
             // the colony its water: the 30-day soak went from a grove that
             // sat steady at 7 palms for a fortnight to 0 palms on day 18,
             // 0 coconuts on day 20 and four thirst deaths on day 21. Site
             // logs come off the ground (GatherWood + bedLogPull) only; the
             // log stage itself is now avoided for FIRST beds — see
             // SpecDream.PremiumBedChance.
             (!pendingLeafSource && groveHasSurplus &&
              (CountInventory(npc, ContentIds.PalmLeaf) == 0 ||
               (piece is { } pLeaf && carriedLeaves < pLeaf.Leaves) ||
               (bedDeficit && carriedLeaves < 3)) &&
              HasReachableWithTag(npc, world, "Palm")));
        // §63 r2: with a stone-hungry site open the miner keeps swinging
        // until she carries a real load (3), not the old 2-stone stop.
        //
        // §80: и «сначала подбери с земли» — иначе цель кормит сама себя.
        // Валун рассыпает камни на ЗЕМЛЮ (Scatter), рюкзак при этом не
        // трогается, а условие ниже смотрит только в рюкзак: разбил валун →
        // предикат остался ровно таким же истинным. Неподвижная точка, из
        // которой выходит только подбор (GatherStone). У травы (:harvestYucca)
        // и дерева эта дыра закрыта с §54.13, у камня забыли — и чужак с
        // киркой выбил на сейве все 19 валунов острова, рассыпав 95 камней
        // и не донеся ни одного.
        //
        // Мера — ЛОКАЛЬНАЯ, не общеостровная: восприятие помнит предметы по
        // всей карте, так что глобальный запрет заморозил бы кирку навсегда
        // из-за камня, увиденного неделю назад. Спрашиваем «есть ли под
        // рукой у меня» и «есть ли под рукой у стройки, ради которой копаю».
        var stonesUnderfoot =
            HasNearbyWithTag(npc, world, "Stone", npc.Tile, SimBalance.PickUpFirstRadiusTiles) ||
            (siteNeedsStones && buildSite != null &&
             HasNearbyWithTag(npc, world, "Stone", buildSite.Tile, SimBalance.PickUpFirstRadiusTiles));
        var mineBoulderAvail = ctx.CanUseToolsOrWeapons && hasPickaxe &&
            stoneCount < System.Math.Max(2, siteStoneWant) && npc.Inventory.HasSpace &&
            !stonesUnderfoot &&
            HasReachableWithTag(npc, world, "Boulder");

        // Spec 45: FREE HANDS — needs handled, no danger => the surplus
        // goes into progress. Static 0.25-0.3 scores never beat the
        // needs-driven day (30-day runs: raft 0/20, no tools, no build);
        // a settled girl now picks up the pickaxe instead of strolling.
        // Spec 45 r2: "good enough" beats "perfect" — the strict 0.45
        // gate never opened (thirst lives above it), so no surplus ever
        // reached the projects. Comfortable-ish and safe is enough.
        // Behavior audit (Jul 2026): `Dangers.Count == 0` froze the whole
        // industry chain — §62 sightings restamp the danger memory (TTL
        // 2400) almost daily, so freeHands held ~never and 25-day soaks
        // finished with 0 ropes, 0 mined stones, 0 site deliveries, 0
        // beds. Same reasoning as buildPeacetime/raftDangerNear: only a
        // FRESH scare (BuildDangerFreshTicks) stays the settled hands;
        // day-old ghosts don't cancel the day's work.
        var freeHands = npc.Needs.Hunger < 0.55f && npc.Needs.Thirst < 0.55f &&
            npc.Needs.Energy > 0.35f && npc.Needs.ThermalDiscomfort < 0.5f &&
            !freshDanger
                ? 0.3f
                : 0f;
        // §64: the dream pull. Once basic needs are met (freeHands), a
        // colonist leans her build/gather effort toward HER OWN current dream
        // — the colony campfire, or her personal bed site. freeHands-gated, so
        // it collapses to 0 the instant any need tightens: it only tips among
        // peacetime chores, never into a survival bid. (While the dream is the
        // campfire, hearthUrgent already tops the auction, so it is harmlessly
        // redundant there; the same path drives the personal-bed dream.) Each
        // feeder it is mirrored onto is already availability-gated on the
        // site's current stage, so the pull matters only when that material is
        // actually wanted.
        // §64 HelpAnyBed: every colonist dreaming of a bed pushes the one
        // staked bed (not just its owner) AND gets the pull even while hands
        // are loaded, so the girl carrying materials actually bids to deliver.
        // The campfire dream keeps its original owner-agnostic + free-hands gate.
        var dreamMatch = SpecDream.Enabled && buildSite != null &&
            ((npc.Mind.CurrentDream == DreamType.Campfire && siteIsHearth && freeHands > 0f) ||
             (npc.Mind.CurrentDream == DreamType.OwnBed && siteIsBed &&
              (SpecDream.HelpAnyBed ||
               (freeHands > 0f && buildSite.Owner is { } dreamOwner && dreamOwner.Equals(npc.Id)))));
        var dreamPull = dreamMatch ? SpecDream.BuildPull : 0f;
        // §63 r2: the site's stone bill pulls the WHOLE mining chain the
        // way the bed's stages pull leaves/sticks/rope — without it
        // GatherStone/MineBoulder sat at 0.55-0.65 and lost every auction
        // to Sit/Drink (MineBoulder selected 0 times in 15 soak-days
        // while the ring waited on 18 stones).
        // …but ONLY with settled hands and a fed fire — an unconditional
        // pull ate TendFire/water time and wiped the probe colony. 0.5:
        // at 0.35 the settled bid (~1.0) still tied with Sit/Dress and
        // MineBoulder fired 0 times in 20 probe-days pickaxe-in-hand.
        var siteStonePull = siteNeedsStones && freeHands > 0f && !fuelLow ? 0.5f : 0f;
        // §54 cold start: fetching stones for the first hearth is urgent too.
        //
        // §80: подбор 0.28 против копания 0.25 — ничья была НЕ безобидной.
        // После того как очаг поднят, hearthUrgent гаснет навсегда, и обе
        // цели набирали побитово одинаковые очки; а правило удержания цели
        // требует перевеса СТРОГО больше порога, так что перевес 0.0 не
        // смещает действующую цель никогда. Копающий оставался копающим.
        AddGoalScore(npc, world.Tick, GoalType.GatherStone,
            (hearthUrgent ? 0.9f : 0.28f) + freeHands + coconutToolBoost + siteStonePull,
            gatherStoneAvail, coconutEmergencyBoost);
        AddGoalScore(npc, world.Tick, GoalType.CraftAxe, 0.3f + freeHands, craftAxeAvail);
        // §63 r2: a site drowning in stone demand (the 18-stone fire ring)
        // makes the PICKAXE the priority — without this pull the 0.55
        // delivery bid swallowed the pickaxe's own 2-stone budget every
        // time and the miner was never born (CraftedPickaxe 0 across every
        // 25-day soak; boulders sat unbroken while the ring starved).
        var pickaxeChainPull = siteNeedsStones && !hasPickaxe &&
            buildSite != null &&
            BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialStones) > pickaxeStoneCost
                ? 0.5f
                : 0f;
        AddGoalScore(npc, world.Tick, GoalType.CraftPickaxe,
            0.25f + freeHands + pickaxeChainPull, craftPickaxeAvail);
        // §47 comfort: the bed-chain pull — mirrors the spec-42 cold
        // chain. Bedless nights are the colony's loudest churn (Sleep
        // plan-starts 787-995 per 25 days, ~all retries), yet at 0.3
        // HarvestTree lost the auction to Dress/Socialize and leaves>=3
        // held 0% of npc-ticks: the palm never got chopped, so the bed
        // never got woven. The pull fires only while the chain is
        // actually short (deficit + no leaves in hand).
        var bedChainPull = bedDeficit && carriedLeaves < 3 && canChop ? 0.25f : 0f;
        AddGoalScore(npc, world.Tick, GoalType.HarvestTree,
            0.3f + freeHands + bedChainPull + bedLogPull + dreamPull, harvestTreeAvail);
        AddGoalScore(npc, world.Tick, GoalType.MineBoulder,
            0.25f + freeHands + siteStonePull, mineBoulderAvail);

        // Spec §54: split a ground log into sticks (the fuel/craft currency)
        // when short on sticks and there's stick demand — chiefly a dying
        // fire, or a stick-framed craft one split away. Needs a chop tool
        // (deadfall sticks bootstrap the first axe, breaking the chicken-and-
        // egg). Fire-urgent so it can win the auction and keep the hearth fed.
        var wantsSticks = fuelLow || siteNeedsSticks ||
            (campfireSeen && (!hasSpear ||
                (!hasPickaxe && stoneCount >= 2) ||
                (hasBow && arrowCount == 0)));
        // §54.10: sticks stack too — split toward the bed's stick bill when a
        // site needs them, not just the 2-stick fuel reserve (§54.12: toward
        // the CURRENT stage's shortfall).
        var stickCap = siteNeedsSticks
            ? BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialSticks)
            : 2;
        // §gear-data: the log's own declaration decides the tool (axe OR
        // knife OR whatever the asset lists) — canChop is only the legacy
        // fallback for undeclared content.
        var splitLogAvail = carriedSticks < stickCap &&
            CanPerformDeclared(world, npc, ContentIds.Log, InteractionType.Process,
                legacyOk: canChop) &&
            npc.Inventory.HasSpace &&
            HasReachableWithTag(npc, world, "Log") && wantsSticks;
        AddGoalScore(npc, world.Tick, GoalType.SplitLog,
            (fuelLow ? 0.5f : 0.3f) + freeHands + bedStickPull + dreamPull, splitLogAvail);

        // Spec §54.2: chop a felled palm CROWN into loose leaves — when leaves
        // are wanted (a bed short, or a build/tent bill) and a crown lies
        // reachable. Needs a chop tool (the same Process gate as splitting).
        // §54.10: with leaves stacking into one slot, a girl brings a whole
        // bundle per trip — so when a bed site is hungry for leaves, gather up
        // toward the full bill instead of stopping at 3 (which forced a dozen
        // half-empty trips and the bed never finished).
        // §54.12: the pre-stake "bed is coming, hoard a few leaves" clause only
        // applies while NO site exists — once one is staked, leaf demand follows
        // its STAGE (hoarding early leaves just fed a stash-at-fire/pick-back-up
        // churn loop with HaulToFire while the stick stage rejected them).
        var wantsLeaves = (bedDeficit && !siteIsBed && carriedLeaves < 3) ||
            (siteNeedsLeaves &&
             carriedLeaves < BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialLeaves)) ||
            (piece is { } pcrown && carriedLeaves < pcrown.Leaves) ||
            (world.Environment.UvIndex > 0.4f && carriedLeaves < 4);
        var chopCrownAvail = wantsLeaves && canChop &&
            npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "PalmCrown");
        AddGoalScore(npc, world.Tick, GoalType.ChopCrown, 0.3f + freeHands + bedLeafPull + dreamPull, chopCrownAvail);
        // Spec §54.2: pick scattered palm leaves off the ground when wanted.
        var gatherLeavesAvail = wantsLeaves && npc.Inventory.HasSpace &&
            HasReachableWithTag(npc, world, "PalmLeaf");
        AddGoalScore(npc, world.Tick, GoalType.GatherLeaves, 0.28f + freeHands + bedLeafPull + dreamPull, gatherLeavesAvail);

        // Spec §54: the cordage & knife chain. Rope is wanted for bowstrings
        // and bed-site lashings; cloth when a sun-shelter is due; the knife is
        // a survival tool (no butchering without it). Gather fiber to feed
        // rope/cloth, then craft at the fire.
        var ropeTarget = siteNeedsRope && buildSite != null
            ? BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialRope)
            : 1;
        var wantRope = (siteNeedsRope && carriedRope < ropeTarget) ||
            (!hasBow && hideCount >= 1 && carriedRope == 0);
        var wantCloth = bedDeficit && carriedCloth == 0;
        // §54.13: gather fiber for the WHOLE rope shortfall, not one rope's
        // worth — the old cost-of-one made a girl cut a yucca (4 fibers
        // scatter), pick up ONE, craft one rope, deliver it, and walk all
        // the way back seven more times. Fiber stacks (§54.10), so carrying
        // the full lashing bill is one pocket slot.
        var ropeShortfall = wantRope ? System.Math.Max(1, ropeTarget - carriedRope) : 0;
        var fiberNeed = ropeShortfall * SimBalance.RopeFiberCost +
            (wantCloth ? SimBalance.ClothFiberCost : 0);
        // Fiber now comes from CUTTING a yucca with a blade (knife/axe); the
        // cut fibers scatter, then get picked up. So: cut yucca → gather fiber.
        // §54.13: pick the ground CLEAN before cutting another plant. The
        // rope-stage soak showed HarvestYucca (0.26) outbidding GatherFiber
        // (0.24) until every yucca on the island was felled — 32 fibers lay
        // scattered while the site waited. Cutting is only available while
        // no loose fiber is reachable; yuccas are consumed, don't waste them.
        var harvestYuccaAvail = ctx.CanUseToolsOrWeapons && carriedFiber < fiberNeed && npc.Inventory.HasSpace &&
            (hasKnife || hasAxe) && !HasReachableWithTag(npc, world, "Fiber") &&
            HasReachableWithTag(npc, world, "Yucca");
        var gatherFiberAvail = carriedFiber < fiberNeed && npc.Inventory.HasSpace &&
            HasReachableWithTag(npc, world, "Fiber");
        // §84: fibers already lying by the stump ARE the recipe — the craft
        // fires on carried + ground pile and happens AT the pile (planning
        // walks her there; the craft-start beat takes the pieces off the
        // ground). CraftRope/Cloth (0.28) outbid GatherFiber (0.24), so
        // while a big-enough pile lies in one place nobody pockets it just
        // to lay it back out; gathering remains the fallback for fiber
        // scattered too thin to cover one craft's bill in a single spot.
        var ropeGroundPileOk = carriedFiber < SimBalance.RopeFiberCost &&
            FindGroundInputPile(npc, world, ContentIds.Fiber,
                SimBalance.RopeFiberCost - carriedFiber) is not null;
        var clothGroundPileOk = carriedFiber < SimBalance.ClothFiberCost &&
            FindGroundInputPile(npc, world, ContentIds.Fiber,
                SimBalance.ClothFiberCost - carriedFiber) is not null;
        var craftRopeAvail = ctx.CanUseToolsOrWeapons && wantRope &&
            (carriedFiber >= SimBalance.RopeFiberCost || ropeGroundPileOk) &&
            CraftPlaceOk(GoalType.CraftRope);
        var craftClothAvail = ctx.CanUseToolsOrWeapons && wantCloth &&
            (carriedFiber >= SimBalance.ClothFiberCost || clothGroundPileOk) &&
            CraftPlaceOk(GoalType.CraftCloth);
        var craftKnifeAvail = ctx.CanUseToolsOrWeapons && !(hasKnife && hasButcherTool) &&
            carriedSticks >= knifeStickCost &&
            stoneCount >= knifeStoneCost && CraftPlaceOk(GoalType.CraftKnife);
        AddGoalScore(npc, world.Tick, GoalType.HarvestYucca, 0.26f + freeHands + bedRopePull, harvestYuccaAvail);
        AddGoalScore(npc, world.Tick, GoalType.GatherFiber, 0.24f + freeHands + bedRopePull + dreamPull, gatherFiberAvail);
        AddGoalScore(npc, world.Tick, GoalType.CraftRope, 0.28f + freeHands + bedRopePull + dreamPull, craftRopeAvail);
        AddGoalScore(npc, world.Tick, GoalType.CraftCloth, 0.28f + freeHands, craftClothAvail);
        AddGoalScore(npc, world.Tick, GoalType.CraftKnife,
            0.34f + freeHands + coconutToolBoost, craftKnifeAvail, coconutEmergencyBoost);

        // Spec §54: butcher a carcass (or, starving, a housemate's body) with
        // a knife — hunger-driven, since the payoff is meat.
        var butcherAvail = ctx.CanUseToolsOrWeapons && hasButcherTool &&
            (HasReachableWithTag(npc, world, "Carcass") ||
             (SimBalance.CannibalismEnabled &&
              npc.Needs.Hunger >= SimBalance.CannibalizeHungerGate &&
              HasReachableWithTag(npc, world, "Corpse")));
        AddGoalScore(npc, world.Tick, GoalType.Butcher,
            0.3f + 0.5f * npc.Needs.Hunger, butcherAvail);

        // §56 Predation: the absolute last resort. A low-compassion survivor,
        // starving with NO softer food left (not even a corpse to butcher)
        // and a knife in hand, hunts the weakest housemate for meat. The
        // NoOtherFoodReachable gate keeps it strictly below every real food
        // source; PredationBaseScore is a low floor so it can never outbid one.
        var preyAvail = SimBalance.PredationEnabled && ctx.CanUseToolsOrWeapons && hasButcherTool &&
            npc.CompassionTrait <= SimBalance.PredationCompassionCeiling &&
            npc.Needs.Hunger >= SimBalance.PredationHungerGate &&
            NoOtherFoodReachable(npc, world) &&
            NearestPreyVictim(npc, world) is not null;
        AddGoalScore(npc, world.Tick, GoalType.Prey,
            SimBalance.PredationBaseScore + npc.Needs.Hunger, preyAvail);

        // §72 Raid: the outsider hunts the girls. He is an opportunist, so
        // this is a BID, not a compulsion — the score rides on how good the
        // target is (alone / hurt / asleep / close), and every self-gate
        // below keeps his own survival ranked above it. With Hunger and
        // Thirst capped at RaidSelfNeedCeiling before he may even bid, a
        // real need always outbids the hunt.
        NPCState raidVictim = null;
        var raidOpportunity = 0f;
        var raidAvail = false;
        if (Spec72.Enabled &&
            npc.Faction != Faction.Colony &&
            world.Tick >= Spec72.RaidGraceDays * EnvironmentSystem.DayLengthTicks &&
            world.Tick >= npc.Mind.RaidCooldownUntilTick &&
            npc.Needs.Hunger <= Spec72.RaidSelfNeedCeiling &&
            npc.Needs.Thirst <= Spec72.RaidSelfNeedCeiling &&
            npc.Needs.Energy >= Spec72.RaidSelfEnergyFloor &&
            RaidMath.IsFitToRaid(npc, world))
        {
            raidVictim = RaidMath.BestVictim(world, npc, out raidOpportunity);
            // Nobody in range — then go LOOKING. The prowl bids at the bare
            // base score, so it only ever wins when he has nothing pressing
            // of his own to do.
            raidAvail = raidVictim is not null ||
                RaidMath.ProwlTarget(world, npc) is not null;
        }

        AddGoalScore(npc, world.Tick, GoalType.Raid,
            Spec72.RaidBaseScore + Spec72.RaidOpportunityGain * raidOpportunity, raidAvail);

        // §81 Abuse: он идёт гнобить — либо чтобы отжать припас, либо
        // просто чтобы с кем-то «пообщаться». Вторая причина не метафора:
        // разговор доступен только между союзниками, амбиентное общение §49
        // считает соседей по фракции, а фракция у него из одного человека,
        // так что Social падает в ноль и там остаётся. Абьюз — единственный
        // способ её закрыть, и потому ставка растёт от ОДИНОЧЕСТВА так же,
        // как от голода.
        //
        // Ставка выше налётной намеренно: нужда должна перебивать
        // возможность. Он охотится, когда подвернулся случай, но гнобит —
        // когда ему самому нужно.
        var abuseDrive = 0f;
        var abuseAvail = false;
        if (Spec81.AbuseEnabled &&
            npc.Faction != Faction.Colony &&
            !AbuseMath.GraceHolds(world, npc) &&
            world.Tick >= npc.Mind.AbuseCooldownUntilTick &&
            !npc.IsFighting &&
            npc.Body.CanUseToolsOrWeapons &&
            !npc.Body.IsProne &&
            npc.Mind.CurrentGoal != GoalType.Flee)
        {
            abuseDrive = AbuseMath.Drive(npc);
            abuseAvail = abuseDrive > 0f &&
                AbuseMath.BestMark(world, npc, out _) is not null;

            // §81.12: охота глазами — «никого не вижу» не значит «нечего
            // хотеть». Одинокий идёт ИСКАТЬ: цель берётся, план строит prowl
            // §90 к лагерю, как он ходит за киркой к известной скале. Гейт по
            // одиночеству, не по Drive: сытость-ветка ненулевая почти всегда
            // (§93) и превратила бы его в вечного бродягу у чужого порога.
            if (!abuseAvail && Spec81.AbuseHuntBySight &&
                abuseDrive > 0f &&
                npc.Needs.Social < Spec81.AbuseSocialFloor &&
                RaidMath.ProwlTarget(world, npc) is not null)
            {
                abuseAvail = true;
            }
        }

        // Сцена уже идёт — цель обязана остаться доступной, иначе аукцион
        // выдернет его с середины (та же оговорка, что у §53 Aid).
        if (npc.Mind.CurrentGoal == GoalType.Abuse &&
            npc.Execution.CurrentInteraction == InteractionType.Abuse)
        {
            abuseAvail = true;
        }

        // §81.14: и ПОХОД тоже. Взял цель — идёт до конца, пока есть тяга:
        // мигание доступности (жертва на миг вышла из поля зрения §81.12,
        // prowl у кромки лагеря) роняло его в «посидеть» на полпути, и со
        // стороны это читалось как «его колбасит туда-сюда». Перебить поход
        // может только нокаут — без сознания решений не принимают вовсе.
        // Drive считается напрямую: верхний блок мог не дойти до него из-за
        // своих гейтов, а тяга у идущего должна оцениваться всегда.
        if (npc.Mind.CurrentGoal == GoalType.Abuse && AbuseMath.Drive(npc) > 0f)
        {
            abuseAvail = true;
        }

        AddGoalScore(npc, world.Tick, GoalType.Abuse,
            Spec81.AbuseBaseScore + abuseDrive, abuseAvail);
        if (raidAvail && world.Tick % 64 == 0)
        {
            Trace.Emit(world, npc.Id, "RaidScored",
                raidVictim is null
                    ? "Victim=none (prowling)"
                    : $"Victim=NPC{raidVictim.Id.Value} Opp={raidOpportunity:F2} " +
                      $"Allies={RaidMath.AlliesAround(world, raidVictim)} " +
                      $"VictimHealth={raidVictim.Health:F2}");
        }

        // Spec 35.3 + §52: build a hut piece when the full bill is carried
        // AND a hammer is in hand — raising a wall now needs the tool.
        var buildAvail = ctx.CanUseToolsOrWeapons && piece is { } needNow &&
            carriedLogs >= needNow.Logs && stoneCount >= needNow.Stones &&
            carriedLeaves >= needNow.Leaves && hasHammer &&
            HasReachableWithTag(npc, world, "BuildSite");
        AddGoalScore(npc, world.Tick, GoalType.Build, 0.4f + freeHands, buildAvail);

        // Spec §52: haul a material to the furniture build-site, or raise it
        // with a hammer once stocked. Weighted like the retired CraftBed
        // (0.55) so a girl already carrying the stones actually delivers them
        // instead of wandering off — otherwise materials never reach the site.
        // §54 cold start: raising the first hearth outranks the day's chores.
        // §54.10: a bed delivery is lifted over peacetime leisure too, so a
        // girl carrying a bundle actually walks it to the site and taps it home.
        // §54.13: a STAGED site (the beds) takes BUNDLES, not single pieces.
        // Delivery (0.55+) outbid every gather goal (≤0.95) the moment ONE
        // piece was in hand, so the 46-leaf mattress became 46 round trips
        // (soak: leaf deliveries 1/46, 2/46, 3/46… spaced ~50-200 ticks).
        // A girl now keeps gathering until she carries the stage's shortfall
        // (capped at half a stack) — or until nothing more can be produced —
        // and only then walks the pile over. Non-bed sites (the campfire's
        // stone/spit upgrades §54.14, hut pieces) still take any piece:
        // stones don't stack.
        var deliverWorthwhile = buildSite != null && buildWindow &&
            CarriesSiteMaterial(npc, buildSite);
        // §63 r2: the pickaxe's own 2-stone budget is RESERVED — while the
        // site still wants more stones than she holds and no pickaxe
        // exists, her stones go to the craft, not the pile (otherwise the
        // 0.55 delivery bid ate the budget every round and the 18-stone
        // ring could only ever be fed pebble-by-pebble from the ground).
        if (deliverWorthwhile && siteNeedsStones && !hasPickaxe &&
            stoneCount <= pickaxeStoneCost &&
            BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialStones) > pickaxeStoneCost)
        {
            var carriesOtherNeededMaterial = false;
            foreach (var mat in BuildSiteMath.AllMaterials)
            {
                if (mat != BuildSiteMath.MaterialStones &&
                    BuildSiteMath.Needs(buildSite, mat) &&
                    npc.Inventory.Items.Contains(mat))
                {
                    carriesOtherNeededMaterial = true;
                    break;
                }
            }

            if (!carriesOtherNeededMaterial)
            {
                deliverWorthwhile = false;
            }
        }

        if (deliverWorthwhile && siteIsBed)
        {
            foreach (var mat in BuildSiteMath.AllMaterials)
            {
                var stageRemaining = BuildSiteMath.Remaining(buildSite, mat);
                if (stageRemaining <= 0)
                {
                    continue;
                }

                var carriedOfStage = 0;
                foreach (var item in npc.Inventory.Items)
                {
                    if (item.DefinitionId == mat)
                    {
                        carriedOfStage++;
                    }
                }

                var canGetMore = mat switch
                {
                    BuildSiteMath.MaterialSticks => splitLogAvail || gatherWoodAvail,
                    BuildSiteMath.MaterialRope => craftRopeAvail || gatherFiberAvail || harvestYuccaAvail,
                    BuildSiteMath.MaterialLeaves => gatherLeavesAvail || chopCrownAvail,
                    BuildSiteMath.MaterialLogs => gatherWoodAvail,
                    _ => false
                };
                var bundle = System.Math.Min(
                    stageRemaining, InventoryState.StackSizeFor(mat) / 2);
                deliverWorthwhile = carriedOfStage >= bundle ||
                    (carriedOfStage > 0 && !canGetMore);
                break;
            }
        }

        var buildFurnitureAvail = buildFurnitureRaise || deliverWorthwhile;
        // §64.9: logs join the delivery pull — the raise half was the only
        // way a log stage could ever be advanced, and it was unweighted.
        var buildFurniturePull =
            (siteNeedsLeaves || siteNeedsSticks || siteNeedsRope || siteWantsLogs) ? 0.2f : 0f;
        // §54.14 (r2): the hearth is built FOR warmth/comfort — a cold girl
        // pushes the stick-pile delivery with the same cold weight that
        // drives the rest of the fire chain (there is no fire to tend yet).
        AddGoalScore(npc, world.Tick, GoalType.BuildFurniture,
            (hearthUrgent ? 0.95f : 0.55f) + freeHands + buildFurniturePull + dreamPull +
            (siteIsHearth ? coldChain : 0f), buildFurnitureAvail);

        // Spec §52: free a slot by carrying a low-value item to the fireside
        // stockpile — but only in peace. Life-threatening pressure (a dog, a
        // stat below 40 %) cancels the errand: you don't tidy your pockets
        // while something is trying to eat you.
        var lifeThreatened = npc.IsFighting || npc.Memory.Dangers.Count > 0 ||
            npc.Health < 0.4f || npc.Needs.Hunger >= 0.6f || npc.Needs.Thirst >= 0.6f;
        var haulVictim = InventoryMath.LowestImportanceDroppable(world, npc);
        var haulToFireAvail = !npc.Inventory.HasSpace && !lifeThreatened && campfireSeen &&
            haulVictim != null &&
            InventoryMath.Importance(world, haulVictim) <= 25;
        AddGoalScore(npc, world.Tick, GoalType.HaulToFire, 0.28f, haulToFireAvail);

        // Spec 35.4: overheating drives a trip to shade or the river. Gate on
        // the latched IsOverheated (enter 0.35 / clear 0.20) rather than a raw
        // 0.35 compare, so availability doesn't flicker on/off around the edge
        // and re-win at zero margin every tick (the old None→CoolOff churn).
        var coolOffUrge = System.MathF.Max(npc.Needs.ThermalDiscomfort, npc.SunExposure - 0.4f);
        var coolOffAvail = ((ctx.EffectiveTemp > 20f && npc.Mind.IsOverheated) ||
                npc.SunExposure >= 0.6f) &&
            (HasReachableWithTag(npc, world, "Shade") || HasReachableWithTag(npc, world, "Water"));
        AddGoalScore(npc, world.Tick, GoalType.CoolOff,
            0.1f + 0.5f * coolOffUrge, coolOffAvail);

        // Laundry audit (Jul 2026): dirty WORN clothing pulls Bathe — she
        // undresses at the shore anyway, and the beached pile is what the
        // existing WashClothes chain can actually target (worn dirt was
        // otherwise invisible to it: garments sat at dirt 0.7-1.0 forever).
        // §40.6: an owed post-bathe redress forces Bathe to stay selected —
        // its plan is what walks her back to the pile and re-dresses her.
        // batheNeed pinned high so a wet-chill Dress urge can't outbid the
        // very goal that will put her clothes back on.
        var batheNeed = ctx.PendingRedress
            ? 1f
            : System.MathF.Max(1f - npc.Needs.Hygiene,
                EquipmentMath.WorstDirtiness(npc) * SimBalance.BatheWornDirtWeight);
        // §63: no spa while bleeding out — a mauled girl (blood < 0.6)
        // planned an 80-tick wash at the far shore between bleed ticks.
        // §89: чужаку на быт ПЛЕВАТЬ (см. подробный комментарий у стирки
        // ниже). Купание — из той же корзины: это занятие человека, у
        // которого всё хорошо.
        var caresAboutGrooming = npc.Faction == Faction.Colony;
        var batheAvail = caresAboutGrooming &&
            (ctx.PendingRedress ||
             (batheNeed >= SimBalance.BatheNeedThreshold &&
              npc.Needs.Blood >= 0.6f &&
              HasReachableBathTile(world, npc) && npc.Body.CanUseToolsOrWeapons));
        if (npc.Mind.CurrentGoal == GoalType.Bathe && npc.Plan.Status == PlanStatus.Active)
        {
            batheAvail = true;
        }
        // Behavior audit (Jul 2026): hygiene chores are settled-hands work
        // too — at bare need×0.7 they NEVER beat Sit/Dress (25-day soaks:
        // Bathed 0, WashClothes 0 across 10 seeds), so worn dirt/festering
        // hygiene simply accumulated. The same freeHands surplus that sends
        // a settled girl to the rope pile sends her to the waterline.
        // …and once she has COMMITTED to the trip (plan active, walking to
        // the waterline), petty leisure must not cancel it mid-way — the
        // Jul 2026 trace showed every wash walk interrupted by Socialize/
        // Sit at ~0.9 vs wash 0.75, so 15 attempts → 0 finished washes.
        // Real needs (thirst/hunger ~2.0, danger) still preempt freely.
        var batheActive = npc.Mind.CurrentGoal == GoalType.Bathe &&
            npc.Plan.Status == PlanStatus.Active;
        // §40.6: a pending redress adds a strong pull so the return-and-dress
        // beats petty leisure AND the wet-chill Dress urge — but stays below
        // the ~2.0 emergency band, so real danger/starvation still preempts
        // (she resumes the redress afterwards via BuildBathePlan).
        AddGoalScore(npc, world.Tick, GoalType.Bathe,
            batheNeed * 0.7f + freeHands + (batheActive ? 0.35f : 0f) +
                (ctx.PendingRedress ? 1f : 0f),
            batheAvail);

        var washNeed = DirtyGarmentWashNeed(world, npc);
        // §89: чужаку на быт ПЛЕВАТЬ. Он не колонист: ему не нужна чистая
        // рубаха, ему нужно, чтобы его боялись. Стирка, купание и прочий
        // уход за собой — это то, чем занимаются люди, у которых всё
        // хорошо, а у него нужда в общении в нуле и закрыть её можно
        // только силой.
        //
        // Гейт по фракции, а не по ставке: понижать вес бесполезно, он
        // всё равно всплывёт, когда остальные дела кончатся, и человек
        // опять пойдёт полоскать чистую рубаху вместо дела.
        var washAvail = caresAboutGrooming &&
            washNeed >= SimBalance.WashClothesNeedThreshold &&
            npc.Needs.Blood >= 0.6f;
        var washActive = npc.Mind.CurrentGoal == GoalType.WashClothes &&
            npc.Plan.Status == PlanStatus.Active;
        if (washActive)
        {
            washAvail = true;
        }
        AddGoalScore(npc, world.Tick, GoalType.WashClothes,
            washNeed * 0.75f + freeHands + (washActive ? 0.35f : 0f), washAvail);

        // Spec 35.5: rain, wet clothes, and the drying chain.
        var wornWetness = 0f;
        foreach (var wornItem in npc.WornItems)
        {
            wornWetness = System.MathF.Max(wornWetness, wornItem.Wetness);
        }


        // Spec 29G: the bed must be earned — 2 logs + 3 palm leaves.
        // The hearth outranks the mattress: never spend logs on a bed
        // while the fire is hungry (777 soak: the bed ate the only wood
        // and the fire never burned again — boiledDrinks=0 all game).
        // Spec 40.14: 3 leaves alone weave the cheap leaf mat; 2 spare logs
        // upgrade it to the solid bedroll (chosen at craft time). The fire
        // must still be alive — its logs are never robbed for the bedroll.
        // §47 comfort: two locks removed. (1) "any reachable bed"
        // capped the colony at ONE crafted bed for three girls —
        // bedDeficit (beds < living girls) lets everyone earn her own.
        // (2) campfireFuel > 0 was the same permanent lock the raft and
        // fire chains had (the pit burns 10-20% of the time; CraftBed
        // never appeared in ANY soak's won-auction top-14) — weaving at
        // the cold pit is fine, the Craft interaction never needed the
        // flame anyway.
        // Spec §54.2: the leaf mat is no longer an atomic craft. BedSiteSystem
        // stakes a bed.leaf build-site by the hearth, and the girls haul the
        // 16 leaves + 6 sticks to it over many trips via the BuildFurniture
        // chain (below) — the mat grows piece by piece, a hammer finishes it.
        // So CraftBed is retired here; leave it disabled rather than churn the
        // Craft dispatch/catalog it still nominally routes through.
        var craftBedAvail = false;
        AddGoalScore(npc, world.Tick, GoalType.CraftBed, 0.6f, craftBedAvail);

        // Spec 40.14 / §66: the tent is RETIRED (user: the lean-to canopy is
        // legacy junk — such builds must not exist). Like CraftBed above, the
        // goal is left disabled rather than churning the Craft dispatch it
        // still nominally routes through; BedSiteSystem also sweeps any
        // already-built shelter.tent out of loaded worlds.
        var craftTentAvail = false;
        AddGoalScore(npc, world.Tick, GoalType.CraftTent,
            0.25f + 0.3f * world.Environment.UvIndex, craftTentAvail);

        // Spec 40.15: the escape raft — a low-priority background project.
        // Only when survival is handled (fed, watered, no danger) does a
        // log get carried to the coast; the way off the island is earned
        // slowly, never at the expense of staying alive. No fire clause:
        // !fuelLow (fuel >= 600) was almost never true in the campfire
        // era, and even "fire burning" holds <16% of the time on 10-day
        // soaks — either variant locks the endgame out permanently.
        // Hunger/thirst/danger already express "the household can spare
        // a pair of hands"; TendFire outbids the raft when fuel matters.
        // Balance audit (Jul 2026): gate relaxed — see raftDangerNear /
        // raftWoodDemand above for the rationale and the measurements.
        var buildRaftAvail = SimBalance.RaftEnabled &&
            ctx.CanUseToolsOrWeapons && carriedLogs >= 1 && world.RaftProgress < WorldState.RaftTarget &&
            npc.Needs.Hunger < SimBalance.RaftNeedGate && npc.Needs.Thirst < SimBalance.RaftNeedGate &&
            !raftDangerNear && KnowsReachableWithTag(npc, world, "Raft");
        // A loaded girl leans coastward: each carried log adds pull so
        // the stocked-up trip actually happens instead of dissolving
        // into strolls. Base 0.4 (was 0.28): plan statistics showed the
        // old weight won the auction 1-5 times in 15 days — the endgame
        // needs to outbid moderate needs (~0.6) whenever the gate is
        // open, and the gate itself already guarantees she's fed,
        // watered and safe when she commits to the coast run.
        AddGoalScore(npc, world.Tick, GoalType.BuildRaft,
            0.4f + freeHands + 0.05f * carriedLogs, buildRaftAvail);
        // §35.5B: CraftRack retired — the rack is a staged fireside
        // build-site now (BedSiteSystem stakes it; BuildFurniture raises).

        // Drying audit (Jul 2026): the old gate (wetness > 0.5) plus a
        // 0.15+0.4×wet score meant the rack was NEVER used (ItemHung=0
        // across 40-day soaks) — passive on-body drying closed the window
        // in ~0.2 days and Sit outbid the trip. Wider window + a score
        // that actually wins the auction right after a wash or a soaking.
        var dryAvail = wornWetness > SimBalance.DryClothesWetThreshold &&
            !world.Environment.IsRaining &&
            (HasReachableWithTag(npc, world, "Rack") ||
             (campfireSeen && campfireFuel > 0f));
        AddGoalScore(npc, world.Tick, GoalType.DryClothes,
            0.2f + 0.6f * wornWetness, dryAvail);


        // Spec 31A.5A: hot and safe → take something off. If everything
        // warm is also armor under fresh danger, keep sweating. Spec 42:
        // only in REAL heat (past the [16,22] band) — day-warmth must not
        // strip the layers that the cold night needs back in an hour.
        var undressAvail = ctx.EffectiveTemp > 24f && npc.Needs.ThermalDiscomfort >= 0.4f &&
            FindRemovableItem(npc, world) is not null;
        AddGoalScore(npc, world.Tick, GoalType.Undress, npc.Needs.ThermalDiscomfort, undressAvail);

        // Spec 29C.5: seeded wandering urge, changes every 160 ticks —
        // occasionally beats Idle, loses to any pressing need.
        var exploreJitter = MathUtil.Hash01(world.Seed, world.Tick / 160, npc.Id.Value, 77) * 0.2f;
        // Spec 31C.7A: a settled colony strolls — long rests made the
        // girls genuinely idle, and idle should wander, not loiter.
        // ...and only when the hearth is in order — full-time tourism
        // collapsed the fire/craft economy on the first soak.
        // Comfort > 0.35 (was 0.5): with beds earned rather than given
        // (spec 29G) comfort is a luxury — the old bar made wanderlust
        // unreachable and the outings gate starved (777: explores=0).
        var wellRested = npc.Needs.Hunger < 0.5f && npc.Needs.Thirst < 0.5f &&
            npc.Needs.Energy > 0.5f && npc.Needs.Comfort > 0.35f && !fuelLow ? 0.15f : 0f;
        AddGoalScore(npc, world.Tick, GoalType.Explore, 0.05f + exploreJitter + wellRested, true);

        AddGoalScore(npc, world.Tick, GoalType.Idle, 0.05f, true);

        // §53.7 — THE AID ERRAND. She means to help, and has nothing to
        // give: the compassion pull spills into the chore that FETCHES the
        // missing supply, so a hungry friend sends her foraging, a parched
        // one sends her to the water, and an untended wound sends her out
        // for plantain and back to the fire to bind a dressing.
        //
        // The spill is scored, not forced: it rides in as an ordinary bid
        // at AidErrandBidShare of the aid pull, so her own emergencies still
        // outrank it and "no other important business" stays a real gate.
        // Only ever raised for a chore that can actually run right now
        // (something to gather, a hand free, no cooldown) — resurrecting an
        // impossible goal is the classic PlanFailed churn loop.
        aidErrandGoal = null;
        aidErrandBid = 0f;
        aidErrandKindNow = errandKind;
        aidErrandTargetNow = errandTarget;
        if (Spec53.Enabled && Spec53.AidCostsSupplies && aidSelfOk)
        {
            if (aidErrandKindNow == AidKind.None && npc.Mind.AidErrandKind != AidKind.None)
            {
                // Out of sight behind her — keep the errand she started.
                aidErrandKindNow = npc.Mind.AidErrandKind;
                aidErrandTargetNow = npc.Mind.AidErrandFor;
                aidErrandBid = npc.Mind.AidErrandBid;
            }
            else if (aidErrandKindNow != AidKind.None)
            {
                aidErrandBid = errandFullBid;
            }

            aidErrandGoal = aidErrandKindNow switch
            {
                AidKind.Feed => ctx.FoodFetchPossible && npc.Inventory.HasSpace
                    ? GoalType.GetFood
                    : (GoalType?)null,
                AidKind.Hydrate => waterFetchPossible ? GoalType.GetWater : (GoalType?)null,
                // Treat/Medicate share the herb chain: leaves first, then
                // the dressing is bound at the fire.
                AidKind.Treat or AidKind.Medicate => bandageCraftPossible
                    ? GoalType.CraftBandage
                    : (herbFetchPossible ? GoalType.GatherHerb : (GoalType?)null),
                _ => null
            };

            if (aidErrandGoal is { } errandGoal && aidErrandBid > 0f &&
                !IsOnCooldown(npc, errandGoal, world.Tick))
            {
                RaiseGoalScore(npc, errandGoal, aidErrandBid);
            }
            else
            {
                // "She just stood there while her friend starved" is the
                // question this answers: there was a ward she wanted to
                // help and no way to go and get what it takes. Sampled
                // (every 64 ticks) so a long dry spell can't flood the log.
                if (aidErrandKindNow != AidKind.None && world.Tick % 64 == 0)
                {
                    Trace.Emit(world, npc.Id, "AidErrandBlocked",
                        $"Kind={aidErrandKindNow} Bid={aidErrandBid:F2} " +
                        $"NoChore=[food={ctx.FoodFetchPossible} space={npc.Inventory.HasSpace} " +
                        $"water={waterFetchPossible} herb={herbFetchPossible} " +
                        $"craft={bandageCraftPossible}]");
                }

                aidErrandGoal = null;
            }
        }

    }
    /// <summary>
    /// §53: сострадание. Кто рядом страдает, могу ли я помочь — и если помочь
    /// нечем, за чем идти.
    ///
    /// <para>
    /// Цельная подсистема: она сама решает свою ставку и сама оставляет метку
    /// поручения. Из скоринга её видно тремя величинами на выходе, а не сотней
    /// строк посреди чужих целей.
    /// </para>
    /// </summary>
    private static void ScoreCompassion(
        WorldState world, NPCState npc, in DecisionContext ctx,
        out bool aidSelfOk, out AidKind errandKind,
        out EntityId? errandTarget, out float errandFullBid)
    {
        aidSelfOk = false;
        errandKind = AidKind.None;
        errandTarget = null;
        errandFullBid = 0f;

    // Spec §53: compassion — tend the worst-off reachable housemate
    // (feed/treat/medicate/console). Gated HARD behind the helper's own
    // survival: a girl in her own crisis (starving, dehydrated, badly
    // hurt or bleeding, fighting, fleeing) looks after herself first.
    // The bid scales with the sufferer's plight × this girl's personality
    // CompassionTrait, plus the accumulated pressure of a spent
    // Compassion need — so a caring girl (high trait) will outbid a bed
    // build for a dying housemate, a reserved one only helps when idle.
    // §53.7: help is PAID FOR out of the helper's own pack, so the
    // scan splits in two — the worst-off housemate she can help RIGHT
    // NOW (bids Aid), and the worst-off one she'd have to fetch a
    // supply for first (bids the fetching chore, below).
    var aidAvail = false;
    var bestSuffering = 0f;
    var bestSuffererAffinity = 0f;
    var bestAidKind = AidKind.None;
    var bestSuffererDying = false; // §105
    var errandSuffering = 0f;
    errandKind = AidKind.None;
    errandTarget = null;
    aidSelfOk = false;
    if (Spec53.Enabled)
    {
        var selfOk = !npc.Mind.IsStarving && !npc.Mind.IsDehydrated &&
            !npc.IsFighting && npc.Mind.CurrentGoal != GoalType.Flee &&
            npc.Needs.Hunger < Spec53.SelfHungerGate &&
            npc.Health >= Spec53.SelfHealthGate &&
            npc.Needs.Blood >= Spec53.SelfHealthGate;
        aidSelfOk = selfOk;
        if (selfOk)
        {
            foreach (var agent in npc.Perception.Agents)
            {
                if (agent.AidKind == AidKind.None ||
                    agent.Suffering < Spec53.SufferingThreshold ||
                    !agent.IsReachable || agent.IsBusy || agent.IsMoving)
                {
                    continue;
                }

                // Nothing to give? She still WANTS to help — remember
                // her as the errand and go and get it (§53.7).
                if (!AidSupply.Has(world, npc, agent.AidKind))
                {
                    if (agent.Suffering > errandSuffering)
                    {
                        errandSuffering = agent.Suffering;
                        errandKind = agent.AidKind;
                        errandTarget = agent.Id;
                    }

                    continue;
                }

                if (agent.Suffering > bestSuffering)
                {
                    bestSuffering = agent.Suffering;
                    bestSuffererAffinity = agent.Relationship.Affinity;
                    bestAidKind = agent.AidKind;
                    bestSuffererDying = agent.IsDying; // §105
                    aidAvail = true;
                }
            }
        }
        // An in-flight aid (walking to the sufferer or mid-care) keeps its
        // goal available so the availability scan can't zero a live plan.
        var curIt = npc.Execution.CurrentInteraction;
        var inAidExec = curIt == InteractionType.FeedOther ||
            curIt == InteractionType.HydrateOther ||
            curIt == InteractionType.TreatOther ||
            curIt == InteractionType.MedicateOther ||
            curIt == InteractionType.ConsoleOther;
        if ((npc.Plan.Status == PlanStatus.Active &&
             npc.Mind.CurrentGoal == GoalType.Aid && npc.Plan.TargetAgentId is not null) ||
            (npc.Execution.Status == ExecutionStatus.InProgress && inAidExec))
        {
            aidAvail = true;
        }
    }
    // §105: соседка УМИРАЕТ — надбавка полная, а не три четверти. Спасение
    // обгоняет любую работу и любую другую помощь; собственный кризис
    // помощницы (гейт selfOk выше, §53.5) по-прежнему сильнее — сначала
    // выживает она сама, иначе на земле окажутся обе.
    var aidEmergency = bestSuffererDying
        ? Spec105.RescueEmergencyBoost * StarvingBoost
        : bestAidKind == AidKind.Treat && bestSuffering >= 0.65f
            ? StarvingBoost * 0.75f
            : 0f;
    AddGoalScore(npc, world.Tick, GoalType.Aid,
        bestSuffering * npc.CompassionTrait * Spec53.AidWeight +
            (1f - npc.Needs.Compassion) * Spec53.PressureWeight,
        aidAvail, aidEmergency, social: System.Math.Max(0f, bestSuffererAffinity) * 0.05f);

    // §53.7: the errand bid MIRRORS the aid bid — same suffering ×
    // trait × weight, same compassion pressure, same bleed-out
    // emergency — and is only then shaded by AidErrandBidShare. It has
    // to: a girl who WOULD have walked over and helped must not simply
    // shrug because her hands are empty. An errand worth strictly less
    // than the aid it serves would lose to the very chores the aid used
    // to outbid, and the ward would starve beside a willing helper.
    var errandNeed = errandSuffering * npc.CompassionTrait * Spec53.AidWeight +
        (1f - npc.Needs.Compassion) * Spec53.PressureWeight;
    var errandEmergency = errandKind == AidKind.Treat && errandSuffering >= 0.65f
        ? StarvingBoost * 0.75f
        : 0f;
    errandFullBid = (0.1f + errandNeed + errandEmergency) * Spec53.AidErrandBidShare;

    // §53.7: an errand already under way keeps pulling even when the
    // sufferer is out of sight behind her — but it ends the moment the
    // supply is in hand (that is the whole point), when the ward is
    // beyond help or gone, when her own body drops into the red, or
    // when the window runs out.
    if (npc.Mind.AidErrandKind != AidKind.None)
    {
        string? errandDone = null;
        if (!Spec53.Enabled || !Spec53.AidCostsSupplies)
        {
            errandDone = "disabled";
        }
        else if (!aidSelfOk)
        {
            errandDone = "helper in her own crisis";
        }
        else if (world.Tick >= npc.Mind.AidErrandUntilTick)
        {
            errandDone = "timed out";
        }
        else if (AidSupply.Has(world, npc, npc.Mind.AidErrandKind))
        {
            errandDone = "supply in hand";
        }
        else if (npc.Mind.AidErrandFor is not { } wardId ||
                 !world.Entities.Npcs.TryGetValue(wardId, out var ward) ||
                 ward.Health <= 0f)
        {
            errandDone = "ward gone";
        }

        if (errandDone is not null)
        {
            Trace.Emit(world, npc.Id, "AidErrandCleared",
                $"Kind={npc.Mind.AidErrandKind} Reason={errandDone}");
            npc.Mind.AidErrandKind = AidKind.None;
            npc.Mind.AidErrandFor = null;
            npc.Mind.AidErrandUntilTick = 0;
            npc.Mind.AidErrandBid = 0f;
        }
    }
    }


    /// <summary>
    /// Выбор цели: печать входа, разложение оценок, максимум и правило
    /// «держать или сменить» (§23.8/23.9).
    ///
    /// <para>
    /// Вынесено из тела цикла, где это было последними ста строками
    /// девятнадцатисотстрочного метода. Само решение — не самая длинная его
    /// часть, но самая ЧИТАЕМАЯ: сюда приходят с готовыми оценками, и весь
    /// арбитраж виден целиком, без прокрутки через семьдесят скоринг-блоков.
    /// </para>
    /// <para>
    /// Шесть флагов доступности собраны в <see cref="AvailabilityTrace"/>: они
    /// нужны ровно одной строке трассы и параметрами только шумели бы.
    /// </para>
    /// </summary>
    private static void ChooseGoal(
        WorldState world, NPCState npc, GoalType previousGoal,
        in AvailabilityTrace avail,
        GoalType? aidErrandGoal, AidKind aidErrandKindNow,
        EntityId? aidErrandTargetNow, float aidErrandBid)
    {
        Trace.Emit(world, npc.Id, "DecisionInput",
            $"Needs=[{Trace.FormatNeeds(npc.Needs)}] " +
            $"Available=[Eat={avail.EatAvail} GetFood={avail.GetFoodAvail} Sleep={avail.SleepAvail} Sit={avail.SitAvail} " +
            $"Dress={avail.DressAvail} Socialize={avail.SocializeAvail}] " +
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
            $"PrevGoal={previousGoal} PlanStatus={npc.Plan.Status} ExecStatus={npc.Execution.Status}");

        GoalScore? best = null;
        foreach (var score in npc.Mind.LastScores)
        {
            Trace.Emit(world, npc.Id, "GoalScored",
                $"{score.Goal}: Base={score.BaseScore:F3} Need={score.NeedModifier:F3} " +
                $"Soc={score.SocialModifier:F3} " +
                $"Env={score.EnvironmentModifier:F3} " +
                $"Emg={score.EmergencyModifier:F3} " +
                $"=> Final={score.FinalScore:F3}");

            if (best is null || score.FinalScore > best.FinalScore)
            {
                best = score;
            }
        }

        if (best is null)
        {
            Trace.Emit(world, npc.Id, "DecisionSkipped", "No scores available");
            return;
        }

        // Spec 23.8/23.9 (iteration 3): hold-or-adopt. A competing goal must
        // beat the current one by a margin; locks resist ordinary drift.
        if (best.Goal != previousGoal &&
            previousGoal != GoalType.None && previousGoal != GoalType.Idle)
        {
            var currentScore = 0f;
            foreach (var score in npc.Mind.LastScores)
            {
                if (score.Goal == previousGoal)
                {
                    currentScore = score.FinalScore;
                    break;
                }
            }

            var hasActivePlan = npc.Plan.Status == PlanStatus.Active ||
                npc.Execution.Status == ExecutionStatus.InProgress;
            var locked = npc.Mind.GoalLock is { } goalLock &&
                goalLock.Goal == previousGoal && world.Tick < goalLock.EndTick;
            var threshold = locked ? LockOverrideDelta : (hasActivePlan ? SwitchDelta : 0f);

            if (best.FinalScore - currentScore <= threshold)
            {
                Trace.Emit(world, npc.Id, "GoalHeld",
                    $"{previousGoal} kept over {best.Goal} " +
                    $"(lead={best.FinalScore - currentScore:F3} <= {threshold:F2}" +
                    $"{(locked ? $", locked until {npc.Mind.GoalLock!.EndTick}" : "")})");
                return;
            }
        }

        npc.Mind.CurrentGoal = best.Goal;
        npc.Mind.LastDecision = new DecisionResult
        {
            SelectedGoal = best.Goal,
            Reason = $"Selected {best.Goal} at tick {world.Tick}"
        };

        foreach (var score in npc.Mind.LastScores)
        {
            npc.Mind.LastDecision.Scores.Add(score);
        }

        var changed = previousGoal != best.Goal;
        if (changed && best.Goal != GoalType.Idle && best.Goal != GoalType.None)
        {
            npc.Mind.GoalLock = new GoalLock
            {
                Goal = best.Goal,
                StartTick = world.Tick,
                EndTick = world.Tick + GoalLockTicks
            };
        }

        Trace.Emit(world, npc.Id, "GoalSelected",
            $"{best.Goal} (Score={best.FinalScore:F3}) " +
            $"{(changed ? $"CHANGED from {previousGoal}" : "UNCHANGED")}");

        // §53.7: the chore she just picked IS the errand — stamp it so it
        // keeps its aid weight while she walks out of the sufferer's sight,
        // and so the trace says WHY a well-fed girl went foraging.
        if (aidErrandGoal is { } stampedErrand && best.Goal == stampedErrand &&
            aidErrandKindNow != AidKind.None)
        {
            var fresh = npc.Mind.AidErrandKind != aidErrandKindNow ||
                npc.Mind.AidErrandFor?.Value != aidErrandTargetNow?.Value;
            npc.Mind.AidErrandKind = aidErrandKindNow;
            npc.Mind.AidErrandFor = aidErrandTargetNow;
            npc.Mind.AidErrandBid = aidErrandBid;
            npc.Mind.AidErrandUntilTick = world.Tick + Spec53.AidErrandTicks;
            if (fresh)
            {
                Trace.Emit(world, npc.Id, "AidErrandStarted",
                    $"Kind={aidErrandKindNow} " +
                    $"For={(aidErrandTargetNow is { } ward ? $"NPC{ward.Value}" : "unknown")} " +
                    $"Goal={stampedErrand} Bid={aidErrandBid:F2}");
            }
        }

        // Spec 23.17: a goal change over an active plan must abort cleanly,
        // releasing object occupancy and junction reservation before replanning.
        if (changed &&
            (npc.Plan.Status == PlanStatus.Active || npc.Execution.Status == ExecutionStatus.InProgress))
        {
            PlanInterruption.Abort(world, npc,
                $"Goal changed {previousGoal}->{best.Goal} over active plan " +
                $"(Starving={npc.Mind.IsStarving})");
        }
    }

    private static bool IsBleedingCrisis(NPCState npc)
    {
        if (npc.Needs.Blood >= 0.45f)
        {
            return false;
        }

        var worstPart = 1f;
        foreach (var part in npc.Body.Parts.Keys)
        {
            if (npc.Body.Parts[part] < worstPart)
            {
                worstPart = npc.Body.Parts[part];
            }
        }

        if (worstPart >= 0.4f)
        {
            return false;
        }

        foreach (var wound in npc.Wounds)
        {
            if (wound.Heal01 < 0.3f && wound.Severity >= 0.05f)
            {
                return true;
            }
        }

        return false;
    }

    private static void SuppressPeacetimeDuringBleeding(NPCState npc)
    {
        foreach (var score in npc.Mind.LastScores)
        {
            if (IsBleedingCrisisGoal(score.Goal, npc))
            {
                // Behavior audit (Jul 2026): the boost used to resurrect an
                // UNAVAILABLE CraftBandage (no herbs carried → FinalScore 0 →
                // +0.5) — the girl stood in a CraftBandage/ExecFailed loop
                // ("missing herb x2" every tick) until she bled out, while the
                // zeroed GatherHerb could have fetched the leaves. Boost only
                // a genuinely available craft.
                if (score.Goal == GoalType.CraftBandage && score.FinalScore > 0f)
                {
                    score.EmergencyModifier = System.MathF.Max(score.EmergencyModifier, StarvingBoost * 0.5f);
                    score.FinalScore += StarvingBoost * 0.5f;
                }

                continue;
            }

            score.FinalScore = 0f;
        }
    }

    private static bool IsBleedingCrisisGoal(GoalType goal, NPCState npc)
    {
        return goal switch
        {
            GoalType.Idle or GoalType.None => true,
            GoalType.Eat or GoalType.GetFood => npc.Mind.IsStarving,
            GoalType.Drink or GoalType.GetWater => npc.Mind.IsDehydrated,
            // Fetching the bandage's herbs IS the crisis response — zeroing it
            // left a herbless bleeder with literally nothing to do (Jul 2026).
            GoalType.CraftBandage or GoalType.GatherHerb => true,
            _ => false
        };
    }

    private static void UpdateDehydratedStatus(WorldState world, NPCState npc)
    {
        if (!npc.Mind.IsDehydrated && npc.Needs.Thirst >= StarvingEnterThreshold)
        {
            npc.Mind.IsDehydrated = true;
            Trace.Emit(world, npc.Id, "StatusDehydrated",
                $"Entered (Thirst={npc.Needs.Thirst:F2})");
        }
        else if (npc.Mind.IsDehydrated && npc.Needs.Thirst < StarvingClearThreshold)
        {
            npc.Mind.IsDehydrated = false;
            Trace.Emit(world, npc.Id, "StatusDehydrated",
                $"Cleared (Thirst={npc.Needs.Thirst:F2})");
        }
    }

    private static void UpdateStarvingStatus(WorldState world, NPCState npc)
    {
        if (!npc.Mind.IsStarving && npc.Needs.Hunger >= StarvingEnterThreshold)
        {
            npc.Mind.IsStarving = true;
            Trace.Emit(world, npc.Id, "StatusStarving",
                $"Entered (Hunger={npc.Needs.Hunger:F2} >= {StarvingEnterThreshold})");
        }
        else if (npc.Mind.IsStarving && npc.Needs.Hunger < StarvingClearThreshold)
        {
            npc.Mind.IsStarving = false;
            Trace.Emit(world, npc.Id, "StatusStarving",
                $"Cleared (Hunger={npc.Needs.Hunger:F2} < {StarvingClearThreshold})");
        }
    }

    // Spec 35.4: overheating latch. Enter at CoolOffEnterThreshold, clear at
    // CoolOffClearThreshold — the hysteresis stops CoolOff's availability from
    // flickering around the entry edge (the tight None→CoolOff→None churn loop).
    private static void UpdateOverheatedStatus(WorldState world, NPCState npc)
    {
        if (!npc.Mind.IsOverheated && npc.Needs.ThermalDiscomfort >= SimBalance.CoolOffEnterThreshold)
        {
            npc.Mind.IsOverheated = true;
            Trace.Emit(world, npc.Id, "StatusOverheated",
                $"Entered (Thermal={npc.Needs.ThermalDiscomfort:F2} >= {SimBalance.CoolOffEnterThreshold})");
        }
        else if (npc.Mind.IsOverheated && npc.Needs.ThermalDiscomfort < SimBalance.CoolOffClearThreshold)
        {
            npc.Mind.IsOverheated = false;
            Trace.Emit(world, npc.Id, "StatusOverheated",
                $"Cleared (Thermal={npc.Needs.ThermalDiscomfort:F2} < {SimBalance.CoolOffClearThreshold})");
        }
    }

    // Spec 23.10 gate, reused by the §53.7 errand spill: a goal on cooldown
    // must never be revived by an outside pull, or the abort that set the
    // cooldown just replays every tick (PlanFailed churn).
    private static bool IsOnCooldown(NPCState npc, GoalType goal, int currentTick)
    {
        foreach (var cooldown in npc.Mind.Cooldowns)
        {
            if (cooldown.Goal == goal && cooldown.EndTick > currentTick)
            {
                return true;
            }
        }

        return false;
    }

    // §53.7: lift an already-scored goal to at least `value` — the compassion
    // pull spilling into the chore that fetches the missing supply. Never
    // lowers a goal that already wants it more on its own account.
    private static void RaiseGoalScore(NPCState npc, GoalType goal, float value)
    {
        foreach (var score in npc.Mind.LastScores)
        {
            if (score.Goal != goal)
            {
                continue;
            }

            if (score.FinalScore < value)
            {
                score.SocialModifier += value - score.FinalScore;
                score.FinalScore = value;
            }

            return;
        }
    }

    private static void AddGoalScore(NPCState npc, int currentTick, GoalType goal, float needValue, bool isAvailable, float emergency = 0f, float social = 0f, float environment = 0f)
    {
        // Spec 23.10: goals on cooldown are hard-gated.
        var onCooldown = false;
        foreach (var cooldown in npc.Mind.Cooldowns)
        {
            if (cooldown.Goal == goal && cooldown.EndTick > currentTick)
            {
                onCooldown = true;
                break;
            }
        }

        var available = isAvailable && !onCooldown;
        var score = new GoalScore
        {
            Goal = goal,
            BaseScore = goal == GoalType.Idle ? 0.01f : 0.1f,
            NeedModifier = needValue,
            EmergencyModifier = emergency,
            SocialModifier = social,
            EnvironmentModifier = environment,
            FinalScore = available ? 0.1f + needValue + emergency + social + environment : 0f
        };

        npc.Mind.LastScores.Add(score);
    }
}

}
