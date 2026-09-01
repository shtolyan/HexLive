using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Adapted from molly_copy Wearing.Wear (spec 31B.3): same serialized layout
// (this file's .meta claims the original script GUID so imported wear
// prefabs bind here), stripped of Actor/IK/game coupling. The actor mesh
// is passed in explicitly instead of being read off an Actor component.
public sealed class Wear : MonoBehaviour
{
    [SerializeField] private List<WearConfig> configs = new();
    [SerializeField] private List<VisualWearSlot> slots = new();
    [SerializeField] private List<VisualWearSlot> noHideUnderwearSlots = new();
    [SerializeField] private List<VisualWearSlot> hideWearSlots = new();
    [SerializeField] private VisualWearLayer layer;
    [SerializeField] private VisualGender gender = VisualGender.Female;

    // Spec §31B.4C: non-zero only on heeled shoes. Everything else leaves it at
    // default, which reads as "no heel" — so this is additive to the serialized
    // layout and every existing prefab keeps binding unchanged.
    [SerializeField] private HeelPose heel;

    // §31B.4F: посадка головного убора — не ноль только у вещей со слотом Head,
    // которым запечённая в вершины позиция не подошла. Default = «как отшито»,
    // так что поле аддитивно к сериализации и старые префабы читаются как прежде.
    [SerializeField] private HeadwearFit headwearFit;

    // Прячет ли эта вещь причёску. Шапка сидит на черепе, а причёска —
    // отдельный меш поверх него, и без этого волосы прорастают сквозь тулью.
    // Значение по умолчанию — «не прячет», так что старые префабы читаются
    // как прежде; головным уборам его ставит экстрактор.
    [SerializeField] private bool hidesHair;

    public VisualWearLayer Layer => layer;

    public bool HidesHair => hidesHair;

    public HeelPose Heel => heel;

    // Serialization contract with the imported prefabs — read back so the
    // compiler (and future gender-aware wardrobes) see it used.
    public VisualGender Gender => gender;

    public IReadOnlyList<VisualWearSlot> Slots => slots;

    /// <summary>
    /// Simulation item represented by this fitted visual. Runtime-only: the
    /// inventory preview uses it to map a renderer under the mouse back to the
    /// clothing card without maintaining another equipment store.
    /// </summary>
    public string DefinitionId { get; private set; } = string.Empty;

    private SkinnedMeshRenderer _meshRenderer;

    /// <summary>
    /// Paint this instance in a variant's materials (spec §31B.4E).
    /// </summary>
    /// <remarks>
    /// Must run BEFORE <see cref="Construct"/>: that caches each slot's dry
    /// colour and smoothness so dirt and wet can be washed back off, and a
    /// cache taken from the prototype would restore the wrong colour the first
    /// time it rained.
    ///
    /// A shorter array than the mesh has submeshes leaves the rest as the
    /// prototype's — a variant may recolour the cloth and keep the buttons.
    /// Null or empty means "this item is not a variant", which is most of them.
    /// </remarks>
    public void ApplyVariant(Material[] materials)
    {
        if (materials == null || materials.Length == 0)
        {
            return;
        }

        var renderer = _meshRenderer != null
            ? _meshRenderer
            : GetComponentInChildren<SkinnedMeshRenderer>();
        if (renderer == null)
        {
            return;
        }

        var current = renderer.sharedMaterials;
        for (var i = 0; i < current.Length && i < materials.Length; i++)
        {
            if (materials[i] != null)
            {
                current[i] = materials[i];
            }
        }

        renderer.sharedMaterials = current;
    }

