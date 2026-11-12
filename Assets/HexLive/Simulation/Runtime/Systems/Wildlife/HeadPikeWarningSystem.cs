using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§120.14: a visible foreign pike may discourage visitors.</summary>
public sealed class HeadPikeWarningSystem : ISimulationSystem
{
    internal const int NoticeRadiusTiles = 2;
    internal const int NoticeCooldownTicks = 240;
    private const float RetreatStress = 0.65f;
    private readonly Dictionary<EntityId, int> _noticeUntil = new();

    public string Name => nameof(HeadPikeWarningSystem);
    public TickLayer Layer => TickLayer.Medium;
    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f || npc.IsDying || npc.IsUnconscious(world.Tick) || npc.IsPlayingDead(world.Tick) ||
                npc.IsFighting || npc.IsBeingCarried || npc.IsCarryingPerson ||
                npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                (_noticeUntil.TryGetValue(npc.Id, out var until) && world.Tick < until)) continue;

            var pike = FindForeignPike(world, npc);
            if (pike is null) continue;
            _noticeUntil[npc.Id] = world.Tick + NoticeCooldownTicks;

            var brave = npc.Traits.Has(TraitKind.Brave);
            var coward = npc.Traits.Has(TraitKind.Coward);
            npc.Needs.Stress = MathUtil.Clamp01(npc.Needs.Stress + (brave ? 0.06f : coward ? 0.32f : 0.20f));

            var retreat = !brave && (coward || npc.Needs.Stress >= RetreatStress) &&
                !npc.Mind.IsStarving && !npc.Mind.IsDehydrated &&
                !GoalCatalog.IsReactive(npc.Mind.CurrentGoal) &&
                MobSystem.TryFleeFromPoint(world, npc, pike.Tile, NoticeRadiusTiles, ContentIds.HeadPike);
            SocialCueSignals.StampItem(world, npc,
                retreat ? "HeadPikeRetreat" : brave ? "HeadPikeDefiant" : "HeadPikeUneasy",
                ContentIds.HeadPike);
        }
    }

    private static WorldObjectState FindForeignPike(WorldState world, NPCState npc)
    {
        WorldObjectState nearest = null;
        var bestDistance = float.MaxValue;
        foreach (var seen in npc.Perception.Objects)
        {
            if (seen.DefinitionId != ContentIds.HeadPike || seen.FromMemory || !seen.IsReachable ||
                HexSpatialMath.HexDistance(npc.Tile, seen.Tile) > NoticeRadiusTiles ||
                !world.Entities.Objects.TryGetValue(seen.Id, out var pike) ||
                pike.DefinitionId != ContentIds.HeadPike || pike.Fragment != npc.Fragment ||
                pike.OwnerFaction is not { } camp || FactionRelations.AreAllies(npc.Faction, camp)) continue;

            if (seen.Distance < bestDistance || (seen.Distance == bestDistance &&
                nearest is not null && pike.Id.Value < nearest.Id.Value))
            {
                nearest = pike;
                bestDistance = seen.Distance;
            }
        }

        return nearest;
    }
}
}
