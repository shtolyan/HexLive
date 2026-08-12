using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: голой при чужаке не ходят. В жару раздеться можно — но только когда
/// рядом некому смотреть; про чужака стало известно — прикрываются, и минимум
/// это таз и грудь, независимо от того, есть ли в вещи защита.
/// </summary>
public sealed class ModestyTests
{
    private const string Bra = "underwear.bra_riot";
    private const string Panties = "underwear.thong_anarchy";

    private static (WorldState world, NPCState npc) NakedColonist()
    {
        var world = TestWorld.CreateWorld(12345);
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.WornItems.Clear();
        npc.Memory.Dangers.Clear();
        return (world, npc);
    }

    [Test]
    public void BareHipsOrChestCountAsMissingCoverAndClothesFixIt()
    {
        var (world, npc) = NakedColonist();
        Assert.That(ModestyMath.MissingCover(world, npc), Is.True, "Голая, а прикрываться нечего.");

        npc.WornItems.Add(new ItemInstance(Bra) { OwnerId = npc.Id.Value });
        Assert.That(ModestyMath.MissingCover(world, npc), Is.True,
            "Один лифчик закрыл и таз тоже?");

        npc.WornItems.Add(new ItemInstance(Panties) { OwnerId = npc.Id.Value });
        Assert.That(ModestyMath.MissingCover(world, npc), Is.False,
            "Таз и грудь закрыты, а её всё ещё считают раздетой.");
    }

    /// <summary>⭐ Бельё — полноценный ответ: оно не греет и не защищает, но закрывает.</summary>
    [Test]
    public void UnderwearCountsAsCoverEvenWithoutArmorOrWarmth()
    {
        var (world, npc) = NakedColonist();

        Assert.That(ModestyMath.CoverGainFromWearing(world, npc, Panties), Is.EqualTo(1),
            "Трусы не засчитаны как прикрытие таза.");
        Assert.That(ModestyMath.CoverGainFromWearing(world, npc, Bra), Is.EqualTo(1));

        npc.WornItems.Add(new ItemInstance(Panties) { OwnerId = npc.Id.Value });
        Assert.That(ModestyMath.CoverGainFromWearing(world, npc, Panties), Is.EqualTo(0),
            "Вторые трусы поверх первых считаются прикрытием.");
    }

    /// <summary>Помеченная опасность = чужак известен, даже если его сейчас не видно.</summary>
    [Test]
    public void ARememberedDangerMakesTheOutsiderKnown()
    {
        var (world, npc) = NakedColonist();
        Assert.That(ModestyMath.OutsiderKnown(world, npc), Is.False,
            "Чужак 'известен' на пустом месте.");

        npc.Memory.Dangers.Add(new HexLive.Simulation.Memory.DangerMemory
        {
            Tile = npc.Tile,
            Tick = world.Tick
        });

        Assert.That(ModestyMath.OutsiderKnown(world, npc), Is.True,
            "Свежая опасность в памяти не считается знанием о чужаке.");
    }

    /// <summary>
    /// Жара раздевает догола только в безопасности: при известном чужаке бельё
    /// остаётся на месте (это отменяет прежнее «жара НИКОГДА не раздевает»).
    /// </summary>
    [Test]
    public void HeatStripsUnderwearOnlyWhenNoOutsiderIsKnown()
    {
        var (world, npc) = NakedColonist();
        npc.WornItems.Add(new ItemInstance(Bra) { OwnerId = npc.Id.Value });

        Assert.That(DecisionSystem.FindRemovableItem(npc, world), Is.EqualTo(Bra),
            "В безопасности бельё снять нельзя — просьба игрока не выполнена.");

        npc.Memory.Dangers.Add(new HexLive.Simulation.Memory.DangerMemory
        {
            Tile = npc.Tile,
            Tick = world.Tick
        });

        Assert.That(DecisionSystem.FindRemovableItem(npc, world), Is.Null,
            "При чужаке рядом всё равно раздевается.");
    }
}

}
