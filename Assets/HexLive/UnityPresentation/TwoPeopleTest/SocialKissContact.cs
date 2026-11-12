using System;
using RootMotion.FinalIK;
using UnityEngine;

namespace HexLive.UnityPresentation.TwoPeopleTest
{

public sealed class SocialKissContact
{
    private readonly float scale;

    public static float Weight(float progress) =>
        Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.43f, 0.515f, progress)) *
        Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.64f, 0.55f, progress));

    public static void Fit(SocialKissContact a, SocialKissContact b, float weight)
    {
        if (weight <= 0f || !a.Ready || !b.Ready) return;
        var scale = Mathf.Min(a.scale, b.scale);
        var gap = b.Mouth - a.Mouth;
        var horizontal = Vector3.ClampMagnitude(new Vector3(gap.x, 0f, gap.z) * 0.5f, 0.09f * scale);
        // Let the taller character crouch. Raising the shorter character would overextend planted legs.
        var aY = Mathf.Clamp(gap.y, -0.16f * scale, 0f);
        var bY = Mathf.Clamp(-gap.y, -0.16f * scale, 0f);
        var aOffset = (horizontal + Vector3.up * aY) * weight;
        var bOffset = (-horizontal + Vector3.up * bY) * weight;
        var descent = Mathf.Min(0.04f * scale * weight,
            Mathf.Max(a.DescentForReach(aOffset), b.DescentForReach(bOffset)));
        // Both descend equally when an ankle would otherwise be beyond the leg's reach.
        a.MoveHips(aOffset - Vector3.up * descent);
        b.MoveHips(bOffset - Vector3.up * descent);
    }

    private readonly Transform hips;
    private readonly Transform upperLip;
    private readonly Transform lowerLip;
    private readonly IKSolverLimb left;
    private readonly IKSolverLimb right;

    public bool Ready => upperLip != null && lowerLip != null && left.initiated && right.initiated;
    public Vector3 Mouth => (upperLip.position + lowerLip.position) * 0.5f;

    public SocialKissContact(Animator actor)
    {
        scale = Mathf.Abs(actor.transform.lossyScale.y);
        var joints = actor.GetComponentsInChildren<Transform>(true);
        upperLip = Array.Find(joints, t => t.name == "LipUpperMiddle");
        lowerLip = Array.Find(joints, t => t.name == "LipLowerMiddle");
        hips = actor.GetBoneTransform(HumanBodyBones.Hips);
        left = Leg(actor, AvatarIKGoal.LeftFoot, HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        right = Leg(actor, AvatarIKGoal.RightFoot, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
    }

    private static IKSolverLimb Leg(Animator actor, AvatarIKGoal goal,
        HumanBodyBones thigh, HumanBodyBones knee, HumanBodyBones foot)
    {
        var solver = new IKSolverLimb { goal = goal, IKPositionWeight = 1f, IKRotationWeight = 1f };
        solver.SetChain(actor.GetBoneTransform(thigh), actor.GetBoneTransform(knee),
            actor.GetBoneTransform(foot), actor.transform);
        return solver;
    }

    private static void StoreFoot(IKSolverLimb solver)
    {
        solver.IKPosition = solver.bone3.transform.position;
        solver.IKRotation = solver.bone3.transform.rotation;
        solver.MaintainBend();
    }

    public float DescentForReach(Vector3 offset) =>
        Mathf.Max(DescentForReach(left, offset), DescentForReach(right, offset));

    private float DescentForReach(IKSolverLimb solver, Vector3 offset)
    {
        var thigh = solver.bone1.transform.position;
        var knee = solver.bone2.transform.position;
        var ankle = solver.bone3.transform.position;
        var reach = Vector3.Distance(thigh, knee) + Vector3.Distance(knee, ankle) - 0.001f * scale;
        var delta = thigh + offset - ankle;
        var verticalReach = Mathf.Sqrt(Mathf.Max(0f,
            reach * reach - delta.x * delta.x - delta.z * delta.z));
        return Mathf.Max(0f, delta.y - verticalReach);
    }

    public void MoveHips(Vector3 offset)
    {
        if (offset.sqrMagnitude < 0.000001f) return;
        StoreFoot(left);
        StoreFoot(right);
        hips.position += offset;
        left.Update();
        right.Update();
    }
}

}
