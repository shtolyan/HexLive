using System.IO;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
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

    [Test]
    public void ExistingHouseRoomExtensionKeepsOwnerAndFinishedModules()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(obj.BuildProduct));
        var originalPlan = world.PlayerBlueprints[hut.BlueprintId];
        var originalAnchor = BlueprintBuildingPlan.AnchorTile(originalPlan);
        var original = BuildingRules.ArchitectureObjects(world, hut)
            .ToDictionary(piece => piece.ArchitectureElements[0].SlotKey,
                piece => (piece.Id, State: piece.ArchitectureElements[0].Clone()));

        BuildingBlueprintDraft applied = null;
        foreach (var direction in HexDirection.All)
        {
            var candidate = originalPlan.Clone();
            var hex = new TileCoord(
                originalAnchor.Q + direction.DQ,
                originalAnchor.R + direction.DR);
            var sectors = Enumerable.Range(0, 6)
                .Select(sector => new FloorSectorKey(hex, sector)).ToArray();
            var edit = BlueprintEditorCommands.CreateRoom(candidate, sectors);
            if (!edit.Succeeded) continue;
            var admission = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(
                hut.Id, BuildingBlueprintJson.Serialize(candidate, pretty: false)));
            if (!admission.Accepted) continue;
            applied = candidate;
            break;
        }

        Assert.That(applied, Is.Not.Null,
            "У стартовой хижины должен найтись хотя бы один свободный сосед для расширения.");
        Assert.That(world.Entities.Objects.ContainsKey(hut.Id), Is.True,
            "Редактирование не должно заменять footprint-owner другим объектом.");
        Assert.That(hut.BuildProduct, Is.EqualTo(ContentIds.HutPlan),
            "Новые модули достраиваются как delta-site на существующем owner.");
        Assert.That(BlueprintBuildingPlan.AnchorTile(world.PlayerBlueprints[hut.BlueprintId]),
            Is.EqualTo(originalAnchor), "Расширение не имеет права пересчитать anchor и сдвинуть дом.");

        var after = BuildingRules.ArchitectureObjects(world, hut)
            .ToDictionary(piece => piece.ArchitectureElements[0].SlotKey);
        foreach (var pair in original)
        {
            if (!after.TryGetValue(pair.Key, out var piece)) continue;
            var state = piece.ArchitectureElements[0];
            Assert.That(piece.Id, Is.EqualTo(pair.Value.Id), $"{pair.Key}: ObjectId changed");
            Assert.That(state.Complete, Is.True, $"{pair.Key}: finished module was reset");
            Assert.That(state.LocalX, Is.EqualTo(pair.Value.State.LocalX).Within(0.0001f));
            Assert.That(state.LocalZ, Is.EqualTo(pair.Value.State.LocalZ).Within(0.0001f));
        }
        Assert.That(after.Values.Any(piece => !piece.ArchitectureElements[0].Complete), Is.True,
            "Добавленная комната должна состоять из реальных незавершённых модулей.");

        BuildingRules.SyncHutElements(world, hut);
        Assert.That(original.Keys.Where(after.ContainsKey).All(key =>
            after[key].ArchitectureElements[0].Complete), Is.True,
            "Первый sync доставок пристройки не должен разобрать готовую часть дома.");
    }

    [Test]
    public void SnapshotExposesOnlyOwnerBlueprintForExistingBuildingEditor()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(obj.BuildProduct));
        var snapshot = WorldSnapshotExporter.Export(world);
        var owner = snapshot.Objects.Single(obj => obj.Id == hut.Id);
        Assert.That(owner.BuildingBlueprintJson, Is.Not.Empty);
        Assert.That(BuildingBlueprintJson.TryDeserialize(
            owner.BuildingBlueprintJson, out var draft, out var error), Is.True, error);
        Assert.That(BlueprintBuildingPlan.Modules(draft), Is.Not.Empty);
        Assert.That(snapshot.Objects.Where(obj => obj.ArchitectureOwnerObjectId == hut.Id.Value)
            .All(obj => string.IsNullOrEmpty(obj.BuildingBlueprintJson)), Is.True,
            "JSON редактора едет один раз на owner, а не в каждом LEGO-модуле.");
    }

    [Test]
    public void ExistingArchitectureEditorCannotSilentlyDuplicateFurniture()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(obj.BuildProduct));
        var before = world.PlayerBlueprints[hut.BlueprintId];
        var tampered = before.Clone();
        tampered.Furniture[0].YawStep++;

        var admission = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(
            hut.Id, BuildingBlueprintJson.Serialize(tampered, pretty: false)));

        Assert.That(admission.Accepted, Is.False);
        Assert.That(world.PlayerBlueprints[hut.BlueprintId], Is.SameAs(before),
            "Отклонённая мебельная правка не должна частично заменить план мира.");
    }
}

}
