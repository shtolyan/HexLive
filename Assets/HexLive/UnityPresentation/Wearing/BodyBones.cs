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
    private Wear _hairInstance;
    private readonly List<GameObject> _hairBones = new();

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
        // По одному словарю на слой, перечислением — иначе новый слой пришлось
        // бы вспомнить дописать сюда, а забытый обрушил бы Equip на первой же
        // сумке (`_byLayer[layer]` без ключа — это исключение, а не пустота).
        foreach (VisualWearLayer layer in System.Enum.GetValues(typeof(VisualWearLayer)))
        {
            _byLayer[layer] = new Dictionary<VisualWearSlot, Wear>();
        }

        UpdateGenitals();
        RefreshHeel();

        foreach (var bone in hip.GetComponentsInChildren<Transform>(true))
        {
            if (_bonesMap.ContainsKey(bone.name) == false)
            {
                _bonesMap.Add(bone.name, bone);
            }
        }

        // A fresh body owns no hair yet — anything from a previous Construct
        // died with the old GameObject.
        _hairInstance = null;
        _hairBones.Clear();
        SetHair(hair);
    }

    // The hairstyle authored on this actor prefab (spec §31B.4B). Dev tools
    // read it to show which one is the default.
    public Wear DefaultHair => hair;

    // The LIVE hairstyle — the one actually on her head right now. Dev tools
    // need it to repaint hair without respawning the body: a hair colour is the
    // same mesh with a different map, so swapping materials on this instance is
    // the whole operation. Null when bald.
    public Wear HairInstance => _hairInstance;

    // Spec §31B.4B: swap the hairstyle on a LIVE body; null = bald. Hair is
    // not a wardrobe item — it owns no slot, never goes through Equip, and
    // only ever has one instance — so it gets its own seam instead of riding
    // the _wears map. Used by Construct and by the WardrobeTest tool.
    public void SetHair(Wear hairPrefab)
    {
        // Wear.Construct re-parents a garment's bones ONTO the body skeleton
        // (ParentConnection), so the hair's bones do NOT stay under the hair
        // root — destroying the root alone strands them under the body and
        // every swap piles up another dead skeleton. Kill them explicitly.
        foreach (var bone in _hairBones)
        {
            if (bone != null)
            {
                Destroy(bone);
            }
        }

        _hairBones.Clear();

        if (_hairInstance != null)
        {
            Destroy(_hairInstance.gameObject);
            _hairInstance = null;
        }

        if (hairPrefab == null)
        {
            return;
        }

        _hairInstance = Instantiate(hairPrefab, wearTransform);

        // Snapshot the bone subtree BEFORE Construct scatters it across the body.
        foreach (var t in _hairInstance.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "hip")
            {
                foreach (var bone in t.GetComponentsInChildren<Transform>(true))
                {
                    _hairBones.Add(bone.gameObject);
                }

                break;
            }
        }

        _hairInstance.Construct(_actorMesh, this, "hair");
        // Причёску можно сменить, не снимая шапки — новая должна остаться под
        // ней, а не выскочить наружу.
        RefreshHairVisibility();

        // Hair must never catch SKIN-layer decals (dirt/sweat grain in the
        // strands): imported prefabs ship odd rendering-layer masks (257),
        // so pin every hair renderer to the cloth bit explicitly.
        foreach (var renderer in _hairInstance.GetComponentsInChildren<Renderer>(true))
        {
            renderer.renderingLayerMask = Wear.ClothDecalLayer;
        }
    }

    public Transform GetBone(string boneName)
    {
        return _bonesMap.TryGetValue(boneName, out var bone) ? bone : null;
    }

    // --- §31B.4C: heels -----------------------------------------------------
    // The tallest heel currently worn. Recomputed when the wardrobe changes
    // rather than scanned every frame — LateUpdate runs on every dressed body
    // in the colony, so it must stay a field read.
    private HeelPose _heel;
    private float _heelPoseWeight = 1f;
    private float _heelPoseTarget = 1f;
    private const float HeelPoseTransitionSeconds = 0.12f;

    public float HeelPoseWeight => _heelPoseWeight;

    // §31.10A: the live skeleton owns this world-space lift, but portrait
    // clones must remove the copied offset before evaluating their neutral
    // studio pose. Expose the actual blended value, not the authored maximum:
    // a clone captured while sitting/lying can be midway through suppression.
    public float AppliedHeelLift => _heel.Any ? _heel.lift * _heelPoseWeight : 0f;

    public void SetHeelPoseSuppressed(bool suppressed)
    {
        _heelPoseTarget = suppressed ? 0f : 1f;
    }

    private void RefreshHeel()
    {
        _heel = default;
        foreach (var wear in _wears.Values)
        {
            // Tallest wins: boots over pumps reads better than the average of
            // two heels, and only one shoe can own the foot slots anyway.
            if (wear != null && wear.Heel.Any && wear.Heel.footDegrees > _heel.footDegrees)
            {
                _heel = wear.Heel;
            }
        }
    }

    // Has to be LateUpdate: the Animator writes the legs every frame, so a pose
    // applied at equip time (or in Update) is gone before it is ever drawn.
    //
    // ⭐ Bug #146: «each frame starts from what the animation wrote» is ONLY
    // true while the Animator actually writes. A standing girl's animator runs
    // in CullUpdateTransforms: the moment her renderer leaves every camera the
    // bones freeze — and a naive «+=» then compounds every frame. Measured in
    // the player: the hip climbed ~3 wu/s, its culling AABB left the frustum
    // (bounds centre at Y=84 wu while she walked at Y=1.3), so the renderer
    // could never become visible again and the animator never woke up — she
    // was invisible FOREVER, until selecting her made the portrait camera
    // render the body and restart the loop. So every bone remembers what we
    // wrote last frame: if nobody else has rewritten the bone since, we first
    // roll back to the remembered base, then apply the fresh offset. The base
    // is always the animator's (or IK's) last real word, and a frozen skeleton
    // stays exactly one application away from it instead of drifting.
    private struct TrackedPose
    {
        public bool Valid;
        public Quaternion BaseRotation;
        public Quaternion WrittenRotation;
    }

    private TrackedPose _lFootPose;
    private TrackedPose _rFootPose;
    private TrackedPose _lToePose;
    private TrackedPose _rToePose;
    private bool _hipLiftValid;
    private Vector3 _hipLiftBase;
    private Vector3 _hipLiftWritten;

    private void LateUpdate()
    {
        _heelPoseWeight = Mathf.MoveTowards(
            _heelPoseWeight,
            _heelPoseTarget,
            Time.unscaledDeltaTime / HeelPoseTransitionSeconds);

        if (_heel.Any == false)
        {
            return;
        }

        var axis = _heel.Axis;
        Pitch(GetBone("lFoot"), ref _lFootPose, axis, _heel.footDegrees * _heelPoseWeight);
        Pitch(GetBone("rFoot"), ref _rFootPose, axis, _heel.footDegrees * _heelPoseWeight);
        Pitch(GetBone("lToe"), ref _lToePose, axis, _heel.toeDegrees * _heelPoseWeight);
        Pitch(GetBone("rToe"), ref _rToePose, axis, _heel.toeDegrees * _heelPoseWeight);

        // Standing on the ball of the foot instead of the sole makes her taller;
        // without the lift she sinks into the ground by exactly the heel height.
        // Along the BODY's up, not the world's; seated/lying/swimming postures
        // fade this entire correction to zero through heelPoseWeight.
        if (hip != null && Mathf.Abs(_heel.lift) > 0.0001f)
        {
            Lift(hip, transform.up * (_heel.lift * _heelPoseWeight));
        }
    }

    private static void Pitch(Transform bone, ref TrackedPose tracked, Vector3 axis, float degrees)
    {
        if (bone == null)
        {
            return;
        }

        var current = bone.localRotation;
        if (tracked.Valid && ExactlyEqual(current, tracked.WrittenRotation))
        {
            // The bone still holds OUR last write — the animator is culled (or
            // off). Roll back to its last real value before applying afresh.
            current = tracked.BaseRotation;
        }

        var written = Mathf.Abs(degrees) < 0.01f
            ? current
            : current * Quaternion.AngleAxis(degrees, axis);
        bone.localRotation = written;
        tracked.BaseRotation = current;
        tracked.WrittenRotation = written;
        tracked.Valid = true;
    }

    // The hip is tracked in LOCAL space on purpose: the actor root moves every
    // render frame (interpolation), so a world-space cache would read every
    // frame as «somebody rewrote the bone» and re-base with the previous lift
    // still baked in — accumulating exactly like the bug this guards against.
    private void Lift(Transform hipBone, Vector3 worldLift)
    {
        var localLift = hipBone.parent != null
            ? hipBone.parent.InverseTransformVector(worldLift)
            : worldLift;

        var current = hipBone.localPosition;
        if (_hipLiftValid && ExactlyEqual(current, _hipLiftWritten))
        {
            current = _hipLiftBase;
        }

        var written = current + localLift;
        hipBone.localPosition = written;
        _hipLiftBase = current;
        _hipLiftWritten = written;
        _hipLiftValid = true;
    }

    private static bool ExactlyEqual(Quaternion a, Quaternion b) =>
        a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

    private static bool ExactlyEqual(Vector3 a, Vector3 b) =>
        a.x == b.x && a.y == b.y && a.z == b.z;

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

    // The key is "<definition id>#<index>" — see SetWearGrime, which matches on
    // the same prefix.
    private static string KeyToDefinitionId(string key)
    {
        var hash = key != null ? key.IndexOf('#') : -1;
        return hash >= 0 ? key.Substring(0, hash) : key;
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
        // §31B.4E: a variant is the prototype's mesh in its own materials. It
        // must be painted BEFORE Construct, which caches each slot's dry colour
        // and smoothness to restore after dirt and wet — cache the prototype's
        // and the variant would wash back to the wrong colour.
        newWear.ApplyVariant(Garments.GarmentVariants.MaterialsOf(KeyToDefinitionId(key)));
        newWear.Construct(_actorMesh, this, key);
        SuppressGarmentShadows(newWear);

        foreach (var slot in newWear.Slots)
        {
            // One garment per (layer, slot) — the old one comes off.
            if (layerDict.TryGetValue(slot, out var conflicting))
            {
                TakeOff(_wearKeys[conflicting]);
            }

            // §74.10 / bug #131: bags sit above the complete outfit. Their
            // Chest/Belly slots position the mesh and reserve the bag layer;
            // they are not coverage claims and must never hide anything below.
            // Only actual clothing layers participate in underwear occlusion.
            if (wearPrefab.Layer is VisualWearLayer.Wear or VisualWearLayer.Outerwear)
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
        UpdateGenitals();
        RefreshHeel();
        RefreshHairVisibility();
    }

    /// <summary>Причёска видна, пока на ней не сидит шапка.</summary>
    /// <remarks>
    /// Считается по ВСЕМ надетым вещам, а не по последней: девушка может носить
    /// сразу и кепку, и капюшон, и снятие одного из них волосы не возвращает.
    /// Поэтому здесь не «спрятать при надевании / показать при снятии», а один
    /// пересчёт, который зовут после любого изменения.
    /// </remarks>
    private void RefreshHairVisibility()
    {
        if (_hairInstance == null)
        {
            return;
        }

        var covered = false;
        foreach (var wear in _wears.Values)
        {
            if (wear != null && wear.HidesHair)
            {
                covered = true;
                break;
            }
        }

        if (covered)
        {
            _hairInstance.Hide();
        }
        else
        {
            _hairInstance.Show();
        }
    }

    // PERF (profiling, Aug-2026): every worn piece is a SkinnedMeshRenderer, and
    // a dressed colonist wears several — each one skinned and drawn again in
    // every shadow cascade it lands in. The BODY still casts, so she keeps her
    // shadow; what is lost is the cloth's own contribution to that silhouette,
    // which at play distance reads as a slightly slimmer blob. Flip this to keep
    // garment shadows if a wide skirt ever needs its outline back.
    private const bool GarmentsCastShadows = false;

    private static void SuppressGarmentShadows(Wear wear)
    {
        if (GarmentsCastShadows)
        {
            return;
        }

        foreach (var renderer in wear.GetComponentsInChildren<Renderer>(true))
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
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
        UpdateGenitals();
        RefreshHeel();
        RefreshHairVisibility();
    }

    // §72: восстановленная логика molly_copy (в §31B.3 её сознательно срезали —
    // девушкам она не нужна). Видно ТОЛЬКО когда слот Pelvis свободен на всех
    // трёх слоях: бельё, одежда, верхняя.
    //
    // Гендерного гейта нет и не нужно: у всех четырёх девушек поле genitals
    // пустое (fileID: 0), заполнено оно только у Kshishtof, так что ранний
    // выход по null оставляет их поведение ровно прежним.
    private void UpdateGenitals()
    {
        if (genitals == null)
        {
            return;
        }

        var covered =
            _byLayer[VisualWearLayer.Underwear].ContainsKey(VisualWearSlot.Pelvis) ||
            _byLayer[VisualWearLayer.Wear].ContainsKey(VisualWearSlot.Pelvis) ||
            _byLayer[VisualWearLayer.Outerwear].ContainsKey(VisualWearSlot.Pelvis);

        genitals.SetActive(!covered);
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
