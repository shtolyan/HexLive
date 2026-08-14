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

        public static GameObject Build(BlueprintElementData element, BuildingBlueprintDraft draft)
        {
            return element.Kind switch
            {
                BlueprintElementKind.Support => BuildSupport(element),
                BlueprintElementKind.FloorSector => BuildFloor(element),
                BlueprintElementKind.Wall => BuildBay(element, draft, BlueprintElementKind.Wall),
                BlueprintElementKind.Window => BuildBay(element, draft, BlueprintElementKind.Window),
                BlueprintElementKind.Door => BuildBay(element, draft, BlueprintElementKind.Door),
                BlueprintElementKind.RoofSector => BuildRoof(element),
                _ => new GameObject($"Unsupported blueprint element {element.Kind}")
            };
        }

        private static GameObject BuildSupport(BlueprintElementData element)
        {
            var point = BlueprintGeometry.ToWorld(element.Node);
            var root = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            root.transform.localPosition = new Vector3(
                point.X, HutAssembly.FloorSurfaceLift + SupportHeight * 0.5f, point.Y);
            root.transform.localScale = new Vector3(0.065f, SupportHeight * 0.5f, 0.065f);
            root.GetComponent<Renderer>().sharedMaterial = Bark;
            return root;
        }

        private static GameObject BuildFloor(BlueprintElementData element)
        {
            var sector = element.FloorSector;
            return Triangle(
                $"Floor sector {sector}",
                BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(sector.Hex)),
                BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector)),
                BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector + 1)),
                HutAssembly.FloorSurfaceLift, Wood, 0.055f);
        }

        private static GameObject BuildRoof(BlueprintElementData element)
        {
            var sector = element.RoofSector;
            return Triangle(
                $"Roof sector {sector}",
                BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(sector.Hex)),
                BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector)),
                BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector + 1)),
                SupportHeight + HutAssembly.FloorSurfaceLift, Leaf, 0.025f, 0.22f);
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
        private float _openAngle;
        private bool _open;
        private Quaternion _closedRotation;

        public bool IsOpen => _open;

        public void Configure(float openAngle, bool startOpen)
        {
            _openAngle = openAngle;
            _closedRotation = transform.localRotation;
            SetOpen(startOpen);
        }

        public void SetOpen(bool open)
        {
            _open = open;
            transform.localRotation = _closedRotation * Quaternion.Euler(0f, open ? _openAngle : 0f, 0f);
        }
    }
}
