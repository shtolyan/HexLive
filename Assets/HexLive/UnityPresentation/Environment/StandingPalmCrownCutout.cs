#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §112: swaps only the <c>LeafGreen</c> material slot of an upright
    /// <c>tree.palm</c> for the character-sphere cutout shader. The imported
    /// <c>WoodBark</c> slot stays URP/Lit; felled crowns never call this helper.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandingPalmCrownCutout : MonoBehaviour
    {
        private const string CrownSurface = "LeafGreen";
        private const string ShaderResource = "HexLive/Shaders/StandingPalmCrownCutout";
        private const string ShaderName = "HexLive/StandingPalmCrownCutout";

        private static readonly Dictionary<Material, SharedMaterial> SharedBySource = new();
        private static bool _missingShaderWarned;
        private static bool _missingSurfaceWarned;

        private readonly List<Material> _sources = new();

        private sealed class SharedMaterial
        {
            public Material Cutout = null!;
            public int References;
        }

        public static bool Apply(GameObject palm)
        {
            var shader = Resources.Load<Shader>(ShaderResource) ?? Shader.Find(ShaderName);
            if (shader == null)
            {
                if (!_missingShaderWarned)
                {
                    _missingShaderWarned = true;
                    Debug.LogWarning(
                        "[PalmCrownCutout] Standing-palm shader was not found; crown holes are disabled.",
                        palm);
                }

                return false;
            }

            var owner = palm.GetComponent<StandingPalmCrownCutout>() ??
                palm.AddComponent<StandingPalmCrownCutout>();
            var swapped = 0;
            foreach (var renderer in palm.GetComponentsInChildren<Renderer>(true))
            {
                var slots = renderer.sharedMaterials;
                var touched = false;
                for (var i = 0; i < slots.Length; i++)
                {
                    var source = slots[i];
                    if (!IsCrownSurface(source))
                    {
                        continue;
                    }

                    slots[i] = Acquire(source!, shader);
                    owner._sources.Add(source!);
                    swapped++;
                    touched = true;
                }

                if (touched)
                {
                    renderer.sharedMaterials = slots;
                }
            }

            if (swapped == 0 && !_missingSurfaceWarned)
            {
                _missingSurfaceWarned = true;
                Debug.LogWarning(
                    "[PalmCrownCutout] Standing palm has no LeafGreen material slot; " +
                    "WoodBark was deliberately left untouched.", palm);
            }

            return swapped > 0;
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

        private static Material Acquire(Material source, Shader shader)
        {
            if (!SharedBySource.TryGetValue(source, out var entry) || entry.Cutout == null)
            {
                var alphaClip =
                    source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f ||
                    source.HasProperty("_AlphaCutoffEnable") &&
                    source.GetFloat("_AlphaCutoffEnable") > 0.5f;

                var material = new Material(source)
                {
                    name = source.name + " (standing-palm crown cutout)",
                    shader = shader,
                    hideFlags = HideFlags.DontSave
                };
                material.SetFloat("_AlphaClipOn", alphaClip ? 1f : 0f);

                entry = new SharedMaterial { Cutout = material };
                SharedBySource[source] = entry;
            }

            entry.References++;
            return entry.Cutout;
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _sources.Count; i++)
            {
                Release(_sources[i]);
            }

            _sources.Clear();
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
                Destroy(entry.Cutout);
            }
            else
            {
                DestroyImmediate(entry.Cutout);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            SharedBySource.Clear();
            _missingShaderWarned = false;
            _missingSurfaceWarned = false;
        }
    }
}
