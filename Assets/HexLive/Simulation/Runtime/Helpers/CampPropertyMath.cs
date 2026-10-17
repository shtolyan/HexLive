using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
/// <summary>§133.12: everyday AI is a guest, not an unsolicited camp looter.
/// This is admission, not ownership transfer or a change to theft penalties.</summary>
internal static class CampPropertyMath
{
    private static bool ExplicitOrder(NPCState actor) =>
        NpcControlPolicy.IsPlayerGoal(actor.Plan.Goal) ||
        (ManualControlMath.IsManual(actor) && NpcControlPolicy.IsManualOrderGoal(actor.Plan.Goal));

    private static bool HostileCamp(WorldState world, NPCState actor, Faction faction) =>
        !CampDiplomacyMath.IsSoloCampMode(world.Mode) &&
        FactionRelations.AreHostile(world, actor.Faction, faction);

    private static bool PermittedGround(WorldState world, NPCState actor,
        TileCoord tile, Faction? hostileOwner = null)
    {
        foreach (var home in world.FactionHomes)
        {
            if (!FactionRelations.AreAllies(actor.Faction, home.Key) &&
                home.Key != hostileOwner && !HostileCamp(world, actor, home.Key) &&
                HexSpatialMath.HexDistance(tile, home.Value) <= TheftMath.PrivateGroundRadiusTiles)
                return false;
        }
        return true;
    }

    internal static bool CanDeposit(WorldState world, NPCState actor, TileCoord tile) =>
        ExplicitOrder(actor) || PermittedGround(world, actor, tile);

    internal static bool CanUse(WorldState world, NPCState actor, PerceivedObject obj) =>
        world.Entities.Objects.TryGetValue(obj.Id, out var live)
            ? CanUse(world, actor, live)
            : ExplicitOrder(actor) || PermittedGround(world, actor, obj.Tile);

    internal static bool CanUse(WorldState world, NPCState actor, WorldObjectState obj)
    {
        if (ExplicitOrder(actor)) return true;

        // Own/allied property may be recovered anywhere. Existing fellow-owner
        // wear grants are still checked separately by the wardrobe code.
        Faction? hostileOwner = null;
        if (obj.Owner is { } id && world.Entities.Npcs.TryGetValue(id, out var owner) && owner.Health > 0f)
        {
            if (FactionRelations.AreAllies(actor, owner)) return true;
            var hostile = CampDiplomacyMath.IsSoloCampMode(world.Mode)
                ? actor.Social.Relationships.TryGetValue(owner.Id, out var relationship) &&
                  relationship.Affinity <= CampDiplomacyMath.HatredAffinityThreshold
                : HostileCamp(world, actor, owner.Faction);
            if (!hostile) return false;
            hostileOwner = owner.Faction;
        }
        if (obj.OwnerFaction is { } faction)
        {
            if (FactionRelations.AreAllies(actor.Faction, faction)) return true;
            if (faction != hostileOwner && !HostileCamp(world, actor, faction)) return false;
        }
        return PermittedGround(world, actor, obj.Tile, hostileOwner);
    }
}
}
