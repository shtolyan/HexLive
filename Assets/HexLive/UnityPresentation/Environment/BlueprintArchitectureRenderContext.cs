#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
/// <summary>Transient §120 preview frames, never save/wire data. Existing
/// houses keep their OWN local draft and yaw; independent pieces share the
/// authoritative free-world frame. A remote house cannot turn a near post.</summary>
public sealed class BlueprintArchitectureRenderContext
{
    private sealed class Group
    {
        internal BuildingBlueprintDraft Source = null!;
        internal TileCoord Destination;
        internal float Yaw;
        internal readonly List<Entry> Entries = new();
    }
    private sealed class Entry
    {
        internal string DisplayId = string.Empty;
        internal string WorldSlot = string.Empty;
        internal BlueprintElementData Local = null!;
    }
    private readonly List<Group> _owners = new();
    private readonly bool _world;
    private readonly Func<TileCoord, float>? _elevationOffset;
    private readonly Dictionary<string, (string definition, Vector3 position, Quaternion rotation)> _poses = new();
    private BuildingBlueprintDraft? _prepared;

    private BlueprintArchitectureRenderContext(bool world, Func<TileCoord, float>? elevationOffset = null)
    { _world = world; _elevationOffset = elevationOffset; }

    public static BlueprintArchitectureRenderContext Local(BuildingBlueprintDraft draft)
    {
        var context = new BlueprintArchitectureRenderContext(false);
        var group = new Group { Source = draft.Clone(), Destination = BlueprintBuildingPlan.AnchorTile(draft) };
        foreach (var element in draft.Elements)
            group.Entries.Add(new Entry { DisplayId = element.Id, WorldSlot = BlueprintBuildingPlan.SlotKey(element), Local = element.Clone() });
        context._owners.Add(group);
        return context;
    }

    public static BlueprintArchitectureRenderContext World(WorldSnapshot snapshot, Func<TileCoord, float>? elevationOffset = null)
    {
        var context = new BlueprintArchitectureRenderContext(true, elevationOffset);
        foreach (var owner in snapshot.Objects.OrderBy(o => o.Id.Value))
        {
            if (string.IsNullOrEmpty(owner.BuildingBlueprintJson) ||
                !BuildingBlueprintJson.TryDeserialize(owner.BuildingBlueprintJson, out var plan, out _)) continue;
            var group = new Group { Source = plan, Destination = owner.Tile, Yaw = owner.RotationDegrees };
            foreach (var authored in plan.Elements)
            {
                var projected = ArchitectureWorldEditing.ToWorld(authored, plan, owner.Tile, owner.RotationDegrees);
                group.Entries.Add(new Entry { DisplayId = $"house_{owner.Id.Value}_{authored.Id}",
                    WorldSlot = BlueprintBuildingPlan.SlotKey(projected), Local = authored.Clone() });
            }
            context._owners.Add(group);
        }
        return context;
    }

    public BlueprintArchitectureRenderContext WithSnapshot(WorldSnapshot snapshot) => World(snapshot, _elevationOffset);

    /// <summary>Once per Rebuild, including undo and temporary command ghosts.
    /// The draft is mutable, so a reference/count-only cache would be stale.</summary>
    public void Prepare(BuildingBlueprintDraft displayed)
    {
        _poses.Clear(); _prepared = displayed;
        var byId = displayed.Elements.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in _owners)
        {
            var local = group.Source.Clone(); local.Elements.Clear(); local.Furniture.Clear();
            var stableAnchor = BlueprintBuildingPlan.AnchorTile(group.Source);
            local.HasAnchor = true; local.AnchorQ = stableAnchor.Q; local.AnchorR = stableAnchor.R;
            var current = new List<(Entry entry, BlueprintElementData element)>();
            foreach (var entry in group.Entries)
            {
                if (!byId.TryGetValue(entry.DisplayId, out var live) ||
                    BlueprintBuildingPlan.SlotKey(live) != entry.WorldSlot) continue;
                var element = entry.Local.Clone(); element.Kind = live.Kind;
                local.Elements.Add(element); current.Add((entry, element)); owned.Add(entry.DisplayId);
            }
            var modules = BlueprintBuildingPlan.Modules(local).ToDictionary(m => m.Key, StringComparer.Ordinal);
            foreach (var pair in current)
                AddPose(pair.entry.DisplayId, pair.element.Kind, modules[BlueprintBuildingPlan.SlotKey(pair.element)], group.Destination, group.Yaw, _world ? _elevationOffset?.Invoke(group.Destination) ?? 0f : 0f);
        }
        if (!_world) return;

        // Matches FreeArchitectureRules.RefreshGeometryAndBills: all independent
        // pieces use one absolute source group. House floors must NOT enter its
        // centroid/seam assignment. Moved old house elements become new free slots.
        var free = new BuildingBlueprintDraft { HasAnchor = true, BlueprintId = "free_world_projection" };
        free.Elements.AddRange(displayed.Elements.Where(e => !owned.Contains(e.Id)).Select(e => e.Clone()));
        var freeModules = BlueprintBuildingPlan.Modules(free).ToDictionary(m => m.Key, StringComparer.Ordinal);
        foreach (var element in free.Elements)
        {
            var module = freeModules[BlueprintBuildingPlan.SlotKey(element)];
            var tile = HexSpatialMath.WorldToTile(new Float2(module.LocalX, module.LocalZ));
            AddPose(element.Id, element.Kind, module, TileCoord.Zero, 0f, _elevationOffset?.Invoke(tile) ?? 0f);
        }
    }

    private void AddPose(string id, BlueprintElementKind kind, BuildingElementBlueprint module, TileCoord destination, float yaw, float elevationOffset)
    {
        var definition = kind switch {
            BlueprintElementKind.Support => "architecture.support.wood",
            BlueprintElementKind.FloorSector => "architecture.floor.board",
            BlueprintElementKind.Window => "architecture.window.wood",
            BlueprintElementKind.Door => "architecture.door.wood",
            BlueprintElementKind.RoofSector => "architecture.roof.palm",
            _ => "architecture.wall.wood"
        };
        var frame = Quaternion.Euler(0f, SimulationUnityMapper.ToUnityFootprintYawDegrees(yaw), 0f);
        var origin = SimulationUnityMapper.ToUnityTilePosition(destination);
        var position = origin + frame * new Vector3(module.LocalX, BlueprintArchitectureFactory.ModuleLift(definition), module.LocalZ)
            + Vector3.up * elevationOffset;
        _poses[id] = (definition, position, frame * BlueprintArchitectureFactory.ModuleRotation(definition, module.LocalYaw));
    }

    internal GameObject? Build(BlueprintElementData element, BuildingBlueprintDraft displayed)
    {
        if (!ReferenceEquals(_prepared, displayed)) Prepare(displayed);
        if (!_poses.TryGetValue(element.Id, out var pose)) return null;
        var result = BlueprintArchitectureFactory.InstantiateModel(pose.definition, pose.position, pose.rotation);
        if (result != null && element.Kind == BlueprintElementKind.Door)
            BlueprintArchitectureFactory.ConfigureAuthoredDoor(result, startOpen: true);
        return result;
    }
}
}