    public void Construct(ActorName actorMesh, BodyBones bodyBones, string equipKey = null)
    {
        DefinitionId = DefinitionIdFromEquipKey(equipKey);
        // Spec 40.10-D: the wear painter seeds its stains from this — the piece's
        // slot on THIS body, which survives the garment being taken off and put
        // back on. Seeding from the fresh instance re-rolled the whole dirt
        // pattern on every re-equip, which read as the cloth flickering.
        _paintSeed = GarmentWearPainter.StableSeed(
            equipKey ?? name, bodyBones != null ? bodyBones.GetInstanceID() : 0);
        _meshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
        var hip = FindHip();

        if (hip == null || _meshRenderer == null)
        {
            Debug.LogWarning($"Wear '{name}': no hip or renderer — skipping construct", this);
            return;
        }

        foreach (var config in configs)
        {
            if (config.actorName == actorMesh)
            {
                // Hair is fitted at the head bone instead (ApplyHairFit);
                // scaling its hip too would compound the two.
                if (slots.Count > 0)
                {
                    hip.localScale = new Vector3(config.scale, config.scale, config.scale);
                }

                if (config.mesh != null)
                {
                    _meshRenderer.sharedMesh = config.mesh;
                }

                break;
            }
        }

        // §74.9 / bugs #129, #130: an imported mesh bounds only the bind pose,
        // not the vertices after this garment's bones have been stitched onto
        // the live actor. Gloves are the sharpest case (their box is only
        // 0.04 x 0.07 m on two axes), but the same contract applies to every
        // fitted piece. Keep skinning outside the stale import AABB so Unity
        // recomputes the live bounds instead of frustum-culling visible cloth.
        // GarmentCloth sets the same flag for simulated hems; doing it here is
        // the common path for ordinary clothes and every future extracted item.
        _meshRenderer.updateWhenOffscreen = true;

        // Spec 35.5: garments opt into the cloth decal layer so rain-droplet
        // projectors can land on them (wounds/dirt/sweat stay skin-only).
        _meshRenderer.renderingLayerMask |= ClothDecalLayer;

        // Stitch every garment bone onto the matching body bone.
        var garmentBones = hip.GetComponentsInChildren<Transform>(true);
        foreach (var bone in garmentBones)
        {
            var bodyBone = bodyBones.GetBone(bone.name);
            if (bodyBone == null)
            {
                continue;
            }

            var connection = bone.gameObject.AddComponent<ParentConnection>();
            connection.ConnectTo(bodyBone);
        }

        ApplyHairFit(actorMesh, garmentBones);
        CacheHeadwearFitBone(garmentBones);
        if (headwearFit.Any)
        {
            ApplyHeadwearFitNow();
        }

        // A garment that swings (skirts) builds its cloth LAST: the per-actor
        // mesh is in place by now and the bones already ride the body, which is
        // the state MagicaCloth samples when it builds its proxy.
        if (TryGetComponent<GarmentCloth>(out var cloth))
        {
            cloth.Build(bodyBones, _meshRenderer);
        }
    }

    private static string DefinitionIdFromEquipKey(string equipKey)
    {
        if (string.IsNullOrEmpty(equipKey))
        {
            return string.Empty;
        }

        var hash = equipKey.IndexOf('#');
        return hash > 0 ? equipKey.Substring(0, hash) : equipKey;
    }

    // Spec §31B.4B: seat a hairstyle on THIS girl's head.
    //
    // Hair only (slots empty) — a garment is refitted by swapping in a per-actor
    // mesh, hair is not. It has to run AFTER the stitching loop: ParentConnection
    // zeroes every matched bone's localPosition, so an offset written earlier is
    // wiped. The anchor is the hair's own `head` bone, now a zero-offset child of
    // the body's head — moving/scaling it carries the whole skull cap and every
    // hair-specific bone hanging off it. Strands weighted to neck/chest bones are
    // stitched elsewhere and stay put, which is why these are fit nudges
    // (a few cm, a few percent) and not a general transform.
    //
    // Takes the bone array captured BEFORE stitching: by now the hair's `head`
    // has been re-parented out from under the hair's own hip, so it can no
    // longer be found by walking down from there.
    private void ApplyHairFit(ActorName actorMesh, Transform[] hairBones)
    {
        if (slots.Count > 0)
        {
            return;
        }

        foreach (var config in configs)
        {
            if (config.actorName != actorMesh)
            {
                continue;
            }

            foreach (var bone in hairBones)
            {
                if (bone != null && bone.name == "head")
                {
                    bone.localScale = new Vector3(config.scale, config.scale, config.scale);
                    bone.localPosition = new Vector3(0f, config.heightOffset, 0f);
                    return;
                }
            }

            return;
        }
    }

    // --- §31B.4F: headwear fit (посадка головного убора) ---

