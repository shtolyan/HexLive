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

// Timed melee exchange (spec 29C.3 v2): the dog and its quarry face off and
// trade blows with real timing — windup (the lunge / the swing) → the hit
// lands (ALL damage happens at that instant) → cooldown. Runs on the Fast
// layer (0.25 s ticks) because the timings are sub-second: dog 0.5 s windup /
// 0.8 s cooldown (SimBalance.Dog*), NPC 0.25 s windup / per-weapon cooldown
// (GearCatalog per-weapon sheets). A started windup ALWAYS lands — a dog
// that began its lunge bites even if the girl stepped away mid-charge.
// MobSystem (medium) still owns targeting, chase, flee assessment, defenders
// and spawning; this system owns only the blows and their deaths.
public sealed class AnimalCombatSystem : ISimulationSystem
{
    public string Name => nameof(AnimalCombatSystem);

    public TickLayer Layer => TickLayer.Fast;

    // Per-mob combat/movement config (one per mob type — see MobCatalog),
    // resolved per creature so mixed packs each obey their own sheet.
    private static Content.MobStats Stats(Wildlife.MobState dog) => Content.MobCatalog.For(dog.MobId);

    // TickDeltaTime is 0.25 s in every bootstrap; combat quantizes seconds
    // onto that grid (minimum one tick).
    public const float TicksPerSecond = 4f;

    public static int SecondsToTicks(float seconds)
    {
        return System.Math.Max(1, (int)System.Math.Round(seconds * TicksPerSecond));
    }

    private readonly System.Collections.Generic.List<Wildlife.MobState> _deadDogs = new();
    private readonly System.Collections.Generic.HashSet<EntityId> _deadNpcs = new();
    private readonly System.Collections.Generic.HashSet<int> _struckNpcs = new();

    public void Run(WorldState world)
    {
        _deadDogs.Clear();
        _deadNpcs.Clear();
        _struckNpcs.Clear();

        foreach (var dog in world.Mobs)
        {
            GlideDog(world, dog);
            RunExchange(world, dog);
            if (dog.Health <= 0f)
            {
                _deadDogs.Add(dog);
            }
        }

        foreach (var dead in _deadDogs)
        {
            world.Mobs.Remove(dead);
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.Mind.CombatAssistDogId == dead.Id)
                {
                    CombatHelpSystem.ClearAssist(npc);
                }
            }

