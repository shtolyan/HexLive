using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

public sealed class HexWorldRenderer : MonoBehaviour
{
    [SerializeField] private SimulationRunnerBehaviour? _runner;

    private const float JunctionMarkerScaleFactor = 1f / 25f;
    private const float EdgeLineHeight = 0.01f;

    private const float NpcRadiusFactor = 17f / 75f;
    private const float NpcHeightFactor = 11f / 30f;

    private const float BedWidthFactor = 7f / 25f;
    private const float BedHeightFactor = 2f / 25f;
    private const float BedDepthFactor = 14f / 75f;

    private const float ChairRadiusFactor = 3f / 25f;
    private const float ChairHeightFactor = 7f / 75f;

    private const float ClothingRadiusFactor = 7f / 75f;
    private const float ClothingHeightFactor = 2f / 15f;

    private const float FoodRadiusFactor = 7f / 75f;

    private readonly Dictionary<TileCoord, GameObject> _tileViews = new();
    private readonly Dictionary<int, GameObject> _junctionViews = new();
    private readonly Dictionary<int, GameObject> _objectViews = new();
    private readonly Dictionary<int, GameObject> _npcViews = new();

    private Transform? _tilesRoot;
    private Transform? _junctionsRoot;
    private Transform? _objectsRoot;
    private Transform? _npcsRoot;
    private int _lastRenderedTick = -1;

    private WorldSnapshot? _lastSnapshot;

    private readonly struct Pose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public Pose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    private readonly Dictionary<int, Pose> _prevNpcPoses = new();
    private readonly Dictionary<int, Pose> _currNpcPoses = new();
    private readonly Dictionary<int, Vector3> _prevObjectPositions = new();
    private readonly Dictionary<int, Vector3> _currObjectPositions = new();

    private float HexRadius => SimulationUnityMapper.HexRadius;

    private float JunctionMarkerScale => HexRadius * JunctionMarkerScaleFactor;

    public void SetRunner(SimulationRunnerBehaviour runner)
    {
        _runner = runner;
    }

    private void Update()
    {
        _runner ??= FindFirstObjectByType<SimulationRunnerBehaviour>();
        if (_runner is null || !_runner.IsReady)
        {
            return;
        }

        var snapshot = _runner.CreateSnapshot();
        if (snapshot is null)
        {
            return;
        }

        EnsureRoots();

        if (snapshot.Tick != _lastRenderedTick)
        {
            RenderSnapshot(snapshot);
            _lastRenderedTick = snapshot.Tick;
            _lastSnapshot = snapshot;
        }

        InterpolateMovables(_runner.TickAlpha);
    }

    private void EnsureRoots()
    {
        _tilesRoot ??= CreateRoot("Tiles");
        _junctionsRoot ??= CreateRoot("Junctions");
        _objectsRoot ??= CreateRoot("Objects");
        _npcsRoot ??= CreateRoot("NPCs");
    }

    private Transform CreateRoot(string label)
    {
        var child = transform.Find(label);
        if (child is not null)
        {
            return child;
        }

        var go = new GameObject(label);
        go.transform.SetParent(transform, false);
        return go.transform;
    }

    private void RenderSnapshot(WorldSnapshot snapshot)
    {
        foreach (var tile in snapshot.Tiles)
        {
            if (!_tileViews.ContainsKey(tile.Coord))
            {
                _tileViews[tile.Coord] = CreateTileView(tile);
            }
        }

        // Build lookup for junction positions
        var junctionPositions = new Dictionary<int, Float2>();
        foreach (var junction in snapshot.Junctions)
        {
            junctionPositions[junction.Id.Value] = junction.WorldPosition;

            var key = junction.Id.Value;
            if (!_junctionViews.ContainsKey(key))
            {
                _junctionViews[key] = CreateJunctionView(junction);
            }
        }

        foreach (var worldObject in snapshot.Objects)
        {
            var key = worldObject.Id.Value;
            if (!_objectViews.TryGetValue(key, out var objectView))
            {
                objectView = CreateObjectView(worldObject, junctionPositions);
                _objectViews[key] = objectView;
            }

            var objPos = GetObjectAnchorPosition(snapshot, worldObject);

            if (_currObjectPositions.TryGetValue(key, out var oldObjPos))
            {
                _prevObjectPositions[key] = oldObjPos;
            }
            else
            {
                _prevObjectPositions[key] = objPos;
            }

            _currObjectPositions[key] = objPos;
        }

        foreach (var npc in snapshot.Npcs)
        {
            var key = npc.Id.Value;
            if (!_npcViews.TryGetValue(key, out var npcView))
            {
                npcView = CreateNpcView(npc);
                _npcViews[key] = npcView;
            }

            var targetPos = SimulationUnityMapper.ToUnityPosition(npc.Position, SimulationUnityMapper.TileHeight);
            var targetRot = Quaternion.Euler(0f, SimulationUnityMapper.ToUnityYawDegrees(npc.RotationDegrees), 0f);
            var targetPose = new Pose(targetPos, targetRot);

            if (_currNpcPoses.TryGetValue(key, out var oldPose))
            {
                _prevNpcPoses[key] = oldPose;
            }
            else
            {
                _prevNpcPoses[key] = targetPose;
            }

            _currNpcPoses[key] = targetPose;
        }
    }

