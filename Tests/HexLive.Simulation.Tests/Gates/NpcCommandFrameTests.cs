using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §121.9/§83: кадры NpcCommand и CommandResult — корреляция и полный
/// round-trip через фрейминг (не только через кодек команды).
/// </summary>
public sealed class NpcCommandFrameTests
{
    [Test]
    public void NpcCommandFrameRoundTripsWithItsCorrelationId()
    {
        var framed = Frame.NpcCommand(
            42, new MoveToCommand(new EntityId(7), new Float2(1.5f, -2.5f), run: true));

        Assert.That((FrameKind)framed[0], Is.EqualTo(FrameKind.NpcCommand));
        var payload = framed.Skip(1).ToArray();
        var (correlationId, command) = Frame.ReadNpcCommand(payload);

        Assert.That(correlationId, Is.EqualTo(42));
        var move = (MoveToCommand)command;
        Assert.That(move.Npc.Value, Is.EqualTo(7));
        Assert.That(move.WorldPosition.X, Is.EqualTo(1.5f));
        Assert.That(move.Run, Is.True);
    }

    [Test]
    public void CommandResultCarriesTheAdmissionVerdictBothWays()
    {
        var refusal = Frame.CommandResult(
            9, accepted: false, actorId: 3, order: "TalkTo", reason: "ControlledByOther");
        var decodedRefusal = Frame.ReadCommandResult(refusal.Skip(1).ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(decodedRefusal.CorrelationId, Is.EqualTo(9));
            Assert.That(decodedRefusal.Accepted, Is.False);
            Assert.That(decodedRefusal.ActorId, Is.EqualTo(3));
            Assert.That(decodedRefusal.Order, Is.EqualTo("TalkTo"));
            Assert.That(decodedRefusal.Reason, Is.EqualTo("ControlledByOther"));
        });
    }

    [Test]
    public void TrailingBytesAfterACommandAreAnErrorNotASilentSkip()
    {
        var framed = Frame.NpcCommand(1, new StopCommand(new EntityId(2)));
        var payload = framed.Skip(1).Concat(new byte[] { 0xAA }).ToArray();

        Assert.Throws<System.IO.InvalidDataException>(
            () => Frame.ReadNpcCommand(payload),
            "Лишние байты за командой — рассинхрон кодека и фрейма; молча " +
            "проглотить их значит прочесть следующий кадр с середины.");
    }
}

}
