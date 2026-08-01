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

// §70: the blows of a human fight, and ONLY the blows. Who is fighting whom,
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
        if (!Spec70.Enabled)
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

            var inReach = MeleeSwing.InReach(world, actor, opponent);
            if (!MeleeSwing.TryAdvanceSwing(world, actor, inReach, out var damage, out var weaponId))
            {
                continue;
            }

            if (!inReach || damage <= 0f)
            {
                continue; // the swing resolved into thin air — she stepped away
            }

            var raiding = FactionRelations.AreHostile(actor, opponent) &&
                actor.Mind.RaidTargetNpcId is { } raidTarget && raidTarget.Equals(opponent.Id);
            if (raiding)
            {
                // The one dial that softens the raider without touching the gear
                // sheets the girls swing too.
                damage *= Spec70.RaidStrikeDamageMult;
            }

            MeleeSwing.ApplyHumanBlow(world, actor, opponent, damage, weaponId,
                raiding ? "RaidStruck" : "RaidFoughtBack");

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
            actor.TurnSpeed * 4f * world.TickDeltaTime);
    }
}

}
