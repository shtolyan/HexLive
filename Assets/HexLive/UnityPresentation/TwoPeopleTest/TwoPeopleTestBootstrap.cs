using System;
using System.Collections.Generic;
using System.Linq;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace HexLive.UnityPresentation.TwoPeopleTest
{
    /// <summary>
    /// Dev tool (Play mode, editor only): pairs the male/female romance clips from
    /// "Sex Animation Clips" by name (Female_X ↔ Male_X), spawns a woman and a man,
    /// and plays a pair in perfect sync from ONE PlayableGraph. The FEMALE pose is
    /// the anchor and always plays at the pair root untouched; the MALE actor sits
    /// on a MaleRoot child transform whose local position/rotation is the tunable
    /// offset — drag MaleRoot in the Scene view or nudge with the keys, then Save
    /// writes the offset into <see cref="RomancePoseCatalog"/>
    /// (Resources/HexLive/Romance/RomancePoseCatalog.asset). On the next open the
    /// saved offsets — and the last selected pose — are restored.
    /// </summary>
    public sealed class TwoPeopleTestBootstrap : MonoBehaviour
    {
        private const string ClipsRoot = "Assets/ImportedActors/AnimLibrary/Sex Animation Clips";
        private const string CatalogAssetPath = "Assets/Resources/HexLive/Romance/RomancePoseCatalog.asset";

        [Header("Актёры")]
        [Tooltip("Женский актёр (якорь пары): Jana, Molly, Jolly, Marta.")]
        public string femaleActor = "Jana";
        [Tooltip("Мужской актёр (получает офсет).")]
        public string maleActor = "Kshishtof";

        [Header("Офсет мужчины (MaleRoot local) — двигай MaleRoot/поля, потом Save")]
        public Vector3 maleLocalPosition;
        public Vector3 maleLocalEuler;

        [Header("Гениталии мужчины (Dicktator бленд-шейпы) — ползунки и в панели")]
        [Tooltip("Веса пресетов; остальные Preset-шейпы гениталий зануляются. Save пишет их в каталог.")]
        public List<RomancePoseCatalog.GenitalShape> genitalShapes = new List<RomancePoseCatalog.GenitalShape>
        {
            new RomancePoseCatalog.GenitalShape { shape = "Flacid Preset 01", weight = 82.5f },
            new RomancePoseCatalog.GenitalShape { shape = "Flacid Preset 02", weight = 0f },
            new RomancePoseCatalog.GenitalShape { shape = "Flacid Preset 03", weight = 0f },
            new RomancePoseCatalog.GenitalShape { shape = "Flacid Preset 04", weight = 0f },
            new RomancePoseCatalog.GenitalShape { shape = "Erection Preset 01", weight = 0f },
            new RomancePoseCatalog.GenitalShape { shape = "Erection Preset 02", weight = 0f },
        };

        [Header("Плейбек")]
        [Range(0f, 2f)] public float speed = 1f;
        public bool playing = true;

        [Header("Шаг стрелок")]
        public float nudgeStep = 0.01f;
        public float rotateStep = 2f;

        private sealed class PoseEntry
        {
            public string key;       // Standing_Doggy_Pose0
            public string category;  // Pose | Loop | Climax
            public AnimationClip female;
            public AnimationClip male;
        }

        private readonly List<PoseEntry> _entries = new List<PoseEntry>();
        private static readonly string[] Categories = { "Pose", "Loop", "Climax" };

        private RomancePoseCatalog _catalog;
        private PoseEntry _current;

        private Transform _pairRoot;   // female anchor — world origin of the pair
        private Transform _maleRoot;   // child of _pairRoot; local TRS = the offset
        private GameObject _femaleBody;
        private GameObject _maleBody;
        private Animator _femaleAnimator;
        private Animator _maleAnimator;

        private PlayableGraph _graph;
        private AnimationClipPlayable _femalePlayable;
        private AnimationClipPlayable _malePlayable;
        private bool _graphAlive;

        private Camera _camera;
        private float _camYaw = 160f;
        private float _camPitch = 12f;
        private float _camDist = 3.2f;
        private Vector3 _camPivot = new Vector3(0f, 0.8f, 0f);

        private Vector2 _menuScroll;
        private string _status = "";
        // §127: предпросмотр выражений лица (DAZ Cute & Fun) на актрисе.
        private Wearing.FaceExpressionRig _femaleFaceRig;
        private int _faceExpression = -1;   // -1 = нейтральное
        private float _faceIntensity = 1f;
        private readonly List<(SkinnedMeshRenderer smr, int index, string name)> _genitalTargets =
            new List<(SkinnedMeshRenderer, int, string)>();
        private List<RomancePoseCatalog.GenitalShape> _defaultGenitalShapes =
            new List<RomancePoseCatalog.GenitalShape>();

        private void Awake()
        {
            Application.runInBackground = true;
            // timeScale переживает Enter Play Mode без domain reload и может
            // остаться нулём от Escape-меню игры — анимации тогда стоят.
            Time.timeScale = 1f;
            BuildEnvironment();
            LoadCatalog();
            DiscoverClips();
            // Inspector/scene values = the seed for every pose not yet tuned.
            _defaultGenitalShapes = CopyShapes(genitalShapes);

            _pairRoot = new GameObject("PairRoot").transform;
            _maleRoot = new GameObject("MaleRoot").transform;
            _maleRoot.SetParent(_pairRoot, false);

            // Both spawn NUDE on purpose (the raw actor prefabs carry no garments,
            // the girls keep their authored hair) — the pair is tuned skin-to-skin.
            _femaleBody = SpawnActor(femaleActor, _pairRoot, out _femaleAnimator);
            _maleBody = SpawnActor(maleActor, _maleRoot, out _maleAnimator);
            SetupMaleGenitals();
            SetupFemaleFace();

            // Restore the last tuned pose, else start on the first one.
            var startKey = _catalog != null ? _catalog.lastSelectedKey : "";
            var start = _entries.FirstOrDefault(e => e.key == startKey) ?? _entries.FirstOrDefault();
            if (start != null) SelectPose(start);
            else _status = $"Клипы не найдены в {ClipsRoot} (инструмент работает только в редакторе).";
        }

        private void OnDestroy()
        {
            DestroyGraph();
        }

        // ---------------------------------------------------------------- setup

        private void BuildEnvironment()
        {
            var camGo = new GameObject("TwoPeopleCamera");
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
            lightGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = Vector3.one * 2f;
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", new Color(0.36f, 0.42f, 0.34f));
            mat.SetFloat("_Smoothness", 0f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private GameObject SpawnActor(string actorName, Transform parent, out Animator animator)
        {
            animator = null;
            var prefab = Resources.Load<GameObject>($"HexLive/Actors/{actorName}");
            if (prefab == null)
            {
                Debug.LogError($"[TwoPeopleTest] Actor prefab 'HexLive/Actors/{actorName}' not found");
                return null;
            }

            var body = Instantiate(prefab, parent);
            body.name = actorName;

            animator = body.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            // The pose comes 100% from the paired clips — IK would fight it.
            foreach (var ik in body.GetComponentsInChildren<FullBodyBipedIK>(true)) ik.enabled = false;
            foreach (var ik in body.GetComponentsInChildren<LookAtIK>(true)) ik.enabled = false;

            var bones = body.GetComponentInChildren<Wearing.BodyBones>();
            if (bones != null && Enum.TryParse<Wearing.ActorName>(actorName, out var parsed))
                bones.Construct(parsed);

            foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                skin.updateWhenOffscreen = true; // paired poses leave authored bounds

            return body;
        }

        // The Dicktator genital mesh ships active on the Kshishtof prefab but with
        // every preset blendshape at 0. Cache every Genitalia preset shape once;
        // ApplyGenitalShapes then drives them from the genitalShapes knobs every
        // frame (cheap), so the panel sliders and the Inspector both work live.
        private void SetupMaleGenitals()
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

        private void ApplyGenitalShapes()
        {
            foreach (var (smr, index, name) in _genitalTargets)
            {
                var weight = 0f;
                foreach (var knob in genitalShapes)
                {
                    if (!string.IsNullOrEmpty(knob.shape) && name.Contains(knob.shape))
                    {
                        weight = knob.weight;
                        break;
                    }
                }

                smr.SetBlendShapeWeight(index, weight);
            }
        }

        // §127: выражения лица из DAZ-пака (Resources/HexLive/FaceExpressions).
        // Префабы приезжают с застрявшими авторскими выражениями (Jana: frown 42)
        // — зануляем каждый eCTRL*-шейп, чтобы предпросмотр начинался с
        // нейтрального лица; морфы персонажа (не eCTRL) не трогаем.
        private void SetupFemaleFace()
        {
            if (_femaleBody == null) return;
            var skins = _femaleBody.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var skin in skins)
            {
                var mesh = skin != null ? skin.sharedMesh : null;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    if (mesh.GetBlendShapeName(i).Contains("eCTRL"))
                        skin.SetBlendShapeWeight(i, 0f);
                }
            }

            _femaleFaceRig = new Wearing.FaceExpressionRig(
                skins, Wearing.FaceExpressionCatalog.Load());
        }

        // Pair Female_X ↔ Male_X by the name with the gender prefix stripped.
        // Category from the folder: ".../Pose/", ".../Climax/", else Loop.
        private void DiscoverClips()
        {
#if UNITY_EDITOR
            var guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipsRoot });
            var byKey = new Dictionary<string, PoseEntry>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;

                bool female = clip.name.StartsWith("Female_", StringComparison.Ordinal);
                bool male = clip.name.StartsWith("Male_", StringComparison.Ordinal);
                if (!female && !male) continue;

                var key = clip.name.Substring(female ? 7 : 5);
                if (!byKey.TryGetValue(key, out var entry))
                {
                    entry = new PoseEntry
                    {
                        key = key,
                        category = path.Contains("Pose") ? "Pose" : path.Contains("Climax") ? "Climax" : "Loop",
                    };
                    byKey.Add(key, entry);
                }

                if (female) entry.female = clip;
                else entry.male = clip;
            }

            _entries.Clear();
            _entries.AddRange(byKey.Values
                .Where(e => e.category != "Pose") // статичные позы пока не используем
                .OrderBy(e => Array.IndexOf(Categories, e.category))
                .ThenBy(e => e.key, StringComparer.Ordinal));
