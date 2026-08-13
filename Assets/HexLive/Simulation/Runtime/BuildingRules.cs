using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

public enum BuildingElementKind
{
    Support,
    Floor,
    Wall,
    Window,
    Door,
    Roof
}

/// <summary>Deterministic per-module construction state derived from one site's delivered pool.</summary>
public sealed class BuildingElementProgress
{
    public string Key { get; internal set; } = string.Empty;
    public BuildingElementKind Kind { get; internal set; }
    public int Index { get; internal set; }
    public float Progress { get; internal set; }
    public bool Buildable { get; internal set; }
    public bool Complete => Progress >= 0.9999f;
}

/// <summary>
/// Shared simulation/presentation grammar for the first architectural kit.
/// A hut is thirty independent modules, not three monolithic reveal stages.
/// The allocation order is stable-random per site, so save/wire only need the
/// existing delivered material counts while every module keeps its own state.
/// </summary>
public static class BuildingRules
{
    public const string HutHearthVariant = "hut-hearth";
    public const int SupportCount = 6;
    public const int FloorElementCount = 6;
    public const int BayCount = 12;
    public const int RoofElementCount = 6;
    public const int HutDoorBay = 7;

    // Player-authored HutTest blueprint v2, snapped to the production junction grid.
    public const float HutBed0LocalX = -0.974279f;
    public const float HutBed0LocalZ = 0f;
    public const float HutBed0LocalYaw = 0f;
    public const float HutBed1LocalX = 0.487139f;
    public const float HutBed1LocalZ = 0.84375f;
    public const float HutBed1LocalYaw = 60f;
    // §133: гардероб у свободной стены. Эти данные будут заменены точным
    // экспортом из HutLayoutDesigner после утверждения игроком.
    public const float HutWardrobeLocalX = -0.487139f;
    public const float HutWardrobeLocalZ = -0.84375f;
    public const float HutWardrobeLocalYaw = 60f;

    public const float HutHearthLocalX = -0.3248f;
    public const float HutHearthLocalZ = 0.5625f;

    /// <summary>
    /// Exact visual/attach centre of the integrated bed nearest its navigation
    /// anchor. The authored four-corner centre lies between junctions, so the
    /// route anchor and the lying-body centre are deliberately different.
    /// Shared by simulation and presentation to keep aid/loot geometry on the
    /// rendered sleeper rather than on the approach grid point.
    /// </summary>
    public static Float2 HutBedVisualPosition(
        Float2 tileCenter, float hutRotationDegrees, Float2 interactionAnchor)
    {
        var radians = hutRotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        Float2 Target(float x, float z) => tileCenter + new Float2(
            x * cos - z * sin,
            x * sin + z * cos);

        var first = Target(HutBed0LocalX, HutBed0LocalZ);
        var second = Target(HutBed1LocalX, HutBed1LocalZ);
        var d0 = interactionAnchor - first;
        var d1 = interactionAnchor - second;
        return d0.X * d0.X + d0.Y * d0.Y <= d1.X * d1.X + d1.Y * d1.Y
            ? first
            : second;
    }

    public const int FrameSticks = 28;
    public const int FrameBoards = 6;
    public const int EnclosureSticks = 6;
    public const int EnclosureBoards = 25;
    public const int EnclosureRope = 4;
    public const int RoofLeaves = 12;

    public const int TotalSticks = FrameSticks + EnclosureSticks;
    public const int TotalBoards = FrameBoards + EnclosureBoards;
    public const int TotalRope = EnclosureRope;
    public const int TotalLeaves = RoofLeaves;

    private sealed class Definition
    {
        public string Key = string.Empty;
        public BuildingElementKind Kind;
        public int Index;
        public int Sticks;
        public int Boards;
        public int Rope;
        public int Leaves;
        public float LocalX;
        public float LocalZ;
        public float LocalYaw;
    }

    public static int RequiredSupportsForRoof(int supportCount) =>
        Math.Max(1, (supportCount + 1) / 2);

    public static BuildingElementKind HutBayKind(int bay) => bay switch
    {
        2 or 3 or 10 or 11 => BuildingElementKind.Window,
        HutDoorBay => BuildingElementKind.Door,
        _ => BuildingElementKind.Wall
    };

