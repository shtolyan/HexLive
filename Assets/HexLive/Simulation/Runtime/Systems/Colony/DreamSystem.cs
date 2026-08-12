using System.Collections.Generic;
using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// Spec §64: the dream driver. A colony-driven, ordered aspiration queue
// (campfire → own bed → …) steers what the settlement BUILDS. This system only
// PUBLISHES state — it advances the queue, latches colony-wide dreams, writes
// each NPC's displayed dream, and frees a dead colonist's bed back to the pool.
// It never touches needs or plans, so with SpecDream.Enabled = false the colony
// reverts to the exact pre-§64 balance (it just parks the active dream at None).
// The actual building is the existing BuildFurniture chain, nudged by a small
// free-hands-gated pull in DecisionSystem; personal beds are the Owner stamp
// that BedSiteSystem writes. Runs Slow, immediately BEFORE BedSiteSystem so the
// active dream / ownership it reads are fresh the same tick.
public sealed class DreamSystem : ISimulationSystem
{
    public string Name => nameof(DreamSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        if (!SpecDream.Enabled)
        {
            // Rollback / harness-bisect: park the dream so nothing gates on it.
            world.ActiveDream = DreamType.None;
            return;
        }

        // Seed the queue once (idempotent — heals fresh AND loaded worlds
        // without touching the factory or serializer).
        if (world.DreamQueue.Count == 0 && SpecDream.DefaultQueue != null)
        {
            world.DreamQueue.AddRange(SpecDream.DefaultQueue);
        }

        // Latch the campfire dream the first time its basis appears. Monotonic:
        // a fire burning out later must not re-open the dream.
        if (!world.CampfireDreamDone)
        {
            // §72: the COLONY's own hearth. Without the faction scope the
            // outsider lighting his fire first would tick the girls' dream off
            // as fulfilled while they still sat in the cold.
            var seen = SpecDream.CampfireRequiresLit
                ? ColonyQueries.LitCampfireExists(world, Faction.Colony)
                : ColonyQueries.CampfireObjectExists(world, Faction.Colony);
            if (seen)
            {
                world.CampfireDreamDone = true;
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "DreamFulfilled",
                        "campfire — the colony has its hearth");
                }
            }
        }

        // One object pass: collect bed owners, and reclaim any bed whose owner
        // has died (back to the shared pool for a bedless survivor to claim).
        // §72: TWO sets, and the distinction matters. Bed reclaim keys on
        // "is the owner alive at all" — scoping THAT to the colony would free
        // the outsider's own bed every slow tick. The colony DREAM keys on "do
        // all of OURS have one" — counting him there would leave the OwnBed
        // dream permanently unmet, pinning BuildPull on a project that can
        // never close.
        var livingIds = new HashSet<EntityId>();
        var colonyLivingIds = new HashSet<EntityId>();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f)
            {
                livingIds.Add(npc.Id);
                if (npc.Faction == Faction.Colony)
                {
                    colonyLivingIds.Add(npc.Id);
                }
            }
        }

        var bedOwners = new HashSet<EntityId>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            // §133: одежда покойной становится ничейной по тому же правилу, что
            // и её кровать, — иначе разрешение спрашивать не у кого и вещь
            // навсегда выпадает из оборота колонии.
            if (def.Layer != null)
            {
                if (obj.Owner is { } garmentOwner && !livingIds.Contains(garmentOwner))
                {
                    obj.Owner = null;
                    if (SimTrace.Enabled)
                    {
                        Trace.DebugSystem(world, "GarmentReleased",
                            $"garment {obj.Id.Value} freed — owner gone");
                    }
                }

                continue;
            }

            if (!def.Tags.Contains("Bed"))
            {
                continue;
            }

            if (obj.Owner is { } owner)
            {
                if (!livingIds.Contains(owner))
                {
                    obj.Owner = null;
                    if (SimTrace.Enabled)
                    {
                        Trace.DebugSystem(world, "BedReclaimed",
                            $"bed {obj.Id.Value} freed — owner gone");
                    }
                    continue;
                }

                bedOwners.Add(owner);
            }
        }

        // Colony active dream = first queue entry not yet met colony-wide.
        world.ActiveDream = DreamType.None;
        foreach (var dream in world.DreamQueue)
        {
            if (!ColonyDreamMet(world, dream, colonyLivingIds, bedOwners))
            {
                world.ActiveDream = dream;
                break;
            }
        }

        // Per-NPC displayed dream = first queue entry SHE hasn't fulfilled.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f)
            {
                continue;
            }

            var dream = DreamType.None;
            foreach (var candidate in world.DreamQueue)
            {
                if (!PersonalDreamMet(world, candidate, npc, bedOwners))
                {
                    dream = candidate;
                    break;
                }
            }

            npc.Mind.CurrentDream = dream;
        }
    }

    // A dream the WHOLE colony must reach before the queue advances past it.
    private static bool ColonyDreamMet(
        WorldState world, DreamType dream, HashSet<EntityId> livingIds, HashSet<EntityId> bedOwners)
    {
        switch (dream)
        {
            case DreamType.Campfire:
                return world.CampfireDreamDone;
            case DreamType.OwnBed:
                // Met once every living colonist owns a bed.
                foreach (var id in livingIds)
                {
                    if (!bedOwners.Contains(id))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true; // None / unknown — nothing to pursue.
        }
    }

    // A dream THIS colonist has personally fulfilled (campfire is shared, a bed
    // is her own).
    private static bool PersonalDreamMet(
        WorldState world, DreamType dream, NPCState npc, HashSet<EntityId> bedOwners)
    {
        switch (dream)
        {
            case DreamType.Campfire:
                // §72: the outsider dreams of HIS hearth, not the girls' latch —
                // otherwise he inherits their progress and never builds a fire
                // of his own. Same aspiration, his own camp.
                return npc.Faction == Faction.Colony
                    ? world.CampfireDreamDone
                    : ColonyQueries.LitCampfireExists(world, npc.Faction);
            case DreamType.OwnBed:
                return bedOwners.Contains(npc.Id);
            default:
                return true;
        }
    }
}

}
