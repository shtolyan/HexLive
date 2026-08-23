using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §128.5: обыск ВЕЩИ, а не человека — истлевшего тела и лежащей вещи с
/// карманами.
/// <para>
/// Зачем: после §28.15C тело через двое суток становится одним лёгким
/// <c>remains.human</c>, и всё, что было при покойной, переезжает в его
/// <c>Contents</c>. Достать это было НЕЧЕМ: вещи умерших пропадали навсегда.
/// Та же дыра у снятого рюкзака — карманы уезжают в его Contents вместе с ним.
/// </para>
/// <para>
/// Модель нарочно простая и не повторяет человеческую раскладку (руки, слои,
/// кобура): у мешка нет тела, и городить ему анатомию значило бы врать. Ячейка
/// — это стопка одинаковых вещей, а <c>SourceIndex</c> — индекс её ПЕРВОГО
/// экземпляра в <c>Contents</c>, ровно как на человеческой стороне.
/// </para>
/// </summary>
internal static class ContainerLootMath
{
    /// <summary>Из этого можно доставать и в это можно класть.</summary>
    public static bool IsLootable(WorldState world, WorldObjectState obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (obj.Contents.Count > 0)
        {
            return true;
        }

        // Пустой мешок — всё ещё мешок: в него кладут. А пустой камень — нет.
        // Мера — карманы вещи (InventoryCapacity) и явные теги: рюкзак с
        // нулевыми карманами контейнером не считается, и это правильно.
        return world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
            (definition.HasTag("Remains") ||
             definition.HasTag("Container") ||
             definition.HasTag(ObjectTags.Wardrobe) ||
             definition.InventoryCapacity > 0);
    }

