using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// Keeps a fitted prosthetic on the animated, but visually collapsed, distal
/// bone chain. References are serialized so an inventory/health clone keeps
/// following its own cloned skeleton without rebuilding the prosthetic.
///
/// §118.5 (bugs #739/#740): the model's twist about the limb axis (its local
/// +Z = toes / back of the hand) comes from the ANIMATED SKELETON, never from
/// the actor root. LieDown/Sleep/Fall clips lay the body down through the hip
/// bone while the root stays upright, so the old root-forward hint lay along
/// the shin of a sleeping girl and the wooden foot spun sideways.
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

    // Distal forward reference (legs): the virtual foot -> toe chain, so the
    // prosthetic foot points where the animated organic foot points.
    [SerializeField] private Transform[] toePath;

    // Body frame (any limb): thighs + lower abdomen. Geometric, so it needs no
    // knowledge of bone local axes, which an FBX import may permute.
    [SerializeField] private Transform leftThigh;
    [SerializeField] private Transform rightThigh;
    [SerializeField] private Transform abdomen;

    private const float MinimumHintFraction = 0.3f;

    public void Configure(
        Transform owner,
        Transform start,
        Transform end,
        IReadOnlyList<Transform> path,
        Vector3 originalStartScale,
        Transform visual,
        Transform heldItemGrip,
        string distalForwardBoneName = null)
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

        toePath = BuildDescendantPath(end, distalForwardBoneName);
        var skeletonRoot = SkeletonRoot(start);
        leftThigh = FindDescendant(skeletonRoot, "lThighBend");
        rightThigh = FindDescendant(skeletonRoot, "rThighBend");
        abdomen = FindDescendant(skeletonRoot, "abdomenLower");

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
        var forward = ForwardHint(endMatrix, virtualEnd, up);

        visualRoot.SetPositionAndRotation(
            startBone.position,
            Quaternion.LookRotation(forward, up));
        SetWorldScale(visualRoot, Vector3.one * (length / ReferenceLength));

        if (grip != null)
        {
            grip.SetPositionAndRotation(virtualEnd, endBone.rotation);
            SetWorldScale(grip, MatrixScale(endMatrix));
        }
    }

    /// <summary>
    /// Unit vector perpendicular to <paramref name="up"/> for the model's +Z.
    /// Order: animated foot->toe (legs), body forward from the skeleton, and
    /// only as a last resort the actor root (degenerate geometry, missing
    /// bones). A hint nearly parallel to the limb is rejected rather than
    /// normalised: its residue is noise that spins the model.
    /// </summary>
    private Vector3 ForwardHint(Matrix4x4 endMatrix, Vector3 virtualEnd, Vector3 up)
    {
        if (toePath != null && toePath.Length > 0 &&
            TryProject(VirtualToe(endMatrix) - virtualEnd, up, out var toe))
        {
            return toe;
        }

        if (leftThigh != null && rightThigh != null && abdomen != null)
        {
            var right = rightThigh.position - leftThigh.position;
            var bodyUp = abdomen.position - (leftThigh.position + rightThigh.position) * 0.5f;
            // Cross(right, up) is forward in Unity: (1,0,0) x (0,1,0) = (0,0,1).
            if (TryProject(Vector3.Cross(right, bodyUp), up, out var body))
            {
                return body;
            }
        }

        if (TryProject(host.forward, up, out var rootForward))
        {
            return rootForward;
        }

        return Vector3.ProjectOnPlane(host.right, up).normalized;
    }

    private static bool TryProject(Vector3 hint, Vector3 up, out Vector3 projected)
    {
        projected = Vector3.zero;
        var magnitude = hint.magnitude;
        if (magnitude < 0.000001f)
        {
            return false;
        }

        projected = Vector3.ProjectOnPlane(hint / magnitude, up);
        if (projected.magnitude < MinimumHintFraction)
        {
            return false;
        }

        projected.Normalize();
        return true;
    }

    /// <summary>
    /// Toe position through the same local-TRS chain as the virtual foot: the
    /// shin is collapsed by amputation, so world positions below it are not.
    /// </summary>
    private Vector3 VirtualToe(Matrix4x4 endMatrix)
    {
        var matrix = endMatrix;
        foreach (var bone in toePath)
        {
            if (bone != null)
            {
                matrix *= Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale);
            }
        }

        return matrix.MultiplyPoint3x4(Vector3.zero);
    }

    private static Transform SkeletonRoot(Transform start)
    {
        // DAZ skeletons are rooted at "hip"; stop there so a search never
        // wanders into props or another actor parented under the same host.
        for (var cursor = start; cursor != null; cursor = cursor.parent)
        {
            if (cursor.name == "hip")
            {
                return cursor;
            }
        }

        return start != null ? start.root : null;
    }

    private static Transform FindDescendant(Transform root, string boneName)
    {
        if (root == null || string.IsNullOrEmpty(boneName))
        {
            return null;
        }

        if (root.name == boneName)
        {
            return root;
        }

        for (var i = 0; i < root.childCount; i++)
        {
            var found = FindDescendant(root.GetChild(i), boneName);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static Transform[] BuildDescendantPath(Transform ancestor, string boneName)
    {
        var target = FindDescendant(ancestor, boneName);
        if (target == null || target == ancestor)
        {
            return System.Array.Empty<Transform>();
        }

        var reverse = new List<Transform>();
        for (var cursor = target; cursor != null && cursor != ancestor; cursor = cursor.parent)
        {
            reverse.Add(cursor);
        }

        reverse.Reverse();
        return reverse.ToArray();
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
