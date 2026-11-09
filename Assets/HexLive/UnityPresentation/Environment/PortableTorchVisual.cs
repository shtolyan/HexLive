#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
/// <summary>§175: flames and soft light follow the authored tip, with campfire reach.</summary>
public sealed class PortableTorchVisual : MonoBehaviour
{
    private Transform _tip = null!;
    private Transform _fire = null!;

    public static CampfireEffect Attach(GameObject model)
    {
        var visual = model.AddComponent<PortableTorchVisual>();
        visual._tip = model.transform;
        foreach (var child in model.GetComponentsInChildren<Transform>(true))
            if (child.name == "fire_point") { visual._tip = child; break; }
        var host = new GameObject("Torch fire");
        visual._fire = host.transform;
        host.transform.SetParent(model.transform, false);
        visual.LateUpdate();
        var effect = host.AddComponent<CampfireEffect>();
        var radius = Simulation.Spatial.HexSpatialMath.HexRadius;
        effect.Construct(radius * 0.18f, radius, playCrackle: false, lightIntensity: 1.5f);
        effect.SetLit(true);
        return effect;
    }

    private void LateUpdate()
    {
        if (_fire == null || _tip == null) return;
        _fire.position = _tip.position;
        _fire.rotation = Quaternion.identity;
        var scale = transform.lossyScale;
        _fire.localScale = new Vector3(
            1f / Mathf.Max(.0001f, Mathf.Abs(scale.x)),
            1f / Mathf.Max(.0001f, Mathf.Abs(scale.y)),
            1f / Mathf.Max(.0001f, Mathf.Abs(scale.z)));
    }
}
}