    public static IReadOnlyList<BuildingElementProgress> ResolveHutElements(
        int siteSeed, int sticks, int boards, int rope, int leaves)
    {
        var definitions = HutDefinitions();
        var nonRoof = new List<Definition>();
        var roofs = new List<Definition>();
        foreach (var definition in definitions)
        {
            (definition.Kind == BuildingElementKind.Roof ? roofs : nonRoof).Add(definition);
        }

        Shuffle(nonRoof, siteSeed ^ 0x4B1D);
        Shuffle(roofs, siteSeed ^ 0x72A9);
        var states = new Dictionary<string, BuildingElementProgress>(definitions.Count);
        Allocate(nonRoof, ref sticks, ref boards, ref rope, ref leaves, true, states);

        var completedSupports = 0;
        foreach (var state in states.Values)
        {
            if (state.Kind == BuildingElementKind.Support && state.Complete) completedSupports++;
        }

        var roofBuildable = completedSupports >= RequiredSupportsForRoof(SupportCount);
        Allocate(roofs, ref sticks, ref boards, ref rope, ref leaves, roofBuildable, states);

        var result = new List<BuildingElementProgress>(definitions.Count);
        foreach (var definition in definitions) result.Add(states[definition.Key]);
        return result;
    }

    public static int CompletedSupports(int siteSeed, int sticks, int boards, int rope)
    {
        var count = 0;
        foreach (var element in ResolveHutElements(siteSeed, sticks, boards, rope, 0))
        {
            if (element.Kind == BuildingElementKind.Support && element.Complete) count++;
        }

        return count;
    }

    public static bool RoofUnlocked(int siteSeed, int sticks, int boards, int rope) =>
        CompletedSupports(siteSeed, sticks, boards, rope) >= RequiredSupportsForRoof(SupportCount);

