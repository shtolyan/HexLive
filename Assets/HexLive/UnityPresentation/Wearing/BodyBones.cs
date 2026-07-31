using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Adapted from molly_copy Wearing.BodyBones (spec 31B.3): same serialized
// field names (hip / wearTransform / hair / genitals — the .meta claims the
// original GUID so actor prefab data binds directly). Equip/TakeOff keep
// the source's layer-and-slot bookkeeping including underwear auto-hiding;
// gender checks, events, and nudity toggling are gone — the base body stays
// as authored and hair is just another Wear spawned on Construct.
public sealed class BodyBones : MonoBehaviour
{
    [SerializeField] private Transform hip;
    [SerializeField] private Transform wearTransform;
    [SerializeField] private Wear hair;
    [SerializeField] private GameObject genitals;

    private readonly Dictionary<string, Transform> _bonesMap = new();
    private readonly Dictionary<string, Wear> _wears = new();
    private readonly Dictionary<VisualWearLayer, Dictionary<VisualWearSlot, Wear>> _byLayer = new();
    private readonly Dictionary<Wear, string> _wearKeys = new();
    private ActorName _actorMesh;

    public Transform WearTransform => wearTransform;

    // Spec §52.8: the skeleton root. Wear.Construct stitches a garment's bones
    // ONTO these body bones (ParentConnection re-parents them out of
    // wearTransform), so a holster's tool.* anchors — children of its leg bones
    // — end up under here, not under wearTransform. SyncHolster searches this.
    public Transform SkeletonRoot => hip;

    public void Construct(ActorName actorMesh)
    {
        _actorMesh = actorMesh;
        _bonesMap.Clear();
        _wears.Clear();
        _wearKeys.Clear();
        _byLayer[VisualWearLayer.Underwear] = new Dictionary<VisualWearSlot, Wear>();
        _byLayer[VisualWearLayer.Wear] = new Dictionary<VisualWearSlot, Wear>();
        _byLayer[VisualWearLayer.Outerwear] = new Dictionary<VisualWearSlot, Wear>();

        if (genitals != null)
        {
            genitals.SetActive(false);
        }

        foreach (var bone in hip.GetComponentsInChildren<Transform>(true))
        {
            if (_bonesMap.ContainsKey(bone.name) == false)
            {
                _bonesMap.Add(bone.name, bone);
            }
        }

        if (hair != null)
        {
            var spawned = Instantiate(hair, wearTransform);
            spawned.Construct(_actorMesh, this, "hair");
            // Hair must never catch SKIN-layer decals (dirt/sweat grain in the
            // strands): imported prefabs ship odd rendering-layer masks (257),
            // so pin every hair renderer to the cloth bit explicitly.
            foreach (var renderer in spawned.GetComponentsInChildren<Renderer>(true))
            {
                renderer.renderingLayerMask = Wear.ClothDecalLayer;
            }
        }
    }

    public Transform GetBone(string boneName)
    {
        return _bonesMap.TryGetValue(boneName, out var bone) ? bone : null;
    }

    public bool IsEquipped(string key)
    {
        return _wears.ContainsKey(key);
    }

    // Spec 40.10-D guard: which garments currently own this prefab's (layer, slot)
    // claims — i.e. whoever would evict it on Equip. Empty when its slots are free.
    public string DescribeSlotOwners(Wear wearPrefab)
    {
        if (wearPrefab == null || !_byLayer.TryGetValue(wearPrefab.Layer, out var layerDict))
        {
            return string.Empty;
        }

        string owners = null;
        foreach (var slot in wearPrefab.Slots)
        {
            if (layerDict.TryGetValue(slot, out var occupant) && occupant != null &&
                _wearKeys.TryGetValue(occupant, out var key))
            {
                owners = owners == null ? $"{key}@{slot}" : $"{owners}, {key}@{slot}";
            }
        }

        return owners ?? string.Empty;
    }

    // Spec 40.10: erode every visual garment mapped from a sim item (a sim item
    // can map to several keys "defId#0", "defId#1", …) by its durability.
    // Dev seam (WardrobeTest): erode every worn garment at once.
    public void SetWearErosion(float durability01)
    {
        foreach (var pair in _wears)
        {
            pair.Value.SetErosion(durability01);
        }
    }

    public void SetWearErosion(string defId, float durability01)
    {
        var prefix = defId + "#";
        foreach (var pair in _wears)
        {
            if (pair.Key.StartsWith(prefix))
            {
                pair.Value.SetErosion(durability01);
            }
        }
    }

    // Spec 35.5: wet sheen for every visual garment of one SIM item — rain
    // soaks it, fire/rack dries it back to the authored look.
    public void SetWearWetness(string defId, float wet01)
    {
        var prefix = defId + "#";
        foreach (var pair in _wears)
        {
            if (pair.Key.StartsWith(prefix))
            {
                pair.Value.SetWetness(wet01);
            }
        }
    }