            Trace.EmitSystem(world, "DogKilled",
                $"Dog={dead.Id} at Tile={dead.Tile.Q},{dead.Tile.R}");
            // Spec §54: the fallen mob leaves a butcherable carcass; the
            // variant carries its mob id so the view shows the right body.
            ExecutionSystem.SpawnCarcass(world, dead.Tile, dead.Junction, dead.MobId);
            MobSystem.ForgetDangerAround(world, dead.Tile, 1); // §54.16: the fear dies with the beast
        }

        // Only bite victims are swept here; every other death cause keeps its
        // existing handler (MobSystem's medium sweep and friends).
        foreach (var deadId in _deadNpcs)
        {
            MobSystem.RemoveDeadNpc(world, deadId);
        }
    }

    // Continuous per-tick movement — the SAME model as an NPC (MovementSystem
    // advances the walker a small delta each fast tick). MobSystem sets the
    // dog's logical junction (and thus TargetPosition) on the medium tick;
    // here the rendered Position glides toward it at a constant per-segment
    // speed, so a whole segment is covered over one medium period and the view
    // never sees a teleport to interpolate as a dash-then-stand jerk. Chase
    // segments span several junctions → a proportionally faster glide (the run
    // gait); roam spans one → a walk.
    private static void GlideDog(WorldState world, Wildlife.MobState dog)
    {
        // Defensive: a dog constructed without seeding TargetPosition would
        // have it at the origin and glide/snap there — treat the un-seeded
        // case (both target and anchor still zero, dog not at origin) as
        // "stand where you are".
        if (dog.TargetPosition.X == 0f && dog.TargetPosition.Y == 0f &&
            dog.GlideAnchor.X == 0f && dog.GlideAnchor.Y == 0f &&
            (dog.Position.X != 0f || dog.Position.Y != 0f))
        {
            dog.TargetPosition = dog.Position;
            dog.GlideAnchor = dog.Position;
            return;
        }

        var target = ClampToHoldDistance(world, dog, dog.TargetPosition);
        var remaining = HexSpatialMath.Distance(dog.Position, target);
        if (remaining <= 0.0001f)
        {
            return;
        }

        // New segment (target moved since we last recomputed): pick a constant
        // speed that finishes this hop in one movement segment.
        if (HexSpatialMath.Distance(dog.GlideAnchor, target) > 0.0001f)
        {
            dog.GlideAnchor = target;
            var segmentSeconds = System.Math.Max(0.05f,
                Stats(dog).GlideSegmentSeconds);
            dog.GlideSpeed = remaining / segmentSeconds;
        }

        // A huge gap is a teleport (spawn/save-load/respawn), not a walk — snap.
        if (remaining > Stats(dog).GlideSnapDistance)
        {
            dog.Position = target;
            return;
        }

        var step = System.Math.Max(dog.GlideSpeed, 0.01f) * world.TickDeltaTime;
        if (step >= remaining)
        {
            dog.Position = target;
            return;
        }

        var dir = HexSpatialMath.Normalize(target - dog.Position);
        dog.Position = dog.Position + dir * step;
    }

    // Combat spacing (§29C.3): an engaged mob's RENDERED glide stops at arm's
    // length from its quarry instead of riding onto her point. Melee is
    // junction-based and junction spacing (~0.37 wu) is far tighter than the
    // models, so without this the pair literally stands inside each other.
    // Only the glide target is bent — Junction/Tile (aggro, pathing, reach)
    // and the persisted TargetPosition stay untouched. Recomputed every fast
    // tick, so a stepping/turning girl smoothly pushes the wolf back and the
    // pair keeps facing off at a constant gap.
    private static Float2 ClampToHoldDistance(
        WorldState world, Wildlife.MobState dog, Float2 target)
    {
        if (dog.Status == Wildlife.MobStatus.Roaming || dog.TargetNpc is not { } quarryId ||
            !world.Entities.Npcs.TryGetValue(quarryId, out var quarry) ||
            quarry.Health <= 0f)
        {
            return target;
        }

        var hold = Stats(dog).MeleeHoldDistance;
        if (hold <= 0.001f)
        {
            return target;
        }

        var fromQuarry = target - quarry.Position;
        var distance = HexSpatialMath.Distance(quarry.Position, target);
        if (distance >= hold)
        {
            return target;
        }

        // Glide target inside the hold ring — project it back out. When the
        // target sits exactly ON the girl (same junction) the push direction
        // comes from where the dog actually is, so it backs out the way it
        // came instead of snapping to an arbitrary side.
        var dir = distance > 0.0001f
            ? fromQuarry * (1f / distance)
            : HexSpatialMath.Distance(quarry.Position, dog.Position) > 0.0001f
                ? HexSpatialMath.Normalize(dog.Position - quarry.Position)
                : new Float2(1f, 0f);
        return quarry.Position + dir * hold;
    }

    private void RunExchange(WorldState world, Wildlife.MobState dog)
    {
        NPCState? target = null;
        if (dog.TargetNpc is { } targetId)
        {
            world.Entities.Npcs.TryGetValue(targetId, out target);
        }

        // 1) A wound-up bite lands — in range or not.
        if (dog.AttackLandsAtTick > 0 && world.Tick >= dog.AttackLandsAtTick)
        {
            dog.AttackLandsAtTick = 0;
            dog.AttackReadyAtTick = world.Tick +
                SecondsToTicks(Stats(dog).AttackCooldownSeconds);
            if (target is { Health: > 0f })
            {
                LandBite(world, dog, target);
            }
        }

        if (target is null)
        {
            return;
        }

        if (target.Health <= 0f)
        {
            _deadNpcs.Add(target.Id);
            dog.TargetNpc = null;
            dog.Status = Wildlife.MobStatus.Roaming;
            return;
        }

        var inMelee = InMelee(world, dog, target);

        // 2) Wind up the next bite when recovered and in reach. A chasing dog
        // that just caught up starts its lunge immediately — no medium-tick
        // wait for the Fighting status.
        if (dog.AttackLandsAtTick == 0 && inMelee && world.Tick >= dog.AttackReadyAtTick &&
            dog.Status != Wildlife.MobStatus.Roaming)
        {
            dog.AttackLandsAtTick = world.Tick +
                SecondsToTicks(Stats(dog).AttackWindupSeconds);
            dog.AttackStartTick = world.Tick;
            Trace.EmitSystem(world, "DogWindup",
                $"Dog={dog.Id} lunges at NPC{target.Id.Value}");
        }

        // The moment the exchange is live, the quarry DEFENDS — on this fast
        // tick, not next medium pass: drop the current plan, stand, and square
        // up. Without this she kept strolling to her errand for up to a whole
        // medium tick while the first bites landed (MobSystem only flags
        // IsFighting once per medium pass). Fleeing girls keep running.
        if (inMelee && dog.Status != Wildlife.MobStatus.Roaming)
        {
            EngageQuarry(world, dog, target);
        }

        // Square up: an engaged standing fighter turns to her attacker fast
        // (combat turn is snappier than the walking turn), so the strikes read
        // face-to-face instead of landing on her back.
        if (target.IsFighting && inMelee)
        {
            FaceDog(world, target, dog);
        }

        // 3) The quarry's counter-swing (standing fighters only — a fleeing
        // girl does not trade hits). One engaged dog per NPC per pass so a
        // pack doesn't multiply her swings.
        if (target.IsFighting && !target.Body.IsProne &&
            !target.IsUnconscious(world.Tick) && // §60: no swings from a coma
            dog.Health > 0f && _struckNpcs.Add(target.Id.Value))
        {
            RunCounterStrike(world, dog, target, inMelee);
        }

        // 4) §104 r8: ПОДМОГА бьёт по тому же таймлайну, что и жертва.
        //
        // Помощницы дрались по легаси-модели (MobSystem, урон за средний
        // проход) и не ставили НИ ОДНОГО видового сигнала: их удары были
        // невидимы — кровь у собаки есть, замаха нет. Здесь они получают
        // ровно то же, что жертва: окно анимации, штамп замаха, вариант удара.
        if (SimBalance.TimedMeleeEverywhere && dog.Health > 0f)
        {
            RunAssistStrikes(world, dog);
        }
    }

    /// <summary>
    /// Удары тех, кто прибежал на помощь (§57): та же собака, тот же таймлайн.
    /// Жертва обслужена выше и в список не попадает — <c>_struckNpcs</c>
    /// держит правило «один замах на тело за проход».
    /// </summary>
    private void RunAssistStrikes(WorldState world, Wildlife.MobState dog)
    {
        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (helper.Mind.CombatAssistDogId != dog.Id ||
                helper.Health <= 0f ||
                helper.Body.IsProne ||
                helper.IsUnconscious(world.Tick) ||
                !_struckNpcs.Add(helper.Id.Value))
            {
                continue;
            }

            // §106: a helper mid-swim throws no punches — swimmers don't strike.
            var inMelee = HexSpatialMath.HexDistance(helper.Tile, dog.Tile) <= 1 &&
                !(Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, helper));
            if (inMelee)
            {
                helper.IsFighting = true;
                FaceDog(world, helper, dog);
            }

            RunCounterStrike(world, dog, helper, inMelee);
        }
    }

    // Instant reactive defense (fast layer). Mirrors MobSystem's medium-pass
    // fight branch: interrupt whatever she was doing and hold her for the
    // exchange. Flee stays a medium-pass decision (assessment needs pack
    // counts); a girl already fleeing is never yanked back into a stand.
    private static void EngageQuarry(WorldState world, Wildlife.MobState dog, NPCState target)
    {
        if (target.Health <= 0f || target.IsFighting ||
            target.Body.IsProne ||  // §50-prone: lying — never pinned standing
            target.IsUnconscious(world.Tick) || // §60: out cold — can't stand to fight
            target.Mind.CurrentGoal == GoalType.Flee)
        {
            return;
        }

        target.IsFighting = true;
        if (target.Plan.Status == PlanStatus.Active ||
            target.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, target, $"Attacked by dog {dog.Id}");
            target.Mind.CurrentGoal = GoalType.None;
        }
    }

    // Combat facing: turn the standing fighter toward the engaged dog at 4x
    // her walking turn speed — a bite exchange squares up in a fraction of a
    // second instead of a leisurely stroll-turn.
    private static void FaceDog(WorldState world, NPCState npc, Wildlife.MobState dog)
    {
        var direction = new Float2(
            dog.Position.X - npc.Position.X,
            dog.Position.Y - npc.Position.Y);
        if (direction.X * direction.X + direction.Y * direction.Y < 0.0001f)
        {
            return;
        }

        npc.RotationDegrees = MathUtil.RotateTowards(
            npc.RotationDegrees,
            HexSpatialMath.AngleDegrees(direction),
            npc.TurnSpeed * 4f * AttributeMath.TurnSpeedMult(npc) * world.TickDeltaTime);
    }

    private static void RunCounterStrike(
        WorldState world, Wildlife.MobState dog, NPCState npc, bool inMelee)
    {
        // §72: a body has ONE swing slot. While a human fight owns it (the man
        // is on her, or she is on him) the dog exchange must not also drive it,
        // or the two would trade the same StrikeLandsAtTick back and forth.
        if (npc.Mind.CombatOpponentNpcId is not null)
        {
            return;
        }

        // ⭐ НЕ EffectiveWeapon: назначенное сценой оружие сюда не относится.
        // Сцена абьюза владеет ЧЕЛОВЕЧЕСКИМ боем, а ForcedMeleeWeaponId
        // переживает момент, когда пара уже расцеплена (оппонента убили), но
        // сцена ещё не дошла до End — тогда девушка вдруг лупила бы волка
        // назначенными кулаками. Против зверя — лучшее, что в руках.
        var weaponId = npc.Body.CanUseToolsOrWeapons
            ? SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.IntactHands)
            : string.Empty;

        // §104 r2: таймлайн замаха тут БОЛЬШЕ НЕ ЖИВЁТ — он один на всех, в
        // MeleeSwing.TryAdvanceSwing. Здесь осталось ровно то, чем собачий бой
        // отличается: урон принимает плоский Health (тела у собаки нет, а
        // значит ни брони, ни раны, ни части тела) и своё событие.
        //
        // Раньше здесь стояла посимвольная копия того же таймлайна, вплоть до
        // соли хеша 777. Именно она и разошлась: собачья половина ставила
        // SwingStartTick, человеческая эту строку потеряла — и удары человека
        // против человека не рисовались никогда (§103).
        if (!MeleeSwing.TryAdvanceSwing(world, npc, inMelee, weaponId, out var strike, out _))
        {
            return;
        }

        dog.Health -= strike;
        Trace.Emit(world, npc.Id, "DogFight",
            $"Dog={dog.Id} struck -{strike:F3}" +
            $"{(string.IsNullOrEmpty(weaponId) ? " (fists)" : " " + weaponId)} " +
            $"DogHealth={System.Math.Max(0f, dog.Health):F2}");
    }

    private static void LandBite(WorldState world, Wildlife.MobState dog, NPCState target)
    {
        // Spec 19.3C: the bite lands on a specific part; only garments
        // covering that part absorb it. §50: never a severed limb.
        var bitPart = AmputateSystemHelpers.RedirectFromStump(target,
            MobSystem.PickAttackPart(world, dog.Id));

        // §104 r5: жертве всё равно, чем в неё прилетело — виду нужен ОДИН
        // сигнал «сейчас попали», и укус даёт его тем же штампом, что удар.
        MeleeSwing.StampHit(world, target, MeleeSwing.BiteWeaponId, bitPart, dog.Position);
        var partArmor = EquipmentMath.ArmorForPart(world, target, bitPart); // trace only
        var damage = EquipmentMath.Mitigate(world, target, bitPart, Stats(dog).AttackDamage);
        target.Body.Parts[bitPart] = System.Math.Max(0f, target.Body.Parts[bitPart] - damage);
        target.Health = target.Body.Mean();
        DamageReactionSystemHelpers.GrantAdrenaline(world, target, damage, "DogBite");
        // Spec 40.8B: the landed bite leaves a wound record (drives the decal;
        // heals & fades on its own clock). Starvation/heat never create these.
        WoundMath.Inflict(world, target, bitPart, damage);

        // Spec §50: a bite that finishes off a mauled limb may tear it away.
        AmputateSystemHelpers.TrySeverOnBite(world, target, bitPart, damage);

        // Spec 35.6: the cloth gets chewed either way — every garment
        // covering the bitten part loses durability; rags fall apart.
        EquipmentMath.WearCoveringItems(world, target, bitPart,
            SimBalance.ClothingBiteDurabilityWear);

        // §105: единая развилка. Укус по уже лежащей на грани срезает запас
        // смерти — зверь догрызает упавшую, и это ускоряет её конец.
        MortalityHelpers.ResolveTrauma(world, target, damage, $"Dog={dog.Id}");

        Trace.Emit(world, target.Id, "DogFight",
            $"Dog={dog.Id} bit: {bitPart} -{damage:F3} (PartArmor={partArmor:F2}) " +
            $"Part={target.Body.Parts[bitPart]:F2} NpcHealth={target.Health:F2}" +
            $"{(target.IsFighting ? string.Empty : " (fleeing)")}");
    }

    private static bool InMelee(WorldState world, Wildlife.MobState dog, NPCState target)
    {
        // §105.14: притворяется мёртвой — зверь потерял к ней интерес и не
        // кусает. Зеркало гейта medium-слоя (MobSystem.IsNpcInRefugeFrom):
        // быстрый слой крутится между сбросом цели и укусом, так что без этой
        // проверки он успевал бы догрызть уже брошенную цель.
        if (target.IsPlayingDead(world.Tick))
        {
            return false;
        }

        // §106: adjacency alone is not a bite — the medium must match too. A
        // wolf at the shore is one junction from the swimmer and still cannot
        // reach her (and she, mid-stroke, cannot counter it either: the fast
        // layer runs between the medium-pass target-drop and this check).
        if (Spec106.WaterSanctuaryEnabled &&
            !CombatMedium.CanEngage(world,
                Content.MobCatalog.For(dog.MobId).AttackMediums, target))
        {
            return false;
        }

        return target.CurrentJunction is { } npcJunction &&
            (npcJunction.Equals(dog.Junction) ||
             (world.Junctions.Items.TryGetValue(dog.Junction, out var dogJunction) &&
              dogJunction.Neighbors.Contains(npcJunction)));
    }
}

}
