using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.WardrobeTest
{

// Wardrobe test scene (dev tool): pick one of the actors, try any wear
// prefab the game ships (Resources/HexLive/Wear/**), watch her run the
// sit -> sleep -> get-up loop, and tune each garment's per-actor fit scale
// (WearConfig.scale) live with the arrow keys. "Save" persists the tuned
// scales back into the wear prefab assets (editor only).
[RequireComponent(typeof(UIDocument))]
public sealed class WardrobeTestBootstrap : MonoBehaviour
{
    // §70: no longer only girls — Kshishtof is the male outsider. A new actor
    // has to be added HERE as well as shipped as a prefab, or the scene simply
    // will not offer him and the omission reads as a broken import.
    private static readonly ActorName[] Actors =
    {
        ActorName.Molly, ActorName.Marta, ActorName.Jana, ActorName.Jolly,
        ActorName.Kshishtof,
    };

    private static readonly int SittingParam = Animator.StringToHash("Sitting");
    private static readonly int LayingParam = Animator.StringToHash("Laying");

    // Palette borrowed from DebugControlsPanel so the tool feels native.
    private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.94f);
    private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
    private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
    private static readonly Color Text = new(0.86f, 0.89f, 0.90f);
    private static readonly Color Accent = new(0.22f, 0.45f, 0.30f);
    private static readonly Color AccentSel = new(0.55f, 0.42f, 0.16f);
    private static readonly Color Muted = new(0.55f, 0.60f, 0.63f);

    private sealed class WearEntry
    {
        public string Key;         // unique id (asset path in editor)
        public string DisplayName; // prefab name
        public string Group;       // Resources sub-folder = sim definition id
        public Wear Asset;         // the PREFAB ASSET's Wear component
        public VisualElement Row;
    }

    // §70: заголовок группы прячется вместе со всеми её строками, иначе после
    // гендерного фильтра остаются висеть подписи без содержимого.
    private readonly Dictionary<string, VisualElement> _groupHeaders = new();

    private readonly List<WearEntry> _entries = new();
    private readonly Dictionary<string, WearEntry> _byKey = new();
    private readonly HashSet<string> _equipped = new();
    private readonly HashSet<string> _dirty = new();
    private string _selectedKey;

    private ActorName _girl = ActorName.Marta;
    private GameObject _actorRoot;
    private BodyBones _bodyBones;
    private Animator _animator;

    private UIDocument _document;
    private readonly Dictionary<ActorName, VisualElement> _girlButtons = new();
    private Label _cycleLabel;
    private Label _scaleTitle;
    private Label _scaleValue;
    private Label _saveLabel;
    private VisualElement _scaleBox;

    // Sit -> sleep(5 s) -> get up loop.
    private enum CyclePhase { Idle, Sit, Lie, GetUp }
    private bool _cycleOn = true;
    private CyclePhase _phase = CyclePhase.Idle;
    private float _phaseTime;
    private const float IdleSeconds = 2f;
    private const float SitSeconds = 3.5f;
    // LieDown clip plays first (~2.5 s), then Sleep holds for the 5 s nap.
    private const float LieSeconds = 7.5f;
    private const float GetUpSeconds = 3.5f;

    // Fit-scale editing.
    private const float ScaleStep = 0.01f;
    private const float ScaleStepFine = 0.001f;
    private const float KeyRepeatDelay = 0.35f;
    private const float KeyRepeatInterval = 0.08f;
    private float _keyHeldTime;
    private float _keyRepeatTimer;

    // Click-to-wound test (§31B.4A step 6): toggle on, click the body — a
    // wound lands on the nearest hurt zone, blood paints the skin AND soaks
    // whatever cloth covers that zone (the same painter path the game runs).
    private static readonly (string Zone, string BoneA, string BoneB)[] HitZones =
    {
        ("Head", "head", ""),
        ("Torso", "abdomenUpper", "chestUpper"),
        ("Pelvis", "hip", "abdomenLower"),
        ("ArmL", "lShldrBend", "lForearmBend"),
        ("ArmR", "rShldrBend", "rForearmBend"),
        ("LegL", "lThighBend", "lShin"),
        ("LegR", "rThighBend", "rShin"),
    };

    private const int MaxHitZones = 7;
    private bool _hitMode;
    private Camera _camera;
    private SkinTexturePainter _skinPainter;
    private readonly List<(string zone, int seed, float heal)> _wounds = new();
    private readonly Dictionary<string, float> _zoneHealth = new();
    private readonly HashSet<string> _noBandages = new();
    private readonly string[] _hitZoneNames = new string[MaxHitZones];
    private readonly float[] _hitZoneStrengths = new float[MaxHitZones];
    private int _woundSeed = 4201;
    private Label _hitLabel;
    private VisualElement _hitButton;
    private float _dirt01;
    private float _tear01;
    private Label _dirtLabel;
    private Label _tearLabel;

    private void Awake()
    {
        // Fit tuning keeps running while the editor window is unfocused.
        Application.runInBackground = true;

        BuildEnvironment();
        CollectWearEntries();
        BuildUi();
        SpawnGirl(_girl);
    }

    private void OnEnable()
    {
        Loc.LanguageChanged += BuildUi;
    }

    private void OnDisable()
    {
        Loc.LanguageChanged -= BuildUi;
    }

    // ---- environment ----

    private void BuildEnvironment()
    {
        var camGo = new GameObject("WardrobeCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 45f;
        cam.nearClipPlane = 0.05f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.19f, 0.23f);
        var orbit = camGo.AddComponent<WardrobeOrbitCamera>();
        orbit.Owner = this;
        _camera = cam;

        var lightGo = new GameObject("Sun");
        var sun = lightGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.96f, 0.88f);
        sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft;
        lightGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);
        // Spec 40.8 v4: droplet glints need env specular even in the fitting
        // room — flat ambient alone leaves smooth pixels with nothing to
        // reflect.
        Environment.ProceduralSkyReflection.Apply();
        Environment.ProceduralSkyReflection.SetDayAmount(1f);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = Vector3.one * 2f;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", new Color(0.36f, 0.42f, 0.34f));
        mat.SetFloat("_Smoothness", 0f);
        ground.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    // ---- wardrobe catalog ----

    private void CollectWearEntries()
    {
        _entries.Clear();
        _byKey.Clear();

        foreach (var prefab in Resources.LoadAll<GameObject>("HexLive/Wear"))
        {
            var wear = prefab.GetComponent<Wear>();
            if (wear == null)
            {
                continue;
            }

            var key = prefab.name;
            var group = "";
#if UNITY_EDITOR
            var path = UnityEditor.AssetDatabase.GetAssetPath(prefab);
            if (!string.IsNullOrEmpty(path))
            {
                key = path;
                var dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                group = dir.Substring(dir.LastIndexOf('/') + 1);
            }
#endif
            var entry = new WearEntry
            {
                Key = key,
                DisplayName = prefab.name,
                Group = group,
                Asset = wear,
            };
            _entries.Add(entry);
            _byKey[key] = entry;
        }

        _entries.Sort((a, b) =>
        {
            var g = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
            return g != 0 ? g : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- actor ----

    private void SpawnGirl(ActorName girl)
    {
        if (_actorRoot != null)
        {
            Destroy(_actorRoot);
        }

        _girl = girl;
        _phase = CyclePhase.Idle;
        _phaseTime = 0f;
        ApplyActorFilter();

        var prefab = Resources.Load<GameObject>($"HexLive/Actors/{girl}");
        if (prefab == null)
        {
            Debug.LogError($"Actor prefab 'HexLive/Actors/{girl}' not found");
            return;
        }

        _actorRoot = new GameObject($"Actor {girl}");
        var body = Instantiate(prefab, _actorRoot.transform);
        body.name = girl.ToString();

        _bodyBones = body.GetComponentInChildren<BodyBones>();
        _animator = body.GetComponentInChildren<Animator>();
        if (_animator != null)
        {
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        // Same dormancy rules as NpcActorView: an unfed FBBIK freezes the pose.
        var fbbik = body.GetComponentInChildren<FullBodyBipedIK>();
        if (fbbik != null)
        {
            fbbik.enabled = false;
        }

        var lookAt = body.GetComponentInChildren<LookAtIK>();
        if (lookAt != null)
        {
            lookAt.solver.IKPositionWeight = 0f;
        }

        _bodyBones?.Construct(girl);

        // Fresh body = clean slate for the wound test.
        _wounds.Clear();
        _zoneHealth.Clear();
        _dirt01 = 0f;
        _tear01 = 0f;
        if (_dirtLabel != null)
        {
            _dirtLabel.text = DirtText();
        }

        if (_tearLabel != null)
        {
            _tearLabel.text = TearText();
        }

        BuildSkinPainter(body);

        // Re-dress the outfit carried over from the previous girl.
        foreach (var key in new List<string>(_equipped))
        {
            if (_byKey.TryGetValue(key, out var entry))
            {
                _bodyBones?.Equip(key, entry.Asset);
            }
        }

        ResyncEquipped();
        RelaxSkinCulling();
        RefreshAllRows();
        RefreshGirlButtons();
        RefreshScalePanel();
    }

    // Lying poses stretch outside the authored skin bounds and get
    // frustum-culled mid-sleep; per-frame bounds keep her visible.
    private void RelaxSkinCulling()
    {
        if (_actorRoot == null)
        {
            return;
        }

        foreach (var skin in _actorRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            skin.updateWhenOffscreen = true;
        }
    }

    // ---- wardrobe actions ----

    private void OnRowClicked(WearEntry entry)
    {
        if (_bodyBones == null)
        {
            return;
        }

        if (_equipped.Contains(entry.Key))
        {
            if (_selectedKey != entry.Key)
            {
                _selectedKey = entry.Key; // first click: pick for scale tuning
            }
            else
            {
                _bodyBones.TakeOff(entry.Key); // second click: undress
                _selectedKey = null;
            }
        }
        else
        {
            _bodyBones.Equip(entry.Key, entry.Asset);
            _selectedKey = entry.Key;
            RelaxSkinCulling();
        }

        ResyncEquipped();
        RefreshAllRows();
        RefreshScalePanel();
    }

    // BodyBones auto-takes-off garments that lose the (layer, slot) conflict,
    // so the local equipped-set is re-read from it after every operation.
    private void ResyncEquipped()
    {
        _equipped.Clear();
        if (_bodyBones == null)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            if (_bodyBones.IsEquipped(entry.Key))
            {
                _equipped.Add(entry.Key);
            }
        }

        if (_selectedKey != null && !_equipped.Contains(_selectedKey))
        {
            _selectedKey = null;
        }
    }

    private void UndressAll()
    {
        _bodyBones?.TakeOffAll();
        _selectedKey = null;
        ResyncEquipped();
        RefreshAllRows();
        RefreshScalePanel();
    }

    // ---- fit scale ----

    private void AdjustScale(float delta)
    {
        if (_selectedKey == null || !_byKey.TryGetValue(_selectedKey, out var entry))
        {
            return;
        }

        var value = Mathf.Clamp(entry.Asset.GetConfigScale(_girl) + delta, 0.5f, 2f);
        value = Mathf.Round(value * 1000f) / 1000f;

        // Write into the prefab ASSET, then re-dress the garment so the body
        // shows exactly what a fresh equip with this scale produces. (Tuning
        // the live instance's bones instead is NOT equivalent: Construct bakes
        // the scale into every stitched bone during re-parenting.)
        entry.Asset.SetConfigScale(_girl, value);
        if (_bodyBones != null && _bodyBones.IsEquipped(entry.Key))
        {
            _bodyBones.TakeOff(entry.Key);
            _bodyBones.Equip(entry.Key, entry.Asset);
            RelaxSkinCulling();
        }

        _dirty.Add(entry.Key);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(entry.Asset);
#endif
        RefreshScalePanel();
    }

    private void SaveDirty()
    {
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.SaveAssets();
        Debug.Log($"Wardrobe: saved fit scales into {_dirty.Count} wear prefab(s)");
        _dirty.Clear();
        RefreshScalePanel();
#else
        Debug.LogWarning("Wardrobe: saving prefabs only works in the editor");
#endif
    }

    // ---- animation cycle ----

    private void Update()
    {
        TickCycle();
        TickScaleKeys();
        TickHitClick();
    }

    // ---- click-to-wound test ----

    private void ToggleHitMode()
    {
        _hitMode = !_hitMode;
        if (_hitLabel != null)
        {
            _hitLabel.text = HitModeText();
        }

        if (_hitButton != null)
        {
            _hitButton.style.backgroundColor = _hitMode ? AccentSel : Raised;
        }
    }

    private string HitModeText()
    {
        return Loc.Get(_hitMode ? "wardrobe.hit_on" : "wardrobe.hit_off");
    }

    private void TickHitClick()
    {
        if (!_hitMode || _bodyBones == null || _camera == null)
        {
            return;
        }

        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
        {
            return;
        }

        var position = mouse.position.ReadValue();
        if (IsPointerOverUi(position))
        {
            return;
        }

        var ray = _camera.ScreenPointToRay(position);
        string bestZone = null;
        var bestDistance = 0.35f; // clicks past the silhouette miss
        var bestPoint = Vector3.zero;
        foreach (var (zone, boneA, boneB) in HitZones)
        {
            var a = _bodyBones.GetBone(boneA);
            if (a == null)
            {
                continue;
            }

            var b = string.IsNullOrEmpty(boneB) ? null : _bodyBones.GetBone(boneB);
            var start = a.position;
            var end = b != null ? b.position : start + Vector3.up * 0.12f;
            var distance = RaySegmentDistance(ray, start, end, out var rayPoint);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestZone = zone;
                bestPoint = rayPoint;
            }
        }

        if (bestZone != null)
        {
            Strike(bestZone, bestPoint);
        }
    }

    private void Strike(string zone, Vector3 hitPoint)
    {
        _wounds.Add((zone, _woundSeed++, 0f));
        var health = _zoneHealth.TryGetValue(zone, out var current) ? current : 1f;
        _zoneHealth[zone] = Mathf.Max(0.05f, health - 0.15f);
        ApplyWounds();

        // Click-accurate cloth blood: the blot lands on the garment point
        // nearest to the actual hit, not the zone's single map anchor (the
        // game path keeps the anchor; this is the test tool being precise).
        if (_bodyBones != null)
        {
            foreach (var painter in _bodyBones.GetComponentsInChildren<GarmentWearPainter>(true))
            {
                painter.PlaceBloodBlotAtWorld(hitPoint, 0.9f);
            }
        }
    }

    private void ApplyWounds()
    {
        _skinPainter?.Sync(_wounds, _noBandages);

        var count = 0;
        foreach (var pair in _zoneHealth)
        {
            if (count >= MaxHitZones)
            {
                break;
            }

            _hitZoneNames[count] = pair.Key;
            _hitZoneStrengths[count] = Mathf.Clamp01(1f - pair.Value);
            count++;
        }

        // Blood level drives the wash/soak state; zone count 0 keeps the
        // anchor-based stains OFF — Strike places click-accurate blots
        // itself, so the two paths don't double-stamp the same hit.
        // NOTE the game couples them: painter dust = dirt − blood, so heavy
        // blood visually suppresses fresh dust (test dirt on a washed body).
        var blood = _wounds.Count == 0 ? 0f : Mathf.Clamp01(0.3f + _wounds.Count * 0.1f);
        _bodyBones?.SetWearGrime(_dirt01, _hitZoneNames, _hitZoneStrengths, 0, blood);
    }

    private void ClearWounds()
    {
        _wounds.Clear();
        _zoneHealth.Clear();
        SpawnGirl(_girl); // painters own their baked stains — a fresh body is the reset
    }

    private static float RaySegmentDistance(Ray ray, Vector3 a, Vector3 b, out Vector3 rayPoint)
    {
        var best = float.MaxValue;
        rayPoint = ray.origin;
        for (var i = 0; i <= 16; i++)
        {
            var point = Vector3.Lerp(a, b, i / 16f);
            var distance = Vector3.Cross(ray.direction, point - ray.origin).magnitude;
            if (distance < best)
            {
                best = distance;
                // Where the click ray passes the limb — approximately the
                // clicked surface point (the segment point sits INSIDE the body).
                rayPoint = ray.origin + ray.direction *
                    Mathf.Max(0f, Vector3.Dot(point - ray.origin, ray.direction));
            }
        }

        return best;
    }

    private void BuildSkinPainter(GameObject body)
    {
        _skinPainter = null;
        foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skin.GetComponentInParent<Wear>() != null)
            {
                continue; // hair or a garment, never skin
            }

            var slots = new List<int>();
            var materials = skin.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && NpcActorView.IsSkinMaterialName(materials[i].name))
                {
                    slots.Add(i);
                }
            }

            if (slots.Count == 0)
            {
                continue;
            }

            _skinPainter = body.AddComponent<SkinTexturePainter>();
            _skinPainter.Construct(skin, slots, _bodyBones,
                _animator != null ? _animator.transform : body.transform, 0, _girl.ToString());
            break; // the first skin renderer is the body — NpcActorView's rule
        }
    }

    private void TickCycle()
    {
        if (_animator == null)
        {
            return;
        }

        if (!_cycleOn)
        {
            _animator.SetBool(SittingParam, false);
            _animator.SetBool(LayingParam, false);
            _phase = CyclePhase.Idle;
            _phaseTime = 0f;
            return;
        }

        _phaseTime += Time.deltaTime;
        var duration = _phase switch
        {
            CyclePhase.Idle => IdleSeconds,
            CyclePhase.Sit => SitSeconds,
            CyclePhase.Lie => LieSeconds,
            _ => GetUpSeconds,
        };

        if (_phaseTime < duration)
        {
            return;
        }

        _phaseTime = 0f;
        _phase = _phase switch
        {
            CyclePhase.Idle => CyclePhase.Sit,
            CyclePhase.Sit => CyclePhase.Lie,
            CyclePhase.Lie => CyclePhase.GetUp,
            _ => CyclePhase.Idle,
        };

        _animator.SetBool(SittingParam, _phase == CyclePhase.Sit);
        _animator.SetBool(LayingParam, _phase == CyclePhase.Lie);
    }

    private void TickScaleKeys()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        var left = keyboard.leftArrowKey.isPressed;
        var right = keyboard.rightArrowKey.isPressed;
        if (!left && !right)
        {
            _keyHeldTime = 0f;
            return;
        }

        var fine = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
        var step = (fine ? ScaleStepFine : ScaleStep) * (right ? 1f : -1f);

        var fire = _keyHeldTime == 0f; // first press fires immediately
        if (fire)
        {
            _keyRepeatTimer = 0f;
        }

        _keyHeldTime += Time.deltaTime;
        if (_keyHeldTime > KeyRepeatDelay)
        {
            _keyRepeatTimer += Time.deltaTime;
            if (_keyRepeatTimer >= KeyRepeatInterval)
            {
                _keyRepeatTimer = 0f;
                fire = true;
            }
        }

        if (fire)
        {
            AdjustScale(step);
        }
    }

    // ---- ui ----

    private void BuildUi()
    {
        _document = GetComponent<UIDocument>();
        var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        if (baseSettings != null)
        {
            var settings = Instantiate(baseSettings);
            settings.name = "WardrobePanelSettings";
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.match = 1f;
            settings.sortingOrder = 100;
            _document.panelSettings = settings;
        }

        var root = _document.rootVisualElement;
        root.Clear();
        root.style.flexGrow = 1f;
        root.pickingMode = PickingMode.Ignore;

        BuildLeftPanel(root);
        BuildWardrobePanel(root);
        BuildScalePanel(root);
    }

    private void BuildLeftPanel(VisualElement root)
    {
        var box = MakePanel();
        box.style.left = 14f;
        box.style.top = 14f;
        box.style.width = 190f;
        root.Add(box);

        _girlButtons.Clear();

        box.Add(MakeTitle(Loc.Get("wardrobe.title")));

        var girlsTitle = MakeTitle(Loc.Get("wardrobe.girl"));
        girlsTitle.style.marginTop = 6f;
        box.Add(girlsTitle);

        foreach (var girl in Actors)
        {
            var captured = girl;
            var button = MakeButton(girl.ToString(), Raised, () => SpawnGirl(captured));
            _girlButtons[girl] = button;
            box.Add(button);
        }

        var animTitle = MakeTitle(Loc.Get("wardrobe.animation"));
        animTitle.style.marginTop = 8f;
        box.Add(animTitle);

        var cycleButton = MakeButton(CycleText(), Raised, ToggleCycle);
        _cycleLabel = (Label)cycleButton[0];
        box.Add(cycleButton);

        var hint = new Label(Loc.Get("wardrobe.anim_hint"));
        hint.style.color = Muted;
        hint.style.fontSize = 10;
        hint.style.marginBottom = 8f;
        box.Add(hint);

        box.Add(MakeButton(Loc.Get("wardrobe.undress_all"), Raised, UndressAll));

        var testTitle = MakeTitle(Loc.Get("wardrobe.test_title"));
        testTitle.style.marginTop = 8f;
        box.Add(testTitle);

        _hitButton = MakeButton(HitModeText(), _hitMode ? AccentSel : Raised, ToggleHitMode);
        _hitLabel = (Label)_hitButton[0];
        box.Add(_hitButton);

        var hitHint = new Label(Loc.Get("wardrobe.hit_hint"));
        hitHint.style.color = Muted;
        hitHint.style.fontSize = 10;
        hitHint.style.whiteSpace = WhiteSpace.Normal;
        hitHint.style.marginBottom = 5f;
        box.Add(hitHint);

        var dirtButton = MakeButton(DirtText(), Raised, CycleDirt);
        _dirtLabel = (Label)dirtButton[0];
        box.Add(dirtButton);

        var tearButton = MakeButton(TearText(), Raised, CycleTear);
        _tearLabel = (Label)tearButton[0];
        box.Add(tearButton);

        box.Add(MakeButton(Loc.Get("wardrobe.hit_clear"), Raised, ClearWounds));

        RefreshGirlButtons();
    }

    private string DirtText() => string.Format(Loc.Get("wardrobe.dirt_btn"), Mathf.RoundToInt(_dirt01 * 100f));

    private string TearText() => string.Format(Loc.Get("wardrobe.tear_btn"), Mathf.RoundToInt(_tear01 * 100f));

    private void CycleDirt()
    {
        _dirt01 = _dirt01 >= 0.99f ? 0f : Mathf.Min(1f, _dirt01 + 0.25f);
        if (_dirtLabel != null)
        {
            _dirtLabel.text = DirtText();
        }

        ApplyWounds();
    }

    private void CycleTear()
    {
        _tear01 = _tear01 >= 0.99f ? 0f : Mathf.Min(1f, _tear01 + 0.25f);
        if (_tearLabel != null)
        {
            _tearLabel.text = TearText();
        }

        // Erosion maps durability -> tear inside Wear (TearBiteDurability curve).
        _bodyBones?.SetWearErosion(1f - _tear01);
    }

    private void ToggleCycle()
    {
        _cycleOn = !_cycleOn;
        if (_cycleLabel != null)
        {
            _cycleLabel.text = CycleText();
        }
    }

    private string CycleText()
    {
        return Loc.Get(_cycleOn ? "wardrobe.cycle_on" : "wardrobe.cycle_off");
    }

    private void BuildWardrobePanel(VisualElement root)
    {
        var box = MakePanel();
        box.style.right = 14f;
        box.style.top = 14f;
        box.style.bottom = 14f;
        box.style.width = 260f;
        root.Add(box);

        box.Add(MakeTitle(Loc.Get("wardrobe.clothes")));

        var hint = new Label(Loc.Get("wardrobe.click_hint"));
        hint.style.color = Muted;
        hint.style.fontSize = 10;
        hint.style.marginBottom = 6f;
        box.Add(hint);

        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.style.flexGrow = 1f;
        box.Add(scroll);

        string group = null;
        foreach (var entry in _entries)
        {
            if (entry.Group != group)
            {
                group = entry.Group;
                var header = new Label(group);
                header.style.color = Muted;
                header.style.fontSize = 10;
                header.style.unityFontStyleAndWeight = FontStyle.Bold;
                header.style.marginTop = 6f;
                header.style.marginBottom = 2f;
                scroll.Add(header);
                _groupHeaders[group] = header;
            }

            var captured = entry;
            var row = MakeButton(entry.DisplayName, Raised, () => OnRowClicked(captured));
            row.style.height = 24f;
            row.style.marginBottom = 3f;
            ((Label)row[0]).style.fontSize = 11;
            entry.Row = row;
            scroll.Add(row);
        }

        ApplyActorFilter();
    }

    // §70: показываем только ту одежду, что скроена под ТЕЛО выбранного актёра.
    // Женская вещь на мужском теле рисуется искорёженным мешем (фит всегда
    // пофигурный), так что это не косметика списка, а защита от заведомо
    // неверного показа.
    private void ApplyActorFilter()
    {
        var sex = ActorSex.Of(_girl);
        var groupHasVisible = new Dictionary<string, bool>();

        foreach (var entry in _entries)
        {
            var fits = entry.Asset != null && entry.Asset.Gender == sex;
            if (entry.Row != null)
            {
                entry.Row.style.display = fits ? DisplayStyle.Flex : DisplayStyle.None;
            }

            groupHasVisible.TryGetValue(entry.Group, out var any);
            groupHasVisible[entry.Group] = any || fits;
        }

        foreach (var pair in _groupHeaders)
        {
            groupHasVisible.TryGetValue(pair.Key, out var any);
            pair.Value.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    private void BuildScalePanel(VisualElement root)
    {
        _scaleBox = MakePanel();
        _scaleBox.style.left = 14f;
        _scaleBox.style.bottom = 14f;
        _scaleBox.style.width = 320f;
        root.Add(_scaleBox);

        _scaleTitle = MakeTitle(Loc.Get("wardrobe.scale_none"));
        _scaleBox.Add(_scaleTitle);

        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.marginTop = 4f;
        row.style.marginBottom = 4f;
        _scaleBox.Add(row);

        var minus = MakeButton("◀", Raised, () => AdjustScale(-ScaleStep));
        minus.style.width = 44f;
        minus.style.justifyContent = Justify.Center;
        row.Add(minus);

        _scaleValue = new Label("—");
        _scaleValue.style.color = Text;
        _scaleValue.style.fontSize = 16;
        _scaleValue.style.unityFontStyleAndWeight = FontStyle.Bold;
        _scaleValue.style.unityTextAlign = TextAnchor.MiddleCenter;
        _scaleValue.style.flexGrow = 1f;
        row.Add(_scaleValue);

        var plus = MakeButton("▶", Raised, () => AdjustScale(+ScaleStep));
        plus.style.width = 44f;
        plus.style.justifyContent = Justify.Center;
        row.Add(plus);

        var hint = new Label(Loc.Get("wardrobe.scale_hint"));
        hint.style.color = Muted;
        hint.style.fontSize = 10;
        hint.style.marginBottom = 6f;
        _scaleBox.Add(hint);

        var save = MakeButton(Loc.Get("wardrobe.save_prefabs"), Accent, SaveDirty);
        _saveLabel = (Label)save[0];
        _scaleBox.Add(save);

        RefreshScalePanel();
    }

    // ---- ui refresh ----

    private void RefreshGirlButtons()
    {
        foreach (var pair in _girlButtons)
        {
            pair.Value.style.backgroundColor = pair.Key == _girl ? Accent : Raised;
        }
    }

    private void RefreshAllRows()
    {
        foreach (var entry in _entries)
        {
            if (entry.Row == null)
            {
                continue;
            }

            var equipped = _equipped.Contains(entry.Key);
            var selected = entry.Key == _selectedKey;
            entry.Row.style.backgroundColor = selected ? AccentSel : equipped ? Accent : Raised;
        }
    }

    private void RefreshScalePanel()
    {
        if (_scaleTitle == null)
        {
            return;
        }

        if (_selectedKey != null && _byKey.TryGetValue(_selectedKey, out var entry))
        {
            _scaleTitle.text = string.Format(Loc.Get("wardrobe.scale_selected"), entry.DisplayName, _girl);
            _scaleValue.text = entry.Asset.GetConfigScale(_girl).ToString("0.000");
        }
        else
        {
            _scaleTitle.text = Loc.Get("wardrobe.scale_none");
            _scaleValue.text = "—";
        }

        if (_saveLabel != null)
        {
            _saveLabel.text = _dirty.Count > 0
                ? string.Format(Loc.Get("wardrobe.save_prefabs_count"), _dirty.Count)
                : Loc.Get("wardrobe.save_prefabs");
        }

        RefreshAllRows();
    }

    // ---- ui primitives (DebugControlsPanel conventions) ----

    private static VisualElement MakePanel()
    {
        var box = new VisualElement();
        box.style.position = Position.Absolute;
        box.style.flexDirection = FlexDirection.Column;
        box.style.backgroundColor = Panel;
        SetBorder(box, Stroke);
        SetRadius(box, 12f);
        box.style.paddingLeft = 8f;
        box.style.paddingRight = 8f;
        box.style.paddingTop = 8f;
        box.style.paddingBottom = 8f;
        return box;
    }

    private static Label MakeTitle(string text)
    {
        var title = new Label(text);
        title.style.color = new Color(0.604f, 0.651f, 0.678f);
        title.style.fontSize = 11;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.marginBottom = 2f;
        return title;
    }

    private static VisualElement MakeButton(string text, Color background, Action onClick)
    {
        var b = new VisualElement();
        b.style.flexDirection = FlexDirection.Row;
        b.style.alignItems = Align.Center;
        b.style.height = 30f;
        b.style.marginBottom = 5f;
        b.style.paddingLeft = 10f;
        b.style.paddingRight = 10f;
        b.style.backgroundColor = background;
        SetRadius(b, 8f);

        var label = new Label(text);
        label.style.color = Text;
        label.style.fontSize = 12;
        label.style.unityFontStyleAndWeight = FontStyle.Bold;
        b.Add(label);

        b.RegisterCallback<MouseDownEvent>(_ => onClick());
        return b;
    }

    private static void SetRadius(VisualElement e, float r)
    {
        e.style.borderTopLeftRadius = r;
        e.style.borderTopRightRadius = r;
        e.style.borderBottomLeftRadius = r;
        e.style.borderBottomRightRadius = r;
    }

    private static void SetBorder(VisualElement e, Color c)
    {
        e.style.borderTopWidth = 1f;
        e.style.borderBottomWidth = 1f;
        e.style.borderLeftWidth = 1f;
        e.style.borderRightWidth = 1f;
        e.style.borderTopColor = c;
        e.style.borderBottomColor = c;
        e.style.borderLeftColor = c;
        e.style.borderRightColor = c;
    }

    // The orbit camera asks before consuming scroll/drag so list scrolling
    // over the panels doesn't also zoom the camera.
    public bool IsPointerOverUi(Vector2 screenPosition)
    {
        var panel = _document != null ? _document.rootVisualElement?.panel : null;
        if (panel == null)
        {
            return false;
        }

        var panelPos = RuntimePanelUtils.ScreenToPanel(
            panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));
        return panel.Pick(panelPos) != null;
    }
}

