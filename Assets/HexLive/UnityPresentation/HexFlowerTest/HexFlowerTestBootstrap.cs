using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.TwoPeopleTest;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace HexLive.UnityPresentation.HexFlowerTest
{
    /// <summary>
    /// Dev tool (Play mode, editor only): a hex FLOWER — the centre tile and its
    /// 6 neighbors, built from the REAL game constants (HexSpatialMath.HexRadius,
    /// HexPointLayout r=3 interior / r=4 boundary ring, ElevationStep 0.55 with
    /// game land levels 1..3). Click a node to place the girl exactly like the
    /// game does, click a TILE to cycle its elevation, rotate the girl in
    /// hex-angle steps and play any §127 romance pair. The MALE toggles on with
    /// the pair's saved RomancePoseCatalog offset and plays the male clip from
    /// the SAME PlayableGraph; his nearest grid node is highlighted and
    /// analysed. The whole arrangement (clip + girl node + facing + elevations
    /// + male flag + male-node analysis) is saved as a SET in RomanceSetCatalog
    /// — created, applied, overwritten and deleted from the sets panel.
    /// </summary>
    public sealed class HexFlowerTestBootstrap : MonoBehaviour
    {
        private const string ClipsRoot = "Assets/ImportedActors/AnimLibrary/Sex Animation Clips";
        private const string PoseCatalogAssetPath = "Assets/Resources/HexLive/Romance/RomancePoseCatalog.asset";
        private const string SetCatalogAssetPath = "Assets/Resources/HexLive/Romance/RomanceSetCatalog.asset";
        private const float ElevationStep = 0.55f; // same constant as HexWorldRenderer
        private const int MinElevation = 1;        // game land levels are 1..3
        private const int MaxElevation = 3;

        [Header("Актёры")]
        public string actorName = "Jana";
        public string maleActor = "Kshishtof";
        public string[] girls = { "Jana", "Molly", "Jolly", "Marta" };

        [Header("Высоты цветка при старте (уровни 1..3, как в игре)")]
        [Tooltip("Порядок: центр, (1,0), (1,-1), (0,-1), (-1,0), (-1,1), (0,1). " +
                 "Дефолт даёт и ступеньки (перепад 1), и обрыв (перепад 2).")]
        public int[] startElevations = { 1, 1, 2, 2, 3, 1, 2 };

        [Header("Поворот (гексовые углы)")]
        [Tooltip("Шаг Q/E в градусах; Shift = 15°. 60° = направления на соседние гексы.")]
        public float rotateStep = 60f;

        [Header("Плейбек")]
        [Range(0f, 2f)] public float speed = 1f;
        public bool playing = true;

        private sealed class NodeMarker
        {
            public Float2 simPosition;
            public readonly List<TileCoord> owners = new List<TileCoord>();
            public TileCoord tile;     // anchor: чей шаблон создал узел
            public AxialPoint subAxial;
            public bool boundary;
            public GameObject marker;
            public Material material;
            public Vector3 position;   // текущая мировая позиция (с высотой)
        }

        private sealed class ClipEntry
        {
            public string key;
            public string category; // Loop | Climax
            public AnimationClip female;
            public AnimationClip male;
        }

        private readonly Dictionary<(int, int), NodeMarker> _nodes = new Dictionary<(int, int), NodeMarker>();
        private readonly Dictionary<TileCoord, int> _elevations = new Dictionary<TileCoord, int>();
        private readonly Dictionary<TileCoord, GameObject> _tileViews = new Dictionary<TileCoord, GameObject>();
        private readonly List<ClipEntry> _clips = new List<ClipEntry>();
        private ClipEntry _currentClip;
        private NodeMarker _currentNode;
        private NodeMarker _maleNode;   // ближайший к парню узел (анализ + подсветка)
        private float _simFacingDeg;    // sim-space: 0° = +X, 60° = сосед

        private GameObject _body;
        private Animator _animator;
        private GameObject _maleBody;
        private Animator _maleAnimator;
        private bool _maleEnabled;
        private readonly List<(SkinnedMeshRenderer smr, int index, string name)> _genitalTargets =
            new List<(SkinnedMeshRenderer, int, string)>();

        private RomancePoseCatalog _poseCatalog;
        private RomanceSetCatalog _setCatalog;
        private RomanceSetCatalog.SceneSet _selectedSet;

        private PlayableGraph _graph;
        private AnimationClipPlayable _femalePlayable;
        private AnimationClipPlayable _malePlayable;
        private bool _graphAlive;

        private Camera _camera;
        private float _camYaw = 180f;
        private float _camPitch = 40f;
        private float _camDist = 7f;
        private Vector3 _camPivot = Vector3.zero;

        private Vector2 _menuScroll;
        private Vector2 _setScroll;
        private string _status = "";

        private static readonly TileCoord[] FlowerTiles =
        {
            new TileCoord(0, 0),
            new TileCoord(1, 0), new TileCoord(1, -1), new TileCoord(0, -1),
            new TileCoord(-1, 0), new TileCoord(-1, 1), new TileCoord(0, 1),
        };

        private void Awake()
        {
            Application.runInBackground = true;
            // timeScale переживает Enter Play Mode без domain reload и может
            // остаться нулём от Escape-меню игры — анимации тогда стоят.
            Time.timeScale = 1f;
            BuildEnvironment();
            for (var i = 0; i < FlowerTiles.Length; i++)
            {
                var elev = i < startElevations.Length ? startElevations[i] : MinElevation;
                _elevations[FlowerTiles[i]] = Mathf.Clamp(elev, MinElevation, MaxElevation);
            }
            BuildFlower();
            RefreshHeights();
            LoadCatalogs();
            DiscoverClips();
            SpawnGirl(actorName);

            if (_nodes.TryGetValue(HexPointLayout.GetJunctionKeyPair(new TileCoord(0, 0), new AxialPoint(0, 0)),
                    out var centre))
                PlaceOnNode(centre);
            // 0° = сосед (1,0) через середину ребра. Кратные 60° = перпендикуляр
            // к ребру (руки в «стену»); вершины гекса — на 30°+60°k (Shift=15°).
            // Стартовать с 90° нельзя: у pointy-top гекса это ВЕРШИНА, и все
            // шаги Q/E уходят мимо рёбер на 30°.
            SetFacing(0f);

            var first = _clips.FirstOrDefault();
            if (first != null) SelectClip(first);
            else _status = $"Клипы не найдены в {ClipsRoot} (только редактор).";
        }

        private void OnDestroy()
        {
            DestroyGraph();
        }

        // ---------------------------------------------------------------- world

        private void BuildEnvironment()
        {
            var camGo = new GameObject("HexFlowerCamera");
            _camera = camGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.16f, 0.19f, 0.23f);
            _camera.fieldOfView = 40f;
            _camera.nearClipPlane = 0.03f;
            UpdateCamera();

            var lightGo = new GameObject("Sun");
            var sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -35f, 0f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);
        }

        private void BuildFlower()
        {
            foreach (var tile in FlowerTiles)
            {
                var go = new GameObject($"Tile {tile.Q},{tile.R}");
                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = BuildHexTopMesh(HexSpatialMath.HexRadius, 0f,
                    -(MaxElevation * ElevationStep + SimulationUnityMapper.TileHeight));
                var renderer = go.AddComponent<MeshRenderer>();
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.SetFloat("_Smoothness", 0f);
                renderer.sharedMaterial = mat;
                _tileViews[tile] = go;

                foreach (var template in HexPointLayout.GetInteriorTemplates())
                    AddNode(tile, template, boundary: false);
                foreach (var template in HexPointLayout.GetBoundaryTemplates())
                    AddNode(tile, template, boundary: true);
            }
        }

        private void AddNode(TileCoord tile, JunctionTemplate template, bool boundary)
        {
            var key = HexPointLayout.GetJunctionKeyPair(tile, template.SubAxial);
            if (_nodes.TryGetValue(key, out var existing))
            {
                if (!existing.owners.Contains(tile)) existing.owners.Add(tile); // общий узел ребра
                return;
            }

            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"Node {key.Item1},{key.Item2}";
            marker.transform.localScale = Vector3.one * (boundary ? 0.075f : 0.05f);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetFloat("_Smoothness", 0.1f);
            marker.GetComponent<MeshRenderer>().sharedMaterial = mat;

            var node = new NodeMarker
            {
                simPosition = HexSpatialMath.PointToWorld(tile, template.Offset),
                tile = tile,
                subAxial = template.SubAxial,
                boundary = boundary,
                marker = marker,
                material = mat,
            };
            node.owners.Add(tile);
            _nodes.Add(key, node);
        }

        // Верх земли тайла — та же формула, что в RtsCameraController/LyingPoseTest.
        private float GroundY(TileCoord tile) =>
            SimulationUnityMapper.TileHeight + _elevations[tile] * ElevationStep;

        private float NodeGroundY(NodeMarker node) => node.owners.Max(GroundY);

        private void RefreshHeights()
        {
            foreach (var pair in _tileViews)
            {
                var tile = pair.Key;
                pair.Value.transform.position = SimulationUnityMapper.ToUnityTilePosition(tile, GroundY(tile));
                var elev = _elevations[tile];
                var shade = 0.34f + 0.05f * (elev - 1);
                var mat = pair.Value.GetComponent<MeshRenderer>().sharedMaterial;
                mat.SetColor("_BaseColor", tile.Q == 0 && tile.R == 0
                    ? new Color(0.40f + 0.04f * elev, 0.45f + 0.03f * elev, 0.36f)
                    : new Color(shade, shade + 0.06f, shade - 0.02f));
            }

            foreach (var node in _nodes.Values)
            {
                var y = NodeGroundY(node);
                node.position = new Vector3(node.simPosition.X, y, node.simPosition.Y);
                node.marker.transform.position =
                    node.position + Vector3.up * SimulationUnityMapper.PointMarkerLift;
                node.material.SetColor("_BaseColor", NodeColor(node));
            }

            if (_currentNode != null)
            {
                if (_body != null) _body.transform.position = _currentNode.position;
                _camPivot = _currentNode.position;
            }

            UpdateMale();
        }

        // Игровое правило стыка (WorldStateFactory): перепад >1 — узел заблокирован
        // (обрыв, красный); перепад 1 — climb seam (жёлтый); ровно — оранжевый.
        private Color NodeColor(NodeMarker node)
        {
            if (!node.boundary) return new Color(0.55f, 0.6f, 0.65f);
            var diff = ElevationDiff(node);
            if (diff > 1) return new Color(0.85f, 0.2f, 0.2f);
            if (diff == 1) return new Color(0.95f, 0.85f, 0.2f);
            return new Color(0.95f, 0.55f, 0.15f);
        }

        private int ElevationDiff(NodeMarker node)
        {
            if (node.owners.Count < 2) return 0;
            return node.owners.Max(t => _elevations[t]) - node.owners.Min(t => _elevations[t]);
        }

        private static Mesh BuildHexTopMesh(float radius, float top, float baseY)
        {
            var mesh = new Mesh { name = "HexFlowerTile" };
            var vertices = new List<Vector3> { new Vector3(0f, top, 0f) };
            var tris = new List<int>();
            for (var i = 0; i < 6; i++)
            {
                var angle = Mathf.Deg2Rad * (60f * i - 30f);
                vertices.Add(new Vector3(radius * Mathf.Cos(angle), top, radius * Mathf.Sin(angle)));
            }

            for (var i = 0; i < 6; i++)
            {
                tris.Add(0);
                tris.Add(1 + (i + 1) % 6);
                tris.Add(1 + i);
            }

            for (var i = 0; i < 6; i++)
            {
                var a = vertices[1 + i];
                var b = vertices[1 + (i + 1) % 6];
                var start = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(new Vector3(b.x, baseY, b.z));
                vertices.Add(new Vector3(a.x, baseY, a.z));
                tris.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        // --------------------------------------------------------------- actors

        private void SpawnGirl(string girl)
        {
            if (_body != null)
            {
                DestroyGraph();
                Destroy(_body);
                _body = null;
                _animator = null;
            }

            actorName = girl;
            _body = SpawnActor(girl, out _animator);
            if (_body == null) return;
            if (_currentNode != null) _body.transform.position = _currentNode.position;
            SetFacing(_simFacingDeg);
            if (_currentClip != null) SelectClip(_currentClip); // новый аниматор — новый граф
        }

        private GameObject SpawnActor(string name, out Animator animator)
        {
            animator = null;
            var prefab = Resources.Load<GameObject>($"HexLive/Actors/{name}");
            if (prefab == null)
            {
                Debug.LogError($"[HexFlowerTest] Actor prefab 'HexLive/Actors/{name}' not found");
                return null;
            }

            var body = Instantiate(prefab);
            body.name = name;
            animator = body.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            foreach (var ik in body.GetComponentsInChildren<FullBodyBipedIK>(true)) ik.enabled = false;
            foreach (var ik in body.GetComponentsInChildren<LookAtIK>(true)) ik.enabled = false;

            var bones = body.GetComponentInChildren<Wearing.BodyBones>();
            if (bones != null && Enum.TryParse<Wearing.ActorName>(name, out var parsed))
                bones.Construct(parsed);

            foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                skin.updateWhenOffscreen = true;
            return body;
        }

        private void SetMaleEnabled(bool enabled)
        {
            _maleEnabled = enabled;
            if (enabled && _maleBody == null)
            {
                _maleBody = SpawnActor(maleActor, out _maleAnimator);
                CacheMaleGenitals();
            }

            if (_maleBody != null) _maleBody.SetActive(enabled);
            if (_currentClip != null) SelectClip(_currentClip); // пересобрать граф с/без парня
            UpdateMale();
        }

        // Парень = героиня + §127-офсет пары (RomancePoseCatalog), как в игре.
        private void UpdateMale()
        {
            if (!_maleEnabled || _maleBody == null || _body == null) return;

            var girlRot = _body.transform.rotation;
            var offsetPos = Vector3.zero;
            var offsetEuler = Vector3.zero;
            if (_currentClip != null && _poseCatalog != null &&
                _poseCatalog.TryGet(_currentClip.key, out var entry) && entry.authored)
            {
                offsetPos = entry.malePosition;
                offsetEuler = entry.maleEuler;
            }

            _maleBody.transform.position = _body.transform.position + girlRot * offsetPos;
            _maleBody.transform.rotation = girlRot * Quaternion.Euler(offsetEuler);
            ApplyMaleGenitals();
            HighlightMaleNode();
        }

        // АНАЛИЗ: ближайший к парню узел сетки — подсветка + данные для сета.
        private void HighlightMaleNode()
        {
            if (_maleNode != null && _maleNode != _currentNode)
                _maleNode.marker.transform.localScale =
                    Vector3.one * (_maleNode.boundary ? 0.075f : 0.05f);
            _maleNode = null;
            if (!_maleEnabled || _maleBody == null) return;

            var p = _maleBody.transform.position;
            var best = float.MaxValue;
            foreach (var node in _nodes.Values)
            {
                var d = Vector2.Distance(new Vector2(node.position.x, node.position.z),
                    new Vector2(p.x, p.z));
                if (d < best) { best = d; _maleNode = node; }
            }

            if (_maleNode != null && _maleNode != _currentNode)
                _maleNode.marker.transform.localScale = Vector3.one * 0.10f;
        }

        private float MaleNodeDistance()
        {
            if (_maleNode == null || _maleBody == null) return 0f;
            var p = _maleBody.transform.position;
            return Vector2.Distance(new Vector2(_maleNode.position.x, _maleNode.position.z),
                new Vector2(p.x, p.z));
        }

        private void CacheMaleGenitals()
        {
            _genitalTargets.Clear();
            if (_maleBody == null) return;
            foreach (var smr in _maleBody.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    var name = mesh.GetBlendShapeName(i);
                    if (name.Contains("Genitalia") && name.Contains("Preset"))
                        _genitalTargets.Add((smr, i, name));
                }
            }
        }

        // По-позные веса из §127-каталога; без записи — дефолт Flacid 01 = 82.5.
        private void ApplyMaleGenitals()
        {
            List<RomancePoseCatalog.GenitalShape> knobs = null;
            if (_currentClip != null && _poseCatalog != null &&
                _poseCatalog.TryGet(_currentClip.key, out var entry) &&
                entry.authored && entry.genitalShapes.Count > 0)
                knobs = entry.genitalShapes;

            foreach (var (smr, index, name) in _genitalTargets)
            {
                var weight = knobs == null
                    ? (name.Contains("Flacid Preset 01") ? 82.5f : 0f)
                    : 0f;
                if (knobs != null)
                {
                    foreach (var knob in knobs)
                    {
                        if (!string.IsNullOrEmpty(knob.shape) && name.Contains(knob.shape))
                        {
                            weight = knob.weight;
                            break;
                        }
                    }
                }

                smr.SetBlendShapeWeight(index, weight);
            }
        }

        private void PlaceOnNode(NodeMarker node)
        {
            if (_currentNode != null)
                _currentNode.marker.transform.localScale =
                    Vector3.one * (_currentNode.boundary ? 0.075f : 0.05f);
            _currentNode = node;
            node.marker.transform.localScale = Vector3.one * 0.12f;
            if (_body != null) _body.transform.position = node.position;
            _camPivot = node.position;
            UpdateMale();
        }

        private void SetFacing(float simDegrees)
        {
            _simFacingDeg = Mathf.Repeat(simDegrees, 360f);
            if (_body != null)
                _body.transform.rotation =
                    Quaternion.Euler(0f, SimulationUnityMapper.ToUnityYawDegrees(_simFacingDeg), 0f);
            UpdateMale();
        }

        // ---------------------------------------------------------------- clips

        private void LoadCatalogs()
        {
#if UNITY_EDITOR
            _poseCatalog = AssetDatabase.LoadAssetAtPath<RomancePoseCatalog>(PoseCatalogAssetPath);
            _setCatalog = AssetDatabase.LoadAssetAtPath<RomanceSetCatalog>(SetCatalogAssetPath);
#else
            _poseCatalog = Resources.Load<RomancePoseCatalog>(RomancePoseCatalog.ResourcesPath);
            _setCatalog = Resources.Load<RomanceSetCatalog>(RomanceSetCatalog.ResourcesPath);
#endif
        }

        private void DiscoverClips()
        {
#if UNITY_EDITOR
            var byKey = new Dictionary<string, ClipEntry>();
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipsRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;
                if (path.Contains("Pose")) continue; // §127.5: статичные позы пока не используем

                bool female = clip.name.StartsWith("Female_", StringComparison.Ordinal);
                bool male = clip.name.StartsWith("Male_", StringComparison.Ordinal);
                if (!female && !male) continue;

                var key = clip.name.Substring(female ? 7 : 5);
                if (!byKey.TryGetValue(key, out var entry))
                {
                    entry = new ClipEntry
                    {
                        key = key,
                        category = path.Contains("Climax") ? "Climax" : "Loop",
                    };
                    byKey.Add(key, entry);
                }

                if (female) entry.female = clip;
                else entry.male = clip;
            }

            _clips.AddRange(byKey.Values
                .Where(e => e.female != null) // героиня — якорь; чисто мужских соло не показываем
                .OrderBy(e => e.category, StringComparer.Ordinal)
                .ThenBy(e => e.key, StringComparer.Ordinal));
#endif
        }

        private void SelectClip(ClipEntry entry)
        {
            _currentClip = entry;
            DestroyGraph();
            if (entry == null) return;

            _graph = PlayableGraph.Create("HexFlowerTest");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _graphAlive = true;

            if (entry.female != null && _animator != null)
            {
                var output = AnimationPlayableOutput.Create(_graph, "Girl", _animator);
                _femalePlayable = AnimationClipPlayable.Create(_graph, entry.female);
                output.SetSourcePlayable(_femalePlayable);
            }

            if (_maleEnabled && entry.male != null && _maleAnimator != null)
            {
                var output = AnimationPlayableOutput.Create(_graph, "Male", _maleAnimator);
                _malePlayable = AnimationClipPlayable.Create(_graph, entry.male);
                output.SetSourcePlayable(_malePlayable);
            }

            ApplySpeed();
            _graph.Play();
            UpdateMale();
        }

        private void DestroyGraph()
        {
            if (_graphAlive && _graph.IsValid()) _graph.Destroy();
            _graphAlive = false;
        }

        private void ApplySpeed()
        {
            var s = playing ? speed : 0f;
            if (_femalePlayable.IsValid()) _femalePlayable.SetSpeed(s);
            if (_malePlayable.IsValid()) _malePlayable.SetSpeed(s);
        }

        // Цикл по длине женского клипа (якорь) — пара не может разъехаться.
        private void LoopClips()
        {
            if (!_graphAlive || _currentClip?.female == null || !_femalePlayable.IsValid()) return;
            var len = _currentClip.female.length;
            if (len <= 0.001f || _femalePlayable.GetTime() < len) return;
            var t = _femalePlayable.GetTime() % len;
            _femalePlayable.SetTime(t);
            if (_malePlayable.IsValid()) _malePlayable.SetTime(t);
        }

        // ----------------------------------------------------------------- sets

        private void SaveNewSet()
        {
#if UNITY_EDITOR
            var catalog = _setCatalog != null ? _setCatalog : CreateSetCatalog();
            if (catalog == null || _currentNode == null || _currentClip == null) return;
            _setCatalog = catalog;
            var set = new RomanceSetCatalog.SceneSet
            {
                name = $"{_currentClip.key} #{catalog.sets.Count + 1}",
            };
            WriteSet(set);
            catalog.sets.Add(set);
            _selectedSet = set;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            _status = $"Сет создан: {set.name}";
#endif
        }

        private void OverwriteSet(RomanceSetCatalog.SceneSet set)
        {
#if UNITY_EDITOR
            if (_setCatalog == null || _currentNode == null || _currentClip == null) return;
            WriteSet(set);
            EditorUtility.SetDirty(_setCatalog);
            AssetDatabase.SaveAssets();
            _status = $"Сет перезаписан: {set.name}";
#endif
        }

        private void WriteSet(RomanceSetCatalog.SceneSet set)
        {
            set.clipKey = _currentClip.key;
            set.girlTileQ = _currentNode.tile.Q;
            set.girlTileR = _currentNode.tile.R;
            set.girlSubQ = _currentNode.subAxial.Q;
            set.girlSubR = _currentNode.subAxial.R;
            set.facingDeg = _simFacingDeg;
            set.maleEnabled = _maleEnabled;
            set.elevations = FlowerTiles.Select(t => _elevations[t]).ToArray();
            if (_maleEnabled && _maleNode != null && _maleBody != null && _body != null)
            {
                set.maleTileQ = _maleNode.tile.Q;
                set.maleTileR = _maleNode.tile.R;
                set.maleSubQ = _maleNode.subAxial.Q;
                set.maleSubR = _maleNode.subAxial.R;
                set.maleNodeDistance = MaleNodeDistance();
                set.maleWorldOffset = _maleBody.transform.position - _body.transform.position;
            }
        }

        private void ApplySet(RomanceSetCatalog.SceneSet set)
        {
            _selectedSet = set;
            for (var i = 0; i < FlowerTiles.Length && i < set.elevations.Length; i++)
                _elevations[FlowerTiles[i]] = Mathf.Clamp(set.elevations[i], MinElevation, MaxElevation);
            RefreshHeights();

            var clip = _clips.FirstOrDefault(c => c.key == set.clipKey);
            if (clip != null && clip != _currentClip) SelectClip(clip);

            var key = HexPointLayout.GetJunctionKeyPair(
                new TileCoord(set.girlTileQ, set.girlTileR), new AxialPoint(set.girlSubQ, set.girlSubR));
            if (_nodes.TryGetValue(key, out var node)) PlaceOnNode(node);
            SetFacing(set.facingDeg);
            if (set.maleEnabled != _maleEnabled) SetMaleEnabled(set.maleEnabled);
            _status = $"Сет применён: {set.name}";
        }

        private void DeleteSet(RomanceSetCatalog.SceneSet set)
        {
#if UNITY_EDITOR
            if (_setCatalog == null) return;
            _setCatalog.sets.Remove(set);
            if (_selectedSet == set) _selectedSet = null;
            EditorUtility.SetDirty(_setCatalog);
            AssetDatabase.SaveAssets();
            _status = $"Сет удалён: {set.name}";
#endif
        }

