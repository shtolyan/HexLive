using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Wearing;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace HexLive.UnityPresentation.AxeChopTest
{
    /// <summary>
    /// Dev tool (Play mode): Jana stands and chops the AI stone axe on a loop with
    /// a tree in front. The axe is spawned exactly like the game does — the
    /// Resources prefab (HexLive/Objects/tool.axe_stone) parented to her right hand
    /// — so this scene is where you tune WHERE it sits in the hand: nudge
    /// <see cref="axeLocalPosition"/> / <see cref="axeLocalEuler"/> /
    /// <see cref="axeLocalScale"/> live in the Inspector (or with the arrow keys),
    /// watch it swing, then "Save" writes the pose into the tool's GearConfig
    /// (entry tool.axe_stone) — the same asset NpcActorView.SetHandProp reads at
    /// runtime, so the tuning carries into the real game.
    ///
    /// Toggle <see cref="chopping"/> off to freeze her in idle (still hand) for
    /// precise tuning, on to preview the swing.
    /// </summary>
    public sealed class AxeChopTestBootstrap : MonoBehaviour
    {
        [Tooltip("Какой инструмент тюним/спавним: tool.axe_stone, tool.knife, tool.pickaxe_stone…")]
        public string toolId = "tool.axe_stone";
        private static readonly int ChoppingParam = Animator.StringToHash("Chopping");
        private static readonly int AttackParam = Animator.StringToHash("Attack");

        [Header("Хват топора (rHand-local) — двигай AxeProp/поля, потом Save")]
        public Vector3 axeLocalPosition = new Vector3(0.064f, -0.03f, 0.012f);
        public Vector3 axeLocalEuler = new Vector3(207.667f, -82.4849f, -92.452f);
        public Vector3 axeLocalScale = Vector3.one;

        [Header("Превью")]
        [Tooltip("Вкл — Яна машет клипом рубки; выкл — стоит в idle (рука неподвижна, удобно точить хват).")]
        public bool chopping = true;

        [Header("Шаг стрелок")]
        public float nudgeStep = 0.005f;

        [Header("Удар в точку (Final IK)")]
        [Tooltip("Вкл — правая рука через FBBIK доводит КОНЧИК лезвия в точку StrikeTarget на фазе удара.")]
        public bool aimStrike = true;
        [Tooltip("Нормализованное время клипа рубки (0..1), КОГДА лезвие приходит в точку (пик кривой). Крути под клип, глядя на normTime в панели.")]
        [Range(0f, 1f)] public float strikeCenter = 0.5f;
        [Tooltip("Ширина окна удара по времени клипа (0..1). Внутри окна вес идёт по кривой, вне — 0.")]
        [Range(0.02f, 1f)] public float strikeWidth = 0.40f;
        [Tooltip("Кривая веса IK ВНУТРИ окна: X=0 начало окна, 0.5=момент удара, 1=конец. Форма = как быстро рука доводится в точку (0→1) и возвращается (1→0).")]
        public AnimationCurve strikeCurve = new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0f));
        [Tooltip("Максимальный вес доводки руки в пике удара.")]
        [Range(0f, 1f)] public float maxHandWeight = 1f;
        [Tooltip("Насколько корпус смещается в сторону удара (FBBIK pullBodyHorizontal). 0=только рука, 1=тело качается за рукой.")]
        [Range(0f, 1f)] public float bodyLean = 0.6f;
        [Tooltip("Рука тянется к точке ПОСТОЯННО (наглядная проверка), игнорируя кривую/окно.")]
        public bool continuousAim = false;
        [Tooltip("Довернуть кисть, чтобы лезвие смотрело в точку (0 = только позиция).")]
        [Range(0f, 1f)] public float strikeRotationWeight = 0f;
        [Tooltip("Двуручное копьё: сила наведения корпуса (AimIK) — крутит спину, чтобы наконечник смотрел в StrikeTarget. Хват из анимации сохраняется.")]
        [Range(0f, 1f)] public float aimWeightMax = 1f;
        [Tooltip("Двуручное копьё: приклеить ЛЕВУЮ руку к точке на древке (FBBIK), чтобы она не соскальзывала — копьё прибито к правой руке.")]
        [Range(0f, 1f)] public float spearLeftHandWeight = 1f;
        [Tooltip("Точка хвата левой руки — локальные координаты на копье. Создаётся дочерний объект 'leftHandAnchor', его можно двигать в сцене.")]
        public Vector3 spearLeftGripLocal = new Vector3(0f, 0.566f, 0f);

        [Header("Взгляд на цель (LookAt IK) — работает вместе с Final IK")]
        [Tooltip("Яна смотрит на StrikeTarget (голова/глаза/корпус), пока рубит. Так же, как в игре она смотрит на собак/дерево.")]
        public bool lookAtTarget = true;
        [Range(0f, 1f)] public float lookAtWeight = 1f;
        [Range(0f, 1f)] public float lookHeadWeight = 0.85f;
        [Range(0f, 1f)] public float lookEyesWeight = 0.5f;
        [Range(0f, 1f)] public float lookBodyWeight = 0.25f;
        [Tooltip("Кончик лезвия в локальных координатах топора. Auto = вычислить из меша.")]
        public bool autoBladeTip = true;
        public Vector3 bladeTipLocal;

        private Animator _animator;
        private BodyBones _bodyBones;
        private Transform _hand;
        private GameObject _axe;
        private RootMotion.FinalIK.FullBodyBipedIK _fbbik;
        private RootMotion.FinalIK.LookAtIK _lookAt;
        private RootMotion.FinalIK.AimIK _aimIK; // two-handed (spear): aim the spine so the tip hits the target
        private Transform _leftAnchor;           // grip point on the shaft the left hand is nailed to
        private GameObject _body;
        private Transform _target;
        private float _fit = 1f; // ObjectFit world-size factor; axeLocalScale is the multiplier
        private int _chopStateHash = Animator.StringToHash("Chop");
        private float _lastStrikeWeight;
        private float _lastPhase = -1f;
        private string _status = "";

        private void Awake()
        {
            // Keep animating/tuning while the editor window is unfocused.
            Application.runInBackground = true;
            BuildEnvironment();
            SpawnJana();
            SeedFromConfig();
            SpawnAxe();
            SpawnStrikeTarget();
            SetupStrikeIK();
            SetupLookAt();
            SetupAim();
        }

        private bool TwoHanded => !string.IsNullOrEmpty(toolId) && toolId.Contains("spear");

        // Two-handed weapons (spear) aim by rotating the SPINE via AimIK so the tip
        // points at the target — both hands + weapon move together, keeping the
        // animation's two-handed grip. (One-handed tools use the FBBIK hand reach.)
        private void SetupAim()
        {
            if (!TwoHanded || _body == null || _bodyBones == null || _axe == null) return;

            var spine = new System.Collections.Generic.List<Transform>();
            foreach (var n in new[] { "abdomenLower", "abdomenUpper", "chestLower", "chestUpper" })
            {
                var b = _bodyBones.GetBone(n);
                if (b != null) spine.Add(b);
            }
            if (spine.Count < 2) { Debug.LogWarning("[AxeChopTest] spine bones for AimIK not found"); return; }

            var root = _bodyBones.GetBone("hip") ?? _bodyBones.GetBone("pelvis");
            _aimIK = _body.AddComponent<RootMotion.FinalIK.AimIK>();
            _aimIK.solver.SetChain(spine.ToArray(), root != null ? root : spine[0].parent);
            _aimIK.solver.transform = _axe.transform; // aim the spear
            _aimIK.solver.axis = Vector3.up;          // spear tip = prop local +Y
            _aimIK.solver.poleAxis = Vector3.up;      // keep her upright
            _aimIK.solver.clampWeight = 0.3f;         // don't crank the spine past natural
            _aimIK.solver.IKPositionWeight = 0f;      // driven per-frame by the strike curve
            _aimIK.enabled = true;
            _aimIK.solver.OnPreUpdate += DriveAim;    // set target + weight right before it solves

            // Create a 'leftHandAnchor' on the shaft and NAIL the left hand to it via
            // the FBBIK effector's TARGET (it follows the spear; never re-nulled).
            // Drag this object in the Scene view to tune where the off hand grips.
            var anchor = new GameObject("leftHandAnchor");
            anchor.transform.SetParent(_axe.transform, false);
            anchor.transform.localPosition = spearLeftGripLocal;
            _leftAnchor = anchor.transform;
            if (_fbbik != null && _fbbik.solver != null)
                _fbbik.solver.leftHandEffector.target = _leftAnchor;
        }

        private void SetupLookAt()
        {
            if (_lookAt == null || _target == null) return;
            _lookAt.enabled = true;
            var s = _lookAt.solver;
            s.target = _target;                 // watch the point she's striking
            s.IKPositionWeight = lookAtWeight;
            s.headWeight = lookHeadWeight;
            s.eyesWeight = lookEyesWeight;
            s.bodyWeight = lookBodyWeight;
            s.clampWeight = 0.5f;               // don't crane past a natural limit
        }

        private void OnDestroy()
        {
            if (_fbbik != null && _fbbik.solver != null)
                _fbbik.solver.OnPreUpdate -= DriveStrikeIK;
            if (_aimIK != null && _aimIK.solver != null)
                _aimIK.solver.OnPreUpdate -= DriveAim;
        }

        private void BuildEnvironment()
        {
            var camGo = new GameObject("AxeChopCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.19f, 0.23f);
            cam.fieldOfView = 38f;
            cam.nearClipPlane = 0.03f;
            camGo.transform.position = new Vector3(1.5f, 1.35f, 1.7f);
            camGo.transform.LookAt(new Vector3(0f, 1.05f, 0.3f));

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

        private void SpawnJana()
        {
            var prefab = Resources.Load<GameObject>("HexLive/Actors/Jana");
            if (prefab == null)
            {
                Debug.LogError("[AxeChopTest] Actor prefab 'HexLive/Actors/Jana' not found");
                return;
            }

            var root = new GameObject("Actor Jana");
            _body = Instantiate(prefab, root.transform);
            var body = _body;
            body.name = "Jana";

            _bodyBones = body.GetComponentInChildren<BodyBones>();
            _animator = body.GetComponentInChildren<Animator>();
            if (_animator != null)
            {
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            _fbbik = body.GetComponentInChildren<FullBodyBipedIK>();
            if (_fbbik != null) _fbbik.enabled = false; // enabled in SetupStrikeIK
            // LookAt IK stays ON — pointed at the StrikeTarget in SetupLookAt so she
            // watches what she's hitting (same as in-game gaze at dogs/trees). It
            // runs together with FBBIK; do NOT zero it here.
            _lookAt = body.GetComponentInChildren<LookAtIK>();

            _bodyBones?.Construct(ActorName.Jana);
            DressJana(); // modest shorts + top (dev preview)
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                skin.updateWhenOffscreen = true; // lying/swinging poses leave authored bounds

            _hand = _bodyBones != null ? _bodyBones.GetBone("rHand") : null;
            if (_hand == null)
                Debug.LogError("[AxeChopTest] rHand bone not found on Jana");
        }

        // Put a modest shorts + top on the preview actor (no skin-heavy default).
        private void DressJana()
        {
            EquipWear("HexLive/Wear/Shorts Green/Shorts Green 19751", "shorts");
            EquipWear("HexLive/Wear/TankTop9_20034/TankTop9_20034 Blue", "top");
        }

        private void EquipWear(string resourcePath, string key)
        {
            if (_bodyBones == null) return;
            var prefab = Resources.Load<GameObject>(resourcePath);
            var wear = prefab != null ? prefab.GetComponent<Wear>() : null;
            if (wear == null)
            {
                Debug.LogWarning($"[AxeChopTest] wear not found: {resourcePath}");
                return;
            }

            _bodyBones.Equip(key, wear);
        }

        private void SpawnTree()
        {
            var tree = Resources.Load<GameObject>("HexLive/Objects/tree.big");
            if (tree == null) return;
            var t = Instantiate(tree);
            t.name = "Tree";
            // She faces +Z; drop the tree just in front of her swing.
            t.transform.position = new Vector3(0.15f, 0f, 0.75f);
        }

        private void SeedFromConfig()
        {
            // §gear: the hand pose lives on the tool's own GearConfig now.
            Config.GearTuning.LoadAndApply();
            if (Config.GearLibrary.TryGetHandPose(toolId, false, out var p, out var r, out var s))
            {
                axeLocalPosition = p;
                axeLocalEuler = r.eulerAngles;
                axeLocalScale = s;
                _status = "Seeded from the gear asset.";
            }
            else
            {
                _status = "No authored hand pose — starting from defaults.";
            }
        }

        private void SpawnAxe()
        {
            if (_hand == null) return;
            var prefab = Resources.Load<GameObject>($"HexLive/Objects/{toolId}");
            if (prefab == null)
            {
                Debug.LogError($"[AxeChopTest] Axe prefab 'HexLive/Objects/{toolId}' not found");
                return;
            }

            _axe = Instantiate(prefab, _hand);
            _axe.name = "AxeProp";
            // Same world-size normalization the game uses (ObjectFit), so the
            // preview size == the in-game size. axeLocalScale is the fine MULTIPLIER
            // on top (default 1), matching the gear asset's hand-scale semantics.
            _fit = HexLive.UnityPresentation.ObjectFit.FitScaleFactor(_axe, toolId);
            // Seed the pose ONCE from the fields/config; after that the AxeProp
            // transform is the source of truth (drag it in the Scene view / edit
            // its Transform — it holds through the swing; Save reads it back).
            _axe.transform.localPosition = axeLocalPosition;
            _axe.transform.localRotation = Quaternion.Euler(axeLocalEuler);
            _axe.transform.localScale = axeLocalScale * _fit;
        }

        private void SpawnStrikeTarget()
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = "StrikeTarget";
            s.transform.localScale = Vector3.one * 0.09f;
            // In front of her, near the ground — a tree root / a log lying down.
            s.transform.position = new Vector3(0.2f, 0.06f, 0.62f);
            var col = s.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", new Color(0.92f, 0.20f, 0.20f));
            m.SetFloat("_Smoothness", 0.35f);
            s.GetComponent<MeshRenderer>().sharedMaterial = m;
            _target = s.transform;
        }

        private void SetupStrikeIK()
        {
            if (_fbbik != null)
            {
                _fbbik.enabled = true;
                if (_fbbik.solver != null)
                {
                    _fbbik.solver.IKPositionWeight = 1f; // master weight — the prefab ships it at 0
                    _fbbik.solver.OnPreUpdate += DriveStrikeIK;
                }
            }

            if (autoBladeTip && _axe != null)
            {
                var r = _axe.GetComponentInChildren<Renderer>();
                if (r != null)
                {
                    var b = r.localBounds; // axe-local: handle +Y, blade forward +Z
                    bladeTipLocal = new Vector3(b.center.x, b.center.y - b.extents.y * 0.10f, b.max.z);
                }
            }
        }

        // Runs right before FBBIK solves (via solver.OnPreUpdate). Places the RIGHT
        // hand so the axe's blade tip lands on the target during the strike window,
        // then releases — the animation carries the rest of the swing.
        private void DriveStrikeIK()
        {
            if (_fbbik == null || _fbbik.solver == null) return;
            var solver = _fbbik.solver;
            solver.IKPositionWeight = 1f;                            // master weight (0 on the prefab → nothing)
            solver.pullBodyHorizontal = TwoHanded ? 0f : bodyLean;  // spear aims via AimIK, not body pull

            // Two-handed (spear): pin the LEFT hand to a grip point on the shaft so
            // it doesn't slip — the spear is parented to the right hand and swung by
            // the animation + AimIK, so the off hand needs IK to follow the shaft.
            // Left hand is nailed to the shaft anchor (target set once in SetupAim) —
            // here we ONLY drive the weight. Do NOT re-null the target (that was the
            // slip bug: nulling target every frame dropped the grip).
            solver.leftHandEffector.positionWeight = (TwoHanded && _leftAnchor != null) ? spearLeftHandWeight : 0f;

            var eff = solver.rightHandEffector;
            var phase = GetChopPhase();
            _lastPhase = phase;

            // Two-handed weapons (spear) aim via AimIK (spine) — NOT the hand — so
            // skip the right-hand reach entirely; it would break the grip.
            var w = (!TwoHanded && aimStrike && _axe != null && _hand != null && _target != null)
                ? CurrentStrikeWeight01() * maxHandWeight
                : 0f;

            _lastStrikeWeight = w;
            if (w <= 0.001f)
            {
                eff.positionWeight = 0f;
                eff.rotationWeight = 0f;
                return;
            }

            // Blade rigidly follows the hand, so the world offset (tip - hand) is
            // constant for the current pose; placing the hand at target - offset
            // lands the blade tip on the target (position-only IK).
            var handW = _hand.position;
            var tipW = _axe.transform.TransformPoint(bladeTipLocal);
            eff.target = null; // use our explicit position, not any authored target
            eff.position = _target.position - (tipW - handW);
            eff.positionWeight = w;
            eff.rotationWeight = 0f; // v1: position only; blade-orientation aiming is a follow-up
        }

        // Drive the tool-appropriate action clip: axe/pickaxe loop the Chop swing;
        // the spear re-fires the one-shot Attack (Bayonet Stab); other tools idle.
        private void DriveActionAnim()
        {
            if (_animator == null) return;
            var chopTool = toolId == "tool.axe_stone" || toolId == "tool.pickaxe_stone";
            var spear = toolId.Contains("spear");
            _animator.SetBool(ChoppingParam, chopping && chopTool);
            if (chopping && spear)
            {
                var st = _animator.GetCurrentAnimatorStateInfo(0);
                if (st.IsName("Idle")) _animator.SetTrigger(AttackParam); // loop the stab
            }
        }

        // IK strike phase from whichever action state is playing (Chop or Attack).
        private float GetChopPhase()
        {
            if (_animator == null) return -1f;
            var st = _animator.GetCurrentAnimatorStateInfo(0);
            if (st.IsName("Chop") || st.IsName("Attack")) return Mathf.Repeat(st.normalizedTime, 1f);
            return -1f;
        }

        // Shared 0..1 strike weight from the curve over the strike window (both the
        // FBBIK hand reach and the spear AimIK use it).
        private float CurrentStrikeWeight01()
        {
            if (!aimStrike) return 0f;
            if (continuousAim) return 1f;
            var phase = GetChopPhase();
            if (phase < 0f || strikeWidth <= 1e-4f) return 0f;
            var d = Mathf.Repeat(phase - (strikeCenter - strikeWidth * 0.5f), 1f);
            return d <= strikeWidth ? Mathf.Clamp01(strikeCurve.Evaluate(d / strikeWidth)) : 0f;
        }

        // Spear: rotate the spine (AimIK) so the tip points at the target, weighted
        // by the strike curve — both hands + spear move together (grip preserved).
        private void DriveAim()
        {
            if (_aimIK == null || _aimIK.solver == null || _target == null) return;
            _aimIK.solver.target = _target;
            _aimIK.solver.IKPositionWeight = CurrentStrikeWeight01() * aimWeightMax;
        }

        private void OnDrawGizmos()
        {
            if (_axe != null)
            {
                var tip = _axe.transform.TransformPoint(bladeTipLocal);
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(tip, 0.02f);
                if (_target != null)
                {
                    Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
                    Gizmos.DrawLine(tip, _target.position);
                }
            }
        }

        private void LateUpdate()
        {
            DriveActionAnim();
            // Keep the gaze live (target may move) alongside the FBBIK strike.
            if (_lookAt != null && _lookAt.solver != null)
                _lookAt.solver.IKPositionWeight = lookAtTarget ? lookAtWeight : 0f;
            HandleNudge();

            // Mirror the (freely user-editable) axe transform INTO the fields for
            // the on-screen readout and Save — we do NOT write it back, so dragging
            // the AxeProp in the Scene view sticks instead of snapping.
            if (_axe != null)
            {
                axeLocalPosition = _axe.transform.localPosition;
                axeLocalEuler = _axe.transform.localEulerAngles;
                axeLocalScale = _fit > 0.0001f ? _axe.transform.localScale / _fit : _axe.transform.localScale;
            }
        }

        private void HandleNudge()
        {
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return;
            if (k.spaceKey.wasPressedThisFrame) chopping = !chopping;
            if (_axe == null) return;
            var step = nudgeStep * (k.leftShiftKey.isPressed ? 0.2f : 1f);
            var p = _axe.transform.localPosition;
            if (k.leftArrowKey.wasPressedThisFrame) p.x -= step;
            if (k.rightArrowKey.wasPressedThisFrame) p.x += step;
            if (k.upArrowKey.wasPressedThisFrame) p.y += step;
            if (k.downArrowKey.wasPressedThisFrame) p.y -= step;
            if (k.pageUpKey.wasPressedThisFrame) p.z += step;
            if (k.pageDownKey.wasPressedThisFrame) p.z -= step;
            _axe.transform.localPosition = p;
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 340, 220), GUI.skin.box);
            GUILayout.Label("<b>Axe attach — tool.axe_stone</b>");
            GUILayout.Label($"pos   {axeLocalPosition}");
            GUILayout.Label($"euler {axeLocalEuler}");
            GUILayout.Label($"scale {axeLocalScale}");
            GUILayout.Space(4);
            GUILayout.Label("Стрелки=XY, PgUp/PgDn=Z, Shift=точнее, Space=свинг вкл/выкл");
            chopping = GUILayout.Toggle(chopping, "Chop animation");
#if UNITY_EDITOR
            if (GUILayout.Button("Save → GearConfig")) SaveToConfig();
#endif
            GUILayout.Space(6);
            aimStrike = GUILayout.Toggle(aimStrike, "Aim strike at StrikeTarget (Final IK)");
            continuousAim = GUILayout.Toggle(continuousAim, "Continuous aim (рука всегда тянется к шару)");
            GUILayout.Label($"clip phase {(_lastPhase < 0f ? 0f : _lastPhase):F2}  →  IK weight {_lastStrikeWeight:F2}");
            GUILayout.Label($"strike center {strikeCenter:F2}  width {strikeWidth:F2}  bodyLean {bodyLean:F2}");
            GUILayout.Label("Кривую веса удара правь в инспекторе (strikeCurve). Тащи <b>StrikeTarget</b> в Scene view.");
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            GUILayout.EndArea();
        }