    // Spec 40.10-C: dirt + hurt zones (name + strength) for every SIM garment
    // (spec 40.8-G: zone names replaced the world-space damage spheres — the
    // painter resolves them through its baked point map). Hair is
    // instantiated directly in Construct (never in _wears), so it stays clean.
    public void SetWearGrime(float dirt01, string[] zones, float[] strengths, int count,
        float blood01 = 0f, float sweat01 = 0f)
    {
        foreach (var pair in _wears)
        {
            pair.Value.SetGrime(dirt01, zones, strengths, count, blood01, sweat01);
        }
    }

    public void SetWearGrime(string defId, float dirt01, string[] zones, float[] strengths,
        int count, float blood01 = 0f)
    {
        var prefix = defId + "#";
        foreach (var pair in _wears)
        {
            if (pair.Key.StartsWith(prefix))
            {
                pair.Value.SetGrime(dirt01, zones, strengths, count, blood01);
            }
        }
    }

    // key = sim item definition id + index (a sim item may map to several
    // visual garments, each equipped under its own key).
    public void Equip(string key, Wear wearPrefab)
    {
        if (_wears.ContainsKey(key))
        {
            return;
        }

        var layerDict = _byLayer[wearPrefab.Layer];
        var underwear = _byLayer[VisualWearLayer.Underwear];
        var newWear = Instantiate(wearPrefab, wearTransform);
        newWear.Construct(_actorMesh, this, key);

        foreach (var slot in newWear.Slots)
        {
            // One garment per (layer, slot) — the old one comes off.
            if (layerDict.TryGetValue(slot, out var conflicting))
            {
                TakeOff(_wearKeys[conflicting]);
            }

            if (wearPrefab.Layer != VisualWearLayer.Underwear)
            {
                if (newWear.HeedHideUnderwearSlot(slot) && underwear.TryGetValue(slot, out var under))
                {
                    under.Hide();
                }
            }
            else
            {
                // Dressing underwear beneath already-worn outer layers.
                if (_byLayer[VisualWearLayer.Wear].TryGetValue(slot, out var outer1) &&
                    outer1.HeedHideUnderwearSlot(slot))
                {
                    newWear.Hide();
                }

                if (_byLayer[VisualWearLayer.Outerwear].TryGetValue(slot, out var outer2) &&
                    outer2.HeedHideUnderwearSlot(slot))
                {
                    newWear.Hide();
                }
            }

            layerDict[slot] = newWear;
        }

        _wears[key] = newWear;
        _wearKeys[newWear] = key;
    }

    public void TakeOff(string key)
    {
        if (_wears.TryGetValue(key, out var wear) == false)
        {
            return;
        }

        var layerDict = _byLayer[wear.Layer];
        var wearLayer = _byLayer[VisualWearLayer.Wear];
        var underwear = _byLayer[VisualWearLayer.Underwear];

        foreach (var slot in wear.Slots)
        {
            if (layerDict.TryGetValue(slot, out var occupant) && occupant == wear)
            {
                layerDict.Remove(slot);
            }

            // Whatever underwear was hidden beneath becomes visible again,
            // unless another outer garment still covers that slot.
            if (wearLayer.TryGetValue(slot, out var stillOn) && stillOn.HeedHideUnderwearSlot(slot))
            {
                continue;
            }

            if (underwear.TryGetValue(slot, out var under))
            {
                under.Show();
            }
        }

        Destroy(wear.gameObject);
        _wears.Remove(key);
        _wearKeys.Remove(wear);
    }

    // Debug: hide every equipped garment (skin inspection) / show them back.
    // Restore re-applies the layer rules: underwear stays hidden wherever a
    // worn outer garment covers its slot — no bras popping through tops.
    public void SetAllWearsVisible(bool visible)
    {
        foreach (var wear in _wears.Values)
        {
            if (!visible)
            {
                wear.Hide();
                continue;
            }

            if (wear.Layer != VisualWearLayer.Underwear)
            {
                wear.Show();
                continue;
            }

            var hiddenByOuter = false;
            foreach (var slot in wear.Slots)
            {
                if (_byLayer[VisualWearLayer.Wear].TryGetValue(slot, out var outer1) &&
                    outer1.HeedHideUnderwearSlot(slot))
                {
                    hiddenByOuter = true;
                    break;
                }

                if (_byLayer[VisualWearLayer.Outerwear].TryGetValue(slot, out var outer2) &&
                    outer2.HeedHideUnderwearSlot(slot))
                {
                    hiddenByOuter = true;
                    break;
                }
            }

            if (hiddenByOuter)
            {
                wear.Hide();
            }
            else
            {
                wear.Show();
            }
        }
    }

    public void TakeOffAll()
    {
        var keys = new List<string>(_wears.Keys);
        foreach (var key in keys)
        {
            TakeOff(key);
        }
    }
}

}
