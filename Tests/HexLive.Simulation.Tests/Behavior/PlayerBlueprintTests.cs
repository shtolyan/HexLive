using System.IO;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §120.8: произвольный чертёж игрока — команда, реестр мира и сейв.
/// Главный страхуемый отказ: загрузчик «чинит» геометрию plan-зданий из
/// committed-плана; без реестра в блобе произвольный дом после load молча
/// превращался бы в hut_player_v1.
/// </summary>
public sealed class PlayerBlueprintTests
{
    private static (WorldState World, WorldObjectState Site, BuildingBlueprintDraft Draft)
        PlaceCustomBlueprint(int seed)
    {
        var engine = TestWorld.CreateEngine(seed);
        var world = engine.World;
        // §120.3 r2: стартовый дом мира уже держит свой чертёж в реестре —
        // счёт в тестах относительный, а размеченная площадка ищется как
        // единственный ещё НЕ достроенный plan-объект.
        var baseline = world.PlayerBlueprints.Count;
        var draft = BuiltInBuildingBlueprints.Hut1Hex();
        var json = BuildingBlueprintJson.Serialize(draft, pretty: false);

        foreach (var tile in world.Tiles.Items.Keys.OrderBy(t => t.Q).ThenBy(t => t.R))
        {
            var admission = engine.ApplyManualCommand(
                new PlaceBuildingBlueprintCommand(json, tile, rotationDegrees: 0f));
            if (!admission.Accepted)
            {
                continue;
            }

            Assert.That(world.PlayerBlueprints, Has.Count.EqualTo(baseline + 1),
                "Команда обязана зарегистрировать ровно один новый чертёж.");
            var site = world.Entities.Objects.Values.Single(candidate =>
                candidate.BuildProduct == ContentIds.HutPlan && candidate.BlueprintId != 0);
            return (world, site, draft);
        }

        Assert.Fail("Прототипный остров не дал ни одного места под 1-гексовый чертёж.");
        return default;
    }

    [Test]
    public void PlacedBlueprintUsesItsOwnModulesNotTheCommittedPlan()
    {
        var (world, site, draft) = PlaceCustomBlueprint(12345);

        var expected = BlueprintBuildingPlan.Modules(draft)
            .Select(module => module.Key).OrderBy(key => key).ToArray();
        var actual = BuildingRules.Elements(world, site)
            .Select(element => element.SlotKey).OrderBy(key => key).ToArray();
        Assert.That(actual, Is.EqualTo(expected),
            "Модули площадки должны прийти из ЕЁ чертежа.");

        var committed = CommittedBuildingPlans.PlayerHutModules
            .Select(module => module.Key).OrderBy(key => key).ToArray();
        Assert.That(actual, Is.Not.EqualTo(committed),
            "Смысл теста — чертёж, отличимый от committed-плана; " +
            "если ключи совпали, замените образец на другой драфт.");
    }

    [Test]
    public void RejectedPlacementLeavesNoOrphanBlueprint()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var baseline = world.PlayerBlueprints.Count;
        var json = BuildingBlueprintJson.Serialize(
            BuiltInBuildingBlueprints.Hut1Hex(), pretty: false);

        // Тайла (9999, 9999) в мире нет — место заведомо негодно.
        var admission = engine.ApplyManualCommand(new PlaceBuildingBlueprintCommand(
            json, new TileCoord(9999, 9999), rotationDegrees: 0f));

        Assert.That(admission.Accepted, Is.False);
        Assert.That(world.PlayerBlueprints, Has.Count.EqualTo(baseline),
            "Отклонённая разметка не должна оставлять чертёж-сироту в реестре.");
    }

    [Test]
    public void BlueprintSurvivesSaveLoadWithoutFallingBackToCommittedPlan()
    {
        var (world, site, draft) = PlaceCustomBlueprint(12345);
        var siteId = site.Id;
        var blueprintId = site.BlueprintId;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(12345);
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        Assert.That(loaded.PlayerBlueprints.ContainsKey(blueprintId), Is.True,
            "Реестр чертежей обязан пережить сейв.");
        var restored = loaded.Entities.Objects[siteId];
        Assert.That(restored.BlueprintId, Is.EqualTo(blueprintId));

        // Ключевая защита: миграция load-а переписывает геометрию слотов —
        // она обязана взять чертёж площадки, а не committed-план.
        var expected = BlueprintBuildingPlan.Modules(draft)
            .Select(module => module.Key).OrderBy(key => key).ToArray();
        var actual = BuildingRules.Elements(loaded, restored)
            .Select(element => element.SlotKey).OrderBy(key => key).ToArray();
        Assert.That(actual, Is.EqualTo(expected));

        // Дверь чертежа осталась дверью: вычисление портала не кидает.
        Assert.DoesNotThrow(() => BuildingRules.DoorOutwardYaw(loaded, restored));
    }

    [Test]
    public void OpenFloorRoomHasNoAutoWallsAndSurvivesJson()
    {
        // §120.8: «Пол без стен» — комната без автоконтура; обычная получает
        // границу, открытая нет, и признак переживает JSON round-trip.
        var draft = BuiltInBuildingBlueprints.Hut1Hex();
        var floorHex = BlueprintBuildingPlan.AnchorTile(draft);
        var open = new FloorSectorKey(new TileCoord(floorHex.Q + 3, floorHex.R), 0);
        var result = BlueprintEditorCommands.CreateRoom(
            draft, new[] { open, new FloorSectorKey(open.Hex, 1) }, openFloor: true);
        Assert.That(result.Succeeded, Is.True, result.Message);

        var openRoomId = draft.Elements
            .Single(element => element.Kind == BlueprintElementKind.FloorSector &&
                               element.FloorSector.Equals(open)).RoomId;
        Assert.That(draft.OpenRooms, Does.Contain(openRoomId));
        Assert.That(draft.Elements.Any(element =>
                element.Origin == BlueprintElementOrigin.RoomBoundary &&
                element.RoomId == openRoomId), Is.False,
            "Открытый настил не должен отращивать автоконтур стен.");

        var json = BuildingBlueprintJson.Serialize(draft, pretty: false);
        Assert.That(BuildingBlueprintJson.TryDeserialize(json, out var restored, out var error),
            Is.True, error);
        Assert.That(restored.OpenRooms, Does.Contain(openRoomId),
            "Признак открытой комнаты обязан пережить JSON (сейв и команду).");
    }
}

}
