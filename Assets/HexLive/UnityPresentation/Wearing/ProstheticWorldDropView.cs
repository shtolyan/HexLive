#nullable enable
using HexLive.Simulation.Content;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// World-drop presentation for the four universal prosthetic items. Models
/// come exclusively from the same atomic object as fitted
/// devices; a failed load deliberately leaves an empty anchor, never a sphere.
/// </summary>
public sealed class ProstheticWorldDropView : MonoBehaviour
{
    // Marta source measurements from §118.5. Atomic assets use a 1 m
    // joint→end reference, while the world actors use this production scale.
    private const float ActorSourceHeightMeters = 1.7f;
    private const float AuthoredActorHeightMeters = 2.4f;
    private const float NpcHeightFactor = 11f / 30f;
    private const float ArmBoneMeters = 0.263512f;
    private const float LegBoneMeters = 0.435129f;

    private int _requestVersion;
    private bool _destroyed;

    public static bool IsSupported(string definitionId) =>
        definitionId is ContentIds.WoodenArm or ContentIds.WoodenLeg or
            ContentIds.MechanicalArm or ContentIds.MechanicalLeg;

    public void Construct(string definitionId, int objectId)
    {
        if (!ProstheticContent.TryDescribeWorldDrop(
                definitionId, objectId, out var part, out var mechanical))
        {
            return;
        }

        var request = ++_requestVersion;
        ProstheticContent.Load(part, definitionId, mechanical,
            prefab => Attach(request, definitionId, objectId, part, mechanical, prefab));
    }

    private void Attach(
        int request, string definitionId, int objectId, BodyPart part,
        bool mechanical, GameObject? prefab)
    {
        // The cached content request may complete after the snapshot
        // removed/replaced this object. Never resurrect a stale world drop.
        if (_destroyed || this == null || request != _requestVersion || prefab == null)
        {
            return;
        }

        var instance = Instantiate(prefab, transform, false);
        instance.name = $"Model {definitionId} {part}";
        if (!ObjectFit.HasRenderableGeometry(instance))
        {
            Debug.LogError($"[ProstheticWorldDrop] Atomic object {ProstheticContent.Address(part, definitionId, mechanical)} " +
                           "loaded without renderable geometry.");
            Destroy(instance);
            return;
        }

        var actorScale = SimulationUnityMapper.HexRadius * NpcHeightFactor *
            AuthoredActorHeightMeters / ActorSourceHeightMeters;
        var referenceLength = part is BodyPart.ArmL or BodyPart.ArmR
            ? ArmBoneMeters : LegBoneMeters;
        instance.transform.localScale *= actorScale * referenceLength;

        // The asset's joint→end axis is +Y. A loose device lies on that axis,
        // with stable per-object yaw so rebuilding a snapshot never makes it pop.
        var yaw = ((uint)objectId * 2654435761u >> 8) / 16777216f * 360f;
        instance.transform.localRotation =
            Quaternion.Euler(90f, yaw, 0f) * instance.transform.localRotation;

        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var material in renderer.materials)
            {
                if (material != null) ProstheticVisual.UpgradeToUrp(material);
            }
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        Ground(instance);
    }

    private static void Ground(GameObject instance)
    {
        var renderers = instance.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;
        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        instance.transform.localPosition += Vector3.up *
            (instance.transform.position.y - bounds.min.y + 0.01f);
    }

    private void OnDestroy()
    {
        _destroyed = true;
        _requestVersion++;
    }
}

}
