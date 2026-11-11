using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §120.13: ОДНО правило размещения для всего, что ставят в режиме стройки —
/// кровать, очаг, шкаф, станции, костёр, факел, грядка, современная мебель.
/// Предмет стоит на точке навигационной сетки (junction, шаг 0,375 wu), занимает
/// свой авторский footprint и повёрнут одним из шести шагов по 60°. На полу или
/// на земле — правило одно и то же; пол лишь поднимает вид на высоту настила.
///
/// <para>
/// Раньше путей было пять (центр гекса §66, слоты чертежа §120.6, «современное»
/// только на готовый пол §176, «поставить из рюкзака» и точечная посадка), и у
/// каждого были свои запреты. Здесь живёт единственная проверка места; команды
/// <see cref="PlaceFurnitureCommand"/>, <see cref="MoveFurnitureCommand"/> и
/// <see cref="RemoveFurnitureCommand"/> — три её потребителя.
/// </para>
/// </summary>
public static class FurniturePlacement
{
    private const float KeyStepY = 0.1875f;
    private const float KeyStepX = HexSpatialMath.Sqrt3 * 0.1875f;

    /// <summary>Ключ точки сетки по мировой позиции — обратная к
    /// <see cref="BlueprintGeometry.JunctionToWorld"/>.</summary>
    public static JunctionKey KeyOf(Float2 position) => new JunctionKey(
        (int)MathF.Round(position.X / KeyStepX),
        (int)MathF.Round(position.Y / KeyStepY));

    public static int YawStepOf(float rotationDegrees) =>
        (((int)MathF.Round(rotationDegrees / 60f)) % 6 + 6) % 6;

    /// <summary>
    /// Строка каталога, которой этот объект мира числится в режиме стройки, или
    /// <c>null</c>, если это не расставляемая мебель. Одна функция для симуляции
    /// и для клиента (snapshot несёт те же четыре поля), чтобы список того, что
    /// можно двигать, нельзя было забыть в одном из двух мест.
    /// </summary>
    public static string CatalogId(
        string definitionId, string buildProduct, string variant, bool architecture)
    {
        if (architecture) return null;
        var site = definitionId == ContentIds.BuildSite;
        var id = site ? buildProduct : definitionId;
        if (string.IsNullOrEmpty(id)) return null;
        // Легаси-кровать цельной хижины стоит не на точке сетки.
        if (variant == ContentIds.HutBedVariant) return null;
        if (id == ContentIds.Campfire && variant == BuildingRules.HutHearthVariant)
            return ContentIds.FurnitureHearth;
        // Семечко в каталоге — это ГРЯДКА; лежащий кокос мебелью не является.
        if (PlantGrowthCatalog.TryGetBySeed(id, out _)) return site ? id : null;
        if (id == ContentIds.Torch)
            return site || variant == InventoryPlacementMath.PlantedTorchVariant ? id : null;
        // Современная мебель: расставленная — мебель, лежащая находка — запас.
        if (PortalWorld.IsFurniture(id))
            return site || variant == PortalWorld.PlacedFurnitureVariant ? id : null;
        return BuildCatalogDefinition.TryGet(id, out var entry) &&
               entry.PlacementKind == BuildCatalogPlacementKind.Furniture
            ? id
            : null;
    }

    public static string CatalogIdOf(WorldObjectState obj) => obj == null
        ? null
        : CatalogId(obj.DefinitionId, obj.BuildProduct, obj.Variant,
            obj.IsArchitectureElement || obj.ArchitectureOwnerId.HasValue);

    public static string CatalogIdOf(ObjectSnapshot obj) => obj == null
        ? null
        : CatalogId(obj.DefinitionId, obj.BuildProduct, obj.Variant,
            obj.ArchitectureOwnerObjectId.HasValue || obj.ArchitectureElements.Count > 0);