    // Кость head самой вещи после сшивания: ParentConnection обнулил её позу,
    // и всё, что пишется сюда, едет НА теле как локальная поправка. Null у
    // причёсок (их head принадлежит ApplyHairFit), у вещей без такой кости и
    // до Construct. WardrobeTest двигает эту кость гизмо на живом экземпляре.
    public Transform HeadwearFitBone => _headwearFitBone;
    private Transform _headwearFitBone;

    public HeadwearFit GetHeadwearFit() => headwearFit;

    // Тот же контракт правки ПРЕФАБ-АССЕТА из тестовой сцены, что у
    // SetConfigScale ниже; персистится кнопкой «Сохранить» (SaveAssets).
    public void SetHeadwearFit(HeadwearFit value)
    {
        headwearFit = value;
    }

    // Применить текущую посадку к уже сшитой кости. Безопасно звать повторно —
    // панель делает это на каждое изменение поля/кадр драга гизмо.
    public void ApplyHeadwearFitNow()
    {
        if (_headwearFitBone == null)
        {
            return;
        }

        _headwearFitBone.localPosition = headwearFit.position;
        _headwearFitBone.localRotation = Quaternion.Euler(headwearFit.rotation);
        _headwearFitBone.localScale = headwearFit.Scale;
    }

    // Как ApplyHairFit: массив костей снят ДО сшивания, потому что head вещи
    // уже переподчинена телу и вниз от hip её больше не найти.
    private void CacheHeadwearFitBone(Transform[] garmentBones)
    {
        _headwearFitBone = null;
        if (slots.Count == 0)
        {
            return; // причёска: её head костью владеет ApplyHairFit
        }

        foreach (var bone in garmentBones)
        {
            if (bone != null && bone.name == "head")
            {
                _headwearFitBone = bone;
                return;
            }
        }
    }

    // --- Wardrobe test-scene support: per-actor fit-scale tuning ---

    // Read the authored fit scale for one actor (1 when no config exists).
    public float GetConfigScale(ActorName actor)
    {
        foreach (var config in configs)
        {
            if (config.actorName == actor)
            {
                return config.scale;
            }
        }

        return 1f;
    }

    // Правится ПРЯМО В ПРЕФАБЕ из тестовой сцены — тем же путём, что и подгонка
    // размера ниже: инспектор для этого пришлось бы открывать по одной вещи, а
    // решение «бельё или куртка» принимается, когда вещь надета и видна на
    // девушке рядом с остальными.
    public void SetLayer(VisualWearLayer value)
    {
        layer = value;
    }

    public void SetHidesHair(bool value)
    {
        hidesHair = value;
    }

    public void SetGender(VisualGender value)
    {
        gender = value;
    }

    // Write the fit scale for one actor; adds a config entry (mesh = shared
    // authored mesh) when the actor had none. Called on the PREFAB ASSET by
    // the wardrobe test scene, then persisted via AssetDatabase.SaveAssets.
    public void SetConfigScale(ActorName actor, float scale)
    {
        foreach (var config in configs)
        {
            if (config.actorName == actor)
            {
                config.scale = scale;
                return;
            }
        }

        configs.Add(new WearConfig { actorName = actor, scale = scale, mesh = null });
    }

    // Hair height offset for one actor, in metres along the head bone's local Y
    // (0 when no config exists). Same PREFAB-ASSET editing contract as the
    // scale pair above — see ApplyHairFit for what it moves.
    public float GetConfigHeight(ActorName actor)
    {
        foreach (var config in configs)
        {
            if (config.actorName == actor)
            {
                return config.heightOffset;
            }
        }

        return 0f;
    }

    public void SetConfigHeight(ActorName actor, float heightOffset)
    {
        foreach (var config in configs)
        {
            if (config.actorName == actor)
            {
                config.heightOffset = heightOffset;
                return;
            }
        }

        configs.Add(new WearConfig { actorName = actor, heightOffset = heightOffset, mesh = null });
    }

    public bool HeedHideUnderwearSlot(VisualWearSlot slot)
    {
        return noHideUnderwearSlots.Contains(slot) == false;
    }