#if UNITY_EDITOR
        private static RomanceSetCatalog CreateSetCatalog()
        {
            var dir = System.IO.Path.GetDirectoryName(SetCatalogAssetPath);
            System.IO.Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
            var catalog = ScriptableObject.CreateInstance<RomanceSetCatalog>();
            AssetDatabase.CreateAsset(catalog, SetCatalogAssetPath);
            return catalog;
        }
#endif

        // ---------------------------------------------------------------- frame

        private void Update()
        {
            LoopClips();
            HandleClick();
            HandleKeys();
            HandleCamera();
        }

        private void HandleClick()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || _camera == null || !mouse.leftButton.wasPressedThisFrame) return;
            var pos = mouse.position.ReadValue();
            // Не воровать клики у IMGUI-панелей (левое меню и правая колонка).
            if (pos.x < 274f || pos.x > Screen.width - 384f) return;

            var ray = _camera.ScreenPointToRay(pos);

            NodeMarker bestNode = null;
            var bestT = float.MaxValue;
            foreach (var node in _nodes.Values)
            {
                var toNode = node.position - ray.origin;
                var t = Vector3.Dot(toNode, ray.direction);
                if (t <= 0f) continue;
                var closest = ray.origin + ray.direction * t;
                var d = Vector3.Distance(closest, node.position);
                if (d < 0.17f && t < bestT) { bestNode = node; bestT = t; }
            }

            if (bestNode != null) { PlaceOnNode(bestNode); return; }

            // Ближайшее по лучу пересечение с ЗЕМЛЁЙ тайла — при разных высотах
            // луч может пройти сквозь плоскости нескольких тайлов.
            TileCoord bestTile = default;
            var found = false;
            var bestHitT = float.MaxValue;
            foreach (var tile in FlowerTiles)
            {
                var y = GroundY(tile);
                if (Mathf.Abs(ray.direction.y) < 1e-5f) continue;
                var t = (y - ray.origin.y) / ray.direction.y;
                if (t <= 0f || t >= bestHitT) continue;
                var hit = ray.origin + ray.direction * t;
                var centre = SimulationUnityMapper.ToUnityTilePosition(tile, y);
                if (InsideHexXZ(hit - centre))
                {
                    bestTile = tile;
                    bestHitT = t;
                    found = true;
                }
            }

            if (found)
            {
                _elevations[bestTile] = _elevations[bestTile] >= MaxElevation ? MinElevation : _elevations[bestTile] + 1;
                RefreshHeights();
            }
        }

        private static bool InsideHexXZ(Vector3 local)
        {
            var apothem = HexSpatialMath.HexRadius * HexSpatialMath.HexApothemFactor;
            for (var i = 0; i < 6; i++)
            {
                var a = Mathf.Deg2Rad * (60f * i);
                var normal = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                if (Vector2.Dot(new Vector2(local.x, local.z), normal) > apothem) return false;
            }

            return true;
        }

        private void HandleKeys()
        {
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return;
            if (k.spaceKey.wasPressedThisFrame) { playing = !playing; ApplySpeed(); }

            var step = k.leftShiftKey.isPressed ? 15f : rotateStep;
            if (k.qKey.wasPressedThisFrame) SetFacing(_simFacingDeg + step);
            if (k.eKey.wasPressedThisFrame) SetFacing(_simFacingDeg - step);

            Vector3 dir = Vector3.zero;
            if (k.upArrowKey.wasPressedThisFrame) dir += Flatten(_camera.transform.forward);
            if (k.downArrowKey.wasPressedThisFrame) dir -= Flatten(_camera.transform.forward);
            if (k.rightArrowKey.wasPressedThisFrame) dir += Flatten(_camera.transform.right);
            if (k.leftArrowKey.wasPressedThisFrame) dir -= Flatten(_camera.transform.right);
            if (dir != Vector3.zero && _currentNode != null) StepToNeighbor(dir.normalized);
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.zero;
        }

        private void StepToNeighbor(Vector3 dir)
        {
            var targetXZ = new Vector2(_currentNode.position.x, _currentNode.position.z)
                           + new Vector2(dir.x, dir.z) * 0.375f; // шаг суб-сетки
            NodeMarker best = null;
            var bestD = 0.3f;
            foreach (var node in _nodes.Values)
            {
                if (node == _currentNode) continue;
                var d = Vector2.Distance(new Vector2(node.position.x, node.position.z), targetXZ);
                if (d < bestD) { best = node; bestD = d; }
            }

            if (best != null) PlaceOnNode(best);
        }

        private void HandleCamera()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || _camera == null) return;
            if (mouse.rightButton.isPressed)
            {
                var d = mouse.delta.ReadValue();
                _camYaw += d.x * 0.25f;
                _camPitch = Mathf.Clamp(_camPitch - d.y * 0.25f, 5f, 85f);
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                _camDist = Mathf.Clamp(_camDist - scroll * 0.003f, 1.5f, 20f);
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            if (_camera == null) return;
            var rot = Quaternion.Euler(_camPitch, _camYaw, 0f);
            _camera.transform.position = _camPivot + rot * (Vector3.back * _camDist);
            _camera.transform.rotation = rot;
        }

        // ------------------------------------------------------------------ GUI

        private void OnGUI()
        {
            DrawClipMenu();
            DrawInfoPanel();
            DrawSetsPanel();
        }

        private void DrawClipMenu()
        {
            GUILayout.BeginArea(new Rect(12, 12, 250, Screen.height - 24), GUI.skin.box);
            GUILayout.Label("<b>Анимации (пары §127)</b>");
            _menuScroll = GUILayout.BeginScrollView(_menuScroll);
            string lastCategory = null;
            foreach (var entry in _clips)
            {
                if (entry.category != lastCategory)
                {
                    lastCategory = entry.category;
                    GUILayout.Space(6);
                    GUILayout.Label($"<b>— {lastCategory} —</b>");
                }

                GUI.enabled = _currentClip != entry;
                var solo = entry.male == null ? "  (solo)" : "";
                if (GUILayout.Button(entry.key + solo)) SelectClip(entry);
                GUI.enabled = true;
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawInfoPanel()
        {
            GUILayout.BeginArea(new Rect(Screen.width - 372, 12, 360, 330), GUI.skin.box);
            GUILayout.Label("<b>HexFlowerTest — цветок из 7 гексов</b>");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Героиня:", GUILayout.Width(64));
            foreach (var girl in girls)
            {
                GUI.enabled = actorName != girl;
                if (GUILayout.Button(girl)) SpawnGirl(girl);
                GUI.enabled = true;
            }

            GUILayout.EndHorizontal();

            var maleToggle = GUILayout.Toggle(_maleEnabled,
                $"Парень ({maleActor}) в этой позе (офсет из §127-каталога)");
            if (maleToggle != _maleEnabled) SetMaleEnabled(maleToggle);
            if (_maleEnabled && _currentClip != null && _currentClip.male == null)
                GUILayout.Label("⚠ У этой анимации нет мужского клипа (solo).");

            if (_currentNode != null)
            {
                var diff = ElevationDiff(_currentNode);
                var kind = !_currentNode.boundary ? "интерьер"
                    : diff > 1 ? "РЕБРО — ОБРЫВ (в игре узел заблокирован)"
                    : diff == 1 ? "РЕБРО — ступенька (climb seam)"
                    : "РЕБРО (общий узел)";
                GUILayout.Label($"Она: тайл ({_currentNode.tile.Q},{_currentNode.tile.R}) " +
                                $"суб ({_currentNode.subAxial.Q},{_currentNode.subAxial.R}) — {kind}");
            }

            if (_maleEnabled && _maleNode != null)
                GUILayout.Label($"Он → узел: тайл ({_maleNode.tile.Q},{_maleNode.tile.R}) " +
                                $"суб ({_maleNode.subAxial.Q},{_maleNode.subAxial.R}), " +
                                $"до узла {MaleNodeDistance():F2} wu");

            GUILayout.Label($"Взгляд (сим): {_simFacingDeg:F0}°  (60°k = в ребро/соседа, 30°+60°k = в вершину)");
            GUILayout.Label("ЛКМ: узел = поставить, тайл = высота 1→2→3. Стрелки = шаг узла.");
            GUILayout.Label("Q/E = поворот 60° (Shift=15°), Space = пауза, ПКМ+колесо = камера.");
            GUILayout.Label("Рёбра: оранж = ровно, жёлтое = ступенька, красное = обрыв.");

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Скорость {speed:F2}", GUILayout.Width(100));
            var newSpeed = GUILayout.HorizontalSlider(speed, 0f, 2f);
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(newSpeed, speed)) { speed = newSpeed; ApplySpeed(); }

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        private void DrawSetsPanel()
        {
            GUILayout.BeginArea(new Rect(Screen.width - 372, 354, 360, Screen.height - 366), GUI.skin.box);
            GUILayout.Label("<b>Сеты (анимация + точка + поворот)</b>");
#if UNITY_EDITOR
            if (GUILayout.Button("💾 Новый сет из текущего состояния")) SaveNewSet();
            if (_selectedSet != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Имя:", GUILayout.Width(40));
                _selectedSet.name = GUILayout.TextField(_selectedSet.name);
                GUILayout.EndHorizontal();
                if (GUILayout.Button($"✎ Перезаписать «{_selectedSet.name}» текущим"))
                    OverwriteSet(_selectedSet);
            }
#endif
            _setScroll = GUILayout.BeginScrollView(_setScroll);
            if (_setCatalog != null)
            {
                RomanceSetCatalog.SceneSet toDelete = null;
                foreach (var set in _setCatalog.sets)
                {
                    GUILayout.BeginHorizontal();
                    var mark = _selectedSet == set ? "▶ " : "   ";
                    if (GUILayout.Button($"{mark}{set.name}")) ApplySet(set);
                    if (GUILayout.Button("✕", GUILayout.Width(26))) toDelete = set;
                    GUILayout.EndHorizontal();
                }

                if (toDelete != null) DeleteSet(toDelete);
            }
            else
            {
                GUILayout.Label("Каталога ещё нет — создастся с первым сетом.");
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