// Simple inspection camera: RMB drag orbits, scroll zooms, MMB drag pans.
public sealed class WardrobeOrbitCamera : MonoBehaviour
{
    public WardrobeTestBootstrap Owner;

    private Vector3 _focus = new(0f, 0.95f, 0f);
    private float _yaw = 180f;
    private float _pitch = 8f;
    private float _distance = 3.4f;

    private void LateUpdate()
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            var overUi = Owner != null && Owner.IsPointerOverUi(mouse.position.ReadValue());

            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.25f, -15f, 85f);
            }

            if (mouse.middleButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                var rot = Quaternion.Euler(0f, _yaw, 0f);
                _focus += rot * new Vector3(-delta.x, 0f, 0f) * 0.002f * _distance
                          + Vector3.up * (-delta.y * 0.002f * _distance);
                _focus.y = Mathf.Clamp(_focus.y, 0.1f, 2.2f);
            }

            if (!overUi)
            {
                // Scroll magnitude differs wildly per device (mac trackpad ~1,
                // mouse wheel ±120) — clamp before applying.
                var scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -3f, 3f);
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    _distance = Mathf.Clamp(_distance * (1f - scroll * 0.05f), 0.6f, 10f);
                }
            }
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = _focus + rotation * new Vector3(0f, 0f, -_distance);
        transform.rotation = rotation;
    }
}

}