    private void InterpolateMovables(float alpha)
    {
        foreach (var kvp in _npcViews)
        {
            var key = kvp.Key;
            var view = kvp.Value;

            if (!_currNpcPoses.TryGetValue(key, out var curr))
            {
                continue;
            }

            if (_prevNpcPoses.TryGetValue(key, out var prev))
            {
                view.transform.position = Vector3.Lerp(prev.Position, curr.Position, alpha);
                view.transform.rotation = Quaternion.Slerp(prev.Rotation, curr.Rotation, alpha);
            }
            else
            {
                view.transform.position = curr.Position;
                view.transform.rotation = curr.Rotation;
            }
        }

        foreach (var kvp in _objectViews)
        {
            var key = kvp.Key;
            var view = kvp.Value;

            if (!_currObjectPositions.TryGetValue(key, out var curr))
            {
                continue;
            }

            if (_prevObjectPositions.TryGetValue(key, out var prev))
            {
                view.transform.position = Vector3.Lerp(prev, curr, alpha);
            }
            else
            {
                view.transform.position = curr;
            }
        }
    }

    private GameObject CreateTileView(TileSnapshot tile)
    {
        var go = new GameObject($"Hex {tile.Coord.Q},{tile.Coord.R}");
        go.transform.SetParent(_tilesRoot, false);
        go.transform.position = SimulationUnityMapper.ToUnityTilePosition(tile.Coord);

        var meshFilter = go.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = BuildHexMesh(HexRadius, SimulationUnityMapper.TileHeight);

        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = CreateMaterial(GetTileColor(tile));

        return go;
    }

