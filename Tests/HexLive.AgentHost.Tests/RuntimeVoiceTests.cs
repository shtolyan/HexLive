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
