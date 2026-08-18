using System;
using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// §83/§121.9: команды NPC на проводе. Явная таблица «тип → номер → writer →
/// reader», а не рефлексивная сериализация: оба конца линкуют этот файл, и
/// формат не может форкнуться; явный writer — домашний стиль (WorldSnapshotCodec).
/// <para>
/// Забыть команду нельзя: CommandCodecCoverageGate находит рефлексией ВСЕ
/// конкретные <see cref="ISimulationCommand"/> в сборке и роняет билд, если
/// тип не зарегистрирован или не переживает round-trip. Новая команда — один
/// case в <see cref="Write"/> и один в <see cref="Read"/>.
/// </para>
/// <para>
/// ⭐ Номера append-only: это формат провода. Переиспользовать или менять
/// нельзя, новый тип получает max+1. Сервер и клиент сверяют версии в
/// рукопожатии, поэтому таблицы всегда одинаковы с обеих сторон; неизвестный
/// номер — это повреждение потока, не совместимость.
/// </para>
/// </summary>
public static class SimulationCommandCodec
{
    public const int WireVersion = 1;

    // Защита от мусора в потоке: злонамеренный клиент не должен уметь
    // заказать аллокацию на гигабайт одним ushort'ом.
    private const int MaxGroupActors = 1024;

    private enum CommandType : ushort
    {
        SetManualControl = 1,
        MoveTo = 2,
        Interact = 3,
        AttackNpc = 4,
        CarryPerson = 5,
        PutDownPerson = 6,
        PutPersonInBed = 7,
        AttackMob = 8,
        Stop = 9,
        CraftItem = 10,
        TalkTo = 11,
        AidPerson = 12,
        TreatLimbs = 13,
        SelfAction = 14,
        GroupMove = 15,
        GroupStop = 16,
        GroupAttackNpc = 17,
        GroupAttackMob = 18,
        SetGroupManualControl = 19,
        ManageInventory = 20,
        TransferInventory = 21,
        TransferContainer = 22,
        PreyPerson = 23,
        AbusePerson = 24,
        PlaceBuildingPlan = 25,
        PlaceFurnitureSite = 26,
    }

