#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

public sealed class HexWorldRenderer : MonoBehaviour
{
    [SerializeField] private SimulationRunnerBehaviour? _runner;

    // Spec 31.17: ~14k junction spheres are ~10M triangles — debug only.
    [SerializeField] private bool _showJunctionMarkers;

    // Spec 20.16: terrain-style grass tufts on grass tiles.
    [SerializeField] private bool _grassDetail = true;
    [SerializeField, Range(0, 30)] private int _grassBladesPerTile = 12;

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

    private const float TreeRadiusFactor = 4f / 25f;
    private const float TreeHeightFactor = 1f / 2f;

    private readonly Dictionary<TileCoord, GameObject> _tileViews = new();
    private readonly Dictionary<int, GameObject> _junctionViews = new();
    private readonly Dictionary<int, GameObject> _objectViews = new();
    private readonly Dictionary<int, GameObject> _npcViews = new();

    // Spec 31B.5: actor-backed views (Marta/Molly/Jana bodies); primitive
    // capsules remain the fallback when no actor prefab matches.
    private readonly Dictionary<int, NpcActorView> _actorViews = new();

    // Spec 31C: the fauna is finally visible.
    private readonly Dictionary<int, GameObject> _dogViews = new();
    private readonly Dictionary<int, GameObject> _crabViews = new();
    private readonly Dictionary<int, Pose> _prevAnimalPoses = new();
    private readonly Dictionary<int, Pose> _currAnimalPoses = new();
    private const float ActorSourceHeightMeters = 1.7f;

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
        _runner ??= FindAnyObjectByType<SimulationRunnerBehaviour>();
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

    private GameObject _seaPlane;

