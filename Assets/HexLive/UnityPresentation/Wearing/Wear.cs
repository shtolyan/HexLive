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
    private const float TearBiteDurability = 0.6f;
    private const int MaxDamageSpheres = 8;
    private static readonly int TearAmountId = Shader.PropertyToID("_TearAmount");
    private static readonly int DirtAmountId = Shader.PropertyToID("_DirtAmount");
    private static readonly int DamageSphereCountId = Shader.PropertyToID("_DamageSphereCount");
    private static readonly int DamageSpheresId = Shader.PropertyToID("_DamageSpheres");
    private static Shader _tearShader;
    private static bool _tearShaderSearched;
    private MaterialPropertyBlock _wearMpb;
    private bool _tearShaderApplied;
    private float _tear;
    private float _dirt;
    private readonly Vector4[] _damageSpheres = new Vector4[MaxDamageSpheres];
    private int _damageSphereCount;

    public void SetErosion(float durability01)
    {
        // 0 at the bite threshold, 1 at rags (near-fully dissolved).
        _tear = Mathf.InverseLerp(TearBiteDurability, 0f, Mathf.Clamp01(durability01));
        PushCondition();
    }

    // Spec 40.10-C: dirt (1 − hygiene) + world-space damage spheres (xyz =
    // bone anchor, w = strength), shared by all the NPC's garments — a sphere
    // only bites fragments within _DamageRadius, so the wound zone maps to
    // the covering garment spatially, no UV knowledge needed.
    public void SetGrime(float dirt01, Vector4[] spheres, int count)
    {
        _dirt = Mathf.Clamp01(dirt01);
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
        var active = _tear > 0f || _dirt > 0.15f || _damageSphereCount > 0;
        if (!active && !_tearShaderApplied)
        {
            return;
        }

        ApplyTearShader();

        _wearMpb ??= new MaterialPropertyBlock();
        _meshRenderer.GetPropertyBlock(_wearMpb);
        _wearMpb.SetFloat(TearAmountId, _tear);
        _wearMpb.SetFloat(DirtAmountId, _dirt);
        _wearMpb.SetFloat(DamageSphereCountId, _damageSphereCount);
        _wearMpb.SetVectorArray(DamageSpheresId, _damageSpheres);
        _meshRenderer.SetPropertyBlock(_wearMpb);
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
        foreach (var material in _meshRenderer.materials)
        {
            material.shader = _tearShader;
        }
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
