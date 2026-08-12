using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §133: одежда персонализирована. Владелец живёт в двух зеркальных полях —
/// <see cref="WorldObjectState.Owner"/> у вещи на земле и
/// <see cref="ItemInstance.OwnerId"/> у надетой/носимой, — и этот класс
/// единственное место, где решается, кто им становится.
///
/// Правила, по которым вещь меняет хозяйку:
/// - ничейная вещь (сюрф §63, вещь покойной) достаётся тому, кто надел;
/// - вещь ЧУЖОЙ фракции переходит взявшей — это и есть лут (игрок: «лутать и
///   одевать одежду чужаков можно, но назначается новый овнер, тот, кто взял»);
/// - вещь своей колонистки владельца НЕ меняет: одалживание остаётся
///   одалживанием, и в следующий раз разрешение спрашивают снова.
/// </summary>
public static class ClothingOwnership
{
    /// <summary>Владелец вещи как id (0 = ничья).</summary>
    public static int OwnerIdOf(WorldObjectState obj) =>
        obj?.Owner is { } owner ? owner.Value : 0;

    /// <summary>Жива ли владелица (мёртвая владелица = вещь ничейная).</summary>
    public static bool OwnerAlive(WorldState world, int ownerId) =>
        ownerId != 0 &&
        world.Entities.Npcs.TryGetValue(new EntityId(ownerId), out var owner) &&
        owner.Health > 0f;

    /// <summary>
    /// Чья это вещь с точки зрения <paramref name="npc"/>: возвращает живую
    /// ДРУГУЮ колонистку-владелицу (ту, у кого надо спрашивать разрешение), или
    /// null — если вещь своя, ничейная, покойной или чужачки.
    /// </summary>
    public static NPCState FellowOwner(WorldState world, NPCState npc, WorldObjectState obj)
    {
        var ownerId = OwnerIdOf(obj);
        if (ownerId == 0 || ownerId == npc.Id.Value ||
            !world.Entities.Npcs.TryGetValue(new EntityId(ownerId), out var owner) ||
            owner.Health <= 0f ||
            owner.Faction != npc.Faction)
        {
            return null;
        }

        return owner;
    }

    /// <summary>
    /// Кому принадлежит вещь ПОСЛЕ того, как её надели/подняли. Своё и чужое
    /// внутри колонии владельца не меняют; ничейное и трофейное — меняют.
    /// </summary>
    public static int ResolveOnTake(WorldState world, NPCState taker, WorldObjectState obj)
    {
        var ownerId = OwnerIdOf(obj);
        if (ownerId == taker.Id.Value)
        {
            return ownerId;
        }

        if (!world.Entities.Npcs.TryGetValue(new EntityId(ownerId), out var owner) ||
            owner.Health <= 0f)
        {
            return taker.Id.Value; // ничейное или вещь покойной — теперь её
        }

        return owner.Faction == taker.Faction
            ? ownerId          // одолжила у подруги — хозяйка прежняя
            : taker.Id.Value;  // трофей с чужака — новый владелец
    }

    /// <summary>
    /// §64-идиома, перенесённая на одежду: со смертью колонистки её вещи на
    /// земле становятся ничейными, иначе разрешение спрашивать было бы не у
    /// кого и одежда осела бы мёртвым грузом.
    /// </summary>
    public static void ReleaseGroundItemsOf(WorldState world, EntityId dead)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Owner == dead &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.Layer != null)
            {
                obj.Owner = null;
            }
        }
    }
}

}
