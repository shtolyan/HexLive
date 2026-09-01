using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{

/// <summary>
/// First vertical slice of the architectural-building grammar: one hex-owned
/// hut, one portal edge, two integrated (non-blocking) cot anchors, and one
/// protected stone hearth at the rear of the room.
/// </summary>
public static class BuildingBootstrap
{
    private const int StarterWardrobeGarmentCount = 6;
    private const float StarterWardrobeGoodArmor = 0.10f;

    public static WorldObjectState SpawnCompletedTestHut(WorldState world, Faction faction)
    {
        var home = ColonyQueries.Home(world, faction) ?? new TileCoord(0, 4);
        if (!world.Tiles.Items.TryGetValue(home, out var homeTile)) return null;

        var candidates = new List<TileCoord>();
        // #179: сперва строгий отбор — дом обходим со всех шести сторон и
        // дверной проём не на шве высоты, — по кольцам 1..3; и только если
        // таких мест нет вовсе, прежний мягкий отбор по кольцам 1..2:
        // дом у горы лучше, чем колония без дома.
        for (var pass = 0; pass < 2 && candidates.Count == 0; pass++)
        {
            var strict = pass == 0;
            var maxRing = strict ? 3 : 2;
            for (var ring = 1; ring <= maxRing && candidates.Count == 0; ring++)
            {
                foreach (var pair in world.Tiles.Items)
                {
                    var coord = pair.Key;
                    var tile = pair.Value;
                    if (HexSpatialMath.HexDistance(coord, home) != ring ||
                        tile.Elevation != homeTile.Elevation ||
                        tile.Flags.HasFlag(TileFlags.Indoor) ||
                        !CanPlaceHut(world, coord))
                    {
                        continue;
                    }

                    if (strict &&
                        (!HutApproachableAllSides(world, coord, tile.Elevation) ||
                         !DoorThresholdFlat(world, coord, home, tile.Elevation)))
                    {
                        continue;
                    }

                    candidates.Add(coord);
                }
            }
        }

        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
        var roll = MathUtil.Hash01(world.Seed, 1, 1, 6601);
        var index = Math.Min(candidates.Count - 1, (int)(roll * candidates.Count));
        var hutTile = candidates[index];
        var anchor = StructurePlacement.CenterJunction(world, hutTile);
        if (anchor is not { } anchorId ||
            !world.Junctions.Items.TryGetValue(anchorId, out var center))
        {
            return null;
        }

        var homePosition = HexSpatialMath.TileToWorld(home);
        // §120.3 r2: стартовый дом — БОЛЬШЕ НЕ авторский FBX-кит hut_1hex, а
        // конструкторный чертёж Hut1Hex (§120.8): та же геометрия (окна на
        // рёбрах 1 и 5, дверь в центре ребра 3, та же мебель на тех же
        // координатах), но собранная из модулей конструктора — стартовый дом и
        // дом, начерченный игроком, выглядят и живут одинаково. hut_1hex
        // остаётся путём совместимости для старых сейвов.
        var draft = Runtime.Blueprints.BuiltInBuildingBlueprints.Hut1Hex();
        var blueprintId = world.NextPlayerBlueprintId++;
        world.PlayerBlueprints[blueprintId] = draft;

        var hut = WorldObjectMutations.SpawnObject(
            world, ContentIds.HutPlan, center.Fragment, hutTile, anchorId);
        hut.BlueprintId = blueprintId;
        // #237: дом стартового лагеря принадлежит ЕМУ, и это записано, а не
        // выведено — иначе после §146.13 слияния/гибели лагеря дверь достаётся
        // соседу по расстоянию (см. DoorTopology.OwnerFaction).
        Core.DoorTopology.StampOwner(world, hut, faction);
        BuildingRules.EnsureHutElements(world, hut, completed: true);
        // Keep the hex itself on one of its six 60° symmetries and choose the
        // symmetry whose door normal is closest to camp. Arbitrary yaw rotates
        // walls off the tile edges; treating local forward as the door normal
        // seals the neighbouring edge instead of the visible doorway.
        var desiredDoorYaw = StructurePlacement.FacingYaw(center.WorldPosition, homePosition);
        var localDoorYaw = BuildingRules.DoorOutwardYaw(world, hut);
        hut.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(
            desiredDoorYaw - localDoorYaw);
        foreach (var piece in BuildingRules.ArchitectureObjects(world, hut))
            piece.RotationDegrees = hut.RotationDegrees;
        CompleteHut(world, hut);

        // §120.6 разметил мебель чертежа обычными площадками; стартовый дом
        // рождается обжитым — поднимаем их тем же ядром подъёма, что у
        // ExecutionSystem, без второй реализации.
        var footprint = FootprintTiles(world, hut);
        var furnitureSites = world.Entities.Objects.Values.Where(candidate =>
                candidate.DefinitionId == ContentIds.BuildSite &&
                !string.IsNullOrEmpty(candidate.BuildProduct) &&
                candidate.BuildProduct != ContentIds.HutPlan &&
                footprint.Contains(candidate.Tile))
            .ToArray();
        foreach (var site in furnitureSites)
        {
            Runtime.ExecutionSystem.RaiseFurnitureSite(world, site, center.Fragment, anchorId);
        }

        // §118.2: аптечка — спутница домашнего гардероба; канонической ветке
        // её даёт SpawnWardrobe, плановому стартовому дому — этот вызов.
        var starterWardrobe = FindWardrobe(world, hut);
        if (starterWardrobe != null) SpawnMedkit(world, hut, starterWardrobe);

        SeedStarterWardrobeGarments(world, hut);
        return hut;
    }

    /// <summary>Creates an ordinary modular site for tests and future NPC staking.</summary>
    public static WorldObjectState CreateHutSite(WorldState world, TileCoord tile, float facingYaw)
    {
        if (!CanPlaceHut(world, tile) || StructurePlacement.CenterJunction(world, tile) is not { } anchor)
        {
            return null;
        }

        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, world.Junctions.Items[anchor].Fragment, tile, anchor);
        site.BuildProduct = ContentIds.Hut1Hex;
        site.BillSticks = BuildingRules.TotalSticks;
        site.BillBoards = BuildingRules.TotalBoards;
        site.BillRope = BuildingRules.TotalRope;
        site.BillLeaves = BuildingRules.TotalLeaves;
        BuildingRules.EnsureHutElements(world, site);
        var localDoorYaw = BuildingRules.DoorOutwardYaw(world, site);
        site.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(
            facingYaw - localDoorYaw);
        return site;
    }

    /// <summary>
    /// Every hex a building stands on. The canonical hut owns exactly its own
    /// tile — that is a property of THAT plan, not of "a building" — while a
    /// committed player plan owns whatever it put floor on, rotated with the
    /// building. Yaw is quantised to the six hex symmetries, so the rotation is
    /// an exact integer number of 60° axial steps and no coordinate is rounded.
    /// </summary>
    public static IReadOnlyList<TileCoord> FootprintTiles(WorldObjectState building)
    {
        if (building == null) return Array.Empty<TileCoord>();
        var product = string.IsNullOrEmpty(building.BuildProduct)
            ? building.DefinitionId
            : building.BuildProduct;
        return FootprintTiles(product, building.Tile, building.RotationDegrees);
    }

    /// <summary>§120.8: футпринт с учётом ЧЕРТЕЖА владельца — произвольный план
    /// игрока владеет своими гексами, а не гексами committed-плана.</summary>
    public static IReadOnlyList<TileCoord> FootprintTiles(
        WorldState world, WorldObjectState building)
    {
        if (building == null) return Array.Empty<TileCoord>();
        var plan = BuildingRules.PlanFor(world, building);
        return plan != null
            ? FootprintTiles(plan, building.Tile, building.RotationDegrees)
            : FootprintTiles(building);
    }

    public static IReadOnlyList<TileCoord> FootprintTiles(
        string buildProduct, TileCoord anchorTile, float rotationDegrees)
    {
        if (buildProduct != ContentIds.HutPlan) return new[] { anchorTile };
        return FootprintTiles(
            Runtime.Blueprints.CommittedBuildingPlans.PlayerHut, anchorTile, rotationDegrees);
    }

    public static IReadOnlyList<TileCoord> FootprintTiles(
        Runtime.Blueprints.BuildingBlueprintDraft plan,
        TileCoord anchorTile, float rotationDegrees)
    {
        var planAnchor = Runtime.Blueprints.BlueprintBuildingPlan.AnchorTile(plan);
        // +60° of world yaw is one axial step (q,r) -> (-r, q+r): TileToWorld
        // maps +q to 0° and +r to 60°, so the two rotations are the same one.
        var steps = ((int)MathF.Round(rotationDegrees / 60f) % 6 + 6) % 6;
        var result = new List<TileCoord>();
        foreach (var hex in Runtime.Blueprints.BlueprintBuildingPlan.Footprint(plan))
        {
            var q = hex.Q - planAnchor.Q;
            var r = hex.R - planAnchor.R;
            for (var step = 0; step < steps; step++)
            {
                var rotatedQ = -r;
                r = q + r;
                q = rotatedQ;
            }

            var tile = new TileCoord(anchorTile.Q + q, anchorTile.R + r);
            if (!result.Contains(tile)) result.Add(tile);
        }

        return result;
    }

    /// <summary>
    /// §146.5: каждому лагерю большого острова — СВОЙ экземпляр чертежа
    /// Hut1Hex, застолблённый как обычный §120.8-сайт в 2-4 гексах от костра.
    /// Экземпляры отдельные затем, чтобы правка игроком СВОЕГО чертежа никогда
    /// не мутировала дома соседних лагерей. Девушки строят его сами:
    /// архитектурный лейн §54 активируется самим фактом сайта.
    /// </summary>
    public static void StakeCampHutPlans(WorldState world)
    {
        // Порядок — ординал фракции (правило BedSiteSystem): порядок словаря
        // не смеет попадать в реплей.
        var camps = new List<Agents.Faction>();
        foreach (var faction in world.FactionHomes.Keys)
        {
            if (Runtime.FactionRelations.IsColonyKind(faction))
            {
                camps.Add(faction);
            }
        }

        camps.Sort((a, b) => ((int)a).CompareTo((int)b));

        foreach (var faction in camps)
        {
            var anchor = world.FactionHomes[faction];
            var candidates = new List<TileCoord>();
            foreach (var pair in world.Tiles.Items)
            {
                var distance = HexSpatialMath.HexDistance(pair.Key, anchor);
                if (distance >= 2 && distance <= 4)
                {
                    candidates.Add(pair.Key);
                }
            }

            candidates.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
            if (candidates.Count == 0)
            {
                continue;
            }

            var draft = Runtime.Blueprints.BuiltInBuildingBlueprints.Hut1Hex();
            var blueprintId = world.NextPlayerBlueprintId++;
            world.PlayerBlueprints[blueprintId] = draft;

            // Сидированный старт + обход по кольцу: CreatePlayerBlueprintSite
            // сам вернёт null на нестроябельном гексе (CanPlaceHut), так что
            // первый подходящий кандидат и есть площадка.
            var start = (int)(MathUtil.Hash01(world.Seed, (int)faction, 146, 14651) *
                candidates.Count) % candidates.Count;
            WorldObjectState site = null;
            for (var i = 0; i < candidates.Count && site == null; i++)
            {
                var tile = candidates[(start + i) % candidates.Count];
                site = CreatePlayerBlueprintSite(world, tile, 0f, blueprintId);
            }

            if (site == null)
            {
                // Патологический сид: лагерь остаётся без чертежа — игрок или
                // прибытия §132 поставят его позже; мир от этого не ломается.
                world.PlayerBlueprints.Remove(blueprintId);
            }
            else
            {
                // #237: чей это будет дом — известно ЗДЕСЬ, в момент
                // застолбления, и записывается на площадку. Подъём унесёт
                // штамп на готовое здание (RaiseFurnitureSite), а слияние
                // лагерей перепишет его на канонический лагерь.
                Core.DoorTopology.StampOwner(world, site, faction);
            }
        }
    }

    /// <summary>
    /// Stakes the player's committed §120 plan as an ordinary unbuilt site.
    /// Everything past this point is the existing furniture-site chain: the
    /// bill is hauled, the modules are raised, and CompleteHut finishes it.
    /// </summary>
    public static WorldObjectState CreateHutPlanSite(
        WorldState world, TileCoord tile, float facingYaw)
    {
        // The facing decides the footprint, so it has to be known before the
        // placement check rather than after the site exists.
        var rotation = StructurePlacement.QuantizeHexSymmetryYaw(
            facingYaw - BuildingRules.DoorLocalOutwardYaw(ContentIds.HutPlan));
        // Bug #188: even a catalog/committed plan becomes INSTANCE DATA once
        // it is staked.  Register a clone so a later game version cannot change
        // the geometry below a half-built site, and so RaiseFurnitureSite can
        // carry one authoritative id into the finished building.
        var blueprintId = world.NextPlayerBlueprintId++;
        var plan = Runtime.Blueprints.CommittedBuildingPlans.PlayerHut.Clone();
        world.PlayerBlueprints[blueprintId] = plan;
        var site = CreatePlanSite(world, tile, rotation, plan, blueprintId);
        if (site == null)
        {
            world.PlayerBlueprints.Remove(blueprintId);
        }
        return site;
    }

    /// <summary>
    /// §120.8: разметить ПРОИЗВОЛЬНЫЙ чертёж игрока. Чертёж обязан УЖЕ лежать в
    /// <c>world.PlayerBlueprints[blueprintId]</c> — модули, топология и мебель
    /// площадки разрешаются через её BlueprintId, и площадка без записи в
    /// реестре молча откатилась бы на committed-план.
    /// </summary>
    public static WorldObjectState CreatePlayerBlueprintSite(
        WorldState world, TileCoord tile, float rotationDegrees, int blueprintId)
    {
        if (world == null ||
            !world.PlayerBlueprints.TryGetValue(blueprintId, out var plan) || plan == null)
        {
            return null;
        }

        return CreatePlanSite(
            world, tile, StructurePlacement.QuantizeHexSymmetryYaw(rotationDegrees),
            plan, blueprintId);
    }

    /// <summary>
    /// Applies a constructor revision to the real building owner already in the
    /// world. Unchanged SlotKeys keep their top-level LEGO objects and progress;
    /// only the delta is reconciled. A finished owner becomes an in-place
    /// HutPlan build site while new modules are hauled, so the old house is not
    /// despawned/replaced by a second aggregate.
    /// </summary>
    internal static bool ApplyBlueprintRevision(
        WorldState world, WorldObjectState owner,
        Runtime.Blueprints.BuildingBlueprintDraft source, out string error)
    {
        error = string.Empty;
        var previous = BuildingRules.EditablePlanFor(world, owner);
        if (world == null || owner == null || previous == null || source == null)
        {
            error = "NotEditableBuilding";
            return false;
        }

        var draft = source.Clone();
        var stableAnchor = Runtime.Blueprints.BlueprintBuildingPlan.AnchorTile(previous);
        draft.HasAnchor = true;
        draft.AnchorQ = stableAnchor.Q;
        draft.AnchorR = stableAnchor.R;
        draft.Normalize();
        // §120.9 r2 (bug #277): мебельная дельта реконсайлится, а не
        // отклоняется глухо. Двигать/убирать/вертеть можно только мебель, чья
        // площадка ещё ПУСТА (ни материала, ни работы) — начатую или готовую
        // ревизия отклоняет целиком: материалы игрока не телепортируются и не
        // пропадают. Здесь только валидация и сбор работы; мутации — ниже,
        // после ВСЕХ проверок размещения.
        List<WorldObjectState> orphanedFurniture = null;
        List<(WorldObjectState Site, float Yaw)> turnedFurniture = null;
        if (!SameFurniture(previous, draft) &&
            !TryPlanFurnitureDelta(
                world, owner, previous, draft,
                out orphanedFurniture, out turnedFurniture))
        {
            error = "FurnitureEditUnsupported";
            return false;
        }

        var oldFootprint = FootprintTiles(world, owner).ToHashSet();
        var newFootprint = FootprintTiles(draft, owner.Tile, owner.RotationDegrees);
        if (!world.Tiles.Items.TryGetValue(owner.Tile, out var ownerTile))
        {
            error = "PlacementBlocked";
            return false;
        }
        foreach (var tile in newFootprint)
        {
            if (!world.Tiles.Items.TryGetValue(tile, out var candidate) ||
                candidate.Elevation != ownerTile.Elevation)
            {
                error = "PlacementBlocked";
                return false;
            }
            if (!oldFootprint.Contains(tile) && !CanPlaceHut(world, tile))
            {
                error = "PlacementBlocked";
                return false;
            }
        }

        var oldKeys = previous.Elements
            .Select(Runtime.Blueprints.BlueprintBuildingPlan.SlotKey)
            .ToHashSet(StringComparer.Ordinal);
        var newKeys = draft.Elements
            .Select(Runtime.Blueprints.BlueprintBuildingPlan.SlotKey)
            .ToHashSet(StringComparer.Ordinal);
        var removedStructuralSlot = previous.Elements.Any(element =>
            !newKeys.Contains(Runtime.Blueprints.BlueprintBuildingPlan.SlotKey(element)) &&
            element.Kind is Runtime.Blueprints.BlueprintElementKind.Wall or
                Runtime.Blueprints.BlueprintElementKind.Window or
                Runtime.Blueprints.BlueprintElementKind.Door or
                Runtime.Blueprints.BlueprintElementKind.RoofSector);
        var removedFloorSlot = previous.Elements.Any(element =>
            element.Kind == Runtime.Blueprints.BlueprintElementKind.FloorSector &&
            !newKeys.Contains(Runtime.Blueprints.BlueprintBuildingPlan.SlotKey(element)));

        var legacyCompleted = owner.DefinitionId == ContentIds.Hut1Hex &&
                              BuildingRules.IsCompletedBuilding(owner);
        ISet<string> completeLegacySlots = legacyCompleted ? oldKeys : null;

        if (owner.BlueprintId == 0)
            owner.BlueprintId = world.NextPlayerBlueprintId++;
        world.PlayerBlueprints[owner.BlueprintId] = draft;
        if (owner.DefinitionId == ContentIds.Hut1Hex) owner.DefinitionId = ContentIds.HutPlan;
        if (owner.BuildProduct == ContentIds.Hut1Hex) owner.BuildProduct = ContentIds.HutPlan;

        // Bug #277: осиротевшие ПУСТЫЕ площадки мебели уходят ДО
        // RepairPlanTopology/CompleteHut — иначе остались бы ничьи Blocked
        // (та же ловушка, что в ApplyCancelBuildSite). Новые и переехавшие
        // placement стейкает CompleteHut → StakePlanFurnitureSites; у
        // недостроенного дома мебель размечается при достройке, как всегда.
        if (orphanedFurniture != null)
        {
            foreach (var site in orphanedFurniture)
            {
                WorldObjectMutations.DespawnObject(world, site.Id);
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    if (npc.Memory.KnownObjects.Remove(site.Id)) npc.Memory.Version++;
                }
            }
        }

        if (turnedFurniture != null)
        {
            foreach (var (site, yaw) in turnedFurniture) site.RotationDegrees = yaw;
        }

        var bill = BuildingRules.ReconcileHutElements(
            world, owner, completeLegacySlots);
        owner.BillSticks = Math.Max(bill.Sticks,
            owner.Contents.Count(item => item.DefinitionId == ContentIds.Stick));
        owner.BillBoards = Math.Max(bill.Boards,
            owner.Contents.Count(item => item.DefinitionId == ContentIds.Board));
        owner.BillRope = Math.Max(bill.Rope,
            owner.Contents.Count(item => item.DefinitionId == ContentIds.Rope));
        owner.BillLeaves = Math.Max(bill.Leaves,
            owner.Contents.Count(item => item.DefinitionId == ContentIds.PalmLeaf));
        owner.BillLogs = 0;
        owner.BillStones = 0;

        var unfinished = bill.Sticks + bill.Boards + bill.Rope + bill.Leaves > 0;
        owner.BuildProduct = unfinished ? ContentIds.HutPlan : string.Empty;
        if (unfinished) BuildingRules.SyncHutElements(world, owner);

        foreach (var tile in oldFootprint)
        {
            if (!world.Tiles.Items.TryGetValue(tile, out var state)) continue;
            if (!newFootprint.Contains(tile) || removedStructuralSlot)
                state.Flags &= ~(TileFlags.Indoor | TileFlags.Roofed);
            if (!newFootprint.Contains(tile) || removedFloorSlot)
                state.Flags &= ~TileFlags.HasFloor;
        }
        foreach (var tile in newFootprint)
        {
            if (oldFootprint.Contains(tile) || !world.Tiles.Items.TryGetValue(tile, out var state))
                continue;
            state.Flags &= ~(TileFlags.HasFloor | TileFlags.Indoor | TileFlags.Roofed);
        }

        RepairPlanTopology(world, owner);
        if (unfinished)
        {
            RememberPlanSite(world, owner);
        }
        else
        {
            CompleteHut(world, owner);
        }
        return true;
    }

    /// <summary>
    /// Bug #277 / §120.9 r2: проверяет мебельную дельту ревизии и собирает
    /// работу по миру. false — дельта трогает мебель, у которой уже есть
    /// прогресс (доставленный материал, начатая работа или сам готовый
    /// предмет): такая мебель не телепортируется. Только сбор, без мутаций —
    /// вызывающий применяет их после ВСЕХ проверок размещения.
    /// </summary>
    private static bool TryPlanFurnitureDelta(
        WorldState world, WorldObjectState owner,
        Runtime.Blueprints.BuildingBlueprintDraft previous,
        Runtime.Blueprints.BuildingBlueprintDraft draft,
        out List<WorldObjectState> orphaned,
        out List<(WorldObjectState Site, float Yaw)> turned)
    {
        orphaned = new List<WorldObjectState>();
        turned = new List<(WorldObjectState, float)>();
        var steps = HexSymmetrySteps(owner.RotationDegrees);
        var planAnchorTile = Runtime.Blueprints.BlueprintBuildingPlan.AnchorTile(previous);
        var index = PlanJunctionIndex(world, owner);

        // Мировая identity размеченной мебели — (product, повёрнутый anchor
        // junction), ровно как в StakePlanFurnitureSites. Поворот сравнивается
        // по yawStep ЧЕРТЕЖЕЙ, а не по мировому углу объекта: у построенной
        // мебели мировой угол не обязан совпадать с формулой штампа площадки.
        var kept = new Dictionary<(string Product, JunctionId Anchor), int>();
        foreach (var placement in draft.Furniture)
        {
            var product = PlanFurnitureProduct(placement.DefinitionId);
            if (product == null) continue;
            var anchorKey = RotatePlanJunction(
                placement.PrimaryJunction, planAnchorTile, owner.Tile, steps);
            if (index.TryGetValue(anchorKey, out var anchorId))
            {
                kept[(product, anchorId)] =
                    Runtime.Blueprints.BlueprintGeometry.NormalizeSector(placement.YawStep);
            }
        }

        foreach (var placement in previous.Furniture)
        {
            var product = PlanFurnitureProduct(placement.DefinitionId);
            if (product == null) continue;
            var tile = RotatePlanTile(placement.Tile, planAnchorTile, owner.Tile, steps);
            if (!world.Tiles.Items.ContainsKey(tile)) continue;
            var anchorKey = RotatePlanJunction(
                placement.PrimaryJunction, planAnchorTile, owner.Tile, steps);
            if (!index.TryGetValue(anchorKey, out var anchorId)) continue;
            var existing = FindPlanFurniture(world, tile, product, anchorId);
            if (existing == null) continue; // ещё не размечена — мир не задет
            var started = existing.DefinitionId != ContentIds.BuildSite ||
                existing.Contents.Count > 0 || existing.CraftWorkDone > 0;
            if (!kept.TryGetValue((product, anchorId), out var yawStep))
            {
                if (started) return false;
                orphaned.Add(existing);
                continue;
            }

            if (Runtime.Blueprints.BlueprintGeometry.NormalizeSector(
                    placement.YawStep) == yawStep)
            {
                continue; // placement не менялся — мир не трогается
            }

            if (started) return false;
            turned.Add((existing, StructurePlacement.QuantizeHexYaw(
                owner.RotationDegrees + yawStep * 60f)));
        }

        return true;
    }

    private static bool SameFurniture(
        Runtime.Blueprints.BuildingBlueprintDraft left,
        Runtime.Blueprints.BuildingBlueprintDraft right)
    {
        if (left.Furniture.Count != right.Furniture.Count) return false;
        var byId = left.Furniture.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var item in right.Furniture)
        {
            if (!byId.TryGetValue(item.Id, out var other) ||
                other.DefinitionId != item.DefinitionId ||
                other.TileQ != item.TileQ || other.TileR != item.TileR ||
                other.JunctionSlot != item.JunctionSlot ||
                Runtime.Blueprints.BlueprintGeometry.NormalizeSector(other.YawStep) !=
                Runtime.Blueprints.BlueprintGeometry.NormalizeSector(item.YawStep))
            {
                return false;
            }
        }
        return true;
    }

    private static WorldObjectState CreatePlanSite(
        WorldState world, TileCoord tile, float rotation,
        Runtime.Blueprints.BuildingBlueprintDraft plan, int blueprintId)
    {
        foreach (var footprint in FootprintTiles(plan, tile, rotation))
        {
            if (!CanPlaceHut(world, footprint)) return null;
        }

        if (StructurePlacement.CenterJunction(world, tile) is not { } anchor) return null;

        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, world.Junctions.Items[anchor].Fragment, tile, anchor);
        site.BuildProduct = ContentIds.HutPlan;
        site.BlueprintId = blueprintId;
        var bill = Runtime.Blueprints.BlueprintBuildingPlan.Bill(
            Runtime.Blueprints.BlueprintBuildingPlan.Modules(plan));
        site.BillSticks = bill.Sticks;
        site.BillBoards = bill.Boards;
        site.BillRope = bill.Rope;
        site.BillLeaves = bill.Leaves;
        site.RotationDegrees = rotation;
        BuildingRules.EnsureHutElements(world, site);
        foreach (var piece in BuildingRules.ArchitectureObjects(world, site))
            piece.RotationDegrees = rotation;
        // Nothing is raised yet, so this blocks nothing — it exists to put the
        // doorway on the map from the first tick and to make the site's topology
        // the SAME code path that every later delivery runs.
        RepairPlanTopology(world, site);
        return site;
    }

    /// <summary>
    /// Finalises either a bootstrap hut or a normally raised hut. Indoor is
    /// granted only here, after the leaf stage has completed and the site has
    /// already become the finished building object.
    /// </summary>
    public static void CompleteHut(WorldState world, WorldObjectState hut)
    {
        if (hut == null || !BuildingRules.IsCompletedBuilding(hut) ||
            !world.Tiles.Items.ContainsKey(hut.Tile))
        {
            return;
        }

        // Save/load and debug callers may supply legacy free yaw. Normalise at
        // the architectural boundary before deriving a portal edge or furniture.
        hut.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(hut.RotationDegrees);

        BuildingRules.EnsureHutElements(world, hut, completed: true);
        foreach (var piece in BuildingRules.ArchitectureObjects(world, hut))
            piece.RotationDegrees = hut.RotationDegrees;
        // §120: the roof covers the whole footprint, so the flags do too. For
        // hut_1hex that list is exactly {hut.Tile} and this is the old line.
        foreach (var footprintTile in FootprintTiles(world, hut))
        {
            if (world.Tiles.Items.TryGetValue(footprintTile, out var footprint))
                footprint.Flags |= TileFlags.HasFloor | TileFlags.Indoor | TileFlags.Roofed;
        }

        // A committed player plan owns its own geometry, so it gets its own
        // finishing pass: the walls it actually raised become obstacles and the
        // furniture IT authored is staked as ordinary build-sites (§120). None
        // of the canonical hut's hand-authored hut_1hex coordinates apply.
        if (hut.DefinitionId == ContentIds.HutPlan)
        {
            RepairPlanTopology(world, hut);
            StakePlanFurnitureSites(world, hut);
            return;
        }

        // The rest of this method is the CANONICAL hut's own kit: its portal
        // edge, its two cots, its hearth and wardrobe all live at coordinates
        // authored for that one hex. A player plan has its own geometry and
        // gets none of it — inventing a placement here would be a guess, and a
        // guess is what puts a bed inside a wall.
        if (hut.DefinitionId != ContentIds.Hut1Hex) return;
        RepairHutTopology(world, hut);
        SpawnCot(world, hut, 0);
        SpawnCot(world, hut, 1);
        RepairIntegratedCotAnchors(world);
        SpawnHearth(world, hut);
        SpawnWardrobe(world, hut);
    }

    // #179 «дом впритык к горе закрывает вход»: к стартовому дому обязан быть
    // подход со всех шести сторон — каждый сосед ходибельный, сухой и не
    // дальше ОДНОЙ ступени высоты. |Δ| >= 2 — ровно порог, которым
    // BlockCliffAndSeaJunctions решает «обрыв» и запирает весь общий обод;
    // прежний счётчик dryNeighbors >= 3 терпел до трёх горных соседей, а
    // портал двери принудительно разлочивается и «открывался» в запертый
    // карман. Правило живёт на спавне нарочно: CanPlaceHut остаётся общим
    // гейтом игроцких строек и их не ужесточает.
    private static bool HutApproachableAllSides(
        WorldState world, TileCoord coord, int elevation)
    {
        foreach (var direction in HexDirection.All)
        {
            var neighborCoord = new TileCoord(
                coord.Q + direction.DQ, coord.R + direction.DR);
            if (!world.Tiles.Items.TryGetValue(neighborCoord, out var neighbor) ||
                !neighbor.Flags.HasFlag(TileFlags.Walkable) ||
                neighbor.Flags.HasFlag(TileFlags.Water) ||
                neighbor.Flags.HasFlag(TileFlags.Blocked) ||
                Math.Abs(neighbor.Elevation - elevation) > 1)
            {
                return false;
            }
        }

        return true;
    }

    // #179: дверной проём — строго вровень с домом. Дверь стоит на ребре в
    // t=0.75, то есть у ВЕРШИНЫ гекса, которой владеют три тайла: сосед по
    // нормали двери и сосед за углом (+60°) — оба обязаны быть той же высоты.
    // Шов высоты в проёме недопустим: шаг «шов-к-шву» патфайндер запрещает,
    // а без прыжка (§50, калеки) запрещён любой подъём — своя же дверь
    // становилась непроходимой. Ориентация двери повторяет спавн:
    // QuantizeHexSymmetryYaw(FacingYaw(дом -> лагерь)).
    private static bool DoorThresholdFlat(
        WorldState world, TileCoord coord, TileCoord home, int elevation)
    {
        var doorYaw = StructurePlacement.QuantizeHexSymmetryYaw(
            StructurePlacement.FacingYaw(
                HexSpatialMath.TileToWorld(coord), HexSpatialMath.TileToWorld(home)));
        foreach (var yaw in stackalloc[] { doorYaw, doorYaw + 60f })
        {
            var neighborCoord = NeighborTowardYaw(coord, yaw);
            if (!world.Tiles.Items.TryGetValue(neighborCoord, out var neighbor) ||
                neighbor.Elevation != elevation)
            {
                return false;
            }
        }

        return true;
    }

    // Сосед, чей центр лежит по данному мировому углу. Центры соседей стоят
    // ровно на кратных 60°, но порядок HexDirection.All углам не соответствует
    // — выбираем максимальный скалярный прирост, чистой геометрией.
    private static TileCoord NeighborTowardYaw(TileCoord coord, float yawDegrees)
    {
        var origin = HexSpatialMath.TileToWorld(coord);
        var radians = yawDegrees * (MathF.PI / 180f);
        var dirX = MathF.Cos(radians);
        var dirY = MathF.Sin(radians);
        var best = coord;
        var bestDot = float.MinValue;
        foreach (var direction in HexDirection.All)
        {
            var neighborCoord = new TileCoord(
                coord.Q + direction.DQ, coord.R + direction.DR);
            var to = HexSpatialMath.TileToWorld(neighborCoord);
            var dot = (to.X - origin.X) * dirX + (to.Y - origin.Y) * dirY;
            if (dot > bestDot)
            {
                bestDot = dot;
                best = neighborCoord;
            }
        }

        return best;
    }

    public static bool CanPlaceHut(WorldState world, TileCoord tile)
    {
        if (!StructurePlacement.HexFreeForBuild(world, tile) ||
            !world.Tiles.Items.TryGetValue(tile, out var footprint))
        {
            return false;
        }

        // Keep the test doorway connected to real land and avoid shoreline
        // half-hexes whose edge junctions were already sealed by worldgen.
        var dryNeighbors = 0;
        foreach (var direction in HexDirection.All)
        {
            var neighbor = new TileCoord(tile.Q + direction.DQ, tile.R + direction.DR);
            if (world.Tiles.Items.TryGetValue(neighbor, out var other) &&
                other.Flags.HasFlag(TileFlags.Walkable) &&
                !other.Flags.HasFlag(TileFlags.Water) &&
                !other.Flags.HasFlag(TileFlags.Blocked) &&
                other.Elevation == footprint.Elevation)
            {
                dryNeighbors++;
            }
        }

        return dryNeighbors >= 3;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §120: a committed player plan's own topology and furniture.
    //
    // The canonical hut can seal per HEX EDGE (RepairHutTopology/SealPerimeter)
    // because every one of its twelve bays lies on one of its own six edges. A
    // player plan cannot: its walls are unit segments of the 0.5-wu build
    // lattice and the approved draft already contains two free-standing walls
    // that belong to no hex edge at all. So the plan blocks per SECTION, on the
    // junctions that section actually stands on — the same navigation lattice,
    // the same Blocked flag, the same portal exemption, one granularity finer.
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] EnvelopeDefinitionIds =
    {
        "architecture.wall.wood", "architecture.window.wood", "architecture.door.wood"
    };

    /// <summary>
    /// Recomputes, from scratch and idempotently, which junctions a committed
    /// plan's raised envelope blocks.
    ///
    /// ⭐ The rule is the player's own, and it is ONE threshold: the moment a
    /// wall/window/door module has ANY delivered material, that module is an
    /// obstacle. Not per stage, not per stick. Floors and roofs never block (you
    /// walk on one and under the other) and supports never block — exactly as
    /// the canonical hut's six posts don't, they only own junctions the wall
    /// beside them sealed.
    ///
    /// A door's throat is never Blocked, built or not (§129): before the leaf
    /// exists the doorway is simply a hole, and afterwards a closed door is
    /// behaviour, not topology. The throat is also withheld from every OTHER
    /// module, because the sections beside a door share its seam junction and
    /// would otherwise brick the doorway shut from the side.
    /// </summary>
    public static void RepairPlanTopology(WorldState world, WorldObjectState owner)
    {
        if (world == null || owner == null) return;
        var product = string.IsNullOrEmpty(owner.BuildProduct)
            ? owner.DefinitionId
            : owner.BuildProduct;
        if (product != ContentIds.HutPlan) return;
        if (!world.Tiles.Items.ContainsKey(owner.Tile)) return;

        // §120.8: топология считается по чертежу ЭТОЙ площадки, не по committed.
        var plan = BuildingRules.PlanFor(world, owner);
        var authoredBySlot = new Dictionary<string, Runtime.Blueprints.BlueprintElementData>(
            StringComparer.Ordinal);
        foreach (var element in plan.Elements)
            authoredBySlot[Runtime.Blueprints.BlueprintBuildingPlan.SlotKey(element)] = element;

        var steps = HexSymmetrySteps(owner.RotationDegrees);
        var planAnchor = Runtime.Blueprints.BlueprintGeometry.HexCenter(
            Runtime.Blueprints.BlueprintBuildingPlan.AnchorTile(plan));
        var siteAnchor = Runtime.Blueprints.BlueprintGeometry.HexCenter(owner.Tile);
        var index = PlanJunctionIndex(world, owner);
        var pieces = BuildingRules.ArchitectureObjects(world, owner).ToArray();

        // Release everything the plan owned, then re-derive. A module that lost
        // its progress (a reset site, a save written by older rules) reopens its
        // own line and nothing else — overlapping boulders keep their block.
        var changed = false;
        changed |= ReleaseBlocked(world, owner);
        foreach (var piece in pieces)
        {
            changed |= ReleaseBlocked(world, piece);
            if (piece.DefinitionId != Core.DoorTopology.DoorDefinitionId) continue;
            foreach (var junctionId in piece.Junctions)
            {
                if (world.Junctions.Items.TryGetValue(junctionId, out var previous) && previous.Door)
                {
                    previous.Door = false;
                    changed = true;
                }
            }
        }

        // Pass 1 — the doorways, before anything is allowed to block.
        var portals = new HashSet<JunctionId>();
        foreach (var piece in pieces)
        {
            if (piece.ArchitectureElements.Count != 1 ||
                piece.DefinitionId != Core.DoorTopology.DoorDefinitionId) continue;
            var element = piece.ArchitectureElements[0];
            if (!authoredBySlot.TryGetValue(element.SlotKey, out var authored)) continue;
            var segment = WorldSegment(authored.Segment, planAnchor, siteAnchor, steps);
            JunctionId? throat = null;
            // The centre junction of the bay is the real navigation throat, and
            // TryDoorPortal is the existing answer to "which one is that".
            if (Runtime.Blueprints.BlueprintGeometry.TryDoorPortal(segment, out var portalKey) &&
                index.TryGetValue(portalKey, out var centre))
            {
                throat = centre;
            }
            else
            {
                foreach (var key in Runtime.Blueprints.BlueprintGeometry.SegmentJunctions(segment))
                {
                    if (!index.TryGetValue(key, out var fallback)) continue;
                    throat = fallback;
                    break;
                }
            }

            if (throat is not { } portalId) continue;
            // One door = one throat: DoorTopology only indexes a door piece with
            // exactly one junction, and that index is what lets a colonist open it.
            piece.Junctions.Clear();
            piece.Junctions.Add(portalId);
            portals.Add(portalId);
            var portal = world.Junctions.Items[portalId];
            WorldObjectMutations.ClearBlockingOwnershipAt(world, portalId);
            portal.Door = element.DeliveredTotal > 0;
            changed = true;
        }

        // Pass 2 — every module with progress claims the junctions it stands on.
        foreach (var piece in pieces)
        {
            if (piece.ArchitectureElements.Count != 1) continue;
            var element = piece.ArchitectureElements[0];
            if (Array.IndexOf(EnvelopeDefinitionIds, element.DefinitionId) < 0) continue;
            if (element.DeliveredTotal <= 0) continue;
            if (!authoredBySlot.TryGetValue(element.SlotKey, out var authored)) continue;
            var segment = WorldSegment(authored.Segment, planAnchor, siteAnchor, steps);
            foreach (var key in Runtime.Blueprints.BlueprintGeometry.SegmentJunctions(segment))
            {
                if (!index.TryGetValue(key, out var junctionId) ||
                    portals.Contains(junctionId) ||
                    !world.Junctions.Items.TryGetValue(junctionId, out var junction))
                {
                    continue;
                }

                var newlyBlocked = !junction.Blocked;
                junction.Blocked = true;
                if (!piece.BlockedJunctions.Contains(junctionId))
                    piece.BlockedJunctions.Add(junctionId);
                changed |= newlyBlocked;
                // §45 r5 / SealPerimeter: a girl left standing ON a junction that
                // just went solid keeps a still-valid key, so perception never
                // re-anchors her and every route reads unreachable. She is nudged
                // off instead — the wall goes up around her, not through her.
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    if (npc.CurrentJunction is not { } current || !current.Equals(junctionId)) continue;
                    npc.CurrentJunction = null;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "WallSolidUnderfoot",
                            $"Slot={element.SlotKey} Junction={junctionId.Value}");
                    }
                }
            }
        }

        if (changed) world.TopologyVersion++;
    }

    private static bool ReleaseBlocked(WorldState world, WorldObjectState holder)
    {
        return WorldObjectMutations.ReleaseOwnedBlocking(world, holder);
    }

    private static int HexSymmetrySteps(float rotationDegrees) =>
        ((int)MathF.Round(rotationDegrees / 60f) % 6 + 6) % 6;

    /// <summary>
    /// One authored build segment placed into the world: de-anchored from the
    /// plan's own hex, turned by the building's rotation, re-anchored on the
    /// site's hex. Integer lattice arithmetic throughout — no coordinate is
    /// rounded, which is why a rotated plan's walls cannot drift off the grid.
    /// </summary>
    private static Runtime.Blueprints.BuildSegmentKey WorldSegment(
        Runtime.Blueprints.BuildSegmentKey authored,
        Runtime.Blueprints.HexBuildNodeKey planAnchor,
        Runtime.Blueprints.HexBuildNodeKey siteAnchor,
        int steps)
    {
        var a = Runtime.Blueprints.BlueprintGeometry.RotateNode(
            new Runtime.Blueprints.HexBuildNodeKey(
                authored.A.Q - planAnchor.Q, authored.A.R - planAnchor.R), steps);
        var b = Runtime.Blueprints.BlueprintGeometry.RotateNode(
            new Runtime.Blueprints.HexBuildNodeKey(
                authored.B.Q - planAnchor.Q, authored.B.R - planAnchor.R), steps);
        return new Runtime.Blueprints.BuildSegmentKey(a + siteAnchor, b + siteAnchor);
    }

    /// <summary>
    /// Junction key -> live junction id over the building's footprint and one
    /// ring around it. The worldgen dictionary that built these keys is private
    /// to the factory and is gone by runtime, so the key is read back off the
    /// junction's own world position — the same integer lattice
    /// <see cref="Runtime.Blueprints.BlueprintGeometry.JunctionToWorld"/> writes.
    /// </summary>
    private static Dictionary<Runtime.Blueprints.JunctionKey, JunctionId> PlanJunctionIndex(
        WorldState world, WorldObjectState owner)
    {
        var index = new Dictionary<Runtime.Blueprints.JunctionKey, JunctionId>();
        var tiles = new List<TileCoord>();
        foreach (var footprint in FootprintTiles(world, owner))
        {
            if (!tiles.Contains(footprint)) tiles.Add(footprint);
            foreach (var direction in HexDirection.All)
            {
                var neighbor = new TileCoord(footprint.Q + direction.DQ, footprint.R + direction.DR);
                if (!tiles.Contains(neighbor)) tiles.Add(neighbor);
            }
        }

        foreach (var coord in tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile)) continue;
            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction)) continue;
                index[JunctionKeyOf(junction.WorldPosition)] = junctionId;
            }
        }

        return index;
    }

    private static Runtime.Blueprints.JunctionKey JunctionKeyOf(Float2 position) =>
        new Runtime.Blueprints.JunctionKey(
            (int)MathF.Round(position.X / (HexSpatialMath.Sqrt3 * 0.1875f)),
            (int)MathF.Round(position.Y / 0.1875f));

    /// <summary>
    /// The plan's OWN furniture, staked as ordinary build-sites so the colony
    /// raises it piece by piece like any other bed (§120).
    ///
    /// Nothing here retypes a coordinate: the placement's tile and junction slot
    /// are resolved through <see cref="HexPointLayout"/> and rotated by the same
    /// hex symmetry the footprint and the walls use. Idempotent — CompleteHut
    /// also runs on save load, and a piece already standing (or already staked)
    /// is left alone.
    /// </summary>
    public static void StakePlanFurnitureSites(WorldState world, WorldObjectState hut)
    {
        if (world == null || hut == null || hut.DefinitionId != ContentIds.HutPlan) return;
        // §120.8: мебель размечает чертёж ЭТОГО дома, не committed-план.
        var plan = BuildingRules.PlanFor(world, hut);
        var steps = HexSymmetrySteps(hut.RotationDegrees);
        var planAnchorTile = Runtime.Blueprints.BlueprintBuildingPlan.AnchorTile(plan);
        var index = PlanJunctionIndex(world, hut);
        var claimed = new HashSet<JunctionId>();

        foreach (var placement in plan.Furniture)
        {
            var product = PlanFurnitureProduct(placement.DefinitionId);
            if (product == null || !world.Content.ObjectDefinitions.ContainsKey(product)) continue;
            var tile = RotatePlanTile(placement.Tile, planAnchorTile, hut.Tile, steps);
            if (!world.Tiles.Items.ContainsKey(tile)) continue;
            var anchorKey = RotatePlanJunction(
                placement.PrimaryJunction, planAnchorTile, hut.Tile, steps);
            if (!index.TryGetValue(anchorKey, out var anchorId)) continue;
            if (!claimed.Add(anchorId)) continue;
            var existing = FindPlanFurniture(world, tile, product, anchorId);
            if (existing != null)
            {
                // Save/load repair: early constructor builds persisted finished
                // indoor furniture with an empty footprint. Re-derive it from
                // the same placement anchor/yaw instead of trusting the blob.
                if (existing.DefinitionId != ContentIds.BuildSite)
                    WorldObjectMutations.SetAuthoredFurnitureBlocking(
                        world, existing, blocked: true);
                continue;
            }

            var site = WorldObjectMutations.SpawnObject(
                world, ContentIds.BuildSite, hut.Fragment, tile, anchorId);
            site.BuildProduct = product;
            // §120.2: the plan's indoor fire is the small household hearth, and
            // the variant is what tells the view (and the save) which one it is.
            if (product == ContentIds.Campfire) site.Variant = BuildingRules.HutHearthVariant;
            site.RotationDegrees = StructurePlacement.QuantizeHexYaw(
                hut.RotationDegrees + Runtime.Blueprints.BlueprintGeometry.NormalizeSector(
                    placement.YawStep) * 60f);
            ApplyPlanFurnitureBill(site, product);
            RememberPlanSite(world, site);
        }
    }

    /// <summary>
    /// The catalog id a §120 furniture placement is BUILT as. "furniture.hearth"
    /// is a Build/Buy catalog row, not a world object — the thing that gets
    /// raised is the ordinary campfire carrying the household-hearth variant,
    /// exactly as the canonical hut's own hearth is.
    /// </summary>
    internal static string PlanFurnitureProduct(string catalogId) => catalogId switch
    {
        "furniture.hearth" => ContentIds.Campfire,
        null or "" => null,
        _ => catalogId
    };

    /// <summary>
    /// Everything the plan's OWN furniture will ask for, summed from the same
    /// bills <see cref="StakePlanFurnitureSites"/> stamps. Kept beside them for
    /// the reason <see cref="Runtime.Blueprints.BlueprintBuildingPlan.Bill"/> is:
    /// a bill computed anywhere else is a second place to forget a material.
    /// </summary>
    public static (int Logs, int Stones, int Leaves, int Sticks, int Rope, int Boards)
        PlanFurnitureBill()
    {
        int logs = 0, stones = 0, leaves = 0, sticks = 0, rope = 0, boards = 0;
        foreach (var placement in Runtime.Blueprints.CommittedBuildingPlans.PlayerHut.Furniture)
        {
            var product = PlanFurnitureProduct(placement.DefinitionId);
            if (product == null) continue;
            var scratch = new WorldObjectState();
            ApplyPlanFurnitureBill(scratch, product);
            logs += scratch.BillLogs;
            stones += scratch.BillStones;
            leaves += scratch.BillLeaves;
            sticks += scratch.BillSticks;
            rope += scratch.BillRope;
            boards += scratch.BillBoards;
        }

        return (logs, stones, leaves, sticks, rope, boards);
    }

    private static void ApplyPlanFurnitureBill(WorldObjectState site, string product) =>
        ApplyFurnitureBill(site, product, householdHearth: true);

    /// <summary>
    /// The one authoritative bill per buildable product — §120.7 player staking
    /// and §120.6 plan staking stamp sites through the same switch, so a
    /// material can only be forgotten in one place. The campfire is the single
    /// product with two bills: the plan's indoor hearth versus the ordinary
    /// outdoor ring.
    /// </summary>
    internal static void ApplyFurnitureBill(
        WorldObjectState site, string product, bool householdHearth)
    {
        switch (product)
        {
            case ContentIds.BedBasic:
                site.BillLogs = SimBalance.BedBasicBillLogs;
                site.BillSticks = SimBalance.BedBasicBillSticks;
                site.BillRope = SimBalance.BedBasicBillRope;
                site.BillLeaves = SimBalance.BedBasicBillLeaves;
                break;
            case ContentIds.Campfire when householdHearth:
                site.BillSticks = SimBalance.HutHearthBillSticks;
                site.BillStones = SimBalance.HutHearthBillStones;
                site.BillRope = SimBalance.HutHearthBillRope;
                break;
            case ContentIds.Campfire:
                site.BillSticks = SimBalance.CampfireBillSticks;
                site.BillStones = SimBalance.CampfireBillStones;
                site.BillRope = SimBalance.CampfireBillRope;
                break;
            case ContentIds.Wardrobe:
                site.BillBoards = SimBalance.WardrobeBillBoards;
                site.BillSticks = SimBalance.WardrobeBillSticks;
                site.BillRope = SimBalance.WardrobeBillRope;
                break;
            case ContentIds.Workbench:
                site.BillSticks = Spec119.WorkbenchBillSticks;
                site.BillBoards = Spec119.WorkbenchBillBoards;
                site.BillRope = Spec119.WorkbenchBillRope;
                break;
            case ContentIds.DryingRack:
                site.BillSticks = SimBalance.RackBillSticks;
                site.BillRope = SimBalance.RackBillRope;
                break;
            case ContentIds.WaterCollector:
                site.BillSticks = SimBalance.WaterCollectorBillSticks;
                site.BillStones = SimBalance.WaterCollectorBillStones;
                site.BillRope = SimBalance.WaterCollectorBillRope;
                site.BillLeaves = SimBalance.WaterCollectorBillLeaves;
                break;
        }
    }

    private static WorldObjectState FindPlanFurniture(
        WorldState world, TileCoord tile, string product, JunctionId anchor)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objects)) return null;
        foreach (var id in objects)
        {
            if (!world.Entities.Objects.TryGetValue(id, out var candidate)) continue;
            var isProduct = candidate.DefinitionId == product ||
                            candidate.BuildProduct == product;
            if (isProduct && candidate.Junctions.Contains(anchor)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// The colony has to KNOW a site to haul to it — the same handoff
    /// BedSiteSystem performs when it stakes a bed by the fire.
    /// </summary>
    internal static void RememberPlanSite(WorldState world, WorldObjectState site)
    {
        var junction = site.Junctions.Count > 0 ? site.Junctions[0] : (JunctionId?)null;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Faction != Faction.Colony) continue;
            npc.Memory.KnownObjects[site.Id] = new Memory.ObjectMemory
            {
                Id = site.Id,
                DefinitionId = site.DefinitionId,
                Tile = site.Tile,
                Junction = junction,
                IsPermanent = true,
                LastSeenTick = world.Tick
            };
            npc.Memory.Version++;
        }
    }

    private static TileCoord RotatePlanTile(
        TileCoord planTile, TileCoord planAnchor, TileCoord siteAnchor, int steps)
    {
        var q = planTile.Q - planAnchor.Q;
        var r = planTile.R - planAnchor.R;
        for (var step = 0; step < steps; step++)
        {
            var rotatedQ = -r;
            r = q + r;
            q = rotatedQ;
        }

        return new TileCoord(siteAnchor.Q + q, siteAnchor.R + r);
    }

    private static Runtime.Blueprints.JunctionKey RotatePlanJunction(
        Runtime.Blueprints.JunctionKey planJunction,
        TileCoord planAnchor,
        TileCoord siteAnchor,
        int steps)
    {
        var from = HexPointLayout.GetJunctionKeyPair(planAnchor, new AxialPoint(0, 0));
        var to = HexPointLayout.GetJunctionKeyPair(siteAnchor, new AxialPoint(0, 0));
        var offset = Runtime.Blueprints.BlueprintGeometry.RotateJunctionOffset(
            new Runtime.Blueprints.JunctionKey(
                planJunction.XKey - from.xKey, planJunction.YKey - from.yKey), steps);
        return new Runtime.Blueprints.JunctionKey(
            to.xKey + offset.XKey, to.yKey + offset.YKey);
    }

    /// <summary>
    /// Rebuilds the footprint from its persisted LEGO elements. Used both at
    /// completion and after loading an older save whose visual door and saved
    /// junction flags may have been authored by different rules.
    /// </summary>
    public static void RepairHutTopology(WorldState world, WorldObjectState hut)
    {
        if (hut == null || hut.DefinitionId != ContentIds.Hut1Hex ||
            !world.Tiles.Items.TryGetValue(hut.Tile, out var tile)) return;

        WorldObjectMutations.ReleaseOwnedBlocking(world, hut);
        var architecture = BuildingRules.ArchitectureObjects(world, hut).ToArray();
        foreach (var piece in architecture)
        {
            WorldObjectMutations.ReleaseOwnedBlocking(world, piece);
            AnchorArchitecturePiece(world, hut, piece);
        }
        foreach (var junctionId in tile.Junctions)
        {
            if (world.Junctions.Items.TryGetValue(junctionId, out var junction))
                junction.Door = false;
        }

        var local = BuildingRules.DoorLocalCenter(world, hut);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var doorCenter = HexSpatialMath.TileToWorld(hut.Tile) + new Float2(
            local.X * cos - local.Y * sin,
            local.X * sin + local.Y * cos);
        var delta = doorCenter - HexSpatialMath.TileToWorld(hut.Tile);
        var doorYaw = MathF.Atan2(delta.Y, delta.X) * 180f / MathF.PI;
        var doorEdge = DoorEdgeForYaw(doorYaw);
        hut.Variant = $"door:{doorEdge}";
        SealPerimeter(world, hut, architecture, doorEdge, doorCenter);
    }

    private static void SealPerimeter(
        WorldState world, WorldObjectState hut, WorldObjectState[] architecture,
        int doorEdge, Float2 doorCenter)
    {
        var tile = world.Tiles.Items[hut.Tile];
        var portals = new HashSet<JunctionId>();
        for (var edge = 0; edge < HexDirection.All.Length; edge++)
        {
            var direction = HexDirection.All[edge];
            var neighbor = new TileCoord(hut.Tile.Q + direction.DQ, hut.Tile.R + direction.DR);
            if (edge == doorEdge)
            {
                var candidates = new List<JunctionId>();
                foreach (var junctionId in tile.Junctions)
                {
                    if (world.Junctions.Items.TryGetValue(junctionId, out var candidate) &&
                        candidate.Tiles.Count >= 2 && candidate.Tiles.Contains(neighbor))
                    {
                        candidates.Add(junctionId);
                    }
                }

                candidates.Sort((a, b) =>
                {
                    // Three junctions nearest the actual half-edge door module,
                    // not three nearest the abstract centre of the whole edge.
                    var ap = world.Junctions.Items[a].WorldPosition - doorCenter;
                    var bp = world.Junctions.Items[b].WorldPosition - doorCenter;
                    return (ap.X * ap.X + ap.Y * ap.Y).CompareTo(
                        bp.X * bp.X + bp.Y * bp.Y);
                });
                // One half-edge door has one actual navigation throat: its
                // centre junction. The neighbouring junctions belong to the
                // jamb/frame and stay blocked like the rest of the wall.
                //
                // §129: the portal is NEVER Blocked, closed leaf or not — a
                // closed door is behaviour (DoorStateVersion + the door caches),
                // not topology. Running for every hut on every load, this line
                // is also the entire save migration: a pre-§129 blob that
                // stored the portal Blocked is healed right here.
                for (var i = 0; i < Math.Min(1, candidates.Count); i++)
                {
                    var portalId = candidates[i];
                    portals.Add(portalId);
                    var portal = world.Junctions.Items[portalId];
                    WorldObjectMutations.ClearBlockingOwnershipAt(world, portalId);
                    portal.Door = true;
                }
                var doorPiece = architecture.FirstOrDefault(piece =>
                    piece.DefinitionId == "architecture.door.wood");
                if (doorPiece != null)
                {
                    doorPiece.Junctions.Clear();
                    doorPiece.Junctions.AddRange(portals);
                }
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (portals.Contains(junctionId)) continue;
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                    junction.Tiles.Count < 2 || !junction.Tiles.Contains(neighbor))
                {
                    continue;
                }

                var newlyBlocked = !junction.Blocked;
                junction.Blocked = true;
                var blocker = NearestBlockingPiece(world, hut, architecture, junction.WorldPosition);
                if (blocker != null && !blocker.BlockedJunctions.Contains(junctionId))
                    blocker.BlockedJunctions.Add(junctionId);
                if (!newlyBlocked) continue;
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    if (npc.CurrentJunction is { } current && current.Equals(junctionId))
                    {
                        npc.CurrentJunction = null;
                    }
                }
            }
        }

        world.TopologyVersion++;
    }

    private static void AnchorArchitecturePiece(
        WorldState world, WorldObjectState hut, WorldObjectState piece)
    {
        var element = piece.ArchitectureElements.Count == 1
            ? piece.ArchitectureElements[0]
            : null;
        if (element == null) return;
        var target = ArchitectureWorldPosition(hut, element);
        var nearest = world.Tiles.Items[hut.Tile].Junctions
            .Where(id => world.Junctions.Items.ContainsKey(id))
            .OrderBy(id => DistanceSquared(world.Junctions.Items[id].WorldPosition, target))
            .FirstOrDefault();
        piece.Junctions.Clear();
        piece.Junctions.Add(nearest);
    }

    private static WorldObjectState NearestBlockingPiece(
        WorldState world, WorldObjectState hut, WorldObjectState[] pieces, Float2 position)
    {
        WorldObjectState best = null;
        var bestDistance = float.MaxValue;
        foreach (var piece in pieces)
        {
            if (piece.ArchitectureElements.Count != 1) continue;
            var element = piece.ArchitectureElements[0];
            if (!element.Complete || element.DefinitionId is not
                    ("architecture.wall.wood" or "architecture.window.wood" or
                     "architecture.door.wood" or "architecture.support.wood"))
                continue;
            var distance = DistanceSquared(position, ArchitectureWorldPosition(hut, element));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = piece;
        }
        return best;
    }

    private static Float2 ArchitectureWorldPosition(
        WorldObjectState hut, ArchitectureElementState element)
    {
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return HexSpatialMath.TileToWorld(hut.Tile) + new Float2(
            element.LocalX * cos - element.LocalZ * sin,
            element.LocalX * sin + element.LocalZ * cos);
    }

    private static float DistanceSquared(Float2 a, Float2 b)
    {
        var delta = a - b;
        return delta.X * delta.X + delta.Y * delta.Y;
    }

    private static void SpawnCot(WorldState world, WorldObjectState hut, int slot)
    {
        if (CotCount(world, hut.Tile) >= 2) return;
        if (FindCotJunction(world, hut, CotLocalPosition(slot)) is not { } junctionId) return;
        var cot = WorldObjectMutations.SpawnObject(
            world, ContentIds.BedBasic, hut.Fragment, hut.Tile, junctionId);
        cot.Variant = ContentIds.HutBedVariant;
        cot.RotationDegrees = StructurePlacement.QuantizeHexYaw(
            hut.RotationDegrees + CotLocalYaw(slot));
        // The route/interaction anchor is not the visible bed centre. Apply the
        // authored rectangle around the committed centre so NPCs can approach
        // from the corridor but can never stand inside the frame (#154).
        WorldObjectMutations.SetAuthoredFurnitureBlocking(
            world, cot, blocked: true, FurnitureWorldPosition(hut, CotLocalPosition(slot)));
    }

    private static JunctionId? FindCotJunction(WorldState world, WorldObjectState hut, Float2 local)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var desired = center + RotateLocal(local, radians);
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked ||
                CotAlreadyUses(world, hut.Tile, junctionId))
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y > 0.9f * 0.9f) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    private static bool CotAlreadyUses(WorldState world, TileCoord tile, JunctionId junction)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objects)) return false;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == ContentIds.BedBasic &&
                obj.Variant == ContentIds.HutBedVariant && obj.Junctions.Contains(junction))
            {
                return true;
            }
        }

        return false;
    }

    private static int CotCount(WorldState world, TileCoord tile)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objects)) return 0;
        var count = 0;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == ContentIds.BedBasic &&
                obj.Variant == ContentIds.HutBedVariant)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Old saves may contain both former <c>building.hut_bed</c> objects on the
    /// same junction. Re-seat every integrated pair on the two authored wall
    /// sides while keeping ids, ownership and interaction state intact.
    /// </summary>
    public static void RepairIntegratedCotAnchors(WorldState world)
    {
        foreach (var hut in world.Entities.Objects.Values)
        {
            if (hut.DefinitionId != ContentIds.Hut1Hex ||
                !world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objectIds))
            {
                continue;
            }

            var cots = new List<WorldObjectState>();
            foreach (var id in objectIds)
            {
                if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                    obj.DefinitionId == ContentIds.BedBasic &&
                    obj.Variant == ContentIds.HutBedVariant)
                {
                    WorldObjectMutations.SetAuthoredFurnitureBlocking(world, obj, blocked: false);
                    cots.Add(obj);
                }
            }

            if (cots.Count == 0) continue;
            cots.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            var used = new HashSet<JunctionId>();
            for (var i = 0; i < cots.Count && i < 2; i++)
            {
                if (FindCotRepairJunction(world, hut, CotLocalPosition(i), used) is not { } anchor) continue;
                cots[i].Junctions.Clear();
                cots[i].Junctions.Add(anchor);
                cots[i].RotationDegrees = StructurePlacement.QuantizeHexYaw(
                    hut.RotationDegrees + CotLocalYaw(i));
                WorldObjectMutations.SetAuthoredFurnitureBlocking(
                    world, cots[i], blocked: true,
                    FurnitureWorldPosition(hut, CotLocalPosition(i)));
                used.Add(anchor);
            }
        }
    }

    private static JunctionId? FindCotRepairJunction(
        WorldState world, WorldObjectState hut, Float2 local, HashSet<JunctionId> used)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var desired = center + RotateLocal(local, radians);
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (used.Contains(junctionId) ||
                !world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked)
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y > 0.9f * 0.9f) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    private static Float2 CotLocalPosition(int slot) => slot == 0
        ? new Float2(BuildingRules.HutBed0LocalX, BuildingRules.HutBed0LocalZ)
        : new Float2(BuildingRules.HutBed1LocalX, BuildingRules.HutBed1LocalZ);

    private static float CotLocalYaw(int slot)
    {
        var bed = CotLocalPosition(slot);
        var hearth = new Float2(BuildingRules.HutHearthLocalX, BuildingRules.HutHearthLocalZ);
        var towardHearth = hearth - bed;
        var yaw = slot == 0 ? BuildingRules.HutBed0LocalYaw : BuildingRules.HutBed1LocalYaw;
        var radians = yaw * MathF.PI / 180f;
        // Footprint yaw maps to Unity yaw=-yaw. The sleep head is -Unity-forward,
        // hence its simulation X/Y direction is (sin(yaw), -cos(yaw)). Choose
        // the bed axis' 180° symmetry that points that head toward the hearth.
        var head = new Float2(MathF.Sin(radians), -MathF.Cos(radians));
        if (head.X * towardHearth.X + head.Y * towardHearth.Y < 0f) yaw += 180f;
        return StructurePlacement.QuantizeHexYaw(yaw);
    }

    private static Float2 RotateLocal(Float2 local, float radians) => new(
        local.X * MathF.Cos(radians) - local.Y * MathF.Sin(radians),
        local.X * MathF.Sin(radians) + local.Y * MathF.Cos(radians));

    private static Float2 FurnitureWorldPosition(WorldObjectState hut, Float2 local)
    {
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        return HexSpatialMath.TileToWorld(hut.Tile) + RotateLocal(local, radians);
    }

    private static void SpawnHearth(WorldState world, WorldObjectState hut)
    {
        if (world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects))
        {
            foreach (var id in objects)
            {
                if (world.Entities.Objects.TryGetValue(id, out var existing) &&
                    existing.DefinitionId == ContentIds.Campfire &&
                    existing.Variant == BuildingRules.HutHearthVariant)
                {
                    return;
                }
            }
        }

        if (FindHearthJunction(world, hut) is not { } junctionId) return;
        var hearth = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, hut.Fragment, hut.Tile, junctionId);
        hearth.Variant = BuildingRules.HutHearthVariant;
        hearth.RotationDegrees = hut.RotationDegrees;
        hearth.ResourceAmount = 0f;
        AddContents(hearth, ContentIds.Stick, SimBalance.HutHearthBillSticks);
        AddContents(hearth, ContentIds.Rope, SimBalance.HutHearthBillRope);
        AddContents(hearth, ContentIds.Stone, SimBalance.HutHearthBillStones);

        // A normal outdoor campfire owns a seven-node disc. The authored
        // household hearth owns exactly its single physical anchor.
        WorldObjectMutations.SetAuthoredFurnitureBlocking(world, hearth, blocked: true);
    }

    /// <summary>
    /// Re-seats the one household hearth from canonical hut-local geometry.
    /// This deliberately repairs current-version saves too: early v33 builds
    /// persisted the former 90-degree furniture basis.
    /// </summary>
    public static void RepairIntegratedHearthAnchor(WorldState world, WorldObjectState hut)
    {
        if (hut == null || hut.DefinitionId != ContentIds.Hut1Hex ||
            !world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects)) return;

        WorldObjectState hearth = null;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var candidate) &&
                candidate.DefinitionId == ContentIds.Campfire)
            {
                hearth = candidate;
                if (candidate.Variant == BuildingRules.HutHearthVariant) break;
            }
        }

        if (hearth == null)
        {
            SpawnHearth(world, hut);
            return;
        }

        WorldObjectMutations.SetAuthoredFurnitureBlocking(world, hearth, blocked: false);
        hearth.Junctions.Clear();
        hearth.Variant = BuildingRules.HutHearthVariant;
        hearth.RotationDegrees = hut.RotationDegrees;

        if (FindHearthJunction(world, hut) is not { } junctionId) return;
        hearth.Junctions.Add(junctionId);
        WorldObjectMutations.SetAuthoredFurnitureBlocking(world, hearth, blocked: true);
    }

    private static JunctionId? FindHearthJunction(WorldState world, WorldObjectState hut)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var desired = center + RotateLocal(
            new Float2(BuildingRules.HutHearthLocalX, BuildingRules.HutHearthLocalZ), radians);
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked ||
                InteriorObjectUses(world, hut, junctionId))
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y > 0.9f * 0.9f) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    /// <summary>
    /// §133: домашний гардероб у свободной стены. Его authored line блокирует
    /// только три физических узла; дверной коридор остаётся снаружи footprint.
    /// </summary>
    private static void SpawnWardrobe(WorldState world, WorldObjectState hut)
    {
        if (FindWardrobe(world, hut) != null) return;
        if (FindWardrobeJunction(world, hut) is not { } junctionId) return;

        var wardrobe = WorldObjectMutations.SpawnObject(
            world, ContentIds.Wardrobe, hut.Fragment, hut.Tile, junctionId);
        wardrobe.RotationDegrees = StructurePlacement.QuantizeHexYaw(
            hut.RotationDegrees + BuildingRules.HutWardrobeLocalYaw);
        WorldObjectMutations.SetAuthoredFurnitureBlocking(world, wardrobe, blocked: true);

        SpawnMedkit(world, hut, wardrobe);
    }

    /// <summary>
    /// ⭐ §118.2: аптечка у гардероба — домашний запас медицины.
    ///
    /// Садится на свободный внутренний джанкшен 10, на один шаг сетки ближе к
    /// центру от pivot гардероба. Трёхузловой authored footprint шкафа 9→4→0
    /// остаётся свободен от ящика; сама аптечка obstacle не ставит.
    ///
    /// Внутри — расходники: сто пластырей и двадцать бинтов. Пластырь закрывает
    /// одну рану, бинт перевязывает зону целиком, отсюда и разница в числе.
    /// </summary>
    private static void SpawnMedkit(
        WorldState world, WorldObjectState hut, WorldObjectState wardrobe)
    {
        if (FindMedkit(world, hut) != null) return;
        if (FindMedkitJunction(world, hut, wardrobe) is not { } junctionId) return;

        var medkit = WorldObjectMutations.SpawnObject(
            world, ContentIds.MedkitBox, hut.Fragment, hut.Tile, junctionId);
        medkit.RotationDegrees = wardrobe.RotationDegrees;
        WorldObjectMutations.SetObstacleBlocking(world, medkit, blocked: false);

        for (var i = 0; i < 100; i++)
        {
            medkit.Contents.Add(new Agents.ItemInstance(ContentIds.Plaster));
        }

        for (var i = 0; i < 20; i++)
        {
            medkit.Contents.Add(new Agents.ItemInstance(ContentIds.Bandage));
        }
    }

    /// <summary>
    /// §133.2 r2: provisions only the fresh-game hut with six distinct loose
    /// garments. The sequence is a stateless world-seeded draw, so recreating
    /// a seed produces the same starter wardrobe; save repair and normal hut
    /// construction intentionally never call this method.
    /// </summary>
    private static void SeedStarterWardrobeGarments(WorldState world, WorldObjectState hut)
    {
        var wardrobe = FindWardrobe(world, hut);
        if (wardrobe == null || wardrobe.Junctions.Count != 1) return;

        // §133.2 r2: six useful choices, with three guaranteed roles: real
        // trousers (stable pants/trouser/jeans/legging id vocabulary), boots,
        // and one protective non-leg piece. The other three come from substantial
        // everyday/outer clothing — never underwear, accessories or bags.
        // Sorting is part of the deterministic-random contract: catalog
        // registration order must not change what a given seed grants.
        var generalCandidates = new List<string>();
        var pantsCandidates = new List<string>();
        var bootCandidates = new List<string>();
        var protectiveCandidates = new List<string>();
        foreach (var garment in GarmentLibrary.Spawnable)
        {
            if (garment == null || garment.Sex == GarmentSex.Male ||
                !world.Content.ObjectDefinitions.TryGetValue(garment.Id, out var definition) ||
                !definition.HasTag("Clothing") ||
                !IsStarterWardrobeGarment(garment, out var isFootwear))
            {
                continue;
            }

            if (isFootwear)
            {
                if (IsStarterWardrobeBoots(garment)) bootCandidates.Add(garment.Id);
            }
            else
            {
                generalCandidates.Add(garment.Id);
                if (IsStarterWardrobePants(garment))
                {
                    pantsCandidates.Add(garment.Id);
                }
            }

            if (IsStarterWardrobeProtective(garment))
            {
                protectiveCandidates.Add(garment.Id);
            }
        }
        generalCandidates.Sort(StringComparer.Ordinal);
        pantsCandidates.Sort(StringComparer.Ordinal);
        bootCandidates.Sort(StringComparer.Ordinal);
        protectiveCandidates.Sort(StringComparer.Ordinal);

        var selected = new List<string>(StarterWardrobeGarmentCount);
        DrawMandatoryStarterGarment(world, hut, pantsCandidates, selected, 13303);
        DrawMandatoryStarterGarment(world, hut, bootCandidates, selected, 13304);
        DrawMandatoryStarterGarment(world, hut, protectiveCandidates, selected, 13305);

        generalCandidates.RemoveAll(selected.Contains);
        while (selected.Count < StarterWardrobeGarmentCount && generalCandidates.Count > 0)
        {
            selected.Add(DrawStarterWardrobeGarment(world, hut, generalCandidates, 13306));
        }

        foreach (var definitionId in selected)
        {
            if (!world.Content.ObjectDefinitions.ContainsKey(definitionId)) continue;

            var garment = WorldObjectMutations.SpawnObject(
                world, definitionId, wardrobe.Fragment, wardrobe.Tile, wardrobe.Junctions[0]);
            // Hanging clothes are stored at the wardrobe's logical junction;
            // they never add a navigation obstacle to the one-hex room.
            WorldObjectMutations.SetObstacleBlocking(world, garment, blocked: false);
        }
    }

    internal static bool IsStarterWardrobeGarment(GarmentParams garment, out bool isFootwear)
    {
        isFootwear = false;
        if (garment == null || garment.Sex == GarmentSex.Male ||
            garment.Layer is WearLayer.Underwear or WearLayer.Bags)
        {
            return false;
        }

        isFootwear = garment.Category == GarmentCategory.Footwear;
        return garment.Category is GarmentCategory.Top or GarmentCategory.Bottom or
            GarmentCategory.Dress or GarmentCategory.Outerwear or
            GarmentCategory.Footwear or GarmentCategory.Armwear or
            GarmentCategory.Legwear or GarmentCategory.Outfit;
    }

    internal static bool IsStarterWardrobePants(GarmentParams garment) =>
        garment != null && garment.Category == GarmentCategory.Bottom &&
        garment.Capacity >= 4 &&
        (garment.Id.IndexOf("pants", StringComparison.OrdinalIgnoreCase) >= 0 ||
         garment.Id.IndexOf("trouser", StringComparison.OrdinalIgnoreCase) >= 0 ||
         garment.Id.IndexOf("jeans", StringComparison.OrdinalIgnoreCase) >= 0 ||
         garment.Id.IndexOf("legging", StringComparison.OrdinalIgnoreCase) >= 0);

    internal static bool IsStarterWardrobeBoots(GarmentParams garment) =>
        garment != null && garment.Category == GarmentCategory.Footwear &&
        garment.Id.IndexOf("boot", StringComparison.OrdinalIgnoreCase) >= 0;

    internal static bool IsStarterWardrobeProtective(GarmentParams garment) =>
        garment != null && garment.Armor >= StarterWardrobeGoodArmor &&
        garment.Category is not GarmentCategory.Bottom and not GarmentCategory.Footwear;

    private static void DrawMandatoryStarterGarment(
        WorldState world,
        WorldObjectState hut,
        List<string> candidates,
        List<string> selected,
        int salt)
    {
        if (candidates.Count == 0) return;
        var definitionId = DrawStarterWardrobeGarment(world, hut, candidates, salt);
        if (!selected.Contains(definitionId)) selected.Add(definitionId);
    }

    private static string DrawStarterWardrobeGarment(
        WorldState world,
        WorldObjectState hut,
        List<string> candidates,
        int salt)
    {
        var roll = MathUtil.Hash01(world.Seed, hut.Id.Value, candidates.Count, salt);
        var index = Math.Min(candidates.Count - 1, (int)(roll * candidates.Count));
        var definitionId = candidates[index];
        candidates.RemoveAt(index);
        return definitionId;
    }

    /// <summary>
    /// Идемпотентная пересадка гардероба из канонической локальной геометрии —
    /// и его появление в домах из старых сейвов, где его ещё не было.
    /// </summary>
    public static void RepairWardrobeAnchor(WorldState world, WorldObjectState hut)
    {
        if (hut == null || hut.DefinitionId != ContentIds.Hut1Hex ||
            !world.Caches.ObjectsByTile.ContainsKey(hut.Tile)) return;

        var wardrobe = FindWardrobe(world, hut);
        if (wardrobe == null)
        {
            SpawnWardrobe(world, hut);
            return;
        }

        var storedGarments = new List<WorldObjectState>();
        var storedGarmentIds = new HashSet<ObjectId>();
        if (wardrobe.Junctions.Count > 0)
        {
            var oldJunction = wardrobe.Junctions[0];
            foreach (var candidate in world.Entities.Objects.Values)
            {
                if (candidate.Id.Equals(wardrobe.Id) ||
                    !candidate.Junctions.Contains(oldJunction) ||
                    !GarmentLibrary.Active.Any(garment => garment.Id == candidate.DefinitionId))
                    continue;

                storedGarments.Add(candidate);
                storedGarmentIds.Add(candidate.Id);
            }
        }

        WorldObjectMutations.SetAuthoredFurnitureBlocking(world, wardrobe, blocked: false);
        wardrobe.Junctions.Clear();
        wardrobe.RotationDegrees = StructurePlacement.QuantizeHexYaw(
            hut.RotationDegrees + BuildingRules.HutWardrobeLocalYaw);

        if (FindWardrobeJunction(world, hut, storedGarmentIds) is not { } junctionId) return;
        wardrobe.Junctions.Add(junctionId);
        WorldObjectMutations.SetAuthoredFurnitureBlocking(world, wardrobe, blocked: true);
        foreach (var garment in storedGarments)
        {
            garment.Junctions.Clear();
            garment.Junctions.Add(junctionId);
            garment.Fragment = wardrobe.Fragment;
            garment.Tile = wardrobe.Tile;
            garment.RotationDegrees = wardrobe.RotationDegrees;
        }
        RepairMedkitAnchor(world, hut, wardrobe);
        world.TopologyVersion++;
    }

    private static void RepairMedkitAnchor(
        WorldState world, WorldObjectState hut, WorldObjectState wardrobe)
    {
        var medkit = FindMedkit(world, hut);
        if (medkit == null)
        {
            SpawnMedkit(world, hut, wardrobe);
            return;
        }

        var ignored = new HashSet<ObjectId> { medkit.Id };
        if (FindMedkitJunction(world, hut, wardrobe, ignored) is not { } junctionId) return;

        foreach (var blockedId in medkit.BlockedJunctions)
        {
            if (world.Junctions.Items.TryGetValue(blockedId, out var blocked)) blocked.Blocked = false;
        }
        medkit.BlockedJunctions.Clear();
        medkit.Junctions.Clear();
        medkit.Junctions.Add(junctionId);
        medkit.Fragment = hut.Fragment;
        medkit.Tile = hut.Tile;
        medkit.RotationDegrees = wardrobe.RotationDegrees;
        WorldObjectMutations.SetObstacleBlocking(world, medkit, blocked: false);
    }

    private static WorldObjectState FindMedkit(WorldState world, WorldObjectState hut)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects)) return null;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var candidate) &&
                candidate.DefinitionId == ContentIds.MedkitBox)
            {
                return candidate;
            }
        }

        return null;
    }

    private static JunctionId? FindMedkitJunction(
        WorldState world,
        WorldObjectState hut,
        WorldObjectState wardrobe = null,
        ISet<ObjectId> ignoredObjects = null)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        // §118.2: «на один шаг сетки ближе к центру от pivot гардероба».
        // §120.3 r2: у планового дома шкаф стоит там, куда его поставил ЧЕРТЁЖ,
        // поэтому желанная точка выводится от фактического pivot шкафа, а
        // авторские координаты кита остаются fallback'ом для легаси-хижины.
        Float2 desired;
        if (wardrobe != null && wardrobe.Junctions.Count == 1 &&
            world.Junctions.Items.TryGetValue(wardrobe.Junctions[0], out var pivot))
        {
            var toCenter = center - pivot.WorldPosition;
            var length = MathF.Sqrt(toCenter.X * toCenter.X + toCenter.Y * toCenter.Y);
            desired = length > 0.001f
                ? pivot.WorldPosition + new Float2(
                    toCenter.X / length * 0.375f, toCenter.Y / length * 0.375f)
                : pivot.WorldPosition;
        }
        else
        {
            desired = center + RotateLocal(
                new Float2(BuildingRules.HutMedkitLocalX, BuildingRules.HutMedkitLocalZ), radians);
        }
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked ||
                InteriorObjectUses(world, hut, junctionId, ignoredObjects) ||
                IsWardrobeFootprintJunction(center, radians, candidate.WorldPosition))
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            const float interiorTemplateRadius = 1.1251f;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y >
                interiorTemplateRadius * interiorTemplateRadius) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    private static bool IsWardrobeFootprintJunction(
        Float2 tileCenter, float hutRadians, Float2 worldPosition)
    {
        foreach (var template in HexPointLayout.GetInteriorTemplates())
        {
            if (template.Slot != 9 && template.Slot != 4 && template.Slot != 0) continue;
            var expected = tileCenter + RotateLocal(template.Offset, hutRadians);
            var delta = worldPosition - expected;
            if (delta.X * delta.X + delta.Y * delta.Y < 0.0001f * 0.0001f) return true;
        }

        return false;
    }

    private static WorldObjectState FindWardrobe(WorldState world, WorldObjectState hut)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects)) return null;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var candidate) &&
                candidate.DefinitionId == ContentIds.Wardrobe)
            {
                return candidate;
            }
        }

        return null;
    }

    private static JunctionId? FindWardrobeJunction(
        WorldState world,
        WorldObjectState hut,
        ISet<ObjectId> ignoredObjects = null)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var desired = center + RotateLocal(
            new Float2(BuildingRules.HutWardrobeLocalX, BuildingRules.HutWardrobeLocalZ), radians);
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked ||
                InteriorObjectUses(world, hut, junctionId, ignoredObjects))
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            // The approved wall-side pivot is interior junction 4 at radius
            // sqrt(0.3247595² + 0.9375²) ~= 0.992 wu. The former 0.9-wu
            // cutoff silently rejected that exact designer point and snapped
            // production inward. Radius 1.125 is the outer ring of the 37
            // interior templates; boundary junctions remain excluded.
            const float interiorTemplateRadius = 1.1251f;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y >
                interiorTemplateRadius * interiorTemplateRadius) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    private static bool InteriorObjectUses(
        WorldState world,
        WorldObjectState hut,
        JunctionId junction,
        ISet<ObjectId> ignoredObjects = null)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects)) return false;
        foreach (var id in objects)
        {
            if (id.Equals(hut.Id) || ignoredObjects?.Contains(id) == true) continue;
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.Junctions.Contains(junction))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddContents(WorldObjectState target, string definitionId, int count)
    {
        for (var i = 0; i < count; i++) target.Contents.Add(new ItemInstance(definitionId));
    }

    private static int DoorEdgeForYaw(float yaw)
    {
        var radians = yaw * MathF.PI / 180f;
        var forward = new Float2(MathF.Cos(radians), MathF.Sin(radians));
        var bestEdge = 0;
        var bestDot = float.MinValue;
        for (var edge = 0; edge < HexDirection.All.Length; edge++)
        {
            var direction = HexDirection.All[edge];
            var offset = HexSpatialMath.Normalize(
                HexSpatialMath.TileToWorld(new TileCoord(direction.DQ, direction.DR)));
            var dot = offset.X * forward.X + offset.Y * forward.Y;
            if (dot > bestDot)
            {
                bestDot = dot;
                bestEdge = edge;
            }
        }

        return bestEdge;
    }
}

}
