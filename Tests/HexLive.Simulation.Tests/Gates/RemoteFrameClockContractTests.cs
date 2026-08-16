using System.IO;
using System.Text;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Клиент, который подключился и не показывает НИЧЕГО, — самая дорогая форма
/// поломки: ошибок нет, связь «живая», пинг 7 мс, а мир пустой.
/// <para>
/// Ровно это и было в первом реальном тесте сервера. <c>RemoteTickClock</c> не
/// отдаёт ни одного кадра, пока не увидит, что кадр ПРИШЁЛ (<c>_started</c>), а
/// единственный вызов «кадр пришёл» стоял ВНУТРИ цикла показа — цикла, который
/// крутится ноль раз, пока часы не стартовали. Замкнутая петля: ничего не
/// падает, просто ни один снапшот не декодируется никогда.
/// </para>
/// </summary>
public sealed class RemoteFrameClockContractTests
{
    private static string Backend() => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Bootstrap", "Remote",
        "RemoteSocketBackend.cs"));

    [Test]
    public void ArrivalsAreReportedToTheClockBeforeItIsAskedWhatToPresent()
    {
        var backend = Backend();

        var arrival = backend.IndexOf("_clock.OnFrameArrived(", System.StringComparison.Ordinal);
        var advance = backend.IndexOf("_clock.Advance(", System.StringComparison.Ordinal);

        Assert.That(arrival, Is.GreaterThan(0), "приход кадров больше никто не сообщает часам");
        Assert.That(advance, Is.GreaterThan(0), "исчез сам вызов показа");
        Assert.That(arrival, Is.LessThan(advance),
            "OnFrameArrived должен стоять ДО Advance: часы не показывают ничего, пока не увидят " +
            "первый приход, и внутри цикла показа этот вызов недостижим (§83)");
    }

    [Test]
    public void PeekedTickMatchesTheDecodedOneForBothFrameKinds()
    {
        var snapshot = new WorldSnapshot { Tick = 4397, Seed = 12345 };

        var keyframe = Encode(w => WorldSnapshotCodec.Write(snapshot, w, false));
        Assert.That(WorldSnapshotCodec.PeekFrameTick(keyframe, keyframe: true), Is.EqualTo(4397));

        // Дельта — от той же базы, поэтому первый кадр кодировщика ключевой;
        // берём ВТОРОЙ, он и есть дельта.
        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(snapshot, false);
        snapshot.Tick = 4398;
        var delta = encoder.Encode(snapshot, false);
        Assert.That(WorldSnapshotCodec.PeekFrameTick(delta, keyframe: false), Is.EqualTo(4398));
    }

    [Test]
    public void PeekRefusesAFrameTooShortToCarryATick()
    {
        Assert.Throws<InvalidDataException>(
            () => WorldSnapshotCodec.PeekFrameTick(new byte[3], keyframe: true));
    }

    private static byte[] Encode(System.Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }
}