    /// <summary>Точки сетки, которые предмет занимает при данном якоре и шаге
    /// поворота. Тот же набор блокирует навигацию у готовой мебели.</summary>
    public static List<JunctionKey> FootprintKeys(string catalogId, JunctionKey anchor, int yawStep)
    {
        var offsets = BlueprintFurnitureFootprints.LocalOffsets(catalogId);
        var result = new List<JunctionKey>(offsets.Count);
        foreach (var local in offsets)
        {
            var rotated = BlueprintGeometry.RotateJunctionOffset(local, yawStep);
            result.Add(new JunctionKey(anchor.XKey + rotated.XKey, anchor.YKey + rotated.YKey));
        }

        return result;
    }

    public static bool TryFindJunction(WorldState world, JunctionKey key, out Junction junction)
    {
        junction = null;
        var point = BlueprintGeometry.JunctionToWorld(key);
        var center = HexSpatialMath.WorldToTile(point);
        if (TryFindInTile(world, center, key, out junction)) return true;
        foreach (var direction in HexDirection.All)
        {
            var coord = new TileCoord(center.Q + direction.DQ, center.R + direction.DR);
            if (TryFindInTile(world, coord, key, out junction)) return true;
        }

        return false;
    }

    private static bool TryFindInTile(WorldState world, TileCoord coord, JunctionKey key, out Junction found)
    {
        found = null;
        if (!world.Tiles.Items.TryGetValue(coord, out var tile)) return false;
        foreach (var id in tile.Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(id, out var junction) ||
                !KeyOf(junction.WorldPosition).Equals(key)) continue;
            // Как FindExactJunction: при совпадении — наименьший id.
            if (found == null || id.Value < found.Id.Value) found = junction;
        }

