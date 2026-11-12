using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using RootMotion.FinalIK;
using UnityEngine;

namespace HexLive.UnityPresentation.TwoPeopleTest
{

public sealed class SocialHugContact : IDisposable
{
    private readonly Torso torso;
    private readonly Arm left;
    private readonly Arm right;

    public SocialHugContact(Animator actor, Transform root, SkinnedMeshRenderer[] skins)
    {
        torso = new Torso(actor, root, skins);
        left = new Arm(actor, root, false);
        right = new Arm(actor, root, true);
    }

    public void ReadPose() => torso.ReadPose();

    public static float Weight(float progress) =>
        Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.27f, 0.43f, progress)) *
        Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.94f, 0.87f, progress));

    public void Fit(SocialHugContact partner, float weight, float progress)
    {
        var release = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.77f, 0.90f, progress));
        left.Fit(partner.torso, weight, release);
        right.Fit(partner.torso, weight, release);
    }

    public float Penetration(SocialHugContact partner) => Mathf.Max(
        left.Penetration(partner.torso), right.Penetration(partner.torso));

    public Vector2 PalmClearance(SocialHugContact partner) => new Vector2(
        left.PalmClearance(partner.torso), right.PalmClearance(partner.torso));

    public void RestoreFingerPose()
    {
        left.RestoreFingerPose();
        right.RestoreFingerPose();
    }

    public void Dispose()
    {
        torso.Dispose();
        left.Dispose();
        right.Dispose();
    }

    private sealed class Torso : IDisposable
    {
        private const int Rows = 13;
        private const int Sides = 12;
        private static readonly Vector2[] Directions = CreateDirections();
        private readonly float[,] support = new float[Rows, Sides];
        private readonly Transform root;
        private readonly Transform hips;
        private readonly Transform neck;
        private readonly Transform leftShoulder;
        private readonly Transform rightShoulder;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<(SkinnedMeshRenderer skin, int[] indices)> sources = new();
        private readonly Mesh baked = new Mesh();
        private readonly float scale;
        private Matrix4x4 toLocal;
        private Matrix4x4 toWorld;
        private Vector3 waist;
        private Vector3 shoulders;
        private float bottom;
        private float top;

        public Torso(Animator actor, Transform root, SkinnedMeshRenderer[] skins)
        {
            this.root = root;
            scale = Mathf.Abs(root.lossyScale.y);
            hips = actor.GetBoneTransform(HumanBodyBones.Hips);
            neck = actor.GetBoneTransform(HumanBodyBones.Neck);
            leftShoulder = actor.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            rightShoulder = actor.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var minimum = root.InverseTransformPoint(hips.position).y - 0.085f;
            var maximum = root.InverseTransformPoint(neck.position).y + 0.055f;
            var width = Vector3.Distance(root.InverseTransformPoint(leftShoulder.position),
                root.InverseTransformPoint(rightShoulder.position)) * 0.5f + 0.085f;
            foreach (var skin in skins)
            {
                if (!skin.enabled || skin.sharedMesh == null) continue;
                var garment = skin.GetComponentInParent<Wear>();
                if (!skin.name.StartsWith("Genesis3", StringComparison.Ordinal) &&
                    !(garment != null && garment.Slots.Any(slot => slot is VisualWearSlot.Chest
                        or VisualWearSlot.Belly or VisualWearSlot.Pelvis))) continue;
                var bones = skin.bones;
                var limb = new bool[bones.Length];
                for (var i = 0; i < bones.Length; i++)
                    limb[i] = bones[i] != null &&
                        (bones[i].IsChildOf(leftShoulder) || bones[i].IsChildOf(rightShoulder));
                var weights = skin.sharedMesh.isReadable ? skin.sharedMesh.boneWeights : Array.Empty<BoneWeight>();
                var indices = new List<int>();
                skin.BakeMesh(baked);
                baked.GetVertices(vertices);
                var matrix = root.worldToLocalMatrix * skin.localToWorldMatrix;
                for (var i = 0; i < vertices.Count; i++)
                {
                    var p = matrix.MultiplyPoint3x4(vertices[i]);
                    if (p.y < minimum || p.y > maximum || Mathf.Abs(p.x) > width) continue;
                    if (i < weights.Length && LimbWeight(weights[i], limb) > 0.58f) continue;
                    indices.Add(i);
                }
                sources.Add((skin, indices.ToArray()));
            }
        }

        private static float LimbWeight(BoneWeight w, bool[] limb) =>
            (limb[w.boneIndex0] ? w.weight0 : 0f) + (limb[w.boneIndex1] ? w.weight1 : 0f) +
            (limb[w.boneIndex2] ? w.weight2 : 0f) + (limb[w.boneIndex3] ? w.weight3 : 0f);

        private static Vector2[] CreateDirections()
        {
            var result = new Vector2[Sides];
            for (var i = 0; i < result.Length; i++)
            {
                var angle = 2f * Mathf.PI * i / result.Length;
                result[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            return result;
        }

        public void ReadPose()
        {
            toLocal = root.worldToLocalMatrix;
            toWorld = root.localToWorldMatrix;
            waist = toLocal.MultiplyPoint3x4(hips.position);
            shoulders = toLocal.MultiplyPoint3x4((leftShoulder.position + rightShoulder.position) * 0.5f);
            bottom = waist.y - 0.07f;
            top = toLocal.MultiplyPoint3x4(neck.position).y + 0.045f;
            for (var row = 0; row < Rows; row++)
                for (var side = 0; side < Sides; side++) support[row, side] = float.NegativeInfinity;

            foreach (var (skin, indices) in sources)
            {
                skin.BakeMesh(baked);
                baked.GetVertices(vertices);
                var matrix = toLocal * skin.localToWorldMatrix;
                foreach (var i in indices)
                {
                    var p = matrix.MultiplyPoint3x4(vertices[i]);
                    if (p.y < bottom || p.y > top) continue;
                    var row = Mathf.RoundToInt(Row(p.y));
                    for (var side = 0; side < Sides; side++)
                        support[row, side] = Mathf.Max(support[row, side],
                            p.x * Directions[side].x + p.z * Directions[side].y);
                }
            }

            for (var row = 0; row < Rows; row++)
            {
                if (!float.IsNegativeInfinity(support[row, 0])) continue;
                var y = Mathf.Lerp(bottom, top, row / (Rows - 1f));
                var center = Center(y);
                for (var side = 0; side < Sides; side++)
                    support[row, side] = center.x * Directions[side].x + center.z * Directions[side].y + 0.045f;
            }
        }

        private float Row(float y) => Mathf.InverseLerp(bottom, top, y) * (Rows - 1);
        private Vector3 Center(float y) => Vector3.Lerp(waist, shoulders, Mathf.InverseLerp(waist.y, shoulders.y, y));

        public float Clearance(Vector3 point)
        {
            var p = toLocal.MultiplyPoint3x4(point);
            var row = Row(p.y);
            var lower = Mathf.Min(Mathf.FloorToInt(row), Rows - 2);
            var distance = Mathf.Max(bottom - p.y, p.y - top);
            for (var side = 0; side < Sides; side++)
            {
                var edge = Mathf.Lerp(support[lower, side], support[lower + 1, side], row - lower);
                distance = Mathf.Max(distance, p.x * Directions[side].x + p.z * Directions[side].y - edge);
            }
            return distance * scale;
        }

        public Vector3 Contact(bool high, float side, float angle, out Vector3 normal)
        {
            var y = high ? shoulders.y - 0.045f : Mathf.Lerp(waist.y, shoulders.y, 0.57f);
            var row = Row(y);
            var lower = Mathf.Min(Mathf.FloorToInt(row), Rows - 2);
            var center = Center(y);
            var ray = new Vector2(side * Mathf.Sin(angle), -Mathf.Cos(angle));
            var radius = float.PositiveInfinity;
            var face = 0;
            for (var i = 0; i < Sides; i++)
            {
                var slope = Vector2.Dot(ray, Directions[i]);
                if (slope < 0.001f) continue;
                var edge = Mathf.Lerp(support[lower, i], support[lower + 1, i], row - lower);
                var hit = (edge - center.x * Directions[i].x - center.z * Directions[i].y) / slope;
                if (hit >= radius) continue;
                radius = hit;
                face = i;
            }
            normal = toWorld.MultiplyVector(new Vector3(Directions[face].x, 0f, Directions[face].y)).normalized;
            return toWorld.MultiplyPoint3x4(new Vector3(center.x + ray.x * radius, y, center.z + ray.y * radius));
        }

        public Vector3 Inward(float side) => -root.right * side;
        public void Dispose() => UnityEngine.Object.Destroy(baked);
    }

    private sealed class Arm
    {
        private const float UpperRadius = 0.043f;
        private const float LowerRadius = 0.034f;
        private const float PalmRadius = 0.015f;
        private readonly bool high;
        private readonly float side;
        private readonly float scale;
        private readonly Transform shoulder;
        private readonly Transform elbow;
        private readonly Transform wrist;
        private readonly Transform middle;
        private readonly Transform index;
        private readonly Transform little;
        private readonly Transform[] fingerBases;
        private readonly Transform[] fingerMiddles;
        private readonly Transform[] fingerEnds;
        private readonly Quaternion[] originalFingerRotations = new Quaternion[4];
        private readonly Vector3[] fingerOffsets = new Vector3[5];
        private readonly IKSolverLimb solver;
        private bool relaxed;

        public Arm(Animator actor, Transform root, bool right)
        {
            high = right;
            side = right ? -1f : 1f;
            scale = Mathf.Abs(root.lossyScale.y);
            Transform Bone(HumanBodyBones l, HumanBodyBones r) => actor.GetBoneTransform(right ? r : l);
            shoulder = Bone(HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm);
            elbow = Bone(HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm);
            wrist = Bone(HumanBodyBones.LeftHand, HumanBodyBones.RightHand);
            middle = Bone(HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal);
            index = Bone(HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal);
            little = Bone(HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal);
            fingerBases = new[] {index, middle,
                Bone(HumanBodyBones.LeftRingProximal, HumanBodyBones.RightRingProximal), little};
            fingerMiddles = new[] {
                Bone(HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.RightThumbIntermediate),
                Bone(HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.RightIndexIntermediate),
                Bone(HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate),
                Bone(HumanBodyBones.LeftRingIntermediate, HumanBodyBones.RightRingIntermediate),
                Bone(HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.RightLittleIntermediate)
            };
            fingerEnds = new[] {
                Bone(HumanBodyBones.LeftThumbDistal, HumanBodyBones.RightThumbDistal),
                Bone(HumanBodyBones.LeftIndexDistal, HumanBodyBones.RightIndexDistal),
                Bone(HumanBodyBones.LeftMiddleDistal, HumanBodyBones.RightMiddleDistal),
                Bone(HumanBodyBones.LeftRingDistal, HumanBodyBones.RightRingDistal),
                Bone(HumanBodyBones.LeftLittleDistal, HumanBodyBones.RightLittleDistal)
            };
            var pole = new GameObject(right ? "Hug right elbow" : "Hug left elbow").transform;
            pole.SetParent(root, false);
            solver = new IKSolverLimb {
                goal = right ? AvatarIKGoal.RightHand : AvatarIKGoal.LeftHand,
                bendGoal = pole, bendModifier = IKSolverLimb.BendModifier.Goal,
                IKPositionWeight = 1f, IKRotationWeight = 1f
            };
            solver.SetChain(shoulder, elbow, wrist, root);
        }

        private Vector3 Palm => Vector3.Lerp(wrist.position, middle.position, 0.68f);

        public void Fit(Torso partner, float weight, float release)
        {
            var origin = shoulder.position;
            var originalElbow = elbow.position;
            var originalWrist = wrist.position;
            var originalRotation = wrist.rotation;
            var palmOffset = Palm - originalWrist;
            var forward = (middle.position - originalWrist).normalized;
            var outward = Vector3.Cross(forward, index.position - little.position).normalized * side;
            var handFrame = Quaternion.LookRotation(forward, outward);
            for (var i = 0; i < fingerEnds.Length; i++)
                fingerOffsets[i] = Fingertip(i) - originalWrist;

            var upper = Vector3.Distance(origin, originalElbow);
            var lower = Vector3.Distance(originalElbow, originalWrist);
            var best = float.PositiveInfinity;
            var bestWrist = originalWrist;
            var bestElbow = originalElbow;
            var bestRotation = originalRotation;
            var bestOverlap = float.PositiveInfinity;
            var swing = Mathf.Max(1f - weight, release) * 44f;
            for (var route = 0; route < 12; route++)
            {
                // If none of the contact routes clear the sleeve, try an arc
                // just outside the shoulder before letting the wrist settle.
                if (route == 6 && bestOverlap < 0.002f * scale) break;
                var around = route % 6;
                var lift = route < 6 ? 0f : 0.024f;
                var angle = Mathf.Min(112f, (high ? 33f : 40f) + 10f * around + swing);
                var contact = partner.Contact(high, side, angle * Mathf.Deg2Rad, out var normal);
                var along = Vector3.ProjectOnPlane(partner.Inward(side) + Vector3.up * (high ? 0.18f : 0.35f), normal);
                var desired = Quaternion.LookRotation(along, normal) * Quaternion.Inverse(handFrame) * originalRotation;
                var rotation = Quaternion.Slerp(originalRotation, desired, weight);
                var delta = rotation * Quaternion.Inverse(originalRotation);
                var clearance = (PalmRadius + 0.003f + Mathf.Sin(Mathf.PI * weight) * 0.045f) * scale;
                var target = Vector3.Lerp(Palm, contact + normal * clearance, weight) +
                    normal * (lift * scale) - delta * palmOffset;
                // Lift the whole hand out when the knuckles or fingers cross the curved shoulder surface.
                for (var step = 0; step < 2; step++)
                {
                    var overlap = HandOverlap(partner, target, delta, palmOffset);
                    if (overlap <= 0f) break;
                    target += normal * Mathf.Min(0.045f * scale, overlap + 0.001f * scale);
                }
                target = origin + Vector3.ClampMagnitude(target - origin, upper + lower - 0.002f * scale);
                var direction = (target - origin).normalized;
                var length = Vector3.Distance(target, origin);
                if (length < 0.025f * scale) continue;
                var projection = (upper * upper - lower * lower + length * length) / (2f * length);
                var height = Mathf.Sqrt(Mathf.Max(0f, upper * upper - projection * projection));
                var pivot = origin + direction * projection;
                var radial = Vector3.ProjectOnPlane(originalElbow - origin, direction).normalized;
                if (radial.sqrMagnitude < 0.5f) radial = Vector3.ProjectOnPlane(normal, direction).normalized;
                var cross = Vector3.Cross(direction, radial);
                var handError = HandOverlap(partner, target, delta, palmOffset);
                var routeCost = (around * 0.003f + lift) * scale * scale + 900f * handError * handError;
                for (var i = -10; i <= 10; i++)
                {
                    var theta = i * (Mathf.PI / 10f);
                    var pole = pivot + (radial * Mathf.Cos(theta) + cross * Mathf.Sin(theta)) * height;
                    var overlap = ChainOverlap(partner, origin, pole, target);
                    var cost = routeCost + 1000f * overlap * overlap + (pole - originalElbow).sqrMagnitude;
                    if (cost >= best) continue;
                    best = cost;
                    bestWrist = target;
                    bestElbow = pole;
                    bestRotation = rotation;
                    bestOverlap = Mathf.Max(overlap, handError);
                }
            }
            solver.IKPosition = bestWrist;
            solver.IKRotation = bestRotation;
            solver.bendGoal.position = bestElbow;
            solver.Update();
            RelaxFingers(partner, weight);
        }

        private Vector3 Fingertip(int i) => fingerEnds[i].position +
            (fingerEnds[i].position - fingerMiddles[i].position) * 0.48f;

        private void RelaxFingers(Torso torso, float weight)
        {
            var forward = (middle.position - wrist.position).normalized;
            for (var i = 0; i < fingerBases.Length; i++)
            {
                var joint = fingerBases[i];
                originalFingerRotations[i] = joint.localRotation;
                var tip = Fingertip(i + 1);
                var limit = Mathf.Min(torso.Clearance(tip), 0.008f * scale);
                var turn = Quaternion.FromToRotation(tip - joint.position, forward);
                joint.rotation = Quaternion.RotateTowards(joint.rotation, turn * joint.rotation, 7f * weight);
                if (torso.Clearance(Fingertip(i + 1)) < limit)
                    joint.localRotation = originalFingerRotations[i];
            }
            relaxed = true;
        }

        public void RestoreFingerPose()
        {
            if (!relaxed) return;
            for (var i = 0; i < fingerBases.Length; i++)
                fingerBases[i].localRotation = originalFingerRotations[i];
            relaxed = false;
        }

        private float HandOverlap(Torso torso, Vector3 position, Quaternion delta, Vector3 palmOffset)
        {
            var overlap = PalmRadius * scale - torso.Clearance(position + delta * palmOffset);
            for (var i = 0; i < fingerOffsets.Length; i++)
                overlap = Mathf.Max(overlap, 0.008f * scale - torso.Clearance(position + delta * fingerOffsets[i]));
            return Mathf.Max(0f, overlap);
        }

        private float ChainOverlap(Torso torso, Vector3 a, Vector3 b, Vector3 c)
        {
            var overlap = 0f;
            for (var i = 0; i <= 6; i++)
            {
                var t = i / 6f;
                overlap = Mathf.Max(overlap, UpperRadius * scale - torso.Clearance(Vector3.Lerp(a, b, t)));
                overlap = Mathf.Max(overlap, Mathf.Lerp(LowerRadius, 0.022f, t) * scale - torso.Clearance(Vector3.Lerp(b, c, t)));
            }
            return overlap;
        }

        public float Penetration(Torso torso)
        {
            var overlap = Mathf.Max(ChainOverlap(torso, shoulder.position, elbow.position, wrist.position),
                PalmRadius * scale - torso.Clearance(Palm));
            for (var i = 0; i < fingerEnds.Length; i++)
                overlap = Mathf.Max(overlap, 0.008f * scale - torso.Clearance(Fingertip(i)));
            return overlap;
        }

        public float PalmClearance(Torso torso) => torso.Clearance(Palm) - PalmRadius * scale;

        public void Dispose() => UnityEngine.Object.Destroy(solver.bendGoal.gameObject);
    }
}
}