    public IReadOnlyList<VisualWearSlot> NoHideUnderwearSlots => noHideUnderwearSlots;

    /// <summary>Скрывает ли эта вещь одежду слоя Wear в указанном слоте.</summary>
    /// <remarks>
    /// Это независимая авторская маска для Outerwear. По умолчанию список
    /// пуст: футболка, штаны и другая одежда под курткой остаются видимыми.
    /// </remarks>
    public bool HidesWearSlot(VisualWearSlot slot)
    {
        return hideWearSlots.Contains(slot);
    }

    public IReadOnlyList<VisualWearSlot> HideWearSlots => hideWearSlots;

    public void SetHideWear(VisualWearSlot slot, bool hide)
    {
        if (hide)
        {
            if (!hideWearSlots.Contains(slot))
            {
                hideWearSlots.Add(slot);
            }
        }
        else
        {
            hideWearSlots.Remove(slot);
        }
    }

    /// <summary>Скрывать ли бельё в этом слоте (правится из тестовой сцены).</summary>
    /// <remarks>
    /// Список хранит ИСКЛЮЧЕНИЯ, а не правила: по умолчанию верхняя вещь бельё
    /// под собой прячет, и сюда попадают слоты, где этого делать не надо —
    /// прозрачная блузка, сетчатые чулки, распахнутая куртка. Поэтому «включено»
    /// в панели значит «бельё видно», то есть слот В списке.
    /// </remarks>
    public void SetHideUnderwear(VisualWearSlot slot, bool hide)
    {
        if (hide)
        {
            noHideUnderwearSlots.Remove(slot);
        }
        else if (!noHideUnderwearSlots.Contains(slot))
        {
            noHideUnderwearSlots.Add(slot);
        }
    }

    public void Hide()
    {
        _meshRenderer ??= GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (_meshRenderer != null)
        {
            _meshRenderer.enabled = false;
        }
    }

    public void Show()
    {
        _meshRenderer ??= GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (_meshRenderer != null)
        {
            _meshRenderer.enabled = true;
        }
    }

    // Spec 40.10: erode a worn-out garment. durability01 (1 = pristine, 0 =
    // rags) drives the HexLive/GarmentTear dissolve: procedural Voronoi holes
    // grow with ragged frayed edges, skin/underlayers show through. The tear
    // shader is swapped in LAZILY the first time wear actually bites, so
    // pristine clothes and hair keep their original material untouched
    // (same-named URP Lit properties carry over on the swap). TUNING KNOB:
    // TearBiteDurability — wear starts showing below this durability.
    // Kept below the UI's "light wear" band so the cloth no longer looks
    // shredded while the HP bar is still mostly green.
    private const float TearBiteDurability = 0.72f;
    private const float TearProgressGamma = 1.35f;
    private const int MaxDamageSpheres = 8;
    // Spec 40.10-D: holes + dirt PAINTED into per-garment textures (UV-stable
    // — the world-space sphere clip breathed with the bones and flickered).
    // Flip off to fall back to the fully procedural shader path.
    private const bool PaintWearIntoTexture = true;
    private GarmentWearPainter _wearPainter;
    private int _paintSeed;
    private static readonly int TearAmountId = Shader.PropertyToID("_TearAmount");
    private static readonly int TearMaskTexId = Shader.PropertyToID("_TearMaskTex");
    private static readonly int TearTexOnId = Shader.PropertyToID("_TearTexOn");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorMapId = Shader.PropertyToID("_BaseColorMap");
    private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
    private static readonly int AlphaCutoffId = Shader.PropertyToID("_AlphaCutoff");
    private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");
    private static readonly int AlphaCutoffEnableId = Shader.PropertyToID("_AlphaCutoffEnable");
    private static readonly int AlphaClipOnId = Shader.PropertyToID("_AlphaClipOn");
    private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
    private static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");
    private static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");
    private static readonly int NormalScaleId = Shader.PropertyToID("_NormalScale");
    private static readonly int MetallicGlossMapId = Shader.PropertyToID("_MetallicGlossMap");
    private static readonly int MaskMapId = Shader.PropertyToID("_MaskMap");
    private static readonly int MaskMapOnId = Shader.PropertyToID("_MaskMapOn");
    private static readonly int OcclusionMapId = Shader.PropertyToID("_OcclusionMap");
    private static readonly int OcclusionMapOnId = Shader.PropertyToID("_OcclusionMapOn");
    private static readonly int DetailMaskId = Shader.PropertyToID("_DetailMask");
    private static readonly int DetailAlbedoMapId = Shader.PropertyToID("_DetailAlbedoMap");
    private static readonly int DetailAlbedoMapOnId = Shader.PropertyToID("_DetailAlbedoMapOn");
    private static readonly int DetailNormalMapId = Shader.PropertyToID("_DetailNormalMap");
    private static readonly int DetailNormalMapOnId = Shader.PropertyToID("_DetailNormalMapOn");
    private static Shader _tearShader;
    private static bool _tearShaderSearched;
    private MaterialPropertyBlock _wearMpb;
    private bool _tearShaderApplied;
    private float _tear;
    private float _dirt;
    private float _blood;
    // Spec 40.8-G: blood soak is localized by HURT ZONE names (the painter
    // looks them up in its baked point map) — the old world-space spheres
    // forced a garment-mesh re-bake whenever the animated bones moved.
    private readonly string[] _damageZones = new string[MaxDamageSpheres];
    private readonly float[] _damageStrengths = new float[MaxDamageSpheres];
    private int _damageZoneCount;

