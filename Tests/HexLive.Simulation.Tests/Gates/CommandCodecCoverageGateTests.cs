using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ §83: НОВАЯ КОМАНДА ⇒ СТРОКА В SimulationCommandCodec — и это держится не
/// дисциплиной. Забытая команда на проводе — это молчаливый провал: локально
/// клик работает, а по сети (и в -hexlive-loopback) команда не доедет вовсе.
/// <para>
/// Два зуба: (1) рефлексия находит ВСЕ конкретные ISimulationCommand в сборке
/// и требует для каждого проштампованный образец здесь — новый тип роняет
/// тест с именем, а не теряется; (2) каждый образец гоняется через
/// Write→Read→Write, и байты обязаны совпасть — перепутанные местами поля
/// одного типа ловятся разными штампами на разных позициях.
/// </para>
/// </summary>
public sealed class CommandCodecCoverageGateTests
{
    // Каждому конкретному типу — образец с НЕ-дефолтными и попарно РАЗНЫМИ
    // значениями полей (чтобы reader, перепутавший Npc и Target, не прошёл).
    private static readonly ISimulationCommand[] Stamped =
    {
        new SetManualControlCommand(new EntityId(11), true),
        new MoveToCommand(new EntityId(12), new Float2(3.5f, -7.25f), run: true),
        new InteractCommand(new EntityId(13), new ObjectId(77), InteractionType.Harvest),
        new AttackNpcCommand(new EntityId(14), new EntityId(41)),
        new CarryPersonCommand(new EntityId(15), new EntityId(51)),
        new PutDownPersonCommand(new EntityId(16)),
        new PutPersonInBedCommand(new EntityId(17), new ObjectId(71)),
        new AttackMobCommand(new EntityId(18), 81),
        new StopCommand(new EntityId(19)),
        new CraftItemCommand(new EntityId(20), GoalType.CraftBandage),
        new TalkToCommand(new EntityId(21), new EntityId(112)),
        new AidPersonCommand(new EntityId(22), new EntityId(122), AidKind.Hydrate),
        new TreatLimbsCommand(new EntityId(23), new EntityId(132)),
        new SelfActionCommand(new EntityId(24), SelfActionKind.WashClothes),
        new GroupMoveCommand(
            new[] { new EntityId(25), new EntityId(26) },
            new Float2(-1.5f, 9.75f), run: true),
        new GroupStopCommand(new[] { new EntityId(27), new EntityId(28) }),
        new GroupAttackNpcCommand(
            new[] { new EntityId(29), new EntityId(30) }, new EntityId(93)),
        new GroupAttackMobCommand(
            new[] { new EntityId(31), new EntityId(32) }, 94),
        new SetGroupManualControlCommand(
            new[] { new EntityId(33), new EntityId(34) }, enabled: true),
        new ManageInventoryCommand(
            new EntityId(35),
            new InventoryItemRef(InventoryItemSource.Worn, 3, "underwear.bra"),
            InventoryAction.Drop),
        new TransferInventoryCommand(
            new EntityId(36), new EntityId(63),
            new InventoryItemRef(InventoryItemSource.Carried, 2, "tool.knife"),
            count: 4, InventoryTransferDirection.Give),
        new TransferContainerCommand(
            new EntityId(37), new ObjectId(73), slotIndex: 5,
            expectedDefinitionId: "item.bandage", count: 2,
            InventoryTransferDirection.Take),
        new PreyPersonCommand(new EntityId(38), new EntityId(83)),
        new AbusePersonCommand(new EntityId(39), new EntityId(84)),
        new PlaceBuildingPlanCommand(new TileCoord(4, -6), rotationDegrees: 120f),
        new PlaceFurnitureSiteCommand(
            "station.drying_rack", new TileCoord(-3, 8), rotationDegrees: 300f),
    };

    [Test]
    public void EveryConcreteCommandTypeHasAStampedSample()
    {
        var concrete = typeof(ISimulationCommand).Assembly.GetTypes()
            .Where(t => typeof(ISimulationCommand).IsAssignableFrom(t) &&
                        t.IsClass && !t.IsAbstract)
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var sampled = Stamped
            .Select(c => c.GetType().FullName)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.That(sampled, Is.EquivalentTo(concrete),
            "Множество конкретных ISimulationCommand разошлось с образцами " +
            "этого гейта. Новая команда обязана получить: (1) проштампованный " +
            "образец здесь, (2) case в SimulationCommandCodec.Write и Read.");
    }

    [Test]
    public void EveryStampedCommandSurvivesTheRoundTripByteForByte()
    {
        foreach (var original in Stamped)
        {
            byte[] first;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                SimulationCommandCodec.Write(w, original);
                w.Flush();
                first = ms.ToArray();
            }

            ISimulationCommand decoded;
            using (var ms = new MemoryStream(first))
            using (var r = new BinaryReader(ms))
            {
                decoded = SimulationCommandCodec.Read(r);
                Assert.That(ms.Position, Is.EqualTo(ms.Length),
                    $"{original.GetType().Name}: reader не дочитал payload — " +
                    "следующая команда в потоке прочтётся с середины.");
            }

            Assert.That(decoded.GetType(), Is.EqualTo(original.GetType()));

            byte[] second;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                SimulationCommandCodec.Write(w, decoded);
                w.Flush();
                second = ms.ToArray();
            }

            Assert.That(second, Is.EqualTo(first),
                $"{original.GetType().Name}: перекодированные байты разошлись — " +
                "reader и writer читают/пишут разные поля.");
        }
    }
}

}
