using System.IO;
using System.Text;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Рукопожатие везёт экспортированные каталоги целиком — и после разреза записи
/// NPC (§83.2 r12) это самый крупный кусок, который вообще пересекает провод:
/// 530 КБ против ~4.6 КБ/с потока. Один раз на подключение, зато весь сразу.
/// Он же и самый сжимаемый: имена ручек повторяются тысячами.
/// </summary>
public sealed class HandshakeCompressionGateTests
{
    [Test]
    public void RealSimDataSurvivesTheRoundTripAndShrinksAtLeastFivefold()
    {
        var json = File.ReadAllText(RepoPaths.SimData);
        var frame = Encode(new Handshake { Seed = 4242, Tick = 7, SimData = json });

        Assert.That(Decode(frame).SimData, Is.EqualTo(json),
            "каталоги должны доехать байт в байт — на них стоит весь баланс сервера (§59.3)");

        var raw = Encoding.UTF8.GetByteCount(json);
        Assert.That(frame.Length * 5, Is.LessThan(raw),
            $"сжатие не сработало: {frame.Length} B против {raw} B исходных");
    }

    [Test]
    public void EmptySimDataStillRoundTrips()
    {
        var decoded = Decode(Encode(new Handshake { Seed = 1, SimData = string.Empty }));
        Assert.That(decoded.SimData, Is.Empty);
    }

    [Test]
    public void EverythingElseInTheHandshakeSurvivesToo()
    {
        var sent = new Handshake
        {
            Seed = 872812195,
            Mode = 1, // §146: BigIsland ordinal
            Tick = 17420,
            TickDeltaTime = 0.25f,
            SpeedMultiplier = 1f,
            Paused = true,
            EventSeq = 123456789L,
            TopologyChecksum = 0xBADDBEF4,
            SimData = "{\"version\":5}",
        };

        var got = Decode(Encode(sent));

        Assert.Multiple(() =>
        {
            Assert.That(got.Seed, Is.EqualTo(sent.Seed));
            Assert.That(got.Mode, Is.EqualTo(sent.Mode));
            Assert.That(got.Tick, Is.EqualTo(sent.Tick));
            Assert.That(got.TickDeltaTime, Is.EqualTo(sent.TickDeltaTime));
            Assert.That(got.SpeedMultiplier, Is.EqualTo(sent.SpeedMultiplier));
            Assert.That(got.Paused, Is.EqualTo(sent.Paused));
            Assert.That(got.EventSeq, Is.EqualTo(sent.EventSeq));
            Assert.That(got.TopologyChecksum, Is.EqualTo(sent.TopologyChecksum));
            Assert.That(got.SimData, Is.EqualTo(sent.SimData));
        });
    }

    /// <summary>
    /// Длину разжатого объявляет отправитель, и верить ей на слово нельзя:
    /// кадр в двадцать байт не должен уметь попросить гигабайт памяти.
    /// </summary>
    [Test]
    public void AnImplausibleSizeIsRefusedInsteadOfAllocated()
    {
        var frame = Encode(new Handshake { Seed = 1, SimData = "{}" });

        // Подменяем объявленную длину разжатого на 1 ГБ: она лежит сразу за
        // фиксированной шапкой (версия, сид, режим §146, тик, dt, скорость,
        // пауза, seq, сумма).
        const int offset = 4 + 4 + 4 + 4 + 4 + 4 + 1 + 8 + 4;
        System.BitConverter.GetBytes(1_000_000_000).CopyTo(frame, offset);

        Assert.Throws<InvalidDataException>(() => Decode(frame));
    }

    private static byte[] Encode(Handshake handshake)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            handshake.Write(writer);
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static Handshake Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return Handshake.Read(reader);
    }
}
