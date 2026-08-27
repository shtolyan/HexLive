using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class MoistureDryingRateTests
{
    [Test]
    public void NaturalDryingSlowsOnlyClothingInEveryStorageLocation()
    {
        var world = TestWorld.CreateWorld(-28147312);
        PrepareDryWeather(world);
        var npc = world.Entities.Npcs.Values.First();
        var outdoor = FindOrdinaryOutdoorTile(world);
        var clothingId = ClothingId(world);
        npc.Tile = outdoor;
        npc.BodyWetness = 1f;
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.WornItems.Add(Wet(clothingId));
        npc.Inventory.Items.Add(Wet(clothingId));
        npc.Inventory.Items.Add(Wet(ContentIds.Board));
        var groundCoat = SpawnPickup(world, clothingId, outdoor);
        groundCoat.Wetness = 1f;

        new MoistureSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems[0].Wetness, Is.EqualTo(0.998f).Within(0.00001f));
            Assert.That(npc.Inventory.Items[0].Wetness, Is.EqualTo(0.998f).Within(0.00001f));
            Assert.That(groundCoat.Wetness, Is.EqualTo(0.998f).Within(0.00001f));
            Assert.That(npc.BodyWetness, Is.EqualTo(0.98f).Within(0.00001f),
                "Bug #160 changes clothes, not skin drying.");
            Assert.That(npc.Inventory.Items[1].Wetness, Is.EqualTo(0.98f).Within(0.00001f),
                "Non-clothing pickups retain their established natural rate.");
        });
    }

    [Test]
    public void DirectSunScalesTheSlowerNaturalClothingRate()
    {
        var world = TestWorld.CreateWorld(-28147312);
        PrepareDryWeather(world);
        world.Environment.UvIndex = 1f;
        var sunnyTile = world.Tiles.Items
            .Where(pair => pair.Value.Flags.HasFlag(TileFlags.Walkable) &&
                !pair.Value.Flags.HasFlag(TileFlags.Water) &&
                !pair.Value.Flags.HasFlag(TileFlags.Indoor) &&
                !TemperatureSystem.IsShaded(world, pair.Key))
            .Select(pair => pair.Key)
            .First();
        var npc = world.Entities.Npcs.Values.First();
        var clothingId = ClothingId(world);
        npc.Tile = sunnyTile;
        npc.BodyWetness = 1f;
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.WornItems.Add(Wet(clothingId));

        new MoistureSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems[0].Wetness, Is.EqualTo(0.994f).Within(0.00001f),
                "Sun is ×3 of the new 0.1 natural clothing channel.");
            Assert.That(npc.BodyWetness, Is.EqualTo(0.94f).Within(0.00001f),
                "Body keeps the established DryBase ×3 sun channel.");
        });
    }

    [Test]
    public void CampfireAndDryingRackKeepTheirEstablishedAbsoluteRates()
    {
        var world = TestWorld.CreateWorld(-28147312);
        PrepareDryWeather(world);
        var fire = world.Entities.Objects.Values.First(
            obj => obj.DefinitionId == ContentIds.Campfire);
        fire.ResourceAmount = 1f;
        var npc = world.Entities.Npcs.Values.First();
        var clothingId = ClothingId(world);
        npc.Tile = fire.Tile;
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.WornItems.Add(Wet(clothingId));

        var rackTile = FindOrdinaryOutdoorTile(world);
        var junctionId = StructurePlacement.CenterJunction(world, rackTile);
        Assert.That(junctionId, Is.Not.Null);
        var fragment = world.Junctions.Items[junctionId.Value].Fragment;
        WorldObjectMutations.SpawnObject(
            world, ContentIds.DryingRack, fragment, rackTile, junctionId.Value);
        var rackCoat = WorldObjectMutations.SpawnObject(
            world, clothingId, fragment, rackTile, junctionId.Value);
        rackCoat.Wetness = 1f;

        new MoistureSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems[0].Wetness, Is.EqualTo(0.92f).Within(0.00001f),
                "Campfire remains DryBase ×4.");
            Assert.That(rackCoat.Wetness, Is.EqualTo(0.9f).Within(0.00001f),
                "Drying rack remains DryBase ×5.");
        });
    }

    [Test]
    public void RainAndWaterStopAtOuterGarmentInsteadOfSoakingCarriedClothingThrough()
    {
        const string pantiesId = "underwear.thong_anarchy";
        const string backpackId = "gear.backpack_riot";
        const string jacketId = "clothing.jacket_ranger";
        var world = TestWorld.CreateWorld(-28147312);
        PrepareDryWeather(world);
        var npc = world.Entities.Npcs.Values.First();
        var outdoor = FindOrdinaryOutdoorTile(world);
        npc.Tile = outdoor;
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();

        var panties = new ItemInstance(pantiesId) { Wetness = 0.2f };
        var backpack = new ItemInstance(backpackId) { Wetness = 0.2f };
        Assert.That(world.Content.ObjectDefinitions[jacketId].Layer, Is.Not.Null,
            "Setup must use a shipped wearable, not the legacy clothing.coat id.");
        var dryCoat = new ItemInstance(jacketId);
        var dampPants = new ItemInstance(ContentIds.LeatherPants) { Wetness = 0.5f };
        npc.WornItems.Add(panties);
        npc.WornItems.Add(backpack);
        EquipmentMath.Recalculate(world, npc);

        // First carried cell belongs to the panties. Fill the body's own carry
        // cells so the second garment demonstrably lands in the backpack tail.
        npc.Inventory.Items.Add(dryCoat);
        var carry = InventoryLayoutBuilder.Build(world, npc).Containers.Single(
            container => container.Kind == InventoryContainerKind.Carry);
        for (var i = 0; i < carry.BaseCapacity + carry.StrengthBonus; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance($"test.rain.filler.{i}"));
        }
        npc.Inventory.Items.Add(dampPants);

        Assert.That(InventoryLayoutBuilder.TryCollectOwnedContents(
            world, npc, 0, out var pantiesContents), Is.True);
        Assert.That(pantiesContents.Any(item => ReferenceEquals(item, dryCoat)), Is.True,
            "Setup: куртка должна лежать именно в кармане трусов.");
        Assert.That(InventoryLayoutBuilder.TryCollectOwnedContents(
            world, npc, 1, out var backpackContents), Is.True);
        Assert.That(backpackContents.Any(item => ReferenceEquals(item, dampPants)), Is.True,
            "Setup: штаны должны лежать именно в бонусной части рюкзака.");

        var groundCoat = SpawnPickup(world, jacketId, outdoor);
        world.Environment.IsRaining = true;
        Assert.That(ShelterMath.RainReaches(world, outdoor), Is.True);

        new MoistureSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(panties.Wetness, Is.EqualTo(1f), "Внешние трусы обязаны промокнуть.");
            Assert.That(backpack.Wetness, Is.EqualTo(1f), "Сам рюкзак обязан промокнуть.");
            Assert.That(dryCoat.Wetness, Is.Zero, "Сухая куртка внутри трусов промокла насквозь.");
            Assert.That(dampPants.Wetness, Is.EqualTo(0.498f).Within(0.00001f),
                "Уже мокрая одежда в рюкзаке должна естественно сохнуть, а не замереть.");
            Assert.That(groundCoat.Wetness, Is.EqualTo(1f),
                "Куртка без внешнего контейнера на земле по-прежнему мокнет.");
        });

        world.Environment.IsRaining = false;
        npc.Tile = world.Tiles.Items.First(pair =>
            pair.Value.Flags.HasFlag(TileFlags.Water)).Key;
        panties.Wetness = 0f;
        backpack.Wetness = 0f;
        dryCoat.Wetness = 0f;
        dampPants.Wetness = 0f;

        new MoistureSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(panties.Wetness, Is.EqualTo(1f));
            Assert.That(backpack.Wetness, Is.EqualTo(1f));
            Assert.That(dryCoat.Wetness, Is.Zero);
            Assert.That(dampPants.Wetness, Is.Zero,
                "Вода не должна проходить сквозь внешний контейнер и на водном тайле.");
        });
    }

    private static void PrepareDryWeather(WorldState world)
    {
        world.Environment.IsRaining = false;
        world.Environment.UvIndex = 0f;
        foreach (var fire in world.Entities.Objects.Values.Where(
                     obj => obj.DefinitionId == ContentIds.Campfire))
        {
            fire.ResourceAmount = 0f;
        }
    }

    private static ItemInstance Wet(string definitionId) => new(definitionId)
    {
        Wetness = 1f
    };

    private static string ClothingId(WorldState world) =>
        world.Content.ObjectDefinitions.Values.First(definition => definition.Layer is not null).Id;

    private static TileCoord FindOrdinaryOutdoorTile(WorldState world)
    {
        return world.Tiles.Items
            .Where(pair => pair.Value.Flags.HasFlag(TileFlags.Walkable) &&
                !pair.Value.Flags.HasFlag(TileFlags.Water) &&
                !pair.Value.Flags.HasFlag(TileFlags.Indoor) &&
                world.Entities.Objects.Values.All(obj =>
                    HexSpatialMath.HexDistance(pair.Key, obj.Tile) > 1 ||
                    obj.DefinitionId != ContentIds.Campfire) &&
                StructurePlacement.CenterJunction(world, pair.Key) is not null)
            .Select(pair => pair.Key)
            .First();
    }

    private static WorldObjectState SpawnPickup(
        WorldState world, string definitionId, TileCoord tile)
    {
        var junctionId = StructurePlacement.CenterJunction(world, tile);
        Assert.That(junctionId, Is.Not.Null);
        return WorldObjectMutations.SpawnObject(
            world, definitionId, world.Junctions.Items[junctionId.Value].Fragment,
            tile, junctionId.Value);
    }
}

}
