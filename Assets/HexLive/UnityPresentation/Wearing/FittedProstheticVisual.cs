using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// View-only fitted prosthetic. The organic distal bone may be collapsed by
/// amputation, so the model and its grip live under the actor root and copy the
/// animated bones' world pose without inheriting their near-zero scale.
/// </summary>
internal sealed class ProstheticVisual
{
    private const float ReferenceLength = 1f;
    private const float MinimumLength = 0.01f;

    private readonly Transform _host;
    private readonly Transform _startBone;
    private readonly Transform _endBone;
    private readonly Transform[] _endPath;
    private readonly Vector3 _startBoneOriginalLocalScale;
    private readonly GameObject _visualRoot;
    private readonly List<MaterialTint> _materials = new();
    private readonly int _targetLayer;
    private readonly Action _modelLoaded;

    private float _lastCondition01 = -1f;
    private float _condition01 = 1f;
    private bool _destroyed;
    private bool _loadCompleted;

    private readonly struct MaterialTint
    {
        public readonly Material Material;
        public readonly Color BaseColor;

        public MaterialTint(Material material)
        {
            Material = material;
            BaseColor = material != null ? material.color : Color.white;
        }
    }

    private ProstheticVisual(
        BodyPart part,
        string definitionId,
        bool mechanical,
        Transform host,
        Transform startBone,
        Transform endBone,
        Transform[] endPath,
        Vector3 startBoneOriginalLocalScale,
        GameObject visualRoot,
        Transform grip,
        int targetLayer,
        Action modelLoaded)
    {
        Part = part;
        DefinitionId = definitionId ?? string.Empty;
        Mechanical = mechanical;
        _host = host;
        _startBone = startBone;
        _endBone = endBone;
        _endPath = endPath;
        _startBoneOriginalLocalScale = startBoneOriginalLocalScale;
        _visualRoot = visualRoot;
        Grip = grip;
        _targetLayer = targetLayer;
        _modelLoaded = modelLoaded;
    }

    public BodyPart Part { get; }

    public string DefinitionId { get; }

    public bool Mechanical { get; }

    public Transform Grip { get; }

    /// <summary>
    /// A failed Addressables operation is also complete: the simulation and
    /// Grip stay usable, and the loading curtain must not wait forever.
    /// </summary>
    public bool IsReady => _loadCompleted;

    public bool Matches(ProstheticSnapshot snapshot) =>
        snapshot != null &&
        snapshot.Part == Part &&
        snapshot.Mechanical == Mechanical &&
        string.Equals(snapshot.DefinitionId, DefinitionId, StringComparison.Ordinal);

    public static ProstheticVisual Create(
        Transform host,
        BodyBones bodyBones,
        BodyPartConditionSnapshot condition,
        Vector3 startBoneOriginalLocalScale)
    {
        if (host == null || bodyBones == null || condition?.Prosthetic == null ||
            !TryBoneNames(condition.Part, out var startName, out var endName))
        {
            return null;
        }

        var start = bodyBones.GetBone(startName);
        var end = bodyBones.GetBone(endName);
        if (start == null || end == null)
        {
            Debug.LogWarning($"[ProstheticView] Missing bones {startName}/{endName} for {condition.Part}.");
            return null;
        }

        return Create(
            host, start, end, condition, startBoneOriginalLocalScale,
            createGrip: true, targetLayer: -1, modelLoaded: null);
    }

