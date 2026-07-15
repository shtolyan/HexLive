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
    [SerializeField] private VisualWearLayer layer;
    [SerializeField] private VisualGender gender = VisualGender.Female;

    public VisualWearLayer Layer => layer;

    // Serialization contract with the imported prefabs — read back so the
    // compiler (and future gender-aware wardrobes) see it used.
    public VisualGender Gender => gender;

    public IReadOnlyList<VisualWearSlot> Slots => slots;

    private SkinnedMeshRenderer _meshRenderer;

    public void Construct(ActorName actorMesh, BodyBones bodyBones)
    {
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
                hip.localScale = new Vector3(config.scale, config.scale, config.scale);
                if (config.mesh != null)
                {
                    _meshRenderer.sharedMesh = config.mesh;
                }

                break;
            }
        }

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

    public bool HeedHideUnderwearSlot(VisualWearSlot slot)
    {
        return noHideUnderwearSlots.Contains(slot) == false;
    }

    public void Hide()
    {
        if (_meshRenderer != null)
        {
            _meshRenderer.enabled = false;
        }
    }

    public void Show()
    {
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
    private const float DamageHoleRevealFloor = 0.08f;
    private const int MaxDamageSpheres = 8;
    // Spec 40.10-D: holes + dirt PAINTED into per-garment textures (UV-stable
    // — the world-space sphere clip breathed with the bones and flickered).
    // Flip off to fall back to the fully procedural shader path.
    private const bool PaintWearIntoTexture = true;
    private GarmentWearPainter _wearPainter;
    private static readonly int SphereTearOnId = Shader.PropertyToID("_SphereTearOn");
    private static readonly int TearAmountId = Shader.PropertyToID("_TearAmount");
    private static readonly int TearMaskTexId = Shader.PropertyToID("_TearMaskTex");
    private static readonly int TearTexOnId = Shader.PropertyToID("_TearTexOn");
    private static readonly int DirtAmountId = Shader.PropertyToID("_DirtAmount");
    private static readonly int DamageSphereCountId = Shader.PropertyToID("_DamageSphereCount");
    private static readonly int DamageSpheresId = Shader.PropertyToID("_DamageSpheres");
    private static Shader _tearShader;
    private static bool _tearShaderSearched;
    private MaterialPropertyBlock _wearMpb;
    private bool _tearShaderApplied;
    private float _tear;
    private float _dirt;
    private float _blood;
    private float _sweat;
    private static readonly int BloodAmountId = Shader.PropertyToID("_BloodAmount");
    private static readonly int SweatAmountId = Shader.PropertyToID("_SweatAmount");
    private readonly Vector4[] _damageSpheres = new Vector4[MaxDamageSpheres];
    private int _damageSphereCount;

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

    // Spec 40.10-C: dirt (1 − hygiene) + world-space damage spheres (xyz =
    // bone anchor, w = strength), shared by all the NPC's garments — a sphere
    // only bites fragments within _DamageRadius, so the wound zone maps to
    // the covering garment spatially, no UV knowledge needed.
    public void SetGrime(float dirt01, Vector4[] spheres, int count,
        float blood01 = 0f, float sweat01 = 0f)
    {
        _dirt = Mathf.Clamp01(dirt01);
        // Spec 40.8-C: blood soaks the cloth over the wound (localized by the
        // damage spheres below); sweat damps it in patches with a sheen.
        _blood = Mathf.Clamp01(blood01);
        _sweat = Mathf.Clamp01(sweat01);
        _damageSphereCount = Mathf.Min(count, MaxDamageSpheres);
        for (var i = 0; i < _damageSphereCount; i++)
        {
            _damageSpheres[i] = spheres[i];
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
        var tearActive = _tear > 0f || _dirt > 0.15f || _damageSphereCount > 0 ||
            _blood > 0.05f || _sweat > 0.25f;
        var active = tearActive || _wet > 0.01f || _wetTouched;
        if (!active && !_tearShaderApplied)
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
        if (PaintWearIntoTexture && _tearShaderApplied)
        {
            if (_wearPainter == null)
            {
                _wearPainter = gameObject.AddComponent<GarmentWearPainter>();
                _wearPainter.Construct(_meshRenderer);
            }

            _wearPainter.SetState(_tear, _dirt, _damageSpheres, _damageSphereCount);
        }

        // Painted bite holes are near-black in the mask: a small floor tear
        // amount clips them open even while overall durability is high.
        var effectiveTear = PaintWearIntoTexture && _wearPainter != null && _wearPainter.HasDamageHoles
            ? Mathf.Max(_tear, DamageHoleRevealFloor)
            : _tear;

        // Per material slot: tear/dirt/spheres are shared, but smoothness and
        // colour restore each slot's OWN dry values (a renderer-wide block
        // used slot 0's white for everything and greyed the blue tank body).
        _wearMpb ??= new MaterialPropertyBlock();
        var slotCount = _drySmoothnessPerSlot?.Length ?? 0;
        for (var i = 0; i < slotCount; i++)
        {
            _meshRenderer.GetPropertyBlock(_wearMpb, i);
            _wearMpb.SetFloat(TearAmountId, effectiveTear);
            _wearMpb.SetFloat(DirtAmountId, PaintWearIntoTexture ? 0f : _dirt);
            _wearMpb.SetFloat(SphereTearOnId, PaintWearIntoTexture ? 0f : 1f);
            _wearMpb.SetFloat(BloodAmountId, _blood);
            _wearMpb.SetFloat(SweatAmountId, _sweat);
            _wearMpb.SetFloat(DamageSphereCountId, _damageSphereCount);
            _wearMpb.SetVectorArray(DamageSpheresId, _damageSpheres);
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
            return; // shader missing: erosion silently no-ops
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

            material.shader = _tearShader;
            if (tearMask != null)
            {
                material.SetTexture(TearMaskTexId, tearMask);
                material.SetFloat(TearTexOnId, 1f);
            }
        }
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
