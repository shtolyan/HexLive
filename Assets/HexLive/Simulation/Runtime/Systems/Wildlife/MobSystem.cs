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

// The ground-mob director (ex-DogSystem): targeting, chase, flee assessment,
// pack raids, spawning and death cleanup for world.Mobs — today that list holds
// the wolf/pack-dog, and every number it reads comes from MobCatalog per mob
// id, so new ground mobs plug into this same loop with their own MobStats
// instead of a parallel copy-pasted system.
public sealed class MobSystem : ISimulationSystem
{
    public string Name => nameof(MobSystem);

    public TickLayer Layer => TickLayer.Medium;

    private static int MaxDogs => WildlifeBalance.MaxDogs; // §46 difficulty pass: 2 -> 3 (12/12 wins at 2 — armed girls out-fought the pair)
    private static int RespawnCheckTicks => WildlifeBalance.DogRespawnCheckTicks; // §46: every 7200 ticks / 30 real minutes — sustained pack pressure, not one skirmish per arc

    // Per-mob combat/behaviour lives in MobCatalog — one config per mob type,
    // tuned by its own ScriptableObject. The ambient spawner/raid is keyed to
    // the DOG sheet (it spawns dogs); everything per-creature reads
    // Stats(dog) so a future tiger obeys its own numbers.
    private static Content.MobStats Dog => Content.MobCatalog.For(Content.MobIds.Dog);
    private static Content.MobStats Stats(Wildlife.MobState dog) => Content.MobCatalog.For(dog.MobId);
    private static float RaidChancePerDay => Dog.RaidChancePerDay;
    private static int RaidPackSize => Dog.RaidPackSize;
    private static int RaidDuskOffsetTicks => WildlifeBalance.RaidDuskOffsetTicks;
    private static int RaidLingerTicks => WildlifeBalance.RaidLingerTicks;
    private static int RaidDepartureBackstopTicks => WildlifeBalance.RaidDepartureBackstopTicks;
    private static int SpawnMinDistanceFromNpc => WildlifeBalance.DogSpawnMinDistanceFromNpc;
    private static float NpcStrikePerPass => SimBalance.NpcStrikePerPass;

    private readonly System.Collections.Generic.List<Wildlife.MobState> _deadDogs = new();
    private readonly System.Collections.Generic.List<Wildlife.MobState> _departed = new();
    private readonly System.Collections.Generic.List<EntityId> _deadNpcs = new();
    private readonly System.Collections.Generic.List<JunctionId> _spawnCandidates = new();

    public void Run(WorldState world)
    {
        // Spec 41.2 v2: the timer lives in WorldState so it survives a save.
        if (world.Tick >= world.NextMobSpawnCheckTick)
        {
            world.NextMobSpawnCheckTick = world.Tick + RespawnCheckTicks;
            while (world.Mobs.Count < MaxDogs)
            {
                if (!TrySpawnDog(world))
                {
                    break;
                }
            }
        }

        // §46 v2: the NIGHT RAID — a seeded swing catastrophe. Constant
        // damage knobs saturated at ~10-15% colony losses (the homeostat
        // absorbs steady pressure); real 50/50 tension needs rare spikes.
        // Roll is a pure function of (seed, cycle). Cycles 0-1 are a
        // grace period — a raid on an unestablished camp is a coin-flip
        // wipe with no story. Raid dogs are ordinary dogs: they can be
        // fought, fled, and they linger until killed.
        // NOTE: the index is the EVENT CYCLE (2400 ticks), not the visual day
        // (24000) — keying it off the stretched clock would have made raids
        // 10x rarer in REAL time. The cost is that a raid no longer lands at
        // the visual dusk: RaidDuskOffsetTicks is now "late in the cycle", so
        // raids arrive at 10 evenly spaced moments across the long day.
        var raidCycle = world.Tick / EnvironmentSystem.EventCycleTicks;
        var raidDusk = raidCycle * EnvironmentSystem.EventCycleTicks + RaidDuskOffsetTicks;
        if (raidCycle >= 2 && world.Tick >= raidDusk && world.Tick < raidDusk + 4 &&
            MathUtil.Hash01(world.Seed, raidCycle, 4646) < RaidChancePerDay)
        {
            var spawned = 0;
            for (var i = 0; i < RaidPackSize; i++)
            {
                // §46 v4: стая — ГОСТЬИ. Раньше здесь стоял голый
                // TrySpawnDog(world), то есть рейд спавнил мимо потолка
                // MaxDogs, а уйти собака могла только смертью: за сессии
                // население росло без предела (у игрока — 14 при потолке 2),
                // и каждая лишняя собака стоит и тика симуляции, и своего
                // скина в кадре.
                if (TrySpawnDog(world, world.Tick + RaidLingerTicks))
                {
                    spawned++;
                }
            }

            if (spawned > 0)
            {
                Trace.EmitSystem(world, "NightRaid",
                    $"{spawned} dogs at dusk of cycle {raidCycle}");
            }
        }

        EnforceResidentCap(world);
        DepartGuests(world);

        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.IsFighting = false;
        }

        _deadDogs.Clear();
        _deadNpcs.Clear();

        ResolveMobOverlaps(world);

        foreach (var dog in world.Mobs)
        {
            RunDog(world, dog);
            if (dog.Health <= 0f)
            {
                _deadDogs.Add(dog);
            }
        }