    // Spec 35.5: rendering-layer bit for cloth — rain droplet projectors land
    // here (wounds/dirt/sweat decals stay skin-only).
    public const uint ClothDecalLayer = 1u << 2;

    // Spec 35.5: wet cloth — glossier and darker while soaked, back to the
    // authored look as it dries. Dry base values are captured from the shared
    // material before any tint, so wet=0 restores the exact original state.
    private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private float _wet;
    private bool _wetTouched;
    // PER SLOT: a garment's colour often lives in a slot's _BaseColor (the
    // blue tank = off-white texture × blue tint on slot 1, white trim on
    // slot 0). A renderer-wide block stomped every slot with slot 0's colour
    // and turned the whole shirt grey — capture and write per material index.
    private float[] _drySmoothnessPerSlot;
    private Color[] _dryColorPerSlot;

    public void SetWetness(float wet01)
    {
        var wet = Mathf.Clamp01(wet01);
        if (Mathf.Approximately(wet, _wet))
        {
            return;
        }

        _wet = wet;
        _wetTouched = true;
        PushCondition();
    }

    private void CaptureDryLook()
    {
        if (_drySmoothnessPerSlot != null || _meshRenderer == null)
        {
            return;
        }

        var materials = _meshRenderer.sharedMaterials;
        _drySmoothnessPerSlot = new float[materials.Length];
        _dryColorPerSlot = new Color[materials.Length];
        for (var i = 0; i < materials.Length; i++)
        {
            _drySmoothnessPerSlot[i] = materials[i] != null && materials[i].HasProperty(SmoothnessId)
                ? materials[i].GetFloat(SmoothnessId)
                : 0.3f;
            _dryColorPerSlot[i] = materials[i] != null && materials[i].HasProperty(BaseColorId)
                ? materials[i].GetColor(BaseColorId)
                : Color.white;
        }
    }

    public void SetErosion(float durability01)
    {
        // 0 at the bite threshold, 1 at rags (near-fully dissolved).
        var rawTear = Mathf.InverseLerp(TearBiteDurability, 0f, Mathf.Clamp01(durability01));
        _tear = Mathf.Pow(rawTear, TearProgressGamma);
        PushCondition();
    }

    // Spec 40.10-C: dirt (1 − hygiene) + hurt zones (name + strength), shared
    // by all the NPC's garments. Zones ONLY localize the blood soak over
    // fresh wounds — they never rip holes: clothing damage is tracked
    // separately (garment durability drives tear).
    public void SetGrime(float dirt01, string[] zones, float[] strengths, int count,
        float blood01 = 0f, float sweat01 = 0f)
    {
        _dirt = Mathf.Clamp01(dirt01);
        // Spec 40.8-C: blood soaks the cloth over the wound (localized by the
        // hurt zones below). Sweat stays in the skin/wetness path.
        _blood = Mathf.Clamp01(blood01);
        _ = sweat01;
        _damageZoneCount = Mathf.Min(count, MaxDamageSpheres);
        for (var i = 0; i < _damageZoneCount; i++)
        {
            _damageZones[i] = zones[i];
            _damageStrengths[i] = strengths[i];
        }

        PushCondition();
    }

