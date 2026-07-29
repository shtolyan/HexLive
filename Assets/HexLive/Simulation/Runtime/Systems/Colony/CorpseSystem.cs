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

// Spec 28.15C: bodies decay away after two days.
public sealed class CorpseSystem : ISimulationSystem
{
    public string Name => nameof(CorpseSystem);

    public TickLayer Layer => TickLayer.Slow;

    private readonly System.Collections.Generic.List<ObjectId> _decayed = new();

    public void Run(WorldState world)
    {
        _decayed.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            // §50: a severed limb ("Decays" tag) rots away on the same clock as
            // a body, but without the mourn/bury interactions a Corpse carries.
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !(definition.Tags.Contains("Corpse") || definition.Tags.Contains("Decays")))
            {
                continue;
            }

            obj.ResourceAmount -= 16f;
            if (obj.ResourceAmount <= 0f)
            {
                _decayed.Add(obj.Id);
            }
        }

        foreach (var id in _decayed)
        {
            WorldObjectMutations.DespawnObject(world, id);
            Trace.EmitSystem(world, "CorpseGone", $"Obj={id.Value} decayed");
        }
    }
}

}
