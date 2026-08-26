using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §83.4: кадр <see cref="FrameKind.Compressed"/> — целый другой кадр,
/// сжатый gzip'ом для соединений, объявивших поддержку заголовком upgrade.
/// Раунд-трип и отказ от неправдоподобных длин (той же меркой, что simdata
/// в рукопожатии).
/// </summary>
public sealed class CompressedFrameTests
{
    [Test]
    public void CompressedFrameRoundTripsTheInnerFrameByteForByte()
    {
        // Повторяющийся паттерн — как реальный кейфрейм — обязан ужаться.
        var payload = new byte[64 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 32);
        }

        var inner = Frame.Wrap(FrameKind.Snapshot, payload);
        var compressed = Frame.Compress(inner);

        Assert.That((FrameKind)compressed[0], Is.EqualTo(FrameKind.Compressed));
        Assert.That(compressed.Length, Is.LessThan(inner.Length),
            "a repetitive frame this size must actually shrink");

        var roundTripped = Frame.Decompress(compressed.Skip(1).ToArray());
        Assert.That(roundTripped, Is.EqualTo(inner));
    }

    [Test]
    public void CompressedFrameWithImplausibleLengthsIsRefused()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(int.MaxValue); // заявленный размер сырого кадра
        writer.Write(16);
        writer.Write(new byte[16]);
        writer.Flush();

        Assert.Throws<InvalidDataException>(() => Frame.Decompress(stream.ToArray()));
    }

    [Test]
    public void TruncatedCompressedFrameIsRefusedNotMisread()
    {
        var inner = Frame.Wrap(FrameKind.Snapshot, new byte[8 * 1024]);
        var compressed = Frame.Compress(inner).Skip(1).ToArray();
        var truncated = compressed.Take(compressed.Length / 2).ToArray();

        Assert.That(
            () => Frame.Decompress(truncated),
            Throws.InstanceOf<InvalidDataException>().Or.InstanceOf<EndOfStreamException>());
    }
}

}