    private static bool IsWardrobe(WorldState world, WorldObjectState obj) =>
        world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Wardrobe);

    private static List<WorldObjectState> WardrobeGarments(
        WorldState world, WorldObjectState wardrobe)
    {
        var result = new List<WorldObjectState>();
        if (!IsWardrobe(world, wardrobe) || wardrobe.Junctions.Count == 0)
        {
            return result;
        }

        var anchor = wardrobe.Junctions[0];
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (candidate.Id.Equals(wardrobe.Id) || candidate.Junctions.Count == 0 ||
                !candidate.Junctions[0].Equals(anchor) || candidate.IsCraftProject ||
                !string.IsNullOrEmpty(candidate.BuildProduct) ||
                !world.Content.ObjectDefinitions.TryGetValue(
                    candidate.DefinitionId, out var definition) ||
                definition.Layer is null)
            {
                continue;
            }

            result.Add(candidate);
        }

        result.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        return result;
    }

    /// <summary>
    /// Ячейки содержимого: стопка на каждый набор одинаковых id, в порядке
    /// первого появления. Порядок детерминирован — панель и приказ обязаны
    /// видеть одну и ту же нумерацию.
    /// </summary>
    public static void BuildCells(
        WorldObjectState obj, List<(string ItemId, int Count, int SourceIndex)> cells)
    {
        cells.Clear();
        for (var index = 0; index < obj.Contents.Count; index++)
        {
            var item = obj.Contents[index];
            var merged = false;
            if (InventoryState.IsStackable(item.DefinitionId))
            {
                for (var cell = 0; cell < cells.Count; cell++)
                {
                    if (cells[cell].ItemId != item.DefinitionId)
                    {
                        continue;
                    }

                    cells[cell] = (cells[cell].ItemId, cells[cell].Count + 1, cells[cell].SourceIndex);
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                cells.Add((item.DefinitionId, 1, index));
            }
        }
    }

    public static void BuildCells(
        WorldState world, WorldObjectState obj,
        List<(string ItemId, int Count, int SourceIndex)> cells)
    {
        BuildCells(obj, cells);
        foreach (var garment in WardrobeGarments(world, obj))
        {
            var merged = false;
            if (InventoryState.IsStackable(garment.DefinitionId))
            {
                for (var cell = 0; cell < cells.Count; cell++)
                {
                    if (cells[cell].ItemId != garment.DefinitionId) continue;
                    cells[cell] = (cells[cell].ItemId, cells[cell].Count + 1,
                        cells[cell].SourceIndex);
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                cells.Add((garment.DefinitionId, 1, cells.Count));
            }
        }
    }

    /// <summary>
    /// Разрешить ячейку в конкретные экземпляры. Ожидаемый id проверяется: панель
    /// рисует прошлый тик, и к моменту приказа содержимое могло измениться.
    /// </summary>
    public static bool TryResolve(
        WorldState world, WorldObjectState obj, int slotIndex,
        string expectedDefinitionId, int requestedCount,
        out List<ItemInstance> items, out List<ObjectId> groundSources)
    {
        items = new List<ItemInstance>();
        groundSources = new List<ObjectId>();
        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        BuildCells(world, obj, cells);
        if (slotIndex < 0 || slotIndex >= cells.Count)
        {
            return false;
        }

        var cell = cells[slotIndex];
        if (!string.IsNullOrEmpty(expectedDefinitionId) && cell.ItemId != expectedDefinitionId)
        {
            return false;
        }

        var take = requestedCount <= 0 ? 1 : System.Math.Min(requestedCount, cell.Count);
        foreach (var item in obj.Contents)
        {
            if (item.DefinitionId != cell.ItemId)
            {
                continue;
            }

            items.Add(item);
            if (items.Count == take)
            {
                break;
            }
        }

        var topLevelCount = items.Count;
        if (topLevelCount < take)
        {
            foreach (var garment in WardrobeGarments(world, obj))
            {
                if (garment.DefinitionId != cell.ItemId) continue;
                items.Add(ToItem(garment));
                topLevelCount++;
                items.AddRange(garment.Contents);
                groundSources.Add(garment.Id);
                if (topLevelCount == take)
                {
                    break;
                }
            }
        }

        return topLevelCount == take && items.Count > 0;
    }

    private static ItemInstance ToItem(WorldObjectState source) => new(source.DefinitionId)
    {
        Wetness = source.Wetness,
        Durability = source.Durability,
        Dirtiness = source.Dirtiness,
        Bloodiness = source.Bloodiness,
        ResourceAmount = source.ResourceAmount,
        OwnerId = source.Owner?.Value ?? 0
    };

    /// <summary>Влезет ли забранное в карманы обыскивающей.</summary>
    public static bool FitsInLooter(
        WorldState world, NPCState looter, IReadOnlyList<ItemInstance> moving)
    {
        var carried = new List<ItemInstance>(looter.Inventory.Items);
        carried.AddRange(moving);
        return PlayerInventoryMath.FitsProjected(
            world, looter, carried, new List<ItemInstance>(looter.WornItems));
    }

    public static bool CanAccept(
        WorldState world, WorldObjectState obj, IReadOnlyList<ItemInstance> moving)
    {
        if (!IsWardrobe(world, obj)) return true;
        var addedGarments = 0;
        foreach (var item in moving)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(
                    item.DefinitionId, out var definition) && definition.Layer is not null)
                addedGarments++;
        }
        return WardrobeGarments(world, obj).Count + addedGarments <= Spec133.WardrobeCapacity;
    }

    public static void TakeFromContainer(
        WorldState world, WorldObjectState obj, NPCState looter,
        IReadOnlyList<ItemInstance> moving, IReadOnlyList<ObjectId> groundSources)
    {
        foreach (var item in moving)
        {
            obj.Contents.Remove(item);
            looter.Inventory.Items.Add(item);
        }

        foreach (var id in groundSources)
        {
            WorldObjectMutations.DespawnObject(world, id);
        }
    }

    public static void GiveToContainer(
        WorldState world, WorldObjectState obj, NPCState looter,
        IReadOnlyList<ItemInstance> moving)
    {
        foreach (var item in moving)
        {
            looter.Inventory.Items.Remove(item);
            if (IsWardrobe(world, obj) && obj.Junctions.Count > 0 &&
                world.Content.ObjectDefinitions.TryGetValue(
                    item.DefinitionId, out var definition) && definition.Layer is not null)
            {
                var stored = WorldObjectMutations.SpawnObject(
                    world, item.DefinitionId, obj.Fragment, obj.Tile, obj.Junctions[0]);
                stored.RotationDegrees = obj.RotationDegrees;
                stored.Wetness = item.Wetness;
                stored.Durability = item.Durability;
                stored.Dirtiness = item.Dirtiness;
                stored.Bloodiness = item.Bloodiness;
                stored.ResourceAmount = item.ResourceAmount;
                stored.Owner = item.OwnerId != 0 ? new EntityId(item.OwnerId) : looter.Id;
            }
            else
            {
                obj.Contents.Add(item);
            }
        }
    }
}

}
