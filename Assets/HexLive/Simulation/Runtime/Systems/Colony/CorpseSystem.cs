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

// §50/§54: то, что истлевает само — отрубленная конечность и звериная туша.
//
// §28.15C v3: ЧЕЛОВЕЧЕСКОГО тела здесь больше нет. Раньше труп гнил двое суток
// и исчезал, а вместе с ним исчезали и вещи на нём, и сам факт, что тут кто-то
// погиб. Теперь тело лежит там, где упало, до конца игры — остров помнит своих
// мёртвых. Убрать его может только нож (§56).
//
// Тег «Corpse» намеренно НЕ входит в фильтр: разница между «истлевает» и «нет»
// живёт ровно в этом одном условии, а не в таймере, который кто-то мог бы
// однажды выставить трупу «на всякий случай».
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
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Decays"))
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
