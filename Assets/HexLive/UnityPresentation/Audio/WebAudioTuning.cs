using System;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// §168.6: FMOD start-up for the browser. FMOD for Unity calls this after it
    /// has applied the platform settings and right before Studio::initialize, so
    /// it is the last word on the mixer format.
    /// <para>
    /// Default: mix at the browser's own output rate. FMOD mixes at 48 kHz unless
    /// told otherwise; a 44.1 kHz AudioContext then needs a resampler, which
    /// costs CPU on the one thread the game also runs on (FMOD HTML5 docs,
    /// «CPU Overhead»).
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

            core.getSoftwareFormat(out var rate, out var speakers, out var rawSpeakers);
            // Before init FMOD's driver info is a default (44100 on a 48 kHz Mac,
            // measured 11.10.2026); the page's own AudioContext knows the truth.
            var driverRate = HexLive.UnityPresentation.Platform.WebPage.AudioSampleRate;
            if (driverRate <= 0)
            {
                core.getDriverInfo(0, out _, out driverRate, out _, out _);
            }
            var rateParam = Param(url, "fmodrate");
            var wanted = int.TryParse(rateParam, out var forced) && forced >= 8000 ? forced : driverRate;
            if (wanted > 0 && wanted != rate)
            {
                reportResult(core.setSoftwareFormat(wanted, speakers, rawSpeakers), "FMOD.System.setSoftwareFormat (web)");
            }

            var buffer = Param(url, "fmodbuf");
            if (!string.IsNullOrEmpty(buffer))
            {
                var parts = buffer.Split(',');
                if (parts.Length == 2 && uint.TryParse(parts[0], out var length) && int.TryParse(parts[1], out var count) &&
                    length >= 256 && count >= 2)
                {
                    reportResult(core.setDSPBufferSize(length, count), "FMOD.System.setDSPBufferSize (url)");
                }
            }

            if (int.TryParse(Param(url, "fmodch"), out var channels) && channels >= 8)
            {
                reportResult(core.setSoftwareChannels(channels), "FMOD.System.setSoftwareChannels (url)");
            }

            core.getOutput(out var finalOutput);
            core.getSoftwareFormat(out var finalRate, out _, out _);
            core.getDSPBufferSize(out var finalLength, out var finalCount);
            core.getSoftwareChannels(out var finalChannels);
            Debug.Log($"[FmodWeb] output {finalOutput}, mix {finalRate} Hz (driver {driverRate}), " +
                      $"buffer {finalLength}x{finalCount}, real channels {finalChannels}");
#endif
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
