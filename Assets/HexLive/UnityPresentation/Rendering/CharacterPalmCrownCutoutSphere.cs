#nullable enable
using System.Collections.Generic;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
    /// <summary>
    /// Spec §112: one world-space hole around one rendered character. Every
    /// live marker contributes a sphere to the standing-palm crown shader;
    /// neither the camera nor selection state participates in the decision.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(300)]
    public sealed class CharacterPalmCrownCutoutSphere : MonoBehaviour
    {
        public const int MaxShaderSpheres = 64;

        // Unity API must not be called from a MonoBehaviour static initializer:
        // the class can first load while Unity is constructing a component.
        private static int SphereCountId;
        private static int SpheresId;
        private static bool _shaderPropertyIdsReady;
        private static readonly List<CharacterPalmCrownCutoutSphere> Live = new();
        private static readonly Vector4[] SphereScratch = new Vector4[MaxShaderSpheres];

        private static int _uploadedFrame = -1;
        private static bool _overflowWarned;

        private NpcActorView? _actor;
        private float _radius;
        private float _fallbackCenterHeight;

        public void Construct(float radius, float fallbackCenterHeight)
        {
            _radius = Mathf.Max(0f, radius);
            _fallbackCenterHeight = Mathf.Max(0f, fallbackCenterHeight);
            _actor = GetComponent<NpcActorView>();
            _uploadedFrame = -1;
        }

        private void Awake()
        {
            EnsureShaderPropertyIds();
            _actor = GetComponent<NpcActorView>();
        }

        private void OnEnable()
        {
            if (!Live.Contains(this))
            {
                Live.Add(this);
            }

            _uploadedFrame = -1;
        }

        private void OnDisable()
        {
            Live.Remove(this);
            _uploadedFrame = -1;
            UploadSpheres();
        }

        private void LateUpdate()
        {
            // All instances share one global array. The first live marker owns
            // the single upload after HexWorldRenderer and actor LateUpdates.
            if (Live.Count > 0 && Live[0] == this && _uploadedFrame != Time.frameCount)
            {
                UploadSpheres();
            }
        }

        private static void UploadSpheres()
        {
            EnsureShaderPropertyIds();
            var count = 0;
            for (var i = 0; i < Live.Count; i++)
            {
                var marker = Live[i];
                if (marker == null || !marker.isActiveAndEnabled || marker._radius <= 0f)
                {
                    continue;
                }

                if (count >= MaxShaderSpheres)
                {
                    if (!_overflowWarned)
                    {
                        _overflowWarned = true;
                        Debug.LogWarning(
                            $"[PalmCrownCutout] More than {MaxShaderSpheres} characters are visible; " +
                            "the extra crown-cutout spheres are ignored.", marker);
                    }

                    break;
                }

                var center = marker.ResolveCenter();
                SphereScratch[count++] = new Vector4(
                    center.x, center.y, center.z, marker._radius);
            }

            Shader.SetGlobalVectorArray(SpheresId, SphereScratch);
            Shader.SetGlobalInt(SphereCountId, count);
            _uploadedFrame = Time.frameCount;
        }

        private Vector3 ResolveCenter()
        {
            if (_actor != null && _actor.TryGetBodyCenter(out var center))
            {
                return center;
            }

            return transform.position + Vector3.up * _fallbackCenterHeight;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            EnsureShaderPropertyIds();
            Live.Clear();
            _uploadedFrame = -1;
            _overflowWarned = false;
            Shader.SetGlobalInt(SphereCountId, 0);
        }

        private static void EnsureShaderPropertyIds()
        {
            if (_shaderPropertyIdsReady)
            {
                return;
            }

            SphereCountId = Shader.PropertyToID("_CharacterPalmCutoutSphereCount");
            SpheresId = Shader.PropertyToID("_CharacterPalmCutoutSpheres");
            _shaderPropertyIdsReady = true;
        }
    }
}