    /// <summary>
    /// Direct-bone factory shared by the live actor and the skeleton-only
    /// health doll. The model arrives asynchronously from Addressables; the
    /// wrapper and optional grip exist immediately.
    /// </summary>
    internal static ProstheticVisual Create(
        Transform host,
        Transform start,
        Transform end,
        BodyPartConditionSnapshot condition,
        Vector3 startBoneOriginalLocalScale,
        bool createGrip,
        int targetLayer,
        Action modelLoaded)
    {
        if (host == null || start == null || end == null || condition?.Prosthetic == null)
        {
            return null;
        }

        if (!TryBuildEndPath(start, end, out var endPath))
        {
            Debug.LogWarning($"[ProstheticView] Bone {end.name} is not below {start.name} " +
                             $"for {condition.Part}.");
            return null;
        }

        var prosthetic = condition.Prosthetic;
        var wrapper = new GameObject($"Prosthetic {condition.Part} {prosthetic.DefinitionId}");
        wrapper.transform.SetParent(host, false);
        if (targetLayer >= 0)
        {
            wrapper.layer = targetLayer;
        }

        // Held props need the original hand's pose and scale, but parenting to
        // that hand would collapse them with the severed organic mesh.
        Transform grip = null;
        if (createGrip && condition.Part is (BodyPart.ArmL or BodyPart.ArmR))
        {
            grip = new GameObject($"ProstheticGrip {condition.Part}").transform;
            grip.SetParent(host, false);
        }

        var visual = new ProstheticVisual(
            condition.Part,
            prosthetic.DefinitionId,
            prosthetic.Mechanical,
            host,
            start,
            end,
            endPath,
            startBoneOriginalLocalScale,
            wrapper,
            grip,
            targetLayer,
            modelLoaded);
        visual.Update(condition);
        ProstheticContent.Load(
            condition.Part,
            prosthetic.DefinitionId,
            prosthetic.Mechanical,
            visual.AttachModel);
        return visual;
    }

    public void Update(BodyPartConditionSnapshot condition)
    {
        UpdatePose();
        if (condition?.Prosthetic == null)
        {
            return;
        }

        var max = condition.Prosthetic.MaxCondition;
        _condition01 = max > 0f
            ? Mathf.Clamp01(condition.Prosthetic.Condition / max)
            : 0f;
        ApplyConditionTint();
    }

    private void AttachModel(GameObject prefab)
    {
        _loadCompleted = true;

        // A handle can complete after the patient changed device, the doll
        // switched target, or the actor was destroyed. Destroy() marks this
        // visual synchronously, so an obsolete request can never resurrect it.
        if (_destroyed || _visualRoot == null || prefab == null)
        {
            return;
        }

        var instance = UnityEngine.Object.Instantiate(prefab, _visualRoot.transform, false);
        instance.name = $"Model {DefinitionId} {Part}";
        if (_targetLayer >= 0)
        {
            SetLayerDeep(instance.transform, _targetLayer);
        }

        _materials.Clear();
        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            // renderer.materials deliberately creates per-owner instances:
            // condition is individual state and must not tint the shared FBX.
            foreach (var material in renderer.materials)
            {
                if (material == null) continue;
                UpgradeToUrp(material);
                _materials.Add(new MaterialTint(material));
            }
        }