    private void EnsureSeaPlane()
    {
        if (_seaPlane != null)
        {
            return;
        }

        // Spec 20.16: the ocean extends past the playable bounds.
        _seaPlane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _seaPlane.name = "Sea";
        _seaPlane.transform.SetParent(transform, false);
        _seaPlane.transform.position = new Vector3(
            0f, SimulationUnityMapper.TileHeight - ElevationStep * 0.45f, 0f);
        _seaPlane.transform.localScale = new Vector3(40f, 1f, 40f); // 400x400 units
        var renderer = _seaPlane.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            renderer.sharedMaterial = CreateWaterMaterial();
        }
    }

    private void EnsureRoots()
    {
        EnsureSeaPlane();
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
        // Spec 31C.4: the river gets banks — tiles adjacent to water are sand.
        _waterCoords.Clear();
        _tileElevations.Clear();
        foreach (var tile in snapshot.Tiles)
        {
            _tileElevations[tile.Coord] = tile.Elevation;
            if (tile.Water)
            {
                _waterCoords.Add(tile.Coord);
            }
        }

        foreach (var tile in snapshot.Tiles)
        {
            if (!_tileViews.ContainsKey(tile.Coord))
            {
                _tileViews[tile.Coord] = CreateTileView(tile, IsSandTile(tile));
            }
        }

        // Build lookup for junction positions
        var junctionPositions = new Dictionary<int, Float2>();
        foreach (var junction in snapshot.Junctions)
        {
            junctionPositions[junction.Id.Value] = junction.WorldPosition;

            if (_showJunctionMarkers)
            {
                var key = junction.Id.Value;
                if (!_junctionViews.ContainsKey(key))
                {
                    _junctionViews[key] = CreateJunctionView(junction);
                }
            }
        }

        // Snapshot-diff despawn (spec 31.15): destroy views whose object
        // disappeared from the simulation (eaten/picked-up apples).
        var liveObjectIds = new HashSet<int>();
        foreach (var worldObject in snapshot.Objects)
        {
            liveObjectIds.Add(worldObject.Id.Value);
        }

        var staleObjectKeys = new List<int>();
        foreach (var key in _objectViews.Keys)
        {
            if (!liveObjectIds.Contains(key))
            {
                staleObjectKeys.Add(key);
            }
        }

        foreach (var key in staleObjectKeys)
        {
            Destroy(_objectViews[key]);
            _objectViews.Remove(key);
            _prevObjectPositions.Remove(key);
            _currObjectPositions.Remove(key);
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

            var targetPos = SimulationUnityMapper.ToUnityPosition(npc.Position, GroundY(npc.Tile));
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

            SyncActorView(snapshot, npc);
        }

        SyncAnimalViews(snapshot);

        // A dead housemate leaves a corpse object - her walking view goes.
        var staleNpcKeys = new List<int>();
        foreach (var key in _npcViews.Keys)
        {
            var alive = false;
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value == key)
                {
                    alive = true;
                    break;
                }
            }

            if (!alive)
            {
                staleNpcKeys.Add(key);
            }
        }

        foreach (var key in staleNpcKeys)
        {
            Destroy(_npcViews[key]);
            _npcViews.Remove(key);
            _actorViews.Remove(key);
            _prevNpcPoses.Remove(key);
            _currNpcPoses.Remove(key);
        }
    }

    // Spec 31B.5: wardrobe, walk animation, and gaze follow the snapshot.
    private void SyncActorView(WorldSnapshot snapshot, NpcSnapshot npc)
    {
        if (!_actorViews.TryGetValue(npc.Id.Value, out var actorView) || actorView == null)
        {
            return;
        }

        actorView.SyncWorn(npc.WornItems);
        actorView.SetInteraction(npc.CurrentInteraction, HeldItemFor(npc));

        // Spec 31C.2: sleeping happens lying on the bed's attach point.
        if (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress")
        {
            actorView.SetLaying(true, FindBedAttachPoint(snapshot, npc));
        }
        else
        {
            actorView.SetLaying(false, null);
        }

        if (npc.CurrentInteraction == "Talk")
        {
            var partner = FindNearestOtherNpc(snapshot, npc);
            if (partner is not null)
            {
                var head = SimulationUnityMapper.ToUnityPosition(partner.Position, GroundY(partner.Tile));
                head.y += HexRadius * NpcHeightFactor * 1.8f;
                actorView.LookAtPoint(head);
                return;
            }
        }

        if (npc.MovementStatus == "Moving" && npc.TargetTile is { } target)
        {
            var ahead = SimulationUnityMapper.ToUnityPosition(
                HexSpatialMath.TileToWorld(target), GroundY(target));
            ahead.y += HexRadius * NpcHeightFactor * 1.5f;
            actorView.LookAtPoint(ahead);
            return;
        }

        actorView.ClearGaze();
    }

    // Spec 31C.6: what she visibly holds — food while eating, the tool
    // while chopping/mining, the pot while drinking boiled water.
    private static string HeldItemFor(NpcSnapshot npc)
    {
        switch (npc.CurrentInteraction)
        {
            case "Eat":
                foreach (var item in npc.InventoryItems)
                {
                    if (item.StartsWith("food."))
                    {
                        return item;
                    }
                }

                return "food.coconut";
            case "Harvest":
                // The goal says WHAT is being harvested: a boulder wants the
                // pickaxe; a tree wants the axe, or the saw when that's the
                // chopper's tool (the saw era showed empty-handed logging).
                if (npc.CurrentGoal == "MineBoulder" &&
                    npc.InventoryItems.Contains("tool.pickaxe_stone"))
                {
                    return "tool.pickaxe_stone";
                }

                if (npc.InventoryItems.Contains("tool.axe_stone"))
                {
                    return "tool.axe_stone";
                }

                if (npc.InventoryItems.Contains("tool.saw"))
                {
                    return "tool.saw";
                }

                return npc.InventoryItems.Contains("tool.pickaxe_stone")
                    ? "tool.pickaxe_stone" : null;
            case "Fuel":
                return npc.InventoryItems.Contains("resource.firewood")
                    ? "resource.firewood" : null;
            case "Craft":
                if (npc.CurrentGoal == "CookMeat" &&
                    npc.InventoryItems.Contains("food.meat_raw"))
                {
                    return "food.meat_raw";
                }

                return npc.InventoryItems.Contains("resource.firewood")
                    ? "resource.firewood" : null;
            case "Build":
                return npc.InventoryItems.Contains("resource.firewood")
                    ? "resource.firewood" : null;
            // Spec 29H: fill the bottle and drink from it — the bottle shows
            // in hand for both.
            case "FillBottle":
            case "Drink":
                return "tool.bottle";
            default:
                return null;
        }
    }

    // The bed she is sleeping on: nearest bed object view within ~a tile.
    private Transform? FindBedAttachPoint(WorldSnapshot snapshot, NpcSnapshot npc)
    {
        ObjectSnapshot? bed = null;
        var bestSq = float.MaxValue;
        foreach (var worldObject in snapshot.Objects)
        {
            if (!worldObject.DefinitionId.Contains("bed"))
            {
                continue;
            }

            var sq = (float)HexSpatialMath.HexDistance(worldObject.Tile, npc.Tile);
            if (sq < bestSq && sq <= 1f)
            {
                bestSq = sq;
                bed = worldObject;
            }
        }

        if (bed is null || !_objectViews.TryGetValue(bed.Id.Value, out var bedView))
        {
            return null;
        }

        var point = bedView.transform.Find("point");
        if (point == null)
        {
            foreach (var t in bedView.GetComponentsInChildren<Transform>())
            {
                if (t.name == "point")
                {
                    point = t;
                    break;
                }
            }
        }

        return point != null ? point : bedView.transform;
    }

    private static NpcSnapshot? FindNearestOtherNpc(WorldSnapshot snapshot, NpcSnapshot npc)
    {
        NpcSnapshot? best = null;
        var bestSq = float.MaxValue;
        foreach (var other in snapshot.Npcs)
        {
            if (other.Id.Value == npc.Id.Value)
            {
                continue;
            }

            var dx = other.Position.X - npc.Position.X;
            var dy = other.Position.Y - npc.Position.Y;
            var sq = dx * dx + dy * dy;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = other;
            }
        }

        return best;
    }

    private void InterpolateMovables(float alpha)
    {
        foreach (var kvp in _dogViews)
        {
            InterpolateAnimal(kvp.Value, kvp.Key, alpha);
        }

        foreach (var kvp in _crabViews)
        {
            InterpolateAnimal(kvp.Value, -kvp.Key - 1, alpha);
        }

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

    private readonly HashSet<TileCoord> _waterCoords = new();

    // Spec 20.16: elevation registry — every movable and object view takes
    // its Y from its tile's top.
    private const float ElevationStep = 0.55f;
    private readonly Dictionary<TileCoord, int> _tileElevations = new();

    // Spec 20.16: prisms skirt down to a shared base so tall tiles read as
    // solid columns rooted below every neighbour — no floating tops.
    private const float TerrainBaseY = -2.5f;

    // World-space UV scales: tops sample a biome texture continuously across
    // tiles; walls stretch a cliff texture along the perimeter and vertically.
    private const float TopUvScale = 0.42f;
    private const float WallUvScaleU = 0.5f;
    private const float WallUvScaleV = 0.45f;

    private float GroundY(TileCoord coord)
    {
        var elevation = _tileElevations.TryGetValue(coord, out var e) ? e : 1;
        return SimulationUnityMapper.TileHeight + elevation * ElevationStep;
    }


    private bool IsSandTile(TileSnapshot tile)
    {
        if (tile.Water || tile.Indoor)
        {
            return false;
        }

        foreach (var water in _waterCoords)
        {
            if (HexSpatialMath.HexDistance(tile.Coord, water) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    private GameObject CreateTileView(TileSnapshot tile, bool sand)
    {
        var go = new GameObject($"Hex {tile.Coord.Q},{tile.Coord.R}");
        go.transform.SetParent(_tilesRoot, false);
        var position = SimulationUnityMapper.ToUnityTilePosition(tile.Coord);
        var world = HexSpatialMath.TileToWorld(tile.Coord);

        var meshFilter = go.AddComponent<MeshFilter>();
        // Spec 20.16: hexes are prisms — top at the tile's elevation, skirt
        // to a shared base so hills read as solid terrain, not floating caps.
        var topHeight = SimulationUnityMapper.TileHeight + tile.Elevation * ElevationStep;
        if (tile.Water)
        {
            topHeight -= ElevationStep * 0.4f; // sunken water surface
        }

        // Submesh 0 = flat top, submesh 1 = the perimeter skirt/cliff.
        meshFilter.sharedMesh = BuildHexPrismMesh(HexRadius, topHeight, TerrainBaseY, world.X, world.Y);

        var meshRenderer = go.AddComponent<MeshRenderer>();
        if (tile.Water)
        {
            // Transparent water surface on top; sandy riverbed on the walls.
            meshRenderer.sharedMaterials = new[]
            {
                CreateWaterMaterial(),
                GetFlatMaterial(Jitter(BiomeColor("sand"), tile.Coord, 0.05f))
            };
        }
        else
        {
            var topBiome = sand ? "sand" : TopBiome(tile.Elevation);
            // Low-poly look: flat facet colours, a subtle per-tile shade
            // jitter for a hand-placed patchwork, darker earthy cliffs.
            meshRenderer.sharedMaterials = new[]
            {
                GetFlatMaterial(Jitter(BiomeColor(topBiome), tile.Coord, 0.07f)),
                GetFlatMaterial(Jitter(BiomeColor("cliff"), tile.Coord, 0.05f))
            };

            // Spec 20.16: leafy tufts stand on grass tops — terrain grass.
            if (_grassDetail && _grassBladesPerTile > 0 &&
                (topBiome == "grass" || topBiome == "grass_dry"))
            {
                BuildGrassClump(go.transform, HexRadius, topHeight, tile.Coord);
            }
        }

        go.transform.position = position;
        return go;
    }

    // Spec 20.16: biome key by elevation band — grass lowland, dry grass,
    // brown-green hills, bare rock, mountain caps.
    private static string TopBiome(int elevation)
    {
        return elevation switch
        {
            <= 1 => "grass",
            2 => "grass_dry",
            3 => "hill",
            4 => "rock",
            _ => "mountain"
        };
    }

    private static Material _waterMaterial;

    private static Material CreateWaterMaterial()
    {
        if (_waterMaterial != null)
        {
            return _waterMaterial;
        }

        // Spec 20.16: stylized cartoon water — waves, fresnel two-tone, glints.
        var stylized = Shader.Find("HexLive/StylizedWater");
        if (stylized != null)
        {
            _waterMaterial = new Material(stylized);
            return _waterMaterial;
        }

        // Fallback: plain transparent URP/Lit if the shader failed to import.
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetFloat("_Surface", 1f); // transparent
        material.SetFloat("_Blend", 0f);   // alpha
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        material.color = new Color(0.20f, 0.45f, 0.75f, 0.6f);
        _waterMaterial = material;
        return material;
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
        // Spec 31C.4: water interaction anchors have no gizmo — the river
        // and pond tiles ARE the visual; NPCs just come and drink.
        if (worldObject.DefinitionId.StartsWith("water."))
        {
            var invisible = new GameObject($"Object {worldObject.DefinitionId} (anchor)");
            invisible.transform.SetParent(_objectsRoot, false);
            return invisible;
        }

        // Spec 31C.3: real prefabs first (Resources/HexLive/Objects/<id>),
        // primitives as the eternal fallback.
        var objectPrefab = Resources.Load<GameObject>($"HexLive/Objects/{worldObject.DefinitionId}");
        if (objectPrefab != null)
        {
            var prefabRoot = new GameObject($"Object {worldObject.DefinitionId}");
            prefabRoot.transform.SetParent(_objectsRoot, false);
            var instance = Instantiate(objectPrefab, prefabRoot.transform);
            FitObjectPrefab(instance, worldObject.DefinitionId);
            var anchorPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            prefabRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                anchorPos, GroundY(worldObject.Tile));
            MaybeAttachCampfire(prefabRoot, worldObject.DefinitionId);
            return prefabRoot;
        }

        var primitiveType = GetObjectPrimitive(worldObject.DefinitionId);
        var scale = GetObjectScale(worldObject.DefinitionId);
        var root = new GameObject($"Object {worldObject.DefinitionId}");
        root.transform.SetParent(_objectsRoot, false);

        var visual = CreatePrimitiveVisual(root.transform, primitiveType, scale, GetObjectColor(worldObject.DefinitionId));
        visual.name = "Visual";

        var pos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
        root.transform.position = SimulationUnityMapper.ToUnityPosition(pos, GroundY(worldObject.Tile));
        MaybeAttachCampfire(root, worldObject.DefinitionId);
        return root;
    }

    // Spec 20.16: the campfire actually burns — flame particles + a warm,
    // flickering point light that lights nearby terrain and actors.
    private void MaybeAttachCampfire(GameObject root, string definitionId)
    {
        if (definitionId != "campfire.spot")
        {
            return;
        }

        var effect = root.AddComponent<HexLive.UnityPresentation.Environment.CampfireEffect>();
        effect.Construct(HexRadius);
    }

    // Animal keys share one pose map: dogs get positive ids, crabs negative.
    private void SyncAnimalViews(WorldSnapshot snapshot)
    {
        var liveKeys = new HashSet<int>();
        foreach (var dog in snapshot.Dogs)
        {
            var key = dog.Id;
            liveKeys.Add(key);
            if (!_dogViews.TryGetValue(key, out _))
            {
                _dogViews[key] = CreateDogView(dog.Id);
            }

            UpdateAnimalPose(key, dog.Position, dog.Tile);
        }

        foreach (var crab in snapshot.Crabs)
        {
            var key = -crab.Id - 1;
            liveKeys.Add(key);
            if (!_crabViews.TryGetValue(crab.Id, out _))
            {
                _crabViews[crab.Id] = CreateCrabView(crab.Id);
            }

            UpdateAnimalPose(key, crab.Position, crab.Tile);
        }

        PruneAnimalViews(_dogViews, liveKeys, negate: false);
        PruneAnimalViews(_crabViews, liveKeys, negate: true);
    }

    private void UpdateAnimalPose(int key, Float2 position, TileCoord tile)
    {
        var target = SimulationUnityMapper.ToUnityPosition(position, GroundY(tile));
        var pose = new Pose(target, Quaternion.identity);
        _prevAnimalPoses[key] = _currAnimalPoses.TryGetValue(key, out var old) ? old : pose;
        _currAnimalPoses[key] = pose;
    }

    private void PruneAnimalViews(Dictionary<int, GameObject> views, HashSet<int> liveKeys, bool negate)
    {
        var stale = new List<int>();
        foreach (var id in views.Keys)
        {
            var key = negate ? -id - 1 : id;
            if (!liveKeys.Contains(key))
            {
                stale.Add(id);
            }
        }

        foreach (var id in stale)
        {
            Destroy(views[id]);
            views.Remove(id);
            var key = negate ? -id - 1 : id;
            _prevAnimalPoses.Remove(key);
            _currAnimalPoses.Remove(key);
        }
    }

    private void InterpolateAnimal(GameObject view, int key, float alpha)
    {
        if (!_currAnimalPoses.TryGetValue(key, out var curr))
        {
            return;
        }

        if (_prevAnimalPoses.TryGetValue(key, out var prev))
        {
            view.transform.position = Vector3.Lerp(prev.Position, curr.Position, alpha);
            var direction = curr.Position - prev.Position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.00001f)
            {
                view.transform.rotation = Quaternion.Slerp(
                    view.transform.rotation, Quaternion.LookRotation(direction), alpha);
            }
        }
        else
        {
            view.transform.position = curr.Position;
        }
    }

    private GameObject CreateDogView(int id)
    {
        var root = new GameObject($"Dog {id}");
        root.transform.SetParent(_npcsRoot, false);
        var bodyLength = HexRadius * 0.28f;
        var body = CreatePrimitiveVisual(root.transform, PrimitiveType.Capsule,
            new Vector3(bodyLength * 0.45f, bodyLength * 0.5f, bodyLength * 0.45f),
            new Color(0.35f, 0.30f, 0.28f));
        body.name = "Body";
        body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        body.transform.localPosition = new Vector3(0f, bodyLength * 0.35f, 0f);
        var head = CreatePrimitiveVisual(root.transform, PrimitiveType.Sphere,
            Vector3.one * bodyLength * 0.35f, new Color(0.30f, 0.25f, 0.23f));
        head.name = "Head";
        head.transform.localPosition = new Vector3(0f, bodyLength * 0.5f, bodyLength * 0.55f);
        return root;
    }

    private GameObject CreateCrabView(int id)
    {
        var root = new GameObject($"Crab {id}");
        root.transform.SetParent(_npcsRoot, false);
        var size = HexRadius * 0.12f;
        var shell = CreatePrimitiveVisual(root.transform, PrimitiveType.Sphere,
            new Vector3(size * 1.6f, size * 0.6f, size * 1.2f), new Color(0.80f, 0.25f, 0.15f));
        shell.name = "Shell";
        shell.transform.localPosition = new Vector3(0f, size * 0.3f, 0f);
        var clawL = CreatePrimitiveVisual(root.transform, PrimitiveType.Sphere,
            Vector3.one * size * 0.45f, new Color(0.85f, 0.30f, 0.18f));
        clawL.name = "ClawL";
        clawL.transform.localPosition = new Vector3(-size * 0.9f, size * 0.25f, size * 0.6f);
        var clawR = CreatePrimitiveVisual(root.transform, PrimitiveType.Sphere,
            Vector3.one * size * 0.45f, new Color(0.85f, 0.30f, 0.18f));
        clawR.name = "ClawR";
        clawR.transform.localPosition = new Vector3(size * 0.9f, size * 0.25f, size * 0.6f);
        return root;
    }

    private GameObject CreateNpcView(NpcSnapshot npc)
    {
        // Spec 31B.5: the girls get real bodies; primitives are the fallback.
        if (!string.IsNullOrEmpty(npc.ActorMesh))
        {
            var actorPrefab = Resources.Load<GameObject>($"HexLive/Actors/{npc.ActorMesh}");
            if (actorPrefab != null)
            {
                var actorRoot = new GameObject($"NPC {npc.Id.Value} ({npc.DisplayName})");
                actorRoot.transform.SetParent(_npcsRoot, false);

                var actorBody = Instantiate(actorPrefab, actorRoot.transform);
                actorBody.name = npc.ActorMesh;
                var targetHeight = HexRadius * NpcHeightFactor * 2.4f;
                var scale = targetHeight / ActorSourceHeightMeters;
                actorBody.transform.localScale = Vector3.one * scale;
                actorBody.transform.localPosition = Vector3.zero;

                var view = actorRoot.AddComponent<NpcActorView>();
                view.Construct(npc.ActorMesh);
                _actorViews[npc.Id.Value] = view;
                return actorRoot;
            }
        }

        var root = new GameObject($"NPC {npc.Id.Value}");
        root.transform.SetParent(_npcsRoot, false);

        var bodyRadius = HexRadius * NpcRadiusFactor;
        var bodyHeight = HexRadius * NpcHeightFactor;
        var headRadius = bodyRadius * 0.7f;

        var skinColor = GetNpcBodyColor(npc.Id.Value);
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
                    return SimulationUnityMapper.ToUnityPosition(junction.WorldPosition, GroundY(worldObject.Tile));
                }
            }
        }

        return SimulationUnityMapper.ToUnityTilePosition(worldObject.Tile, GroundY(worldObject.Tile));
    }

    private static Float2 GetObjectAnchorFromJunctions(ObjectSnapshot worldObject, Dictionary<int, Float2> junctionPositions)
    {
        if (worldObject.Junctions.Count > 0 && junctionPositions.TryGetValue(worldObject.Junctions[0].Value, out var pos))
        {
            return pos;
        }

        return HexSpatialMath.TileToWorld(worldObject.Tile);
    }

    // Spec 20.16: a solid hex prism — flat top (submesh 0) plus a skirt of
    // six quads down to a shared base (submesh 1) so elevation reads as rock
    // columns that visually meet their lower neighbours. UVs are world-space
    // so the biome texture flows continuously from tile to tile.
    private static Mesh BuildHexPrismMesh(float radius, float top, float baseY, float worldX, float worldZ)
    {
        var mesh = new Mesh { name = "HexPrism" };

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var topTris = new List<int>();
        var wallTris = new List<int>();

        // --- top face (fan around the centre) ---
        vertices.Add(new Vector3(0f, top, 0f));
        uvs.Add(new Vector2(worldX, worldZ) * TopUvScale);

        var rim = new Vector3[6];
        for (var i = 0; i < 6; i++)
        {
            var angle = Mathf.Deg2Rad * (60f * i - 30f);
            rim[i] = new Vector3(radius * Mathf.Cos(angle), top, radius * Mathf.Sin(angle));
            vertices.Add(rim[i]);
            uvs.Add(new Vector2(worldX + rim[i].x, worldZ + rim[i].z) * TopUvScale);
        }

        for (var i = 0; i < 6; i++)
        {
            var a = 1 + i;
            var b = 1 + (i + 1) % 6;
            topTris.Add(0);
            topTris.Add(b);
            topTris.Add(a);
        }

        // --- perimeter skirt (own vertices for hard-edged cliff shading) ---
        for (var i = 0; i < 6; i++)
        {
            var a = rim[i];
            var b = rim[(i + 1) % 6];
            var start = vertices.Count;

            vertices.Add(new Vector3(a.x, top, a.z));
            vertices.Add(new Vector3(b.x, top, b.z));
            vertices.Add(new Vector3(b.x, baseY, b.z));
            vertices.Add(new Vector3(a.x, baseY, a.z));

            var uA = i * WallUvScaleU;
            var uB = (i + 1) * WallUvScaleU;
            uvs.Add(new Vector2(uA, top * WallUvScaleV));
            uvs.Add(new Vector2(uB, top * WallUvScaleV));
            uvs.Add(new Vector2(uB, baseY * WallUvScaleV));
            uvs.Add(new Vector2(uA, baseY * WallUvScaleV));

            wallTris.Add(start + 0);
            wallTris.Add(start + 1);
            wallTris.Add(start + 2);
            wallTris.Add(start + 0);
            wallTris.Add(start + 2);
            wallTris.Add(start + 3);
        }

        mesh.subMeshCount = 2;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(topTris, 0);
        mesh.SetTriangles(wallTris, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---- Flat low-poly biome colours ----
    // No textures: each tile is a flat-shaded facet with a small, stable
    // per-tile shade jitter so a field of grass reads as a hand-placed
    // patchwork rather than one dead-flat sheet. Materials are cached by
    // quantised colour so the SRP batcher still groups most tiles.

    private static Color BiomeColor(string biome)
    {
        return biome switch
        {
            "grass" => new Color(0.42f, 0.63f, 0.30f),
            "grass_dry" => new Color(0.56f, 0.63f, 0.33f),
            "hill" => new Color(0.53f, 0.47f, 0.30f),
            "rock" => new Color(0.56f, 0.51f, 0.45f),
            "mountain" => new Color(0.60f, 0.60f, 0.62f),
            "sand" => new Color(0.89f, 0.81f, 0.58f),
            "cliff" => new Color(0.47f, 0.35f, 0.26f),
            _ => new Color(0.5f, 0.5f, 0.5f)
        };
    }

    // Deterministic ±amount brightness wobble keyed off the tile coord.
    private static Color Jitter(Color color, TileCoord coord, float amount)
    {
        var seed = (uint)(coord.Q * 73856093 ^ coord.R * 19349663) ^ 0x85EBCA6Bu;
        var k = 1f + (NextRand(ref seed) - 0.5f) * 2f * amount;
        return new Color(color.r * k, color.g * k, color.b * k);
    }

    private static readonly Dictionary<int, Material> _flatMaterials = new();

    private static Material GetFlatMaterial(Color color)
    {
        // Quantise to ~24 levels per channel to keep the material count low.
        var key = (Mathf.RoundToInt(color.r * 24f) << 16)
                  | (Mathf.RoundToInt(color.g * 24f) << 8)
                  | Mathf.RoundToInt(color.b * 24f);
        if (_flatMaterials.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = color
        };
        material.SetFloat("_Smoothness", 0f);
        material.SetFloat("_Cull", 0f); // double-sided: skirt interiors never show
        _flatMaterials[key] = material;
        return material;
    }

    // ---- Terrain grass tufts ----

    private static Material _grassMaterial;

    private static Material GetGrassMaterial()
    {
        if (_grassMaterial != null)
        {
            return _grassMaterial;
        }

        // Flat solid-green blades — low-poly, no texture, no alpha seams.
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = new Color(0.36f, 0.56f, 0.24f)
        };
        material.SetFloat("_Smoothness", 0f);
        material.SetFloat("_Cull", 0f); // single triangles seen from both sides
        _grassMaterial = material;
        return material;
    }

    private void BuildGrassClump(Transform parent, float radius, float topY, TileCoord coord)
    {
        var go = new GameObject("Grass");
        go.transform.SetParent(parent, false);
        var meshFilter = go.AddComponent<MeshFilter>();
        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = GetGrassMaterial();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshFilter.sharedMesh = BuildGrassMesh(radius, topY, coord);
    }

    private Mesh BuildGrassMesh(float radius, float topY, TileCoord coord)
    {
        var mesh = new Mesh { name = "GrassClump" };
        var vertices = new List<Vector3>();
        var tris = new List<int>();

        // Deterministic per-tile scatter — same tile always looks the same.
        var seed = (uint)(coord.Q * 73856093 ^ coord.R * 19349663) ^ 0x9E3779B9u;
        var apothem = radius * 0.78f;

        for (var b = 0; b < _grassBladesPerTile; b++)
        {
            var ang = NextRand(ref seed) * Mathf.PI * 2f;
            var rad = Mathf.Sqrt(NextRand(ref seed)) * apothem;
            var px = Mathf.Cos(ang) * rad;
            var pz = Mathf.Sin(ang) * rad;
            var h = radius * (0.14f + NextRand(ref seed) * 0.10f);
            var w = radius * 0.05f;
            var yaw = NextRand(ref seed) * Mathf.PI;
            // A tiny lean so tufts aren't rigidly vertical.
            var lean = radius * (NextRand(ref seed) - 0.5f) * 0.06f;

            AddGrassBlade(vertices, tris, px, pz, topY, w, h, yaw, lean);
            AddGrassBlade(vertices, tris, px, pz, topY, w, h, yaw + Mathf.PI * 0.5f, lean);
        }

        mesh.SetVertices(vertices);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // One low-poly blade: a triangle tapering from a base edge to a tip.
    private static void AddGrassBlade(List<Vector3> vertices, List<int> tris,
        float px, float pz, float baseY, float halfWidth, float height, float yaw, float lean)
    {
        var dx = Mathf.Cos(yaw) * halfWidth;
        var dz = Mathf.Sin(yaw) * halfWidth;
        var start = vertices.Count;

        vertices.Add(new Vector3(px - dx, baseY, pz - dz));
        vertices.Add(new Vector3(px + dx, baseY, pz + dz));
        vertices.Add(new Vector3(px + lean, baseY + height, pz + lean));

        tris.Add(start + 0);
        tris.Add(start + 1);
        tris.Add(start + 2);
    }

    // Cheap deterministic LCG in [0,1); avoids UnityEngine.Random global state.
    private static float NextRand(ref uint state)
    {
        state = state * 1664525u + 1013904223u;
        return (state >> 8) / 16777216f;
    }

    // Spec 31C.3: normalize any downloaded/transferred model to the hex
    // metric by its rendered bounds — no per-asset scale guessing.
    private void FitObjectPrefab(GameObject instance, string definitionId)
    {
        var renderers = instance.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return;
        }

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        float target;
        float current;
        if (definitionId.Contains("tree"))
        {
            target = HexRadius * 2.2f; // palms tower over the girls
            current = bounds.size.y;
        }
        else if (definitionId.Contains("bed"))
        {
            target = HexRadius * 0.95f;
            current = Mathf.Max(bounds.size.x, bounds.size.z);
        }
        else if (definitionId.StartsWith("food."))
        {
            target = HexRadius * 0.12f;
            current = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        }
        else if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource."))
        {
            target = HexRadius * 0.18f;
            current = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        }
        else if (definitionId == "campfire.spot")
        {
            target = HexRadius * 0.55f;
            current = Mathf.Max(bounds.size.x, bounds.size.z);
        }
        else if (definitionId == "grave.npc")
        {
            target = HexRadius * 0.35f;
            current = bounds.size.y;
        }
        else if (definitionId == "rock.boulder")
        {
            target = HexRadius * 0.45f;
            current = Mathf.Max(bounds.size.x, bounds.size.z);
        }
        else if (definitionId == "forest.deadfall" || definitionId == "station.drying_rack" ||
                 definitionId == "construction.site")
        {
            target = HexRadius * 0.7f;
            current = Mathf.Max(bounds.size.x, bounds.size.z);
        }
        else
        {
            target = HexRadius * 0.6f;
            current = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        }

        if (current > 0.0001f)
        {
            instance.transform.localScale *= target / current;
        }

        // ground the model: bottom of bounds sits on the tile top
        var scaledRenderers = instance.GetComponentsInChildren<Renderer>();
        var scaledBounds = scaledRenderers[0].bounds;
        for (var i = 1; i < scaledRenderers.Length; i++)
        {
            scaledBounds.Encapsulate(scaledRenderers[i].bounds);
        }

        var lift = instance.transform.position.y - scaledBounds.min.y;
        instance.transform.localPosition += new Vector3(0f, lift, 0f);
    }

    private static PrimitiveType GetObjectPrimitive(string definitionId)
    {
        // Spec 31C.5: legible silhouettes — no more mystery spheres.
        if (definitionId.StartsWith("water.") || definitionId == "campfire.spot" ||
            definitionId.Contains("stone") || definitionId.Contains("boulder") ||
            definitionId == "grave.npc" || definitionId == "station.drying_rack")
        {
            return PrimitiveType.Cylinder;
        }

        if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource.") ||
            definitionId == "construction.site")
        {
            return PrimitiveType.Cube;
        }

        if (definitionId == "corpse.npc")
        {
            return PrimitiveType.Capsule;
        }

        if (definitionId.Contains("tree"))
        {
            return PrimitiveType.Cylinder;
        }

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
        var r = HexSpatialMath.HexRadius;
        if (definitionId.StartsWith("water."))
        {
            return new Vector3(r * 0.5f, r * 0.015f, r * 0.5f); // flat pool disc
        }

        if (definitionId == "campfire.spot")
        {
            return new Vector3(r * 0.3f, r * 0.04f, r * 0.3f); // ember ring
        }

        if (definitionId.Contains("boulder"))
        {
            return new Vector3(r * 0.35f, r * 0.22f, r * 0.35f);
        }

        if (definitionId.Contains("stone"))
        {
            return new Vector3(r * 0.12f, r * 0.06f, r * 0.12f);
        }

        if (definitionId == "grave.npc")
        {
            return new Vector3(r * 0.22f, r * 0.05f, r * 0.35f);
        }

        if (definitionId == "corpse.npc")
        {
            return new Vector3(r * 0.14f, r * 0.05f, r * 0.3f); // lying blob
        }

        if (definitionId == "station.drying_rack")
        {
            return new Vector3(r * 0.05f, r * 0.35f, r * 0.05f); // post
        }

        if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource."))
        {
            return Vector3.one * r * 0.11f;
        }

        if (definitionId == "construction.site")
        {
            return new Vector3(r * 0.5f, r * 0.1f, r * 0.5f);
        }

        if (definitionId.Contains("tree"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * TreeRadiusFactor,
                HexSpatialMath.HexRadius * TreeHeightFactor,
                HexSpatialMath.HexRadius * TreeRadiusFactor);
        }

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
        if (tile.Water)
        {
            return new Color(0.25f, 0.45f, 0.75f);
        }

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

    // Distinct body tints so multiple NPCs are tellable apart in play mode.
    private static Color GetNpcBodyColor(int npcId)
    {
        return (npcId % 4) switch
        {
            1 => new Color(0.88f, 0.84f, 0.72f), // warm beige
            2 => new Color(0.70f, 0.80f, 0.90f), // pale blue
            3 => new Color(0.76f, 0.88f, 0.70f), // pale green
            _ => new Color(0.90f, 0.78f, 0.86f)  // pale pink
        };
    }

    private static Color GetObjectColor(string definitionId)
    {
        if (definitionId == "food.coconut")
        {
            return new Color(0.42f, 0.28f, 0.15f); // coconut husk
        }

        if (definitionId.Contains("tree"))
        {
            return new Color(0.18f, 0.42f, 0.16f);
        }

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

        if (definitionId.StartsWith("water."))
        {
            return new Color(0.25f, 0.5f, 0.8f);
        }

        if (definitionId == "campfire.spot")
        {
            return new Color(0.9f, 0.45f, 0.1f);
        }

        if (definitionId.Contains("stone") || definitionId.Contains("boulder"))
        {
            return new Color(0.5f, 0.5f, 0.52f);
        }

        if (definitionId == "grave.npc")
        {
            return new Color(0.35f, 0.35f, 0.38f);
        }

        if (definitionId == "corpse.npc")
        {
            return new Color(0.8f, 0.65f, 0.55f);
        }

        if (definitionId.StartsWith("tool."))
        {
            return new Color(0.45f, 0.32f, 0.18f);
        }

        if (definitionId.StartsWith("resource."))
        {
            return new Color(0.6f, 0.45f, 0.25f);
        }

        if (definitionId.StartsWith("food."))
        {
            return new Color(0.85f, 0.3f, 0.25f);
        }

        if (definitionId == "construction.site")
        {
            return new Color(0.8f, 0.7f, 0.3f);
        }

        return new Color(0.6f, 0.6f, 0.6f); // neutral unknown, not alarm-red
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