    public static void Write(BinaryWriter w, ISimulationCommand command)
    {
        switch (command)
        {
            case SetManualControlCommand c:
                w.Write((ushort)CommandType.SetManualControl);
                WriteEntity(w, c.Npc);
                w.Write(c.Enabled);
                break;
            case MoveToCommand c:
                w.Write((ushort)CommandType.MoveTo);
                WriteEntity(w, c.Npc);
                WireIo.WriteFloat2(w, c.WorldPosition);
                w.Write(c.Run);
                break;
            case InteractCommand c:
                w.Write((ushort)CommandType.Interact);
                WriteEntity(w, c.Npc);
                w.Write(c.Target.Value);
                w.Write((int)c.Interaction);
                break;
            case AttackNpcCommand c:
                w.Write((ushort)CommandType.AttackNpc);
                WriteEntity(w, c.Npc);
                WriteEntity(w, c.Target);
                break;
            case CarryPersonCommand c:
                w.Write((ushort)CommandType.CarryPerson);
                WriteEntity(w, c.Npc);
                WriteEntity(w, c.Target);
                break;
            case PutDownPersonCommand c:
                w.Write((ushort)CommandType.PutDownPerson);
                WriteEntity(w, c.Npc);
                break;
            case PutPersonInBedCommand c:
                w.Write((ushort)CommandType.PutPersonInBed);
                WriteEntity(w, c.Npc);
                w.Write(c.Bed.Value);
                break;
            case AttackMobCommand c:
                w.Write((ushort)CommandType.AttackMob);
                WriteEntity(w, c.Npc);
                w.Write(c.MobId);
                break;
            case StopCommand c:
                w.Write((ushort)CommandType.Stop);
                WriteEntity(w, c.Npc);
                break;
            case CraftItemCommand c:
                w.Write((ushort)CommandType.CraftItem);
                WriteEntity(w, c.Npc);
                w.Write((int)c.RecipeGoal);
                break;
            case TalkToCommand c:
                w.Write((ushort)CommandType.TalkTo);
                WriteEntity(w, c.Npc);
                WriteEntity(w, c.Target);
                break;
            case AidPersonCommand c:
                w.Write((ushort)CommandType.AidPerson);
                WriteEntity(w, c.Npc);
                WriteEntity(w, c.Target);
                w.Write((int)c.Kind);
                break;
            case TreatLimbsCommand c:
                w.Write((ushort)CommandType.TreatLimbs);
                WriteEntity(w, c.Npc);
                WriteEntity(w, c.Target);
                break;
            case SelfActionCommand c:
                w.Write((ushort)CommandType.SelfAction);
                WriteEntity(w, c.Npc);
                w.Write((int)c.Kind);
                break;
            case GroupMoveCommand c:
                w.Write((ushort)CommandType.GroupMove);
                WriteActors(w, c.Actors);
                WireIo.WriteFloat2(w, c.WorldPosition);
                w.Write(c.Run);
                break;
            case GroupStopCommand c:
                w.Write((ushort)CommandType.GroupStop);
                WriteActors(w, c.Actors);
                break;
            case GroupAttackNpcCommand c:
                w.Write((ushort)CommandType.GroupAttackNpc);
                WriteActors(w, c.Actors);
                WriteEntity(w, c.Target);
                break;
            case GroupAttackMobCommand c:
                w.Write((ushort)CommandType.GroupAttackMob);
                WriteActors(w, c.Actors);
                w.Write(c.MobId);
                break;
            case SetGroupManualControlCommand c:
                w.Write((ushort)CommandType.SetGroupManualControl);
                WriteActors(w, c.Actors);
                w.Write(c.Enabled);
                break;
            case ManageInventoryCommand c:
                w.Write((ushort)CommandType.ManageInventory);
                WriteEntity(w, c.Npc);
                WriteItemRef(w, c.Item);
                w.Write((int)c.Action);
                break;
            case TransferInventoryCommand c:
                w.Write((ushort)CommandType.TransferInventory);
                WriteEntity(w, c.Looter);
                WriteEntity(w, c.Other);
                WriteItemRef(w, c.Item);
                w.Write(c.Count);
                w.Write((int)c.Direction);
                break;
            case PreyPersonCommand c:
                w.Write((ushort)CommandType.PreyPerson);
                WriteEntity(w, c.Npc);
                WriteEntity(w, c.Target);
                break;
            case AbusePersonCommand c:
                w.Write((ushort)CommandType.AbusePerson);
                WriteEntity(w, c.Npc);
                WriteEntity(w, c.Target);
                break;
            case TransferContainerCommand c:
                w.Write((ushort)CommandType.TransferContainer);
                WriteEntity(w, c.Looter);
                w.Write(c.Container.Value);
                w.Write(c.SlotIndex);
                WireIo.WriteString(w, c.ExpectedDefinitionId);
                w.Write(c.Count);
                w.Write((int)c.Direction);
                break;
            case PlaceBuildingPlanCommand c:
                w.Write((ushort)CommandType.PlaceBuildingPlan);
                w.Write(c.Tile.Q);
                w.Write(c.Tile.R);
                w.Write(c.RotationDegrees);
                break;
            case PlaceFurnitureSiteCommand c:
                w.Write((ushort)CommandType.PlaceFurnitureSite);
                WireIo.WriteString(w, c.CatalogId);
                w.Write(c.Tile.Q);
                w.Write(c.Tile.R);
                w.Write(c.RotationDegrees);
                break;
            default:
                throw new NotSupportedException(
                    $"SimulationCommandCodec: незарегистрированный тип команды " +
                    $"{command.GetType().Name} — добавь case в Write и Read " +
                    "(за этим следит CommandCodecCoverageGate).");
        }
    }

