using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;
using System.Collections.Generic;

namespace HexLive.Simulation.Runtime
{

/// <summary>§160.6: newly observed foreign camp visitors wake the agent.
/// No world-object scan, and no automatic choice to expel or attack.</summary>
public sealed class AgentIncidentSystem : ISimulationSystem
{
    public string Name => nameof(AgentIncidentSystem);
    public TickLayer Layer => TickLayer.Medium;
    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world)
    {
        foreach (var observer in world.Entities.Npcs.Values)
        {
            var control = observer.Mind.ExternalControl;
            if (control?.IsActive != true || observer.Health <= 0f || observer.IsUnconscious(world.Tick)) continue;
            control.VisibleIntruders.Clear();
            Observe(world, observer, control, observer.Perception.Agents);
            Observe(world, observer, control, observer.Perception.Hostiles);
            control.SeenIntruders.Clear();
            control.SeenIntruders.UnionWith(control.VisibleIntruders);
        }
    }

    private static void Observe(WorldState world, NPCState observer, ExternalNpcControl control,
        List<PerceivedAgent> visible)
    {
        foreach (var actor in visible)
        {
            if (FactionRelations.AreAllies(observer.Faction, actor.Faction) ||
                !ColonyQueries.InCamp(world, actor.Tile, observer.Faction)) continue;
            control.VisibleIntruders.Add(actor.Id);
            if (control.SeenIntruders.Contains(actor.Id)) continue;
            Trace.Emit(world, observer.Id, "AgentObservedIntruder",
                $"Actor=NPC{actor.Id.Value} Faction={actor.Faction} Tile={actor.Tile.Q},{actor.Tile.R}");
        }
    }
}

}
