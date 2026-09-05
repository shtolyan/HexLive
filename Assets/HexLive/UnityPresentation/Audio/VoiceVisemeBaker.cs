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
    public static bool TryBake(byte[] wav, string text, string outputPath, out string error,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadPcm(wav, out var pcm, out error)) return false;
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

    public static bool TryValidateWave(byte[] wav, out int durationMilliseconds, out string error)
    {
        durationMilliseconds = 0;
        if (!TryReadPcm(wav, out var samples, out error)) return false;
        durationMilliseconds = (int)Math.Round(samples.Length * 1000d / 44100);
        return true;
    }

    private static bool TryReadPcm(byte[] wav, out short[] samples, out string error)
    {
        samples = Array.Empty<short>();
        error = "InvalidWave";
        if (wav == null || wav.Length < 44 || wav.Length > 3 * 1024 * 1024 ||
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
        if (format != 1 || channels != 1 || rate != 44100 || byteRate != 88200 ||
            blockAlign != 2 || bits != 16 || dataOffset < 0 || dataBytes < 2 ||
            (dataBytes & 1) != 0 || dataBytes > 44100 * 2 * 30) return false;
        samples = new short[dataBytes / 2];
        Buffer.BlockCopy(wav, dataOffset, samples, 0, dataBytes);
        error = string.Empty;
        return true;
    }
}
}
