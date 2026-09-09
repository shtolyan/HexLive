using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
    /// <summary>
    /// Shared lifecycle for human assist fights. Many defenders may point at
    /// one attacker; the attacker keeps one reply target, always the closest
    /// living opponent currently engaging them.
    /// </summary>
    internal static class HumanCombatPairing
    {
        public static void EngageAssist(WorldState world, NPCState defender, NPCState attacker)
        {
            defender.Mind.CombatOpponentNpcId = attacker.Id;
            defender.IsFighting = true;
            SelectNearestReply(world, attacker);
        }

        public static void ClearFor(WorldState world, NPCState actor)
        {
            var oldOpponent = actor.Mind.CombatOpponentNpcId;
            actor.Mind.CombatOpponentNpcId = null;
            actor.IsFighting = false;
            FightScene.ReleaseSwingSlot(actor);

            if (oldOpponent is not { } opponentId ||
                !world.Entities.Npcs.TryGetValue(opponentId, out var opponent))
            {
                return;
            }

            if (opponent.Mind.CombatOpponentNpcId is { } back && back.Equals(actor.Id))
            {
                opponent.Mind.CombatOpponentNpcId = null;
                opponent.IsFighting = false;
                FightScene.ReleaseSwingSlot(opponent);
                SelectNearestReply(world, opponent);
            }
        }

        public static void ClearAssistsAgainst(WorldState world, EntityId attackerId)
        {
            foreach (var helper in world.Entities.Npcs.Values)
            {
                if (helper.Mind.CombatAssistAttackerNpcId is not { } assisted ||
                    !assisted.Equals(attackerId))
                {
                    continue;
                }

                ClearFor(world, helper);
                CombatHelpSystem.ClearAssist(helper);
            }

            if (world.Entities.Npcs.TryGetValue(attackerId, out var attacker))
            {
                SelectNearestReply(world, attacker);
            }
        }

        public static void SelectNearestReply(WorldState world, NPCState attacker)
        {
            NPCState nearest = null;
            var bestDistance = float.MaxValue;
            foreach (var candidate in world.Entities.Npcs.Values)
            {
                if (candidate.Id.Equals(attacker.Id) ||
                    candidate.Health <= 0f ||
                    candidate.IsUnconscious(world.Tick) ||
                    candidate.Mind.CombatOpponentNpcId is not { } target ||
                    !target.Equals(attacker.Id))
                {
                    continue;
                }

                var distance = HexSpatialMath.Distance(attacker.Position, candidate.Position);
                if (distance < bestDistance ||
                    (System.Math.Abs(distance - bestDistance) < 0.0001f &&
                     (nearest == null || candidate.Id.Value < nearest.Id.Value)))
                {
                    nearest = candidate;
                    bestDistance = distance;
                }
            }

            if (nearest == null)
            {
                attacker.Mind.CombatOpponentNpcId = null;
                attacker.IsFighting = false;
                FightScene.ReleaseSwingSlot(attacker);
                return;
            }

            attacker.Mind.CombatOpponentNpcId = nearest.Id;
            attacker.IsFighting = true;
        }
    }
}