#if UNITY_EDITOR
        [ContextMenu("Save Axe Attach → GearConfig")]
        private void SaveToConfig()
        {
            // §gear: write straight onto the tool's own asset — one card per
            // item holds EVERYTHING, hand pose included.
            Config.GearTuning.LoadAndApply();
            var cfg = Config.GearLibrary.ConfigFor(toolId);
            if (cfg == null)
            {
                _status = $"No GearConfig asset for '{toolId}' — create one under Resources/HexLive/Gear first.";
                Debug.LogError($"[AxeChopTest] {_status}");
                return;
            }

            // Read the axe's CURRENT transform (the source of truth), not stale fields.
            var pos = _axe != null ? _axe.transform.localPosition : axeLocalPosition;
            var euler = _axe != null ? _axe.transform.localEulerAngles : axeLocalEuler;
            // Save the MULTIPLIER (strip the ObjectFit factor) — the game re-applies fit.
            var scale = _axe != null && _fit > 0.0001f ? _axe.transform.localScale / _fit : axeLocalScale;

            cfg.handPoseAuthored = true;
            cfg.handLocalPosition = pos;
            cfg.handLocalEuler = euler;
            cfg.handLocalScale = scale;

            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();
            _status = $"Saved to {cfg.name}.asset: pos={pos} euler={euler} scale={scale}";
            Debug.Log($"[AxeChopTest] {_status}");
        }
#endif
    }
}
