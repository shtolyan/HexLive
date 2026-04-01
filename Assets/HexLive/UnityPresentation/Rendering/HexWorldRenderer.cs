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

    private const float PointMarkerScaleFactor = 1f / 25f;

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
    private readonly Dictionary<int, GameObject> _pointViews = new();
    private readonly Dictionary<int, GameObject> _objectViews = new();
    private readonly Dictionary<int, GameObject> _npcViews = new();

    private Transform? _tilesRoot;
    private Transform? _pointsRoot;
    private Transform? _objectsRoot;
    private Transform? _npcsRoot;
    private int _lastRenderedTick = -1;

    private float HexRadius => SimulationUnityMapper.HexRadius;

    private float PointMarkerScale => HexRadius * PointMarkerScaleFactor;

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
        if (snapshot is null || snapshot.Tick == _lastRenderedTick)
        {
            return;
        }

        EnsureRoots();
        RenderSnapshot(snapshot);
        _lastRenderedTick = snapshot.Tick;
    }

    private void EnsureRoots()
    {
        _tilesRoot ??= CreateRoot("Tiles");
        _pointsRoot ??= CreateRoot("Points");
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

        foreach (var point in snapshot.Points)
        {
            var key = point.Id.Value;
            if (!_pointViews.ContainsKey(key))
            {
                _pointViews[key] = CreatePointView(point);
            }
        }

        foreach (var worldObject in snapshot.Objects)
        {
            var key = worldObject.Id.Value;
            if (!_objectViews.TryGetValue(key, out var objectView))
            {
                objectView = CreateObjectView(worldObject);
                _objectViews[key] = objectView;
            }

            objectView.transform.position = GetObjectAnchorPosition(snapshot, worldObject);
        }

        foreach (var npc in snapshot.Npcs)
        {
            var key = npc.Id.Value;
            if (!_npcViews.TryGetValue(key, out var npcView))
            {
                npcView = CreateNpcView(npc);
                _npcViews[key] = npcView;
            }

            npcView.transform.position = SimulationUnityMapper.ToUnityPosition(npc.Position, SimulationUnityMapper.TileHeight);
            npcView.transform.rotation = Quaternion.Euler(0f, npc.RotationDegrees, 0f);
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

    private GameObject CreatePointView(PointSnapshot point)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = $"Point {point.Id.Value}";
        go.transform.SetParent(_pointsRoot, false);
        go.transform.localScale = Vector3.one * PointMarkerScale;
        go.transform.position = SimulationUnityMapper.ToUnityPointPosition(
            point.WorldPosition,
            SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift);

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            renderer.sharedMaterial = CreateMaterial(GetPointColor(point));
        }

        return go;
    }

    private GameObject CreateObjectView(ObjectSnapshot worldObject)
    {
        var primitiveType = GetObjectPrimitive(worldObject.DefinitionId);
        var scale = GetObjectScale(worldObject.DefinitionId);
        var root = new GameObject($"Object {worldObject.DefinitionId}");
        root.transform.SetParent(_objectsRoot, false);

        var visual = CreatePrimitiveVisual(root.transform, primitiveType, scale, GetObjectColor(worldObject.DefinitionId));
        visual.name = "Visual";
        root.transform.position = GetObjectAnchorPosition(_runner!.CreateSnapshot()!, worldObject);
        return root;
    }

    private GameObject CreateNpcView(NpcSnapshot npc)
    {
        var root = new GameObject($"NPC {npc.Id.Value}");
        root.transform.SetParent(_npcsRoot, false);

        var visual = CreatePrimitiveVisual(
            root.transform,
            PrimitiveType.Capsule,
            new Vector3(HexRadius * NpcRadiusFactor, HexRadius * NpcHeightFactor, HexRadius * NpcRadiusFactor),
            new Color(0.88f, 0.84f, 0.72f));
        visual.name = "Visual";
        return root;
    }

    private Vector3 GetObjectAnchorPosition(WorldSnapshot snapshot, ObjectSnapshot worldObject)
    {
        if (worldObject.Points.Count > 0)
        {
            foreach (var point in snapshot.Points)
            {
                if (point.Id == worldObject.Points[0])
                {
                    return SimulationUnityMapper.ToUnityPointPosition(point.WorldPosition, SimulationUnityMapper.TileHeight);
                }
            }
        }

        return SimulationUnityMapper.ToUnityTilePosition(worldObject.Tile, SimulationUnityMapper.TileHeight);
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

    private static Color GetPointColor(PointSnapshot point)
    {
        if (point.Occupied)
        {
            return new Color(0.83f, 0.19f, 0.19f);
        }

        if (point.Reserved)
        {
            return new Color(0.95f, 0.45f, 0.12f);
        }

        return point.Kind == HexLive.Simulation.Spatial.PointKind.Connection
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
