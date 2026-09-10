using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Agents.Effects
{
    public interface IActiveEffectSink
    {
        void Add(ActiveEffect effect);
    }

    public interface IEffectSink : IActiveEffectSink
    {
        void Add(EffectImpact impact);
    }

    /// <summary>§48.8: one read-only projection for status chips and live need causes.</summary>
    public static class EffectReadModel
    {
        public static void Visit<TSink>(WorldState world, NPCState npc, ref TSink sink)
            where TSink : struct, IEffectSink
        {
            if (npc == null) return;
            Visit(world, npc, TemperatureSystem.EffectiveUv(world, npc.Tile), ref sink);
        }

        // Snapshot already computed UV for the sun display; do not probe it twice.
        public static void Visit<TSink>(WorldState world, NPCState npc, float effectiveUv,
            ref TSink sink) where TSink : struct, IEffectSink
        {
            if (npc == null) return;
            var nearLitFire = TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f;
            var restingInBed = npc.Execution.CurrentInteraction == InteractionType.Sleep &&
                npc.Execution.TargetObject is { } bedId &&
                world.Entities.Objects.TryGetValue(bedId, out var bed) &&
                bed.DefinitionId == ContentIds.BedBasic;
            EffectEvaluator.Collect(npc, world.Tick, effectiveUv, nearLitFire, restingInBed, ref sink);

            var impacts = npc.EffectImpacts.Items;
            for (var i = 0; i < impacts.Count; i++)
            {
                var row = impacts[i];
                var duplicate = false;
                for (var previous = 0; previous < i; previous++)
                {
                    var earlier = impacts[previous];
                    if (earlier.Need == row.Need && earlier.Kind == row.Kind &&
                        earlier.Direction == row.Direction)
                    {
                        duplicate = true;
                        break;
                    }
                }
                // Cadence remains owned by the ledger. Never sum magnitudes
                // or erase an opposite direction while deduplicating its view.
                if (!duplicate) sink.Add(row);
            }
        }
    }
}