#endif
        }

        private void LoadCatalog()
        {
#if UNITY_EDITOR
            _catalog = AssetDatabase.LoadAssetAtPath<RomancePoseCatalog>(CatalogAssetPath);
#else
            _catalog = Resources.Load<RomancePoseCatalog>(RomancePoseCatalog.ResourcesPath);
#endif
        }

        private static List<RomancePoseCatalog.GenitalShape> CopyShapes(
            IEnumerable<RomancePoseCatalog.GenitalShape> source) =>
            source.Select(g => new RomancePoseCatalog.GenitalShape { shape = g.shape, weight = g.weight })
                .ToList();

        // ------------------------------------------------------------- playback

        private void SelectPose(PoseEntry entry)
        {
            _current = entry;

            // Seed the male offset from the catalog; an untuned pair starts at zero
            // (paired clips are authored in one shared space, so zero is the best guess).
            if (_catalog != null && _catalog.TryGet(entry.key, out var saved) && saved.authored)
            {
                maleLocalPosition = saved.malePosition;
                maleLocalEuler = saved.maleEuler;
                genitalShapes = saved.genitalShapes.Count > 0
                    ? CopyShapes(saved.genitalShapes)
                    : CopyShapes(_defaultGenitalShapes);
                _status = "Офсет и бленд-шейпы из каталога.";
            }
            else
            {
                // Climax is just the FINISH of the same romance loop — an untuned
                // climax inherits every knob from its loop's saved entry.
                var loopSeed = entry.category == "Climax" ? FindLoopSeed(entry.key) : null;
                if (loopSeed != null)
                {
                    maleLocalPosition = loopSeed.malePosition;
                    maleLocalEuler = loopSeed.maleEuler;
                    genitalShapes = loopSeed.genitalShapes.Count > 0
                        ? CopyShapes(loopSeed.genitalShapes)
                        : CopyShapes(_defaultGenitalShapes);
                    _status = "Климакс: все настройки скопированы из Loop.";
                }
                else
                {
                    maleLocalPosition = Vector3.zero;
                    maleLocalEuler = Vector3.zero;
                    genitalShapes = CopyShapes(_defaultGenitalShapes);
                    _status = "Поза не настроена — офсет в нулях, бленд-шейпы дефолтные.";
                }
            }

            _maleRoot.localPosition = maleLocalPosition;
            _maleRoot.localRotation = Quaternion.Euler(maleLocalEuler);

            if (_femaleBody != null) _femaleBody.SetActive(entry.female != null);
            if (_maleBody != null) _maleBody.SetActive(entry.male != null);

            RebuildGraph();

            if (_catalog != null && _catalog.lastSelectedKey != entry.key)
            {
                _catalog.lastSelectedKey = entry.key;
#if UNITY_EDITOR
                EditorUtility.SetDirty(_catalog);
#endif
            }
        }

        // Standing_Doggy_Climax → the authored entry of Standing_Doggy_Loop0/1…
        private RomancePoseCatalog.Entry FindLoopSeed(string climaxKey)
        {
            if (_catalog == null) return null;
            var cut = climaxKey.LastIndexOf("_Climax", StringComparison.Ordinal);
            if (cut <= 0) return null;
            var loopPrefix = climaxKey.Substring(0, cut) + "_Loop";
            foreach (var e in _catalog.entries)
            {
                if (e.authored && e.key.StartsWith(loopPrefix, StringComparison.Ordinal))
                    return e;
            }

            return null;
        }

        // ONE graph drives both animators — the pair can never drift apart.
        private void RebuildGraph()
        {
            DestroyGraph();
            if (_current == null) return;

            _graph = PlayableGraph.Create("TwoPeopleTest");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _graphAlive = true;

            if (_current.female != null && _femaleAnimator != null)
            {
                var output = AnimationPlayableOutput.Create(_graph, "Female", _femaleAnimator);
                _femalePlayable = AnimationClipPlayable.Create(_graph, _current.female);
                output.SetSourcePlayable(_femalePlayable);
            }

            if (_current.male != null && _maleAnimator != null)
            {
                var output = AnimationPlayableOutput.Create(_graph, "Male", _maleAnimator);
                _malePlayable = AnimationClipPlayable.Create(_graph, _current.male);
                output.SetSourcePlayable(_malePlayable);
            }

            ApplySpeed();
            _graph.Play();
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

        private void RestartSync()
        {
            if (_femalePlayable.IsValid()) _femalePlayable.SetTime(0);
            if (_malePlayable.IsValid()) _malePlayable.SetTime(0);
        }

        // Loop both clips on the FEMALE clip's cycle (the anchor) so a length
        // mismatch cannot slowly desync the pair.
        private void LoopClips()
        {
            if (!_graphAlive || _current == null) return;
            var master = _current.female != null ? _current.female : _current.male;
            if (master == null || master.length <= 0.001f) return;

            var anchor = _current.female != null ? _femalePlayable : _malePlayable;
            if (!anchor.IsValid() || anchor.GetTime() < master.length) return;

            var t = anchor.GetTime() % master.length;
            if (_femalePlayable.IsValid()) _femalePlayable.SetTime(t);
            if (_malePlayable.IsValid()) _malePlayable.SetTime(t);
        }

        // ---------------------------------------------------------------- frame

        private void Update()
        {
            LoopClips();
            HandleNudge();
            HandleCamera();
            ApplyGenitalShapes();
            _femaleFaceRig?.Apply(_faceExpression, _faceIntensity);
        }

        private void LateUpdate()
        {
            // MaleRoot (freely draggable in the Scene view) is the source of truth;
            // mirror it into the fields for the readout and Save — never write back.
            if (_maleRoot != null)
            {
                maleLocalPosition = _maleRoot.localPosition;
                maleLocalEuler = _maleRoot.localEulerAngles;
            }
        }

        private void HandleNudge()
        {
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null || _maleRoot == null) return;
            if (k.spaceKey.wasPressedThisFrame) { playing = !playing; ApplySpeed(); }

            var fine = k.leftShiftKey.isPressed ? 0.2f : 1f;
            var step = nudgeStep * fine;
            var p = _maleRoot.localPosition;
            if (k.leftArrowKey.wasPressedThisFrame) p.x -= step;
            if (k.rightArrowKey.wasPressedThisFrame) p.x += step;
            if (k.upArrowKey.wasPressedThisFrame) p.z += step;
            if (k.downArrowKey.wasPressedThisFrame) p.z -= step;
            if (k.pageUpKey.wasPressedThisFrame) p.y += step;
            if (k.pageDownKey.wasPressedThisFrame) p.y -= step;
            _maleRoot.localPosition = p;

            var yaw = rotateStep * fine;
            if (k.qKey.wasPressedThisFrame) _maleRoot.Rotate(0f, -yaw, 0f, Space.Self);
            if (k.eKey.wasPressedThisFrame) _maleRoot.Rotate(0f, yaw, 0f, Space.Self);
        }

        private void HandleCamera()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || _camera == null) return;

            if (mouse.rightButton.isPressed)
            {
                var d = mouse.delta.ReadValue();
                _camYaw += d.x * 0.25f;
                _camPitch = Mathf.Clamp(_camPitch - d.y * 0.25f, -20f, 80f);
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                _camDist = Mathf.Clamp(_camDist - scroll * 0.002f, 0.8f, 8f);

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
            DrawPoseMenu();
            DrawControlPanel();
        }

        private void DrawPoseMenu()
        {
            GUILayout.BeginArea(new Rect(12, 12, 250, Screen.height - 24), GUI.skin.box);
            GUILayout.Label("<b>Позы (Female ↔ Male)</b>");
            _menuScroll = GUILayout.BeginScrollView(_menuScroll);
            string lastCategory = null;
            foreach (var entry in _entries)
            {
                if (entry.category != lastCategory)
                {
                    lastCategory = entry.category;
                    GUILayout.Space(6);
                    GUILayout.Label($"<b>— {lastCategory} —</b>");
                }

                var authored = _catalog != null && _catalog.TryGet(entry.key, out var e) && e.authored;
                var solo = entry.female == null || entry.male == null;
                var label = (authored ? "● " : "   ") + entry.key + (solo ? "  (solo)" : "");
                var wasSelected = _current == entry;
                GUI.enabled = !wasSelected;
                if (GUILayout.Button(label)) SelectPose(entry);
                GUI.enabled = true;
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawControlPanel()
        {
            var height = 430 + genitalShapes.Count * 24;
            GUILayout.BeginArea(new Rect(Screen.width - 372, 12, 360, height), GUI.skin.box);
            GUILayout.Label($"<b>TwoPeopleTest — {(_current != null ? _current.key : "нет позы")}</b>");
            GUILayout.Label($"male pos   {maleLocalPosition}");
            GUILayout.Label($"male euler {maleLocalEuler}");
            GUILayout.Space(4);
            GUILayout.Label("Стрелки=XZ, PgUp/PgDn=Y, Q/E=поворот, Shift=точнее");
            GUILayout.Label("Space=пауза, ПКМ=камера, колесо=зум. Тащи <b>MaleRoot</b> в Scene view.");
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Скорость {speed:F2}", GUILayout.Width(100));
            var newSpeed = GUILayout.HorizontalSlider(speed, 0f, 2f);
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(newSpeed, speed)) { speed = newSpeed; ApplySpeed(); }

            var newPlaying = GUILayout.Toggle(playing, "Играть анимацию (обе синхронно)");
            if (newPlaying != playing) { playing = newPlaying; ApplySpeed(); }
            if (GUILayout.Button("⟲ Рестарт с нуля (синхрон)")) RestartSync();

            GUILayout.Space(6);
            GUILayout.Label("<b>Гениталии</b> — у КАЖДОЙ позы свои веса (Save пишет в её запись):");
            foreach (var knob in genitalShapes)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{knob.shape} {knob.weight:F1}", GUILayout.Width(190));
                knob.weight = GUILayout.HorizontalSlider(knob.weight, 0f, 100f);
                GUILayout.EndHorizontal();
            }

            if (_femaleFaceRig != null && _femaleFaceRig.Count > 0)
            {
                GUILayout.Space(6);
                var faceName = _faceExpression >= 0
                    ? _femaleFaceRig.NameOf(_faceExpression)
                    : "нейтральное";
                GUILayout.Label($"<b>Выражение лица</b> — {faceName}");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("◀", GUILayout.Width(40)))
                    _faceExpression = _faceExpression <= -1
                        ? _femaleFaceRig.Count - 1 : _faceExpression - 1;
                if (GUILayout.Button("▶", GUILayout.Width(40)))
                    _faceExpression = _faceExpression >= _femaleFaceRig.Count - 1
                        ? -1 : _faceExpression + 1;
                if (GUILayout.Button("Сброс", GUILayout.Width(70)))
                    _faceExpression = -1;
                GUILayout.Label($"сила {_faceIntensity:F2}", GUILayout.Width(70));
                _faceIntensity = GUILayout.HorizontalSlider(_faceIntensity, 0f, 1f);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
#if UNITY_EDITOR
            if (GUILayout.Button("💾 Save → RomancePoseCatalog")) SaveCurrent();
#endif
            if (GUILayout.Button("Сброс офсета (из каталога / в нули)") && _current != null) SelectPose(_current);

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        // ----------------------------------------------------------------- save

#if UNITY_EDITOR
        [ContextMenu("Save Male Offset → RomancePoseCatalog")]
        private void SaveCurrent()
        {
            if (_current == null) return;
            var catalog = _catalog != null ? _catalog : CreateCatalogAsset();
            if (catalog == null) return;
            _catalog = catalog;

            var entry = catalog.GetOrAdd(_current.key);
            entry.malePosition = _maleRoot.localPosition;
            entry.maleEuler = _maleRoot.localEulerAngles;
            entry.authored = true;
            entry.genitalShapes = CopyShapes(genitalShapes); // per-pose, like the offset
            catalog.lastSelectedKey = _current.key;

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            _status = $"Сохранено: {_current.key} pos={entry.malePosition} euler={entry.maleEuler}";
            Debug.Log($"[TwoPeopleTest] {_status}");
        }

        private static RomancePoseCatalog CreateCatalogAsset()
        {
            var dir = System.IO.Path.GetDirectoryName(CatalogAssetPath);
            System.IO.Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
            var catalog = ScriptableObject.CreateInstance<RomancePoseCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogAssetPath);
            return catalog;
        }
#endif
    }
}
