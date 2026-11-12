using System.IO;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Gates
{
public sealed class WirePropertyProtocolTests
{
    [TestCase(47)]
    [TestCase(46)]
    [TestCase(45)]
    [TestCase(44)]
    [TestCase(43)]
    public void OldSnapshotIsRejectedBeforeReadingAnyObject(int oldVersion)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(oldVersion); stream.Position = 0; using var reader = new BinaryReader(stream);
        var error = Assert.Throws<InvalidDataException>(() => WorldSnapshotCodec.Read(reader, new WorldSnapshot()));
        Assert.That(error.Message, Does.Contain($"expected {WorldSnapshotCodec.WireVersion}"));
    }
    [TestCase(31)]
    [TestCase(30)]
    [TestCase(29)]
    [TestCase(28)]
    public void OldHandshakeCannotConnectToCurrentObjectLayout(int oldVersion)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(oldVersion); stream.Position = 0; using var reader = new BinaryReader(stream);
        var error = Assert.Throws<InvalidDataException>(() => Handshake.Read(reader));
        Assert.That(error.Message, Does.Contain($"expected {Handshake.ProtocolVersion}"));
    }
    [Test]
    public void CurrentHandshakeRoundTripsWithExplicitCompatibilityGate()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        new Handshake { Seed=649, WorldId="test-property-ownership", ControlEnabled=true }.Write(writer);
        stream.Position = 0; using var reader = new BinaryReader(stream);
        Assert.That(reader.ReadInt32(), Is.EqualTo(32)); stream.Position=0;
        var received = Handshake.Read(reader);
        Assert.That(received.WorldId, Is.EqualTo("test-property-ownership"));
        Assert.That(received.ControlEnabled, Is.True);
        Assert.That(WorldSnapshotCodec.WireVersion, Is.EqualTo(48));
    }
}
}
