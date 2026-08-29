using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §146.14 (bug #291): «Сделать домом» на своём очаге переносит якорь лагеря;
/// чужой очаг отклоняется, недостроенная площадка — ещё не очаг.
/// </summary>
public sealed class CampHomeTests
{
    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .First();

    private static TileCoord FindFreeTile(
        WorldState world, System.Func<TileCoord, bool> fits)
    {
        foreach (var tile in world.Tiles.Items.Keys
                     .OrderBy(t => t.Q).ThenBy(t => t.R))
        {
            if (fits(tile) &&
                StructurePlacement.HexFreeForBuild(world, tile) &&
                StructurePlacement.CenterJunction(world, tile) is not null)
            {
                return tile;
            }
        }

        Assert.Fail("Не нашлось свободного гекса под очаг для теста.");
        return default;
    }

    private static WorldObjectState SpawnHearth(WorldState world, TileCoord tile)
    {
        var center = StructurePlacement.CenterJunction(world, tile);
        Assert.That(center, Is.Not.Null);
        return WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, new FragmentId(1), tile, center!.Value);
    }

    [Test]
    public void OwnNewHearthBecomesHomeAndGoHomeFollows()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        engine.Step(); // раздать стартовые джанкшены — до шага CurrentJunction пуст
        var npc = Colonist(world);
        var oldHome = world.FactionHomes[Faction.Colony];
        var outsiders = world.FactionHomes[Faction.Outsiders];

        // Ничья земля: дальше радиуса лагеря от ОБОИХ домов — ровно сценарий
        // игрока «построил новый костёр, а дом не поменялся».
        var farTiles = world.Tiles.Items.Keys
            .Where(candidate =>
                HexSpatialMath.HexDistance(candidate, oldHome) > Spec72.MaxCampRadiusTiles &&
                HexSpatialMath.HexDistance(candidate, outsiders) > Spec72.MaxCampRadiusTiles)
            .ToList();
        var freeTiles = farTiles
            .Where(candidate =>
                StructurePlacement.HexFreeForBuild(world, candidate) &&
                StructurePlacement.CenterJunction(world, candidate) is not null)
            .ToList();
        var reachable = freeTiles
            .Where(candidate =>
                npc.CurrentJunction is { } from &&
                StructurePlacement.CenterJunction(world, candidate) is { } center &&
                Connectivity.Reachable(world, from, center, npc.Body.CanJump))
            .OrderBy(t => t.Q).ThenBy(t => t.R)
            .ToList();
        Assert.That(reachable, Is.Not.Empty,
            $"Нет тайла под очаг: far={farTiles.Count} free={freeTiles.Count}");
        var tile = reachable[0];
        var fire = SpawnHearth(world, tile);
        var doorVersion = world.DoorStateVersion;

        var admission = ManualCommandExecutor.Apply(
            world, new SetCampHomeCommand(npc.Id, fire.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"Переезд отклонён: {admission.Reason}");
            Assert.That(world.FactionHomes[Faction.Colony], Is.EqualTo(tile),
                "Якорь дома обязан переехать на новый очаг.");
            Assert.That(world.DoorStateVersion, Is.GreaterThan(doorVersion),
                "§129: без бампа версии кэш дверных запретов держит старый вердикт.");
        });

        // «Бежать домой» теперь ведёт в НОВЫЙ лагерь — сама жалоба игрока.
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Mind.ManualControl = true;
        var goHome = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(npc.Id, SelfActionKind.GoHome));

        Assert.Multiple(() =>
        {
            Assert.That(goHome.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                $"GoHome отклонён: {goHome.Reason}");
            Assert.That(npc.Plan.TargetTile, Is.Not.Null);
            Assert.That(HexSpatialMath.HexDistance(
                    npc.Plan.TargetTile!.Value, tile),
                Is.LessThanOrEqualTo(Spec72.MaxCampRadiusTiles),
                "Маршрут «домой» обязан вести в круг нового очага.");
        });
    }

    [Test]
    public void ForeignHearthIsRejected()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        var before = world.FactionHomes[Faction.Colony];
        var outsiders = world.FactionHomes[Faction.Outsiders];

        var tile = FindFreeTile(world, candidate =>
            HexSpatialMath.HexDistance(candidate, outsiders) <= 2);
        var fire = SpawnHearth(world, tile);

        var admission = ManualCommandExecutor.Apply(
            world, new SetCampHomeCommand(npc.Id, fire.Id));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status,
                Is.Not.EqualTo(ManualCommandAdmissionStatus.Accepted),
                "Чужой очаг не делают домом — только дипломатия §146.12.");
            Assert.That(admission.Reason, Is.EqualTo("ForeignCamp"));
            Assert.That(world.FactionHomes[Faction.Colony], Is.EqualTo(before),
                "Отклонённый переезд не смеет трогать якорь.");
        });
    }

    [Test]
    public void UnfinishedHearthSiteIsNotYetAHome()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        var oldHome = world.FactionHomes[Faction.Colony];
        var outsiders = world.FactionHomes[Faction.Outsiders];

        var tile = FindFreeTile(world, candidate =>
            HexSpatialMath.HexDistance(candidate, oldHome) > Spec72.MaxCampRadiusTiles &&
            HexSpatialMath.HexDistance(candidate, outsiders) > Spec72.MaxCampRadiusTiles);
        var center = StructurePlacement.CenterJunction(world, tile);
        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, new FragmentId(1), tile, center!.Value);
        site.BuildProduct = ContentIds.Campfire;

        var admission = ManualCommandExecutor.Apply(
            world, new SetCampHomeCommand(npc.Id, site.Id));

        Assert.That(admission.Reason, Is.EqualTo("NotAHearth"),
            "Недостроенная площадка — ещё не очаг.");
    }

    [Test]
    public void CurrentHomeHearthIsRejectedAsAlreadyHome()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        var home = world.FactionHomes[Faction.Colony];
        var fire = SpawnHearth(world, home);

        var admission = ManualCommandExecutor.Apply(
            world, new SetCampHomeCommand(npc.Id, fire.Id));

        Assert.That(admission.Reason, Is.EqualTo("AlreadyHome"));
    }
}

}
