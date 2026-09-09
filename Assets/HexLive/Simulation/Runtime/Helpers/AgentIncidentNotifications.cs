using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

/// <summary>§160.6: facts addressed to the witnessing agent, never commands.
/// Uses the ordinary personal perception lists, including visible hostiles.</summary>
internal static class AgentIncidentNotifications
{
    internal static bool CanWitness(WorldState world, NPCState observer, NPCState actor) =>
        observer.Mind.ExternalControl?.IsActive == true &&
        observer.Id != actor.Id && observer.Health > 0f &&
        !observer.IsUnconscious(world.Tick) && PerceptionMath.Sees(observer, actor.Id);

    internal static void Theft(WorldState world, NPCState actor, string item, EntityId? victim = null,
        WorldObjectState source = null)
    {
        foreach (var observer in world.Entities.Npcs.Values)
        {
            if (!CanWitness(world, observer, actor) ||
                source != null && !SeesObject(observer, source.Id) ||
                victim is { } victimId && victimId != observer.Id && !PerceptionMath.Sees(observer, victimId))
                continue;
            Trace.Emit(world, observer.Id, "AgentObservedTheft",
                $"Actor=NPC{actor.Id.Value} Item={item} Victim={victim?.Value.ToString() ?? "camp"} " +
                $"Tile={actor.Tile.Q},{actor.Tile.R}");
        }
    }

    internal static void Loot(WorldState world, NPCState actor, EntityId victim, string phase)
    {
        foreach (var observer in world.Entities.Npcs.Values)
        {
            if (!CanWitness(world, observer, actor) ||
                victim != observer.Id && !PerceptionMath.Sees(observer, victim)) continue;
            Trace.Emit(world, observer.Id, "AgentObservedLoot",
                $"Actor=NPC{actor.Id.Value} Victim=NPC{victim.Value} Phase={phase} " +
                $"Tile={actor.Tile.Q},{actor.Tile.R}");
        }
    }

    internal static void LootObject(WorldState world, NPCState actor, WorldObjectState source)
    {
        foreach (var observer in world.Entities.Npcs.Values)
        {
            if (!CanWitness(world, observer, actor) || !SeesObject(observer, source.Id)) continue;
            Trace.Emit(world, observer.Id, "AgentObservedLoot",
                $"Actor=NPC{actor.Id.Value} Object={source.Id.Value} Phase=TookItem " +
                $"Tile={actor.Tile.Q},{actor.Tile.R}");
        }
    }

    private static bool SeesObject(NPCState observer, ObjectId source)
    {
        foreach (var visible in observer.Perception.Objects)
            if (visible.Id == source && !visible.FromMemory) return true;
        return false;
    }
}

}
