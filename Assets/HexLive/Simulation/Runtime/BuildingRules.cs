using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;

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
        if (owner == null || owner.ArchitectureElements.Count > 0) return;
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
            var kind = bay == 0 ? BuildingElementKind.Door :
                bay == 3 || bay == 9 ? BuildingElementKind.Window : BuildingElementKind.Wall;
            var sticks = bay == 0 || bay is 1 or 2 or 4 or 5 or 6 ? 1 : 0;
            var rope = bay == 0 || bay is 3 or 7 or 9 ? 1 : 0;
            var edge = bay / 2;
            var half = bay % 2;
            var a0 = (90f + edge * 60f) * (MathF.PI / 180f);
            var a1 = (90f + (edge + 1) * 60f) * (MathF.PI / 180f);
            var t = half == 0 ? 0.25f : 0.75f;
            var x0 = MathF.Cos(a0) * 1.5f;
            var z0 = MathF.Sin(a0) * 1.5f;
            var x1 = MathF.Cos(a1) * 1.5f;
            var z1 = MathF.Sin(a1) * 1.5f;
            result.Add(New(kind, bay, sticks, bay == 0 ? 3 : 2, rope,
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
