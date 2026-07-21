using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

// Spec §64: small colony-scope world queries the dream layer needs. There is no
// generic "any object with tag T near P" helper in the codebase (every call
// site hand-rolls the scan), so the two the dream needs live here and are shared
// by DreamSystem, BedSiteSystem, and the sleep preference.
public static class ColonyQueries
{
    // The bed a colonist personally owns (a "Bed"-tagged object stamped with her
    // id), or null if she has none. Beds don't despawn, so this is stable — no
    // latch needed for the per-NPC own-bed dream.
    public static WorldObjectState OwnedBed(WorldState world, EntityId id)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Owner is { } owner && owner.Equals(id) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.Tags.Contains("Bed"))
            {
                return obj;
            }
        }

        return null;
    }

    // Is there a lit campfire in the world? (tag "Campfire" + burning fuel.)
    // The campfire dream latches on the first true of this.
    public static bool LitCampfireExists(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.ResourceAmount > 0f &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.Tags.Contains("Campfire"))
            {
                return true;
            }
        }

        return false;
    }

    // Is there any campfire OBJECT (lit or cold)? Used when SpecDream is tuned to
    // latch the campfire dream on existence rather than on first light.
    public static bool CampfireObjectExists(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.Tags.Contains("Campfire"))
            {
                return true;
            }
        }

        return false;
    }
}

}
