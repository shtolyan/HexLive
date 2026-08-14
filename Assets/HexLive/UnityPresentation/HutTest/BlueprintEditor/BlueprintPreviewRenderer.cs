#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.Environment;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.HutTest.BlueprintEditor
{
    public enum BlueprintEditorMode
    {
        Rooms,
        Architecture,
        Furniture,
        Roof
    }

    /// <summary>
    /// Reads the same integer blueprint used by commands/tests and materializes
    /// one GameObject per LEGO element. Furniture always goes through its
    /// production assembly factory; no preview-only model transforms exist.
    /// </summary>
    public sealed class BlueprintPreviewRenderer : MonoBehaviour
    {
        private const float FloorY = HutAssembly.FloorSurfaceLift;
        private readonly Dictionary<string, GameObject> _objects = new();
        // Everything the cutaway may hide, with the plan position it is scored
        // by. Corner supports belong here too: they are full-height posts, and
        // leaving them out left one standing in front of the selected colonist.
        private readonly List<(Vector2 position, Renderer[] renderers)> _walls = new();
        private readonly List<Transform> _conflictMarkers = new();
        private Transform? _elementRoot;
        private Transform? _overlayRoot;
        private Material? _green;
        private Material? _red;
        private Material? _amber;
        private BlueprintEditorMode _mode;
        private BuildingBlueprintDraft? _draft;
        private string _selectedId = string.Empty;
        private HashSet<string> _selectedIds = new();
        private HashSet<JunctionKey> _conflicts = new();
        private HashSet<HexBuildNodeKey> _buildConflicts = new();
        private HashSet<string> _ghostIds = new();
        private bool _invalidGhost;

        public BuildingBlueprintDraft? Draft => _draft;

        public void Rebuild(
            BuildingBlueprintDraft draft,
            BlueprintEditorMode mode,
            string selectedId = "",
            IEnumerable<JunctionKey>? conflicts = null,
            IEnumerable<HexBuildNodeKey>? buildConflicts = null,
            IEnumerable<string>? ghostIds = null,
            bool invalidGhost = false,
            IEnumerable<string>? selectedIds = null)
        {
            EnsureRoots();
            ClearChildren(_elementRoot!);
            ClearChildren(_overlayRoot!);
            _objects.Clear();
            _walls.Clear();
            _conflictMarkers.Clear();
            _draft = draft;
            _mode = mode;
            _selectedId = selectedId ?? string.Empty;
            _selectedIds = selectedIds != null
                ? new HashSet<string>(selectedIds)
                : string.IsNullOrEmpty(_selectedId)
                    ? new HashSet<string>()
                    : new HashSet<string> { _selectedId };
            _conflicts = conflicts != null
                ? new HashSet<JunctionKey>(conflicts)
                : new HashSet<JunctionKey>();
            _buildConflicts = buildConflicts != null
                ? new HashSet<HexBuildNodeKey>(buildConflicts)
                : new HashSet<HexBuildNodeKey>();
            _ghostIds = ghostIds != null
                ? new HashSet<string>(ghostIds)
                : new HashSet<string>();
            _invalidGhost = invalidGhost;

            foreach (var element in draft.Elements)
                BuildElement(element);
            foreach (var furniture in draft.Furniture)
                BuildFurniture(furniture);
            BuildOverlay();
            ApplyModeVisibility();
            ApplyGhostFeedback();
        }

        public bool TryGetObject(string id, out GameObject gameObject) => _objects.TryGetValue(id, out gameObject!);

        public void ShowWallGuide(HexBuildNodeKey start, HexBuildNodeKey end, bool valid)
        {
            EnsureRoots();
            if (!BlueprintGeometry.TryLine(start, end, out _, out var count)) return;
            var a = BlueprintGeometry.ToWorld(start);
            var b = BlueprintGeometry.ToWorld(end);
            var material = valid ? Green : Red;
            var guide = new GameObject(valid ? "Valid wall gesture" : "Invalid wall gesture");
            guide.transform.SetParent(_overlayRoot, false);
            var line = guide.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = 2;
            line.SetPosition(0, new Vector3(a.X, FloorY + 0.09f, a.Y));
            line.SetPosition(1, new Vector3(b.X, FloorY + 0.09f, b.Y));
            line.startWidth = line.endWidth = 0.055f;
            line.sharedMaterial = material;
            line.startColor = line.endColor = material.color;

            guide.name += $" ({count} × 0.5 = {count * BlueprintGeometry.BuildStep:0.0} wu)";
        }

        private void LateUpdate()
        {
            var pulse = 1f + Mathf.Sin(Time.unscaledTime * 7f) * 0.18f;
            foreach (var marker in _conflictMarkers)
                if (marker != null) marker.localScale = Vector3.one * 0.075f * pulse;

        }

        public void UpdateCutaway(Camera? camera)
        {
            if (_mode != BlueprintEditorMode.Furniture || camera == null || _walls.Count == 0)
            {
                foreach (var wall in _walls)
                    foreach (var renderer in wall.renderers) renderer.shadowCastingMode = ShadowCastingMode.On;
                return;
            }

            var localCamera = transform.InverseTransformPoint(camera.transform.position);
            var ordered = _walls.Select(wall =>
            {
                var position = wall.position;
                var cameraDirection = new Vector2(localCamera.x - position.x, localCamera.z - position.y).normalized;
                var outward = position.sqrMagnitude > 0.001f ? position.normalized : cameraDirection;
                return (wall, score: Vector2.Dot(outward, cameraDirection));
            }).OrderByDescending(entry => entry.score).ToArray();

            var hiddenCount = Math.Max(1, ordered.Length / 3);
            for (var i = 0; i < ordered.Length; i++)
            {
                foreach (var renderer in ordered[i].wall.renderers)
                {
                    // Shadows remain even while the nearest wall is invisible.
                    renderer.shadowCastingMode = i < hiddenCount
                        ? ShadowCastingMode.ShadowsOnly
                        : ShadowCastingMode.On;
                }
            }
        }

        private void BuildElement(BlueprintElementData element)
        {
            if (_draft == null) return;
            var root = BlueprintArchitectureFactory.Build(element, _draft);
            root.name = $"Blueprint {element.Kind} {element.Id}";
            root.transform.SetParent(_elementRoot, false);
            _objects[element.Id] = root;
            if (element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door)
            {
                var a = BlueprintGeometry.ToWorld(element.Segment.A);
                var b = BlueprintGeometry.ToWorld(element.Segment.B);
                _walls.Add((new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f),
                            root.GetComponentsInChildren<Renderer>(true)));
            }
            else if (element.Kind == BlueprintElementKind.Support)
            {
                var node = BlueprintGeometry.ToWorld(element.Node);
                _walls.Add((new Vector2(node.X, node.Y),
                            root.GetComponentsInChildren<Renderer>(true)));
            }
        }

        private void BuildFurniture(FurniturePlacementData item)
        {
            GameObject? root = item.DefinitionId switch
            {
                ContentIds.BedBasic => HutFurnitureFactory.BuildBed(),
                "furniture.hearth" => HutFurnitureFactory.BuildHearth(),
                "furniture.wardrobe" => WardrobeAssembly.BuildFinished(),
                _ => null
            };
            if (root == null) return;
            root.name = $"Blueprint furniture {item.DefinitionId} {item.Id}";
            root.transform.SetParent(_elementRoot, false);
            // Draw the model on the CENTRE of its occupied junctions, not on the
            // anchor junction. A bed's logical footprint runs from -0.5625 to
            // +0.9375 along its length, so the anchor sits 0.1875 wu off centre
            // and placing the mesh there slid every bed away from its wall.
            var occupied = BlueprintFurnitureFootprints.OccupiedJunctions(item);
            var centre = Vector2.zero;
            foreach (var junction in occupied)
            {
                var world = BlueprintGeometry.JunctionToWorld(junction);
                centre += new Vector2(world.X, world.Y);
            }
            centre /= Mathf.Max(1, occupied.Count);
            root.transform.localPosition = new Vector3(centre.x, FloorY, centre.y);
            root.transform.localRotation = Quaternion.Euler(0f, -item.YawStep * 60f, 0f);
            _objects[item.Id] = root;
        }

        private void BuildOverlay()
        {
            if (_draft == null || _overlayRoot == null) return;
            if (_mode == BlueprintEditorMode.Furniture)
            {
                var occupants = new Dictionary<JunctionKey, List<string>>();
                foreach (var furniture in _draft.Furniture)
                    foreach (var key in BlueprintFurnitureFootprints.OccupiedJunctions(furniture))
                    {
                        if (!occupants.TryGetValue(key, out var owners))
                        {
                            owners = new List<string>();
                            occupants[key] = owners;
                        }
                        owners.Add(furniture.Id);
                    }

                var tiles = _draft.Elements.Where(element => element.Kind == BlueprintElementKind.FloorSector)
                    .Select(element => element.FloorSector.Hex).Distinct().ToArray();
                if (tiles.Length == 0) tiles = new[] { HexLive.Simulation.Common.TileCoord.Zero };
                // The interior set stops at r=3. The r=4 boundary ring is where
                // two hexes MEET, so a room spanning several hexes had no points
                // at all along its inner seams: the player could neither build
                // nor place furniture there. Those nodes are shared by both
                // hexes, hence the dedupe by JunctionKey.
                var drawn = new HashSet<JunctionKey>();
                foreach (var tile in tiles)
                foreach (var template in HexLive.Simulation.Spatial.HexPointLayout.GetInteriorTemplates()
                             .Concat(HexLive.Simulation.Spatial.HexPointLayout.GetBoundaryTemplates()))
                {
                    var pair = HexLive.Simulation.Spatial.HexPointLayout.GetJunctionKeyPair(tile, template.SubAxial);
                    var key = new JunctionKey(pair.xKey, pair.yKey);
                    if (!drawn.Add(key)) continue;
                    var conflict = _conflicts.Contains(key) ||
                        occupants.TryGetValue(key, out var owners) && owners.Count > 1;
                    var material = conflict ? Red :
                        occupants.TryGetValue(key, out owners)
                            ? owners.Any(_selectedIds.Contains) ? Amber : Red
                            : Green;
                    AddMarker(BlueprintGeometry.JunctionToWorld(key), material, $"junction {key}", conflict);
                }
            }
            else
            {
                var occupied = new Dictionary<HexBuildNodeKey, string>();
                foreach (var element in _draft.Elements)
                {
                    if (element.Kind == BlueprintElementKind.Support) occupied[element.Node] = element.Id;
                    else if (element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door)
                    {
                        occupied[element.Segment.A] = element.Id;
                        occupied[element.Segment.B] = element.Id;
                    }
                }
                var nodes = new HashSet<HexBuildNodeKey>();
                foreach (var element in _draft.Elements)
                {
                    if (element.Kind == BlueprintElementKind.FloorSector)
                    {
                        var center = BlueprintGeometry.HexCenter(element.FloorSector.Hex);
                        for (var q = -3; q <= 3; q++)
                        for (var r = -3; r <= 3; r++)
                            if (Math.Max(Math.Abs(q), Math.Max(Math.Abs(r), Math.Abs(q + r))) <= 3)
                                nodes.Add(center + new HexBuildNodeKey(q, r));
                    }
                    else if (element.Kind == BlueprintElementKind.Support) nodes.Add(element.Node);
                    else if (element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window or BlueprintElementKind.Door)
                    {
                        nodes.Add(element.Segment.A);
                        nodes.Add(element.Segment.B);
                    }
                }
                if (nodes.Count == 0)
                    for (var q = -3; q <= 3; q++)
                    for (var r = -3; r <= 3; r++)
                        if (Math.Max(Math.Abs(q), Math.Max(Math.Abs(r), Math.Abs(q + r))) <= 3)
                            nodes.Add(new HexBuildNodeKey(q, r));
                foreach (var node in nodes)
                {
                    var conflict = _buildConflicts.Contains(node);
                    var material = conflict ? Red : occupied.TryGetValue(node, out var owner)
                        ? _selectedIds.Contains(owner) ? Amber : Red
                        : Green;
                    AddMarker(BlueprintGeometry.ToWorld(node), material, $"build {node}", conflict);
                }
            }
        }

        private void ApplyModeVisibility()
        {
            foreach (var element in _draft?.Elements ?? Enumerable.Empty<BlueprintElementData>())
            {
                if (!_objects.TryGetValue(element.Id, out var go)) continue;
                if (element.Kind == BlueprintElementKind.RoofSector)
                    go.SetActive(_mode == BlueprintEditorMode.Roof);
                else
                    SetTint(go, _mode == BlueprintEditorMode.Roof ? 0.32f : 1f);
            }
        }

        private void ApplyGhostFeedback()
        {
            if (_ghostIds.Count == 0) return;
            var color = _invalidGhost
                ? new Color(0.94f, 0.24f, 0.29f, 0.72f)
                : new Color(0.20f, 0.78f, 0.53f, 0.72f);
            foreach (var id in _ghostIds)
                if (_objects.TryGetValue(id, out var root)) SetColor(root, color);
        }

        private void AddMarker(
            HexLive.Simulation.Common.Float2 point, Material material, string name, bool pulse = false)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.SetParent(_overlayRoot, false);
            marker.transform.localPosition = new Vector3(point.X, FloorY + 0.045f, point.Y);
            marker.transform.localScale = Vector3.one * 0.075f;
            marker.GetComponent<Renderer>().sharedMaterial = material;
            Destroy(marker.GetComponent<Collider>());
            if (pulse) _conflictMarkers.Add(marker.transform);
        }

        private void EnsureRoots()
        {
            if (_elementRoot == null)
            {
                _elementRoot = new GameObject("Blueprint elements").transform;
                _elementRoot.SetParent(transform, false);
            }
            if (_overlayRoot == null)
            {
                _overlayRoot = new GameObject("Blueprint grid overlay").transform;
                _overlayRoot.SetParent(transform, false);
            }
        }

        private static void ClearChildren(Transform root)
        {
            for (var index = root.childCount - 1; index >= 0; index--)
            {
                var child = root.GetChild(index).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
        }

        private static void SetTint(GameObject root, float alpha)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                var color = renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_BaseColor")
                    ? renderer.sharedMaterial.GetColor("_BaseColor")
                    : Color.white;
                color.r *= alpha;
                color.g *= alpha;
                color.b *= alpha;
                block.SetColor("_BaseColor", color);
                block.SetColor("_Color", color);
                renderer.SetPropertyBlock(block);
            }
        }

        private static void SetColor(GameObject root, Color color)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                block.SetColor("_BaseColor", color);
                block.SetColor("_Color", color);
                renderer.SetPropertyBlock(block);
            }
        }

        private Material Green => _green ??= OverlayMaterial("BlueprintFree", new Color(0.20f, 0.78f, 0.53f, 0.92f));
        private Material Red => _red ??= OverlayMaterial("BlueprintBlocked", new Color(0.94f, 0.24f, 0.29f, 0.95f));
        private Material Amber => _amber ??= OverlayMaterial("BlueprintSelected", new Color(1f, 0.63f, 0.20f, 0.98f));

        private static Material OverlayMaterial(string name, Color color)
        {
            // HexLive/BlueprintOverlay bakes ZTest Always into the pass. The URP
            // Unlit fallback cannot do that — its ZTest is hardcoded — so the
            // dots would hide behind walls exactly when they matter most.
            var shader = Shader.Find("HexLive/BlueprintOverlay")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            var material = new Material(shader) { name = name, color = color, renderQueue = 5000 };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)CompareFunction.Always);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            return material;
        }
    }
}
