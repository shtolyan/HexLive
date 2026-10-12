using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
public sealed class WildlifePickView : MonoBehaviour
{
    private Renderer[] _renderers;
    public Renderer[] Renderers => _renderers ??= GetComponentsInChildren<Renderer>();
    public bool Raycast(Ray ray, out float distance)
    {
        distance=float.PositiveInfinity;
        if (!gameObject.activeInHierarchy) return false;
        foreach (var renderer in Renderers)
            if (renderer.enabled && renderer.bounds.IntersectRay(ray, out var hit) && hit >= 0)
                distance=Mathf.Min(distance,hit);
        return !float.IsPositiveInfinity(distance);
    }
}
}
