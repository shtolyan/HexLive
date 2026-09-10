#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace HexLive.UnityPresentation.Audio
{
/// <summary>§160 bounded runtime WAV → canonical §67.7 HXLS timeline.</summary>
public static class VoiceVisemeBaker
{
    private const int SampleRate = 44100;
    private const int LevelFrameSamples = SampleRate / 50; // 20 ms
    private const int MinimumActiveFrames = 5;             // reject clicks/impulses
    private const double ActiveThreshold = 0.005623413251903491; // -45 dBFS
    private const double TargetActiveRms = 0.204880204996;       // -13.77 dBFS
    private const double MaximumBoost = 1.412537544623;          // +3 dB
    private const double OutputPeakCeiling = 0.891250938134;     // -1 dBFS

    public static bool TryBake(byte[] wav, string text, string outputPath, out string error,
        CancellationToken cancellationToken = default)
        => TryBake(wav, text, outputPath, 1f, out error, out _, cancellationToken);

    /// <summary>
    /// Bakes the canonical lip-sync sidecar and measures a bounded playback
    /// gain from that same decoded PCM. The gain raises only sustained active
    /// speech, never attenuation, and keeps the final channel peak below the
    /// supplied FMOD base gain's -1 dBFS ceiling (§67.15, #386).
    /// </summary>
    public static bool TryBake(byte[] wav, string text, string outputPath, float playbackBaseGain,
        out string error, out float playbackGain, CancellationToken cancellationToken = default)
    {
        playbackGain = 1f;
        if (!TryReadPcm(wav, out var pcm, out error)) return false;
        playbackGain = MeasurePlaybackGain(pcm, playbackBaseGain);
        try
        {
            var frames = RuntimeVoiceAlignment.Bake(pcm, text, cancellationToken);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write(Encoding.ASCII.GetBytes("HXLS"));
            writer.Write((ushort)1);
            writer.Write((byte)14);
            writer.Write((byte)60);
            writer.Write((ushort)frames.Length);
            writer.Write(pcm.Length);
            foreach (var frame in frames) writer.Write(frame);
            return true;
        }
        catch (IOException) { error = "VisemeWriteFailed"; return false; }
        catch (UnauthorizedAccessException) { error = "VisemeWriteFailed"; return false; }
    }

    private static float MeasurePlaybackGain(short[] samples, float playbackBaseGain)
    {
        if (samples.Length == 0 || float.IsNaN(playbackBaseGain) ||
            float.IsInfinity(playbackBaseGain) || playbackBaseGain <= 0f) return 1f;

        long peak = 0;
        double activeSquares = 0d;
        var activeSamples = 0;
        var activeFrames = 0;
        for (var start = 0; start < samples.Length; start += LevelFrameSamples)
        {
            var count = Math.Min(LevelFrameSamples, samples.Length - start);
            double squares = 0d;
            for (var i = start; i < start + count; i++)
            {
                var value = (long)samples[i];
                var magnitude = Math.Abs(value);
                if (magnitude > peak) peak = magnitude;
                squares += value * value;
            }

            var frameRms = Math.Sqrt(squares / count) / 32768d;
            if (frameRms < ActiveThreshold) continue;
            activeFrames++;
            activeSamples += count;
            activeSquares += squares;
        }

        // Silence, background hiss and isolated transients are not speech.
        if (activeFrames < MinimumActiveFrames || activeSamples == 0 || peak == 0) return 1f;
        var activeRms = Math.Sqrt(activeSquares / activeSamples) / 32768d;
        if (activeRms <= 0d) return 1f;

        var targetGain = TargetActiveRms / activeRms;
        var peakGain = OutputPeakCeiling / ((peak / 32768d) * playbackBaseGain);
        var gain = Math.Min(MaximumBoost, Math.Min(targetGain, peakGain));
        return gain > 1d && !double.IsNaN(gain) && !double.IsInfinity(gain) ? (float)gain : 1f;
    }

    public static bool TryValidateWave(byte[] wav, out int durationMilliseconds, out string error)
    {
        durationMilliseconds = 0;
        if (!TryReadPcm(wav, out var samples, out error)) return false;
        durationMilliseconds = (int)Math.Round(samples.Length * 1000d / SampleRate);
        return true;
    }

    private static bool TryReadPcm(byte[] wav, out short[] samples, out string error)
    {
        samples = Array.Empty<short>();
        error = "InvalidWave";
        if (wav == null || wav.Length < 44 || wav.Length > 6 * 1024 * 1024 ||
            Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(wav, 8, 4) != "WAVE" ||
            BitConverter.ToUInt32(wav, 4) != wav.Length - 8) return false;
        ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
        uint rate = 0, byteRate = 0;
        var fmtSeen = false;
        var dataOffset = -1;
        var dataBytes = 0;
        var offset = 12;
        while (offset < wav.Length)
        {
            if (wav.Length - offset < 8) return false;
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (size < 0 || offset + 8L + size + (size & 1) > wav.Length) return false;
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            if (id == "fmt ")
            {
                if (fmtSeen || size < 16) return false;
                fmtSeen = true;
                format = BitConverter.ToUInt16(wav, offset + 8);
                channels = BitConverter.ToUInt16(wav, offset + 10);
                rate = BitConverter.ToUInt32(wav, offset + 12);
                byteRate = BitConverter.ToUInt32(wav, offset + 16);
                blockAlign = BitConverter.ToUInt16(wav, offset + 20);
                bits = BitConverter.ToUInt16(wav, offset + 22);
            }
            else if (id == "data")
            {
                if (dataOffset >= 0) return false;
                dataOffset = offset + 8;
                dataBytes = size;
            }
            offset += 8 + size + (size & 1);
        }
        error = "WaveMustBePcm16Mono44100";
        if (format != 1 || channels != 1 || rate != SampleRate || byteRate != SampleRate * 2 ||
            blockAlign != 2 || bits != 16 || dataOffset < 0 || dataBytes < 2 ||
            (dataBytes & 1) != 0 || dataBytes > SampleRate * 2 * 30) return false;
        samples = new short[dataBytes / 2];
        Buffer.BlockCopy(wav, dataOffset, samples, 0, dataBytes);
        error = string.Empty;
        return true;
    }
}
}
