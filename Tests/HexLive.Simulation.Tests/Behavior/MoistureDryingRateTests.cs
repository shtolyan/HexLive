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
