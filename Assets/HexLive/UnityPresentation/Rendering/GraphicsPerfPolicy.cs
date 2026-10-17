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

            float scale;
            if (HexLive.UnityPresentation.Platform.WebPage.IsWeb)
            {
                // §168.11: the web pipeline asset (Mobile_RPAsset) already
                // carries its own renderScale. The height rule below gave 1.0
                // for any canvas under 1600 rows and silently overwrote it —
                // the browser rendered every pixel at full size. Only the
                // page's ?rscale= measurement knob may change it.
                var requested = HexLive.UnityPresentation.Platform.WebPage.QueryFloat("rscale");
                if (requested == null)
                {
                    return;
                }

                scale = Mathf.Clamp(requested.Value, 0.5f, 1f);
            }
            else
            {
                scale = ReadOverride() ?? Mathf.Clamp(
                    TargetHeightPixels / Mathf.Max(1, Screen.height), MinRenderScale, 1f);
            }
            if (Mathf.Approximately(scale, pipeline.renderScale))
            {
                return;
            }

            pipeline.renderScale = scale;
            Debug.Log(
                $"[GraphicsPerf] renderScale {scale:0.00} for {Screen.width}x{Screen.height} " +
                $"(override with {RenderScaleArgument} <0.5..1>)");
        }

        /// <summary>
        /// §168.11: the browser's post stack. High-quality bloom filtering
        /// (bicubic upsampling over up to six mips) is the expensive half of
        /// the bloom and barely shows on the flat low-poly art, so the web
        /// build drops it. <c>?bloom=0</c> in the page address switches bloom
        /// off entirely — a measurement knob, not a player setting. Player
        /// builds only: in the editor the profile is an asset on disk.
        /// </summary>
        public static void ApplyWebPostProcessing()
        {
            if (Application.isEditor || !HexLive.UnityPresentation.Platform.WebPage.IsWeb)
            {
                return;
            }

            var bloomOff = HexLive.UnityPresentation.Platform.WebPage.QueryFloat("bloom") == 0f;
            foreach (var volume in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                var profile = volume.sharedProfile;
                if (profile == null || !profile.TryGet<Bloom>(out var bloom))
                {
                    continue;
                }

                bloom.highQualityFiltering.Override(false);
                if (bloomOff)
                {
                    bloom.active = false;
                }

                Debug.Log($"[GraphicsPerf] web bloom: {(bloomOff ? "off (?bloom=0)" : "low-quality filtering")}");
            }
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
