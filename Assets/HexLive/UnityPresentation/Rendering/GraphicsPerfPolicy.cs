#nullable enable
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HexLive.UnityPresentation.Rendering
{
    /// <summary>
    /// PERF: player builds on Retina/4K displays are fill-rate bound at native
    /// resolution (profiled Jul-2026 — full-res SSAO, bloom and decal passes
    /// over ~8M pixels). This caps the URP render scale so the world renders
    /// at roughly 1600 rows and upscales to the display; the flat low-poly art
    /// survives the linear upscale with no visible cost, while pixel work
    /// drops by ~40-50 %. A 1080p/1440p display is left at native 1.0.
    ///
    /// `-hexlive-renderscale &lt;value&gt;` overrides the policy (1 = native, off).
    /// The editor is never touched: the pipeline asset is a ScriptableObject,
    /// and mutating it in the editor would dirty the asset on disk.
    /// </summary>
    public static class GraphicsPerfPolicy
    {
        private const string RenderScaleArgument = "-hexlive-renderscale";
        private const float TargetHeightPixels = 1600f;
        private const float MinRenderScale = 0.7f;

        public static void ApplyRenderScale()
        {
            if (Application.isEditor)
            {
                return;
            }

            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset pipeline)
            {
                return;
            }

            var scale = ReadOverride() ?? Mathf.Clamp(
                TargetHeightPixels / Mathf.Max(1, Screen.height), MinRenderScale, 1f);
            if (Mathf.Approximately(scale, pipeline.renderScale))
            {
                return;
            }

            pipeline.renderScale = scale;
            Debug.Log(
                $"[GraphicsPerf] renderScale {scale:0.00} for {Screen.width}x{Screen.height} " +
                $"(override with {RenderScaleArgument} <0.5..1>)");
        }

        private static float? ReadOverride()
        {
            string[] args;
            try
            {
                // Fully qualified: the sibling namespace
                // HexLive.UnityPresentation.Environment shadows System.Environment.
                args = System.Environment.GetCommandLineArgs();
            }
            catch (System.Exception)
            {
                return null;
            }

            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], RenderScaleArgument, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (float.TryParse(
                        args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    // URP clamps renderScale to [0.1, 2]; mirror the floor so a
                    // typo cannot render the game as a postage stamp.
                    return Mathf.Clamp(value, 0.5f, 1f);
                }
            }

            return null;
        }
    }
}