    private void PushCondition()
    {
        if (_meshRenderer == null)
        {
            return;
        }

        // Pristine, clean, unhurt: leave the original material alone (the
        // dirt gate skips pointless swaps for barely-visible smudges).
        // Wetness alone does NOT swap in the tear shader — it only rides the
        // property block (works on the original URP Lit material too).
        // Damage spheres alone do NOT trigger the swap: wounds never rip
        // cloth, spheres only localize blood (gated by _blood itself).
        var tearActive = _tear > 0f;
        var paintActive = _dirt > 0.05f || _blood > 0.05f;
        var active = tearActive || paintActive || _wet > 0.01f || _wetTouched;
        if (!active && !_tearShaderApplied && _wearPainter == null)
        {
            return;
        }

        // Capture the authored per-slot look BEFORE the tear swap.
        CaptureDryLook();

        if (tearActive)
        {
            ApplyTearShader();
        }

        // Spec 40.10-D: the painter stamps holes/dirt into textures; the
        // shader then must not ALSO rip via world spheres (flicker) or paint
        // its procedural dust (double filth). Blood soak keeps the spheres.
        if (PaintWearIntoTexture && (tearActive || paintActive || _wearPainter != null))
        {
            if (_wearPainter == null)
            {
                _wearPainter = gameObject.AddComponent<GarmentWearPainter>();
                _wearPainter.Construct(_meshRenderer, _paintSeed);
            }

            // Dirt and blood are SEPARATE stain layers with their own UV
            // spots — both draw at full strength (the old dust = dirt − blood
            // suppression made bloodied cloth lose its grime and was retired).
            _wearPainter.SetState(_tear, _dirt, _blood, _damageZones, _damageStrengths,
                _damageZoneCount);
        }

        // Per material slot: tear/dirt/spheres are shared, but smoothness and
        // colour restore each slot's OWN dry values (a renderer-wide block
        // used slot 0's white for everything and greyed the blue tank body).
        _wearMpb ??= new MaterialPropertyBlock();
        var slotCount = _drySmoothnessPerSlot?.Length ?? 0;
        for (var i = 0; i < slotCount; i++)
        {
            _meshRenderer.GetPropertyBlock(_wearMpb, i);
            _wearMpb.SetFloat(TearAmountId, _tear);
            // Spec 35.5: soaked cloth shines and darkens; dry restores base.
            // 0.72 matches the wet SKIN gloss — 0.85 here while the body wore
            // 0.72 read as mismatched smoothness across materials.
            _wearMpb.SetFloat(SmoothnessId, Mathf.Lerp(_drySmoothnessPerSlot[i], 0.72f, _wet));
            var soaked = Color.Lerp(_dryColorPerSlot[i], _dryColorPerSlot[i] * 0.6f, _wet);
            soaked.a = _dryColorPerSlot[i].a;
            _wearMpb.SetColor(BaseColorId, soaked);
            _meshRenderer.SetPropertyBlock(_wearMpb, i);
        }
    }

