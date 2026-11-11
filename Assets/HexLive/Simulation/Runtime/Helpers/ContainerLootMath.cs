using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
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
    public const int CampfireFuelCapacity = 20;

    // §151.3: к двадцати топливным ячейкам добавляются ЖАРОЧНЫЕ. Их
    // ровно две, а не шесть: мясо стопкуется, поэтому шесть крюков вертела
    // (SimBalance.CampfireSpitCapacity) показываются двумя стопками — сырой и
    // готовой. Настоящий предел крюков стережёт CanAccept, а не число ячеек.
    public const int CampfireSpitCells = 2;

    public const float FuelTicksPerStick = 1200f;

    // Read compatibility for v66-v87 wood buffers. New entries use the explicit
    // CampfireFuelRole and never overwrite amounts, age, or liquid metadata.
    private const float QueuedFuelMarker = -151f;
    private static bool IsDiscard(ItemInstance item) => item.CampfireFuelRole == CampfireFuelRole.Discard;

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

        if (BuildingRules.IsCompletedBuilding(obj) && obj.InstalledBuildMaterials != null)
        {
            System.Span<int> seen = stackalloc int[BuildSiteMath.AllMaterials.Length];
            seen.Clear();
            foreach (var item in obj.Contents)
            {
                var material = System.Array.IndexOf(BuildSiteMath.AllMaterials, item.DefinitionId);
                if (material < 0 || ++seen[material] > obj.InstalledBuildMaterials[material]) return true;
            }
            return false;
        }

        if (obj.DefinitionId == ContentIds.ModernFridge ||
            obj.Contents.Exists(item => IsStoredItem(world, obj, item)))
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
        (definition.HasTag(ObjectTags.Rack) || definition.HasTag(ObjectTags.Wardrobe));

    private static bool IsWardrobe(WorldState world, WorldObjectState obj) =>
        world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Wardrobe);

    private static bool IsCampfire(WorldState world, WorldObjectState obj) =>
        world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Campfire);

    public static int Capacity(WorldState world, WorldObjectState obj)
    {
        // §151.3: жарочные ячейки появляются только вместе с ВЕРТЕЛОМ — на
        // голом костре вешать мясо не на что, и рисовать пустые крюки значило
        // бы обещать игроку действие, которое симуляция отклонит.
        if (IsCampfire(world, obj))
        {
            return CampfireFuelCapacity +
                (BuildSiteMath.CampfireSpitComplete(obj) ? CampfireSpitCells : 0);
        }

        if (obj.DefinitionId == ContentIds.WaterCollector) return 1;
        if (obj.DefinitionId == ContentIds.ModernFridge) return ModernFoodCatalog.FridgeCapacity;
        if (obj.DefinitionId == ContentIds.StorageChest) return StorageChest.Capacity;
        if (IsWardrobe(world, obj)) return Spec133.WardrobeCapacity;
        if (IsRack(world, obj)) return SimBalance.RackCapacity;
        if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
            definition.Layer == WearLayer.Bags) return definition.InventoryCapacity;
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
        Collect(wardrobe.Tile);
        foreach (var direction in HexDirection.All)
            Collect(new TileCoord(wardrobe.Tile.Q + direction.DQ, wardrobe.Tile.R + direction.DR));
        result.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        return result;

        void Collect(TileCoord tile)
        {
            if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var ids)) return;
            foreach (var id in ids)
            {
                if (id.Equals(wardrobe.Id) || !world.Entities.Objects.TryGetValue(id, out var candidate) ||
                    !candidate.Fragment.Equals(wardrobe.Fragment) || candidate.Junctions.Count == 0 ||
                    !candidate.Junctions[0].Equals(anchor) || candidate.IsCraftProject ||
                    !string.IsNullOrEmpty(candidate.BuildProduct) ||
                    !world.Content.ObjectDefinitions.TryGetValue(candidate.DefinitionId, out var definition) ||
                    !AcceptsExternalObject(wardrobe, definition, candidate.DefinitionId)) continue;
                result.Add(candidate);
            }
        }
    }

    internal static void ObserveStorage(WorldState world, NPCState npc, PerceivedObject perceived)
    {
        if (perceived.FromMemory || !world.Entities.Objects.TryGetValue(perceived.Id, out var root) ||
            !root.Fragment.Equals(npc.Fragment)) return;
        var hasHangers = IsRack(world, root);
        if ((!hasHangers && root.Contents.Count == 0) || !IsLootable(world, root) || IsCampfire(world, root)) return;

        var accessible = CampPropertyMath.CanUseUnordered(world, npc, root);
        var children = accessible ? ObserveItems(world, npc, root.Contents, root) : null;
        if (hasHangers)
        {
            foreach (var hanging in WardrobeGarments(world, root))
            {
                if (!npc.Perception.LiveObjectPool.TryGetValue(hanging.Id, out var seen) ||
                    !npc.Perception.Objects.Contains(seen)) continue;
                seen.StorageParentId = root.Id;
                if (!accessible || !CampPropertyMath.CanUseUnordered(world, npc, hanging)) continue;
                var node = new PerceivedStoredItem(hanging.DefinitionId, hanging.Owner?.Value ?? 0,
                    hanging.OwnerFaction, null, hanging.Id, hanging.ResourceAmount, hanging.WaterKind)
                { Contents = ObserveItems(world, npc, hanging.Contents, hanging) };
                (children ??= new()).Add(node);
            }
        }
        perceived.StoredItems = children;
    }

    private static List<PerceivedStoredItem>? ObserveItems(WorldState world, NPCState npc,
        List<ItemInstance> items, WorldObjectState? root = null, int depth = 0)
    {
        List<PerceivedStoredItem>? children = null;
        foreach (var item in items)
        {
            if ((root != null && !IsStoredItem(world, root, item)) ||
                !CampPropertyMath.CanUseStoredItem(world, npc, item.OwnerId)) continue;
            var node = new PerceivedStoredItem(item.DefinitionId, item.OwnerId, null, item, null,
                item.ResourceAmount, item.WaterKind);
            if (depth < ItemInstance.MaxStorageDepth && item.Contents is { Count: > 0 } nested)
                node.Contents = ObserveItems(world, npc, nested, depth: depth + 1);
            (children ??= new()).Add(node);
        }
        return children;
    }

    internal static bool TryTakeObservedItem(WorldState world, NPCState npc,
        WorldObjectState root, PerceivedStoredItem[] path)
    {
        if (path.Length == 0 || !IsLootable(world, root) || IsCampfire(world, root) ||
            !root.Fragment.Equals(npc.Fragment) || (root.IsOccupied && root.CurrentUser != npc.Id) ||
            !CampPropertyMath.CanUseUnordered(world, npc, root)) return false;

        var physicalContainer = root;
        List<ItemInstance>? contents = root.Contents;
        for (var index = 0; index < path.Length; index++)
        {
            var node = path[index];
            if (index == 0 && node.WorldObjectId is { } hangerId && index < path.Length - 1)
            {
                if (!WardrobeGarments(world, root).Exists(item => item.Id.Equals(hangerId)) ||
                    !world.Entities.Objects.TryGetValue(hangerId, out var hanging) ||
                    (hanging.IsOccupied && hanging.CurrentUser != npc.Id) ||
                    !CampPropertyMath.CanUseUnordered(world, npc, hanging)) return false;
                physicalContainer = hanging;
                contents = hanging.Contents;
                continue;
            }

            if (contents is null || node.Instance is not { } item ||
                InventoryMath.IndexOfReference(contents, item) < 0 ||
                (ReferenceEquals(contents, physicalContainer.Contents) && !IsStoredItem(world, physicalContainer, item)) ||
                !CampPropertyMath.CanUseStoredItem(world, npc, item.OwnerId)) return false;
            if (index == path.Length - 1)
            {
                var moving = new[] { item };
                if (!FitsInLooter(world, npc, moving) ||
                    (npc.Plan.Goal is GoalType.GetWater or GoalType.Drink &&
                     !IsStoredWaterSource(npc, item.DefinitionId, item.ResourceAmount, item.WaterKind))) return false;
                var theft = TheftMath.IsTakingTheft(world, npc, root, moving) ||
                    (!ReferenceEquals(root, physicalContainer) && TheftMath.IsTheft(world, npc, physicalContainer));
                InventoryMath.RemoveReference(contents, item);
                npc.Inventory.Items.Add(item);
                if (theft) TheftMath.OnStolen(world, npc, root);
                return true;
            }
            contents = item.Contents;
        }
        return false;
    }

    internal static bool IsStoredWaterSource(NPCState npc, string id, float amount, WaterKind waterKind) =>
        id == ContentIds.Coconut ? DecisionSystem.HasCoconutBlade(npc) :
        id == ContentIds.CoconutPierced ? amount > 0f :
        WaterVesselCatalog.IsPortable(id) && BottleInventoryMath.Charges(amount, waterKind, id) > 0;

    private static bool AcceptsExternalObject(
        WorldObjectState container, ObjectDefinition definition, string itemId) =>
        container.DefinitionId == ContentIds.WaterCollector
            ? WaterVesselCatalog.IsPortable(itemId)
            : definition.Layer is not null;

    private static bool IsStoredItem(
        WorldState world, WorldObjectState obj, ItemInstance item) =>
        !IsInstalledHouseMaterial(obj, item) &&
        (!IsCampfire(world, obj) || IsQueuedCampfireFuel(obj, item) || IsSpitMeat(item));

    private static bool IsInstalledHouseMaterial(WorldObjectState obj, ItemInstance item)
    {
        if (!BuildingRules.IsCompletedBuilding(obj) || obj.InstalledBuildMaterials == null) return false;
        var material = System.Array.IndexOf(BuildSiteMath.AllMaterials, item.DefinitionId);
        if (material < 0 || obj.InstalledBuildMaterials[material] <= 0) return false;
        var ordinal = 0;
        foreach (var candidate in obj.Contents)
        {
            if (candidate.DefinitionId != item.DefinitionId) continue;
            ordinal++;
            if (ReferenceEquals(candidate, item)) return ordinal <= obj.InstalledBuildMaterials[material];
        }
        return false;
    }

    /// <summary>§151.3 (bug #293): мясо на вертеле — тоже содержимое станции.
    /// Оно лежит в том же persisted-списке, что топливо и доставленный
    /// материал, но принадлежит вертелу: окно обыска показывает его ячейкой,
    /// откуда готовый кусок можно снять, а сырой — повесить.</summary>
    public static bool IsSpitMeat(ItemInstance item) =>
        item is not null && !IsDiscard(item) &&
        (HumanMeatCatalog.IsRaw(item.DefinitionId) ||
         HumanMeatCatalog.IsCooked(item.DefinitionId));

    public static bool IsQueuedCampfireFuel(
        WorldObjectState container, ItemInstance item) =>
        container?.DefinitionId == ContentIds.Campfire &&
        item is not null && (item.CampfireFuelRole != CampfireFuelRole.None ||
            item.ResourceAmount == QueuedFuelMarker);

    private static bool IsFuel(WorldState world, ItemInstance item) =>
        LeafDryingMath.IsDry(world,item) ||
        world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
        definition.HasTag(ObjectTags.Wood);

    private static float FuelTicks(ItemInstance item) =>
        IsDiscard(item) ? FuelTicksPerStick : item.DefinitionId == ContentIds.PalmLeaf ? FuelTicksPerStick * .25f : item.DefinitionId == ContentIds.Log
            ? FuelTicksPerStick * 4f
            : FuelTicksPerStick;

    private static readonly string[] CampfireFuelPreference =
    {
        ContentIds.PalmLeaf, ContentIds.Stick, ContentIds.Board, ContentIds.Log
    };

    /// <summary>§151.2: the action and container accept wood or fully dry leaves.
    /// Prefer cheap fuel before a four-stick log, but keep content-tagged wood
    /// extensible without another hard-coded interaction branch.</summary>
    public static ItemInstance FindCarriedCampfireFuel(WorldState world, NPCState npc)
    {
        foreach (var preferred in CampfireFuelPreference)
        {
            foreach (var item in npc.Inventory.Items)
            {
                if (item.DefinitionId == preferred && IsFuel(world, item))
                {
                    return item;
                }
            }
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (IsFuel(world, item)) return item;
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

        InventoryMath.RemoveReference(npc.Inventory.Items, item);
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
            if (!IsQueuedCampfireFuel(fire, item)) continue;
            fire.Contents.RemoveAt(i);
            fuelTicks = FuelTicks(item);
            return true;
        }

        return false;
    }

    public static bool HasQueuedCampfireFuel(WorldState world, WorldObjectState fire)
    {
        foreach (var item in fire.Contents)
        {
            if (IsQueuedCampfireFuel(fire, item)) return true;
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
            if (IsInstalledHouseMaterial(obj, item)) continue;
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
            AddStoredCell(obj, cells, item, index);
        }
        foreach (var garment in WardrobeGarments(world, obj))
        {
            AddCell(cells, garment.DefinitionId, cells.Count);
        }
    }

    private static void AddCell(
        List<(string ItemId, int Count, int SourceIndex)> cells,
        string itemId, int sourceIndex, bool forceStack = false)
    {
        if (forceStack || InventoryState.IsStackable(itemId))
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

    // §151.2 / #668: every queued definition gets one display cell, including
    // normally non-stackable tools and vessels. Physical instances remain in
    // Contents in insertion order, with all durability/liquid/pocket data intact.
    private static void AddStoredCell(WorldObjectState obj,
        List<(string ItemId, int Count, int SourceIndex)> cells,
        ItemInstance item, int sourceIndex)
    {
        var queued = IsQueuedCampfireFuel(obj, item);
        if (queued || InventoryState.IsStackable(item.DefinitionId))
        {
            for (var cell = 0; cell < cells.Count; cell++)
            {
                if (cells[cell].ItemId != item.DefinitionId ||
                    IsQueuedCampfireFuel(obj, obj.Contents[cells[cell].SourceIndex]) != queued) continue;
                cells[cell] = (item.DefinitionId, cells[cell].Count + 1, cells[cell].SourceIndex);
                return;
            }
        }
        cells.Add((item.DefinitionId, 1, sourceIndex));
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
        // Build the persisted-content prefix separately. Non-stackable rows
        // (notably bottles with different water) each retain their own exact
        // SourceIndex; resolving merely by definition would always take the
        // first bottle no matter which row the player clicked.
        var contentCells = new List<(string ItemId, int Count, int SourceIndex)>();
        for (var index = 0; index < obj.Contents.Count; index++)
        {
            var item = obj.Contents[index];
            if (!IsStoredItem(world, obj, item)) continue;
            AddStoredCell(obj, contentCells, item, index);
        }

        if (slotIndex < contentCells.Count)
        {
            var contentCell = contentCells[slotIndex];
            if (contentCell.ItemId != cell.ItemId ||
                contentCell.SourceIndex < 0 ||
                contentCell.SourceIndex >= obj.Contents.Count)
            {
                return false;
            }

            var queued = IsQueuedCampfireFuel(obj, obj.Contents[contentCell.SourceIndex]);
            if (!queued && !InventoryState.IsStackable(cell.ItemId))
            {
                var selected = obj.Contents[contentCell.SourceIndex];
                if (!IsStoredItem(world, obj, selected) ||
                    selected.DefinitionId != cell.ItemId)
                {
                    return false;
                }

                items.Add(selected);
            }
            else
            {
                for (var index = contentCell.SourceIndex;
                     index < obj.Contents.Count && items.Count < take;
                     index++)
                {
                    var item = obj.Contents[index];
                    if (item.DefinitionId == cell.ItemId &&
                        IsStoredItem(world, obj, item) &&
                        IsQueuedCampfireFuel(obj, item) == queued)
                    {
                        items.Add(item);
                    }
                }
            }

            return items.Count == take;
        }

        // Rack/collector storage is represented by physical world objects at
        // the shared anchor. Their accepted types (garments and bottle) are
        // non-stackable, so every remaining UI row maps 1:1 to this sorted
        // list. Materialize that exact object and its owned bundle.
        var externalIndex = slotIndex - contentCells.Count;
        var external = WardrobeGarments(world, obj);
        if (externalIndex < 0 || externalIndex >= external.Count)
        {
            return false;
        }

        var storedObject = external[externalIndex];
        if (storedObject.DefinitionId != cell.ItemId || take != 1)
        {
            return false;
        }

        AddExternalItems(world, obj, storedObject, items);
        groundSources.Add(storedObject.Id);
        return true;
    }

    /// <summary>
    /// Resolve a delayed container action from the physical source reserved at
    /// command admission. This deliberately ignores the old row number: rows
    /// can shift while the actor walks, but an equal bottle must never stand in
    /// for the one the player selected.
    /// </summary>
    public static bool TryResolveReserved(
        WorldState world, WorldObjectState obj, string expectedDefinitionId,
        int requestedCount, ItemInstance selectedItem, ObjectId? selectedWorldObject,
        out List<ItemInstance> items, out List<ObjectId> groundSources)
    {
        items = new List<ItemInstance>();
        groundSources = new List<ObjectId>();

        if (selectedWorldObject is { } groundId)
        {
            WorldObjectState storedObject = null;
            foreach (var candidate in WardrobeGarments(world, obj))
            {
                if (candidate.Id.Equals(groundId))
                {
                    storedObject = candidate;
                    break;
                }
            }

            if (storedObject is null ||
                storedObject.DefinitionId != expectedDefinitionId ||
                requestedCount > 1)
            {
                return false;
            }

            AddExternalItems(world, obj, storedObject, items);
            groundSources.Add(storedObject.Id);
            return true;
        }

        if (selectedItem is null || selectedItem.DefinitionId != expectedDefinitionId)
        {
            return false;
        }

        var selectedIndex = InventoryMath.IndexOfReference(obj.Contents, selectedItem);
        if (selectedIndex < 0 || !IsStoredItem(world, obj, selectedItem))
        {
            return false;
        }

        var queued = IsQueuedCampfireFuel(obj, selectedItem);
        var take = (queued || InventoryState.IsStackable(expectedDefinitionId))
            ? System.Math.Max(1, requestedCount)
            : 1;
        for (var index = selectedIndex;
             index < obj.Contents.Count && items.Count < take;
             index++)
        {
            var item = obj.Contents[index];
            if (item.DefinitionId == expectedDefinitionId &&
                IsStoredItem(world, obj, item) &&
                IsQueuedCampfireFuel(obj, item) == queued)
            {
                items.Add(item);
            }
        }

        return items.Count == take && ReferenceEquals(items[0], selectedItem);
    }

    private static void AddExternalItems(WorldState world, WorldObjectState container,
        WorldObjectState source, List<ItemInstance> items)
    {
        var item = ToItem(container, source);
        items.Add(item);
        if (source.Contents.Count == 0) return;
        if (PreservesContentsOnPickup(world, source))
            item.GetOrCreateContents().AddRange(source.Contents);
        else
            items.AddRange(source.Contents);
    }

    internal static bool PreservesContentsOnPickup(WorldState world, WorldObjectState source) =>
        world.Content.ObjectDefinitions.TryGetValue(source.DefinitionId, out var definition) &&
        definition.Layer == WearLayer.Bags;

    private static ItemInstance ToItem(
        WorldObjectState container, WorldObjectState source) => new(source.DefinitionId)
    {
        Wetness = source.Wetness,
        Durability = source.Durability,
        Dirtiness = source.Dirtiness,
        Bloodiness = source.Bloodiness,
        // A parked collector bottle predates physical bottle instances and
        // stores a 0..1 fill fraction in the world object. Inventory bottles
        // store real drink charges (0..BottleCapacity), so the loot boundary
        // must use the same conversion as TakeVessel.
        ResourceAmount = container.DefinitionId == ContentIds.WaterCollector
            ? WaterCollectorMath.AmountIn(source)
            : source.ResourceAmount,
        WaterKind = container.DefinitionId == ContentIds.WaterCollector &&
                    source.WaterKind == WaterKind.None &&
                    WaterCollectorMath.AmountIn(source) > 0f
            ? WaterKind.Rain
            : source.WaterKind,
        LastAddedWaterKind = source.LastAddedWaterKind,
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
        if (IsCampfire(world, obj)) return CampfireAccepts(world, obj, moving);

        var isRack = IsRack(world, obj);
        var isCollector = obj.DefinitionId == ContentIds.WaterCollector;
        var isFridge = obj.DefinitionId == ContentIds.ModernFridge;
        foreach (var item in moving)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(
                    item.DefinitionId, out var definition) ||
                !FitsStorageDepth(item, PreservesContentsOnPickup(world, obj) ? 1 : 0) ||
                (isRack && definition.Layer is null) ||
                (isFridge && !definition.HasTag("Food")) ||
                (isCollector && !WaterVesselCatalog.IsPortable(item.DefinitionId)))
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

    private static bool FitsStorageDepth(ItemInstance item, int depth)
    {
        if (item.Contents is not { Count: > 0 } contents) return true;
        if (depth >= ItemInstance.MaxStorageDepth) return false;
        foreach (var child in contents)
            if (!FitsStorageDepth(child, depth + 1)) return false;
        return true;
    }

    /// <summary>
    /// §151.3 (bug #293): у костра ДВА независимых предела — очередь топлива и
    /// крюки вертела. Считать их одной вместимостью нельзя: запас дров не имеет
    /// права занять место мяса, а мясо — место дров. Правило про вертел здесь
    /// то же, что у автономной готовки (§54.14): без готового вертела вешать
    /// не на что, а крюков ровно <c>CampfireSpitCapacity</c>.
    /// </summary>
    private static bool CampfireAccepts(
        WorldState world, WorldObjectState fire, IReadOnlyList<ItemInstance> moving)
    {
        var fuelCells = new List<(string ItemId, int Count, int SourceIndex)>();
        foreach (var item in fire.Contents)
        {
            if (IsQueuedCampfireFuel(fire, item))
            {
                AddCell(fuelCells, item.DefinitionId, fuelCells.Count, forceStack: true);
            }
        }

        var hooksUsed = BuildSiteMath.HangingMeat(fire, ContentIds.MeatRaw) +
                        BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked);
        var spitReady = BuildSiteMath.CampfireSpitComplete(fire);
        foreach (var item in moving)
        {
            if (item.Contents is { Count: > 0 }) return false;
            // Готовое мясо обратно на вертел не вешают: оно уже пожарено, и
            // второй круг над огнём для него ничего не значит.
            if (HumanMeatCatalog.IsRaw(item.DefinitionId))
            {
                hooksUsed++;
                if (!spitReady || hooksUsed > SimBalance.CampfireSpitCapacity)
                {
                    return false;
                }

                continue;
            }

            if (!world.Content.ObjectDefinitions.ContainsKey(item.DefinitionId)) return false;

            AddCell(fuelCells, item.DefinitionId, fuelCells.Count, forceStack: true);
            if (fuelCells.Count > CampfireFuelCapacity) return false;
        }

        return true;
    }

    public static void TakeFromContainer(
        WorldState world, WorldObjectState obj, NPCState looter,
        IReadOnlyList<ItemInstance> moving, IReadOnlyList<ObjectId> groundSources)
    {
        var theft = TheftMath.IsTakingTheft(world, looter, obj, moving);
        foreach (var id in groundSources)
            if (world.Entities.Objects.TryGetValue(id, out var external) && TheftMath.IsTheft(world, looter, external))
                theft = true;
        foreach (var item in moving)
        {
            // A row can be a reference from Contents or a materialized copy of
            // an external rack object (removed below through groundSources).
            // Remove by identity when it is the former; never definition-equal
            // remove a different bottle when it is the latter.
            InventoryMath.RemoveReference(obj.Contents, item);
            // Служебное число снимается вместе с вещью: у топлива это метка
            // очереди, у мяса — прогресс прожарки (§151.3). В кармане ни то,
            // ни другое смысла не имеет.
            if (item.ResourceAmount == QueuedFuelMarker ||
                item.CampfireFuelRole == CampfireFuelRole.None && IsSpitMeat(item))
                item.ResourceAmount = 0f;
            item.CampfireFuelRole = CampfireFuelRole.None;

            looter.Inventory.Items.Add(item);
        }

        foreach (var id in groundSources)
        {
            if (world.Entities.Objects.TryGetValue(id, out var source)) source.Contents.Clear();
            WorldObjectMutations.DespawnObject(world, id);
        }
        if (theft && moving.Count > 0) TheftMath.OnStolen(world, looter, obj);
        if (obj.DefinitionId == ContentIds.StorageChest && moving.Count > 0) StorageChest.SetLid(obj, true);
    }

    public static void GiveToContainer(
        WorldState world, WorldObjectState obj, NPCState looter,
        IReadOnlyList<ItemInstance> moving)
    {
        foreach (var item in moving)
        {
            if (!InventoryMath.RemoveReference(looter.Inventory.Items, item)) continue;
            LeafDryingMath.Initialize(item,world.Tick);
            var externalStorage =
                (IsRack(world, obj) || obj.DefinitionId == ContentIds.WaterCollector) &&
                obj.Junctions.Count > 0;
            if (externalStorage &&
                world.Content.ObjectDefinitions.TryGetValue(
                    item.DefinitionId, out var definition) &&
                AcceptsExternalObject(obj, definition, item.DefinitionId))
            {
                if (obj.DefinitionId == ContentIds.WaterCollector)
                {
                    WaterCollectorMath.SpawnParkedVessel(world, obj, item, looter.Id);
                    continue;
                }
                var stored = WorldObjectMutations.SpawnObject(
                    world, item.DefinitionId, obj.Fragment, obj.Tile, obj.Junctions[0]);
                stored.RotationDegrees = obj.RotationDegrees;
                stored.Wetness = item.Wetness;
                stored.Durability = item.Durability;
                stored.Dirtiness = item.Dirtiness;
                stored.Bloodiness = item.Bloodiness;
                stored.ResourceAmount = item.ResourceAmount;
                stored.WaterKind = item.WaterKind;
                stored.LastAddedWaterKind = item.LastAddedWaterKind;
                stored.Owner = item.OwnerId != 0 ? new EntityId(item.OwnerId) : looter.Id;
                if (item.Contents is { Count: > 0 } contents)
                {
                    stored.Contents.AddRange(contents);
                    contents.Clear();
                }
            }
            else if (IsCampfire(world, obj))
            {
                // §151.3: дерево встаёт в очередь топлива (метка), сырой кусок
                // — на вертел с нулевым прогрессом прожарки; дальше его крутит
                // FireSystem ровно так же, как повешенный самой колонисткой.
                if (HumanMeatCatalog.IsRaw(item.DefinitionId)) item.ResourceAmount = 0f;
                else item.CampfireFuelRole = IsFuel(world,item) ? CampfireFuelRole.Wood : CampfireFuelRole.Discard;
                obj.Contents.Add(item);
            }
            else
            {
                obj.Contents.Add(item);
                if (obj.DefinitionId == ContentIds.StorageChest) StorageChest.SetLid(obj, true);
            }
        }
    }
}

/// <summary>Read-only fire prerequisites for external observers (§42).</summary>
public static class CampfireReadiness
{
    public static bool HasQueuedFuel(WorldState world, WorldObjectState fire) =>
        ContainerLootMath.HasQueuedCampfireFuel(world, fire);
    public static bool HasCarriedFuel(WorldState world, NPCState npc) =>
        ContainerLootMath.FindCarriedCampfireFuel(world, npc) != null;
}

}
