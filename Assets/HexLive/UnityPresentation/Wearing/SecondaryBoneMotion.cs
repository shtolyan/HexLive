using System;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Opt-in secondary animation for authored bones (§31B). No cloth, Jobs or
    /// native plugin. The constrained Verlet spring is adapted from Unity's
    /// UnityChanSpringBone (MIT; see SecondaryBoneMotion.NOTICE.txt).
    /// Restore before Animator; solve after Animator. Never changes bone scale.
    /// </summary>
    [DefaultExecutionOrder(11000), DisallowMultipleComponent]
    public sealed class SecondaryBoneMotion : MonoBehaviour
    {
        [Serializable] public struct Settings
        {
            public float stiffness, damping, maxAngle, motionInheritance;
            public Vector3 gravity;
            public float particleRadius;
            public static Settings Hair => new Settings { stiffness = 240f, damping = 24f,
                maxAngle = 14f, motionInheritance = .8f, gravity = new Vector3(0, -.4f, 0), particleRadius = .012f };
            public static Settings Breast => new Settings { stiffness = 420f, damping = 22f,
                maxAngle = 5f, motionInheritance = .85f, gravity = Vector3.zero, particleRadius = .005f };
        }

        public struct Capsule
        {
            public Transform a, b;
            public Vector3 offsetA, offsetB;
            public float radius;
        }

        sealed class Joint
        {
            public Transform bone;
            public Vector3 tipLocal, current, previous;
            public Quaternion animated, written;
            public bool hasWritten;
        }

        Joint[] joints = Array.Empty<Joint>();
        Capsule[] colliders = Array.Empty<Capsule>();
        Transform anchor;
        Vector3 lastAnchor;
        Settings settings;
        bool initialized;
        float previousStepTime;
        public Camera cullingCamera;
        public float maxDistance = 20f;
        public float LastMaxAngle { get; private set; }
        public float LastMaxLengthError { get; private set; }
        public int ResetCount { get; private set; }
        public int JointCount => joints.Length;

        // Bones must be ordered parent first. Endpoints are in each bone's local
        // space, including the terminal virtual tip for a childless breast/lock.
        public void Configure(Transform motionAnchor, Transform[] bones, Vector3[] tips,
            Settings configuration, Capsule[] obstacles = null)
        {
            if (motionAnchor == null || bones == null || tips == null || bones.Length != tips.Length)
                throw new ArgumentException("A motion anchor and matching bone/tip arrays are required");
            RestorePose();
            anchor = motionAnchor;
            settings = configuration;
            colliders = obstacles ?? Array.Empty<Capsule>();
            joints = new Joint[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || tips[i].sqrMagnitude < 1e-10f)
                    throw new ArgumentException("Missing bone or zero-length tip");
                for (int previous = 0; previous < i; previous++)
                    if (bones[i] == bones[previous]) throw new ArgumentException("Duplicate spring bone");
                joints[i] = new Joint { bone = bones[i], tipLocal = tips[i], animated = bones[i].localRotation };
            }
            ResetSimulation();
        }

        public void RestorePose()
        {
            foreach (var joint in joints)
            {
                if (joint?.bone == null || !joint.hasWritten) continue;
                // Do not erase a pose another animation system already wrote.
                if (Quaternion.Angle(joint.bone.localRotation, joint.written) < .001f)
                    joint.bone.localRotation = joint.animated;
                joint.hasWritten = false;
            }
        }

        public void ResetSimulation()
        {
            foreach (var joint in joints)
            {
                if (joint?.bone == null) continue;
                joint.current = joint.previous = joint.bone.TransformPoint(joint.tipLocal);
            }
            if (anchor != null) lastAnchor = anchor.position;
            initialized = true;
            previousStepTime = 0f;
            ResetCount++;
        }

        void Update() => RestorePose();
        void LateUpdate() => Step(Time.deltaTime);
        void OnDisable() { RestorePose(); initialized = false; }

        public void Step(float deltaTime)
        {
            LastMaxAngle = LastMaxLengthError = 0f;
            if (anchor == null || joints.Length == 0 || deltaTime <= 0f) return;
            foreach (var joint in joints)
            {
                if (joint.bone == null) { initialized = false; return; }
                joint.animated = joint.bone.localRotation;
            }
            var shift = anchor.position - lastAnchor;
            if (!initialized || deltaTime > .15f || shift.sqrMagnitude > 1f ||
                (cullingCamera != null && (cullingCamera.transform.position - anchor.position).sqrMagnitude > maxDistance * maxDistance))
            {
                ResetSimulation();
                return;
            }
            lastAnchor = anchor.position;
            var inherited = shift * Mathf.Clamp01(settings.motionInheritance);
            foreach (var joint in joints) { joint.current += inherited; joint.previous += inherited; }

            int steps = Mathf.Clamp(Mathf.CeilToInt(deltaTime * 60f), 1, 8);
            float dt = deltaTime / steps;
            float drag = Mathf.Exp(-Mathf.Max(0, settings.damping) * dt);
            for (int step = 0; step < steps; step++)
            {
                foreach (var joint in joints) joint.bone.localRotation = joint.animated;
                foreach (var joint in joints)
                {
                    var bone = joint.bone;
                    var head = bone.position;
                    var target = bone.TransformPoint(joint.tipLocal);
                    var rest = target - head;
                    float length = rest.magnitude;
                    if (length < .00001f) continue;
                    // Convert the previous Verlet displacement to this step's
                    // duration so a changing render rate does not inject energy.
                    float timeRatio = previousStepTime > 0f ? dt / previousStepTime : 1f;
                    var next = joint.current + (joint.current - joint.previous) * (drag * timeRatio) +
                        (Mathf.Max(0, settings.stiffness) * (target - joint.current) + settings.gravity) * (dt * dt);
                    joint.previous = joint.current;
                    if (!Finite(next)) next = target;
                    for (int iteration = 0; iteration < 3; iteration++)
                    {
                        next = Limit(head, rest, next, length);
                        foreach (var capsule in colliders)
                        {
                            if (capsule.a == null) continue;
                            var a = capsule.a.TransformPoint(capsule.offsetA);
                            var b = capsule.b != null ? capsule.b.TransformPoint(capsule.offsetB) : a;
                            var ab = b - a;
                            float t = ab.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(next - a, ab) / ab.sqrMagnitude) : 0f;
                            var center = a + ab * t;
                            var away = next - center;
                            float radius = capsule.radius + settings.particleRadius;
                            if (away.sqrMagnitude < radius * radius)
                                next = center + (away.sqrMagnitude > 1e-10f ? away.normalized : rest.normalized) * radius;
                        }
                    }
                    joint.current = Limit(head, rest, next, length);
                    bone.rotation = Quaternion.FromToRotation(rest, joint.current - head) * bone.rotation;
                    LastMaxAngle = Mathf.Max(LastMaxAngle, Vector3.Angle(rest, joint.current - head));
                    LastMaxLengthError = Mathf.Max(LastMaxLengthError, Mathf.Abs((joint.current - head).magnitude - length));
                    joint.written = bone.localRotation;
                    joint.hasWritten = true;
                }
                previousStepTime = dt;
            }
        }

        Vector3 Limit(Vector3 head, Vector3 rest, Vector3 next, float length)
        {
            var direction = next - head;
            if (direction.sqrMagnitude < 1e-10f) direction = rest;
            return head + Vector3.RotateTowards(rest.normalized, direction.normalized,
                Mathf.Clamp(settings.maxAngle, 0f, 80f) * Mathf.Deg2Rad, 0f) * length;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
            !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }
}
