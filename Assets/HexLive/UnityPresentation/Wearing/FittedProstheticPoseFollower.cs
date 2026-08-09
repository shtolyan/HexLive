using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// Keeps a fitted prosthetic on the animated, but visually collapsed, distal
/// bone chain. References are serialized so an inventory/health clone keeps
/// following its own cloned skeleton without rebuilding the prosthetic.
/// </summary>
public sealed class FittedProstheticPoseFollower : MonoBehaviour
{
    private const float ReferenceLength = 1f;
    private const float MinimumLength = 0.01f;

    [SerializeField] private Transform host;
    [SerializeField] private Transform startBone;
    [SerializeField] private Transform endBone;
    [SerializeField] private Transform[] endPath;
    [SerializeField] private Vector3 startBoneOriginalLocalScale = Vector3.one;
    [SerializeField] private Transform visualRoot;
    [SerializeField] private Transform grip;

    public void Configure(
        Transform owner,
        Transform start,
        Transform end,
        IReadOnlyList<Transform> path,
        Vector3 originalStartScale,
        Transform visual,
        Transform heldItemGrip)
    {
        host = owner;
        startBone = start;
        endBone = end;
        startBoneOriginalLocalScale = originalStartScale;
        visualRoot = visual;
        grip = heldItemGrip;
        endPath = new Transform[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            endPath[i] = path[i];
        }

        RefreshNow();
    }

    private void LateUpdate() => RefreshNow();

    public void RefreshNow()
    {
        if (host == null || startBone == null || endBone == null || visualRoot == null)
        {
            return;
        }

        var endMatrix = VirtualEndMatrix();
        var virtualEnd = endMatrix.MultiplyPoint3x4(Vector3.zero);
        var direction = virtualEnd - startBone.position;
        var length = direction.magnitude;
        if (length < MinimumLength)
        {
            // The follower lives on visualRoot so deactivating that GameObject
            // would also disable the only code capable of restoring it.
            visualRoot.localScale = Vector3.zero;
            return;
        }

        var up = direction / length;
        var forward = Vector3.ProjectOnPlane(host.forward, up);
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.ProjectOnPlane(host.right, up);
        }

        visualRoot.SetPositionAndRotation(
            startBone.position,
            Quaternion.LookRotation(forward.normalized, up));
        SetWorldScale(visualRoot, Vector3.one * (length / ReferenceLength));

        if (grip != null)
        {
            grip.SetPositionAndRotation(virtualEnd, endBone.rotation);
            SetWorldScale(grip, MatrixScale(endMatrix));
        }
    }

    private Matrix4x4 VirtualEndMatrix()
    {
        var startLocal = Matrix4x4.TRS(
            startBone.localPosition,
            startBone.localRotation,
            startBoneOriginalLocalScale);
        var matrix = startBone.parent != null
            ? startBone.parent.localToWorldMatrix * startLocal
            : startLocal;

        if (endPath != null)
        {
            foreach (var bone in endPath)
            {
                if (bone != null)
                {
                    matrix *= Matrix4x4.TRS(
                        bone.localPosition, bone.localRotation, bone.localScale);
                }
            }
        }

        return matrix;
    }

    private static Vector3 MatrixScale(Matrix4x4 matrix) => new(
        matrix.GetColumn(0).magnitude,
        matrix.GetColumn(1).magnitude,
        matrix.GetColumn(2).magnitude);

    private static void SetWorldScale(Transform target, Vector3 wanted)
    {
        var parentScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
        target.localScale = new Vector3(
            SafeDivide(wanted.x, parentScale.x),
            SafeDivide(wanted.y, parentScale.y),
            SafeDivide(wanted.z, parentScale.z));
    }

    private static float SafeDivide(float value, float divisor) =>
        Mathf.Abs(divisor) > 0.000001f ? value / divisor : value;
}

}
