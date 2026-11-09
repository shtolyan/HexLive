using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Bug #312 (вердикт игрока): всё в радиусе пяти гексов от чужого лагеря —
/// приватная территория. Взять там чужую вещь — не «подобрать», а УКРАСТЬ:
/// меню красит глагол красным (клиент считает то же предикатом по снапшоту),
/// а свидетельницы лагеря-хозяина, видящие воровку, теряют к ней отношение.
///
/// Личная собственность другой живой NPC защищена и внутри своего лагеря.
/// Радиус меряется от домашнего якоря лагеря (WorldState.FactionHomes — он и
/// есть костёр лагеря, §146.14). Своё и союзное забрать — не кража: владелец
/// вещи снимает подозрение раньше геометрии только для самой хозяйки/разрешения.
/// </summary>
public static class TheftMath
{
    public const int PrivateGroundRadiusTiles = 5;

    // Замеченная кража бьёт по отношению свидетельницы к воровке — тем же
    // порядком величины, что благодарность за спасение (§57), только в минус.
    public const float WitnessAffinityHit = 0.15f;

    public static bool IsForeignPrivateGround(
        WorldState world, Faction actorFaction, TileCoord tile)
    {
        foreach (var pair in world.FactionHomes)
        {
            if (FactionRelations.AreAllies(actorFaction, pair.Key))
            {
                continue;
            }

            if (HexSpatialMath.HexDistance(tile, pair.Value) <= PrivateGroundRadiusTiles)
            {
                return true;
            }
        }

        return false;
    }

    // Personal property does not become communal just because its owner is an ally.
    public static bool IsUnauthorizedUse(int actorId, Faction actorFaction,
        int? personalOwnerId, Faction? communalOwner, bool authorized, bool foreignGround)
    {
        if (personalOwnerId == actorId || authorized) return false;
        if (personalOwnerId.HasValue) return true;
        if (communalOwner.HasValue)
            return !FactionRelations.AreAllies(actorFaction, communalOwner.Value);
        return foreignGround;
    }

    public static bool IsTheft(WorldState world, NPCState actor, WorldObjectState obj,
        bool authorized = false)
    {
        if (actor is null || obj is null) return false;
        int? owner = obj.Owner is { } id && ClothingOwnership.OwnerAlive(world, id.Value)
            ? id.Value : null;
        return IsUnauthorizedUse(actor.Id.Value, actor.Faction, owner, obj.OwnerFaction,
            authorized, IsForeignPrivateGround(world, actor.Faction, obj.Tile));
    }

    public static bool IsTheft(Debug.WorldSnapshot snapshot, Debug.NpcSnapshot actor,
        Debug.ObjectSnapshot obj, InteractionType interaction = InteractionType.PickUp)
    {
        if (snapshot is null || actor is null || obj is null) return false;
        var foreignGround = false;
        foreach (var home in snapshot.CampHomes)
            if (!FactionRelations.AreAllies(actor.Faction, home.Faction) &&
                HexSpatialMath.HexDistance(obj.Tile, home.Tile) <= PrivateGroundRadiusTiles)
                foreignGround = true;
        return IsUnauthorizedUse(actor.Id.Value, actor.Faction, obj.PropertyOwnerNpcId,
            obj.PropertyOwnerFaction,
            interaction == InteractionType.Dress && actor.WearGrantObjectIds.Contains(obj.Id.Value),
            foreignGround);
    }

    /// <summary>Кража состоялась: событие в хронику + удар по отношению
    /// каждой свидетельницы НЕсоюзной фракции, которая видит воровку.</summary>
    public static void OnStolen(WorldState world, NPCState thief, WorldObjectState obj)
    {
        AgentIncidentNotifications.Theft(world, thief, obj.DefinitionId, source: obj);
        Trace.Emit(world, thief.Id, "ItemStolen",
            $"NPC{thief.Id.Value} Def={obj.DefinitionId} " +
            $"Tile={obj.Tile.Q},{obj.Tile.R}");

        foreach (var witness in world.Entities.Npcs.Values)
        {
            if (witness.Id.Equals(thief.Id) ||
                witness.Health <= 0f ||
                witness.IsUnconscious(world.Tick))
            {
                continue;
            }

            var sees = false;
            foreach (var agent in witness.Perception.Agents)
            {
                if (agent.Id.Equals(thief.Id))
                {
                    sees = true;
                    break;
                }
            }

            if (!sees)
            {
                continue;
            }

            var relation = witness.Social.GetOrCreate(thief.Id);
            relation.Affinity = MathUtil.Clamp(
                relation.Affinity - WitnessAffinityHit, -1f, 1f);
            witness.Social.MarkInteraction(thief.Id, world.Tick);
            Trace.Emit(world, witness.Id, "RelationshipChanged",
                $"NPC{witness.Id.Value}->NPC{thief.Id.Value} " +
                $"Aff={relation.Affinity:F2} (-{WitnessAffinityHit:0.00}) Cause=[Theft]");
        }
    }
}

}
