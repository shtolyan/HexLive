#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §112: registers only the <c>LeafGreen</c> submesh of an upright
    /// <c>tree.palm</c>. A camera-side manager switches that material slot to
    /// a shadow-only material near the lens; the trunk and felled foliage keep
    /// their original materials.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandingPalmCrownVisibility : MonoBehaviour
    {
        private const string CrownSurface = "LeafGreen";
        private const string ShaderResource = "HexLive/Shaders/StandingPalmCrownShadowOnly";
        private const string ShaderName = "HexLive/StandingPalmCrownShadowOnly";

        private static readonly Dictionary<Material, SharedMaterial> SharedBySource = new();
        private static readonly List<StandingPalmCrownVisibility> Active = new();
        private static int _registryVersion;
        private static bool _missingShaderWarned;
        private static bool _missingSurfaceWarned;

        private readonly List<CrownRenderer> _renderers = new();
        private readonly List<Material> _sources = new();
        private bool _configured;
        private bool _registered;
        private bool _hidden;

        internal static IReadOnlyList<StandingPalmCrownVisibility> ActiveCrowns => Active;
        internal static int RegistryVersion => _registryVersion;
        internal bool IsHidden => _hidden;

        private sealed class CrownRenderer
        {
            public Renderer Renderer = null!;
            public Material[] VisibleMaterials = null!;
            public Material[] HiddenMaterials = null!;
            public Bounds LocalCrownBounds;
        }

        private sealed class SharedMaterial
        {
            public Material ShadowOnly = null!;
            public int References;
        }

        // Bug #339-adjacent: AtomicResources.Load асинхронный — на холодном
        // серверном входе бандл шейдера ещё качается, первый Load отдаёт null,
        // и все пальмы стартовой сцены навсегда оставались без скрытия кроны
        // («shadow-only shader was not found» в Player.log). Не готов — палъма
        // встаёт в очередь и дожимается, когда шейдер приедет (RetryPending
        // зовёт камера-сторона каждый кадр, пока очередь не пуста).
        private static readonly List<GameObject> PendingPalms = new();

        public static bool Apply(GameObject palm)
        {
            var shader = HexLive.UnityPresentation.Content.AtomicResources.Load<Shader>(ShaderResource) ?? Shader.Find(ShaderName);
            if (shader == null)
            {
                if (!PendingPalms.Contains(palm))
                {
                    PendingPalms.Add(palm);
                }

                if (!_missingShaderWarned)
                {
                    _missingShaderWarned = true;
                    Debug.LogWarning(
                        "[PalmCrownVisibility] Standing-palm shadow-only shader is not loaded yet; " +
                        "crown hiding for this palm is queued until the bundle arrives.",
                        palm);
                }

                return false;
            }

            return ApplyWithShader(palm, shader);
        }

        /// <summary>Дожать пальмы, построенные до приезда бандла шейдера.
        /// Дёшево: при пустой очереди — одна проверка счётчика.</summary>
        internal static void RetryPending()
        {
            if (PendingPalms.Count == 0)
            {
                return;
            }

            var shader = HexLive.UnityPresentation.Content.AtomicResources.Load<Shader>(ShaderResource) ?? Shader.Find(ShaderName);
            if (shader == null)
            {
                return;
            }

            for (var i = PendingPalms.Count - 1; i >= 0; i--)
            {
                var palm = PendingPalms[i];
                if (palm != null)
                {
                    ApplyWithShader(palm, shader);
                }
            }

            PendingPalms.Clear();
        }

        private static bool ApplyWithShader(GameObject palm, Shader shader)
        {
            var owner = palm.GetComponent<StandingPalmCrownVisibility>() ??
                palm.AddComponent<StandingPalmCrownVisibility>();
            if (owner._configured)
            {
                return owner._renderers.Count > 0;
            }

            return owner.Configure(palm, shader);
        }

        internal bool TryGetWorldCrownBounds(out Bounds worldBounds)
        {
            worldBounds = default;
            var found = false;
            for (var i = 0; i < _renderers.Count; i++)
            {
                var entry = _renderers[i];
                if (entry.Renderer == null)
                {
                    continue;
                }

                var next = TransformBounds(
                    entry.Renderer.transform.localToWorldMatrix,
                    entry.LocalCrownBounds);
                if (!found)
                {
                    worldBounds = next;
                    found = true;
                }
                else
                {
                    worldBounds.Encapsulate(next.min);
                    worldBounds.Encapsulate(next.max);
                }
            }

            return found;
        }

        internal void SetHidden(bool hidden)
        {
            if (_hidden == hidden)
            {
                return;
            }

            _hidden = hidden;
            for (var i = 0; i < _renderers.Count; i++)
            {
                var entry = _renderers[i];
                if (entry.Renderer != null)
                {
                    entry.Renderer.sharedMaterials = hidden
                        ? entry.HiddenMaterials
                        : entry.VisibleMaterials;
                }
            }
        }

        internal static void ShowAllRegistered()
        {
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var crown = Active[i];
                if (crown != null)
                {
                    crown.SetHidden(false);
                }
            }
        }

        private bool Configure(GameObject palm, Shader shader)
        {
            var crownSlots = 0;
            foreach (var renderer in palm.GetComponentsInChildren<Renderer>(true))
            {
                var visible = renderer.sharedMaterials;
                var hidden = (Material[])visible.Clone();
                var touched = false;
                var hasCrownBounds = false;
                var crownBounds = default(Bounds);

                for (var i = 0; i < visible.Length; i++)
                {
                    var source = visible[i];
                    if (!IsCrownSurface(source))
                    {
                        continue;
                    }

                    hidden[i] = Acquire(source!, shader);
                    _sources.Add(source!);
                    crownSlots++;
                    touched = true;

                    if (TryGetSubMeshBounds(renderer, i, out var subMeshBounds))
                    {
                        if (!hasCrownBounds)
                        {
                            crownBounds = subMeshBounds;
                            hasCrownBounds = true;
                        }
                        else
                        {
                            crownBounds.Encapsulate(subMeshBounds.min);
                            crownBounds.Encapsulate(subMeshBounds.max);
                        }
                    }
                }

                if (!touched)
                {
                    continue;
                }

                // The imported palm exposes submesh bounds without readable
                // vertex data. Keep the renderer bound as a safe fallback for
                // another future palm asset with a different mesh layout.
                if (!hasCrownBounds)
                {
                    crownBounds = renderer.localBounds;
                }

                _renderers.Add(new CrownRenderer
                {
                    Renderer = renderer,
                    VisibleMaterials = visible,
                    HiddenMaterials = hidden,
                    LocalCrownBounds = crownBounds
                });
            }

            _configured = true;
            if (crownSlots == 0)
            {
                if (!_missingSurfaceWarned)
                {
                    _missingSurfaceWarned = true;
                    Debug.LogWarning(
                        "[PalmCrownVisibility] Standing palm has no LeafGreen material slot; " +
                        "WoodBark was deliberately left untouched.", palm);
                }

                return false;
            }

            if (isActiveAndEnabled)
            {
                Register();
            }

            return true;
        }

        private static bool IsCrownSurface(Material? material)
        {
            if (material == null)
            {
                return false;
            }

            var surface = material.name.Replace(" (Instance)", string.Empty);
            return string.Equals(surface, CrownSurface, StringComparison.Ordinal);
        }

        private static bool TryGetSubMeshBounds(
            Renderer renderer, int materialIndex, out Bounds bounds)
        {
            Mesh? mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                mesh = skinned.sharedMesh;
            }
            else if (renderer.TryGetComponent<MeshFilter>(out var filter))
            {
                mesh = filter.sharedMesh;
            }

            if (mesh == null || materialIndex < 0 || materialIndex >= mesh.subMeshCount)
            {
                bounds = default;
                return false;
            }

            bounds = mesh.GetSubMesh(materialIndex).bounds;
            return bounds.size.sqrMagnitude > Mathf.Epsilon;
        }

        private static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
        {
            var center = matrix.MultiplyPoint3x4(localBounds.center);
            var extents = localBounds.extents;
            var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            var axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
            var axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
            var worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, worldExtents * 2f);
        }

        private static Material Acquire(Material source, Shader shader)
        {
            if (!SharedBySource.TryGetValue(source, out var entry) || entry.ShadowOnly == null)
            {
                var alphaClip = source.IsKeywordEnabled("_ALPHATEST_ON") ||
                    source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f ||
                    source.HasProperty("_AlphaCutoffEnable") &&
                    source.GetFloat("_AlphaCutoffEnable") > 0.5f;

                var material = new Material(source)
                {
                    name = source.name + " (standing-palm crown shadow-only)",
                    shader = shader,
                    hideFlags = HideFlags.DontSave,
                    enableInstancing = true
                };
                material.SetFloat("_AlphaClipOn", alphaClip ? 1f : 0f);

                entry = new SharedMaterial { ShadowOnly = material };
                SharedBySource[source] = entry;
            }

            entry.References++;
            return entry.ShadowOnly;
        }

        private void OnEnable()
        {
            if (_configured)
            {
                Register();
            }
        }

        private void OnDisable()
        {
            Unregister();
            SetHidden(false);
        }

        private void OnDestroy()
        {
            Unregister();
            SetHidden(false);
            for (var i = 0; i < _sources.Count; i++)
            {
                Release(_sources[i]);
            }

            _sources.Clear();
            _renderers.Clear();
        }

        private void Register()
        {
            if (_registered || _renderers.Count == 0)
            {
                return;
            }

            Active.Add(this);
            _registered = true;
            _registryVersion++;
        }

        private void Unregister()
        {
            if (!_registered)
            {
                return;
            }

            Active.Remove(this);
            _registered = false;
            _registryVersion++;
        }

        private static void Release(Material source)
        {
            if (!SharedBySource.TryGetValue(source, out var entry))
            {
                return;
            }

            entry.References--;
            if (entry.References > 0)
            {
                return;
            }

            SharedBySource.Remove(source);
            if (Application.isPlaying)
            {
                Destroy(entry.ShadowOnly);
            }
            else
            {
                DestroyImmediate(entry.ShadowOnly);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Active.Clear();
            SharedBySource.Clear();
            PendingPalms.Clear();
            _registryVersion = 0;
            _missingShaderWarned = false;
            _missingSurfaceWarned = false;
        }
    }
}
