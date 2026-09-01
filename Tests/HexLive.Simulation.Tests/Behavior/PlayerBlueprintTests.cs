using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
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

    private static List<WorldObjectState> FurnitureSites(WorldState world) =>
        world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.BuildSite &&
                !string.IsNullOrEmpty(obj.BuildProduct) &&
                obj.BuildProduct != ContentIds.HutPlan)
            .ToList();

    // §120.9 r2 / bug #277: правка мебели существующего дома реконсайлится,
    // а не отклоняется глухо. У стартового дома вся мебель уже ПОСТРОЕНА и
    // потому не правится; пустую площадку тесты добывают, снося построенный
    // шкаф и переразмечая его штатным StakePlanFurnitureSites.
    private static (SimulationEngine Engine, WorldObjectState Hut, WorldObjectState Site)
        HutWithEmptyWardrobeSite()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(obj.BuildProduct));
        var built = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.Wardrobe);
        WorldObjectMutations.DespawnObject(world, built.Id);
        Bootstrap.BuildingBootstrap.StakePlanFurnitureSites(world, hut);
        var site = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.BuildSite &&
            obj.BuildProduct == ContentIds.Wardrobe);
        Assert.That(site.Contents, Is.Empty, "Прекондиция: площадка не пуста.");
        return (engine, hut, site);
    }

    private static int WardrobeIndex(BuildingBlueprintDraft draft) =>
        draft.Furniture.FindIndex(item => item.DefinitionId == ContentIds.Wardrobe);

    [Test]
    public void ExistingEditorTurnsUntouchedFurnitureInPlace_Bug277()
    {
        var (engine, hut, site) = HutWithEmptyWardrobeSite();
        var world = engine.World;
        var before = world.PlayerBlueprints[hut.BlueprintId];
        var wasYaw = site.RotationDegrees;

        var accepted = false;
        for (var delta = 1; delta < 6 && !accepted; delta++)
        {
            Assert.That(BuildingBlueprintJson.TryDeserialize(
                BuildingRules.EditablePlanJsonFor(world, hut),
                out var candidate, out var jsonError), Is.True, jsonError);
            var i = WardrobeIndex(candidate);
            Assert.That(i, Is.GreaterThanOrEqualTo(0));
            candidate.Furniture[i].YawStep = BlueprintGeometry.NormalizeSector(
                candidate.Furniture[i].YawStep + delta);
            var json = BuildingBlueprintJson.Serialize(candidate, pretty: false);
            if (!BuildingBlueprintJson.TryDeserialize(json, out _, out _)) continue;
            accepted = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(
                hut.Id, json)).Accepted;
        }

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True,
                "Bug #277: поворот пустой мебельной площадки обязан приниматься.");
            Assert.That(world.PlayerBlueprints[hut.BlueprintId], Is.Not.SameAs(before),
                "Принятая правка обязана заменить план мира.");
            Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.True,
                "Поворот на месте не пересоздаёт площадку.");
            Assert.That(System.MathF.Abs(site.RotationDegrees - wasYaw), Is.GreaterThan(0.5f),
                "Пустая площадка обязана повернуться вслед за чертежом.");
        });
    }

    [Test]
    public void ExistingEditorRemovesUntouchedFurnitureSite_Bug277()
    {
        var (engine, hut, site) = HutWithEmptyWardrobeSite();
        var world = engine.World;

        Assert.That(BuildingBlueprintJson.TryDeserialize(
            BuildingRules.EditablePlanJsonFor(world, hut),
            out var revised, out var jsonError), Is.True, jsonError);
        revised.Furniture.RemoveAt(WardrobeIndex(revised));
        var admission = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(
            hut.Id, BuildingBlueprintJson.Serialize(revised, pretty: false)));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Accepted, Is.True,
                "Bug #277: удаление НЕ начатой мебели обязано приниматься.");
            Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False,
                "Осиротевшая пустая площадка обязана уйти из мира.");
            Assert.That(world.PlayerBlueprints[hut.BlueprintId].Furniture
                    .Any(item => item.DefinitionId == ContentIds.Wardrobe), Is.False,
                "Шкаф обязан уйти и из плана мира.");
        });
    }

    // Случай игрока из #277: дом ещё СТРОИТСЯ, мебель мира не существует —
    // любая мебельная правка чертежа принимается и просто заменяет план.
    [Test]
    public void UnfinishedHouseAcceptsFurnitureEdits_Bug277()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var json = BuildingBlueprintJson.Serialize(
            BuiltInBuildingBlueprints.Hut1Hex(), pretty: false);
        WorldObjectState site = null;
        foreach (var tile in world.Tiles.Items.Keys.OrderBy(t => t.Q).ThenBy(t => t.R))
        {
            if (!engine.ApplyManualCommand(new PlaceBuildingBlueprintCommand(
                    json, tile, rotationDegrees: 0f)).Accepted)
            {
                continue;
            }

            site = world.Entities.Objects.Values.Single(candidate =>
                candidate.BuildProduct == ContentIds.HutPlan && candidate.BlueprintId != 0);
            break;
        }

        Assert.That(site, Is.Not.Null,
            "Прототипный остров не дал места под чертёж.");

        Assert.That(BuildingBlueprintJson.TryDeserialize(
            BuildingRules.EditablePlanJsonFor(world, site),
            out var revised, out var jsonError), Is.True, jsonError);
        Assert.That(revised.Furniture, Is.Not.Empty,
            "Прекондиция: у committed-плана нет мебели.");
        revised.Furniture.RemoveAt(0);
        var admission = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(
            site.Id, BuildingBlueprintJson.Serialize(revised, pretty: false)));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Accepted, Is.True,
                "Bug #277: мебельная правка недостроенного дома обязана приниматься.");
            Assert.That(world.PlayerBlueprints[site.BlueprintId].Furniture,
                Has.Count.EqualTo(revised.Furniture.Count));
        });
    }

    [Test]
    public void ExistingEditorRefusesToTouchStartedFurniture_Bug277()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(obj.BuildProduct));

        var before = world.PlayerBlueprints[hut.BlueprintId];
        // Вся мебель стартового дома уже ПОСТРОЕНА — каждое удаление обязано
        // отклоняться: готовый предмет ревизия не сносит и не двигает.
        for (var i = 0; i < before.Furniture.Count; i++)
        {
            Assert.That(BuildingBlueprintJson.TryDeserialize(
                BuildingRules.EditablePlanJsonFor(world, hut),
                out var candidate, out var jsonError), Is.True, jsonError);
            candidate.Furniture.RemoveAt(i);
            var json = BuildingBlueprintJson.Serialize(candidate, pretty: false);
            if (!BuildingBlueprintJson.TryDeserialize(json, out _, out _)) continue;
            var admission = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(
                hut.Id, json));
            Assert.That(admission.Accepted, Is.False,
                $"Построенная мебель не двигается и не убирается (placement {i}).");
        }

        Assert.That(world.PlayerBlueprints[hut.BlueprintId], Is.SameAs(before),
            "Отклонённая мебельная правка не должна частично заменить план мира.");
    }
}

}
