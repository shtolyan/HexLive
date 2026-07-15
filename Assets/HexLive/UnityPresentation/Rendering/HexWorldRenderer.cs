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

    // The lying clips are ground-authored (the body's underside baked at root
    // Y=0), but the visible back rests a touch below the root plane, so pinning
    // the root straight onto the mattress top makes the sleeper sink in. Lift
    // the pin by this much (world units) so the body rests on top of the bed.
    private const float SleepBodyLift = 0.12f;

    private const float ChairRadiusFactor = 3f / 25f;
    private const float ChairHeightFactor = 7f / 75f;

    private const float ClothingRadiusFactor = 7f / 75f;
    private const float ClothingHeightFactor = 2f / 15f;

    private const float FoodRadiusFactor = 7f / 75f;

    private const float TreeRadiusFactor = 4f / 25f;
    private const float TreeHeightFactor = 1f / 2f;

    private readonly Dictionary<TileCoord, GameObject> _tileViews = new();
    private readonly Dictionary<int, GameObject> _junctionViews = new();

    // Spec 40.17: a persistent "точечка на шве" — a small amber dot marking a
    // climb seam (a one-step ledge you clamber up), always shown (not the
    // debug-only junction spheres).
    private readonly Dictionary<int, GameObject> _seamMarkers = new();
    private readonly Dictionary<int, GameObject> _objectViews = new();

    // Spec §54: object keys whose view is a tree, so despawn (a chop) plays a
    // fall animation + leaves a stump instead of a hard cut.
    private readonly HashSet<int> _treeViewKeys = new();

    // §Wardrobe-anim: garment world objects hidden on the ground this frame
    // because their owner has picked them up into hand for the "don" beat.
    // Must match ExecutionSystem.WardrobeHandoffFraction.
    private const float WardrobeHandoffFraction = 0.5f;
    private readonly HashSet<int> _wardrobeHiddenObjects = new();

    // Spec 40.13: dead actors stay as physics-ragdoll corpses — keyed by the
    // corpse.npc OBJECT id (the sim's logic anchor for mourn/bury/decay), so
    // the body view lives exactly as long as the corpse object does.
    private readonly Dictionary<int, GameObject> _corpseBodyViews = new();
    private readonly Dictionary<int, GameObject> _npcViews = new();

    // Spec 31B.5: actor-backed views (Marta/Molly/Jana bodies); primitive
    // capsules remain the fallback when no actor prefab matches.
    private readonly Dictionary<int, NpcActorView> _actorViews = new();

    // Spec 28.15E: the last talk-outcome tick popped per NPC, so the "+/-"
    // relationship glyph fires exactly once when a fresh outcome arrives.
    private readonly Dictionary<int, int> _lastTalkResultTick = new();

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

    // §40.18-B: NPCs currently standing on a water tile ride the live wave
    // swell every render frame (WaterWave), so their Y stays glued to the same
    // surface the shader draws — no static bob coefficients needed.
    private readonly Dictionary<int, bool> _npcOnWater = new();
    private readonly Dictionary<int, Vector3> _prevObjectPositions = new();
    private readonly Dictionary<int, Vector3> _currObjectPositions = new();

    private float HexRadius => SimulationUnityMapper.HexRadius;

    private float JunctionMarkerScale => HexRadius * JunctionMarkerScaleFactor;

    public void SetRunner(SimulationRunnerBehaviour runner)
    {
        _runner = runner;
    }

    // The NPC's current interpolated world position (feet), so the orbit
    // camera can follow the smooth visual position instead of the raw,
    // tick-stepped simulation position.
    public bool TryGetNpcViewPosition(int npcId, out Vector3 position)
    {
        if (_npcViews.TryGetValue(npcId, out var view) && view != null)
        {
            position = view.transform.position;
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    // Orbit pivot: the pose-aware visual center of the NPC's body (chest when
    // standing, following the body down when sitting/lying).
    public bool TryGetNpcBodyCenter(int npcId, out Vector3 center)
    {
        if (_actorViews.TryGetValue(npcId, out var actorView) && actorView != null)
        {
            return actorView.TryGetBodyCenter(out center);
        }

        if (_npcViews.TryGetValue(npcId, out var view) && view != null)
        {
            center = view.transform.position + Vector3.up * (HexRadius * NpcHeightFactor);
            return true;
        }

        center = Vector3.zero;
        return false;
    }

    // Face anchor of the LIVE in-world character (real dirt/tan/clothes) for
    // the portrait camera. Falls back to the primitive view's head sphere.
    public bool TryGetNpcFace(
        int npcId, out Vector3 faceCenter, out Vector3 faceForward, out Vector3 faceUp, out float scale)
    {
        if (_actorViews.TryGetValue(npcId, out var actorView) && actorView != null)
        {
            return actorView.TryGetFace(out faceCenter, out faceForward, out faceUp, out scale);
        }

        if (_npcViews.TryGetValue(npcId, out var view) && view != null)
        {
            var bodyHeight = HexRadius * NpcHeightFactor;
            faceCenter = view.transform.position + Vector3.up * (bodyHeight * 2.2f);
            faceForward = view.transform.forward;
            faceUp = view.transform.up;
            scale = bodyHeight * 2.4f / 1.7f;
            return true;
        }

        faceCenter = Vector3.zero;
        faceForward = Vector3.forward;
        faceUp = Vector3.up;
        scale = 1f;
        return false;
    }

    private void Update()
    {
        // While offline ticks wind forward, stay dark: winding is pure headless
        // simulation and painting each intermediate world (skin decals, actor
        // sync) behind the loading curtain only starves the tick budget. Views
        // build once, afterward, in loading phase 3.
        if (HexLive.UnityPresentation.UI.LoadingScreen.IsReplaying)
        {
            return;
        }

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

        UpdateRain(snapshot.IsRaining);
        UpdateEnvironmentWetness(snapshot.IsRaining);
        // §40.18-B: our owned wave params -> the water material, so the mesh
        // swell always matches what WaterWave.Height gives the swimmer.
        WaterWave.PushToShader();
        InterpolateMovables(_runner.TickAlpha);
    }

    // Spec 40.2-B: ground blood stains manager (lazy — lives under the
    // renderer, cleared with it on scene teardown).
    private HexLive.UnityPresentation.Environment.GroundBloodStains _bloodStains;

    private HexLive.UnityPresentation.Environment.GroundBloodStains EnsureBloodStains()
    {
        if (_bloodStains == null)
        {
            var go = new GameObject("BloodStains");
            go.transform.SetParent(transform, false);
            _bloodStains = go.AddComponent<HexLive.UnityPresentation.Environment.GroundBloodStains>();
        }

        return _bloodStains;
    }

    // Spec 40.2-C: blood spilled while in the water billows on the surface
    // instead of pooling on the ground (see WaterBloodStains).
    private HexLive.UnityPresentation.Environment.WaterBloodStains _waterBlood;

    private HexLive.UnityPresentation.Environment.WaterBloodStains EnsureWaterBlood()
    {
        if (_waterBlood == null)
        {
            var go = new GameObject("WaterBloodStains");
            go.transform.SetParent(transform, false);
            _waterBlood = go.AddComponent<HexLive.UnityPresentation.Environment.WaterBloodStains>();
        }

        return _waterBlood;
    }

    // Spec 33.2 (iter 33): cartoon rain — a shower of little droplet particles
    // over the island whenever the weather says it's raining.
    private ParticleSystem _rain;

    private void UpdateRain(bool raining)
    {
        if (_rain == null)
        {
            var go = new GameObject("Rain");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 30f, 0f);
            // Particles emit along local +Z — aim it straight DOWN, or the
            // "rain" sprays sideways 30 units above the island (the bug that
            // made rain invisible in play).
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _rain = go.AddComponent<ParticleSystem>();
            _rain.Stop();

            var main = _rain.main;
            main.startSpeed = 22f;
            // Thin, dainty streaks — width comes from startSize in stretch mode.
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.06f);
            main.startLifetime = 2.2f;
            main.startColor = new Color(0.65f, 0.78f, 0.95f, 0.55f);
            main.maxParticles = 9000;
            main.gravityModifier = 1.2f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = _rain.emission;
            emission.rateOverTime = 2600f; // lots of small drops

            var shape = _rain.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(60f, 60f, 1f); // XY box, perpendicular to fall

            // Kill each drop exactly at ground level...
            var groundY = SimulationUnityMapper.TileHeight + 0.03f;
            var planeGo = new GameObject("RainGroundPlane");
            planeGo.transform.SetParent(transform, false); // NOT under the rotated emitter
            planeGo.transform.position = new Vector3(0f, groundY, 0f);
            var collision = _rain.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.Planes;
            collision.SetPlane(0, planeGo.transform);
            collision.bounce = 0f;
            collision.lifetimeLoss = 1f;

            // ...and burst a few tiny droplets where it lands (the splash).
            var splash = CreateSplashSystem(go.transform);
            var subEmitters = _rain.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(
                splash, ParticleSystemSubEmitterType.Collision, ParticleSystemSubEmitterProperties.InheritNothing);

            var renderer = _rain.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.velocityScale = 0.018f;
                renderer.lengthScale = 5f;
                renderer.sharedMaterial = GetRainMaterial();
            }
        }

        if (raining && !_rain.isPlaying)
        {
            _rain.Play();
        }
        else if (!raining && _rain.isPlaying)
        {
            _rain.Stop();
        }
    }

    // Tiny droplet burst where a raindrop hits the ground. Must live as a
    // child of the rain system (Unity sub-emitter rule); the local rotation
    // cancels the parent's 90° so the hemisphere sprays world-up.
    private ParticleSystem CreateSplashSystem(Transform rainRoot)
    {
        var go = new GameObject("RainSplash");
        go.transform.SetParent(rainRoot, false);
        go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        var splash = go.AddComponent<ParticleSystem>();
        splash.Stop();

        var main = splash.main;
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.7f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.38f);
        main.startColor = new Color(0.75f, 0.86f, 1f, 0.75f);
        main.maxParticles = 6000;
        main.gravityModifier = 1.4f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = splash.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 3, 5, 1, 0.01f) });

        var shape = splash.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.02f;

        var renderer = splash.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = GetRainMaterial();
        }

        return splash;
    }

    private static Material _rainMaterial;

    // A particle-capable unlit transparent material — URP Lit renders
    // stretched billboards black/invisible.
    private static Material GetRainMaterial()
    {
        if (_rainMaterial != null)
        {
            return _rainMaterial;
        }

        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
        {
            _rainMaterial = CreateWaterMaterial();
            return _rainMaterial;
        }

        var material = new Material(shader);
        material.SetFloat("_Surface", 1f);
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        material.color = new Color(0.65f, 0.78f, 0.95f, 0.6f);
        _rainMaterial = material;
        return material;
    }

    // ---- Rain mood: cloud-dimmed light + wet darkened glossy terrain ----

    private float _wetness;                 // 0 dry .. 1 soaked (visual only)
    private Light _sun;
    private bool _sunCached;
    private float _sunBaseIntensity;
    private Color _sunBaseColor;
    private float _ambientBase;
    private float _appliedWetness = -1f;

    private void UpdateEnvironmentWetness(bool raining)
    {
        // Soak fast (~6 s), dry out slowly (~25 s).
        var target = raining ? 1f : 0f;
        var rate = raining ? Time.deltaTime / 6f : Time.deltaTime / 25f;
        _wetness = Mathf.MoveTowards(_wetness, target, rate);

        if (Mathf.Abs(_wetness - _appliedWetness) < 0.004f)
        {
            return;
        }

        _appliedWetness = _wetness;
        ApplyWetness(_wetness);
    }

    private void ApplyWetness(float w)
    {
        // Cloud light: dimmer, cooler, flatter.
        if (!_sunCached)
        {
            _sun = RenderSettings.sun;
            if (_sun == null)
            {
                // Fallback: the scene's directional light that lights the world
                // (skip specials like the portrait stage's dedicated light).
                foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (light.type == LightType.Directional && (light.cullingMask & 1) != 0)
                    {
                        _sun = light;
                        break;
                    }
                }
            }

            if (_sun != null)
            {
                _sunBaseIntensity = _sun.intensity;
                _sunBaseColor = _sun.color;
            }

            _ambientBase = RenderSettings.ambientIntensity;
            _sunCached = true;
        }

        if (_sun != null)
        {
            _sun.intensity = _sunBaseIntensity * Mathf.Lerp(1f, 0.45f, w);
            _sun.color = Color.Lerp(_sunBaseColor, new Color(0.62f, 0.68f, 0.78f), w * 0.6f);
        }

        RenderSettings.ambientIntensity = _ambientBase * Mathf.Lerp(1f, 0.65f, w);

        // Wet ground: darker and glossier. The flat-material cache is shared
        // by every tile, so this touches a few dozen materials, not thousands.
        foreach (var pair in _flatMaterials)
        {
            if (pair.Value == null || !_flatBaseColors.TryGetValue(pair.Key, out var baseColor))
            {
                continue;
            }

            pair.Value.color = baseColor * Mathf.Lerp(1f, 0.68f, w);
            pair.Value.SetFloat("_Smoothness", 0.75f * w);
        }

        if (_grassMaterial != null)
        {
            _grassMaterial.color = _grassBaseColor * Mathf.Lerp(1f, 0.68f, w);
        }
    }

    private GameObject _seaPlane;

    private void EnsureSeaPlane()
    {
        if (_seaPlane != null)
        {
            return;
        }

        var waterY = SimulationUnityMapper.TileHeight - ElevationStep * 0.45f;

        // Spec 20.16: an opaque deep sea floor well below the surface. It gives
        // the stylized water real depth everywhere (so open sea reads deep and
        // foam-free); the only shallow intersections left are the land walls
        // rising through the surface — i.e. foam hugs the shore, nothing else.
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "SeaFloor";
        floor.transform.SetParent(transform, false);
        floor.transform.position = new Vector3(0f, waterY - 1.6f, 0f);
        floor.transform.localScale = new Vector3(40f, 1f, 40f);
        var floorRenderer = floor.GetComponent<MeshRenderer>();
        if (floorRenderer is not null)
        {
            floorRenderer.sharedMaterial = CreateMaterial(new Color(0.06f, 0.14f, 0.26f));
        }

        // Spec 20.16: the ocean surface extends past the playable bounds.
        _seaPlane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _seaPlane.name = "Sea";
        _seaPlane.transform.SetParent(transform, false);
        _seaPlane.transform.position = new Vector3(0f, waterY, 0f);
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
        _swimCoords.Clear();
        _tileElevations.Clear();
        _indoorCoords.Clear();
        foreach (var tile in snapshot.Tiles)
        {
            _tileElevations[tile.Coord] = tile.Elevation;
            if (tile.Water)
            {
                _waterCoords.Add(tile.Coord);

                // §40.18-B: deep (unwalkable) water is swum, not waded — the
                // actor's root sinks below the surface there.
                if (!tile.Walkable)
                {
                    _swimCoords.Add(tile.Coord);
                }
            }

            if (tile.Indoor)
            {
                _indoorCoords.Add(tile.Coord);
            }
        }

        foreach (var tile in snapshot.Tiles)
        {
            if (!_tileViews.ContainsKey(tile.Coord))
            {
                _tileViews[tile.Coord] = CreateTileView(tile, IsSandTile(tile));
            }
        }

        // Spec 20.16: one merged, finely-tessellated water surface for an even
        // seam-free wave across every water hex (see EnsureWaterSurface).
        EnsureWaterSurface(snapshot);

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

            // Spec 40.17: climb-seam dots — always shown, once per seam.
            if (junction.IsClimbSeam && !_seamMarkers.ContainsKey(junction.Id.Value))
            {
                _seamMarkers[junction.Id.Value] = CreateSeamMarker(junction);
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
            var view = _objectViews[key];
            _objectViews.Remove(key);
            _prevObjectPositions.Remove(key);
            _currObjectPositions.Remove(key);

            // Spec §54: a felled tree tilts over and leaves a stump instead of
            // vanishing — the logs the sim scattered land around it in the same
            // frame, so it reads as "chopped down".
            if (_treeViewKeys.Remove(key) && view != null)
            {
                var fall = view.AddComponent<HexLive.UnityPresentation.Environment.TreeFall>();
                fall.Fell(HexRadius);
            }
            else if (view != null)
            {
                Destroy(view);
            }
        }

        // Spec 40.13: the ragdolled body follows its corpse object out of the
        // world (decayed or buried into a grave).
        var staleCorpseBodies = new List<int>();
        foreach (var key in _corpseBodyViews.Keys)
        {
            if (!liveObjectIds.Contains(key))
            {
                staleCorpseBodies.Add(key);
            }
        }

        foreach (var key in staleCorpseBodies)
        {
            Destroy(_corpseBodyViews[key]);
            _corpseBodyViews.Remove(key);
        }

        // §Wardrobe-anim: a garment being donned vanishes from the ground the
        // moment its owner lifts it into hand (the "don" beat), so we never show
        // the same piece both on the floor and in the hand.
        _wardrobeHiddenObjects.Clear();
        foreach (var n in snapshot.Npcs)
        {
            if (n.CurrentInteraction == "Dress" &&
                n.InteractionProgress >= WardrobeHandoffFraction &&
                n.TargetObjectId is { } hiddenId)
            {
                _wardrobeHiddenObjects.Add(hiddenId);
            }
        }

        foreach (var worldObject in snapshot.Objects)
        {
            var key = worldObject.Id.Value;
            // Spec 40.13: an adopted actor body IS the corpse's view — no blob.
            if (_corpseBodyViews.ContainsKey(key))
            {
                continue;
            }

            if (!_objectViews.TryGetValue(key, out var objectView))
            {
                objectView = CreateObjectView(worldObject, junctionPositions);
                _objectViews[key] = objectView;
                // Spec §54: remember trees so felling them animates.
                if (worldObject.DefinitionId.Contains("tree"))
                {
                    _treeViewKeys.Add(key);
                }
            }

            // §Wardrobe-anim: hide/show the ground garment as its owner picks it
            // up / drops it (SetActive is idempotent, so this is cheap per frame).
            var shouldHide = _wardrobeHiddenObjects.Contains(key);
            if (objectView.activeSelf == shouldHide)
            {
                objectView.SetActive(!shouldHide);
            }

            // Spec 29E.3: the campfire burns only while it has fuel.
            if (worldObject.DefinitionId == "campfire.spot")
            {
                var fire = objectView.GetComponent<HexLive.UnityPresentation.Environment.CampfireEffect>();
                if (fire != null)
                {
                    fire.SetLit(worldObject.ResourceAmount > 0f);
                }
            }

            // Spec §54: re-pile a build-site as its delivered materials grow.
            if (!string.IsNullOrEmpty(worldObject.BuildProduct))
            {
                var pile = objectView.GetComponent<HexLive.UnityPresentation.Environment.BuildSitePile>();
                if (pile != null)
                {
                    pile.Refresh(worldObject);
                }
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

            // ActorGroundY is the STATIC surface (tile top − sink); the live
            // wave swell is added per render frame in InterpolateViews so the
            // swimmer bobs with the exact surface under her, not a snapshot.
            _npcOnWater[key] = _waterCoords.Contains(npc.Tile);
            var targetPos = SimulationUnityMapper.ToUnityPosition(npc.Position, ActorGroundY(npc.Tile));
            var targetRot = Quaternion.Euler(0f, SimulationUnityMapper.ToUnityYawDegrees(npc.RotationDegrees), 0f);
            var targetPose = new Pose(targetPos, targetRot);

            if (_currNpcPoses.TryGetValue(key, out var oldPose))
            {
                _prevNpcPoses[key] = oldPose;

                // §45: stepping onto a tile one level up/down is a visible
                // hop, not a glide — the actor plays JumpUp/JumpDown and its
                // own Y-offset curve carries the body to the exact new level.
                var stepDy = targetPos.y - oldPose.Position.y;
                if (Mathf.Abs(stepDy) > ElevationStep * 0.5f &&
                    _actorViews.TryGetValue(key, out var jumper) && jumper != null)
                {
                    jumper.TriggerHexStepJump(stepDy);
                }
            }
            else
            {
                _prevNpcPoses[key] = targetPose;
            }

            _currNpcPoses[key] = targetPose;

            SyncActorView(snapshot, npc);

            // Spec 40.2-B/C: a bleeding girl drips blood. On land it pools at
            // her feet; IN THE WATER (iteration 1: detected via _npcOnWater) it
            // billows on the surface around her instead — different spawn, no
            // ground puddle underwater.
            if (_npcOnWater.TryGetValue(key, out var onWaterNow) && onWaterNow)
            {
                var waterSurfaceY = GroundY(npc.Tile) - ElevationStep * 0.4f;
                EnsureWaterBlood().OnNpcTick(key, npc.Blood, targetPos, waterSurfaceY, snapshot.Tick);
            }
            else
            {
                EnsureBloodStains().OnNpcTick(key, npc.Blood, targetPos, snapshot.Tick);
            }
        }

        _bloodStains?.Advance(snapshot.Tick);
        _waterBlood?.Advance(snapshot.Tick);

        UpdateGrassFlattening(snapshot);

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
            // Spec 40.13: death — the actor body stays where she fell as a
            // physics ragdoll, adopted as her corpse.npc object's view (the
            // sim already drives grief/mourn/bury around that object). Only
            // when no matching corpse exists does the view just vanish.
            var adopted = false;
            if (_actorViews.TryGetValue(key, out var deadActor) && deadActor != null)
            {
                foreach (var worldObject in snapshot.Objects)
                {
                    if (worldObject.DefinitionId != "corpse.npc" ||
                        worldObject.OwnerNpcId != key)
                    {
                        continue;
                    }

                    var corpseId = worldObject.Id.Value;
                    deadActor.SetLedgeSit(false);
                    deadActor.ClearGaze();
                    // Spec 40.13 v2: death is a quiet lie-down, then the pose
                    // freezes (SetDead) — NO ragdoll: the hex tiles carry no
                    // colliders, so physics bodies spun out and fell through.
                    deadActor.SetRagdoll(false);
                    deadActor.SetDead(GroundY(worldObject.Tile));

                    // The capsule-blob corpse view from this frame's object
                    // pass is replaced by the real body.
                    if (_objectViews.TryGetValue(corpseId, out var blob))
                    {
                        Destroy(blob);
                        _objectViews.Remove(corpseId);
                        _prevObjectPositions.Remove(corpseId);
                        _currObjectPositions.Remove(corpseId);
                    }

                    _corpseBodyViews[corpseId] = _npcViews[key];
                    adopted = true;
                    break;
                }
            }

            if (!adopted)
            {
                Destroy(_npcViews[key]);
            }

            _npcViews.Remove(key);
            _actorViews.Remove(key);
            _prevNpcPoses.Remove(key);
            _currNpcPoses.Remove(key);
            _npcOnWater.Remove(key);
        }
    }

    // Spec 31B.5: wardrobe, walk animation, and gaze follow the snapshot.
    private void SyncActorView(WorldSnapshot snapshot, NpcSnapshot npc)
    {
        if (!_actorViews.TryGetValue(npc.Id.Value, out var actorView) || actorView == null)
        {
            return;
        }

        // Fast-forward: animations play at the sim's speed multiplier, so 2× /
        // 4× / 50× worlds move bodies 2× / 4× / 50× faster too (1× on pause).
        actorView.SetSimSpeed(_runner != null && !_runner.IsPaused ? _runner.SpeedMultiplier : 1f);
        // §40.18-B: in deep water the animator swims (tread idle / strokes).
        actorView.SetSwimming(_swimCoords.Contains(npc.Tile));
        // §21.21B: sim-driven hex-step jump — the view flies its ballistic
        // arc (and compresses the jump clip) over the sim's hop window. The
        // height delta is the EXACT root-level difference, so a dive into
        // water lands at swim depth (ActorGroundY handles the sink), not one
        // dry step down.
        // §21.21B v4 circle climbing: the sim itself steps her back onto the
        // hex inner circle during the takeoff beat and flies circle-to-circle
        // — the view only needs the exact height delta and the water flag.
        actorView.SetHopSignal(npc.HopKind,
            npc.HopKind.Length > 0
                ? ActorGroundY(npc.HopTargetTile) - ActorGroundY(npc.Tile)
                : 0f,
            npc.HopKind.Length > 0 && _swimCoords.Contains(npc.HopTargetTile));
        actorView.SyncWorn(npc.WornItems);
        actorView.SetInteraction(npc.CurrentInteraction, HeldItemFor(npc));
        // §Wardrobe-anim: the two-beat dress/undress sequence (gather + garment
        // in hand). Runs after SetInteraction, which it overrides for these verbs.
        actorView.SetWardrobeAction(npc.CurrentInteraction, npc.InteractionProgress, npc.HeldGarmentId);
        // Spec 28.15E: overhead chat bubble — show the talk's emoji, and pop a
        // "+/-" once when a talk outcome resolves (new TalkResultTick).
        actorView.SetTalkTopic(npc.TalkTopic);
        if (npc.TalkResultTick > 0 &&
            (!_lastTalkResultTick.TryGetValue(npc.Id.Value, out var seenTick) ||
             seenTick != npc.TalkResultTick))
        {
            _lastTalkResultTick[npc.Id.Value] = npc.TalkResultTick;
            // Skip the very first observation per NPC (avoid a stale pop when a
            // view is created for an NPC that already talked before we looked).
            if (seenTick != 0 || npc.TalkResultTick == snapshot.Tick)
            {
                actorView.PopRelationship(npc.TalkResultDelta);
            }
        }
        // Iter 28: ledge seat — the sim flags a sit at a one-step seam; the
        // view lifts the butt onto the upper step (knobs in NpcActorView).
        actorView.SetLedgeSit(npc.IsLedgeSit, npc.LedgeSeatStepsUp);
        // Spec 20.16: hunting/combat shows the weapon and drives a draw/thrust.
        actorView.SetCombat(npc.IsFighting, WeaponFor(npc));
        // Spec 33.1: a carried weapon rides slung on the back when it isn't in
        // the hand (SetBackWeapon hides it if it's the current hand prop).
        actorView.SetBackWeapon(BackWeaponFor(npc));
        // Spec 40.7: shiver when cold, fan when hot (signed thermal comfort).
        actorView.SetThermal(npc.ThermalComfort);
        // Face mood: the expression mirrors overall wellbeing — fed needs and
        // health lift it, hunger/thirst/exhaustion drag it down; a fight
        // switches the face to anger.
        var wellbeing =
            ((1f - npc.Hunger) + (1f - npc.Thirst) + npc.Energy +
             npc.Comfort + npc.Social + npc.Health) / 6f;
        actorView.SetFaceMood(wellbeing, npc.IsFighting);
        // A fresh bleeding wound makes her wince in pain. A wound bleeds only
        // while fresh (heal01 < 0.3, spec 44); pain scales with the freshest
        // open wound and fades to nothing as they clot/heal.
        var pain = 0f;
        if (npc.Wounds != null)
        {
            foreach (var w in npc.Wounds)
            {
                var wparts = w.Split('|');
                if (wparts.Length >= 3 && float.TryParse(wparts[2],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var heal))
                {
                    pain = Mathf.Max(pain, Mathf.Clamp01((0.3f - heal) / 0.3f));
                }
            }
        }

        actorView.SetFacePain(pain);
        // Spec 40.9 / 40.1: injury posture (limp/crawl/arm-hang/head-clutch)
        // and the winded panting, both derived sim-side and exported.
        actorView.SetPosture(npc.PostureHint, npc.Winded);
        // Spec 40.7/40.8: weather the bare skin — tan browns it, sunburn
        // reddens it. (The old low-HP bruised-red whole-body flush was
        // retired: the painted wound marks carry the injury look on their
        // own — the flush just muddied them.)
        actorView.SetSkinWeathering(npc.TanLevel, npc.Sunburn, 0f, npc.Hygiene);
        // Spec 40.8/40.6: persistent skin decals — wound marks per hurt zone,
        // dust as hygiene drops, sweat droplets in the heat. Bare zones only.
        // Debug overrides: forced sweat, and clothes-off exposes every zone.
        var thermalForSweat = UI.DebugControlsPanel.SweatOverride ?? npc.ThermalComfort;
        var uncoveredForDecals = UI.DebugControlsPanel.HideClothing ? AllBodyZones : npc.UncoveredParts;
        // Spec 35.5: rain reuses the sweat tech — an NPC standing outdoors in
        // the rain glistens and beads exactly like sweating; garments carry
        // their own sim wetness (soaked cloth shines/darkens, dries back).
        var rainWet = snapshot.IsRaining && !_indoorCoords.Contains(npc.Tile) ? 1f : 0f;
        actorView.SetBodyCondition(npc.BodyParts, uncoveredForDecals, npc.Hygiene, thermalForSweat,
            rainWet, npc.WornWetness, npc.Wounds, npc.BandagedZones, npc.SeveredParts);
        actorView.SetClothingHidden(UI.DebugControlsPanel.HideClothing);
        // Portrait isolation: keep the whole actor hierarchy (incl. garments,
        // props and decals spawned this tick) on the Actors layer.
        actorView.EnsureActorLayer();
        // Spec 40.10: tear worn-out garments — cutoff erosion by durability.
        foreach (var entry in npc.WornDurability)
        {
            var tab = entry.IndexOf('\t');
            if (tab > 0 && float.TryParse(
                    entry.Substring(tab + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var durability))
            {
                actorView.SetGarmentWear(entry.Substring(0, tab), durability);
            }
        }

        // Spec 31C.2: sleeping happens lying on the bed's attach point.
        // Spec 40.13: a fainted body lies limp where it dropped (no bed).
        if (npc.IsFainted)
        {
            // Spec 40.13 v2: collapse lies down with the baked laying clip —
            // ragdoll physics is retired (no tile colliders to land on).
            actorView.SetRagdoll(false);
            actorView.SetLaying(true, null, GroundY(npc.Tile));
        }
        else if (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress")
        {
            actorView.SetRagdoll(false);
            actorView.SetLaying(true, FindBedAttachPoint(snapshot, npc, out var bedSurfaceY), bedSurfaceY);
        }
        else
        {
            actorView.SetRagdoll(false);
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
            case "Process":
                // Spec §54: splitting a log — the axe (or saw) is in hand.
                if (npc.InventoryItems.Contains("tool.axe_stone"))
                {
                    return "tool.axe_stone";
                }

                return npc.InventoryItems.Contains("tool.saw") ? "tool.saw" : null;
            case "Butcher":
                // Spec §54: the knife is in hand while butchering.
                return npc.InventoryItems.Contains("tool.knife") ? "tool.knife" : null;
            case "Fuel":
                // Spec §54: a stick feeds the fire.
                return npc.InventoryItems.Contains("resource.stick")
                    ? "resource.stick" : null;
            case "Craft":
                if (npc.CurrentGoal == "CookMeat" &&
                    npc.InventoryItems.Contains("food.meat_raw"))
                {
                    return "food.meat_raw";
                }

                return npc.InventoryItems.Contains("resource.stick")
                    ? "resource.stick" : null;
            case "Build":
                // Spec §54: builds are log-framed.
                return npc.InventoryItems.Contains("resource.log")
                    ? "resource.log" : null;
            // Spec 29H: fill the bottle and drink from it — the bottle shows in hand.
            case "FillBottle":
                return "tool.bottle";
            // §55: coconut water is sipped straight from the husk, so the coconut
            // shows in hand; plain water is drunk from the bottle. (Was always the
            // bottle — a coconut drink wrongly raised a bottle / nothing.)
            case "Drink":
                foreach (var item in npc.InventoryItems)
                {
                    if (item.StartsWith("food.coconut"))
                    {
                        return item;
                    }
                }

                return "tool.bottle";
            default:
                return null;
        }
    }

    // Spec 33.1: the weapon slung on the back — the carried spear/bow, so it
    // is always visibly "equipped" even when idle. SetBackWeapon hides it if
    // it happens to be in the hand this frame (fighting).
    private static string BackWeaponFor(NpcSnapshot npc)
    {
        if (npc.InventoryItems.Contains("tool.bow"))
        {
            return "tool.bow";
        }

        return npc.InventoryItems.Contains("tool.spear") ? "tool.spear" : null;
    }

    // Debug clothes-off mode treats the whole body as bare for skin decals.
    private static readonly List<string> AllBodyZones = new()
    {
        "Head", "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR"
    };

    // Spec 20.16: the weapon an NPC fights/hunts with — bow (with arrows)
    // preferred, then spear, then the knife as a last-ditch melee blade. Null
    // only when truly unarmed (bare-handed brawl).
    private static string WeaponFor(NpcSnapshot npc)
    {
        if (npc.InventoryItems.Contains("tool.bow") && npc.InventoryItems.Contains("resource.arrow"))
        {
            return "tool.bow";
        }

        if (npc.InventoryItems.Contains("tool.spear"))
        {
            return "tool.spear";
        }

        return npc.InventoryItems.Contains("tool.knife") ? "tool.knife" : null;
    }

    // The bed she is sleeping on: nearest bed object view within ~a tile.
    private Transform? FindBedAttachPoint(WorldSnapshot snapshot, NpcSnapshot npc, out float surfaceY)
    {
        // Default: the sleeper's own tile top, used when there is no bed.
        surfaceY = GroundY(npc.Tile);

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

        // The body's underside should rest on the bed's top surface.
        var bedRenderers = bedView.GetComponentsInChildren<Renderer>();
        if (bedRenderers.Length > 0)
        {
            var bedBounds = bedRenderers[0].bounds;
            for (var i = 1; i < bedRenderers.Length; i++)
            {
                bedBounds.Encapsulate(bedRenderers[i].bounds);
            }

            surfaceY = bedBounds.max.y + SleepBodyLift;
        }
        else
        {
            surfaceY = GroundY(bed.Tile);
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

        // Spec §54.2: an explicit "point" marker (our assembled beds place one)
        // also defines the sleep HEIGHT — the body lies at the point's Y, not the
        // bed's bbox top, so each bed's tuned lift is honoured.
        if (point != null)
        {
            surfaceY = point.position.y;
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

            var onWater = _npcOnWater.TryGetValue(key, out var w) && w;

            if (_prevNpcPoses.TryGetValue(key, out var prev))
            {
                var pos = Vector3.Lerp(prev.Position, curr.Position, alpha);
                // §45 hex-step jump: don't ALSO lerp the root's Y across the
                // step — the actor animates the vertical itself (jump offset),
                // so the root snaps straight to the new ground level.
                if (Mathf.Abs(curr.Position.y - prev.Position.y) > ElevationStep * 0.5f)
                {
                    pos.y = curr.Position.y;
                }

                // §40.18-B: ride the live wave swell at her exact XZ — same
                // formula the shader displaces the surface with (WaterWave).
                if (onWater)
                {
                    pos.y += WaterWave.HeightNow(pos.x, pos.z);
                }

                view.transform.position = pos;
                view.transform.rotation = Quaternion.Slerp(prev.Rotation, curr.Rotation, alpha);
            }
            else
            {
                var pos = curr.Position;
                if (onWater)
                {
                    pos.y += WaterWave.HeightNow(pos.x, pos.z);
                }

                view.transform.position = pos;
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
    private readonly HashSet<TileCoord> _swimCoords = new();

    // Spec 35.5: rain wets only NPCs standing outdoors.
    private readonly HashSet<TileCoord> _indoorCoords = new();

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

    // §40.18-B: where an ACTOR's root sits on a tile. On land that is the
    // ground; in deep water she hangs SinkDepth below the water surface; in
    // walkable shallows (the river) she wades WadeDepth under it — knee-deep,
    // not walking ON the water.
    private float ActorGroundY(TileCoord coord)
    {
        if (!_waterCoords.Contains(coord))
        {
            return GroundY(coord);
        }

        // Water tiles render their surface sunken 40% of a step below the
        // tile top (spec 31C.4) — mirror CreateTileView's formula.
        var surfaceY = GroundY(coord) - ElevationStep * 0.4f;
        return surfaceY - (_swimCoords.Contains(coord)
            ? SwimVisuals.SinkDepth
            : SwimVisuals.WadeDepth);
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

        // Spec 20.16: hexes are prisms — top at the tile's elevation, skirt
        // to a shared base so hills read as solid terrain, not floating caps.
        var topHeight = SimulationUnityMapper.TileHeight + tile.Elevation * ElevationStep;
        if (tile.Water)
        {
            topHeight -= ElevationStep * 0.4f; // sunken water surface
        }

        if (tile.Water)
        {
            // Spec 20.16: the wavy water SURFACE is NOT built here. Per-tile
            // hex tops each tented independently around their centre vertex
            // (7 verts/hex under-sampled the world-space wave) and their
            // separate transparent draws double-blended along shared edges —
            // both read as visible seams between hexes. EnsureWaterSurface()
            // merges every water tile into ONE finely-tessellated mesh with a
            // single transparent draw, so the wave is one smooth even sheet.
            // Only the opaque per-tile riverbed stays here.

            // §31C.4/§40.18-B: wadable shallows get a visible RIVERBED hex
            // exactly WadeDepth under the surface — the same height the
            // actors sink to, so a knee-deep girl reads as standing on the
            // bottom instead of hovering inside translucent water.
            if (tile.Walkable)
            {
                var bed = new GameObject("Riverbed");
                bed.transform.SetParent(go.transform, false);
                var bedFilter = bed.AddComponent<MeshFilter>();
                bedFilter.sharedMesh = BuildHexPrismMesh(
                    HexRadius, topHeight - SwimVisuals.WadeDepth, TerrainBaseY,
                    world.X, world.Y, includeSkirt: true);
                var bedRenderer = bed.AddComponent<MeshRenderer>();
                // Queue 2999: drawn before the water (which tints it), but
                // ABOVE the opaque cutoff (2500) — so the bed never enters
                // _CameraDepthTexture. The stylized water reads depth from
                // that texture: with the bed visible to it the whole river
                // became "2 cm shallow" and foamed edge to edge. This way the
                // water sees the deep SeaFloor and the river renders exactly
                // like the sea, while the girls still stand on a real bottom.
                bedRenderer.sharedMaterials = new[]
                {
                    RiverbedMaterial(Jitter(BiomeColor("sand"), tile.Coord, 0.06f)),
                    RiverbedMaterial(Jitter(BiomeColor("cliff"), tile.Coord, 0.05f))
                };
            }
        }
        else
        {
            // Submesh 0 = flat top, submesh 1 = the perimeter skirt/cliff.
            var meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = BuildHexPrismMesh(
                HexRadius, topHeight, TerrainBaseY, world.X, world.Y, includeSkirt: true);
            var meshRenderer = go.AddComponent<MeshRenderer>();

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

    // The live water material (sea plane + water tiles share it). The
    // day/night controller darkens its colours with the directional light so
    // the sea stops glowing at night.
    public static Material ActiveWaterMaterial { get; private set; }

    private static Material CreateWaterMaterial()
    {
        if (_waterMaterial != null)
        {
            return _waterMaterial;
        }

        // Spec 20.16: prefer the imported "Definitive Stylized Water URP"
        // material (depth gradient, animated foam, fresnel, refraction) — the
        // tuned asset carries its distortion/foam texture with it. A runtime
        // COPY: the controller tints it per frame, and tinting the loaded
        // asset directly would dirty the .mat on disk in the editor.
        var definitive = Resources.Load<Material>("HexLive/Water/StylizedWaterDefinitive");
        if (definitive != null)
        {
            _waterMaterial = new Material(definitive);
            ActiveWaterMaterial = _waterMaterial;
            return _waterMaterial;
        }

        // Fallback: my hand-written stylized water shader.
        var stylized = Shader.Find("HexLive/StylizedWater");
        if (stylized != null)
        {
            _waterMaterial = new Material(stylized);
            ActiveWaterMaterial = _waterMaterial;
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

    // Spec 40.17: a small amber dot sitting on a climb seam — where an NPC
    // clambers up a one-step ledge. Scaled below the debug junction sphere so
    // it reads as a marker, lifted a touch above the ground.
    private GameObject CreateSeamMarker(JunctionSnapshot junction)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = $"ClimbSeam {junction.Id.Value}";
        go.transform.SetParent(_junctionsRoot, false);
        go.transform.localScale = Vector3.one * (JunctionMarkerScale * 1.6f);
        go.transform.position = SimulationUnityMapper.ToUnityPosition(
            junction.WorldPosition,
            SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift * 2f);

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            // Warm amber, distinct from terrain — reads as a climb hint.
            renderer.sharedMaterial = CreateMaterial(new Color(0.88f, 0.54f, 0.24f));
        }

        return go;
    }

    private GameObject CreateObjectView(ObjectSnapshot worldObject, Dictionary<int, Float2> junctionPositions)
    {
        // Spec 31C.4: water interaction anchors have no gizmo — the river
        // and pond tiles ARE the visual; NPCs just come and drink.
        // Spec 40.13 v2: corpse.npc has no blob either — the dead actor's own
        // body (adopted, lying asleep) IS the corpse; and death spawns no
        // visible grave marker at all.
        if (worldObject.DefinitionId.StartsWith("water.") ||
            worldObject.DefinitionId == "corpse.npc" ||
            worldObject.DefinitionId == "grave.npc")
        {
            var invisible = new GameObject($"Object {worldObject.DefinitionId} (anchor)");
            invisible.transform.SetParent(_objectsRoot, false);
            return invisible;
        }

        // Spec §54: a build-site shows the piece ASSEMBLING from its delivered
        // materials — a pile of hauled stones/logs/leaves growing toward the
        // bill, instead of a flat pad. Refreshed each frame as more is delivered.
        if (!string.IsNullOrEmpty(worldObject.BuildProduct))
        {
            var siteRoot = new GameObject($"Object {worldObject.DefinitionId} (site {worldObject.BuildProduct})");
            siteRoot.transform.SetParent(_objectsRoot, false);
            var siteAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            siteRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                siteAnchor, GroundY(worldObject.Tile));
            var pile = siteRoot.AddComponent<HexLive.UnityPresentation.Environment.BuildSitePile>();
            pile.Rebuild(worldObject);
            return siteRoot;
        }

        // Spec §50: a severed limb — the real limb geometry carved from its
        // former owner's body mesh (falls through to a primitive when the
        // owner/mesh can't provide it, e.g. a non-readable import).
        if (worldObject.DefinitionId == "body.limb_severed")
        {
            NpcActorView owner = null;
            if (worldObject.OwnerNpcId is { } ownerId)
            {
                _actorViews.TryGetValue(ownerId, out owner);
            }

            var anchorPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            var worldPos = SimulationUnityMapper.ToUnityPosition(anchorPos, GroundY(worldObject.Tile));

            var limb = HexLive.UnityPresentation.Wearing.SeveredLimbFactory.Build(
                owner, worldObject.Variant);
            if (limb != null)
            {
                var limbRoot = new GameObject($"Object {worldObject.DefinitionId} {worldObject.Variant}");
                limbRoot.transform.SetParent(_objectsRoot, false);
                limb.transform.SetParent(limbRoot.transform, false);
                var limbScale = HexRadius * NpcHeightFactor * 2.4f / ActorSourceHeightMeters;
                limb.transform.localScale = Vector3.one * limbScale;
                limb.transform.localRotation = Quaternion.Euler(
                    90f, (worldObject.Id.Value * 47) % 360, 0f); // lie on its side, scattered yaw
                GroundVisual(limb, lift: 0.05f);
                limbRoot.transform.position = worldPos;
                Debug.Log($"[§50 limb] REAL MESH '{worldObject.Variant}' obj#{worldObject.Id.Value} " +
                    $"at {worldPos} (owner {worldObject.OwnerNpcId})");
                return limbRoot;
            }

            // Fallback (the actor mesh isn't Read/Write, or the owner is gone):
            // a clearly visible blood-red limb-sized capsule, so the dropped
            // limb is NEVER invisible on scene — findable by name "…(fallback)".
            var fbRoot = new GameObject($"Object {worldObject.DefinitionId} {worldObject.Variant} (fallback)");
            fbRoot.transform.SetParent(_objectsRoot, false);
            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "LimbCapsule";
            capsule.transform.SetParent(fbRoot.transform, false);
            capsule.transform.localScale = new Vector3(0.22f, 0.5f, 0.22f);
            capsule.transform.localRotation = Quaternion.Euler(80f, (worldObject.Id.Value * 47) % 360, 0f);
            var capMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            capMat.SetColor("_BaseColor", new Color(0.7f, 0.04f, 0.04f));
            capMat.SetFloat("_Smoothness", 0.15f);
            capsule.GetComponent<MeshRenderer>().sharedMaterial = capMat;
            GroundVisual(capsule, lift: 0.12f); // lifted so it's not buried under her feet
            fbRoot.transform.position = worldPos;
            Debug.Log($"[§50 limb] FALLBACK capsule '{worldObject.Variant}' obj#{worldObject.Id.Value} " +
                $"at {worldPos} (owner {worldObject.OwnerNpcId}) — enable Read/Write on the actor mesh for real geometry");
            return fbRoot;
        }

        // Spec §54.2: a palm is ASSEMBLED from N trunk-segment logs + a crown, so
        // it visibly matches the logs/crown that drop when it's felled. Already
        // absolute-sized (each segment = a dropped log), so no FitObjectPrefab.
        if (HexLive.UnityPresentation.Environment.PalmTreeFactory.IsPalm(worldObject.DefinitionId))
        {
            var palm = HexLive.UnityPresentation.Environment.PalmTreeFactory.Build(worldObject.DefinitionId);
            if (palm != null)
            {
                palm.transform.SetParent(_objectsRoot, false);
                var palmAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                palm.transform.position = SimulationUnityMapper.ToUnityPosition(
                    palmAnchor, GroundY(worldObject.Tile));
                // §54.2: fell it with the same tilt+stump animation as any tree.
                return palm;
            }
        }

        // §54.2: the felled-palm stump — a short standing ring-cut log you can
        // perch on (a sim obstacle with a Sit interaction).
        if (worldObject.DefinitionId == "stump.palm")
        {
            var stumpRoot = new GameObject("Object stump.palm");
            stumpRoot.transform.SetParent(_objectsRoot, false);
            var stump = HexLive.UnityPresentation.Environment.StumpFactory.Build(HexRadius);
            stump.transform.SetParent(stumpRoot.transform, false);
            stump.transform.localPosition = new Vector3(
                0f, HexLive.UnityPresentation.Environment.StumpFactory.BaseLift, 0f);
            var stumpAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            stumpRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                stumpAnchor, GroundY(worldObject.Tile));
            return stumpRoot;
        }

        // Spec §54.2: the dropped palm crown is a fluffy cluster of leaves — no
        // trunk. Same builder the standing palm's top uses; the big palm's crown
        // is fuller than the small one's (matching its leaf drop).
        if (worldObject.DefinitionId == "resource.palm_crown" ||
            worldObject.DefinitionId == "resource.palm_crown_small")
        {
            var frondCount = worldObject.DefinitionId == "resource.palm_crown_small"
                ? HexLive.Simulation.Runtime.SimBalance.SmallPalmCrownLeaves
                : HexLive.Simulation.Runtime.SimBalance.BigPalmCrownLeaves;
            var crown = HexLive.UnityPresentation.Environment.PalmCrownFactory.Build(HexRadius * 0.85f, frondCount);
            if (crown != null)
            {
                var crownRoot = new GameObject("Object resource.palm_crown");
                crownRoot.transform.SetParent(_objectsRoot, false);
                crown.transform.SetParent(crownRoot.transform, false);
                var cAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                crownRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                    cAnchor, GroundY(worldObject.Tile));
                return crownRoot;
            }
        }

        // Spec §54.2: the beds are the assembled prefab (bed_leaf_final /
        // bed_basic_final) with every piece toggled on — the same prefab a
        // build-site grows piece by piece, so finished and in-progress match.
        if (HexLive.UnityPresentation.Environment.BedFactory.IsBed(worldObject.DefinitionId))
        {
            var bed = HexLive.UnityPresentation.Environment.BedAssembly.BuildFinished(worldObject.DefinitionId);
            if (bed != null)
            {
                bed.transform.SetParent(_objectsRoot, false);
                var bedAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                bed.transform.position = SimulationUnityMapper.ToUnityPosition(
                    bedAnchor, GroundY(worldObject.Tile));
                return bed;
            }
        }

        // Spec 31C.3: real prefabs first (Resources/HexLive/Objects/<id>),
        // primitives as the eternal fallback.
        var objectPrefab = Resources.Load<GameObject>($"HexLive/Objects/{worldObject.DefinitionId}");
        if (objectPrefab != null)
        {
            var prefabRoot = new GameObject($"Object {worldObject.DefinitionId}");
            prefabRoot.transform.SetParent(_objectsRoot, false);
            var instance = Instantiate(objectPrefab, prefabRoot.transform);
            FitObjectPrefab(instance, worldObject.DefinitionId, worldObject.Id.Value);
            var anchorPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            prefabRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                anchorPos, GroundY(worldObject.Tile));
            MaybeAttachCampfire(prefabRoot, worldObject.DefinitionId);
            return prefabRoot;
        }

        // Spec 40.19: dropped clothing shows the real garment lying flat on
        // the ground (footwear stands as-is). The factory's pivot is the
        // garment's own centre, so the drop sits exactly on its anchor.
        var garment = HexLive.UnityPresentation.Wearing.GarmentDropFactory.Build(worldObject.DefinitionId);
        if (garment != null)
        {
            var garmentRoot = new GameObject($"Object {worldObject.DefinitionId}");
            garmentRoot.transform.SetParent(_objectsRoot, false);
            garment.transform.SetParent(garmentRoot.transform, false);
            // Same world scale the girls wear it at, so the drop reads
            // proportional to its former owner; deterministic scatter yaw.
            var garmentScale = HexRadius * NpcHeightFactor * 2.4f / ActorSourceHeightMeters;
            garment.transform.localScale = Vector3.one * garmentScale;
            garment.transform.localRotation = Quaternion.Euler(0f, (worldObject.Id.Value * 73) % 360, 0f);
            GroundVisual(garment, lift: 0.01f); // epsilon: thin cloth vs tile z-fight
            var garmentPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            garmentRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                garmentPos, GroundY(worldObject.Tile));
            return garmentRoot;
        }

        // Spec 20.16: procedural low-poly model for known tools/resources/food
        // before the generic primitive fallback.
        var lowPoly = HexLive.UnityPresentation.Environment.LowPolyToolFactory.Build(worldObject.DefinitionId);
        if (lowPoly != null)
        {
            var modelRoot = new GameObject($"Object {worldObject.DefinitionId}");
            modelRoot.transform.SetParent(_objectsRoot, false);
            lowPoly.transform.SetParent(modelRoot.transform, false);
            FitObjectPrefab(lowPoly, worldObject.DefinitionId, worldObject.Id.Value);
            var anchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            modelRoot.transform.position = SimulationUnityMapper.ToUnityPosition(anchor, GroundY(worldObject.Tile));
            return modelRoot;
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
    // Spec §54: ring the pit with real low-poly stones (like Stranded Deep's
    // fire pit) so the hearth reads as "piled from stones", lit or not.
    private void MaybeAttachCampfire(GameObject root, string definitionId)
    {
        if (definitionId != "campfire.spot")
        {
            return;
        }

        AddCampfireStoneRing(root.transform);

        var effect = root.AddComponent<HexLive.UnityPresentation.Environment.CampfireEffect>();
        effect.Construct(HexRadius);
    }

    // Spec §54: a ring of ~9 low-poly stones around the fire pit.
    private void AddCampfireStoneRing(Transform parent)
    {
        const int stoneCount = 9;
        var ringRadius = HexRadius * 0.42f;
        for (var i = 0; i < stoneCount; i++)
        {
            var angle = (i / (float)stoneCount) * Mathf.PI * 2f;
            var stone = HexLive.UnityPresentation.Environment.LowPolyToolFactory.Build("resource.stone");
            if (stone == null)
            {
                continue;
            }

            stone.name = "RingStone";
            stone.transform.SetParent(parent, false);
            // Vary size/rotation a touch so the ring doesn't look stamped.
            var s = 0.5f + 0.12f * Mathf.Sin(i * 2.3f);
            stone.transform.localScale = new Vector3(s, s * 0.8f, s);
            stone.transform.localPosition = new Vector3(
                Mathf.Cos(angle) * ringRadius, 0f, Mathf.Sin(angle) * ringRadius);
            stone.transform.localRotation = Quaternion.Euler(0f, i * 40f, 0f);
        }
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
                view.Construct(npc.ActorMesh, npc.Id.Value);
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
    private static Mesh BuildHexPrismMesh(float radius, float top, float baseY, float worldX, float worldZ,
        bool includeSkirt = true)
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
        // Spec 20.16: water tiles skip the skirt so no opaque wall reaches the
        // surface between two water hexes — otherwise the stylized water's
        // depth-intersection foam would ring every hex, not just the shore.
        if (includeSkirt)
        {
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
        }

        mesh.subMeshCount = includeSkirt ? 2 : 1;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(topTris, 0);
        if (includeSkirt)
        {
            mesh.SetTriangles(wallTris, 1);
        }

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private GameObject _waterSurface;

    // Spec 20.16: how many times each of a hex's 6 wedges is subdivided. The
    // Definitive water shader offsets vertices by a UV-driven wave; a bare hex
    // top (7 verts spanning ~3u) sampled it far too coarsely, so each hex
    // tented on its own. Level 4 drops edge length to ~0.37u so the merged
    // sheet reads as one continuous wave. NOTE: the wave frequency is set by
    // the material's _WavesAmplitude (it multiplies UV inside the sine) — if
    // the surface still looks choppy, lower _WavesAmplitude for a gentler,
    // longer wave rather than piling on more triangles here.
    private const int WaterSubdivisions = 4;

    // Merge every water tile's top into a SINGLE tessellated, world-space mesh
    // with one transparent draw. This kills both seam sources at once: the
    // per-hex tenting (now finely sampled + shared across the whole sheet) and
    // the double-blend where separate transparent tile draws overlapped along
    // shared edges. Vertices are baked in world XZ; the object sits at the
    // origin so posWS == the baked coords and the shader wave stays continuous.
    private void EnsureWaterSurface(WorldSnapshot snapshot)
    {
        if (_waterSurface != null)
        {
            return;
        }

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        foreach (var tile in snapshot.Tiles)
        {
            if (!tile.Water)
            {
                continue;
            }

            var world = HexSpatialMath.TileToWorld(tile.Coord);
            var topHeight = SimulationUnityMapper.TileHeight
                          + tile.Elevation * ElevationStep
                          - ElevationStep * 0.4f; // sunken water surface
            AppendSubdividedHexTop(vertices, uvs, tris, world.X, world.Y, topHeight, WaterSubdivisions);
        }

        if (vertices.Count == 0)
        {
            return;
        }

        var mesh = new Mesh
        {
            name = "WaterSurface",
            // Many tiles × subdivided verts easily clears the 16-bit ceiling.
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        _waterSurface = new GameObject("WaterSurface");
        _waterSurface.transform.SetParent(_tilesRoot, false);
        _waterSurface.transform.position = Vector3.zero; // verts already world XZ
        _waterSurface.AddComponent<MeshFilter>().sharedMesh = mesh;
        _waterSurface.AddComponent<MeshRenderer>().sharedMaterial = CreateWaterMaterial();
    }

    // Tessellate one hexagon (centred at world cx,cz, top at y) into a
    // triangular grid and append it to the shared lists. Each of the 6 wedges
    // (centre → rim[i] → rim[i+1]) is split into n² sub-triangles via
    // barycentric subdivision. UVs mirror BuildHexPrismMesh (world XZ scaled).
    private void AppendSubdividedHexTop(
        List<Vector3> vertices, List<Vector2> uvs, List<int> tris,
        float cx, float cz, float y, int n)
    {
        var centre = new Vector3(cx, y, cz);

        var rim = new Vector3[6];
        for (var i = 0; i < 6; i++)
        {
            var angle = Mathf.Deg2Rad * (60f * i - 30f);
            rim[i] = new Vector3(cx + HexRadius * Mathf.Cos(angle), y, cz + HexRadius * Mathf.Sin(angle));
        }

        for (var i = 0; i < 6; i++)
        {
            // Wind centre → rim[i+1] → rim[i] so the top faces +Y (matches
            // BuildHexPrismMesh); the water shader is Cull Off regardless.
            AppendSubdividedTriangle(vertices, uvs, tris, centre, rim[(i + 1) % 6], rim[i], n);
        }
    }

    private void AppendSubdividedTriangle(
        List<Vector3> vertices, List<Vector2> uvs, List<int> tris,
        Vector3 p0, Vector3 p1, Vector3 p2, int n)
    {
        var baseIndex = vertices.Count;

        // Row r (0..n) holds r+1 points along the p0→p1 / p0→p2 fronts.
        for (var r = 0; r <= n; r++)
        {
            for (var c = 0; c <= r; c++)
            {
                var w0 = (n - r) / (float)n;
                var w2 = r == 0 ? 0f : c / (float)n;
                var w1 = 1f - w0 - w2;
                var p = p0 * w0 + p1 * w1 + p2 * w2;
                vertices.Add(p);
                uvs.Add(new Vector2(p.x, p.z) * TopUvScale);
            }
        }

        // Triangulate the barycentric grid (index of (r,c) = r*(r+1)/2 + c).
        for (var r = 1; r <= n; r++)
        {
            var rowStart = baseIndex + r * (r - 1) / 2;       // start of row r-1
            var nextStart = baseIndex + r * (r + 1) / 2;      // start of row r
            for (var c = 0; c < r; c++)
            {
                tris.Add(rowStart + c);
                tris.Add(nextStart + c);
                tris.Add(nextStart + c + 1);
                if (c < r - 1)
                {
                    tris.Add(rowStart + c);
                    tris.Add(nextStart + c + 1);
                    tris.Add(rowStart + c + 1);
                }
            }
        }
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

    // Dry base colours for the wet-terrain effect (rain darkens the cache).
    private static readonly Dictionary<int, Color> _flatBaseColors = new();
    private static Color _grassBaseColor = Color.white;

    // Riverbed variant of the flat terrain material: identical look, but
    // renderQueue 2999 keeps it OUT of _CameraDepthTexture (opaques ≤ 2500)
    // while still drawing under the transparent water — the stylized water
    // must not "see" the bed or the whole river reads as foamy shallows.
    private static readonly Dictionary<int, Material> _riverbedMaterials = new();

    private static Material RiverbedMaterial(Color color)
    {
        var key = (Mathf.RoundToInt(color.r * 24f) << 16)
                  | (Mathf.RoundToInt(color.g * 24f) << 8)
                  | Mathf.RoundToInt(color.b * 24f);
        if (_riverbedMaterials.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        var material = new Material(GetFlatMaterial(color))
        {
            renderQueue = 2999
        };
        _riverbedMaterials[key] = material;
        return material;
    }

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
        _flatBaseColors[key] = color;
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
        _grassBaseColor = new Color(0.36f, 0.56f, 0.24f);
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = _grassBaseColor
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
        _grassByTile[coord] = go;
    }

    // A lying body flattens the grass under it: the tile's tuft clump hides
    // while someone sleeps/faints/lies dead there and pops back after. Cost is
    // O(lying bodies) per tick — only the affected tiles ever toggle.
    private readonly Dictionary<TileCoord, GameObject> _grassByTile = new();
    private readonly HashSet<TileCoord> _lyingTiles = new();
    private readonly HashSet<TileCoord> _hiddenGrassTiles = new();
    private readonly List<TileCoord> _grassToggleScratch = new();

    private void UpdateGrassFlattening(WorldSnapshot snapshot)
    {
        _lyingTiles.Clear();
        foreach (var npc in snapshot.Npcs)
        {
            if (npc.IsFainted ||
                (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress"))
            {
                _lyingTiles.Add(npc.Tile);
            }
        }

        foreach (var obj in snapshot.Objects)
        {
            if (obj.DefinitionId == "corpse.npc")
            {
                _lyingTiles.Add(obj.Tile);
            }
        }

        // Re-grow where nobody lies anymore.
        _grassToggleScratch.Clear();
        foreach (var coord in _hiddenGrassTiles)
        {
            if (!_lyingTiles.Contains(coord))
            {
                _grassToggleScratch.Add(coord);
            }
        }

        foreach (var coord in _grassToggleScratch)
        {
            _hiddenGrassTiles.Remove(coord);
            if (_grassByTile.TryGetValue(coord, out var grass) && grass != null)
            {
                grass.SetActive(true);
            }
        }

        // Flatten under the newly lying.
        foreach (var coord in _lyingTiles)
        {
            if (!_hiddenGrassTiles.Contains(coord) &&
                _grassByTile.TryGetValue(coord, out var grass) && grass != null)
            {
                grass.SetActive(false);
                _hiddenGrassTiles.Add(coord);
            }
        }
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
    private void FitObjectPrefab(GameObject instance, string definitionId, int idValue)
    {
        // Size comes from the shared ObjectFit table (same one the in-hand prop
        // uses in NpcActorView.SetHandProp) so a tool/coconut is the same physical
        // size on the ground and in the hand.
        instance.transform.localScale *= ObjectFit.FitScaleFactor(instance, definitionId);
        // Scattered ground pose BEFORE grounding so the drop rests on its rotated
        // bounds (a lain-flat tool sits on its side, not floating at its old height).
        instance.transform.localRotation = GroundScatterRotation(definitionId, idValue);
        GroundVisual(instance);
    }

    // A dropped prop's ground pose: a deterministic random yaw so the map doesn't
    // read as a rigid grid, and — for items authored standing — a tip onto the
    // side so they lie flat like something dropped, not stuck upright in the soil.
    private static Quaternion GroundScatterRotation(string definitionId, int idValue)
    {
        // Deterministic per-object yaw in [0,360): stable across every view
        // rebuild, unlike UnityEngine.Random which would pop on each refresh.
        uint state = (uint)idValue * 2654435761u;
        var yaw = NextRand(ref state) * 360f;

        if (LiesFlatOnGround(definitionId))
        {
            // Authored handle-+Y (TOOL_GENERATION_SPEC); 90° about X tips that long
            // axis onto the ground, then the yaw scatters which way it points.
            return Quaternion.Euler(90f, yaw, 0f);
        }

        return Quaternion.Euler(0f, yaw, 0f);
    }

    // Spec tools are authored standing (handle +Y). On the ground they should lie
    // on their side like a dropped tool; the pot is a container that rests upright.
    private static bool LiesFlatOnGround(string definitionId)
    {
        return definitionId.StartsWith("tool.") && definitionId != "tool.pot";
    }

    // Ground the model: bottom of its renderer bounds sits on the tile top
    // (plus an optional epsilon for near-flat meshes that would z-fight).
    private static void GroundVisual(GameObject instance, float lift = 0f)
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

        var offset = instance.transform.position.y - bounds.min.y + lift;
        instance.transform.localPosition += new Vector3(0f, offset, 0f);
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