    private void ApplyTearShader()
    {
        if (_tearShaderApplied)
        {
            return;
        }

        _tearShaderApplied = true;
        if (!_tearShaderSearched)
        {
            _tearShaderSearched = true;
            _tearShader = Shader.Find("HexLive/GarmentTear");
        }

        if (_tearShader == null)
        {
            // Build-stripping guard: HexLive/GarmentTear must live in Always
            // Included Shaders (Shader.Find-only shaders vanish from builds).
            Debug.LogWarning(
                "[Wear] HexLive/GarmentTear shader not found — garment tear disabled. " +
                "Check Project Settings ▸ Graphics ▸ Always Included Shaders.", this);
            return;
        }

        // .materials instantiates per-garment copies, so each girl's shirt
        // tears independently; _BaseMap/_BaseColor/_BumpMap survive the swap.
        // The artistic dissolve mask (fal.ai ragged holes at varied depths)
        // replaces the procedural Voronoi when present — one mask serves
        // every garment type, since it lives in generic UV space.
        // TRANSPARENT slots (stockings, sheer sleeves) keep their authored
        // shader: GarmentTear is opaque alpha-test and turned sheer fabric
        // solid black. Their holes are punched into the albedo ALPHA by the
        // wear painter instead — the original shader blends them out.
        var tearMask = Resources.Load<Texture2D>("HexLive/Decals/tear_mask");
        foreach (var material in _meshRenderer.materials)
        {
            if (IsTransparentMaterial(material))
            {
                continue;
            }

            var baseMap = GetTextureOrNull(material, BaseMapId) ??
                GetTextureOrNull(material, BaseColorMapId);
            var cutoff = material.HasProperty(CutoffId)
                ? material.GetFloat(CutoffId)
                : material.HasProperty(AlphaCutoffId)
                    ? material.GetFloat(AlphaCutoffId)
                    : 0.5f;
            var alphaClipOn =
                material.HasProperty(AlphaClipId) && material.GetFloat(AlphaClipId) > 0.5f ||
                material.HasProperty(AlphaCutoffEnableId) && material.GetFloat(AlphaCutoffEnableId) > 0.5f;
            var bumpMap = GetTextureOrNull(material, BumpMapId) ??
                GetTextureOrNull(material, NormalMapId);
            var bumpScale = material.HasProperty(BumpScaleId)
                ? material.GetFloat(BumpScaleId)
                : material.HasProperty(NormalScaleId)
                    ? material.GetFloat(NormalScaleId)
                    : 1f;
            var metallicGloss = GetTextureOrNull(material, MetallicGlossMapId);
            var maskMap = GetTextureOrNull(material, MaskMapId);
            var occlusion = GetTextureOrNull(material, OcclusionMapId);
            var detailMask = GetTextureOrNull(material, DetailMaskId);
            var detailAlbedo = GetTextureOrNull(material, DetailAlbedoMapId);
            var detailNormal = GetTextureOrNull(material, DetailNormalMapId);

            material.shader = _tearShader;
            if (baseMap != null) material.SetTexture(BaseMapId, baseMap);
            material.SetFloat(CutoffId, cutoff);
            material.SetFloat(AlphaClipOnId, alphaClipOn ? 1f : 0f);
            if (bumpMap != null) material.SetTexture(BumpMapId, bumpMap);
            material.SetFloat(BumpScaleId, bumpScale);
            if (metallicGloss != null) material.SetTexture(MetallicGlossMapId, metallicGloss);
            if (maskMap != null) material.SetTexture(MaskMapId, maskMap);
            material.SetFloat(MaskMapOnId, maskMap != null ? 1f : 0f);
            if (occlusion != null) material.SetTexture(OcclusionMapId, occlusion);
            material.SetFloat(OcclusionMapOnId, occlusion != null ? 1f : 0f);
            if (detailMask != null) material.SetTexture(DetailMaskId, detailMask);
            if (detailAlbedo != null) material.SetTexture(DetailAlbedoMapId, detailAlbedo);
            material.SetFloat(DetailAlbedoMapOnId, detailAlbedo != null ? 1f : 0f);
            if (detailNormal != null) material.SetTexture(DetailNormalMapId, detailNormal);
            material.SetFloat(DetailNormalMapOnId, detailNormal != null ? 1f : 0f);

            if (tearMask != null)
            {
                material.SetTexture(TearMaskTexId, tearMask);
                material.SetFloat(TearTexOnId, 1f);
            }
        }
    }

    private static Texture GetTextureOrNull(Material material, int id)
    {
        return material != null && material.HasProperty(id)
            ? material.GetTexture(id)
            : null;
    }

    // URP Lit convention: _Surface 1 = Transparent; high queues too.
    public static bool IsTransparentMaterial(Material material)
    {
        if (material == null)
        {
            return false;
        }

        if (material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f)
        {
            return true;
        }

        return material.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private Transform FindHip()
    {
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "hip")
            {
                return t;
            }
        }

        return null;
    }
}

}
