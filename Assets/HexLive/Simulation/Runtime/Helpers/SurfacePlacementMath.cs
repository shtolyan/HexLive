using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§176: authored surfaces hold ordinary loose world objects.</summary>
public static class SurfacePlacementMath
{
    private static readonly (Float2 Offset, float Height)[] TableSlots =
    {
        (new Float2(-.18f, -.20f), .7625f),
        (new Float2(.18f, .20f), .7625f),
    };

    private static readonly (Float2 Offset, float Height)[] ShelfSlots =
    {
        (new Float2(.03f, .12f), .22f),
        (new Float2(.03f, -.08f), .58f),
        (new Float2(.03f, .10f), .94f),
        (new Float2(.03f, -.12f), 1.30f),
    };

    public static bool TryGetSlot(string definitionId, byte slot, out Float2 offsetXZ, out float height)
    {
        var slots = definitionId switch
        {
            "furniture.modern_table" => TableSlots,
            "furniture.modern_shelf" => ShelfSlots,
            _ => null,
        };
        if (slots == null || slot >= slots.Length)
        {
            offsetXZ = default;
            height = 0;
            return false;
        }

        (offsetXZ, height) = slots[slot];
        return true;
    }

    public static bool TryPlace(WorldState world, WorldObjectState item, WorldObjectState surface, byte slot)
    {
        if (!world.Entities.Objects.TryGetValue(item.Id, out var activeItem) || activeItem != item ||
            !world.Entities.Objects.TryGetValue(surface.Id, out var activeSurface) || activeSurface != surface ||
            !ValidLink(world, item, surface, slot))
            return false;

        if (world.Caches.ObjectsByTile.TryGetValue(surface.Tile, out var objects))
            foreach (var id in objects)
                if (id != item.Id && world.Entities.Objects.TryGetValue(id, out var other) &&
                    other.SurfaceObjectId == surface.Id && other.SurfaceSlot == slot)
                    return false;

        item.SurfaceObjectId = surface.Id;
        item.SurfaceSlot = slot;
        return true;
    }

    // Dropping or removing the surface leaves its objects at their ground anchors.
    internal static void Detach(WorldState world, WorldObjectState obj)
    {
        Clear(obj);
        if (!TryGetSlot(obj.DefinitionId, 0, out _, out _) ||
            !world.Caches.ObjectsByTile.TryGetValue(obj.Tile, out var objects))
            return;

        foreach (var id in objects)
            if (world.Entities.Objects.TryGetValue(id, out var item) && item.SurfaceObjectId == obj.Id)
                Clear(item);
    }

    internal static void RepairLoadedLinks(WorldState world)
    {
        var occupied = new HashSet<(ObjectId, byte)>();
        foreach (var item in world.Entities.Objects.Values)
            if (item.SurfaceObjectId is { } id &&
                (!world.Entities.Objects.TryGetValue(id, out var surface) ||
                 !ValidLink(world, item, surface, item.SurfaceSlot) ||
                 !occupied.Add((id, item.SurfaceSlot))))
                Clear(item);
    }

    private static bool ValidLink(WorldState world, WorldObjectState item, WorldObjectState surface, byte slot) =>
        item.Id != surface.Id && item.Tile == surface.Tile && item.Fragment == surface.Fragment &&
        !surface.SurfaceObjectId.HasValue && TryGetSlot(surface.DefinitionId, slot, out _, out _) &&
        world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
        !definition.HasTag("Furniture") && definition.Interactions.Exists(interaction => interaction.Type == InteractionType.PickUp);

    private static void Clear(WorldObjectState item)
    {
        item.SurfaceObjectId = null;
        item.SurfaceSlot = 0;
    }
}

}
