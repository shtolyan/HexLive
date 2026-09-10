using System.Text;
using System.Text.Json;
using HexLive.UnityPresentation.Audio;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class RuntimeVoiceTests
{
    [Test]
    public void RuntimeAlignmentMatchesCanonicalOfflineGoldenForRussianHexkufaAndFallback()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "Fixtures/runtime-lipsync.json")));
        var path = Path.Combine(Path.GetTempPath(), "hexlive-vis-" + Guid.NewGuid().ToString("N") + ".vis");
        try
        {
            foreach (var item in fixture.RootElement.EnumerateArray())
            {
                Assert.That(VoiceVisemeBaker.TryBake(MakeWave(), item.GetProperty("text").GetString()!,
                    path, out var error), Is.True, error);
                var expected = Convert.FromBase64String(item.GetProperty("visBase64").GetString()!);
                var actual = File.ReadAllBytes(path);
                Assert.That(actual, Is.EqualTo(expected), item.GetProperty("name").GetString());
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void RuntimeRejectsCorruptWaveWithoutCreatingVis()
    {
        var wav = MakeWave();
        wav[32] = 4; // inconsistent block alignment
        Assert.That(VoiceVisemeBaker.TryValidateWave(wav, out _, out _), Is.False);
        wav = MakeWave();
        wav[4] ^= 1; // RIFF length
        Assert.That(VoiceVisemeBaker.TryValidateWave(wav, out _, out _), Is.False);
    }

    [Test]
    public void RuntimeBakingSupportsCancellationBeforeExpensiveDsp()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => VoiceVisemeBaker.TryBake(MakeWave(),
            "Привет", "must-not-be-created.vis", out _, stop.Token));
    }

    [Test]
    public void ExternalSpeechLevelMatchesAuthoredTargetWithinPeakCeiling()
    {
        var wav = MakeLevelWave(activeAmplitude: 5193, peakAmplitude: 30000, activeFrames: 10);
        var gain = BakeAndMeasure(wav);
        var activeRms = 5193d / 32768d;
        var afterDb = 20d * Math.Log10(activeRms * gain);
        var finalPeakDb = 20d * Math.Log10(30000d / 32768d * 0.75d * gain);
        Assert.That(afterDb, Is.EqualTo(-13.77d).Within(0.03d));
        Assert.That(finalPeakDb, Is.LessThanOrEqualTo(-1d + 0.001d));
        Assert.That(gain, Is.InRange(1f, (float)Math.Pow(10d, 3d / 20d)));
    }

    [Test]
    public void ExternalSpeechLevelDoesNotRaiseSilenceOrAnIsolatedImpulse()
    {
        Assert.That(BakeAndMeasure(MakeLevelWave(0, 0, 10)), Is.EqualTo(1f));
        Assert.That(BakeAndMeasure(MakeLevelWave(0, short.MaxValue, 1)), Is.EqualTo(1f));
    }

    [Test]
    public void ExternalSpeechLevelLimitsHotPeakInsteadOfClipping()
    {
        var gain = BakeAndMeasure(MakeLevelWave(activeAmplitude: 4500,
            peakAmplitude: short.MaxValue, activeFrames: 10));
        var finalPeak = short.MaxValue / 32768d * 0.75d * gain;
        Assert.That(gain, Is.GreaterThan(1f));
        Assert.That(20d * Math.Log10(finalPeak), Is.EqualTo(-1d).Within(0.01d));
    }

    private static float BakeAndMeasure(byte[] wav)
    {
        var path = Path.Combine(Path.GetTempPath(), "hexlive-level-" + Guid.NewGuid().ToString("N") + ".vis");
        try
        {
            Assert.That(VoiceVisemeBaker.TryBake(wav, "Привет", path, 0.75f,
                out var error, out var gain), Is.True, error);
            return gain;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static byte[] MakeLevelWave(short activeAmplitude, short peakAmplitude, int activeFrames)
    {
        const int frameSamples = 882;
        const int totalFrames = 10;
        var samples = frameSamples * totalFrames;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(44100);
        writer.Write(88200); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var value = i < activeFrames * frameSamples ? activeAmplitude : (short)0;
            if (i == 0) value = peakAmplitude;
            writer.Write(value);
        }
        return stream.ToArray();
    }

    private static byte[] MakeWave()
    {
        const int samples = 88200;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(44100);
        writer.Write(88200); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            writer.Write((short)(i < 4410 || i >= 74970 ? 0 : ((i * 97) % 20001) - 10000));
        return stream.ToArray();
    }
}
