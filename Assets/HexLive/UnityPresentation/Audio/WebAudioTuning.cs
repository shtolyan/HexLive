using System;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// §168.6: FMOD start-up for the browser. FMOD for Unity calls this after it
    /// has applied the platform settings and right before Studio::initialize, so
    /// it is the last word on the mixer format.
    /// <para>
    /// Default: FMOD's own 48 kHz mix and the player-verified DSP buffer (4096×4). The
    /// rate is NOT matched to the page: see the comment at setSoftwareFormat.
    /// </para>
    /// <para>
    /// Field tuning without a rebuild, from the page URL (player report
    /// 10.10.2026 «звук хрипит», cause not yet measured):
    /// <c>fmodbuf=2048,8</c> (DSP buffer length,count), <c>fmodrate=48000</c>
    /// (or <c>auto</c>), <c>fmodout=worklet|webaudio</c>, <c>fmodch=24</c>
    /// (real channels). What was applied is logged as <c>[FmodWeb]</c>.
    /// </para>
    /// The asset is assigned to the WebGL platform by HexLiveWebGLPlayerBuild.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Web Audio Tuning")]
    public sealed class WebAudioTuning : FMODUnity.PlatformCallbackHandler
    {
        // #483: PC Chrome player confirmed less/no crackle with fmodbuf=4096,4.
        // Shared by the builder and the last pre-init callback; the ignored FMOD
        // settings asset must never silently restore the older, shorter buffer.
        public const int DefaultBufferLength = 4096;
        public const int DefaultBufferCount = 4;

        public override void PreInitialize(FMOD.Studio.System system, Action<FMOD.RESULT, string> reportResult)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (system.getCoreSystem(out var core) != FMOD.RESULT.OK)
            {
                return;
            }
            var url = Application.absoluteURL ?? string.Empty;

            var outputParam = Param(url, "fmodout");
            if (outputParam == "webaudio" || outputParam == "worklet")
            {
                var output = outputParam == "webaudio" ? FMOD.OUTPUTTYPE.WEBAUDIO : FMOD.OUTPUTTYPE.AUDIOWORKLET;
                reportResult(core.setOutput(output), "FMOD.System.setOutput (url)");
            }

            // Mixer rate: leave FMOD's own 48 kHz. Its AudioWorklet output runs
            // at that rate whatever the device is (measured 11.10.2026: a 44.1 kHz
            // page context, FMOD driver 48000 after init), and the browser
            // converts to the device on the audio thread. Matching the page's
            // 44.1 kHz instead made FMOD itself resample 44.1→48 — the opposite
            // of the intent. Only an explicit fmodrate= changes it.
            core.getSoftwareFormat(out var rate, out var speakers, out var rawSpeakers);
            var driverRate = HexLive.UnityPresentation.Platform.WebPage.AudioSampleRate;
            if (int.TryParse(Param(url, "fmodrate"), out var forced) && forced >= 8000 && forced != rate)
            {
                reportResult(core.setSoftwareFormat(forced, speakers, rawSpeakers), "FMOD.System.setSoftwareFormat (url)");
            }

            ConfigureDspBuffer(core, url, reportResult);

            if (int.TryParse(Param(url, "fmodch"), out var channels) && channels >= 8)
            {
                reportResult(core.setSoftwareChannels(channels), "FMOD.System.setSoftwareChannels (url)");
            }

            core.getOutput(out var finalOutput);
            core.getSoftwareFormat(out var finalRate, out _, out _);
            core.getDSPBufferSize(out var finalLength, out var finalCount);
            core.getSoftwareChannels(out var finalChannels);
            Debug.Log($"[FmodWeb] output {finalOutput}, mix {finalRate} Hz (page context {driverRate}), " +
                      $"buffer {finalLength}x{finalCount}, real channels {finalChannels}");
#endif
        }

        // Kept outside the web-only call site so the exact pre-init FMOD API
        // path can also be verified against the installed native SDK in tests.
        internal static void ConfigureDspBuffer(FMOD.System core, string url,
            Action<FMOD.RESULT, string> reportResult)
        {
            uint length = DefaultBufferLength;
            var count = DefaultBufferCount;
            var buffer = Param(url ?? string.Empty, "fmodbuf");
            if (!string.IsNullOrEmpty(buffer))
            {
                var parts = buffer.Split(',');
                if (parts.Length == 2 && uint.TryParse(parts[0], out var requestedLength) &&
                    int.TryParse(parts[1], out var requestedCount) && requestedLength >= 256 && requestedCount >= 2)
                {
                    length = requestedLength;
                    count = requestedCount;
                }
            }
            reportResult(core.setDSPBufferSize(length, count), "FMOD.System.setDSPBufferSize (web policy)");
        }

        private static string Param(string url, string name)
        {
            var query = url.IndexOf('?');
            if (query < 0)
            {
                return null;
            }
            foreach (var pair in url.Substring(query + 1).Split('&', '#'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name)
                {
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            return null;
        }
    }
}
