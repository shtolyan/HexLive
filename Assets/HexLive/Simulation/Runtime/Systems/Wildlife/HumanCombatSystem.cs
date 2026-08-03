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

// §72: the blows of a human fight, and ONLY the blows. Who is fighting whom,
// who rallies, who runs and who dies is RaidSystem's business — this mirrors
// exactly the MobSystem/AnimalCombatSystem split, for the same reason: the
// timing has to run on the Fast layer or a swing cannot be timed at all.
//
// Every fighter carries Mind.CombatOpponentNpcId while the fight is live; that
// is both the pairing and the claim on the single swing slot each body has.
public sealed class HumanCombatSystem : ISimulationSystem
{
    public string Name => nameof(HumanCombatSystem);

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        if (!Spec72.Enabled)
        {
            return;
        }

        foreach (var actor in world.Entities.Npcs.Values)
        {
            if (actor.Mind.CombatOpponentNpcId is not { } opponentId ||
                actor.Health <= 0f ||
                actor.IsUnconscious(world.Tick) ||
                actor.Body.IsProne ||
                !world.Entities.Npcs.TryGetValue(opponentId, out var opponent) ||
                opponent.Health <= 0f)
            {
                continue;
            }

            // Turn to face — the same courtesy the dog fight pays.
            FaceOpponent(world, actor, opponent);

            // And square up at arm's length — the same spacing the dog fight
            // keeps (AnimalCombatSystem.ClampToHoldDistance).
            HoldStandOff(world, actor, opponent);

            var inReach = InteractionReach.CanStrike(world, actor, opponent);
            if (!MeleeSwing.TryAdvanceSwing(world, actor, inReach,
                    out var damage, out var weaponId, out var clipSeconds))
            {
                continue;
            }

            if (!inReach || damage <= 0f)
            {
                continue; // the swing resolved into thin air — she stepped away
            }

            // §97: «нападает» — это и налёт, и сцена абьюза. Без второй половины
            // ЕГО удары помечались как ответные (RaidFoughtBack), и по логу было
            // не разобрать, кто кого бьёт.
            // §107: и третья половина — групповая охота. Тут «нападает» уже
            // ОНА, и без этой ветки её удары попадали бы в лог как ответные,
            // то есть расправа читалась бы как самооборона.
            var hunting = FactionRelations.AreHostile(actor, opponent) &&
                actor.Mind.CurrentGoal == GoalType.GroupHunt &&
                actor.Mind.GroupHuntTargetNpcId is { } huntTarget &&
                huntTarget.Equals(opponent.Id);
            var raiding = FactionRelations.AreHostile(actor, opponent) &&
                ((actor.Mind.RaidTargetNpcId is { } raidTarget && raidTarget.Equals(opponent.Id)) ||
                 (actor.Mind.AbuseTargetNpcId is { } abuseTarget && abuseTarget.Equals(opponent.Id)));
            // Множитель налёта — только настоящему налёту. У сцены абьюза свой
            // регулятор: чем она бьёт (лестница ненависти) и сколько ударов.
            if (raiding && actor.Mind.CurrentGoal == GoalType.Raid)
            {
                // The one dial that softens the raider without touching the gear
                // sheets the girls swing too.
                damage *= Spec72.RaidStrikeDamageMult;
            }

            // §103: постановочная сцена САМА считает свои удары и сама решает,
            // когда открыть следующий замах — см. AI/FightScene. Здесь стоял
            // блок, знавший про абьюз поимённо: он различал бьющего и
            // отбивающуюся, лез в Spec81 за числом ударов и разводил их по
            // БАЗОВОЙ длительности оружия, тогда как сам замах брался из
            // варианта удара. Две мерки на одно расстояние — ровно та болезнь,
            // что дала мёртвую зону §102, только во времени.
            FightScene.OnBlowLanded(world, actor, clipSeconds);

            if (hunting)
            {
                actor.Mind.GroupHuntBlowsLanded++;
            }

            MeleeSwing.ApplyHumanBlow(world, actor, opponent, damage, weaponId,
                hunting ? "GroupHuntStruck" : raiding ? "RaidStruck" : "RaidFoughtBack");

            // Emit the outcome HERE, at the blow that caused it. MobSystem
            // sweeps every 0-health NPC on the next medium pass — before
            // RaidSystem gets a look — so a trace left to the medium layer
            // would simply never fire, and the soak would count zero raider
            // deaths while the man died six times.
            if (opponent.Health <= 0f)
            {
                Trace.EmitSystem(world, raiding ? "RaidKilledVictim" : "RaiderKilled",
                    $"NPC{opponent.Id.Value} ({opponent.DisplayName}) killed by " +
                    $"NPC{actor.Id.Value} ({actor.DisplayName}) at " +
                    $"Tile={opponent.Tile.Q},{opponent.Tile.R}");
                actor.Mind.CombatOpponentNpcId = null;
            }
        }
    }

    // §72.5 combat stand-off — the human mirror of the mob fight's
    // ClampToHoldDistance. A paired STANDING fighter backs her rendered
    // Position off until the pair is MeleeHoldDistance apart; each side runs
    // this for itself, so a mutual pairing splits the gap symmetrically and a
    // one-sided one (a defender on the raider) converges alone. A MOVING
    // fighter is owned by MovementSystem and left alone — a chase can always
    // close, and there is no tug-of-war over Position. Junction/Tile (reach,
    // pathing, occupancy) are untouched, and movement self-heals: the next
    // walk simply starts from the shifted spot.
    private static void HoldStandOff(WorldState world, NPCState actor, NPCState opponent)
    {
        var hold = Spec72.MeleeHoldDistance;
        if (hold <= 0.001f || actor.Movement.IsMoving)
        {
            return;
        }

        var distance = HexSpatialMath.Distance(actor.Position, opponent.Position);
        if (distance >= hold)
        {
            return;
        }

        // Straight away from the opponent. Two coincident bodies (the
        // same-junction corner case) split along a stable per-pair axis —
        // hashed from the ids only, never the tick, so it cannot jitter.
        Float2 dir;
        if (distance > 0.0001f)
        {
            dir = HexSpatialMath.Normalize(actor.Position - opponent.Position);
        }
        else
        {
            var angle = MathUtil.Hash01(world.Seed, actor.Id.Value, opponent.Id.Value, 811) *
                2f * System.MathF.PI;
            dir = new Float2(System.MathF.Cos(angle), System.MathF.Sin(angle));
        }

        // Each tick closes at most half the remaining gap: two standing
        // fighters meet the ring exactly instead of overshooting past it, and
        // a lone adjuster still converges geometrically within a second.
        var step = System.MathF.Min(
            Spec72.MeleeHoldGlideSpeed * world.TickDeltaTime,
            (hold - distance) * 0.5f);
        actor.Position += dir * step;
    }

    private static void FaceOpponent(WorldState world, NPCState actor, NPCState opponent)
    {
        var direction = new Float2(
            opponent.Position.X - actor.Position.X,
            opponent.Position.Y - actor.Position.Y);
        if (System.Math.Abs(direction.X) < 0.0001f && System.Math.Abs(direction.Y) < 0.0001f)
        {
            return;
        }

        actor.RotationDegrees = MathUtil.RotateTowards(
            actor.RotationDegrees,
            HexSpatialMath.AngleDegrees(direction),
            actor.TurnSpeed * 4f * AttributeMath.TurnSpeedMult(actor) * world.TickDeltaTime);
    }
}

}
