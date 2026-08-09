using System.Collections;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// Presentation lifecycle for one persisted <c>body.limb_severed</c> object.
/// A fresh limb waits through the owner's pose/render frame before capturing,
/// so the body reaches its fallen/prone pose first. Old objects are rebuilt at
/// their saved junction from the same owner's reference FBX.
/// </summary>
public sealed class SeveredLimbDropView : MonoBehaviour
{
    private NpcActorView _owner;
    private int? _ownerNpcId;
    private string _variant;
    private int _objectId;
    private float _referenceScale;
    private float _fallbackScale;
    private float _maxCaptureDistance;
    private bool _captureCurrentPose;
    private Mesh _ownedMesh;
    private Material _ownedMaterial;

    public void Construct(
        NpcActorView owner,
        int? ownerNpcId,
        string variant,
        int objectId,
        bool captureCurrentPose,
        float referenceScale,
        float fallbackScale,
        float maxCaptureDistance)
    {
        _owner = owner;
        _ownerNpcId = ownerNpcId;
        _variant = variant;
        _objectId = objectId;
        _referenceScale = referenceScale;
        _fallbackScale = fallbackScale;
        _maxCaptureDistance = maxCaptureDistance;
        _captureCurrentPose = captureCurrentPose;

        if (captureCurrentPose || (owner == null && ownerNpcId is not null))
        {
            StartCoroutine(BuildAfterOwnerPose());
        }
        else
        {
            BuildReferenceOrFallback();
        }
    }

    private IEnumerator BuildAfterOwnerPose()
    {
        // HexWorldRenderer creates world objects before it syncs NPC condition
        // in this snapshot. Waiting through EndOfFrame lets SetBodyCondition,
        // the Animator and NpcActorView.LateUpdate finish the fallen/prone pose.
        // The invisible bone-only pose clone is then baked and destroyed in
        // this same coroutine step, before it could ever be rendered.
        yield return new WaitForEndOfFrame();

        if (this == null)
        {
            yield break;
        }

        // On a reconnect the limb object pass runs before SyncCorpseViews.
        // Resolve again now so a corpse created later in that same frame still
        // supplies its own FBX instead of degrading permanently to a capsule.
        if (_owner == null && _ownerNpcId is { } ownerId)
        {
            NpcActorView.TryGetLive(ownerId, out _owner);
        }

        GameObject visual = null;
        if (_captureCurrentPose && _owner != null && OwnerStillAtDrop())
        {
            visual = SeveredLimbFactory.BuildFromCurrentPose(_owner, _variant);
        }

        if (visual != null)
        {
            // Its Mesh is renderer-local baked geometry and its root already
            // carries the source body's world TRS. Keep that exact world pose
            // when it becomes a child of the persisted hex-object anchor.
            visual.transform.SetParent(transform, true);
            TrackGeneratedAssets(visual);
            Debug.Log($"[§50 limb] POSED FBX '{_variant}' obj#{_objectId} " +
                      $"at {visual.transform.position} (owner {_owner?.name})");
            yield break;
        }

        BuildReferenceOrFallback();
    }

    private bool OwnerStillAtDrop()
    {
        var offset = _owner.transform.position - transform.position;
        offset.y = 0f;
        return offset.sqrMagnitude <= _maxCaptureDistance * _maxCaptureDistance;
    }

    private void BuildReferenceOrFallback()
    {
        var visual = SeveredLimbFactory.BuildReference(_owner, _variant);
        if (visual != null)
        {
            visual.transform.SetParent(transform, false);
            visual.transform.localScale = Vector3.one * _referenceScale;
            visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            GroundVisual(visual, 0.05f);
            TrackGeneratedAssets(visual);
            Debug.Log($"[§50 limb] REFERENCE FBX '{_variant}' obj#{_objectId} " +
                      $"at {transform.position} (owner {_owner?.name})");
            return;
        }

        // The actor is absent or its FBX violates the Read/Write contract.
        // Keep an obvious correctly-sized object instead of borrowing another
        // actor's geometry or silently losing the limb.
        var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = "LimbCapsule (fallback)";
        capsule.transform.SetParent(transform, false);
        capsule.transform.localScale = new Vector3(0.22f, 0.5f, 0.22f) * _fallbackScale;
        capsule.transform.localRotation = Quaternion.Euler(80f, 0f, 0f);
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetColor("_BaseColor", new Color(0.7f, 0.04f, 0.04f));
        material.SetFloat("_Smoothness", 0.15f);
        capsule.GetComponent<MeshRenderer>().sharedMaterial = material;
        _ownedMaterial = material;
        GroundVisual(capsule, 0.12f);
        Debug.LogWarning($"[§50 limb] FALLBACK capsule '{_variant}' obj#{_objectId} " +
                         $"at {transform.position} — owner FBX unavailable or not readable");
    }

    private void TrackGeneratedAssets(GameObject visual)
    {
        _ownedMesh = visual.GetComponentInChildren<MeshFilter>()?.sharedMesh;
        _ownedMaterial = visual.GetComponentInChildren<MeshRenderer>()?.sharedMaterial;
    }

    private void OnDestroy()
    {
        // Runtime-created meshes/materials are not imported assets. Release
        // them when the decaying world object disappears; the fallback capsule
        // keeps Unity's built-in mesh and owns only its generated material.
        if (_ownedMesh != null)
        {
            Destroy(_ownedMesh);
        }

        if (_ownedMaterial != null)
        {
            Destroy(_ownedMaterial);
        }
    }

    private static void GroundVisual(GameObject instance, float lift)
    {
        var renderers = instance.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return;
        }

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        var offset = instance.transform.position.y - bounds.min.y + lift;
        instance.transform.localPosition += Vector3.up * offset;
    }
}

}