        _lastCondition01 = -1f;
        ApplyConditionTint();
        UpdatePose();
        _modelLoaded?.Invoke();
    }

    private void ApplyConditionTint()
    {
        if (Mathf.Abs(_condition01 - _lastCondition01) < 0.002f)
        {
            return;
        }

        _lastCondition01 = _condition01;
        foreach (var entry in _materials)
        {
            if (entry.Material == null) continue;
            entry.Material.color = Mechanical
                ? MechanicalConditionColor(entry.BaseColor, _condition01)
                : WoodenConditionColor(entry.BaseColor, _condition01);
        }
    }

    public void UpdatePose()
    {
        if (_visualRoot == null || _startBone == null || _endBone == null)
        {
            return;
        }

        var endMatrix = VirtualEndMatrix();
        var virtualEnd = endMatrix.MultiplyPoint3x4(Vector3.zero);
        var direction = virtualEnd - _startBone.position;
        var length = direction.magnitude;
        if (length < MinimumLength)
        {
            _visualRoot.SetActive(false);
            return;
        }

        if (!_visualRoot.activeSelf)
        {
            _visualRoot.SetActive(true);
        }

        var up = direction / length;
        var forward = Vector3.ProjectOnPlane(_host.forward, up);
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.ProjectOnPlane(_host.right, up);
        }

        _visualRoot.transform.SetPositionAndRotation(
            _startBone.position,
            Quaternion.LookRotation(forward.normalized, up));
        SetWorldScale(_visualRoot.transform, Vector3.one * (length / ReferenceLength));

        if (Grip != null)
        {
            Grip.SetPositionAndRotation(virtualEnd, _endBone.rotation);
            SetWorldScale(Grip, MatrixScale(endMatrix));
        }
    }

    public void Destroy()
    {
        _destroyed = true;
        if (_visualRoot != null)
        {
            UnityEngine.Object.Destroy(_visualRoot);
        }

        if (Grip != null)
        {
            UnityEngine.Object.Destroy(Grip.gameObject);
        }
    }

    internal static bool TryBoneNames(BodyPart part, out string start, out string end)
    {
        switch (part)
        {
            case BodyPart.ArmL:
                start = "lForearmBend";
                end = "lHand";
                return true;
            case BodyPart.ArmR:
                start = "rForearmBend";
                end = "rHand";
                return true;
            case BodyPart.LegL:
                start = "lShin";
                end = "lFoot";
                return true;
            case BodyPart.LegR:
                start = "rShin";
                end = "rFoot";
                return true;
            default:
                start = null;
                end = null;
                return false;
        }
    }

    private static bool TryBuildEndPath(Transform start, Transform end, out Transform[] path)
    {
        var reverse = new List<Transform>();
        var cursor = end;
        while (cursor != null && cursor != start)
        {
            reverse.Add(cursor);
            cursor = cursor.parent;
        }

        if (cursor != start)
        {
            path = Array.Empty<Transform>();
            return false;
        }

        reverse.Reverse();
        path = reverse.ToArray();
        return true;
    }

    /// <summary>
    /// Reconstructs the hand/foot endpoint as if the severed distal bone had
    /// kept its authored scale. ApplySeveredLimbs collapses that bone so the
    /// organic skinned mesh disappears; reading endBone.position directly
    /// would therefore make the fitted device about 0.0001 of its real size.
    /// Local animation rotations remain valid, so replaying the short chain
    /// produces the animated, uncollapsed endpoint without restoring the limb.
    /// </summary>
    private Matrix4x4 VirtualEndMatrix()
    {
        var startLocal = Matrix4x4.TRS(
            _startBone.localPosition,
            _startBone.localRotation,
            _startBoneOriginalLocalScale);
        var matrix = _startBone.parent != null
            ? _startBone.parent.localToWorldMatrix * startLocal
            : startLocal;

        foreach (var bone in _endPath)
        {
            matrix *= Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale);
        }

        return matrix;
    }

    private static Vector3 MatrixScale(Matrix4x4 matrix) => new(
        matrix.GetColumn(0).magnitude,
        matrix.GetColumn(1).magnitude,
        matrix.GetColumn(2).magnitude);

    private static Color WoodenConditionColor(Color original, float condition01)
    {
        var worn = new Color(
            original.r * 0.42f,
            original.g * 0.32f,
            original.b * 0.25f,
            original.a);
        return Color.Lerp(worn, original, Mathf.Sqrt(condition01));
    }

    private static Color MechanicalConditionColor(Color original, float condition01)
    {
        var rust = new Color(
            Mathf.Max(original.r * 0.38f, 0.25f),
            original.g * 0.25f,
            original.b * 0.16f,
            original.a);
        return Color.Lerp(rust, original, Mathf.Sqrt(condition01));
    }

    private static void UpgradeToUrp(Material material)
    {
        var urp = Shader.Find("Universal Render Pipeline/Lit");
        if (urp == null || material.shader == urp)
        {
            return;
        }

        // Blender FBX materials otherwise import with the built-in Standard
        // shader and render magenta under HexLive's URP pipeline. Preserve the
        // authored flat colour while switching only this per-actor instance.
        var color = material.color;
        material.shader = urp;
        material.color = color;
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
    }

    private static void SetWorldScale(Transform target, Vector3 wanted)
    {
        var parentScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
        target.localScale = new Vector3(
            SafeDivide(wanted.x, parentScale.x),
            SafeDivide(wanted.y, parentScale.y),
            SafeDivide(wanted.z, parentScale.z));
    }

    private static float SafeDivide(float value, float divisor) =>
        Mathf.Abs(divisor) > 0.00001f ? value / divisor : value;

    private static void SetLayerDeep(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (var i = 0; i < root.childCount; i++)
        {
            SetLayerDeep(root.GetChild(i), layer);
        }
    }
}

}
