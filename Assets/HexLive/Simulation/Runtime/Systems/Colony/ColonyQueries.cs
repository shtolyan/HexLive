using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// Spec §64: small colony-scope world queries the dream layer needs. There is no
// generic "any object with tag T near P" helper in the codebase (every call
// site hand-rolls the scan), so the two the dream needs live here and are shared
// by DreamSystem, BedSiteSystem, and the sleep preference.
public static class ColonyQueries
{
    // §72: how many of ONE faction are still alive. "The colony" used to be
    // world.Entities.Npcs.Count, which quietly counts the outsider — and a bed
    // deficit or a colony dream measured against that can never be satisfied.
    // NOTE: counts every entry of the faction, dying ones included — exactly
    // what the pre-§72 `world.Entities.Npcs.Count` did (the death sweep removes
    // a body within the same medium pass). Adding a Health > 0 filter here
    // looks more correct and is NOT: it shifts bed demand by one on the tick
    // someone dies, and in a colony this finely balanced that cascades into a
    // measurably different run. The kill-switch has to be faithful first.
    public static int LivingCount(WorldState world, Agents.Faction faction)
    {
        var count = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (FactionRelations.AreAllies(npc.Faction, faction))
            {
                count++;
            }
        }

        return count;
    }

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

    // §72: this faction's camp anchor, or null if it has none authored.
    public static TileCoord? Home(WorldState world, Agents.Faction faction)
    {
        return world.FactionHomes.TryGetValue(faction, out var home) ? home : null;
    }

    // §72: is this tile inside the given camp? A campfire or a bed has no
    // Owner worth scoping by — a hearth belongs to whoever stands at it — so a
    // camp is scoped by DISTANCE to its anchor. With one camp authored this is
    // always true, which is why the pre-§72 answers are preserved exactly.
    public static bool InCamp(WorldState world, TileCoord tile, Agents.Faction faction)
    {
        if (Home(world, faction) is not { } home)
        {
            return true; // no anchor authored — the whole island is "the camp"
        }

        return HexSpatialMath.HexDistance(tile, home) <= Spec72.MaxCampRadiusTiles;
    }

    // Is there a lit campfire in THIS faction's camp? (tag "Campfire" + burning
    // fuel.) The campfire dream latches on the first true of this — so it must
    // be the colony's own hearth: the outsider lighting his fire first must not
    // tick the girls' dream off as fulfilled.
    public static bool LitCampfireExists(WorldState world, Agents.Faction faction)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.ResourceAmount > 0f &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.Tags.Contains("Campfire") &&
                InCamp(world, obj.Tile, faction))
            {
                return true;
            }
        }

        return false;
    }

    // Is there any campfire OBJECT (lit or cold) in this faction's camp? Used
    // when SpecDream is tuned to latch the campfire dream on existence rather
    // than on first light.
    public static bool CampfireObjectExists(WorldState world, Agents.Faction faction)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.Tags.Contains("Campfire") &&
                InCamp(world, obj.Tile, faction))
            {
                return true;
            }
        }

        return false;
    }
}

}
