using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Постановочная сцена боя: чем бить, сколько раз — и что остаётся ПОСЛЕ.
/// </summary>
public sealed class FightSceneTests
{
    private static (WorldState world, NPCState a, NPCState b) Pair()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToList();
        return (world, npcs[0], npcs[1]);
    }

    /// <summary>
    /// ⭐ РЕГРЕСС §104 r7: сентинел «сцена доиграна» не должен пережить сцену.
    ///
    /// <para>
    /// Последний удар отодвигает готовность на полмиллиарда тиков — так сцена
    /// говорит «своё он уже сказал». Штатный конец её снимал, а обрыв мимо него
    /// (RaidSystem расцепляет пару своим Unpair) — нет: боец оставался без
    /// замахов до переполнения, то есть навсегда, и выглядело бы это как «стоит
    /// столбом и не отвечает».
    /// </para>
    /// </summary>
    [Test]
    public void SceneOverSentinelDoesNotSurviveAnUnpair()
    {
        var (world, abuser, mark) = Pair();

        FightScene.Begin(world, abuser, mark, GearCatalog.Fist, blows: 2, spacingClips: 1.2f);
        FightScene.OnBlowLanded(world, abuser, clipSeconds: 1.5f);
        FightScene.OnBlowLanded(world, abuser, clipSeconds: 1.5f);

        Assert.That(FightScene.IsComplete(abuser), Is.True,
            "Два удара из двух — сцена обязана считаться доигранной.");
        Assert.That(abuser.StrikeReadyAtTick, Is.GreaterThan(world.Tick + 100_000),
            "После последнего удара следующий замах должен быть отодвинут за горизонт.");

        // Обрыв МИМО FightScene.End — так делает RaidSystem, когда расцепляет пару.
        FightScene.ReleaseSwingSlot(abuser);

        Assert.That(abuser.StrikeReadyAtTick, Is.LessThanOrEqualTo(world.Tick),
            "Слот замаха не отпущен: боец останется без ударов навсегда.");
        Assert.That(abuser.StrikeLandsAtTick, Is.Zero,
            "Незавершённый замах пережил расцепление пары.");
    }

    /// <summary>Обычный кулдаун оружия — не сентинел, и стирать его нельзя.</summary>
    [Test]
    public void ReleaseSwingSlotKeepsAnOrdinaryCooldown()
    {
        var (world, npc, _) = Pair();
        var ready = world.Tick + 12;
        npc.StrikeReadyAtTick = ready;

        FightScene.ReleaseSwingSlot(npc);

        Assert.That(npc.StrikeReadyAtTick, Is.EqualTo(ready),
            "Стёрт легальный кулдаун оружия — бить станут чаще, чем позволяет " +
            "таймлайн замаха.");
    }

    /// <summary>
    /// Лестница ненависти §93: пока он её не ненавидит — только кулаки, каким бы
    /// арсеналом он ни владел.
    /// </summary>
    [Test]
    public void StrangerIsBeatenWithFists()
    {
        var (world, abuser, mark) = Pair();
        abuser.Inventory.Items.Add(ContentIds.Knife);
        abuser.Social.GetOrCreate(mark.Id).Affinity = 0f;

        Assert.That(FightScene.PickWeapon(abuser, mark), Is.EqualTo(GearCatalog.Fist),
            "Нож достают по ИСТОРИИ отношений, а не по наличию в рюкзаке.");
    }

    /// <summary>А заработанная ненависть — уже оружие.</summary>
    [Test]
    public void HatredReachesForTheWeapon()
    {
        var (world, abuser, mark) = Pair();
        abuser.Inventory.Items.Add(ContentIds.Knife);
        abuser.Social.GetOrCreate(mark.Id).Affinity = -1f;

        Assert.That(FightScene.PickWeapon(abuser, mark), Is.EqualTo(ContentIds.Knife),
            "На дне симпатии он берётся за то, что есть в руках.");
    }
}

}