    public static void EnsureHutElements(WorldObjectState owner, bool completed = false)
    {
        if (owner == null) return;
        if (owner.ArchitectureElements.Count > 0) return;
        var definitions = HutDefinitions();
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            owner.ArchitectureElements.Add(new ArchitectureElementState
            {
                ElementId = i + 1,
                DefinitionId = DefinitionId(definition.Kind),
                SlotKey = definition.Key,
                SlotIndex = definition.Index,
                Layer = PlacementLayer.Architecture,
                LocalX = definition.LocalX,
                LocalZ = definition.LocalZ,
                LocalYaw = definition.LocalYaw,
                RequiredSticks = definition.Sticks,
                RequiredBoards = definition.Boards,
                RequiredRope = definition.Rope,
                RequiredLeaves = definition.Leaves,
                Buildable = completed || definition.Kind != BuildingElementKind.Roof,
                DeliveredSticks = completed ? definition.Sticks : 0,
                DeliveredBoards = completed ? definition.Boards : 0,
                DeliveredRope = completed ? definition.Rope : 0,
                DeliveredLeaves = completed ? definition.Leaves : 0,
                WorkDone = completed ? 1 : 0
            });
        }
    }

    /// <summary>
    /// Creates the constructor as real top-level world objects. The hut/site is
    /// only the footprint aggregate; it intentionally owns no renderable pieces.
    /// </summary>
    public static void EnsureHutElements(WorldState world, WorldObjectState owner, bool completed = false)
    {
        if (world == null || owner == null) return;
        if (ArchitectureObjects(world, owner).Any()) return;

        // Build the canonical component records, or consume v32-v33 records
        // already loaded on the aggregate.
        EnsureHutElements(owner, completed);
        var components = owner.ArchitectureElements.Select(element => element.Clone()).ToArray();
        owner.ArchitectureElements.Clear();
        var anchor = owner.Junctions.Count > 0
            ? owner.Junctions[0]
            : StructurePlacement.CenterJunction(world, owner.Tile);
        if (anchor is not { } anchorId) return;

        foreach (var component in components)
        {
            var piece = WorldObjectMutations.SpawnObject(
                world, component.DefinitionId, owner.Fragment, owner.Tile, anchorId);
            piece.ArchitectureOwnerId = owner.Id;
            piece.RotationDegrees = owner.RotationDegrees;
            piece.ArchitectureElements.Add(component);
        }
    }

    public static IEnumerable<WorldObjectState> ArchitectureObjects(
        WorldState world, WorldObjectState owner) =>
        world.Entities.Objects.Values.Where(candidate =>
            candidate.ArchitectureOwnerId == owner.Id && candidate.ArchitectureElements.Count == 1);

    public static IEnumerable<ArchitectureElementState> Elements(
        WorldState world, WorldObjectState owner) =>
        ArchitectureObjects(world, owner).Select(piece => piece.ArchitectureElements[0]);

    public static void ReparentElements(WorldState world, ObjectId oldOwner, WorldObjectState newOwner)
    {
        foreach (var piece in world.Entities.Objects.Values)
        {
            if (piece.ArchitectureOwnerId != oldOwner) continue;
            piece.ArchitectureOwnerId = newOwner.Id;
            // Смена тайла обязана пройти и через ObjectsByTile: индекс — общий
            // (hazard/fruit/placement/perception), запись на старом тайле — это
            // объект-призрак для каждого его читателя.
            if (!piece.Tile.Equals(newOwner.Tile))
            {
                if (world.Caches.ObjectsByTile.TryGetValue(piece.Tile, out var fromList))
                {
                    fromList.Remove(piece.Id);
                }

                if (!world.Caches.ObjectsByTile.TryGetValue(newOwner.Tile, out var toList))
                {
                    toList = new System.Collections.Generic.List<ObjectId>();
                    world.Caches.ObjectsByTile[newOwner.Tile] = toList;
                }

                if (!toList.Contains(piece.Id))
                {
                    toList.Add(piece.Id);
                }
            }

            piece.Tile = newOwner.Tile;
            piece.Fragment = newOwner.Fragment;
            piece.RotationDegrees = newOwner.RotationDegrees;
        }
    }

    public static void MaterializeLegacyElements(WorldState world)
    {
        var owners = world.Entities.Objects.Values
            .Where(obj => obj.ArchitectureElements.Count > 0 && !obj.ArchitectureOwnerId.HasValue)
            .ToArray();
        foreach (var owner in owners)
        {
            EnsureHutElements(world, owner,
                completed: owner.DefinitionId == ContentIds.Hut1Hex);
        }
    }

    public static float DoorOutwardYaw(WorldState world, WorldObjectState owner)
    {
        EnsureHutElements(world, owner, completed: owner.DefinitionId == ContentIds.Hut1Hex);
        foreach (var element in Elements(world, owner))
        {
            if (element.DefinitionId != "architecture.door.wood") continue;
            var localYaw = MathF.Atan2(element.LocalZ, element.LocalX) * 180f / MathF.PI;
            return StructurePlacement.QuantizeHexSymmetryYaw(owner.RotationDegrees + localYaw);
        }
        throw new InvalidOperationException("Hut blueprint has no architecture.door.wood element.");
    }

    public static Float2 DoorLocalCenter(WorldState world, WorldObjectState owner)
    {
        EnsureHutElements(world, owner, completed: owner.DefinitionId == ContentIds.Hut1Hex);
        foreach (var element in Elements(world, owner))
            if (element.DefinitionId == "architecture.door.wood")
                return new Float2(element.LocalX, element.LocalZ);
        throw new InvalidOperationException("Hut blueprint has no architecture.door.wood element.");
    }

    public static bool RoofUnlocked(WorldState world, WorldObjectState owner)
    {
        EnsureHutElements(world, owner);
        return Elements(world, owner).Count(element =>
            element.DefinitionId == "architecture.support.wood" && element.Complete) >=
            RequiredSupportsForRoof(SupportCount);
    }

    public static bool FloorComplete(WorldState world, WorldObjectState owner)
    {
        EnsureHutElements(world, owner);
        var floors = Elements(world, owner)
            .Where(element => element.DefinitionId == "architecture.floor.board").ToArray();
        return floors.Length > 0 && floors.All(element => element.Complete);
    }

    public static bool AssignDeliveredMaterial(WorldState world, WorldObjectState owner, string materialId)
    {
        EnsureHutElements(world, owner);
        var elements = Elements(world, owner).ToArray();
        RefreshRoofBuildability(elements);
        var candidates = elements.Where(element =>
            element.Buildable && !element.Complete && RemainingFor(element, materialId) > 0).ToList();
        if (candidates.Count == 0) return false;

        var delivered = elements.Sum(element => element.DeliveredTotal);
        var target = candidates[StableIndex(owner.Id.Value, delivered, materialId, candidates.Count)];
        switch (materialId)
        {
            case ContentIds.Stick: target.DeliveredSticks++; break;
            case ContentIds.Board: target.DeliveredBoards++; break;
            case ContentIds.Rope: target.DeliveredRope++; break;
            case ContentIds.PalmLeaf: target.DeliveredLeaves++; break;
            default: return false;
        }
        if (target.DeliveredSticks >= target.RequiredSticks &&
            target.DeliveredBoards >= target.RequiredBoards &&
            target.DeliveredRope >= target.RequiredRope &&
            target.DeliveredLeaves >= target.RequiredLeaves)
            target.WorkDone = target.WorkRequired;
        RefreshRoofBuildability(elements);
        return true;
    }

    public static void SyncHutElements(WorldState world, WorldObjectState owner)
    {
        EnsureHutElements(world, owner);
        var definitions = HutDefinitions();
        var elements = Elements(world, owner).ToArray();
        var byKey = elements.ToDictionary(element => element.SlotKey);
        foreach (var element in elements)
        {
            element.DeliveredSticks = 0;
            element.DeliveredBoards = 0;
            element.DeliveredRope = 0;
            element.DeliveredLeaves = 0;
            element.WorkDone = 0;
        }

        var sticks = Count(owner, ContentIds.Stick);
        var boards = Count(owner, ContentIds.Board);
        var rope = Count(owner, ContentIds.Rope);
        var leaves = Count(owner, ContentIds.PalmLeaf);
        var nonRoof = definitions.FindAll(definition => definition.Kind != BuildingElementKind.Roof);
        var roofs = definitions.FindAll(definition => definition.Kind == BuildingElementKind.Roof);
        Shuffle(nonRoof, owner.Id.Value ^ 0x4B1D);
        Shuffle(roofs, owner.Id.Value ^ 0x72A9);
        SyncAllocate(nonRoof, byKey, ref sticks, ref boards, ref rope, ref leaves, true);
        var supports = elements.Count(element =>
            element.DefinitionId == "architecture.support.wood" && element.Complete);
        SyncAllocate(roofs, byKey, ref sticks, ref boards, ref rope, ref leaves,
            supports >= RequiredSupportsForRoof(SupportCount));
    }

    private static void RefreshRoofBuildability(IEnumerable<ArchitectureElementState> elements)
    {
        var array = elements as ArchitectureElementState[] ?? elements.ToArray();
        var unlocked = array.Count(element => element.DefinitionId == "architecture.support.wood" &&
            element.Complete) >= RequiredSupportsForRoof(SupportCount);
        foreach (var element in array)
            if (element.DefinitionId == "architecture.roof.palm") element.Buildable = unlocked;
    }

    /// <summary>
    /// Slot geometry is blueprint data, while delivery/work fields are instance
    /// state. Reapply the canonical geometry on load so old sites keep their
    /// progress but cannot retain the former mirrored door metadata.
    /// </summary>
    public static void RefreshHutElementGeometry(WorldObjectState owner)
    {
        if (owner == null) return;
        var definitions = HutDefinitions();
        var byKey = new Dictionary<string, Definition>();
        foreach (var definition in definitions) byKey[definition.Key] = definition;
        foreach (var element in owner.ArchitectureElements)
        {
            if (!byKey.TryGetValue(element.SlotKey, out var definition)) continue;
            element.DefinitionId = DefinitionId(definition.Kind);
            element.SlotIndex = definition.Index;
            element.Layer = PlacementLayer.Architecture;
            element.LocalX = definition.LocalX;
            element.LocalZ = definition.LocalZ;
            element.LocalYaw = definition.LocalYaw;
        }
    }

    public static void RefreshHutElementGeometry(WorldState world, WorldObjectState owner)
    {
        EnsureHutElements(world, owner, completed: owner.DefinitionId == ContentIds.Hut1Hex);
        var definitions = HutDefinitions().ToDictionary(definition => definition.Key);
        foreach (var element in Elements(world, owner))
        {
            if (!definitions.TryGetValue(element.SlotKey, out var definition)) continue;
            element.DefinitionId = DefinitionId(definition.Kind);
            element.SlotIndex = definition.Index;
            element.Layer = PlacementLayer.Architecture;
            element.LocalX = definition.LocalX;
            element.LocalZ = definition.LocalZ;
            element.LocalYaw = definition.LocalYaw;
        }
    }

    public static float DoorOutwardYaw(WorldObjectState owner)
    {
        EnsureHutElements(owner);
        foreach (var element in owner.ArchitectureElements)
        {
            if (element.DefinitionId != "architecture.door.wood") continue;
            var localYaw = MathF.Atan2(element.LocalZ, element.LocalX) * 180f / MathF.PI;
            return StructurePlacement.QuantizeHexSymmetryYaw(owner.RotationDegrees + localYaw);
        }

        throw new InvalidOperationException("Hut blueprint has no architecture.door.wood element.");
    }

    public static Float2 DoorLocalCenter(WorldObjectState owner)
    {
        EnsureHutElements(owner);
        foreach (var element in owner.ArchitectureElements)
        {
            if (element.DefinitionId == "architecture.door.wood")
                return new Float2(element.LocalX, element.LocalZ);
        }

        throw new InvalidOperationException("Hut blueprint has no architecture.door.wood element.");
    }

    public static void SyncHutElements(WorldObjectState owner)
    {
        EnsureHutElements(owner);
        var definitions = HutDefinitions();
        var byKey = new Dictionary<string, ArchitectureElementState>();
        foreach (var element in owner.ArchitectureElements)
        {
            byKey[element.SlotKey] = element;
            element.DeliveredSticks = 0;
            element.DeliveredBoards = 0;
            element.DeliveredRope = 0;
            element.DeliveredLeaves = 0;
            element.WorkDone = 0;
        }

        var sticks = Count(owner, ContentIds.Stick);
        var boards = Count(owner, ContentIds.Board);
        var rope = Count(owner, ContentIds.Rope);
        var leaves = Count(owner, ContentIds.PalmLeaf);
        var nonRoof = definitions.FindAll(definition => definition.Kind != BuildingElementKind.Roof);
        var roofs = definitions.FindAll(definition => definition.Kind == BuildingElementKind.Roof);
        Shuffle(nonRoof, owner.Id.Value ^ 0x4B1D);
        Shuffle(roofs, owner.Id.Value ^ 0x72A9);
        SyncAllocate(nonRoof, byKey, ref sticks, ref boards, ref rope, ref leaves, true);
        var supports = 0;
        foreach (var element in owner.ArchitectureElements)
        {
            if (element.DefinitionId == "architecture.support.wood" && element.Complete) supports++;
        }
        SyncAllocate(roofs, byKey, ref sticks, ref boards, ref rope, ref leaves,
            supports >= RequiredSupportsForRoof(SupportCount));
    }

    public static bool RoofUnlocked(WorldObjectState owner)
    {
        EnsureHutElements(owner);
        var supports = 0;
        foreach (var element in owner.ArchitectureElements)
        {
            if (element.DefinitionId == "architecture.support.wood" && element.Complete) supports++;
        }
        return supports >= RequiredSupportsForRoof(SupportCount);
    }

    public static bool FloorComplete(WorldObjectState owner)
    {
        EnsureHutElements(owner);
        var found = false;
        foreach (var element in owner.ArchitectureElements)
        {
            if (element.DefinitionId != "architecture.floor.board") continue;
            found = true;
            if (!element.Complete) return false;
        }

        return found;
    }

    public static bool AssignDeliveredMaterial(WorldObjectState owner, string materialId)
    {
        EnsureHutElements(owner);
        RefreshRoofBuildability(owner);
        var candidates = new List<ArchitectureElementState>();
        foreach (var element in owner.ArchitectureElements)
        {
            if (!element.Buildable || element.Complete || RemainingFor(element, materialId) <= 0) continue;
            candidates.Add(element);
        }
        if (candidates.Count == 0) return false;

        var delivered = 0;
        foreach (var element in owner.ArchitectureElements) delivered += element.DeliveredTotal;
        var roll = StableIndex(owner.Id.Value, delivered, materialId, candidates.Count);
        var target = candidates[roll];
        switch (materialId)
        {
            case ContentIds.Stick: target.DeliveredSticks++; break;
            case ContentIds.Board: target.DeliveredBoards++; break;
            case ContentIds.Rope: target.DeliveredRope++; break;
            case ContentIds.PalmLeaf: target.DeliveredLeaves++; break;
            default: return false;
        }
        if (target.DeliveredSticks >= target.RequiredSticks &&
            target.DeliveredBoards >= target.RequiredBoards &&
            target.DeliveredRope >= target.RequiredRope &&
            target.DeliveredLeaves >= target.RequiredLeaves)
        {
            target.WorkDone = target.WorkRequired;
        }
        RefreshRoofBuildability(owner);
        return true;
    }

    private static int RemainingFor(ArchitectureElementState element, string materialId) => materialId switch
    {
        ContentIds.Stick => Math.Max(0, element.RequiredSticks - element.DeliveredSticks),
        ContentIds.Board => Math.Max(0, element.RequiredBoards - element.DeliveredBoards),
        ContentIds.Rope => Math.Max(0, element.RequiredRope - element.DeliveredRope),
        ContentIds.PalmLeaf => Math.Max(0, element.RequiredLeaves - element.DeliveredLeaves),
        _ => 0
    };

    private static void RefreshRoofBuildability(WorldObjectState owner)
    {
        var supports = 0;
        foreach (var element in owner.ArchitectureElements)
        {
            if (element.DefinitionId == "architecture.support.wood" && element.Complete) supports++;
        }
        var unlocked = supports >= RequiredSupportsForRoof(SupportCount);
        foreach (var element in owner.ArchitectureElements)
        {
            if (element.DefinitionId == "architecture.roof.palm") element.Buildable = unlocked;
        }
    }

    private static int StableIndex(int ownerId, int delivered, string materialId, int count)
    {
        unchecked
        {
            // Never use string.GetHashCode here: its value is not a persisted
            // cross-process contract. Constructor choices must replay exactly
            // after save/load and on the presentation client.
            uint materialHash = 2166136261u;
            for (var i = 0; i < materialId.Length; i++)
            {
                materialHash ^= materialId[i];
                materialHash *= 16777619u;
            }
            uint x = (uint)(ownerId * 397 ^ delivered * 7919) ^ materialHash;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            return (int)(x % (uint)count);
        }
    }

    private static List<Definition> HutDefinitions()
    {
        var result = new List<Definition>(SupportCount + FloorElementCount + BayCount + RoofElementCount);
        for (var i = 0; i < SupportCount; i++)
        {
            var angle = (90f + i * 60f) * (MathF.PI / 180f);
            result.Add(New(BuildingElementKind.Support, i, sticks: i < 4 ? 5 : 4,
                localX: MathF.Cos(angle) * 1.5f, localZ: MathF.Sin(angle) * 1.5f,
                localYaw: 90f + i * 60f));
        }

        for (var i = 0; i < FloorElementCount; i++)
        {
            result.Add(New(BuildingElementKind.Floor, i, boards: 1,
                localZ: -0.96f + i * 0.384f));
        }

        for (var bay = 0; bay < BayCount; bay++)
        {
            var kind = HutBayKind(bay);
            var sticks = bay == HutDoorBay || bay is 0 or 1 or 4 or 5 or 6 ? 1 : 0;
            // Preserve the approved total bill while moving the modules.
            var rope = bay == HutDoorBay || bay is 2 or 10 or 11 ? 1 : 0;
            var edge = bay / 2;
            var half = bay % 2;
            // Unity's imported Blender X/Y -> X/Z basis puts exported Bay_00
            // on the upper-left edge (90°..150°). This catalog position is
            // also the portal source; using the Blender pre-import sign here
            // placed navigation on the opposite edge from the rendered door.
            var a0 = (90f + edge * 60f) * (MathF.PI / 180f);
            var a1 = (90f + (edge + 1) * 60f) * (MathF.PI / 180f);
            var t = half == 0 ? 0.25f : 0.75f;
            var x0 = MathF.Cos(a0) * 1.5f;
            var z0 = MathF.Sin(a0) * 1.5f;
            var x1 = MathF.Cos(a1) * 1.5f;
            var z1 = MathF.Sin(a1) * 1.5f;
            result.Add(New(kind, bay, sticks, bay == HutDoorBay ? 3 : 2, rope,
                localX: x0 + (x1 - x0) * t, localZ: z0 + (z1 - z0) * t,
                localYaw: MathF.Atan2(-(z1 - z0), x1 - x0) * 180f / MathF.PI));
        }

        for (var i = 0; i < RoofElementCount; i++)
        {
            var angle = (90f - i * 60f) * (MathF.PI / 180f);
            result.Add(New(BuildingElementKind.Roof, i, leaves: 2,
                localX: MathF.Cos(angle) * 0.75f, localZ: MathF.Sin(angle) * 0.75f,
                localYaw: 90f - i * 60f));
        }

        return result;
    }

    private static Definition New(BuildingElementKind kind, int index,
        int sticks = 0, int boards = 0, int rope = 0, int leaves = 0,
        float localX = 0f, float localZ = 0f, float localYaw = 0f) => new()
    {
        Key = $"{kind.ToString().ToLowerInvariant()}.{index}",
        Kind = kind,
        Index = index,
        Sticks = sticks,
        Boards = boards,
        Rope = rope,
        Leaves = leaves,
        LocalX = localX,
        LocalZ = localZ,
        LocalYaw = localYaw
    };

    private static string DefinitionId(BuildingElementKind kind) => kind switch
    {
        BuildingElementKind.Support => "architecture.support.wood",
        BuildingElementKind.Floor => "architecture.floor.board",
        BuildingElementKind.Wall => "architecture.wall.wood",
        BuildingElementKind.Window => "architecture.window.wood",
        BuildingElementKind.Door => "architecture.door.wood",
        BuildingElementKind.Roof => "architecture.roof.palm",
        _ => "architecture.unknown"
    };

    private static int Count(WorldObjectState owner, string definitionId)
    {
        var count = 0;
        foreach (var item in owner.Contents)
        {
            if (item.DefinitionId == definitionId) count++;
        }
        return count;
    }

    private static void SyncAllocate(List<Definition> definitions,
        Dictionary<string, ArchitectureElementState> states,
        ref int sticks, ref int boards, ref int rope, ref int leaves, bool buildable)
    {
        foreach (var definition in definitions)
        {
            if (!states.TryGetValue(definition.Key, out var state)) continue;
            state.Buildable = buildable;
            if (!buildable) continue;
            state.DeliveredSticks = Take(ref sticks, definition.Sticks);
            state.DeliveredBoards = Take(ref boards, definition.Boards);
            state.DeliveredRope = Take(ref rope, definition.Rope);
            state.DeliveredLeaves = Take(ref leaves, definition.Leaves);
            if (state.DeliveredSticks >= state.RequiredSticks &&
                state.DeliveredBoards >= state.RequiredBoards &&
                state.DeliveredRope >= state.RequiredRope &&
                state.DeliveredLeaves >= state.RequiredLeaves)
            {
                state.WorkDone = state.WorkRequired;
            }
        }
    }

    private static void Allocate(List<Definition> definitions,
        ref int sticks, ref int boards, ref int rope, ref int leaves,
        bool buildable, Dictionary<string, BuildingElementProgress> states)
    {
        foreach (var definition in definitions)
        {
            var required = definition.Sticks + definition.Boards + definition.Rope + definition.Leaves;
            var delivered = 0;
            if (buildable)
            {
                delivered += Take(ref sticks, definition.Sticks);
                delivered += Take(ref boards, definition.Boards);
                delivered += Take(ref rope, definition.Rope);
                delivered += Take(ref leaves, definition.Leaves);
            }

            states[definition.Key] = new BuildingElementProgress
            {
                Key = definition.Key,
                Kind = definition.Kind,
                Index = definition.Index,
                Buildable = buildable,
                Progress = required <= 0 ? 1f : delivered / (float)required
            };
        }
    }

    private static int Take(ref int pool, int required)
    {
        var used = Math.Min(Math.Max(0, pool), required);
        pool -= used;
        return used;
    }

    private static void Shuffle(List<Definition> values, int seed)
    {
        unchecked
        {
            uint state = (uint)seed + 0x9E3779B9u;
            for (var i = values.Count - 1; i > 0; i--)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                var other = (int)(state % (uint)(i + 1));
                (values[i], values[other]) = (values[other], values[i]);
            }
        }
    }
}

}
