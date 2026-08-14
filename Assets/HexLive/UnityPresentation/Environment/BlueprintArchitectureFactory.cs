#nullable enable
using System;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime.Blueprints;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Canonical visual factory for integer-keyed architecture LEGO pieces.
    /// Both the constructor and future production loading call this class;
    /// there are no scene-specific correction rotations or offsets.
    /// </summary>
    public static class BlueprintArchitectureFactory
    {
        public const float WallHeight = 1.5f;
        public const float SupportHeight = 1.6f;
        private static Material? _wood;
        private static Material? _woodLight;
        private static Material? _bark;
        private static Material? _leaf;
        private static readonly System.Collections.Generic.Dictionary<string, GameObject?> ModelPrefabs = new();
        private static readonly System.Collections.Generic.Dictionary<Material, Material> UrpMaterialVariants = new();

        public static GameObject Build(BlueprintElementData element, BuildingBlueprintDraft draft)
        {
            return element.Kind switch
            {
                BlueprintElementKind.Support => BuildSupport(element, draft),
                BlueprintElementKind.FloorSector => BuildFloor(element),
                BlueprintElementKind.Wall => BuildBay(element, draft, BlueprintElementKind.Wall),
                BlueprintElementKind.Window => BuildBay(element, draft, BlueprintElementKind.Window),
                BlueprintElementKind.Door => BuildBay(element, draft, BlueprintElementKind.Door),
                BlueprintElementKind.RoofSector => BuildRoof(element, draft),
                _ => new GameObject($"Unsupported blueprint element {element.Kind}")
            };
        }

        /// <summary>
        /// Stage-by-stage reveal: every direct mesh child of BuildStage_1/2/3 is
        /// one delivered resource, so a partially built element shows exactly the
        /// pieces its materials paid for (§120.1). Stage order is sticks, boards,
        /// rope; a stage stays fully hidden until the previous one is complete.
        /// </summary>
        public static void ApplyStageProgress(
            GameObject element, int deliveredSticks, int deliveredBoards, int deliveredRope)
        {
            var delivered = new[] { deliveredSticks, deliveredBoards, deliveredRope };
            for (var stage = 0; stage < 3; stage++)
            {
                var root = FindStage(element.transform, stage + 1);
                if (root == null) continue;
                var previousComplete = true;
                for (var earlier = 0; earlier < stage && previousComplete; earlier++)
                {
                    var earlierRoot = FindStage(element.transform, earlier + 1);
                    if (earlierRoot == null) continue;
                    previousComplete = delivered[earlier] >= ResourceUnits(earlierRoot).Count;
                }
                var visible = previousComplete ? delivered[stage] : 0;
                var units = ResourceUnits(root);
                for (var i = 0; i < units.Count; i++)
                    units[i].gameObject.SetActive(i < visible);
                foreach (Transform child in root)
                    if (IsContainer(child.name)) child.gameObject.SetActive(previousComplete);
            }
        }

        public static int ResourceCount(GameObject element, int stage)
        {
            var root = FindStage(element.transform, stage);
            return root == null ? 0 : ResourceUnits(root).Count;
        }

        /// <summary>
        /// One entry per delivered resource. The door pivot is a transparent
        /// container: its leaf boards are ordinary stage-2 board resources, while
        /// hinge hardware and state markers cost nothing and follow their stage.
        /// </summary>
        private static System.Collections.Generic.List<Transform> ResourceUnits(Transform stageRoot)
        {
            var units = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in stageRoot)
            {
                if (IsContainer(child.name))
                {
                    foreach (Transform nested in child)
                        if (!IsHardware(nested.name)) units.Add(nested);
                    continue;
                }
                if (!IsHardware(child.name)) units.Add(child);
            }
            return units;
        }

        private static bool IsContainer(string name) =>
            name.StartsWith("HL_Door_Pivot", System.StringComparison.Ordinal);

        private static bool IsHardware(string name) =>
            name.Contains("_deco_") ||
            name.StartsWith("HL_Door_State_", System.StringComparison.Ordinal);

        private static Transform? FindStage(Transform root, int stage)
        {
            var suffix = "BuildStage_" + stage;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name.StartsWith(suffix, System.StringComparison.Ordinal)) return child;
            return null;
        }

        /// <summary>
        /// Authored §120 element models (Blender kit, export_arch_elements.py):
        /// BuildStage_1/2/3 children are single delivered resources, section
        /// length runs along local +Z, origin is the section centre on the floor.
        /// </summary>
        private static GameObject? InstantiateModel(string definitionId, Vector3 position, Quaternion yaw)
        {
            if (!ModelPrefabs.TryGetValue(definitionId, out var prefab))
            {
                prefab = Resources.Load<GameObject>("HexLive/Objects/" + definitionId);
                ModelPrefabs[definitionId] = prefab;
            }
            if (prefab == null) return null;

            var root = new GameObject(definitionId);
            root.transform.SetPositionAndRotation(position, yaw);
            var model = UnityEngine.Object.Instantiate(prefab, root.transform, false);
            model.name = definitionId + " (model)";
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                var sourceMaterials = renderer.sharedMaterials;
                var remapped = new Material[sourceMaterials.Length];
                for (var i = 0; i < sourceMaterials.Length; i++)
                    remapped[i] = UrpVariant(sourceMaterials[i]);
                renderer.sharedMaterials = remapped;
            }
            return root;
        }

        private static Material UrpVariant(Material? source)
        {
            if (source == null) return Wood;
            if (UrpMaterialVariants.TryGetValue(source, out var cached) && cached != null) return cached;
            var material = new Material(source) { name = source.name + " (arch URP)" };
            material.doubleSidedGI = true;
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.08f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            material.enableInstancing = true;
            UrpMaterialVariants[source] = material;
            return material;
        }

        private static GameObject BuildSupport(BlueprintElementData element, BuildingBlueprintDraft draft)
        {
            var point = BlueprintGeometry.ToWorld(element.Node);
            var position = new Vector3(point.X, HutAssembly.FloorSurfaceLift, point.Y);
            // A corner post owns the joint where a bay drops its own pair, so it
            // has to READ as that same post. Its two sticks separate along local
            // +Z, and it used to be placed unrotated — the pair then pointed a
            // random way and the corner looked like a beam was simply missing.
            // Aim it outward along the radius, which bisects the two walls.
            var outward = DoorOutward(element, draft, position);
            var model = InstantiateModel("architecture.support.wood", position,
                Quaternion.LookRotation(outward, Vector3.up));
            if (model != null) return model;

            var root = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            root.transform.localPosition = new Vector3(
                point.X, HutAssembly.FloorSurfaceLift + SupportHeight * 0.5f, point.Y);
            root.transform.localScale = new Vector3(0.065f, SupportHeight * 0.5f, 0.065f);
            root.GetComponent<Renderer>().sharedMaterial = Bark;
            return root;
        }

        /// <summary>
        /// Aims the sector model at its bisector. The FBX axis conversion lands
        /// the authored +X on Unity's -X, so the triangle came out pointing
        /// exactly 180 deg the wrong way: measured in the preview root's own
        /// space the error was 178.3-179.6 deg on every sector, which is why a
        /// click resolved one sector and highlighted the one opposite it.
        /// Deriving this by hand got the sign wrong and drew every floor and
        /// roof triangle mirrored about the X axis — 1.34 wu away from the
        /// sector the click resolved to, so selection and deletion looked
        /// broken. LookRotation takes the direction itself, so there is no
        /// hand-rolled convention left to get backwards.
        /// </summary>
        private static Quaternion SectorYaw(Vector3 bisector) =>
            Quaternion.LookRotation(bisector.normalized, Vector3.up) *
            Quaternion.Euler(0f, 90f, 0f);

        private static GameObject BuildFloor(BlueprintElementData element)
        {
            var sector = element.FloorSector;
            var center = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(sector.Hex));
            var a = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector));
            var b = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector + 1));
            // Authored floor triangle spans local +X; aim it along the sector bisector.
            var bisector = new Vector3(
                (a.X + b.X) * 0.5f - center.X, 0f, (a.Y + b.Y) * 0.5f - center.Y);
            var model = InstantiateModel(
                "architecture.floor.board",
                new Vector3(center.X, 0f, center.Y),
                SectorYaw(bisector));
            if (model != null) return model;

            return Triangle(
                $"Floor sector {sector}",
                center, a, b,
                HutAssembly.FloorSurfaceLift, Wood, 0.055f);
        }

        /// <summary>
        /// Height of the roof eave: the top of a finished wall board (2.164 in
        /// the authored wall) above the walkable deck.
        /// </summary>
        public const float RoofEaveHeight = HutAssembly.FloorSurfaceLift + 2.164f;

        /// <summary>
        /// A hex keeps its dome only when all six of its sectors are roofed.
        /// A hex the room merely reaches into is roofed flat and low instead:
        /// a half-built dome is the shape that produced the visible gaps, and
        /// the player wants the finished hex to stay a dome while the annex
        /// reads as a lower flat roof beside it.
        /// </summary>
        private static bool IsHexFullyRoofed(RoofSectorKey sector, BuildingBlueprintDraft draft)
        {
            for (var index = 0; index < 6; index++)
            {
                var key = new RoofSectorKey(sector.Hex, index);
                if (!draft.Elements.Any(other =>
                        other.Kind == BlueprintElementKind.RoofSector && other.RoofSector == key))
                    return false;
            }
            return true;
        }

        private static GameObject BuildRoof(BlueprintElementData element, BuildingBlueprintDraft draft)
        {
            var sector = element.RoofSector;
            var center = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(sector.Hex));
            var a = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector));
            var b = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector + 1));
            var bisector = new Vector3(
                (a.X + b.X) * 0.5f - center.X, 0f, (a.Y + b.Y) * 0.5f - center.Y);
            // The authored panel rises from the outer hex edge to the hex centre,
            // so neighbouring hexes always meet along a shared edge at each
            // one's LOW point — the same height whatever shape the room is.
            // That is what lets a stretched room roof itself with no joiner.
            // Sloping EVERY sector down to its own hex edge tiles perfectly but
            // digs a valley along every shared edge — a gutter through the
            // middle of the house, and that crease is exactly where the player
            // saw gaps. A sector whose outer edge faces another roofed sector is
            // inside the room, so it stays flat at ridge height; only the outer
            // ring keeps its skirt.
            var whole = IsHexFullyRoofed(element.RoofSector, draft);
            var model = InstantiateModel(
                whole ? "architecture.roof.palm" : "architecture.roof.palm.flat",
                new Vector3(center.X, RoofEaveHeight, center.Y),
                SectorYaw(bisector));
            if (model != null) return model;

            return Triangle(
                $"Roof sector {sector}", center, a, b,
                RoofEaveHeight, Leaf, 0.025f, 0.22f);
        }

        private static GameObject BuildBay(
            BlueprintElementData element,
            BuildingBlueprintDraft draft,
            BlueprintElementKind kind)
        {
            var root = new GameObject(kind.ToString());
            var a2 = BlueprintGeometry.ToWorld(element.Segment.A);
            var b2 = BlueprintGeometry.ToWorld(element.Segment.B);
            var a = new Vector3(a2.X, HutAssembly.FloorSurfaceLift, a2.Y);
            var b = new Vector3(b2.X, HutAssembly.FloorSurfaceLift, b2.Y);
            var direction = b - a;
            var yaw = Quaternion.LookRotation(direction.normalized, Vector3.up);
            var midpoint = (a + b) * 0.5f;

            var definitionId = kind switch
            {
                BlueprintElementKind.Wall => "architecture.wall.wood",
                BlueprintElementKind.Window => "architecture.window.wood",
                _ => "architecture.door.wood"
            };
            var authored = InstantiateModel(definitionId, midpoint, yaw);
            if (authored != null)
            {
                DropPostsOwnedByASupport(authored, element, draft);
                if (kind == BlueprintElementKind.Door)
                    ConfigureDoorPivot(authored, element, draft, midpoint, direction.normalized);
                return authored;
            }

            if (kind == BlueprintElementKind.Wall)
            {
                for (var board = 0; board < 3; board++)
                    AddCube(root.transform, $"board_{board}",
                        midpoint + Vector3.up * (0.25f + board * 0.5f), yaw,
                        new Vector3(0.09f, 0.46f, direction.magnitude * 0.96f),
                        board == 1 ? WoodLight : Wood);
                return root;
            }

            if (kind == BlueprintElementKind.Window)
            {
                AddCube(root.transform, "window_sill", midpoint + Vector3.up * 0.30f,
                    yaw, new Vector3(0.09f, 0.56f, direction.magnitude * 0.96f), Wood);
                AddCube(root.transform, "window_lintel", midpoint + Vector3.up * 1.36f,
                    yaw, new Vector3(0.09f, 0.24f, direction.magnitude * 0.96f), WoodLight);
                return root;
            }

            var along = direction.normalized;
            AddCube(root.transform, "door_jamb_a", a + along * 0.035f + Vector3.up * 0.75f,
                yaw, new Vector3(0.10f, 1.5f, 0.07f), Bark);
            AddCube(root.transform, "door_jamb_b", b - along * 0.035f + Vector3.up * 0.75f,
                yaw, new Vector3(0.10f, 1.5f, 0.07f), Bark);
            AddCube(root.transform, "door_lintel", midpoint + Vector3.up * 1.45f,
                yaw, new Vector3(0.10f, 0.12f, direction.magnitude), Bark);

            var outward = DoorOutward(element, draft, midpoint);
            var positive = Quaternion.AngleAxis(72f, Vector3.up) * along;
            var negative = Quaternion.AngleAxis(-72f, Vector3.up) * along;
            var openAngle = Vector3.Dot(positive, outward) >= Vector3.Dot(negative, outward) ? 72f : -72f;
            var pivot = new GameObject("DoorPivot").transform;
            pivot.SetParent(root.transform, true);
            pivot.position = a + along * 0.05f;
            pivot.rotation = yaw;
            AddCube(pivot, "door_leaf", new Vector3(0f, 0.72f, direction.magnitude * 0.43f),
                Quaternion.identity, new Vector3(0.075f, 1.34f, direction.magnitude * 0.82f),
                WoodLight, false);
            var visual = pivot.gameObject.AddComponent<BlueprintDoorVisual>();
            visual.Configure(openAngle, true);
            return root;
        }

        /// <summary>
        /// A bay authors its post pair on its far (+Z) node. When a standalone
        /// Support element already owns that node — every hex corner has one —
        /// the bay must not build a second pair on top of it, or each corner
        /// grows two overlapping sets of posts. The corner post is the Support.
        /// </summary>
        private static void DropPostsOwnedByASupport(
            GameObject authored, BlueprintElementData element, BuildingBlueprintDraft draft)
        {
            var node = element.Segment.B;
            var owned = draft.Elements.Any(other =>
                other.Kind == BlueprintElementKind.Support && other.Node == node);
            if (!owned) return;

            // Drop the whole bundle, posts AND their lashing. Removing only the
            // posts left the rope rings hanging in mid-air over the joint.
            var posts = FindStage(authored.transform, 1);
            if (posts != null)
            {
                foreach (var child in posts.Cast<Transform>().ToArray())
                {
                    if (!child.name.Contains("_stick_")) continue;
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }
            var lashing = FindStage(authored.transform, 3);
            if (lashing == null) return;
            foreach (var child in lashing.Cast<Transform>().ToArray())
            {
                if (!child.name.Contains("_rope")) continue;
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }

        private static void ConfigureDoorPivot(
            GameObject authored, BlueprintElementData element, BuildingBlueprintDraft draft,
            Vector3 midpoint, Vector3 along)
        {
            Transform? pivot = null;
            Transform? openMarker = null;
            Transform? closedMarker = null;
            foreach (var child in authored.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.StartsWith("HL_Door_Pivot", System.StringComparison.Ordinal)) pivot = child;
                else if (child.name.StartsWith("HL_Door_State_Open", System.StringComparison.Ordinal)) openMarker = child;
                else if (child.name.StartsWith("HL_Door_State_Closed", System.StringComparison.Ordinal)) closedMarker = child;
            }
            if (pivot == null) return;

            var visual = pivot.gameObject.AddComponent<BlueprintDoorVisual>();
            if (openMarker != null && closedMarker != null)
            {
                // The FBX import conversion means the pivot's local Y is not the
                // world vertical; the authored state markers carry the correct
                // local poses, so open/close interpolates between them (§120.3).
                var closed = closedMarker.localRotation;
                var open = openMarker.localRotation;
                // Mirror the swing when the colony floor lies on the authored
                // open side, so the leaf always opens outward.
                var outward = DoorOutward(element, draft, midpoint);
                var openWorld = pivot.parent == null
                    ? open
                    : pivot.parent.rotation * open;
                var closedWorld = pivot.parent == null
                    ? closed
                    : pivot.parent.rotation * closed;
                var swingWorld = openWorld * Quaternion.Inverse(closedWorld);
                var swingLocal = open * Quaternion.Inverse(closed);
                if (Vector3.Dot(swingWorld * along - along, outward) < 0f)
                    open = Quaternion.Inverse(swingLocal) * closed;
                visual.ConfigurePoses(closed, open, true);
            }
            else
            {
                visual.Configure(72f, true);
            }
        }

        private static Vector3 DoorOutward(
            BlueprintElementData door, BuildingBlueprintDraft draft, Vector3 midpoint)
        {
            var floors = draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector &&
                    (door.RoomId <= 0 || element.RoomId == door.RoomId))
                .Select(element => element.FloorSector).ToArray();
            if (floors.Length == 0) return midpoint.normalized;
            var centroid = Vector3.zero;
            foreach (var floor in floors)
            {
                var center = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(floor.Hex));
                var a = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(floor.Hex, floor.Sector));
                var b = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(floor.Hex, floor.Sector + 1));
                centroid += new Vector3((center.X + a.X + b.X) / 3f, 0f, (center.Y + a.Y + b.Y) / 3f);
            }
            centroid /= floors.Length;
            var outward = midpoint - centroid;
            outward.y = 0f;
            return outward.sqrMagnitude < 0.001f ? midpoint.normalized : outward.normalized;
        }

        private static GameObject Triangle(
            string name, Float2 center, Float2 a, Float2 b,
            float y, Material material, float thickness, float centerRise = 0f)
        {
            var root = new GameObject(name);
            var mesh = new Mesh { name = name + " mesh" };
            mesh.vertices = new[]
            {
                new Vector3(center.X, y + centerRise, center.Y),
                new Vector3(a.X, y, a.Y),
                new Vector3(b.X, y, b.Y),
                new Vector3(center.X, y - thickness + centerRise, center.Y),
                new Vector3(a.X, y - thickness, a.Y),
                new Vector3(b.X, y - thickness, b.Y)
            };
            mesh.triangles = new[]
                { 0, 1, 2, 5, 4, 3, 0, 3, 4, 0, 4, 1, 1, 4, 5, 1, 5, 2, 2, 5, 3, 2, 3, 0 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>().sharedMaterial = material;
            root.AddComponent<MeshCollider>().sharedMesh = mesh;
            return root;
        }

        private static void AddCube(
            Transform parent, string name, Vector3 position, Quaternion rotation,
            Vector3 scale, Material material, bool worldSpace = true)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, worldPositionStays: worldSpace);
            if (worldSpace)
            {
                cube.transform.position = position;
                cube.transform.rotation = rotation;
            }
            else
            {
                cube.transform.localPosition = position;
                cube.transform.localRotation = rotation;
            }
            cube.transform.localScale = scale;
            cube.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static Material Wood => _wood ??= CreateMaterial(
            "BlueprintWood", new Color(0.43f, 0.24f, 0.11f));
        private static Material WoodLight => _woodLight ??= CreateMaterial(
            "BlueprintWoodLight", new Color(0.63f, 0.39f, 0.18f));
        private static Material Bark => _bark ??= CreateMaterial(
            "BlueprintBark", new Color(0.24f, 0.13f, 0.07f));
        private static Material Leaf => _leaf ??= CreateMaterial(
            "BlueprintLeaf", new Color(0.18f, 0.38f, 0.16f));

        private static Material CreateMaterial(string name, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }
    }

    public sealed class BlueprintDoorVisual : MonoBehaviour
    {
        private bool _open;
        private Quaternion _closedRotation;
        private Quaternion _openRotation;

        public bool IsOpen => _open;

        public void Configure(float openAngle, bool startOpen)
        {
            _closedRotation = transform.localRotation;
            _openRotation = _closedRotation * Quaternion.Euler(0f, openAngle, 0f);
            SetOpen(startOpen);
        }

        /// <summary>Authored FBX doors carry explicit closed/open local poses.</summary>
        public void ConfigurePoses(Quaternion closed, Quaternion open, bool startOpen)
        {
            _closedRotation = closed;
            _openRotation = open;
            SetOpen(startOpen);
        }

        public void SetOpen(bool open)
        {
            _open = open;
            transform.localRotation = open ? _openRotation : _closedRotation;
        }
    }
}
