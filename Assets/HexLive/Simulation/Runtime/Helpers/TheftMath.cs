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
/// Радиус меряется от домашнего якоря лагеря (WorldState.FactionHomes — он и
/// есть костёр лагеря, §146.14). Своё и союзное забрать — не кража: владелец
/// вещи из союзной фракции снимает подозрение раньше геометрии.
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

    public static bool IsTheft(WorldState world, NPCState actor, WorldObjectState obj)
    {
        if (actor is null || obj is null)
        {
            return false;
        }

        // Владелец вещи — союзник? Забрать своё из любого места — не кража.
        if (obj.Owner is { } ownerId &&
            world.Entities.Npcs.TryGetValue(ownerId, out var owner) &&
            FactionRelations.AreAllies(actor.Faction, owner.Faction))
        {
            return false;
        }

        return IsForeignPrivateGround(world, actor.Faction, obj.Tile);
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
                FactionRelations.AreAllies(witness.Faction, thief.Faction) ||
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
