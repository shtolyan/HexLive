using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §54.17 (r3): вертел раньше каменного кольца.
/// <para>
/// Старый порядок стадий ставил кольцо из 18 камней ВТОРОЙ стадией, а
/// <c>Remaining()</c> выдаёт нехватку только текущей стадии — жарка ждала
/// кольцо, которое в соаках не достроилось ни разу (§63.4), и
/// <c>MeatRoasted</c> не мог случиться вовсе. Эти тесты пиняют новый
/// порядок: вертел (палки → верёвка) собирается БЕЗ единого камня, кольцо —
/// длинный хвост.
/// </para>
/// </summary>
public sealed class CampfireStageTests
{
    private static WorldObjectState NewCampfireSite() => new()
    {
        DefinitionId = "build.site",
        BuildProduct = "campfire.spot",
        BillSticks = SimBalance.CampfireBillSticks,
        BillStones = SimBalance.CampfireBillStones,
        BillRope = SimBalance.CampfireBillRope,
    };

    private static void Deliver(WorldObjectState site, string materialId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            site.Contents.Add(new ItemInstance(materialId));
        }
    }

    [Test]
    public void SpitStagesComeBeforeTheStoneRing()
    {
        var site = NewCampfireSite();

        // Стадия 1 — рабочий костёр из 9 палок; камни ещё не принимаются.
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialSticks),
            Is.EqualTo(BuildSiteMath.CampfireStage1Sticks));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialStones), Is.False,
            "Камни запрошены до вертела — порядок стадий откатился к до-§54.17.");
        Deliver(site, BuildSiteMath.MaterialSticks, BuildSiteMath.CampfireStage1Sticks);

        // Стадии 2-3 — рогатины и перекладина: ещё 3 палки, камни всё ещё нет.
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialSticks), Is.EqualTo(2));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialStones), Is.False);
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialRope), Is.False,
            "Верёвка запрошена раньше рогатин и перекладины.");
        Deliver(site, BuildSiteMath.MaterialSticks, 3);

        // Стадия 4 — обвязка.
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialRope), Is.EqualTo(2));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialStones), Is.False);
        Deliver(site, BuildSiteMath.MaterialRope, 2);

        // Стадия 5 — кольцо, единственное, что осталось.
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialStones), Is.EqualTo(18));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialSticks), Is.False);
        Deliver(site, BuildSiteMath.MaterialStones, 18);

        Assert.That(BuildSiteMath.IsStocked(site), Is.True);
    }

    [Test]
    public void SpitCompletesWithZeroStones()
    {
        var fire = new WorldObjectState { DefinitionId = "campfire.spot" };
        Deliver(fire, BuildSiteMath.MaterialSticks, SimBalance.CampfireBillSticks);
        Deliver(fire, BuildSiteMath.MaterialRope, SimBalance.CampfireBillRope);

        Assert.That(BuildSiteMath.CampfireSpitComplete(fire), Is.True,
            "Вертел обязан читаться готовым без единого камня — иначе жарка " +
            "снова заперта за кольцом, которое почти никогда не достраивается.");
        Assert.That(BuildSiteMath.CampfireRingComplete(fire), Is.False,
            "Кольцо не должно читаться готовым без камней.");
    }

    [Test]
    public void RingStillDemandsTheFullStoneBill()
    {
        var fire = new WorldObjectState { DefinitionId = "campfire.spot" };
        Deliver(fire, BuildSiteMath.MaterialStones, SimBalance.CampfireBillStones - 1);
        Assert.That(BuildSiteMath.CampfireRingComplete(fire), Is.False);

        Deliver(fire, BuildSiteMath.MaterialStones, 1);
        Assert.That(BuildSiteMath.CampfireRingComplete(fire), Is.True);
    }

    /// <summary>
    /// Старый сейв, где кольцо уже доставлено (до-§54.17 порядок): пул
    /// переатрибутируется без потерь — камни ложатся в свою (теперь последнюю)
    /// стадию, а сайт просит недостающие палки вертела.
    /// </summary>
    [Test]
    public void OldSaveWithRingDeliveredReattributesCleanly()
    {
        var site = NewCampfireSite();
        Deliver(site, BuildSiteMath.MaterialSticks, BuildSiteMath.CampfireStage1Sticks);
        Deliver(site, BuildSiteMath.MaterialStones, 18);

        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialSticks), Is.EqualTo(2));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialStones), Is.False,
            "Уже доставленные камни не должны запрашиваться повторно.");

        Deliver(site, BuildSiteMath.MaterialSticks, 3);
        Deliver(site, BuildSiteMath.MaterialRope, 2);
        Assert.That(BuildSiteMath.IsStocked(site), Is.True);
    }

    [Test]
    public void CookingFireSelectionSkipsAnIncompleteFireSeenFirst()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        var incomplete = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, junctions[0].Tiles[0], junctions[0].Id);
        incomplete.ResourceAmount = 500f;
        var usable = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, junctions[1].Tiles[0], junctions[1].Id);
        usable.ResourceAmount = 500f;
        Deliver(usable, BuildSiteMath.MaterialSticks, SimBalance.CampfireBillSticks);
        Deliver(usable, BuildSiteMath.MaterialRope, SimBalance.CampfireBillRope);

        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(SeenFire(incomplete)); // dictionary/order trap from #93
        npc.Perception.Objects.Add(SeenFire(usable));

        Assert.That(DecisionSystem.FindCookingFire(npc, world), Is.SameAs(usable),
            "Первый увиденный новый/недостроенный очаг не должен скрывать " +
            "другой доступный горящий костёр с готовым вертелом.");

        usable.ResourceAmount = 0f;
        Assert.That(DecisionSystem.FindCookingFire(npc, world), Is.Null,
            "Холодный вертел нельзя заимствовать у горящего костра без вертела.");
    }

    private static PerceivedObject SeenFire(WorldObjectState fire) => new()
    {
        Id = fire.Id,
        DefinitionId = fire.DefinitionId,
        Tile = fire.Tile,
        IsReachable = true,
        IsOccupied = false,
        Distance = 1f,
    };
}

}
