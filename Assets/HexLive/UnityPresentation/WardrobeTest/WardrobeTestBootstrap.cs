using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.WardrobeTest
{

// Wardrobe test scene (dev tool): pick one of the actors, try any wear
// prefab the game ships (Resources/HexLive/Wear/**) from an icon grid with
// underwear/wear/outerwear tabs, optionally run the sit -> sleep -> get-up
// loop (off by default — she stands in idle), and tune each garment's
// per-actor fit scale (WearConfig.scale) live with the arrow keys. "Save" persists the tuned
// scales back into the wear prefab assets (editor only).
[RequireComponent(typeof(UIDocument))]
public sealed class WardrobeTestBootstrap : MonoBehaviour
{
    // §72: no longer only girls — Kshishtof is the male outsider. A new actor
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
        public int Index;          // this prefab's rank inside its Group
        public Wear Asset;         // the PREFAB ASSET's Wear component
        public VisualElement Row;

        // §31B.4E: the items painted on this geometry, prototype first — empty
        // for the (many) garments the catalog knows only one colour of.
        public IReadOnlyList<GarmentDefinition> Variants;

        // Which of them is on the body right now.
        public string VariantId;

        // BodyBones reads the variant's materials out of the key's "<item
        // id>#<index>" head, so the CHOSEN colour has to travel in the key —
        // the panel's own Key is an asset path and means nothing to the
        // catalog. The index keeps two prefabs of one definition apart.
        public string EquipKey => $"{VariantId}#{Index}";
    }

    // Вкладки по слою одежды (VisualWearLayer): бельё → одежда → верхняя,
    // в порядке одевания. Активная вкладка — второй фильтр рядом с гендерным.
    private VisualWearLayer _wearTab = VisualWearLayer.Underwear;
    private readonly Dictionary<VisualWearLayer, VisualElement> _tabButtons = new();

    private readonly List<WearEntry> _entries = new();
    private readonly Dictionary<string, WearEntry> _byKey = new();
    private readonly HashSet<string> _equipped = new();
    private readonly HashSet<string> _dirty = new();
    private string _selectedKey;

    // §31B.4E: the second level of the browser. The clothes grid lists one tile
    // per GEOMETRY; the colours of whichever prototype is selected open here.
    private VisualElement _variantsBox;
    private Label _variantsTitle;
    private ScrollView _variantsStrip;
    private Label _variantsDesc;
    private readonly Dictionary<string, VisualElement> _variantTiles = new();
    private WearEntry _variantsFor;

    // Hair is NOT wardrobe (spec §31B.4B): no slot, no layer, never equipped —
    // one instance swapped through BodyBones.SetHair. It also does not live in
    // Resources, so it gets its own list instead of riding _entries.
    private sealed class HairEntry
    {
        public string DisplayName;
        public Wear Asset;          // null = the "bald" row
        public VisualElement Row;
    }

    private readonly List<HairEntry> _hair = new();
    private HairEntry _selectedHair;
    private Label _hairScaleLabel;
    private Label _hairHeightLabel;
    private VisualElement _hairSaveButton;
    private ScrollView _hairColourStrip;
    private bool _hairDirty;

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
    private readonly Dictionary<VisualWearLayer, VisualElement> _layerButtons = new();
    private VisualElement _hidesHairToggle;
    private VisualElement _noHideRow;
    private Label _hideWearTitle;
    private VisualElement _hideWearRow;
    private TextField _commentField;

    // Заметки об осмотре. Живут рядом с манифестами поставок, а не в префабе:
    // это разговор про вещь, а не её свойство, и читать их будет тот, кто
    // правит конвейер, — по одному файлу, а не по девяноста двум ассетам.
    private const string CommentsPath = "Assets/Editor/WearDrops/_comments.json";
    private readonly Dictionary<string, string> _comments = new();
    private bool _commentsDirty;

    // Sit -> sleep(5 s) -> get up loop. Off by default — the actor just stands
    // in idle until the cycle button turns the loop on.
    private enum CyclePhase { Idle, Sit, Lie, GetUp }
    private bool _cycleOn;
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
#if UNITY_EDITOR
        LoadComments();
#endif
        CollectWearEntries();
        CollectHairEntries();
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

        // Several prefabs can share one definition folder (a dress that is a top
        // plus a skirt), and they must not collide on the equip key.
        var perGroup = new Dictionary<string, int>();

        // Арт вещей уехал из Resources в атомарные owner bundles. Браузер — редакторный,
        // поэтому берёт префабы прямо с диска: ему нужен весь список сразу,
        // а не то, что уже собрано в бандлы.
        foreach (var prefab in LoadWearPrefabsForBrowser())
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
            // §31B.4E: the folder IS the art id, so the wear folders already are
            // one-per-geometry and this list is prototypes only. Guard it anyway
            // — a stray folder named after a VARIANT would otherwise smuggle a
            // second tile for a geometry that is already in the grid.
            if (!string.IsNullOrEmpty(group) && GarmentVariants.ArtIdOf(group) != group)
            {
                continue;
            }

#if UNITY_EDITOR
            // A batch that has already been reviewed is not shown: the pause
            // between batches exists to look at what is new, and every finished
            // drop left in the grid makes that harder. Flip `done` in
            // Assets/Editor/WearDrops/import-plan.json to bring one back.
            if (!string.IsNullOrEmpty(group) && WardrobeDropFilter.Hidden.Contains(group))
            {
                continue;
            }
#endif

            var index = perGroup.TryGetValue(group, out var used) ? used : 0;
            perGroup[group] = index + 1;

            var variants = GarmentVariants.VariantsOf(group);
            var entry = new WearEntry
            {
                Key = key,
                DisplayName = prefab.name,
                Group = group,
                Index = index,
                Asset = wear,
                Variants = variants,
                // No catalog row (a dev-only prefab) still needs an item id for
                // the equip key; the folder name is what the catalog would use.
                VariantId = variants.Count > 0 ? variants[0].id : group,
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

    // Every prefab under ImportedActors/Wear IS a hairstyle — garments live in
    // Resources/HexLive/Wear instead, so the folder alone is the discriminator
    // (verified 2026-08: all 17 prefabs there are hair, all with empty slots).
    // Editor-only: hair is referenced straight off the actor prefabs and is
    // deliberately NOT in Resources, so there is nothing to enumerate at
    // runtime. The panel simply stays empty in a build.
    private void CollectHairEntries()
    {
        _hair.Clear();
        // Asset == null IS the bald row; its label is resolved at build time,
        // because BuildUi re-runs on language change but this does not.
        _hair.Add(new HairEntry { DisplayName = null, Asset = null });

#if UNITY_EDITOR
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets(
                     "t:Prefab", new[] { "Assets/ImportedActors/Hair" }))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var wear = prefab != null ? prefab.GetComponent<Wear>() : null;
            if (wear == null)
            {
                continue;
            }

            _hair.Add(new HairEntry { DisplayName = prefab.name, Asset = wear });
        }
#endif

        _hair.Sort((a, b) =>
        {
            // The bald row stays pinned at the top (and has no DisplayName).
            if (a.Asset == null)
            {
                return b.Asset == null ? 0 : -1;
            }

            if (b.Asset == null)
            {
                return 1;
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void OnHairClicked(HairEntry entry)
    {
        if (_bodyBones == null)
        {
            return;
        }

        _selectedHair = entry;
        _bodyBones.SetHair(entry.Asset);
        RefreshHairColours();

        // A new hairstyle brings new renderers — they need the same culling
        // relaxation the body got, or the strands vanish mid-sleep.
        RelaxSkinCulling();
        RefreshHairRows();
        RefreshHairFit();
    }

    private void RefreshHairRows()
    {
        foreach (var entry in _hair)
        {
            if (entry.Row != null)
            {
                entry.Row.style.backgroundColor = entry == _selectedHair ? Accent : Raised;
            }
        }
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

        var prefab = HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>($"HexLive/Actors/{girl}");
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

        // Re-dress the outfit carried over from the previous girl, each piece in
        // the colour it was last wearing.
        foreach (var key in new List<string>(_equipped))
        {
            if (_byKey.TryGetValue(key, out var entry))
            {
                _bodyBones?.Equip(entry.EquipKey, entry.Asset);
            }
        }

        ResyncEquipped();
        RelaxSkinCulling();
        RefreshAllRows();
        RefreshGirlButtons();
        RefreshScalePanel();

        // A fresh body wears the hairstyle authored on HER actor prefab, so the
        // panel follows the girl instead of keeping the previous selection.
        var authored = _bodyBones != null ? _bodyBones.DefaultHair : null;
        _selectedHair = _hair.Find(h => h.Asset == authored) ?? _hair.Find(h => h.Asset == null);
        RefreshHairRows();
        RefreshHairFit();
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
                _bodyBones.TakeOff(entry.EquipKey); // second click: undress
                _selectedKey = null;
            }
        }
        else
        {
            _bodyBones.Equip(entry.EquipKey, entry.Asset);
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
            if (_bodyBones.IsEquipped(entry.EquipKey))
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
        if (_bodyBones != null && _bodyBones.IsEquipped(entry.EquipKey))
        {
            _bodyBones.TakeOff(entry.EquipKey);
            _bodyBones.Equip(entry.EquipKey, entry.Asset);
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
        SaveComments();
        Debug.Log($"Wardrobe: сохранено префабов {_dirty.Count}, заметок {_comments.Count}");
        _dirty.Clear();
        RefreshScalePanel();
#else
        Debug.LogWarning("Wardrobe: saving prefabs only works in the editor");
#endif
    }

#if UNITY_EDITOR
    // Плоский JSON `{"id": "заметка"}`, отсортированный по id. Своими руками, а
    // не JsonUtility: он не умеет словари, а заводить ради двух строк класс с
    // парой массивов — значит сделать файл, который неудобно читать глазами, а
    // читать его будут именно глазами.
    private void LoadComments()
    {
        _comments.Clear();
        if (!System.IO.File.Exists(CommentsPath))
        {
            return;
        }

        foreach (var raw in System.IO.File.ReadAllLines(CommentsPath))
        {
            // Индексы считаются по ОДНОЙ строке — обрезанной. Смешивать их с
            // индексами исходной значит резать заметку не там, где кажется.
            var line = raw.Trim().TrimEnd(',');
            if (!line.StartsWith("\"", StringComparison.Ordinal))
            {
                continue;
            }

            var split = line.IndexOf("\": \"", StringComparison.Ordinal);
            if (split <= 0 || !line.EndsWith("\"", StringComparison.Ordinal))
            {
                continue;
            }

            var id = line.Substring(1, split - 1);
            var text = line.Substring(split + 4, line.Length - split - 5);
            _comments[id] = text.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }

    private void SaveComments()
    {
        if (!_commentsDirty)
        {
            return;
        }

        var sb = new System.Text.StringBuilder("{\n");
        var keys = new List<string>(_comments.Keys);
        keys.Sort(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var text = _comments[keys[i]].Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "").Replace("\n", "\\n");
            sb.Append("  \"").Append(keys[i]).Append("\": \"").Append(text).Append('"')
              .Append(i < keys.Count - 1 ? ",\n" : "\n");
        }

        sb.Append("}\n");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CommentsPath));
        System.IO.File.WriteAllText(CommentsPath, sb.ToString());
        UnityEditor.AssetDatabase.ImportAsset(CommentsPath);
        _commentsDirty = false;
    }
#endif

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
        BuildHairPanel(root);
        BuildScalePanel(root);
        BuildVariantsPanel(root);
    }

    // Sits just left of the clothes panel (which is 344 wide at right:14).
    private void BuildHairPanel(VisualElement root)
    {
        var box = MakePanel();
        box.style.right = 372f;
        box.style.top = 14f;
        box.style.bottom = 14f;
        box.style.width = 200f;
        root.Add(box);

        box.Add(MakeTitle(Loc.Get("wardrobe.hair")));

        var hint = new Label(Loc.Get("wardrobe.hair_hint"));
        hint.style.color = Muted;
        hint.style.fontSize = 10;
        hint.style.whiteSpace = WhiteSpace.Normal;
        hint.style.marginBottom = 6f;
        box.Add(hint);

        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.style.flexGrow = 1f;
        box.Add(scroll);

        foreach (var entry in _hair)
        {
            var captured = entry;
            var label = entry.Asset == null ? Loc.Get("wardrobe.hair_none") : entry.DisplayName;
            var row = MakeButton(label, Raised, () => OnHairClicked(captured));
            row.style.height = 24f;
            row.style.marginBottom = 3f;
            ((Label)row[0]).style.fontSize = 11;
            entry.Row = row;
            scroll.Add(row);
        }

        // --- цвета выбранной причёски. Вариант причёски — это ТА ЖЕ причёска с
        // другой картой (у волос нет ни слота, ни строки каталога), поэтому цвет
        // применяется подменой материалов на живой причёске, без пересоздания.
        var colourTitle = MakeTitle(Loc.Get("wardrobe.hair_colour"));
        colourTitle.style.marginTop = 8f;
        box.Add(colourTitle);

        _hairColourStrip = new ScrollView(ScrollViewMode.Vertical);
        _hairColourStrip.style.maxHeight = 150f;
        _hairColourStrip.contentContainer.style.flexDirection = FlexDirection.Row;
        _hairColourStrip.contentContainer.style.flexWrap = Wrap.Wrap;
        box.Add(_hairColourStrip);

        // --- fit block: every hairstyle was authored on the generic Genesis3
        // head, so each girl needs her own nudge. Written into the hair
        // prefab's WearConfig, per actor, and applied live.
        var fitTitle = MakeTitle(Loc.Get("wardrobe.hair_fit"));
        fitTitle.style.marginTop = 8f;
        box.Add(fitTitle);

        _hairScaleLabel = MakeStepper(box, () => AdjustHairScale(-0.01f), () => AdjustHairScale(0.01f));
        _hairHeightLabel = MakeStepper(box, () => AdjustHairHeight(-0.005f), () => AdjustHairHeight(0.005f));

        _hairSaveButton = MakeButton(Loc.Get("wardrobe.hair_save"), Accent, SaveHairFit);
        box.Add(_hairSaveButton);

        RefreshHairRows();
        RefreshHairFit();
        RefreshHairColours();
    }

    // Цвета лежат папками рядом с самой причёской:
    // ImportedActors/Hair/<Причёска>/Materials/<Цвет>/<Поверхность>.mat —
    // их собирает меню HexLive/Hair/Build Hair Variants из HairVariants.json.
    // Как и список причёсок, это редакторская витрина: в сборке волосы не
    // перечисляются, а стоят прямо на префабе актрисы.
    private void RefreshHairColours()
    {
        if (_hairColourStrip == null)
        {
            return;
        }

        _hairColourStrip.Clear();

#if UNITY_EDITOR
        var hair = _selectedHair?.Asset;
        if (hair == null)
        {
            return;
        }

        var root = $"Assets/ImportedActors/Hair/{hair.name}/Materials";
        var folders = UnityEditor.AssetDatabase.GetSubFolders(root);
        if (folders.Length == 0)
        {
            return;
        }

        // «Как из коробки» — материалы самого прототипа, уровнем выше цветов.
        AddHairColourTile(Loc.Get("wardrobe.hair_colour_base"), null);
        Array.Sort(folders, StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            AddHairColourTile(System.IO.Path.GetFileName(folder), folder);
        }
#endif
    }

    private void AddHairColourTile(string label, string folder)
    {
        var tile = MakeButton(label, Raised, () => ApplyHairColour(folder));
        tile.style.height = 20f;
        tile.style.marginRight = 3f;
        tile.style.marginBottom = 3f;
        tile.style.paddingLeft = 5f;
        tile.style.paddingRight = 5f;
        ((Label)tile[0]).style.fontSize = 10;
        _hairColourStrip.Add(tile);
    }

    // ⭐ Цвет — это ТЕКСТУРА, а не материал. Настроенный материал причёски
    // существует в ЕДИНСТВЕННОМ экземпляре на поверхность, и цвет меняет в нём
    // только карту — на ЖИВОМ экземпляре, который `SetHair` и так создаёт заново.
    //
    // Почему не копией материала на каждый цвет: тогда подобранные вручную
    // пороги прозрачности лежали бы в 254 копиях, и правка порога у прототипа
    // не дошла бы ни до одной. Настройки должны жить в одном месте — ровно
    // потому, что однажды они уже разошлись с картинками.
    //
    // Почему по экземпляру, а не по ассету: материал в Unity общий, и запись в
    // ассет перекрасила бы эту причёску у всех сразу — Jana и Marta не смогли
    // бы носить её разного цвета.
    private void ApplyHairColour(string folder)
    {
#if UNITY_EDITOR
        var live = _bodyBones != null ? _bodyBones.HairInstance : null;
        if (live == null)
        {
            return;
        }

        var hairName = _selectedHair?.Asset != null ? _selectedHair.Asset.name : null;
        var source = folder ?? $"Assets/ImportedActors/Hair/{hairName}/Materials";
        var byName = new Dictionary<string, Material>();
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Material", new[] { source }))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            // FindAssets ищет вглубь: материалы ДРУГИХ цветов сюда попасть не должны.
            if (System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') != source)
            {
                continue;
            }

            var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null)
            {
                byName[mat.name] = mat;
            }
        }

        foreach (var renderer in live.GetComponentsInChildren<Renderer>(true))
        {
            var mats = renderer.sharedMaterials;
            for (var i = 0; i < mats.Length; i++)
            {
                var surface = mats[i] != null
                    ? mats[i].name.Replace(" (Instance)", string.Empty)
                    : null;
                if (surface != null && byName.TryGetValue(surface, out var swap))
                {
                    mats[i] = swap;
                }
            }

            renderer.sharedMaterials = mats;
        }
#endif
    }

    // "[-] label [+]" row, returning the middle label so callers can retitle it.
    private Label MakeStepper(VisualElement parent, Action minus, Action plus)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 3f;

        var less = MakeButton("-", Raised, minus);
        less.style.width = 26f;
        less.style.height = 22f;
        row.Add(less);

        var value = new Label("-");
        value.style.flexGrow = 1f;
        value.style.color = Text;
        value.style.fontSize = 11;
        value.style.unityTextAlign = TextAnchor.MiddleCenter;
        row.Add(value);

        var more = MakeButton("+", Raised, plus);
        more.style.width = 26f;
        more.style.height = 22f;
        row.Add(more);

        parent.Add(row);
        return value;
    }

    private void AdjustHairScale(float delta)
    {
        if (_selectedHair?.Asset == null)
        {
            return;
        }

        var scale = Mathf.Clamp(_selectedHair.Asset.GetConfigScale(_girl) + delta, 0.5f, 2f);
        _selectedHair.Asset.SetConfigScale(_girl, scale);
        MarkHairDirty();
    }

    private void AdjustHairHeight(float delta)
    {
        if (_selectedHair?.Asset == null)
        {
            return;
        }

        var height = Mathf.Clamp(_selectedHair.Asset.GetConfigHeight(_girl) + delta, -0.2f, 0.2f);
        _selectedHair.Asset.SetConfigHeight(_girl, height);
        MarkHairDirty();
    }

    // Re-spawn the hair so Wear.Construct re-applies the fit — it is baked in
    // at construct time, not driven per frame.
    private void MarkHairDirty()
    {
        _hairDirty = true;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(_selectedHair.Asset);
#endif
        _bodyBones?.SetHair(_selectedHair.Asset);
        RelaxSkinCulling();
        RefreshHairFit();
    }

    private void SaveHairFit()
    {
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.SaveAssets();
        _hairDirty = false;
        RefreshHairFit();
        Debug.Log("Wardrobe: saved hair fit into the hair prefab(s)");
#else
        Debug.LogWarning("Wardrobe: saving prefabs only works in the editor");
#endif
    }

    private void RefreshHairFit()
    {
        var hair = _selectedHair?.Asset;
        if (_hairScaleLabel != null)
        {
            _hairScaleLabel.text = string.Format(
                Loc.Get("wardrobe.hair_scale"), hair != null ? hair.GetConfigScale(_girl) : 1f);
        }

        if (_hairHeightLabel != null)
        {
            _hairHeightLabel.text = string.Format(
                Loc.Get("wardrobe.hair_height"), hair != null ? hair.GetConfigHeight(_girl) : 0f);
        }

        if (_hairSaveButton != null)
        {
            _hairSaveButton.style.backgroundColor = _hairDirty ? AccentSel : Accent;
        }
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
        box.style.width = 344f;
        root.Add(box);

        box.Add(MakeTitle(Loc.Get("wardrobe.clothes")));

        var hint = new Label(Loc.Get("wardrobe.click_hint"));
        hint.style.color = Muted;
        hint.style.fontSize = 10;
        hint.style.marginBottom = 6f;
        box.Add(hint);

        _tabButtons.Clear();
        var tabs = new VisualElement();
        tabs.style.flexDirection = FlexDirection.Row;
        tabs.style.marginBottom = 6f;
        box.Add(tabs);

        foreach (var (layer, term) in new[]
                 {
                     (VisualWearLayer.Underwear, "wardrobe.tab_underwear"),
                     (VisualWearLayer.Wear, "wardrobe.tab_wear"),
                     (VisualWearLayer.Outerwear, "wardrobe.tab_outerwear"),
                 })
        {
            var captured = layer;
            var tab = MakeButton(Loc.Get(term), Raised, () => OnTabClicked(captured));
            tab.style.flexGrow = 1f;
            tab.style.flexBasis = 0f;
            tab.style.height = 26f;
            tab.style.marginBottom = 0f;
            tab.style.marginRight = layer == VisualWearLayer.Outerwear ? 0f : 4f;
            tab.style.paddingLeft = 0f;
            tab.style.paddingRight = 0f;
            var label = (Label)tab[0];
            label.style.fontSize = 11;
            label.style.flexGrow = 1f;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _tabButtons[layer] = tab;
            tabs.Add(tab);
        }

        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.style.flexGrow = 1f;
        // Сетка: тайлы текут слева направо и заворачиваются на новую строку.
        scroll.contentContainer.style.flexDirection = FlexDirection.Row;
        scroll.contentContainer.style.flexWrap = Wrap.Wrap;
        box.Add(scroll);

        foreach (var entry in _entries)
        {
            var captured = entry;
            entry.Row = MakeWearTile(entry, () => OnRowClicked(captured));
            scroll.Add(entry.Row);
        }

        RefreshTabs();
        ApplyActorFilter();
    }

    // Квадратный тайл: превью вещи (те же инвентарные иконки
    // Resources/HexLive/UI/Items/<id>, id = папка = definition id) и имя
    // префаба под ним. Без иконки — первая буква имени как заглушка.
    private VisualElement MakeWearTile(WearEntry entry, Action onClick)
    {
        var tile = new VisualElement();
        tile.style.width = 72f;
        tile.style.height = 98f;
        tile.style.marginRight = 4f;
        tile.style.marginBottom = 4f;
        tile.style.paddingTop = 4f;
        tile.style.paddingLeft = 3f;
        tile.style.paddingRight = 3f;
        tile.style.alignItems = Align.Center;
        tile.style.backgroundColor = Raised;
        SetRadius(tile, 8f);
        tile.RegisterCallback<MouseDownEvent>(_ => onClick());

        var iconBox = new VisualElement();
        iconBox.style.width = 58f;
        iconBox.style.height = 58f;
        iconBox.style.flexShrink = 0f;
        iconBox.style.alignItems = Align.Center;
        iconBox.style.justifyContent = Justify.Center;
        iconBox.pickingMode = PickingMode.Ignore;
        tile.Add(iconBox);

        var sprite = LoadItemIcon(entry.Group);
        if (sprite != null)
        {
            var image = new Image();
            image.sprite = sprite;
            image.scaleMode = ScaleMode.ScaleToFit;
            image.style.width = 56f;
            image.style.height = 56f;
            image.pickingMode = PickingMode.Ignore;
            iconBox.Add(image);
        }
        else
        {
            var placeholder = new Label(entry.DisplayName.Substring(0, 1));
            placeholder.style.color = Muted;
            placeholder.style.fontSize = 24;
            placeholder.style.unityFontStyleAndWeight = FontStyle.Bold;
            placeholder.pickingMode = PickingMode.Ignore;
            iconBox.Add(placeholder);
        }

        var name = new Label(entry.DisplayName);
        name.style.color = Text;
        name.style.fontSize = 9;
        name.style.unityTextAlign = TextAnchor.UpperCenter;
        name.style.whiteSpace = WhiteSpace.Normal;
        name.style.overflow = Overflow.Hidden;
        name.style.flexGrow = 1f;
        name.style.width = 66f;
        name.pickingMode = PickingMode.Ignore;
        tile.Add(name);

        return tile;
    }

    private void OnTabClicked(VisualWearLayer layer)
    {
        _wearTab = layer;
        RefreshTabs();
        ApplyActorFilter();
    }

    private void RefreshTabs()
    {
        foreach (var pair in _tabButtons)
        {
            pair.Value.style.backgroundColor = pair.Key == _wearTab ? Accent : Raised;
        }
    }

    // §72: показываем только ту одежду, что скроена под ТЕЛО выбранного актёра.
    // Женская вещь на мужском теле рисуется искорёженным мешем (фит всегда
    // пофигурный), так что это не косметика списка, а защита от заведомо
    // неверного показа. Второе условие — слой активной вкладки.
    private void ApplyActorFilter()
    {
        var sex = ActorSex.Of(_girl);
        foreach (var entry in _entries)
        {
            var fits = entry.Asset != null && entry.Asset.Gender == sex &&
                entry.Asset.Layer == _wearTab;
            if (entry.Row != null)
            {
                entry.Row.style.display = fits ? DisplayStyle.Flex : DisplayStyle.None;
            }
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

        BuildWearFlags(_scaleBox);

        var save = MakeButton(Loc.Get("wardrobe.save_prefabs"), Accent, SaveDirty);
        _saveLabel = (Label)save[0];
        _scaleBox.Add(save);

        RefreshScalePanel();
    }

    // ---- то, что раньше правилось только в инспекторе ----
    //
    // Слой, «прячет волосы» и заметка живут здесь по одной причине: их решают,
    // ГЛЯДЯ на надетую вещь. Слой — это не свойство ткани, а ответ на вопрос
    // «что она вытесняет»; заметку пишут ровно тогда, когда видно, что не так.
    // Через инспектор это значит открыть префаб, потерять сцену из виду и
    // забыть половину.
    private void BuildWearFlags(VisualElement box)
    {
        var layers = new VisualElement();
        layers.style.flexDirection = FlexDirection.Row;
        layers.style.marginBottom = 4f;
        box.Add(layers);

        _layerButtons.Clear();
        foreach (VisualWearLayer value in System.Enum.GetValues(typeof(VisualWearLayer)))
        {
            var pick = value;
            var button = MakeButton(Loc.Get($"wardrobe.layer_{value.ToString().ToLowerInvariant()}"),
                Raised, () => SetSelectedLayer(pick));
            button.style.flexGrow = 1f;
            button.style.marginRight = 3f;
            button.style.justifyContent = Justify.Center;
            layers.Add(button);
            _layerButtons[pick] = button;
        }

        _hidesHairToggle = MakeButton(Loc.Get("wardrobe.hides_hair"), Raised, ToggleHidesHair);
        _hidesHairToggle.style.marginBottom = 4f;
        box.Add(_hidesHairToggle);

        var noHideTitle = new Label(Loc.Get("wardrobe.shows_underwear"));
        noHideTitle.style.color = Muted;
        noHideTitle.style.fontSize = 10;
        box.Add(noHideTitle);

        // Строится заново под каждую вещь: перечислять здесь ВСЕ слоты
        // бессмысленно, речь только о тех, которые эта вещь закрывает.
        _noHideRow = new VisualElement();
        _noHideRow.style.flexDirection = FlexDirection.Row;
        _noHideRow.style.flexWrap = Wrap.Wrap;
        _noHideRow.style.marginBottom = 6f;
        box.Add(_noHideRow);

        _hideWearTitle = new Label(Loc.Get("wardrobe.hides_wear"));
        _hideWearTitle.style.color = Muted;
        _hideWearTitle.style.fontSize = 10;
        box.Add(_hideWearTitle);

        _hideWearRow = new VisualElement();
        _hideWearRow.style.flexDirection = FlexDirection.Row;
        _hideWearRow.style.flexWrap = Wrap.Wrap;
        _hideWearRow.style.marginBottom = 6f;
        box.Add(_hideWearRow);

        _commentField = new TextField { multiline = true };
        _commentField.style.marginBottom = 6f;
        _commentField.style.minHeight = 46f;
        _commentField.style.whiteSpace = WhiteSpace.Normal;
        _commentField.RegisterValueChangedCallback(e => StoreComment(e.newValue));
        box.Add(_commentField);
    }

    private void SetSelectedLayer(VisualWearLayer layer)
    {
        if (_selectedKey == null || !_byKey.TryGetValue(_selectedKey, out var entry) ||
            entry.Asset.Layer == layer)
        {
            return;
        }

        entry.Asset.SetLayer(layer);

        // Слой живёт ДВАЖДЫ: в префабе — чтобы вещи вытесняли друг друга на
        // теле, и в определении — чтобы то же самое считала симуляция. Оба
        // перечисления идут в одном порядке (Underwear, Wear, Outerwear), но
        // это разные типы в разных сборках. Правка только префаба разошлась бы
        // тихо: на девушке одно, в игре другое.
#if UNITY_EDITOR
        var id = entry.VariantId ?? entry.Group;
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:GarmentDefinition"))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var def = UnityEditor.AssetDatabase.LoadAssetAtPath<GarmentDefinition>(path);
            if (def == null || def.id != id)
            {
                continue;
            }

            def.layer = (WearLayer)(int)layer;
            UnityEditor.EditorUtility.SetDirty(def);
            break;
        }
#endif

        MarkDirtyAndRedress(entry);
    }

    private void ToggleHideUnderwear(VisualWearSlot slot)
    {
        if (_selectedKey == null || !_byKey.TryGetValue(_selectedKey, out var entry))
        {
            return;
        }

        entry.Asset.SetHideUnderwear(slot, !entry.Asset.HeedHideUnderwearSlot(slot));
        MarkDirtyAndRedress(entry);
    }

    // Кнопка на каждый слот, который эта вещь закрывает. Подсвечена — бельё под
    // ней в этом слоте ВИДНО (слот в списке исключений); тусклая — спрятано,
    // как по умолчанию.
    private void RebuildNoHideRow(WearEntry entry)
    {
        if (_noHideRow == null)
        {
            return;
        }

        _noHideRow.Clear();
        if (entry == null || entry.Asset == null)
        {
            return;
        }

        foreach (var slot in entry.Asset.Slots)
        {
            var pick = slot;
            var shown = !entry.Asset.HeedHideUnderwearSlot(pick);
            var chip = MakeButton(slot.ToString(), shown ? AccentSel : Raised,
                () => ToggleHideUnderwear(pick));
            chip.style.marginRight = 3f;
            chip.style.marginBottom = 3f;
            chip.style.paddingLeft = 6f;
            chip.style.paddingRight = 6f;
            _noHideRow.Add(chip);
        }
    }

    private void ToggleHideWear(VisualWearSlot slot)
    {
        if (_selectedKey == null || !_byKey.TryGetValue(_selectedKey, out var entry) ||
            entry.Asset.Layer != VisualWearLayer.Outerwear)
        {
            return;
        }

        entry.Asset.SetHideWear(slot, !entry.Asset.HidesWearSlot(slot));
        MarkDirtyAndRedress(entry);
    }

    // Только Outerwear имеет нижележащий Wear-слой. Подсвеченный слот означает
    // «скрыть одежду под этой вещью»; пустая маска — безопасный default.
    private void RebuildHideWearRow(WearEntry entry)
    {
        if (_hideWearTitle == null || _hideWearRow == null)
        {
            return;
        }

        _hideWearRow.Clear();
        var applicable = entry != null && entry.Asset != null &&
            entry.Asset.Layer == VisualWearLayer.Outerwear;
        _hideWearTitle.style.display = applicable ? DisplayStyle.Flex : DisplayStyle.None;
        _hideWearRow.style.display = applicable ? DisplayStyle.Flex : DisplayStyle.None;
        if (!applicable)
        {
            return;
        }

        foreach (var slot in entry.Asset.Slots)
        {
            var pick = slot;
            var hidden = entry.Asset.HidesWearSlot(pick);
            var chip = MakeButton(slot.ToString(), hidden ? AccentSel : Raised,
                () => ToggleHideWear(pick));
            chip.style.marginRight = 3f;
            chip.style.marginBottom = 3f;
            chip.style.paddingLeft = 6f;
            chip.style.paddingRight = 6f;
            _hideWearRow.Add(chip);
        }
    }

    private void ToggleHidesHair()
    {
        if (_selectedKey == null || !_byKey.TryGetValue(_selectedKey, out var entry))
        {
            return;
        }

        entry.Asset.SetHidesHair(!entry.Asset.HidesHair);
        MarkDirtyAndRedress(entry);
    }

    // Заново надеть — иначе изменение слоя видно только на следующем надевании:
    // вытеснение по слоям считается в Equip, а не каждый кадр.
    private void MarkDirtyAndRedress(WearEntry entry)
    {
        if (_bodyBones != null && _bodyBones.IsEquipped(entry.EquipKey))
        {
            _bodyBones.TakeOff(entry.EquipKey);
            _bodyBones.Equip(entry.EquipKey, entry.Asset);
            RelaxSkinCulling();
        }

        _dirty.Add(entry.Key);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(entry.Asset);
#endif
        // Список фильтруется по слою вещи, а не по тому, каким он был при
        // сборке UI: сменив слой, вещь должна тут же уехать в свою вкладку,
        // иначе она останется висеть там, где её больше нет.
        ApplyActorFilter();
        RefreshScalePanel();
    }

    private void StoreComment(string text)
    {
        if (_selectedKey == null || !_byKey.TryGetValue(_selectedKey, out var entry))
        {
            return;
        }

        var id = entry.VariantId ?? entry.Group;
        if (string.IsNullOrWhiteSpace(text))
        {
            _comments.Remove(id);
        }
        else
        {
            _comments[id] = text;
        }

        _commentsDirty = true;
    }

    // ---- variants (§31B.4E) ----

    // Sits between the scale panel (left, 320 wide) and the hair panel, and
    // stays hidden until a prototype with more than one colour is selected —
    // most of the wardrobe has exactly one, and an always-present empty panel
    // would just eat the view of the girl the tool exists to show.
    private void BuildVariantsPanel(VisualElement root)
    {
        _variantsFor = null;
        _variantTiles.Clear();

        _variantsBox = MakePanel();
        _variantsBox.style.left = 348f;
        _variantsBox.style.bottom = 14f;
        _variantsBox.style.width = 560f;
        _variantsBox.style.display = DisplayStyle.None;
        root.Add(_variantsBox);

        _variantsTitle = MakeTitle(string.Empty);
        _variantsBox.Add(_variantsTitle);

        var hint = new Label(Loc.Get("wardrobe.variants_hint"));
        hint.style.color = Muted;
        hint.style.fontSize = 10;
        hint.style.marginBottom = 6f;
        _variantsBox.Add(hint);

        _variantsStrip = new ScrollView(ScrollViewMode.Vertical);
        // A DAZ product can ship 78 colourways — they wrap into rows and the
        // panel scrolls, rather than growing a mile-wide horizontal strip.
        _variantsStrip.style.maxHeight = 178f;
        _variantsStrip.contentContainer.style.flexDirection = FlexDirection.Row;
        _variantsStrip.contentContainer.style.flexWrap = Wrap.Wrap;
        _variantsBox.Add(_variantsStrip);

        _variantsDesc = new Label(string.Empty);
        _variantsDesc.style.color = Muted;
        _variantsDesc.style.fontSize = 10;
        _variantsDesc.style.whiteSpace = WhiteSpace.Normal;
        _variantsDesc.style.marginTop = 5f;
        _variantsDesc.style.minHeight = 26f;
        _variantsBox.Add(_variantsDesc);

        RefreshVariantsPanel();
    }

    private void RefreshVariantsPanel()
    {
        if (_variantsBox == null)
        {
            return;
        }

        WearEntry entry = null;
        if (_selectedKey != null)
        {
            _byKey.TryGetValue(_selectedKey, out entry);
        }

        // One colour is not a choice — the panel would only ever restate what
        // the clothes grid already shows.
        var variants = entry?.Variants;
        if (variants == null || variants.Count < 2)
        {
            _variantsFor = null;
            _variantsBox.style.display = DisplayStyle.None;
            _variantsStrip.Clear();
            _variantTiles.Clear();
            return;
        }

        _variantsBox.style.display = DisplayStyle.Flex;

        // Rebuilding the strip on every refresh would re-run 78 tiles per arrow
        // key while the fit scale is being nudged; only the selection changes.
        if (_variantsFor != entry)
        {
            _variantsFor = entry;
            _variantsStrip.Clear();
            _variantTiles.Clear();
            _variantsTitle.text = string.Format(
                Loc.Get("wardrobe.variants"), PrototypeName(entry), variants.Count);

            foreach (var def in variants)
            {
                var captured = def;
                var tile = MakeVariantTile(entry, captured);
                _variantTiles[captured.id] = tile;
                _variantsStrip.Add(tile);
            }

            ShowVariantDesc(SelectedVariant(entry));
        }

        RefreshVariantTiles(entry);
    }

    private VisualElement MakeVariantTile(WearEntry entry, GarmentDefinition def)
    {
        var tile = new VisualElement();
        tile.style.width = 64f;
        tile.style.height = 84f;
        tile.style.marginRight = 4f;
        tile.style.marginBottom = 4f;
        tile.style.paddingTop = 3f;
        tile.style.alignItems = Align.Center;
        tile.style.backgroundColor = Raised;
        SetRadius(tile, 8f);
        tile.RegisterCallback<MouseDownEvent>(_ => OnVariantClicked(entry, def));
        // The description belongs to one variant but is far too long for a
        // 64-pixel tile, so it lives on a single line under the strip and
        // follows the cursor.
        tile.RegisterCallback<MouseEnterEvent>(_ => ShowVariantDesc(def));
        tile.RegisterCallback<MouseLeaveEvent>(_ => ShowVariantDesc(SelectedVariant(entry)));

        var iconBox = new VisualElement();
        iconBox.style.width = 48f;
        iconBox.style.height = 48f;
        iconBox.style.flexShrink = 0f;
        iconBox.style.alignItems = Align.Center;
        iconBox.style.justifyContent = Justify.Center;
        iconBox.pickingMode = PickingMode.Ignore;
        tile.Add(iconBox);

        var sprite = LoadItemIcon(def.id);
        if (sprite != null)
        {
            var image = new Image();
            image.sprite = sprite;
            image.scaleMode = ScaleMode.ScaleToFit;
            image.style.width = 46f;
            image.style.height = 46f;
            image.pickingMode = PickingMode.Ignore;
            iconBox.Add(image);
        }
        else
        {
            // A colourway ships its own icon; a missing one is a hole in the
            // drop, so it reads as an empty frame rather than a stand-in.
            var placeholder = new Label("?");
            placeholder.style.color = Muted;
            placeholder.style.fontSize = 20;
            placeholder.pickingMode = PickingMode.Ignore;
            iconBox.Add(placeholder);
        }

        var name = new Label(VariantName(def));
        name.style.color = Text;
        name.style.fontSize = 9;
        name.style.unityTextAlign = TextAnchor.UpperCenter;
        name.style.whiteSpace = WhiteSpace.Normal;
        name.style.overflow = Overflow.Hidden;
        name.style.flexGrow = 1f;
        name.style.width = 60f;
        name.pickingMode = PickingMode.Ignore;
        tile.Add(name);

        return tile;
    }

    private void OnVariantClicked(WearEntry entry, GarmentDefinition def)
    {
        if (_bodyBones == null)
        {
            return;
        }

        // A variant's materials go on through Wear.ApplyVariant, which MUST run
        // before Construct — that caches the dry colour to wash back to, and a
        // cache taken from the prototype would rinse this piece into the wrong
        // colour after the first rain. So the colour changes by re-dressing,
        // never by repainting what is already on the body.
        _bodyBones.TakeOff(entry.EquipKey); // no-op when it is not worn yet
        entry.VariantId = def.id;
        _bodyBones.Equip(entry.EquipKey, entry.Asset);
        RelaxSkinCulling();

        _selectedKey = entry.Key;
        ResyncEquipped();
        RefreshAllRows();
        RefreshScalePanel();
        ShowVariantDesc(def);
    }

    private void RefreshVariantTiles(WearEntry entry)
    {
        foreach (var pair in _variantTiles)
        {
            pair.Value.style.backgroundColor = pair.Key == entry.VariantId ? AccentSel : Raised;
        }
    }

    private GarmentDefinition SelectedVariant(WearEntry entry)
    {
        if (entry?.Variants == null)
        {
            return null;
        }

        foreach (var def in entry.Variants)
        {
            if (def.id == entry.VariantId)
            {
                return def;
            }
        }

        return null;
    }

    private void ShowVariantDesc(GarmentDefinition def)
    {
        if (_variantsDesc != null)
        {
            _variantsDesc.text = def == null ? string.Empty : VariantDesc(def);
        }
    }

    // The heading names the GEOMETRY, so it reads off the prototype's own item
    // term; the prefab name is a DAZ export artefact and means nothing to a
    // reader. Falls back to it only when the folder has no catalog row at all.
    private static string PrototypeName(WearEntry entry)
    {
        foreach (var def in entry.Variants)
        {
            if (def.id == entry.Group)
            {
                return VariantName(def);
            }
        }

        return entry.DisplayName;
    }

    // §58: the player-facing name is the item's I2 term. The inspector's
    // displayName and the raw id are dev fallbacks — a garment whose terms were
    // never authored has to stay identifiable in the tool that shows it.
    private static string VariantName(GarmentDefinition def)
    {
        var key = $"item.{ItemInfo.Slug(def.id)}.name";
        if (Loc.Has(key))
        {
            return Loc.Get(key);
        }

        return string.IsNullOrEmpty(def.displayName) ? def.id : def.displayName;
    }

    private static string VariantDesc(GarmentDefinition def)
    {
        var key = $"item.{ItemInfo.Slug(def.id)}.desc";
        return Loc.Has(key) ? Loc.Get(key) : def.id;
    }

    // Icons are named by ITEM id, which may carry dots ("clothing.belt_cindy");
    // the slug form is the fallback, same rule the inventory panel follows.
    // Только для браузера: все префабы арта вещей с диска.
    private static List<GameObject> LoadWearPrefabsForBrowser()
    {
        var result = new List<GameObject>();
#if UNITY_EDITOR
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets(
                     "t:Prefab", new[] { "Assets/HexLiveContent/Wear" }))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null)
            {
                result.Add(prefab);
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
#endif
        return result;
    }

    private static Sprite LoadItemIcon(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return Wearing.Garments.ItemIcons.Load(id);
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

        WearEntry entry = null;
        if (_selectedKey != null && _byKey.TryGetValue(_selectedKey, out entry))
        {
            _scaleTitle.text = string.Format(Loc.Get("wardrobe.scale_selected"), entry.DisplayName, _girl);
            _scaleValue.text = entry.Asset.GetConfigScale(_girl).ToString("0.000");
        }
        else
        {
            _scaleTitle.text = Loc.Get("wardrobe.scale_none");
            _scaleValue.text = "—";
        }

        foreach (var pair in _layerButtons)
        {
            pair.Value.style.backgroundColor =
                entry != null && entry.Asset.Layer == pair.Key ? AccentSel : Raised;
        }

        if (_hidesHairToggle != null)
        {
            _hidesHairToggle.style.backgroundColor =
                entry != null && entry.Asset.HidesHair ? AccentSel : Raised;
        }

        RebuildNoHideRow(entry);
        RebuildHideWearRow(entry);

        if (_commentField != null)
        {
            // SetValueWithoutNotify, иначе перерисовка панели тут же запишет то,
            // что сама и подставила, и заметка соседней вещи уедет к этой.
            var id = entry != null ? entry.VariantId ?? entry.Group : null;
            _commentField.SetValueWithoutNotify(
                id != null && _comments.TryGetValue(id, out var note) ? note : "");
            _commentField.SetEnabled(entry != null);
        }

        if (_saveLabel != null)
        {
            _saveLabel.text = _dirty.Count > 0
                ? string.Format(Loc.Get("wardrobe.save_prefabs_count"), _dirty.Count)
                : Loc.Get("wardrobe.save_prefabs");
        }

        RefreshAllRows();
        // Every path that changes the selection already lands here, so the
        // variants strip follows it from one place instead of six.
        RefreshVariantsPanel();
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
