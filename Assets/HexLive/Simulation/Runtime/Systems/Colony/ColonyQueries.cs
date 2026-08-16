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

        var own = HexSpatialMath.HexDistance(tile, home);
        if (own > Spec72.MaxCampRadiusTiles)
        {
            return false;
        }

        // §72.13: anchors sit ≥ OutsiderCampMinDistanceTiles (8) apart but the radius
        // is 6, so the two discs can OVERLAP — and a tile in the overlap used
        // to count as "in camp" for BOTH factions. That let BedSiteSystem
        // adopt the enemy hearth and stake this camp's beds around it, and let
        // the girls' campfire dream latch on the outsider's fire. A hostile
        // camp strictly closer claims the tile; a tie stays ours, mirroring
        // DecisionSystem.IsOurSite.
        foreach (var pair in world.FactionHomes)
        {
            if (!FactionRelations.AreAllies(faction, pair.Key) &&
                HexSpatialMath.HexDistance(tile, pair.Value) < own)
            {
                return false;
            }
        }

        return true;
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

    // ⭐ §54.2 r2 (баг #168): сколько предметов с этим тегом ЕСТЬ В МИРЕ.
    //
    // Это сознательно НЕ восприятие. Запас рощи (§64.9 «пальма — это вода
    // колонии») и «сначала подбери, что лежит» — утверждения про остров, а не
    // про то, что видно с этого места, и мерить их перцепцией значило мерить
    // не ту величину: стоя в свежей роще колонистка видела больше резерва и
    // рубила, потом отходила и видела следующие пять. Замер по сейву
    // (seed −28147312, tick 91585): пальм в мире НОЛЬ, пней 17, при этом на
    // земле лежат 595 листьев и 128 палок.
    //
    // Кэш на тик: сканировать ~1000 объектов на каждую NPC каждый тик решений
    // ни к чему, а состав мира внутри тика не меняется.
    public static int WorldCountWithTag(WorldState world, string tag)
    {
        if (world.TagCensusTick != world.Tick)
        {
            world.TagCensus.Clear();
            foreach (var obj in world.Entities.Objects.Values)
            {
                if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
                {
                    continue;
                }

                foreach (var objTag in def.Tags)
                {
                    world.TagCensus.TryGetValue(objTag, out var prior);
                    world.TagCensus[objTag] = prior + 1;
                }
            }

            world.TagCensusTick = world.Tick;
        }

        return world.TagCensus.TryGetValue(tag, out var count) ? count : 0;
    }
}

}
