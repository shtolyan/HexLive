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
    public const int CampfireFuelCapacity = 3;
    public const float FuelTicksPerStick = 1200f;

    // ItemInstance has no free enum field. Wood does not otherwise use its
    // ResourceAmount, so this persisted negative value is an unambiguous §151
    // discriminator between queued fuel and the campfire's construction bill.
    private const float QueuedFuelMarker = -151f;

    /// <summary>Из этого можно доставать и в это можно класть.</summary>
    public static bool IsLootable(WorldState world, WorldObjectState obj)
    {
        if (obj is null)
        {
            return false;
        }

        // Доставленные материалы стройки не становятся лутом только потому,
        // что технически живут в Contents. Живой костёр — узкое исключение:
        // BuildCells показывает у него лишь помеченный топливный запас.
        if (obj.IsCraftProject ||
            (!string.IsNullOrEmpty(obj.BuildProduct) &&
             obj.DefinitionId != ContentIds.Campfire))
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
             definition.HasTag(ObjectTags.Rack) ||
             definition.HasTag(ObjectTags.Wardrobe) ||
             definition.HasTag(ObjectTags.Campfire) ||
             obj.DefinitionId == ContentIds.WaterCollector ||
             definition.InventoryCapacity > 0);
    }

    private static bool IsRack(WorldState world, WorldObjectState obj) =>
        world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Rack);

    private static bool IsWardrobe(WorldState world, WorldObjectState obj) =>
        world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Wardrobe);

    private static bool IsCampfire(WorldState world, WorldObjectState obj) =>
        world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Campfire);

    public static int Capacity(WorldState world, WorldObjectState obj)
    {
        if (IsCampfire(world, obj)) return CampfireFuelCapacity;
        if (obj.DefinitionId == ContentIds.WaterCollector) return 1;
        if (IsWardrobe(world, obj)) return Spec133.WardrobeCapacity;
        if (IsRack(world, obj)) return SimBalance.RackCapacity;
        return 0;
    }

    private static List<WorldObjectState> WardrobeGarments(
        WorldState world, WorldObjectState wardrobe)
    {
        var result = new List<WorldObjectState>();
        if ((!IsRack(world, wardrobe) &&
             wardrobe.DefinitionId != ContentIds.WaterCollector) ||
            wardrobe.Junctions.Count == 0)
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
                !AcceptsExternalObject(wardrobe, definition, candidate.DefinitionId))
            {
                continue;
            }

            result.Add(candidate);
        }

        result.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        return result;
    }

    private static bool AcceptsExternalObject(
        WorldObjectState container, ObjectDefinition definition, string itemId) =>
        container.DefinitionId == ContentIds.WaterCollector
            ? itemId == ContentIds.Bottle
            : definition.Layer is not null;

    private static bool IsStoredItem(
        WorldState world, WorldObjectState obj, ItemInstance item) =>
        !IsCampfire(world, obj) || IsQueuedCampfireFuel(obj, item);

    public static bool IsQueuedCampfireFuel(
        WorldObjectState container, ItemInstance item) =>
        container?.DefinitionId == ContentIds.Campfire &&
        item is not null && item.ResourceAmount == QueuedFuelMarker;

    private static bool IsWood(WorldState world, ItemInstance item) =>
        world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Wood);

    private static float FuelTicks(ItemInstance item) =>
        item.DefinitionId == ContentIds.Log
            ? FuelTicksPerStick * 4f
            : FuelTicksPerStick;

    private static readonly string[] CampfireFuelPreference =
    {
        ContentIds.Stick, ContentIds.Board, ContentIds.Log
    };

    /// <summary>§151.2: the action and the container accept the same Wood set.
    /// Prefer cheap fuel before a four-stick log, but keep content-tagged wood
    /// extensible without another hard-coded interaction branch.</summary>
    public static ItemInstance FindCarriedCampfireFuel(WorldState world, NPCState npc)
    {
        foreach (var preferred in CampfireFuelPreference)
        {
            foreach (var item in npc.Inventory.Items)
            {
                if (item.DefinitionId == preferred && IsWood(world, item))
                {
                    return item;
                }
            }
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (IsWood(world, item)) return item;
        }

        return null;
    }

    public static bool HasCampfireFuelAvailable(
        WorldState world, NPCState npc, WorldObjectState fire) =>
        FindCarriedCampfireFuel(world, npc) is not null ||
        HasQueuedCampfireFuel(world, fire);

    public static bool TryConsumeCarriedCampfireFuel(
        WorldState world, NPCState npc, out float fuelTicks)
    {
        var item = FindCarriedCampfireFuel(world, npc);
        if (item is null)
        {
            fuelTicks = 0f;
            return false;
        }

        npc.Inventory.Items.Remove(item);
        fuelTicks = FuelTicks(item);
        return true;
    }

    /// <summary>§151: consume the oldest queued stack member into active fuel.</summary>
    public static bool TryConsumeCampfireFuel(
        WorldState world, WorldObjectState fire, out float fuelTicks)
    {
        fuelTicks = 0f;
        for (var i = 0; i < fire.Contents.Count; i++)
        {
            var item = fire.Contents[i];
            if (!IsQueuedCampfireFuel(fire, item) || !IsWood(world, item)) continue;
            fire.Contents.RemoveAt(i);
            item.ResourceAmount = 0f;
            fuelTicks = FuelTicks(item);
            return true;
        }

        return false;
    }

    public static bool HasQueuedCampfireFuel(WorldState world, WorldObjectState fire)
    {
        foreach (var item in fire.Contents)
        {
            if (IsQueuedCampfireFuel(fire, item) && IsWood(world, item)) return true;
        }

        return false;
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
        cells.Clear();
        for (var index = 0; index < obj.Contents.Count; index++)
        {
            var item = obj.Contents[index];
            if (!IsStoredItem(world, obj, item)) continue;
            AddCell(cells, item.DefinitionId, index);
        }
        foreach (var garment in WardrobeGarments(world, obj))
        {
            AddCell(cells, garment.DefinitionId, cells.Count);
        }
    }

    private static void AddCell(
        List<(string ItemId, int Count, int SourceIndex)> cells,
        string itemId, int sourceIndex)
    {
        if (InventoryState.IsStackable(itemId))
        {
            for (var cell = 0; cell < cells.Count; cell++)
            {
                if (cells[cell].ItemId != itemId) continue;
                cells[cell] = (itemId, cells[cell].Count + 1, cells[cell].SourceIndex);
                return;
            }
        }

        cells.Add((itemId, 1, sourceIndex));
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
            if (item.DefinitionId != cell.ItemId || !IsStoredItem(world, obj, item))
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
        var isRack = IsRack(world, obj);
        var isCollector = obj.DefinitionId == ContentIds.WaterCollector;
        var isCampfire = IsCampfire(world, obj);
        foreach (var item in moving)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(
                    item.DefinitionId, out var definition) ||
                (isRack && definition.Layer is null) ||
                (isCollector && item.DefinitionId != ContentIds.Bottle) ||
                (isCampfire && !definition.HasTag(ObjectTags.Wood)))
            {
                return false;
            }
        }

        var capacity = Capacity(world, obj);
        if (capacity <= 0) return true;
        var projected = new List<(string ItemId, int Count, int SourceIndex)>();
        BuildCells(world, obj, projected);
        foreach (var item in moving) AddCell(projected, item.DefinitionId, projected.Count);
        return projected.Count <= capacity;
    }

    public static void TakeFromContainer(
        WorldState world, WorldObjectState obj, NPCState looter,
        IReadOnlyList<ItemInstance> moving, IReadOnlyList<ObjectId> groundSources)
    {
        foreach (var item in moving)
        {
            obj.Contents.Remove(item);
            if (IsQueuedCampfireFuel(obj, item)) item.ResourceAmount = 0f;
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
            var externalStorage =
                (IsRack(world, obj) || obj.DefinitionId == ContentIds.WaterCollector) &&
                obj.Junctions.Count > 0;
            if (externalStorage &&
                world.Content.ObjectDefinitions.TryGetValue(
                    item.DefinitionId, out var definition) &&
                AcceptsExternalObject(obj, definition, item.DefinitionId))
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
            else if (IsCampfire(world, obj))
            {
                item.ResourceAmount = QueuedFuelMarker;
                obj.Contents.Add(item);
            }
            else
            {
                obj.Contents.Add(item);
            }
        }
    }
}

}