        return found != null;
    }

    /// <summary>Тайл, на котором числится объект с этим якорем: точка на ребре
    /// принадлежит двум-трём гексам, берём тот, что под самой точкой.</summary>
    public static TileCoord TileOf(Junction anchor)
    {
        var under = HexSpatialMath.WorldToTile(anchor.WorldPosition);
        if (anchor.Tiles.Contains(under) || anchor.Tiles.Count == 0) return under;
        var best = anchor.Tiles[0];
        foreach (var tile in anchor.Tiles)
            if (tile.Q < best.Q || tile.Q == best.Q && tile.R < best.R) best = tile;
        return best;
    }

    /// <summary>Предмет мира по строке каталога и якорю — так клиент адресует
    /// мебель: ему не нужен ObjectId только что размеченной площадки.</summary>
    public static WorldObjectState FindAt(WorldState world, string catalogId, JunctionKey key)
    {
        if (!TryFindJunction(world, key, out var anchor)) return null;
        WorldObjectState found = null;
        foreach (var tile in NearbyTiles(TileOf(anchor), 1))
        {
            if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var ids)) continue;
            foreach (var id in ids)
            {
                if (!world.Entities.Objects.TryGetValue(id, out var obj) ||
                    obj.Junctions.Count == 0 || !obj.Junctions[0].Equals(anchor.Id) ||
                    CatalogIdOf(obj) != catalogId) continue;
                if (found == null || obj.Id.Value < found.Id.Value) found = obj;
            }
        }

        return found;
    }

    /// <summary>
    /// Можно ли поставить предмет каталога якорем на эту точку. <c>null</c> —
    /// можно; иначе причина отказа. <paramref name="ignore"/> — сам передвигаемый
    /// предмет: его собственные точки свободны для него же.
    /// </summary>
    public static string Validate(
        WorldState world, string catalogId, Junction anchor, int yawStep, WorldObjectState ignore = null)
    {
        if (world == null || anchor == null) return "PlacementBlocked";
        var tileCoord = TileOf(anchor);
        if (!world.Tiles.Items.TryGetValue(tileCoord, out var tile) ||
            !tile.Flags.HasFlag(TileFlags.Walkable) ||
            (tile.Flags & (TileFlags.Water | TileFlags.Blocked)) != 0 ||
            PortalWorld.IsApartmentTile(tileCoord))
            return "PlacementBlocked";

        var anchorKey = KeyOf(anchor.WorldPosition);
        var keys = FootprintKeys(catalogId, anchorKey, yawStep);
        var footprint = new HashSet<JunctionId>();
        foreach (var key in keys)
        {
            if (!TryFindJunction(world, key, out var junction) || junction.Door ||
                junction.Fragment != anchor.Fragment ||
                world.SwimJunctions.Contains(junction.Id) ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id))
                return "PlacementBlocked";
            footprint.Add(junction.Id);
        }

        // Кто держит заблокированные точки следа. Стена дома держать вправе —
        // мебель стоит вплотную к стене; дерево, валун, чужая мебель и обрыв —
        // нет. Команда игрока редка, один проход по объектам здесь дёшев.
        var heldByArchitecture = new HashSet<JunctionId>();
        foreach (var other in world.Entities.Objects.Values)
        {
            if (other.BlockedJunctions.Count == 0 || ignore != null && other.Id.Equals(ignore.Id)) continue;
            var architecture = other.IsArchitectureElement || other.ArchitectureOwnerId.HasValue ||
                               BuildSiteMath.IsArchitecturalBuilding(BuildingRules.ProductOf(other));
            foreach (var held in other.BlockedJunctions)
            {
                if (!footprint.Contains(held)) continue;
                if (!architecture) return "PlacementOccupied";
                heldByArchitecture.Add(held);
            }
        }

        var ownBlocking = ignore != null
            ? new HashSet<JunctionId>(ignore.BlockedJunctions)
            : null;
        foreach (var id in footprint)
        {
            if (!world.Junctions.Items[id].Blocked || heldByArchitecture.Contains(id)) continue;
            if (ownBlocking != null && ownBlocking.Contains(id)) continue;
            return "PlacementBlocked"; // обрыв, кромка моря — ничья блокировка
        }

        // Площадки не блокируют навигацию, пока строятся, а деревце или могила
        // могут не иметь радиуса вовсе — пересечение следов проверяем явно.
        var mine = new HashSet<JunctionKey>(keys);
        foreach (var coord in NearbyTiles(tileCoord, 2))
        {
            if (!world.Caches.ObjectsByTile.TryGetValue(coord, out var ids)) continue;
            foreach (var id in ids)
            {
                if (!world.Entities.Objects.TryGetValue(id, out var other) ||
                    ignore != null && other.Id.Equals(ignore.Id) ||
                    other.Junctions.Count == 0 ||
                    !world.Junctions.Items.TryGetValue(other.Junctions[0], out var otherAnchor)) continue;
                var otherKey = KeyOf(otherAnchor.WorldPosition);
                var otherCatalog = CatalogIdOf(other);
                if (otherCatalog != null)
                {
                    foreach (var key in FootprintKeys(otherCatalog, otherKey, YawStepOf(other.RotationDegrees)))
                        if (mine.Contains(key)) return "PlacementOccupied";
                }
                else if (mine.Contains(otherKey) && StructurePlacement.IsStructure(world, other) &&
                         !other.IsArchitectureElement && !other.ArchitectureOwnerId.HasValue &&
                         !BuildSiteMath.IsArchitecturalBuilding(BuildingRules.ProductOf(other)))
                {
                    return "PlacementOccupied";
                }
            }
        }

        return null;
    }

    private static IEnumerable<TileCoord> NearbyTiles(TileCoord center, int radius)
    {
        for (var dq = -radius; dq <= radius; dq++)
        for (var dr = Math.Max(-radius, -dq - radius); dr <= Math.Min(radius, -dq + radius); dr++)
            yield return new TileCoord(center.Q + dq, center.R + dr);
    }

    private static bool CanEditFootprint(
        WorldState world, IReadOnlyCollection<Faction> factions, string catalogId, JunctionKey anchor, int yawStep)
    {
        foreach (var key in FootprintKeys(catalogId, anchor, yawStep))
        {
            if (!TryFindJunction(world, key, out var junction)) return false;
            foreach (var tile in junction.Tiles)
                if (!ConstructionAccess.CanEditTile(world, factions, tile)) return false;
        }

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Поставить.
    // ─────────────────────────────────────────────────────────────────────

    internal static string Place(
        WorldState world, string catalogId, JunctionKey key, int yawStep,
        IReadOnlyCollection<Faction> factions, out WorldObjectState placed)
    {
        placed = null;
        yawStep = BlueprintGeometry.NormalizeSector(yawStep);
        if (!BuildCatalogDefinition.TryGet(catalogId, out var entry) ||
            entry.PlacementKind != BuildCatalogPlacementKind.Furniture)
            return "UnknownProduct";
        var product = Bootstrap.BuildingBootstrap.PlanFurnitureProduct(catalogId);
        if (product == null || !world.Content.ObjectDefinitions.ContainsKey(product)) return "UnknownProduct";
        if (!TryFindJunction(world, key, out var anchor)) return "PlacementBlocked";
        if (!CanEditFootprint(world, factions, catalogId, key, yawStep)) return ConstructionAccess.ForeignReason;
        var reason = Validate(world, catalogId, anchor, yawStep);
        if (reason != null) return reason;

        var tile = TileOf(anchor);
        var builder = ConstructionAccess.Builder(world, factions, tile);
        var rotation = StructurePlacement.QuantizeHexYaw(yawStep * 60f);

        if (PortalWorld.IsFurniture(catalogId))
        {
            // §176: современная мебель не строится — берётся готовый экземпляр
            // из запаса лагеря (рюкзаки жителей и земля лагеря) и ставится сразу.
            reason = PortalWorld.PlaceFurnitureFromStock(world, catalogId, tile, anchor.Id, rotation, builder, out placed);
            return reason;
        }

        if (PlantGrowthCatalog.TryGetBySeed(product, out _))
        {
            if (!PlantGrowthSystem.CanPlantAt(world, anchor.Id, tile)) return "PlantingSpotOccupied";
            placed = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite, anchor.Fragment, tile, anchor.Id);
            placed.BuildProduct = product;
            placed.OwnerFaction = builder;
            return null;
        }

        var householdHearth = catalogId == ContentIds.FurnitureHearth;
        placed = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite, anchor.Fragment, tile, anchor.Id);
        placed.BuildProduct = product;
        if (householdHearth) placed.Variant = BuildingRules.HutHearthVariant;
        placed.RotationDegrees = rotation;
        Bootstrap.BuildingBootstrap.ApplyFurnitureBill(placed, product, householdHearth);
        placed.OwnerFaction = builder;
        Bootstrap.BuildingBootstrap.RememberPlanSite(world, placed);
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildSitePlanned",
                $"Product={product} Tile={tile} Site={placed.Id.Value} Yaw={yawStep}");
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Передвинуть / повернуть — и площадку, и уже построенное.
    // ─────────────────────────────────────────────────────────────────────

    internal static string Move(
        WorldState world, string catalogId, JunctionKey from, JunctionKey to, int yawStep,
        IReadOnlyCollection<Faction> factions)
    {
        yawStep = BlueprintGeometry.NormalizeSector(yawStep);
        var obj = FindAt(world, catalogId, from);
        if (obj == null) return "NoSuchFurniture";
        if (!ConstructionAccess.CanEditObject(world, factions, obj)) return ConstructionAccess.ForeignReason;
        if (obj.IsOccupied || obj.CurrentUser.HasValue) return "Occupied";
        if (!TryFindJunction(world, to, out var target)) return "PlacementBlocked";
        if (!CanEditFootprint(world, factions, catalogId, to, yawStep)) return ConstructionAccess.ForeignReason;
        if (!world.Junctions.Items.TryGetValue(obj.Junctions[0], out var oldAnchor)) return "NoSuchFurniture";
        if (target.Fragment != oldAnchor.Fragment) return "PlacementBlocked";
        var reason = Validate(world, catalogId, target, yawStep, ignore: obj);
        if (reason != null) return reason;

        var oldAnchorId = oldAnchor.Id;
        var product = BuildingRules.ProductOf(obj);
        // Вещи на столе/полке остаются на полу прежнего места (§176.3).
        SurfacePlacementMath.Detach(world, obj);
        WorldObjectMutations.ReleaseOwnedBlocking(world, obj);
        obj.Junctions[0] = target.Id;
        obj.Fragment = target.Fragment;
        WorldObjectMutations.MoveObjectTile(world, obj, TileOf(target));
        obj.RotationDegrees = StructurePlacement.QuantizeHexYaw(yawStep * 60f);
        // Легаси-кровать «в доме по умолчанию» после перестановки — обычная
        // кровать на точке сетки: её центр и поза сна считаются от якоря.
        WorldObjectMutations.SetObstacleBlocking(world, obj, blocked: true);
        ExecutionSystem.ApplyIndoorFurnitureFootprint(world, obj);
        if (obj.DefinitionId == ContentIds.Workbench)
        {
            obj.CraftJunction = StructurePlacement.WorkbenchJunction(
                world, obj.Tile, target.Id, obj.RotationDegrees);
        }

        // Мебель из плана дома больше не принадлежит его чертежу: иначе
        // следующая достройка дома заново разметит площадку на старом месте.
        Bootstrap.BuildingBootstrap.ForgetPlanFurnitureAt(world, oldAnchorId, product);
        ForgetStalePlans(world, obj);
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "FurnitureMoved",
                $"Obj={obj.Id.Value} Def={catalogId} Tile={obj.Tile} Yaw={yawStep}");
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Убрать площадку. Готовую мебель режим стройки не уничтожает.
    // ─────────────────────────────────────────────────────────────────────

    internal static string Remove(
        WorldState world, string catalogId, JunctionKey at, IReadOnlyCollection<Faction> factions)
    {
        var obj = FindAt(world, catalogId, at);
        if (obj == null) return "NoSuchFurniture";
        if (!ConstructionAccess.CanEditObject(world, factions, obj)) return ConstructionAccess.ForeignReason;
        if (obj.DefinitionId != ContentIds.BuildSite) return "FurnitureBuilt";
        if (obj.IsOccupied || obj.CurrentUser.HasValue) return "Occupied";

        var anchorId = obj.Junctions[0];
        var product = obj.BuildProduct;
        // Привезённое не испаряется: материалы ложатся на землю на месте
        // площадки теми же экземплярами.
        if (world.Junctions.Items.TryGetValue(anchorId, out var anchor))
        {
            foreach (var item in obj.Contents)
            {
                if (item == null || !world.Content.ObjectDefinitions.ContainsKey(item.DefinitionId)) continue;
                var dropped = WorldObjectMutations.SpawnObject(
                    world, item.DefinitionId, anchor.Fragment, obj.Tile, anchorId);
                dropped.Wetness = item.Wetness;
                dropped.Durability = item.Durability;
                dropped.Dirtiness = item.Dirtiness;
                dropped.Bloodiness = item.Bloodiness;
            }
        }

        obj.Contents.Clear();
        WorldObjectMutations.DespawnObject(world, obj.Id);
        Bootstrap.BuildingBootstrap.ForgetPlanFurnitureAt(world, anchorId, product);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Memory.KnownObjects.Remove(obj.Id)) npc.Memory.Version++;
        }

        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildSiteCancelled", $"Site={obj.Id.Value} Product={product}");
        }

        return null;
    }

    /// <summary>Девушки помнят предмет на старом месте — переписать запись
    /// (в том числе постоянную память о своей кровати §64.10), чтобы никто не
    /// пошёл к пустой точке.</summary>
    private static void ForgetStalePlans(WorldState world, WorldObjectState obj)
    {
        var junction = obj.Junctions.Count > 0 ? obj.Junctions[0] : (JunctionId?)null;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!npc.Memory.KnownObjects.TryGetValue(obj.Id, out var record) || record == null) continue;
            record.Tile = obj.Tile;
            record.Junction = junction;
            record.LastSeenTick = world.Tick;
            npc.Memory.Version++;
        }
    }
}

}