    public static ISimulationCommand Read(BinaryReader r)
    {
        var type = (CommandType)r.ReadUInt16();
        switch (type)
        {
            case CommandType.SetManualControl:
                return new SetManualControlCommand(ReadEntity(r), r.ReadBoolean());
            case CommandType.MoveTo:
                return new MoveToCommand(
                    ReadEntity(r), WireIo.ReadFloat2(r), r.ReadBoolean());
            case CommandType.Interact:
                return new InteractCommand(
                    ReadEntity(r), new ObjectId(r.ReadInt32()),
                    (InteractionType)r.ReadInt32());
            case CommandType.AttackNpc:
                return new AttackNpcCommand(ReadEntity(r), ReadEntity(r));
            case CommandType.CarryPerson:
                return new CarryPersonCommand(ReadEntity(r), ReadEntity(r));
            case CommandType.PutDownPerson:
                return new PutDownPersonCommand(ReadEntity(r));
            case CommandType.PutPersonInBed:
                return new PutPersonInBedCommand(
                    ReadEntity(r), new ObjectId(r.ReadInt32()));
            case CommandType.AttackMob:
                return new AttackMobCommand(ReadEntity(r), r.ReadInt32());
            case CommandType.Stop:
                return new StopCommand(ReadEntity(r));
            case CommandType.CraftItem:
                return new CraftItemCommand(ReadEntity(r), (GoalType)r.ReadInt32());
            case CommandType.TalkTo:
                return new TalkToCommand(ReadEntity(r), ReadEntity(r));
            case CommandType.AidPerson:
                return new AidPersonCommand(
                    ReadEntity(r), ReadEntity(r), (AidKind)r.ReadInt32());
            case CommandType.TreatLimbs:
                return new TreatLimbsCommand(ReadEntity(r), ReadEntity(r));
            case CommandType.SelfAction:
                return new SelfActionCommand(
                    ReadEntity(r), (SelfActionKind)r.ReadInt32());
            case CommandType.GroupMove:
                return new GroupMoveCommand(
                    ReadActors(r), WireIo.ReadFloat2(r), r.ReadBoolean());
            case CommandType.GroupStop:
                return new GroupStopCommand(ReadActors(r));
            case CommandType.GroupAttackNpc:
                return new GroupAttackNpcCommand(ReadActors(r), ReadEntity(r));
            case CommandType.GroupAttackMob:
                return new GroupAttackMobCommand(ReadActors(r), r.ReadInt32());
            case CommandType.SetGroupManualControl:
                return new SetGroupManualControlCommand(ReadActors(r), r.ReadBoolean());
            case CommandType.ManageInventory:
                return new ManageInventoryCommand(
                    ReadEntity(r), ReadItemRef(r), (InventoryAction)r.ReadInt32());
            case CommandType.TransferInventory:
                return new TransferInventoryCommand(
                    ReadEntity(r), ReadEntity(r), ReadItemRef(r), r.ReadInt32(),
                    (InventoryTransferDirection)r.ReadInt32());
            case CommandType.PreyPerson:
                return new PreyPersonCommand(ReadEntity(r), ReadEntity(r));
            case CommandType.AbusePerson:
                return new AbusePersonCommand(ReadEntity(r), ReadEntity(r));
            case CommandType.TransferContainer:
                return new TransferContainerCommand(
                    ReadEntity(r), new ObjectId(r.ReadInt32()), r.ReadInt32(),
                    r.ReadString(), r.ReadInt32(),
                    (InventoryTransferDirection)r.ReadInt32());
            case CommandType.PlaceBuildingPlan:
                return new PlaceBuildingPlanCommand(
                    new TileCoord(r.ReadInt32(), r.ReadInt32()), r.ReadSingle());
            case CommandType.PlaceFurnitureSite:
                return new PlaceFurnitureSiteCommand(
                    r.ReadString(), new TileCoord(r.ReadInt32(), r.ReadInt32()),
                    r.ReadSingle());
            default:
                throw new InvalidDataException(
                    $"SimulationCommandCodec: неизвестный номер типа {(ushort)type}.");
        }
    }

    private static void WriteEntity(BinaryWriter w, EntityId id) => w.Write(id.Value);

    private static EntityId ReadEntity(BinaryReader r) => new(r.ReadInt32());

    private static void WriteActors(BinaryWriter w, IReadOnlyList<EntityId> actors)
    {
        w.Write(actors.Count);
        for (var i = 0; i < actors.Count; i++)
        {
            w.Write(actors[i].Value);
        }
    }

    private static List<EntityId> ReadActors(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > MaxGroupActors)
        {
            throw new InvalidDataException(
                $"SimulationCommandCodec: группа из {count} акторов — мусор в потоке.");
        }

        var actors = new List<EntityId>(count);
        for (var i = 0; i < count; i++)
        {
            actors.Add(new EntityId(r.ReadInt32()));
        }

        return actors;
    }

    private static void WriteItemRef(BinaryWriter w, InventoryItemRef item)
    {
        w.Write((int)item.Source);
        w.Write(item.Index);
        WireIo.WriteString(w, item.ExpectedDefinitionId);
    }

    private static InventoryItemRef ReadItemRef(BinaryReader r) => new(
        (InventoryItemSource)r.ReadInt32(), r.ReadInt32(), r.ReadString());
}

}