        foreach (var dead in _deadDogs)
        {
            world.Mobs.Remove(dead);
            // §135: зверя убили с добычей в зубах — конечность падает там, где
            // он упал, а не исчезает вместе с ним.
            MobLimbPrize.DropAtDeath(world, dead);
            Trace.EmitSystem(world, "DogKilled",
                $"Dog={dead.Id} at Tile={dead.Tile.Q},{dead.Tile.R}");
            // Spec §54: the fallen mob leaves a butcherable carcass; the
            // variant carries its mob id so the view shows the right body.
            ExecutionSystem.SpawnCarcass(world, dead.Tile, dead.Junction, dead.MobId);
            ForgetDangerAround(world, dead.Tile, 1); // §54.16: the fear dies with the beast
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f)
            {
                _deadNpcs.Add(npc.Id);
            }
        }

        foreach (var deadId in _deadNpcs)
        {
            RemoveDeadNpc(world, deadId);
        }
    }

    // §46 v4: остров держит MaxDogs ЖИТЕЛЕЙ; всё сверх того — гости, которым
    // назначается срок ухода. Правило самовосстанавливающееся, и в этом его
    // смысл: оно чинит не только новые миры, но и сейвы, накопившие стаю по
    // старому багу, без отдельной ветки миграции по версии блоба.
    //
    // Уходят по одному, а не всей толпой разом: срок разнесён на четверть
    // окна гостевания, чтобы остров пустел постепенно и это читалось как
    // «стая ушла», а не как «собаки исчезли».
    private void EnforceResidentCap(WorldState world)
    {
        var residents = 0;
        foreach (var dog in world.Mobs)
        {
            if (dog.LeavesAtTick == 0)
            {
                residents++;
            }
        }

        if (residents <= MaxDogs)
        {
            return;
        }

        // Самые новые (наибольший Id) уходят первыми — старожилы остаются.
        var stagger = 0;
        for (var i = world.Mobs.Count - 1; i >= 0 && residents > MaxDogs; i--)
        {
            var dog = world.Mobs[i];
            if (dog.LeavesAtTick != 0)
            {
                continue;
            }

            // Всегда строго в будущем: 0 — это «житель», и срок ухода, упавший
            // в ноль, молча вернул бы гостя в жители.
            dog.LeavesAtTick = System.Math.Max(1,
                world.Tick + RaidLingerTicks + stagger * (RaidLingerTicks / 4));
            stagger++;
            residents--;
            Trace.EmitSystem(world, "MobLeaving",
                $"Dog={dog.Id} over resident cap, leaves at tick {dog.LeavesAtTick}");
        }
    }

    // Гость уходит первым тиком после срока, когда его НИКТО НЕ ВИДИТ (§125) —
    // собака не должна испариться на глазах у колонистки. Если же колония
    // стоит прямо на нём и он не выходит из виду, срабатывает предохранитель:
    // иначе «временная» стая осталась бы навсегда, то есть ровно тем багом,
    // который мы и чиним.
    private void DepartGuests(WorldState world)
    {
        _departed.Clear();
        foreach (var dog in world.Mobs)
        {
            if (dog.LeavesAtTick <= 0 || world.Tick < dog.LeavesAtTick || dog.Health <= 0f)
            {
                continue;
            }

            // Уходит только СПОКОЙНАЯ собака: гость, растворившийся посреди
            // погони или драки, читался бы как пропажа противника, а не как
            // «стая ушла».
            var forced = world.Tick >= dog.LeavesAtTick + RaidDepartureBackstopTicks;
            if (!forced && (dog.Status != Wildlife.MobStatus.Roaming || IsSeenByColony(world, dog)))
            {
                continue;
            }

            _departed.Add(dog);
        }

        foreach (var dog in _departed)
        {
            world.Mobs.Remove(dog);
            Trace.EmitSystem(world, "MobLeft",
                $"Dog={dog.Id} at Tile={dog.Tile.Q},{dog.Tile.R}");
            ForgetDangerAround(world, dog.Tile, 1); // ушла — метка страха уходит с ней
        }
    }

    private static bool IsSeenByColony(WorldState world, Wildlife.MobState dog)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f)
            {
                continue;
            }

            if (HexSpatialMath.HexDistance(npc.Tile, dog.Tile) <= PerceptionMath.RadiusTiles(npc))
            {
                return true;
            }
        }

        return false;
    }

    private void ResolveMobOverlaps(WorldState world)
    {
        foreach (var dog in world.Mobs)
        {
            if (dog.Health <= 0f || !IsJunctionOccupiedByActor(world, dog.Junction, dog))
            {
                continue;
            }

            if (!world.Junctions.Items.TryGetValue(dog.Junction, out var junction))
            {
                continue;
            }

            foreach (var neighborId in junction.Neighbors)
            {
                if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor) ||
                    neighbor.Blocked || neighbor.Door ||
                    IsIndoorJunction(world, neighborId) ||
                    SpatialQueries.IsAllWaterJunction(world, neighborId) ||
                    IsJunctionOccupiedByActor(world, neighborId, dog))
                {
                    continue;
                }

                MoveDogTo(world, dog, neighbor);
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "DogUnstacked",
                        $"Dog={dog.Id} moved to Junction={neighborId.Value}");
                }
                break;
            }
        }
    }

    private void RunDog(WorldState world, Wildlife.MobState dog)
    {
        // §135: в зубах добыча — это ВСЯ его программа. Ни цели, ни погони, ни
        // драки: отойти подальше, встать, доесть. Ветка стоит до захвата цели
        // намеренно — иначе тот же проход снова навёл бы его на колонию.
        if (dog.IsCarryingLimb)
        {
            RunLimbCarry(world, dog);
            return;
        }

        // §135: лежащая конечность — готовая еда, за которую не надо драться.
        // Зверь без цели идёт за ней и ест, была драка или нет. Стоит ДО
        // захвата цели: голодный выбирает падаль, а не новую охоту.
        if (dog.TargetNpc is null && RunScentForLimb(world, dog))
        {
            return;
        }

        // Acquire/validate target.
        NPCState? target = null;
        if (dog.TargetNpc is { } targetId)
        {
            world.Entities.Npcs.TryGetValue(targetId, out target);
        }

        if (target is null)
        {
            dog.TargetNpc = null;
            dog.Status = Wildlife.MobStatus.Roaming;

            var bestDistance = int.MaxValue;
            // Spec 29C.3: after giving up a hopeless chase the dog ignores
            // prey for the hunt cooldown — otherwise it re-acquired the same
            // unreachable girl on the very next pass and never left the camp.
            // §135: сытый зверь не охотится вообще — он только что съел ногу.
            if (world.Tick >= dog.NextHuntAllowedTick && !MobLimbPrize.IsSated(world, dog))
            {
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    // Sanctuary (spec 29C.4A) + water (§106): unreachable-by-
                    // medium NPCs are never targets.
                    if (IsNpcInRefugeFrom(world, dog, npc))
                    {
                        continue;
                    }

                    var distance = HexSpatialMath.HexDistance(dog.Tile, npc.Tile);
                    if (distance <= Stats(dog).AggroRadiusTiles && distance < bestDistance)
                    {
                        bestDistance = distance;
                        target = npc;
                    }
                }
            }

            if (target is not null)
            {
                dog.TargetNpc = target.Id;
                dog.Status = Wildlife.MobStatus.Chasing;
                RememberDanger(world, target);
                Trace.EmitSystem(world, "DogAggro",
                    $"Dog={dog.Id} targets NPC{target.Id.Value} " +
                    $"(Dist={HexSpatialMath.HexDistance(dog.Tile, target.Tile)})");
            }
        }
        else if (HexSpatialMath.HexDistance(dog.Tile, target.Tile) > Stats(dog).AggroRadiusTiles + 3 ||
                 IsNpcInRefugeFrom(world, dog, target))
        {
            // Lost interest — target got far away or reached refuge: a door
            // (spec 29C.4A) or the water's edge (§106). The wolf lets go NOW,
            // not after the chase-stall timer — standing statue at the shore
            // read as a bug, not as patience.
            // Причина — в локальную: перенос строки ВНУТРИ дырки интерполяции
            // компилируется только с C# 11, а headless-проект
            // (HexLive.Simulation.Standalone) собирается на C# 9 — Unity это
            // проглатывала, а `dotnet build` падал, то есть гейты и соаки
            // просто не запускались. Текст трассы не изменился.
            var lostReason = IsNpcInSanctuary(world, target)
                ? " (went indoors)"
                : target.IsPlayingDead(world.Tick)
                    ? " (playing dead)"
                    : IsNpcInRefugeFrom(world, dog, target)
                        ? " (in the water)"
                        : string.Empty;
            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "DogLostTarget",
                    $"Dog={dog.Id} lost NPC{target.Id.Value}{lostReason}");
            }
            dog.TargetNpc = null;
            dog.Status = Wildlife.MobStatus.Roaming;
            target = null;
        }

        if (target is null)
        {
            Roam(world, dog);
            return;
        }

        // In range? Same or adjacent junction = melee. §106: and the medium
        // must match — a wolf on the shore junction is ADJACENT to the swimmer
        // one junction out, but its teeth stop at the waterline.
        var inMelee = target.CurrentJunction is { } npcJunction &&
            (npcJunction.Equals(dog.Junction) ||
             (world.Junctions.Items.TryGetValue(dog.Junction, out var dogJunction) &&
              dogJunction.Neighbors.Contains(npcJunction))) &&
            (!Spec106.WaterSanctuaryEnabled ||
             CombatMedium.CanEngage(world, Stats(dog).AttackMediums, target));

        if (!inMelee)
        {
            dog.Status = Wildlife.MobStatus.Chasing;
            // Spec 29C.4A: out of melee = she has (for now) broken contact, so
            // the flee-stall clock resets — the cornered-fight valve only fires
            // on CONTINUOUS melee pinning, never on a chase she is outrunning.
            // Bug #20: this clock belongs to the NPC-versus-PACK encounter,
            // not to each dog. With two wolves, the one still chasing used to
            // reset the clock set by the wolf already pinning the victim every
            // pass. The victim then stayed Flee forever, neither escaping nor
            // reaching the cornered-fight valve; weapon/IsFighting flickered
            // with mob iteration order. Break contact only when the whole pack
            // is out of junction melee.
            var packHasMeleeContact = CountAdjacentDogs(world, target) > 0;
            if (!packHasMeleeContact)
            {
                target.Mind.FleeContactSinceTick = 0;
            }
            var junctionBefore = dog.Junction;
            ChaseStep(world, dog, target);

            // Spec 29C.3 (stuck-chase give-up): the mob-side mirror of the
            // girls' standoff-release valve. A chase that cannot move the dog
            // at all (no walkable route — quarry behind the hut, approach ring
            // sealed) is hopeless; after DogChaseStallGiveUpTicks of standing
            // still it drops the target, takes a hunt cooldown so it actually
            // wanders away, and stops being a statue mid-camp. Any successful
            // step or melee contact resets the clock.
            if (dog.Junction.Equals(junctionBefore))
            {
                if (dog.ChaseStallSinceTick == 0)
                {
                    dog.ChaseStallSinceTick = world.Tick;
                }
                else if (world.Tick - dog.ChaseStallSinceTick >=
                    WildlifeBalance.DogChaseStallGiveUpTicks)
                {
                    dog.ChaseStallSinceTick = 0;
                    dog.NextHuntAllowedTick = world.Tick + WildlifeBalance.DogHuntCooldownTicks;
                    Trace.EmitSystem(world, "DogGaveUp",
                        $"Dog={dog.Id} chase of NPC{target.Id.Value} stalled " +
                        $"{WildlifeBalance.DogChaseStallGiveUpTicks} ticks — wandering off");
                    dog.TargetNpc = null;
                    dog.Status = Wildlife.MobStatus.Roaming;
                    return;
                }
            }
            else
            {
                dog.ChaseStallSinceTick = 0;
            }

            TryCoverFire(world, dog, target);

            // Another dog already owns the live exchange. This chaser may
            // close distance, but must not start a fresh flee/standoff decision
            // afterward and overwrite the pack-level fighting latch.
            if (packHasMeleeContact || CountAdjacentDogs(world, target) > 0)
            {
                return;
            }

            // Behavior audit (Jul 2026): a girl being RUN DOWN at arm's length
            // kept strolling to her errand — IsFighting only latched in actual
            // melee, so between bites (dog cooldown / one junction behind) the
            // auction re-planned Drink/GatherTools and she walked, the dog
            // caught up, bit, and the cycle repeated until she bled out.
            // Within 1 tile of a charging dog she now squares up (same stance
            // AnimalCombatSystem enforces in melee); an active flee keeps
            // running, prone/unconscious bodies stay down. Radius 1, not 2 —
            // a dog stuck two tiles away behind a ledge it can't path over
            // kept the stance latched forever and the frozen girl starved
            // mid-"siege" (iter-4 regression, seed 999).
            if (!target.IsFighting && target.Health > 0f &&
                target.Mind.CurrentGoal != GoalType.Flee &&
                !target.Body.IsProne && !target.IsUnconscious(world.Tick) &&
                HexSpatialMath.HexDistance(dog.Tile, target.Tile) <= 1)
            {
                // Besieged-standoff fix (Fix B, Jul 2026): before squaring her up
                // at arm's length, assess flee — a HURT or outnumbered girl (the
                // same thresholds the melee branch uses) breaks for a refuge
                // instead of latching into a "fight" a stuck dog can never land.
                // That freeze out-starved the girl it besieged: pinned IsFighting,
                // no blow either way, flee never assessed (it lived only in the
                // melee branch), she starved and bled out where she stood (seed
                // 308477163, Jana, day 4.79). A healthy girl still squares up (no
                // regression to the run-down protection above); only a girl who
                // would ALSO flee in melee now flees one tile-step sooner.
                var attackers = CountAdjacentDogs(world, target);
                // Spec 29C.4A: a girl who just committed to the fight squares up
                // instead of bolting the instant the dog gives her a step — the
                // commitment window must not be undone by the square-up branch.
                var standoffCommitted = world.Tick < target.Mind.FightCommitUntilTick;
                var fledStandoff = !standoffCommitted &&
                    (target.Health < 0.6f || WorstPartHealth(target) < 0.35f ||
                    attackers >= 2) && TryStartFlee(world, target, attackers, dog.Id);

                if (!fledStandoff)
                {
                    // Spec 29C.4A standoff-release valve: Fix B above only frees
                    // a HURT girl; a healthy one still latched "fighting" against
                    // a dog that can never close the junction gap and stood
                    // frozen until the player quit (seed 521091321 day 30 —
                    // wolf beside the camp, badge «дерётся», no bite ever
                    // landing). After StandoffReleaseTicks of continuous
                    // blow-less square-up she stops honouring the latch for a
                    // grace window — walks, drinks, re-plans. Melee contact
                    // resets the window (see the fight branch), so a genuine
                    // run-down — where the dog DOES reach melee between her
                    // steps — can never trip the valve.
                    var window = TrackSquareUpWindow(world, target);
                    if (world.Tick < target.Mind.StandoffReleaseUntilTick)
                    {
                        // Released — the latch stays off while she clears out.
                    }
                    else if (!standoffCommitted &&
                        window >= SimBalance.StandoffReleaseTicks)
                    {
                        target.Mind.StandoffReleaseUntilTick =
                            world.Tick + SimBalance.StandoffReleaseGraceTicks;
                        target.Mind.SquareUpSinceTick = 0;
                        RememberDangerAt(world, target, dog.Tile);
                        Trace.Emit(world, target.Id, "StandoffReleased",
                            $"Dog={dog.Id} never reached melee in {window} ticks — " +
                            $"dropping the stance (grace {SimBalance.StandoffReleaseGraceTicks})");
                    }
                    // §121: с приказом она не оборачивается и на разгон зверя —
                    // идёт дальше (правило Кенши). Без приказа — как все.
                    else if (!ManualControlMath.IsOrderedManual(target))
                    {
                        target.IsFighting = true;
                        if (target.Plan.Status == PlanStatus.Active ||
                            target.Execution.Status == ExecutionStatus.InProgress ||
                            target.IsCarryingPerson)
                        {
                            PlanInterruption.AbortForCombat(
                                world, target, $"Charged by dog {dog.Id}");
                            target.Mind.CurrentGoal = GoalType.None;
                        }
                    }
                }
            }

            return;
        }

        // Fight: hold the pair in the exchange. Spec 29C.4A: the NPC assesses —
        // outnumbered or badly hurt means run, otherwise stand and strike back.
        // The actual blows (windup → hit → cooldown) are timed sub-second and
        // land in AnimalCombatSystem on the Fast layer; this medium pass only
        // manages statuses, flee assessment and helpers.
        dog.Status = Wildlife.MobStatus.Fighting;
        // Spec 29C.4A: real contact — the standoff valve re-arms; from here the
        // melee machinery (bites, strikes, flee assessment, commit) owns the
        // fight and the release grace must not linger into a real exchange.
        target.Mind.SquareUpSinceTick = 0;
        target.Mind.StandoffReleaseUntilTick = 0;
        // Spec 29C.3: melee = the chase worked; the stall clock re-arms.
        dog.ChaseStallSinceTick = 0;
        RememberDanger(world, target);
        CombatHelpSystem.RallyFriends(world, target, dog.Id, null, $"Dog={dog.Id}");
        RunDogDefenders(world, dog, target);
        if (dog.Health <= 0f)
        {
            return;
        }

        // Spec §60: an unconscious body neither flees nor fights — it lies
        // where it fell and takes the bites. Collapsing near dogs is lethal.
        var helpless = target.IsUnconscious(world.Tick);

        // Spec 29C.4A cornered-fight valve. A flee only saves her if it BREAKS
        // melee contact — she is in melee THIS tick, so if she is still fleeing,
        // time the stall. Once the dog has stayed on her FleeStallTicks past the
        // first pinned tick the escape has plainly failed, so she abandons the
        // doomed run and COMMITS to the fight rather than be bitten for free
        // until a limb tears off and she goes prone (seed 351193917: Молди fled
        // a dog pinned at Dist=0 for 130+ ticks, never once striking back, LegR
        // severed at t1095 → prone → dead). Prone/unconscious bodies can't stand,
        // so they are exempt (§50 is HARD — a crawl-away is all they have).
        var committedToFight = world.Tick < target.Mind.FightCommitUntilTick;
        if (target.Mind.CurrentGoal == GoalType.Flee && !helpless &&
            !target.Body.IsProne && !committedToFight)
        {
            if (target.Mind.FleeContactSinceTick == 0)
            {
                target.Mind.FleeContactSinceTick = world.Tick;
            }
            else if (world.Tick - target.Mind.FleeContactSinceTick >= SimBalance.FleeStallTicks)
            {
                committedToFight = true;
                target.Mind.FleeContactSinceTick = 0;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, target.Id, "FleeStalled",
                        $"Cornered by dog {dog.Id} (Health={target.Health:F2}) — standing to fight");
                }
            }
        }

        // Hold the commitment as long as the dog stays engaged, so the flee
        // assessment below can't ping-pong her back out of the stand mid-fight.
        // Re-armed each melee tick; it only lapses once the mob is dead or has
        // broken contact, after which a fresh flee is allowed again.
        if (committedToFight)
        {
            target.Mind.FightCommitUntilTick = world.Tick + SimBalance.FightCommitGraceTicks;
        }

        // A committed fighter never re-opens the flee assessment (that re-flee
        // was the endless-maul loop); a genuinely new flee only starts for a
        // girl not currently standing her ground.
        // §121: ручная не убегает от зверя сама — как и от человека (§109.8).
        // Отступление есть у игрока: приказ идти, который она — по правилу
        // Кенши — не бросит даже под укусами.
        var manualTarget = ManualControlMath.IsManual(target);
        var fleeing = target.Mind.CurrentGoal == GoalType.Flee && !committedToFight;
        if (!fleeing && !helpless && !committedToFight && !manualTarget)
        {
            var attackers = CountAdjacentDogs(world, target);
            // §50-prone: a girl on the ground CANNOT stand and trade blows —
            // lying is always a crawl-away, never a stand.
            if (target.Body.IsProne ||
                target.Health < 0.6f || WorstPartHealth(target) < 0.35f || attackers >= 2)
            {
                fleeing = TryStartFlee(world, target, attackers, dog.Id);
            }
        }

        // §50-prone: no refuge to crawl to still never means standing up —
        // a prone girl lies where she is (and takes the bites; §50 is HARD).
        //
        // ⭐ §121 ПРАВИЛО КЕНШИ, вторая половина: с приказом она НЕ встаёт в
        // стойку и против зверя — идёт и терпит укусы. Без приказа стоит и
        // дерётся, как все (ветка ниже отрабатывает как прежде).
        if (!fleeing && !helpless && !target.Body.IsProne &&
            !(manualTarget && ManualControlMath.HasActiveOrder(target)))
        {
            var wasFighting = target.IsFighting;
            target.IsFighting = true;
            if (target.Plan.Status == PlanStatus.Active ||
                target.Execution.Status == ExecutionStatus.InProgress ||
                target.IsCarryingPerson)
            {
                PlanInterruption.AbortForCombat(world, target, $"Attacked by dog {dog.Id}");
                target.Mind.CurrentGoal = GoalType.None;
            }

            // Spec §52: at the first strike, drop the load to ready the weapon —
            // "quick, throw down the firewood and grab the spear." Both hands are
            // needed for the spear, so bulky resources in hand hit the ground
            // (recoverable after the fight). Only when actually spear-armed and
            // two-handed; a bare-handed girl keeps whatever she carries.
            if (!wasFighting && target.Body.CanUseToolsOrWeapons &&
                target.Body.IntactHands >= 2 &&
                Content.GearCatalog.For(SimBalance.BestMeleeWeapon(
                    target.Inventory.Items, target.Body.WeaponHands)).TwoHanded)
            {
                ReadySpearHands(world, target);
            }
        }
    }

    // Spec 19.3C: dogs bite low — legs most, head rarely.
    internal static BodyPart PickAttackPart(WorldState world, int dogId, NPCState target)
    {
        BodyDamageResolver.DecayAllHitBias(world, target);
        var roll = MathUtil.Hash01(world.Seed, world.Tick, dogId, 555);
        return MeleeSwing.PickWeighted(target, roll,
            (BodyPart.LegL, 0.30f),
            (BodyPart.LegR, 0.30f),
            (BodyPart.ArmL, 0.125f),
            (BodyPart.ArmR, 0.125f),
            (BodyPart.Torso, 0.10f),
            (BodyPart.Pelvis, 0.03f),
            (BodyPart.Head, 0.02f));
    }

    // Spec 29C.4A: stamp the square-up window and return how long it has run.
    // The branch executes on the Medium layer, so a silence of more than three
    // passes (12 ticks at the default 4-tick interval) means contact was
    // broken in between and the window restarts from this tick.
    private const int SquareUpStaleGapTicks = 12;

    private static int TrackSquareUpWindow(WorldState world, NPCState target)
    {
        var mind = target.Mind;
        if (mind.SquareUpSinceTick == 0 ||
            world.Tick - mind.SquareUpLastTick > SquareUpStaleGapTicks)
        {
            mind.SquareUpSinceTick = world.Tick;
        }

        mind.SquareUpLastTick = world.Tick;
        return world.Tick - mind.SquareUpSinceTick;
    }

    internal static float WorstPartHealth(NPCState npc)
    {
        var worst = 1f;
        foreach (var value in npc.Body.Parts.Values)
        {
            worst = System.Math.Min(worst, value);
        }

        return worst;
    }

    private static bool IsIndoorTile(WorldState world, TileCoord tile)
    {
        return world.Tiles.Items.TryGetValue(tile, out var t) &&
            t.Flags.HasFlag(TileFlags.Indoor);
    }

    private static bool IsIndoorJunction(WorldState world, JunctionId junctionId)
    {
        return world.Junctions.Items.TryGetValue(junctionId, out var junction) &&
            junction.Tiles.Count > 0 && IsIndoorTile(world, junction.Tiles[0]);
    }

    internal static bool IsNpcInSanctuary(WorldState world, NPCState npc) =>
        IsIndoorTile(world, npc.Tile) ||
        (npc.CurrentJunction is { } junction && IsIndoorJunction(world, junction));

    // §106: для зверя вода — вторая форма санктуария (§29C.4A): по способности
    // моба (AttackMediums) туда не дотянуться зубами, и топтаться статуей у
    // кромки до stall-таймера — не выжидание, а баг. НЕ слито в IsNpcInSanctuary:
    // «indoor» читают и Raid/Abuse/Threat со СВОИМИ ручками, у воды — своя.
    // §105.14: притворяющаяся мёртвой — третья форма того же убежища. Зверь не
    // наводится на неподвижное тело и БРОСАЕТ уже ведущуюся погоню: обе половины
    // достаются одной строкой, потому что и захват цели (:193), и ветка
    // DogLostTarget спрашивают этот предикат.
    private static bool IsNpcInRefugeFrom(WorldState world, Wildlife.MobState mob, NPCState npc) =>
        IsNpcInSanctuary(world, npc) ||
        npc.IsPlayingDead(world.Tick) ||
        (Spec106.WaterSanctuaryEnabled &&
         !CombatMedium.CanEngage(world, Stats(mob).AttackMediums, npc));

    private static int CountAdjacentDogs(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } npcJunction)
        {
            return 0;
        }

        var count = 0;
        foreach (var dog in world.Mobs)
        {
            if (dog.Health <= 0f)
            {
                continue;
            }

            if (dog.Junction.Equals(npcJunction) ||
                (world.Junctions.Items.TryGetValue(dog.Junction, out var dogJunction) &&
                 dogJunction.Neighbors.Contains(npcJunction)))
            {
                count++;
            }
        }

        return count;
    }

    private static void RunDogDefenders(WorldState world, Wildlife.MobState dog, NPCState quarry)
    {
        foreach (var defender in world.Entities.Npcs.Values)
        {
            if (defender.Id == quarry.Id ||
                defender.Health <= 0f ||
                defender.Mind.CurrentGoal != GoalType.Defend ||
                defender.Mind.CombatAssistDogId != dog.Id ||
                defender.CurrentJunction is not { } defenderJunction)
            {
                continue;
            }

            var adjacent = defenderJunction.Equals(dog.Junction) ||
                (world.Junctions.Items.TryGetValue(dog.Junction, out var dogJunction) &&
                 dogJunction.Neighbors.Contains(defenderJunction));
            if (!adjacent ||
                // §106: подмога не дерётся из воды — пловчиха не бьёт.
                (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, defender)))
            {
                continue;
            }

            var wasFighting = defender.IsFighting;
            defender.IsFighting = true;
            if (!wasFighting && defender.Body.CanUseToolsOrWeapons &&
                defender.Body.IntactHands >= 2 &&
                Content.GearCatalog.For(SimBalance.BestMeleeWeapon(
                    defender.Inventory.Items, defender.Body.WeaponHands)).TwoHanded)
            {
                ReadySpearHands(world, defender);
            }

            var weaponId = defender.Body.CanUseToolsOrWeapons
                ? SimBalance.BestMeleeWeapon(defender.Inventory.Items, defender.Body.WeaponHands)
                : string.Empty;
            var weaponMult = SimBalance.MeleeStrikeBonus(weaponId);
            var attackSpeed = SimBalance.MeleeAttackSpeed(weaponId);
            // §104 r8: когда удары всех идут по таймлайну, эти наносит
            // AnimalCombatSystem.RunAssistStrikes — на быстром слое, с окном
            // анимации и штампом замаха. Здесь остаётся только подход, стойка
            // и трасса: два источника урона по одной собаке били бы вдвое.
            var strikeReady = !SimBalance.TimedMeleeEverywhere &&
                SimBalance.MeleeStrikeReady(world.Tick, defender.Id.Value, weaponId);
            var strike = strikeReady
                ? NpcStrikePerPass * defender.StrikeFactor() * weaponMult
                : 0f;
            if (strike > 0f)
            {
                dog.Health -= strike;
            }

            Trace.Emit(world, defender.Id, "HelpCryDefended",
                $"Victim=NPC{quarry.Id.Value} Dog={dog.Id} Strike={strike:F3} " +
                $"Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)}" +
                $"{(strikeReady ? string.Empty : " recovering")} Speed={attackSpeed:F1} " +
                $"DogHealth={System.Math.Max(0f, dog.Health):F2}");
            SocialCueSignals.Stamp(world, defender, "HelpCryDefended:dog", quarry.Id);

            if (dog.Health <= 0f)
            {
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    if (npc.Mind.CombatAssistDogId == dog.Id)
                    {
                        CombatHelpSystem.ClearAssist(npc);
                    }
                }
                return;
            }
        }
    }

    // Spec §52 / §54: drop the bulky load to free both hands for the spear.
    // Resources (logs/sticks/stone/leaves) hit the ground at the NPC's feet —
    // recoverable after the fight; the spear/bottle/tools/food stay.
    private static readonly string[] _bulkyHandItems =
    {
        ContentIds.Log, ContentIds.Stick, ContentIds.Stone, ContentIds.PalmLeaf
    };

    internal static void ReadySpearHands(WorldState world, NPCState npc)
    {
        var dropped = 0;
        foreach (var mat in _bulkyHandItems)
        {
            while (npc.Inventory.Items.Find(i => i.DefinitionId == mat) is { } item)
            {
                npc.Inventory.Items.Remove(item);
                ExecutionSystem.DropItemAtFeet(world, npc, item);
                dropped++;
            }
        }

        if (dropped > 0)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "SpearReadied",
                    $"Dropped {dropped} to grab the spear");
            }
        }
    }

    // §54.16: is a LIVE beast standing within `radius` of this tile? The
    // danger MEMORY outlives the beast by up to a day; this is the present
    // tense of it, so a plan can tell "a wolf is there" from "a wolf was
    // there yesterday".
    internal static bool MobNear(WorldState world, TileCoord tile, int radius)
    {
        foreach (var mob in world.Mobs)
        {
            if (HexSpatialMath.HexDistance(tile, mob.Tile) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    // §54.16: the beast is dead — clear the fear it stamped around this spot
    // for every colonist, so the kill site stops being a no-go zone (the meat
    // and hide lying there are the whole point of the fight). Radius 1 covers
    // both stamps a fight leaves: the mob's tile (§62 far-spotting) and the
    // girl's own feet (29C.4A).
    internal static void ForgetDangerAround(WorldState world, TileCoord tile, int radius)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Memory.Dangers.RemoveAll(
                d => HexSpatialMath.HexDistance(d.Tile, tile) <= radius);
        }
    }

    // Spec 29C.4A: record the attack site (deduped by tile, capped).
    // §56: also used by PredationSystem so a preyed-on victim flags the danger.
    internal static void RememberDanger(WorldState world, NPCState npc)
    {
        RememberDangerAt(world, npc, npc.Tile);
    }

    // Spec §62: far-spotting files the MOB's tile (not the girl's own feet),
    // so producer bias steers her away from where the wolf actually prowls.
    internal static void RememberDangerAt(WorldState world, NPCState npc, TileCoord tile)
    {
        foreach (var danger in npc.Memory.Dangers)
        {
            if (danger.Tile == tile)
            {
                danger.Tick = world.Tick;
                return;
            }
        }

        npc.Memory.Dangers.Add(new Memory.DangerMemory { Tile = tile, Tick = world.Tick });
        if (npc.Memory.Dangers.Count > 8)
        {
            npc.Memory.Dangers.RemoveAt(0);
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "DangerRemembered",
                $"Tile={tile.Q},{tile.R} (dogs)");
        }
    }

    // Spec 29C.4A: run for the nearest reachable indoor junction.
    // §56: also used by PredationSystem so a preyed-on victim can bolt.
    // §81.13: бегство ДОМОЙ, а не в ближайшее укрытие. TryStartFlee ниже ищет
    // ближайший indoor-узел — для чужака, стоящего во дворе колонии, это ЕЁ
    // хижина (та самая грабля, что записана в RaidSystem.BreakOff). Разбитый
    // после сцены бежит к якорю СВОЕЙ фракции: жертва — в лагерь к подругам,
    // чужак — к себе. Цель та же Flee: скорость ×2.25, аукцион закрыт до
    // прибытия, кольца угроз игнорируются.
    internal static bool TryFleeToCamp(WorldState world, NPCState npc, string reason)
    {
        if (npc.CurrentJunction is not { } startJunction ||
            !world.FactionHomes.TryGetValue(npc.Faction, out var camp) ||
            IsFleeOnCooldown(world, npc))
        {
            return false;
        }

        var currentToCamp = HexSpatialMath.HexDistance(npc.Tile, camp);
        var candidates = new System.Collections.Generic.List<Junction>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id))
            {
                continue;
            }

            var toCamp = HexSpatialMath.HexDistance(junction.Tiles[0], camp);
            if (toCamp < currentToCamp)
            {
                candidates.Add(junction);
            }
        }

        // Сначала как можно ближе к якорю, при равенстве — ближе к себе.
        // Перебираем, а не берём один геометрически лучший: ближайшая точка
        // может требовать прыжка, которого раненая/несущая человека не умеет.
        candidates.Sort((a, b) =>
        {
            var byCamp = HexSpatialMath.HexDistance(a.Tiles[0], camp).CompareTo(
                HexSpatialMath.HexDistance(b.Tiles[0], camp));
            if (byCamp != 0)
            {
                return byCamp;
            }

            var byDistance = HexSpatialMath.Distance(npc.Position, a.WorldPosition).CompareTo(
                HexSpatialMath.Distance(npc.Position, b.WorldPosition));
            return byDistance != 0 ? byDistance : a.Id.Value.CompareTo(b.Id.Value);
        });

        if (!TryReserveReachableFleeTarget(world, npc, startJunction, candidates,
                out var refuge))
        {
            if (candidates.Count > 0)
            {
                MarkFleeUnavailable(world, npc, "No physically reachable route toward camp");
            }

            return false;
        }

        // Баг #13: «дом» должен быть ДАЛЬШЕ, чем стоишь. Свой джанкшен занят
        // самим собой, так что стоящему В лагере поиск отдавал соседний
        // свободный узел — «бегство» удавалось каждый средний тик, клапан
        // §109.8 съедал его ход, и разбитого били в его же дворе, а он шаркал
        // на месте и не отвечал ни разу. Некуда бежать — не бегство: вернуть
        // false и пусть вызывающий дерётся (форма §108 TryFleeHome, где
        // refuge.Equals(from) стоял с самого начала).
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, npc, reason);
            // Abort releases the old target. If it happened to equal the newly
            // chosen refuge, reacquire our short anti-race reservation.
            SpatialMutations.TryReserveJunction(world, refuge, npc.Id, world.Tick, 48);
        }

        npc.IsFighting = false;
        // §109.9: бегство РАСЦЕПЛЯЕТ бой. Живая пара — это замахи от
        // HumanCombatSystem: бегущая с парой скользила по земле в атакующей
        // позе, продолжая бить воздух. §108 в своём TryFleeHome это уже знал.
        npc.Mind.CombatOpponentNpcId = null;
        FightScene.ReleaseSwingSlot(npc);
        npc.Mind.FleeContactSinceTick = 0;
        npc.Mind.CurrentGoal = GoalType.Flee;
        npc.Plan.Goal = GoalType.Flee;
        npc.Plan.TargetJunctionId = refuge;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = refuge
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "FleeStarted",
                $"To camp Junction={refuge.Value} ({reason} Health={npc.Health:F2})");
        }
        return true;
    }

    internal static bool TryStartFlee(
        WorldState world,
        NPCState npc,
        int attackers,
        int? dogId = null,
        EntityId? attackerNpcId = null)
    {
        if (npc.CurrentJunction is not { } startJunction || IsFleeOnCooldown(world, npc))
        {
            return false;
        }

        var candidates = new System.Collections.Generic.List<Junction>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            // Jul 2026: the refuge must be FREE — three girls fleeing the same
            // raid all targeted the same interior junction; the second one's
            // route came up Blocked, the flee aborted, and she stood re-fleeing
            // (681→668→667→…) while the dog chewed her down (seed 12345 d0.8).
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !IsIndoorTile(world, junction.Tiles[0]) ||
                !SpatialQueries.IsJunctionFree(world, junction.Id))
            {
                continue;
            }

            candidates.Add(junction);
        }

        candidates.Sort((a, b) =>
        {
            var byDistance = HexSpatialMath.Distance(npc.Position, a.WorldPosition).CompareTo(
                HexSpatialMath.Distance(npc.Position, b.WorldPosition));
            return byDistance != 0 ? byDistance : a.Id.Value.CompareTo(b.Id.Value);
        });

        if (!TryReserveReachableFleeTarget(world, npc, startJunction, candidates,
                out var refuge))
        {
            if (candidates.Count > 0)
            {
                MarkFleeUnavailable(world, npc, "No physically reachable indoor refuge");
            }

            return false; // nowhere to run — keep fighting
        }

        PlanInterruption.Abort(world, npc, $"Fleeing from dogs (attackers={attackers})");
        SpatialMutations.TryReserveJunction(world, refuge, npc.Id, world.Tick, 48);
        npc.IsFighting = false;
        // §109.9: бегство расцепляет бой — см. TryFleeToCamp выше. Здесь та же
        // дыра давала «скользит от налётчика, не переставая махать ножом».
        npc.Mind.CombatOpponentNpcId = null;
        FightScene.ReleaseSwingSlot(npc);
        // Spec 29C.4A: a fresh flee earns a fresh stall grace — clear any pin
        // clock from a prior engagement so the new run is judged on its own.
        npc.Mind.FleeContactSinceTick = 0;
        npc.Mind.CurrentGoal = GoalType.Flee;
        npc.Plan.Goal = GoalType.Flee;
        npc.Plan.TargetJunctionId = refuge;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = refuge
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "FleeStarted",
                $"To indoor Junction={refuge.Value} (Health={npc.Health:F2} Attackers={attackers})");
        }
        if (dogId.HasValue)
        {
            CombatHelpSystem.CallForHelpFromDog(world, npc, dogId.Value, attackers);
        }
        else if (attackerNpcId.HasValue)
        {
            CombatHelpSystem.CallForHelpFromNpc(world, npc, attackerNpcId.Value, attackers);
        }
        return true;
    }

    private static bool IsFleeOnCooldown(WorldState world, NPCState npc) =>
        npc.Mind.Cooldowns.Exists(c =>
            c.Goal == GoalType.Flee && c.EndTick > world.Tick);

    // Бюджет полных поисков пути на один выбор убежища. Кандидаты
    // отсортированы «лучший первым», штатный выбор укладывается в первые
    // попытки; исчерпывающий перебор случается ровно тогда, когда дороги нет
    // вообще (замерено: 800 мс и 0.8 ГБ аллокаций за тик на острове 1x —
    // та же болезнь, что бюджет TryFindDestination у переноски §118).
    // После бюджета — тот же честный MarkFleeUnavailable, но за миллисекунды.
    private const int FleePathSearchBudget = 24;

    private static bool TryReserveReachableFleeTarget(
        WorldState world,
        NPCState npc,
        JunctionId start,
        System.Collections.Generic.List<Junction> candidates,
        out JunctionId refuge)
    {
        var avoid = PathfindingSystem.OtherActorJunctions(world, npc);
        var searches = 0;
        foreach (var candidate in candidates)
        {
            if (++searches > FleePathSearchBudget)
            {
                break;
            }

            // This is the exact physical contract PathfindingSystem will use
            // on the following fast tick. Connectivity alone is insufficient:
            // it ignores elevation jumps.
            var path = HexPathfinder.FindPath(
                world, start, candidate.Id, avoid,
                weightClimb: false,
                canJump: npc.Body.CanJump);
            if (path.Count == 0 ||
                !SpatialMutations.TryReserveJunction(
                    world, candidate.Id, npc.Id, world.Tick, 48))
            {
                continue;
            }

            refuge = candidate.Id;
            return true;
        }

        refuge = default;
        return false;
    }

    private static void MarkFleeUnavailable(WorldState world, NPCState npc, string reason)
    {
        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Flee);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "FleeUnavailable", reason);

        }
    }

    // §135: поход за ЛЕЖАЩЕЙ конечностью. Возвращает true, если этот проход
    // зверя занят падалью, — тогда ни охоты, ни блужданий.
    //
    // ⭐ ЦЕНА. В обычной игре это одно сравнение счётчика с нулём: конечностей
    // на острове нет почти всегда (индекс §135 в RuntimeCaches). Полный поиск
    // пути запускается ТОЛЬКО когда падаль есть и она в радиусе чутья, а
    // недостижимая — гасится кулдауном `ScentGiveUpTicks`, иначе зверь строил
    // бы Дейкстру по 14 000 узлов каждый средний тик до самого гниения ноги.
    private bool RunScentForLimb(WorldState world, Wildlife.MobState dog)
    {
        if (world.Tick < dog.NextScentScanTick || MobLimbPrize.IsSated(world, dog))
        {
            return false;
        }

        // Уже выбранная падаль главнее новой: без этого зверь на каждом проходе
        // переприцеливался бы на «ближайшую сейчас» и ходил между двумя ногами.
        Content.WorldObjectState prize = null;
        if (dog.PrizeObjectId != 0 &&
            world.Entities.Objects.TryGetValue(new ObjectId(dog.PrizeObjectId), out var held) &&
            MobLimbPrize.IsSeveredLimb(held))
        {
            prize = held;
        }

        if (prize is null && !MobLimbPrize.TryFindNearby(world, dog, out prize))
        {
            dog.PrizeObjectId = 0;
            return false;
        }

        dog.PrizeObjectId = prize.Id.Value;
        dog.Status = Wildlife.MobStatus.Roaming;

        var limbJunction = prize.Junctions.Count > 0 ? prize.Junctions[0] : dog.Junction;
        var arrived = limbJunction.Equals(dog.Junction) ||
            (world.Junctions.Items.TryGetValue(dog.Junction, out var at) &&
             at.Neighbors.Contains(limbJunction));
        if (arrived)
        {
            MobLimbPrize.TakeFromGround(world, dog, prize);
            return true;
        }

        if (!StepTowardJunction(world, dog, limbJunction))
        {
            // Дороги нет (нога упала в хижине, за стеной, на скале). Не
            // пересчитывать её каждый проход — вот ради чего кулдаун.
            dog.PrizeObjectId = 0;
            dog.NextScentScanTick = world.Tick + Spec135.ScentGiveUpTicks;
            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "MobScentUnreachable",
                    $"Dog={dog.Id} cannot reach limb Obj={prize.Id.Value}");
            }
            return false;
        }

        return true;
    }

    // §135: программа зверя с добычей в зубах. Три состояния подряд, и все три
    // читаются с экрана: отходит → стоит и ест → уходит сытым.
    private void RunLimbCarry(WorldState world, Wildlife.MobState dog)
    {
        // Ни цели, ни статуса драки — вид рисует спокойного зверя с ношей.
        dog.TargetNpc = null;
        dog.Status = Wildlife.MobStatus.Roaming;

        if (dog.LimbEatenAtTick == 0)
        {
            if (MobLimbPrize.IsClearOfColony(world, dog))
            {
                MobLimbPrize.Settle(world, dog, "out of aggro range");
                return;
            }

            // Предохранитель: на тесном острове уйти бывает НЕКУДА (лагерь
            // поперёк единственного прохода, кольцо занято). Без него нога
            // висела бы в зубах вечно, потому что условие «отошёл» никогда не
            // выполняется, — та же болезнь, что stuck-chase у погони.
            if (world.Tick - dog.LimbTakenAtTick >= Spec135.RetreatGiveUpTicks)
            {
                MobLimbPrize.Settle(world, dog, "nowhere left to go");
                return;
            }

            RetreatStep(world, dog);
            return;
        }

        // Ест: стоит на месте (Roam не вызывается намеренно — «стоит с ногой в
        // зубах» это и есть сцена), пока срок не вышел.
        if (world.Tick >= dog.LimbEatenAtTick)
        {
            MobLimbPrize.Finish(world, dog);
        }
    }

    // §135: шаг ПРОЧЬ от ближайшей колонистки. Жадный подъём по расстоянию, а
    // не путь к точке: «подальше» — это направление, а не адрес, и на острове
    // из 285 тайлов любая выбранная точка через десяток тиков оказывалась бы не
    // там, где стоит колония. Тупик локального максимума разбирает
    // предохранитель RetreatGiveUpTicks выше.
    private static void RetreatStep(WorldState world, Wildlife.MobState dog)
    {
        var steps = Stats(dog).ChaseStepsPerTick;
        for (var step = 0; step < steps; step++)
        {
            if (!world.Junctions.Items.TryGetValue(dog.Junction, out var junction))
            {
                return;
            }

            var currentScore = RetreatScore(world, dog.Tile, junction.WorldPosition);
            Junction? best = null;
            var bestScore = currentScore;
            foreach (var neighborId in junction.Neighbors)
            {
                if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor) ||
                    neighbor.Blocked || neighbor.Door ||
                    IsIndoorJunction(world, neighborId) ||
                    SpatialQueries.IsAllWaterJunction(world, neighborId) ||
                    IsJunctionOccupiedByActor(world, neighborId, dog) ||
                    neighbor.Tiles.Count == 0)
                {
                    continue;
                }

                var score = RetreatScore(world, neighbor.Tiles[0], neighbor.WorldPosition);
                // Строгое улучшение + разрыв по номеру узла: без него зверь
                // ходил бы туда-сюда между двумя равноценными точками.
                if (score > bestScore ||
                    (best is not null && score == bestScore &&
                     neighborId.Value < best.Id.Value))
                {
                    best = neighbor;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                return; // локальный максимум — дальше решает предохранитель
            }

            if (!MoveDogTo(world, dog, best))
            {
                return;
            }
        }
    }

    // Чем дальше от ближайшей живой колонистки, тем лучше. Гексовое расстояние
    // главнее (по нему считается и агр), метрическое — только разрыв ничьих,
    // чтобы шаг внутри одного гекса всё-таки уводил в нужную сторону.
    private static float RetreatScore(WorldState world, TileCoord tile, Float2 position)
    {
        var hexes = int.MaxValue;
        var metric = float.MaxValue;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f)
            {
                continue;
            }

            hexes = System.Math.Min(hexes, HexSpatialMath.HexDistance(tile, npc.Tile));
            metric = System.Math.Min(metric, HexSpatialMath.Distance(position, npc.Position));
        }

        if (hexes == int.MaxValue)
        {
            return 0f; // никого живого — любое место одинаково безопасно
        }

        return hexes * 1000f + metric;
    }

    private static void Roam(WorldState world, Wildlife.MobState dog)
    {
        if (MathUtil.Hash01(world.Seed, world.Tick, dog.Id, 313) > Stats(dog).RoamChance)
        {
            return;
        }

        if (!world.Junctions.Items.TryGetValue(dog.Junction, out var junction) ||
            junction.Neighbors.Count == 0)
        {
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, dog.Id, 719) * junction.Neighbors.Count);
        pick = System.Math.Min(pick, junction.Neighbors.Count - 1);
        var nextId = junction.Neighbors[pick];
        if (!world.Junctions.Items.TryGetValue(nextId, out var next) || next.Blocked || next.Door ||
            IsIndoorJunction(world, nextId) || SpatialQueries.IsAllWaterJunction(world, nextId))
        {
            return;
        }

        MoveDogTo(world, dog, next);
    }

    // Spec 29C.3 (chase-path fix): every junction a ground mob may never step
    // on — indoor (sanctuary), doors, all-water. Fed to FindPath as hardAvoid
    // so the chase plans routes the dog can actually WALK: without it the
    // pathfinder returned the girls' shortest path THROUGH the hut, ChaseStep
    // refused the first indoor step, and the dog stood frozen mid-camp every
    // pass (seed 521091321 day 43, dog 13). Blocked junctions are skipped by
    // FindPath itself. Rebuilt lazily when TopologyVersion moves.
    private static System.Collections.Generic.HashSet<JunctionId> EnsureMobForbidden(WorldState world)
    {
        var cache = world.Caches.MobForbiddenJunctions;
        if (world.Caches.MobForbiddenBuiltVersion == world.TopologyVersion)
        {
            return cache;
        }

        cache.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Door ||
                IsIndoorJunction(world, junction.Id) ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                cache.Add(junction.Id);
            }
        }

        world.Caches.MobForbiddenBuiltVersion = world.TopologyVersion;
        return cache;
    }

    private static void ChaseStep(WorldState world, Wildlife.MobState dog, NPCState target)
    {
        if (target.CurrentJunction is { } targetJunction)
        {
            StepTowardJunction(world, dog, targetJunction);
        }
    }

    // Один шаг маршрута к узлу. Общий для погони за колонисткой и похода за
    // лежащей падалью (§135) — не копия: цена этого места это ПОЛНЫЙ поиск
    // Дейкстры по ~14 000 узлов на каждого идущего зверя за средний тик, и
    // второй экземпляр той же логики означал бы второй набор её граблей.
    // Возвращает false, если дороги нет, — вызывающий решает, что это значит.
    private static bool StepTowardJunction(
        WorldState world, Wildlife.MobState dog, JunctionId targetJunction)
    {
        _mobPathAvoidScratch.Clear();
        AddActorJunctions(world, _mobPathAvoidScratch, dog);

        // §40.17 v2: weightClimb: false — a mob pays NO time for an elevation
        // step (MoveDogTo relocates it logically at once, the view glides to
        // catch up), so the climb weight would price something that never
        // happens: the wolf would loop around ledges the girl simply hops, and
        // she would kite it along every lip. It also unhooks chase routes from
        // future retunes of the seam prices, which is the class of change that
        // historically reshuffled the whole dog dance.
        var path = HexPathfinder.FindPath(world, dog.Junction, targetJunction, _mobPathAvoidScratch,
            weightClimb: false, hardAvoid: EnsureMobForbidden(world));
        if (path.Count < 2)
        {
            return false;
        }

        // A chasing dog sprints several junctions per pass (roam stays one) —
        // this is what puts it above the run-gait speed threshold on screen.
        var steps = System.Math.Min(Stats(dog).ChaseStepsPerTick, path.Count - 1);
        for (var i = 1; i <= steps; i++)
        {
            if (!world.Junctions.Items.TryGetValue(path[i], out var next) || next.Blocked || next.Door ||
                IsIndoorJunction(world, path[i]) || SpatialQueries.IsAllWaterJunction(world, path[i]))
            {
                break;
            }

            if (IsJunctionOccupiedByActor(world, path[i], dog))
            {
                break;
            }

            if (!MoveDogTo(world, dog, next))
            {
                break;
            }
        }

        return true;
    }

    // Spec 35.6: the fear arc gains an answer — an archer housemate (not
    // the one being chased, not in a fight) covers the flight from range.
    private static void TryCoverFire(WorldState world, Wildlife.MobState dog, NPCState quarry)
    {
        foreach (var archer in world.Entities.Npcs.Values)
        {
            if (archer.Id.Value == quarry.Id.Value || archer.IsFighting ||
                // §72: nobody spends the colony's scarce arrows saving the man
                // who hunts them from a wolf.
                !FactionRelations.AreAllies(archer, quarry) ||
                !archer.Inventory.Items.Contains(ContentIds.Bow) ||
                !archer.Inventory.Items.Contains(ContentIds.Arrow) ||
                HexSpatialMath.HexDistance(archer.Tile, dog.Tile) > 3)
            {
                continue;
            }

            archer.Inventory.Items.Remove(ContentIds.Arrow);
            var roll = MathUtil.Hash01(world.Seed, world.Tick, dog.Id * 191 + archer.Id.Value, 907);
            if (roll < 0.5f)
            {
                dog.Health -= 0.35f;
                Trace.Emit(world, archer.Id, "DogShot",
                    $"Dog={dog.Id} hit (Roll={roll:F2}) DogHealth={System.Math.Max(0f, dog.Health):F2}");
            }
            else
            {
                Trace.Emit(world, archer.Id, "DogShot",
                    $"Dog={dog.Id} missed (Roll={roll:F2})");
            }

            return;
        }
    }

    private static readonly System.Collections.Generic.HashSet<JunctionId> _mobPathAvoidScratch = new();

    private static bool MoveDogTo(WorldState world, Wildlife.MobState dog, Junction next)
    {
        if (IsJunctionOccupiedByActor(world, next.Id, dog))
        {
            return false;
        }

        dog.Junction = next.Id;
        // Logical position (Junction/Tile) jumps NOW — combat, aggro and
        // pathing read those. The RENDERED Position is left to glide toward
        // the junction over the coming fast ticks (AnimalMovementSystem), so
        // the dog moves continuously like an NPC instead of teleporting.
        dog.TargetPosition = next.WorldPosition;
        if (next.Tiles.Count > 0)
        {
            dog.Tile = next.Tiles[0];
        }

        return true;
    }

    private static void AddActorJunctions(
        WorldState world,
        System.Collections.Generic.HashSet<JunctionId> occupied,
        Wildlife.MobState self)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f && npc.CurrentJunction is { } npcJunction)
            {
                occupied.Add(npcJunction);
            }
        }

        foreach (var other in world.Mobs)
        {
            if (!ReferenceEquals(other, self) && other.Health > 0f)
            {
                occupied.Add(other.Junction);
            }
        }
    }

    private static bool IsJunctionOccupiedByActor(
        WorldState world,
        JunctionId junctionId,
        Wildlife.MobState self)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f && npc.CurrentJunction is { } npcJunction && npcJunction.Equals(junctionId))
            {
                return true;
            }
        }

        foreach (var other in world.Mobs)
        {
            if (!ReferenceEquals(other, self) && other.Health > 0f && other.Junction.Equals(junctionId))
            {
                return true;
            }
        }

        return false;
    }

    // leavesAtTick: 0 — ЖИТЕЛЬ (амбиентный респавн, живёт до смерти); >0 —
    // ГОСТЬ ночного рейда, который после этого тика уходит (§46 v4).
    private bool TrySpawnDog(WorldState world, int leavesAtTick = 0)
    {
        _spawnCandidates.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                IsIndoorTile(world, junction.Tiles[0]) ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id) ||
                IsJunctionOccupiedByActor(world, junction.Id, self: null))
            {
                continue;
            }

            var tile = junction.Tiles[0];
            var farEnough = true;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (HexSpatialMath.HexDistance(tile, npc.Tile) < SpawnMinDistanceFromNpc)
                {
                    farEnough = false;
                    break;
                }
            }

            // §72.12: и подальше от СТОЯНОК, а не только от тел. Правило выше
            // считает от текущего положения NPC, поэтому стая спокойно заводится
            // у очага, пока хозяин отошёл за дровами, — а он возвращается прямо
            // в неё. Четверо девушек дома закрывают округу собой, одиночке
            // закрывать некому.
            if (farEnough && Spec72.Enabled)
            {
                foreach (var home in world.FactionHomes)
                {
                    if (HexSpatialMath.HexDistance(tile, home.Value) <
                        Spec72.DogSpawnMinDistanceFromCamp)
                    {
                        farEnough = false;
                        break;
                    }
                }
            }

            if (farEnough)
            {
                _spawnCandidates.Add(junction.Id);
            }
        }

        if (_spawnCandidates.Count == 0)
        {
            return false;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, world.NextMobId, 431) * _spawnCandidates.Count);
        pick = System.Math.Min(pick, _spawnCandidates.Count - 1);
        var spawnJunction = world.Junctions.Items[_spawnCandidates[pick]];

        var dog = new Wildlife.MobState
        {
            Id = world.NextMobId++,
            MobId = Content.MobIds.Dog, // the ambient spawner spawns dogs
            Junction = spawnJunction.Id,
            Position = spawnJunction.WorldPosition,
            TargetPosition = spawnJunction.WorldPosition,
            GlideAnchor = spawnJunction.WorldPosition,
            Tile = spawnJunction.Tiles[0],
            Health = Dog.MaxHealth,
            LeavesAtTick = leavesAtTick
        };
        world.Mobs.Add(dog);
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "DogSpawned",
                $"Dog={dog.Id} at Tile={dog.Tile.Q},{dog.Tile.R} Junction={dog.Junction.Value}");
        }
        return true;
    }

    // Spec 29C.2: death cleanup must be total.
    // §56: also invoked by PredationSystem when a stalked victim is killed.
    //
    // §28.15C v3: «total» относится к ПРИТЯЗАНИЯМ, а не к телу. Мёртвая обязана
    // отпустить всё, что держала в мире (брони, занятые джанкшены, объекты под
    // ней), иначе живые вечно упираются в призрака. Но сама она никуда не
    // девается: NPCState целиком переезжает в Entities.Corpses и лежит там до
    // конца игры — вместе с надетым и карманами.
    internal static void RemoveDeadNpc(WorldState world, EntityId deadId)
    {
        if (world.Entities.Npcs.TryGetValue(deadId, out var dying))
        {
            ExecutionSystem.ReleaseClaims(world, dying);
        }

        if (!world.Entities.Npcs.TryGetValue(deadId, out var npc))
        {
            return;
        }

        // #79: capture this before Abort clears Sleep/other execution state.
        // A negative persisted variant means the body was already lying still
        // and must keep that pose instead of replaying a death performance.
        var preserveLyingDeathPose = npc.IsLyingDown(world.Tick);

        PlanInterruption.Abort(world, npc, "Died");

        world.Entities.Npcs.Remove(deadId);

        if (world.Occupancy.EntitiesInTile.TryGetValue(npc.Tile, out var tileEntities))
        {
            tileEntities.Remove(deadId);
        }

        if (world.Caches.EntitiesByTile.TryGetValue(npc.Tile, out var cachedTile))
        {
            cachedTile.Remove(deadId);
        }

        if (world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var cachedFragment))
        {
            cachedFragment.Remove(deadId);
        }

        // Release anything the NPC still owns anywhere in the world.
        var reservationKeys = new System.Collections.Generic.List<JunctionId>();
        foreach (var pair in world.Reservations.Junctions)
        {
            if (pair.Value.Owner == deadId)
            {
                reservationKeys.Add(pair.Key);
            }
        }

        foreach (var key in reservationKeys)
        {
            world.Reservations.Junctions.Remove(key);
        }

        var occupiedKeys = new System.Collections.Generic.List<JunctionId>();
        foreach (var pair in world.Occupancy.JunctionOwner)
        {
            if (pair.Value == deadId)
            {
                occupiedKeys.Add(pair.Key);
            }
        }

        foreach (var key in occupiedKeys)
        {
            world.Occupancy.JunctionOwner[key] = null;
        }

        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.CurrentUser == deadId)
            {
                obj.CurrentUser = null;
                obj.IsOccupied = false;
            }
        }

        // §60.2a r4 / §113 r2: death inherits the already validated lying pose.
        // The corpse object's junction is only an interaction/render anchor, so
        // choose the node nearest that pose instead of teleporting back to centre
        // (which could put the body into a fire or across a cliff).
        JunctionId? dropJunction = null;
        if (world.Tiles.Items.TryGetValue(npc.Tile, out var deathTile) &&
            deathTile.Junctions.Count > 0)
        {
            var bestDist = float.MaxValue;
            foreach (var jId in deathTile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(jId, out var j) || j.Blocked)
                {
                    continue;
                }

                var d = HexSpatialMath.Distance(j.WorldPosition, npc.Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    dropJunction = jId;
                }
            }

            dropJunction ??= deathTile.Junctions[0];
        }

        dropJunction ??= npc.CurrentJunction;

        // §28.15C v3: вещи НЕ падают на землю — они остаются на теле. Раньше
        // смерть вываливала весь гардероб и карманы кучей под ноги, и колония
        // получала обратно всё до нитки бесплатно; теперь за этим надо прийти
        // (§28.15F Loot), а до тех пор она лежит одетая — такой, какой её
        // видели живой.
        //
        // Тело переезжает в отдельный реестр целиком. Его безопасная позиция и
        // курс НЕ пересчитываются: якорь выбран по ним, а не наоборот.
        npc.DeathAnimVariant = preserveLyingDeathPose
            ? -1
            : (int)(MathUtil.Hash01(world.Seed, world.Tick, deadId.Value, 977) * 1024f);
        if (dropJunction is { } restJunction)
        {
            npc.CurrentJunction = restJunction;
        }

        world.Entities.Corpses[deadId] = npc;

        // Spec 28.15C: the body remains; witnesses grieve immediately.
        if (dropJunction is { } corpseJunction)
        {
            var corpse = WorldObjectMutations.SpawnObject(
                world, ContentIds.CorpseNpc, npc.Fragment, npc.Tile, corpseJunction);
            corpse.CurrentUser = deadId; // whose body this is
            corpse.SpawnTick = world.Tick;
            if (npc.IsBeingCarried)
            {
                CorpseMath.SuspendAnchor(world, npc);
            }
            // §28.15C v5: свежий якорь хранит тело две игровые суток, затем
            // CorpseSystem заменяет его останками ещё на двое суток. В руках
            // замена откладывается, но срок всё равно считается от SpawnTick.

            foreach (var witness in world.Entities.Npcs.Values)
            {
                // §125.6: увидела ли она смерть — по своим глазам (был
                // хардкод 6, потом её радиус, теперь сам список восприятия).
                if (!PerceptionMath.Sees(witness, npc.Id))
                {
                    continue;
                }

                // §72: you do not mourn the stranger who was trying to kill
                // you. TriggerGrief floors the social loss regardless of
                // affinity AND marks the spot as a danger memory, so an ungated
                // sweep leaves the colony depressed and afraid of the ground
                // they just won on — a silent difficulty multiplier hiding in
                // the death path. Winning the fight reads as relief instead.
                if (FactionRelations.AreHostile(witness, npc))
                {
                    witness.Needs.Comfort = MathUtil.Clamp(
                        witness.Needs.Comfort + Spec72.EnemyDeathRelief, 0f, 1f);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, witness.Id, "EnemyDeathRelief",
                            $"NPC{deadId.Value} ({npc.DisplayName}) is dead");
                    }
                    continue;
                }

                GriefSystemHelpers.TriggerGrief(world, witness, corpse);
            }
        }

        var deathCause = BuildDeathCause(world, npc);
        world.DeathRecords.Add(new DeathRecord
        {
            EntityId = deadId,
            DisplayName = npc.DisplayName,
            Tick = world.Tick,
            Tile = npc.Tile,
            Cause = deathCause
        });

        Trace.Emit(world, deadId, "NpcDied",
            $"NPC{deadId.Value} ({npc.DisplayName}) died at Tile={npc.Tile.Q},{npc.Tile.R} " +
            $"Cause=[{deathCause}] " +
            $"keeping worn=[{string.Join(",", npc.WornItems)}] " +
            $"inventory=[{string.Join(",", npc.Inventory.Items)}]");
    }

    private static string BuildDeathCause(WorldState world, NPCState npc)
    {
        var recentCause = ReadDeathCause(world, npc);
        var worstPart = FindWorstPart(npc, out var worstHp);
        var vitals = npc.Body.VitalDestroyed(out var vital)
            ? $" Vital={vital}:0"
            : string.Empty;

        return $"{recentCause}; Health={npc.Health:F2} Blood={npc.Needs.Blood:F2} " +
               $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
               $"Thermal={npc.Needs.ThermalComfort:+0.00;-0.00} " +
               $"Worst={worstPart}:{worstHp:F2}{vitals}";
    }

    /// <summary>§30.17: причина берётся ИЗ СОСТОЯНИЯ (штамп ставит Trace.Emit
    /// в момент самого удара), а не поиском по кольцу событий. Кольцо — это
    /// диагностика: его глубина зависит от того, включена ли многословная
    /// трасса, и пока причина выкапывалась оттуда, один и тот же сид давал
    /// разную DeathRecord.Cause в редакторе, в билде и в headless-прогоне —
    /// притом что Cause уходит в сейв и по проводу.
    /// <para>
    /// Окно то же, что задумывалось раньше (240 тиков): смерть наступает в тот
    /// же или соседний тик с ударом, всё, что старше, — уже не про эту смерть.
    /// Прежний поиск по кольцу до этого окна физически не доставал (2048
    /// записей ≈ 11 тиков многословной трассы), так что окно наконец работает.
    /// </para></summary>
    private static string ReadDeathCause(WorldState world, NPCState npc)
    {
        if (npc.Mind.DeathCauseTick != int.MinValue &&
            world.Tick - npc.Mind.DeathCauseTick <= 240 &&
            !string.IsNullOrEmpty(npc.Mind.DeathCauseText))
        {
            return npc.Mind.DeathCauseText;
        }

        return InferDeathCause(npc);
    }

    private static string InferDeathCause(NPCState npc)
    {
        if (npc.Needs.Blood <= 0f)
        {
            return "BledOut: blood reached 0";
        }

        if (npc.Needs.Hunger >= SimBalance.StarveDeathThreshold &&
            npc.Needs.Thirst >= SimBalance.StarveDeathThreshold)
        {
            return "StarvedToDeath: hunger and thirst reached the death threshold";
        }

        if (npc.Needs.Hunger >= SimBalance.StarveDeathThreshold)
        {
            return "StarvedToDeath: hunger reached the death threshold";
        }

        if (npc.Needs.Thirst >= SimBalance.StarveDeathThreshold)
        {
            return "StarvedToDeath: thirst reached the death threshold";
        }

        if (npc.Body.VitalDestroyed(out var vital))
        {
            return $"VitalPartDestroyed: {vital} reached 0 HP";
        }

        return "Health reached 0";
    }

    private static BodyPart FindWorstPart(NPCState npc, out float hp)
    {
        var worst = BodyPart.Torso;
        hp = 1f;
        foreach (var pair in npc.Body.Parts)
        {
            if (pair.Value < hp)
            {
                worst = pair.Key;
                hp = pair.Value;
            }
        }

        return worst;
    }
}

}
