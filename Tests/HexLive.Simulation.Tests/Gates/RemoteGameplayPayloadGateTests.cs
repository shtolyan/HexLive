using System.IO;
using System.Text;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Что едет по проводу ВСЕГДА, а что только при открытой отладочной панели.
/// <para>
/// Граница между ними — не вкусовщина, а разница между «панель пуста, потому
/// что её не просили» и «вкладка листа персонажа пуста, и игрок считает это
/// поломкой игры». Отношения (§57) жили в отладочном блоке, и локально это не
/// было видно ВООБЩЕ: панель читает снапшот напрямую, кодек в местной игре не
/// участвует. Наружу это вылезло только на сервере.
/// </para>
/// </summary>
public sealed class RemoteGameplayPayloadGateTests
{
    [Test]
    public void RelationshipsSurviveWithDebugDetailsOff()
    {
        var sent = new WorldSnapshot { Tick = 7 };
        var npc = new NpcSnapshot
        {
            Id = new EntityId(1),
            DisplayName = "npc.mira.name",
            BottleWaterKind = WaterKind.Coconut
        };
        npc.RelationshipDetails.Add(new RelationshipSnapshot
        {
            OtherId = 2,
            OtherName = "npc.jolly.name",
            Trust = 0.25f,
            Familiarity = 0.5f,
            Affinity = -0.75f,
            LastInteractionTick = 654,
        });

        // Чисто отладочное — оно как раз ехать не должно.
        npc.GoalScores.Add(new GoalScoreSnapshot { Goal = "Drink", FinalScore = 1.5f });
        npc.KnownObjects.Add("tree.palm@1,2");
        sent.Npcs.Add(npc);

        var received = RoundTrip(sent, includeDebugDetails: false);

        Assert.That(received.Npcs, Has.Count.EqualTo(1));
        var got = received.Npcs[0];

        Assert.Multiple(() =>
        {
            Assert.That(got.RelationshipDetails, Has.Count.EqualTo(1),
                "отношения снова уехали в отладочный блок — на сервере вкладка «Отношения» опустеет молча (§57, §83)");
            Assert.That(got.RelationshipDetails[0].OtherId, Is.EqualTo(2));
            Assert.That(got.RelationshipDetails[0].OtherName, Is.EqualTo("npc.jolly.name"));
            Assert.That(got.RelationshipDetails[0].Trust, Is.EqualTo(0.25f));
            Assert.That(got.RelationshipDetails[0].Affinity, Is.EqualTo(-0.75f));
            Assert.That(got.RelationshipDetails[0].LastInteractionTick, Is.EqualTo(654));
            Assert.That(got.BottleWaterKind, Is.EqualTo(WaterKind.Coconut),
                "provenance бутылки — игровой inventory state и обязан ехать без debug-details (§55.4)");

            Assert.That(got.GoalScores, Is.Empty, "дампы панели поехали без спроса — это мегабайты в тик");
            Assert.That(got.KnownObjects, Is.Empty);
        });
    }

    [Test]
    public void DebugDumpsStillRideWhenTheyAreAskedFor()
    {
        var sent = new WorldSnapshot { Tick = 7 };
        var npc = new NpcSnapshot { Id = new EntityId(1) };
        npc.RelationshipDetails.Add(new RelationshipSnapshot { OtherId = 2, OtherName = "x" });
        npc.GoalScores.Add(new GoalScoreSnapshot { Goal = "Drink", FinalScore = 1.5f });
        sent.Npcs.Add(npc);

        var got = RoundTrip(sent, includeDebugDetails: true).Npcs[0];

        Assert.That(got.RelationshipDetails, Has.Count.EqualTo(1));
        Assert.That(got.GoalScores, Has.Count.EqualTo(1));
        Assert.That(got.GoalScores[0].Goal, Is.EqualTo("Drink"));
    }

    private static WorldSnapshot RoundTrip(WorldSnapshot snapshot, bool includeDebugDetails)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WorldSnapshotCodec.Write(snapshot, writer, includeDebugDetails);
            writer.Flush();
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var into = new WorldSnapshot();
        WorldSnapshotCodec.Read(reader, into);
        return into;
    }
}
