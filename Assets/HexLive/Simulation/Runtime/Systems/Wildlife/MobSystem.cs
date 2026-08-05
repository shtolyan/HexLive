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
    private static int SpawnMinDistanceFromNpc => WildlifeBalance.DogSpawnMinDistanceFromNpc;
    private static float NpcStrikePerPass => SimBalance.NpcStrikePerPass;

    private readonly System.Collections.Generic.List<Wildlife.MobState> _deadDogs = new();
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
                if (TrySpawnDog(world))
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
                Trace.EmitSystem(world, "DogUnstacked",
                    $"Dog={dog.Id} moved to Junction={neighborId.Value}");
                break;
            }
        }
    }

    private void RunDog(WorldState world, Wildlife.MobState dog)
    {
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
            if (world.Tick >= dog.NextHuntAllowedTick)
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
            Trace.EmitSystem(world, "DogLostTarget",
                $"Dog={dog.Id} lost NPC{target.Id.Value}{lostReason}");
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
            target.Mind.FleeContactSinceTick = 0;
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
                    else
                    {
                        target.IsFighting = true;
                        if (target.Plan.Status == PlanStatus.Active ||
                            target.Execution.Status == ExecutionStatus.InProgress)
                        {
                            PlanInterruption.Abort(world, target, $"Charged by dog {dog.Id}");
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
                Trace.Emit(world, target.Id, "FleeStalled",
                    $"Cornered by dog {dog.Id} (Health={target.Health:F2}) — standing to fight");
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
        var fleeing = target.Mind.CurrentGoal == GoalType.Flee && !committedToFight;
        if (!fleeing && !helpless && !committedToFight)
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
        if (!fleeing && !helpless && !target.Body.IsProne)
        {
            var wasFighting = target.IsFighting;
            target.IsFighting = true;
            if (target.Plan.Status == PlanStatus.Active ||
                target.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, target, $"Attacked by dog {dog.Id}");
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
                    target.Inventory.Items, target.Body.IntactHands)).TwoHanded)
            {
                ReadySpearHands(world, target);
            }
        }
    }

    // Spec 19.3C: dogs bite low — legs most, head rarely.
    internal static BodyPart PickAttackPart(WorldState world, int dogId)
    {
        var roll = MathUtil.Hash01(world.Seed, world.Tick, dogId, 555);
        if (roll < 0.30f) return BodyPart.LegL;
        if (roll < 0.60f) return BodyPart.LegR;
        if (roll < 0.725f) return BodyPart.ArmL;
        if (roll < 0.85f) return BodyPart.ArmR;
        if (roll < 0.95f) return BodyPart.Torso;
        if (roll < 0.98f) return BodyPart.Pelvis;
        return BodyPart.Head;
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
                    defender.Inventory.Items, defender.Body.IntactHands)).TwoHanded)
            {
                ReadySpearHands(world, defender);
            }

            var weaponId = defender.Body.CanUseToolsOrWeapons
                ? SimBalance.BestMeleeWeapon(defender.Inventory.Items, defender.Body.IntactHands)
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
            Trace.Emit(world, npc.Id, "SpearReadied",
                $"Dropped {dropped} to grab the spear");
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

        Trace.Emit(world, npc.Id, "DangerRemembered",
            $"Tile={tile.Q},{tile.R} (dogs)");
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
            !world.FactionHomes.TryGetValue(npc.Faction, out var camp))
        {
            return false;
        }

        JunctionId? best = null;
        var bestScore = float.MaxValue;
        var bestToCamp = int.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id))
            {
                continue;
            }

            // Сначала как можно ближе к якорю, при равенстве — ближе к себе.
            var toCamp = HexSpatialMath.HexDistance(junction.Tiles[0], camp);
            var score = toCamp * 1000f +
                HexSpatialMath.Distance(npc.Position, junction.WorldPosition);
            if (score < bestScore)
            {
                bestScore = score;
                bestToCamp = toCamp;
                best = junction.Id;
            }
        }

        // Баг #13: «дом» должен быть ДАЛЬШЕ, чем стоишь. Свой джанкшен занят
        // самим собой, так что стоящему В лагере поиск отдавал соседний
        // свободный узел — «бегство» удавалось каждый средний тик, клапан
        // §109.8 съедал его ход, и разбитого били в его же дворе, а он шаркал
        // на месте и не отвечал ни разу. Некуда бежать — не бегство: вернуть
        // false и пусть вызывающий дерётся (форма §108 TryFleeHome, где
        // refuge.Equals(from) стоял с самого начала).
        if (best is not { } refuge ||
            bestToCamp >= HexSpatialMath.HexDistance(npc.Tile, camp) ||
            !Connectivity.Reachable(world, startJunction, refuge))
        {
            return false;
        }

        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, npc, reason);
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

        Trace.Emit(world, npc.Id, "FleeStarted",
            $"To camp Junction={refuge.Value} ({reason} Health={npc.Health:F2})");
        return true;
    }

    internal static bool TryStartFlee(
        WorldState world,
        NPCState npc,
        int attackers,
        int? dogId = null,
        EntityId? attackerNpcId = null)
    {
        if (npc.CurrentJunction is not { } startJunction)
        {
            return false;
        }

        JunctionId? best = null;
        var bestDistance = float.MaxValue;
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

            var distance = HexSpatialMath.Distance(npc.Position, junction.WorldPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = junction.Id;
            }
        }

        if (best is not { } refuge ||
            !Connectivity.Reachable(world, startJunction, refuge))
        {
            return false; // nowhere to run — keep fighting
        }

        PlanInterruption.Abort(world, npc, $"Fleeing from dogs (attackers={attackers})");
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

        Trace.Emit(world, npc.Id, "FleeStarted",
            $"To indoor Junction={refuge.Value} (Health={npc.Health:F2} Attackers={attackers})");
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
        if (target.CurrentJunction is not { } targetJunction)
        {
            return;
        }

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
            return;
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

    private bool TrySpawnDog(WorldState world)
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
            Health = Dog.MaxHealth
        };
        world.Mobs.Add(dog);
        Trace.EmitSystem(world, "DogSpawned",
            $"Dog={dog.Id} at Tile={dog.Tile.Q},{dog.Tile.R} Junction={dog.Junction.Value}");
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

        // §60.2a lying-body invariant: the corpse rests at the tile CENTRE like
        // every other body on the ground (coma/faint/sleep) — anchor it to the
        // centre-most junction of the death tile, NOT the rim junction the NPC
        // happened to die on (npc.CurrentJunction), which left corpses hanging
        // off the hex edge. The corpse object renders at Junctions[0], so the
        // anchor junction IS its visible position.
        JunctionId? dropJunction = null;
        if (world.Tiles.Items.TryGetValue(npc.Tile, out var deathTile) &&
            deathTile.Junctions.Count > 0)
        {
            var deathCentre = HexSpatialMath.TileToWorld(npc.Tile);
            var bestDist = float.MaxValue;
            foreach (var jId in deathTile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(jId, out var j) || j.Blocked)
                {
                    continue;
                }

                var d = HexSpatialMath.Distance(j.WorldPosition, deathCentre);
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
        // Тело переезжает в отдельный реестр целиком. Позиция подтягивается к
        // якорю: труп лежит в ЦЕНТРЕ гекса (§60.2a), а упасть она могла на
        // ободе — иначе одежда на теле и объект-якорь оказались бы в разных
        // точках, и обирать её пришлось бы не с той клетки, где она лежит.
        npc.DeathAnimVariant = (int)(MathUtil.Hash01(world.Seed, world.Tick, deadId.Value, 977) * 1024f);
        if (dropJunction is { } restJunction &&
            world.Junctions.Items.TryGetValue(restJunction, out var restNode))
        {
            npc.CurrentJunction = restJunction;
            npc.Position = restNode.WorldPosition;
        }

        world.Entities.Corpses[deadId] = npc;

        // Spec 28.15C: the body remains; witnesses grieve immediately.
        if (dropJunction is { } corpseJunction)
        {
            var corpse = WorldObjectMutations.SpawnObject(
                world, ContentIds.CorpseNpc, npc.Fragment, npc.Tile, corpseJunction);
            corpse.CurrentUser = deadId; // whose body this is
            corpse.SpawnTick = world.Tick;
            // §28.15C v3: таймера гниения здесь БОЛЬШЕ НЕТ. Тело не истлевает и
            // не хоронится — оно лежит до конца игры. Убрать его может только
            // нож (§56), и это уже осознанный поступок живого человека.

            foreach (var witness in world.Entities.Npcs.Values)
            {
                if (HexSpatialMath.HexDistance(witness.Tile, npc.Tile) > 6)
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
                    Trace.Emit(world, witness.Id, "EnemyDeathRelief",
                        $"NPC{deadId.Value} ({npc.DisplayName}) is dead");
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
        var recentCause = FindRecentDeathCauseEvent(world, npc);
        var worstPart = FindWorstPart(npc, out var worstHp);
        var vitals = npc.Body.VitalDestroyed(out var vital)
            ? $" Vital={vital}:0"
            : string.Empty;

        return $"{recentCause}; Health={npc.Health:F2} Blood={npc.Needs.Blood:F2} " +
               $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
               $"Thermal={npc.Needs.ThermalComfort:+0.00;-0.00} " +
               $"Worst={worstPart}:{worstHp:F2}{vitals}";
    }

    private static string FindRecentDeathCauseEvent(WorldState world, NPCState npc)
    {
        var events = world.Events.Items;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            if (e.EntityId != npc.Id.Value)
            {
                continue;
            }

            // Death cleanup happens in the same or a nearby tick as the hit,
            // bleed-out, starvation or exposure event. Anything older is likely
            // stale context rather than the reason this body just dropped.
            if (world.Tick - e.Tick > 240)
            {
                break;
            }

            if (IsDeathCauseEvent(e.Type))
            {
                return $"{e.Type}: {e.Message}";
            }
        }

        return InferDeathCause(npc);
    }

    private static bool IsDeathCauseEvent(string type) => type switch
    {
        "BledOut" or
        "DogFight" or
        "Drowned" or
        "Heatstroke" or
        "Hypothermia" or
        "LimbSevered" or
        "PreyFoughtBack" or
        "Preyed" or
        // §72: without these two every raid death is recorded as inferred
        // starvation/exposure — and the soak's own accounting then lies about
        // the very mechanic being tuned.
        "RaidFoughtBack" or
        "RaidStruck" or
        "SharkBite" or
        "StarvedToDeath" or
        "Sunburn" or
        "VitalPartDestroyed" => true,
        _ => false
    };

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