    private GameObject CreateJunctionView(JunctionSnapshot junction)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = $"Junction {junction.Id.Value}";
        go.transform.SetParent(_junctionsRoot, false);
        var scale = junction.Blocked ? JunctionMarkerScale * 4f : JunctionMarkerScale;
        go.transform.localScale = Vector3.one * scale;
        go.transform.position = SimulationUnityMapper.ToUnityPosition(
            junction.WorldPosition,
            SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift);

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            renderer.sharedMaterial = CreateMaterial(GetJunctionColor(junction));
        }

        return go;
    }

    private GameObject CreateObjectView(ObjectSnapshot worldObject, Dictionary<int, Float2> junctionPositions)
    {
        var primitiveType = GetObjectPrimitive(worldObject.DefinitionId);
        var scale = GetObjectScale(worldObject.DefinitionId);
        var root = new GameObject($"Object {worldObject.DefinitionId}");
        root.transform.SetParent(_objectsRoot, false);

        var visual = CreatePrimitiveVisual(root.transform, primitiveType, scale, GetObjectColor(worldObject.DefinitionId));
        visual.name = "Visual";

        var pos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
        root.transform.position = SimulationUnityMapper.ToUnityPosition(pos, SimulationUnityMapper.TileHeight);
        return root;
    }

    private GameObject CreateNpcView(NpcSnapshot npc)
    {
        var root = new GameObject($"NPC {npc.Id.Value}");
        root.transform.SetParent(_npcsRoot, false);

        var bodyRadius = HexRadius * NpcRadiusFactor;
        var bodyHeight = HexRadius * NpcHeightFactor;
        var headRadius = bodyRadius * 0.7f;

        var skinColor = new Color(0.88f, 0.84f, 0.72f);
        var visorColor = new Color(0.08f, 0.08f, 0.08f);

        var body = CreatePrimitiveVisual(
            root.transform,
            PrimitiveType.Cylinder,
            new Vector3(bodyRadius, bodyHeight, bodyRadius),
            skinColor);
        body.name = "Body";

        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        head.transform.SetParent(root.transform, false);
        head.transform.localScale = Vector3.one * headRadius;
        head.transform.localPosition = new Vector3(0f, bodyHeight * 2f + headRadius * 0.5f, 0f);
        var headRenderer = head.GetComponent<MeshRenderer>();
        if (headRenderer is not null)
        {
            headRenderer.sharedMaterial = CreateMaterial(skinColor);
        }

        var visor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visor.name = "Visor";
        visor.transform.SetParent(head.transform, false);
        visor.transform.localScale = new Vector3(0.85f, 0.35f, 0.3f);
        visor.transform.localPosition = new Vector3(0f, 0.05f, 0.4f);
        var visorRenderer = visor.GetComponent<MeshRenderer>();
        if (visorRenderer is not null)
        {
            visorRenderer.sharedMaterial = CreateMaterial(visorColor);
        }

        return root;
    }

    private Vector3 GetObjectAnchorPosition(WorldSnapshot snapshot, ObjectSnapshot worldObject)
    {
        if (worldObject.Junctions.Count > 0)
        {
            foreach (var junction in snapshot.Junctions)
            {
                if (junction.Id.Equals(worldObject.Junctions[0]))
                {
                    return SimulationUnityMapper.ToUnityPosition(junction.WorldPosition, SimulationUnityMapper.TileHeight);
                }
            }
        }

        return SimulationUnityMapper.ToUnityTilePosition(worldObject.Tile, SimulationUnityMapper.TileHeight);
    }

    private static Float2 GetObjectAnchorFromJunctions(ObjectSnapshot worldObject, Dictionary<int, Float2> junctionPositions)
    {
        if (worldObject.Junctions.Count > 0 && junctionPositions.TryGetValue(worldObject.Junctions[0].Value, out var pos))
        {
            return pos;
        }

        return HexSpatialMath.TileToWorld(worldObject.Tile);
    }

    private static Mesh BuildHexMesh(float radius, float height)
    {
        var mesh = new Mesh
        {
            name = "HexTile"
        };

        var vertices = new List<Vector3> { new(0f, height, 0f) };
        var triangles = new List<int>();

        for (var i = 0; i < 6; i++)
        {
            var angle = Mathf.Deg2Rad * (60f * i - 30f);
            vertices.Add(new Vector3(radius * Mathf.Cos(angle), height, radius * Mathf.Sin(angle)));
        }

        for (var i = 1; i <= 6; i++)
        {
            var next = i == 6 ? 1 : i + 1;
            triangles.Add(0);
            triangles.Add(next);
            triangles.Add(i);
        }

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        return mesh;
    }

    private static PrimitiveType GetObjectPrimitive(string definitionId)
    {
        if (definitionId.Contains("bed"))
        {
            return PrimitiveType.Cube;
        }

        if (definitionId.Contains("chair"))
        {
            return PrimitiveType.Cylinder;
        }

        if (definitionId.Contains("clothing"))
        {
            return PrimitiveType.Capsule;
        }

        return PrimitiveType.Sphere;
    }

    private static Vector3 GetObjectScale(string definitionId)
    {
        if (definitionId.Contains("bed"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * BedWidthFactor,
                HexSpatialMath.HexRadius * BedHeightFactor,
                HexSpatialMath.HexRadius * BedDepthFactor);
        }

        if (definitionId.Contains("chair"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * ChairRadiusFactor,
                HexSpatialMath.HexRadius * ChairHeightFactor,
                HexSpatialMath.HexRadius * ChairRadiusFactor);
        }

        if (definitionId.Contains("clothing"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * ClothingRadiusFactor,
                HexSpatialMath.HexRadius * ClothingHeightFactor,
                HexSpatialMath.HexRadius * ClothingRadiusFactor);
        }

        return Vector3.one * (HexSpatialMath.HexRadius * FoodRadiusFactor);
    }

    private static GameObject CreatePrimitiveVisual(Transform parent, PrimitiveType primitiveType, Vector3 scale, Color color)
    {
        var visual = GameObject.CreatePrimitive(primitiveType);
        visual.transform.SetParent(parent, false);
        visual.transform.localScale = scale;
        visual.transform.localPosition = new Vector3(0f, GetBottomOffset(primitiveType, scale), 0f);

        var renderer = visual.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            renderer.sharedMaterial = CreateMaterial(color);
        }

        return visual;
    }

    private static float GetBottomOffset(PrimitiveType primitiveType, Vector3 scale)
    {
        switch (primitiveType)
        {
            case PrimitiveType.Capsule:
            case PrimitiveType.Cylinder:
                return scale.y;
            case PrimitiveType.Cube:
            case PrimitiveType.Sphere:
            default:
                return scale.y * 0.5f;
        }
    }

    private static Color GetTileColor(TileSnapshot tile)
    {
        if (tile.Blocked)
        {
            return new Color(0.38f, 0.29f, 0.24f);
        }

        if (!tile.Walkable)
        {
            return new Color(0.27f, 0.26f, 0.25f);
        }

        return tile.Indoor
            ? new Color(0.72f, 0.67f, 0.56f)
            : new Color(0.49f, 0.61f, 0.47f);
    }

    private static Color GetJunctionColor(JunctionSnapshot junction)
    {
        if (junction.Occupied)
        {
            return new Color(0.83f, 0.19f, 0.19f);
        }

        if (junction.Reserved)
        {
            return new Color(0.95f, 0.45f, 0.12f);
        }

        if (junction.Blocked)
        {
            return new Color(0.45f, 0.15f, 0.15f);
        }

        return junction.Tiles.Count > 1
            ? new Color(0.24f, 0.78f, 0.34f)
            : new Color(0.94f, 0.94f, 0.94f);
    }

    private static Color GetObjectColor(string definitionId)
    {
        if (definitionId.Contains("bed"))
        {
            return new Color(0.39f, 0.52f, 0.78f);
        }

        if (definitionId.Contains("chair"))
        {
            return new Color(0.75f, 0.48f, 0.22f);
        }

        if (definitionId.Contains("clothing"))
        {
            return new Color(0.22f, 0.48f, 0.82f);
        }

        return new Color(0.82f, 0.18f, 0.16f);
    }

    private static Material CreateMaterial(Color color)
    {
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = color
        };

        return material;
    }
}

}
