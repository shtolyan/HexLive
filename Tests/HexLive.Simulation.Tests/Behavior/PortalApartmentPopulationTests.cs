using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PortalApartmentPopulationTests
{
    private IReadOnlyList<GarmentParams> _previousGarments;

    [SetUp]
    public void UseBuiltInGarments()
    {
        _previousGarments = GarmentLibrary.Active;
        GarmentLibrary.ResetToDefaults();
    }

    [TearDown]
    public void RestoreGarments() => GarmentLibrary.Override(_previousGarments);

    [Test]
    public void FirstVisitUsesAFreeApartmentPointEvenAtThePlayerWorldLimit()
    {
        var world = CreateWorld();
        world.CreationConfig = new WorldCreationConfig
        {
            PopulationLimit = 1, PlayersOnly = true, PlayerCamp = Faction.Colony
        };
        var outside = Landing(world, TileCoord.Zero);
        var player = new NPCState
        {
            Id = new EntityId(7), Faction = Faction.Colony, Fragment = outside.Fragment,
            Tile = outside.Tile, Position = outside.Position, CurrentJunction = outside.Junction,
        };
        PopulationArrivalMath.AddToWorld(world, player, outside.Junction);
        world.PlayerControlledNpcs.Add(player.Id.Value);
        world.NextRuntimeObjectId = 1_000_000;
        world.DeathRecords.Add(new DeathRecord { EntityId = new EntityId(1_000_000) });

        var inside = ApartmentLandings(world);
        Assert.That(SpatialMutations.TryReserveJunction(world, inside[0].Junction, player.Id, world.Tick, 48), Is.True);
        var resident = PortalApartmentPopulation.TrySpawnResident(world, 1, new[] { outside, inside[0], inside[1] });

        Assert.That(resident, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(resident.Id.Value, Is.EqualTo(1_000_001));
            Assert.That(resident.ProfileId, Is.EqualTo(PortalWorld.ResidentProfileId));
            Assert.That(resident.Faction, Is.EqualTo(Faction.Castaway));
            Assert.That(resident.CurrentJunction, Is.EqualTo(inside[1].Junction));
            Assert.That(world.Portal.Resident, Is.EqualTo(resident.Id));
            Assert.That(world.Occupancy.JunctionOwner[inside[1].Junction], Is.EqualTo(resident.Id));
            Assert.That(world.PlayerControlledNpcs, Is.EquivalentTo(new[] { player.Id.Value }));
            Assert.That(resident.Mind.ManualControl, Is.False);
            Assert.That(resident.Inventory.Items, Is.Empty);
            Assert.That(resident.Needs.Stress, Is.EqualTo(.8f));
            Assert.That(resident.Traits.Has(TraitKind.Coward), Is.True);
            Assert.That(AttributeSet.All.Select(resident.Attributes.Get),
                Is.All.InRange(.36f - 1e-5f, .64f + 1e-5f));
            Assert.That(SkillSet.All.Select(resident.Skills.Get), Is.All.Zero);
        });
        Assert.That(PortalApartmentPopulation.TrySpawnResident(world, 1, inside), Is.Null);
    }

    [Test]
    public void SameDayVisitsVaryOutfitsAndNeverReuseAResidentIdentity()
    {
        var world = CreateWorld();
        var points = ApartmentLandings(world);
        var appearances = new List<bool>();
        var ids = new HashSet<EntityId>();
        var bras = new HashSet<string>();
        var briefs = new HashSet<string>();
        var outfits = new HashSet<string>();
        for (var visit = 1; visit <= 33; visit++)
        {
            var resident = PortalApartmentPopulation.TrySpawnResident(world, visit, points);
            appearances.Add(resident != null);
            if (resident is null) continue;

            Assert.That(ids.Add(resident.Id), Is.True);
            var worn = resident.WornItems.Select(item => world.Content.ObjectDefinitions[item.DefinitionId]).ToArray();
            Assert.That(resident.WornItems.All(item => item.OwnerId == resident.Id.Value &&
                GarmentLibrary.IsPortalWearAvailable(item.DefinitionId) &&
                !GarmentLibrary.IsSpawnable(item.DefinitionId)), Is.True);
            for (var index = 0; index < worn.Length; index++)
                Assert.That(worn.Skip(index + 1).Any(other => WearSlotCatalog.Occupies(worn[index], other)), Is.False);
            var underwear = worn.Where(item => item.Layer == WearLayer.Underwear).ToArray();
            bras.Add(underwear.Single(item => item.Covers.Contains(BodyPart.Torso) &&
                !item.Covers.Contains(BodyPart.Pelvis)).Id);
            briefs.Add(underwear.Single(item => item.Covers.Contains(BodyPart.Pelvis)).Id);
            outfits.Add(string.Join(",", worn.Where(item => item.Layer != WearLayer.Underwear).Select(item => item.Id)));

            world.Portal.Resident = null;
            AdminWorldCommands.Execute(world, new AdminCommand { Kind = "delete_npc", NpcId = resident.Id.Value });
            Assert.That(world.Entities.Npcs.ContainsKey(resident.Id), Is.False);
        }

        Assert.That(appearances[0], Is.True);
        for (var i = 1; i < appearances.Count; i += 2)
            Assert.That(appearances[i], Is.Not.EqualTo(appearances[i + 1]));
        Assert.That(bras.Count, Is.GreaterThan(1));
        Assert.That(briefs.Count, Is.GreaterThan(1));
        Assert.That(outfits.Count, Is.GreaterThan(1));
    }

    [Test]
    public void WardrobeStockVariesFromTwoToFourAndUsesTheNormalLootRows()
    {
        var world = CreateWorld();
        var counts = new HashSet<int>();
        var assortments = new HashSet<string>();
        for (var visit = 1; visit <= 24; visit++)
        {
            var wardrobe = CreateWardrobe(world);
            var count = PortalApartmentPopulation.StockWardrobe(world, wardrobe, visit);
            counts.Add(count);
            assortments.Add(string.Join(",", wardrobe.Contents.Select(item => item.DefinitionId).OrderBy(id => id)));
            Assert.That(count, Is.InRange(2, 4));
            Assert.That(wardrobe.Contents.Select(item => item.DefinitionId).Distinct().Count(), Is.EqualTo(count));
            Assert.That(wardrobe.Contents.All(item => GarmentLibrary.IsPortalWearAvailable(item.DefinitionId) &&
                !GarmentLibrary.IsSpawnable(item.DefinitionId)), Is.True);
            Assert.That(PortalApartmentPopulation.StockWardrobe(world, wardrobe, visit), Is.Zero);

            var first = wardrobe.Contents[0];
            Assert.That(ContainerLootMath.IsLootable(world, wardrobe), Is.True);
            Assert.That(ContainerLootMath.TryResolve(world, wardrobe, 0, first.DefinitionId, 1,
                out var items, out var groundSources), Is.True);
            Assert.That(items.Single(), Is.SameAs(first));
            Assert.That(groundSources, Is.Empty);
        }

        Assert.That(counts, Is.EquivalentTo(new[] { 2, 3, 4 }));
        Assert.That(assortments.Count, Is.GreaterThan(3));
    }

    [Test]
    public void WardrobeAndResidentOnlyUseInstalledCivilianGarments()
    {
        var installed = new[] { "clothing.shorts_classic", "clothing.halter_vamp", "clothing.croptop_fit" };
        GarmentLibrary.Override(GarmentLibrary.Active.Where(item =>
            !PortalContentPolicy.IsPortalWear(item.Id) || installed.Contains(item.Id)));
        var world = CreateWorld();
        var wardrobe = CreateWardrobe(world);

        Assert.That(PortalApartmentPopulation.StockWardrobe(world, wardrobe, 1), Is.InRange(2, 3));
        Assert.That(wardrobe.Contents.Select(item => item.DefinitionId), Is.SubsetOf(installed));
        var resident = PortalApartmentPopulation.TrySpawnResident(world, 1, ApartmentLandings(world));
        Assert.That(resident.WornItems.Select(item => item.DefinitionId), Is.SubsetOf(installed));
    }

    private static WorldState CreateWorld()
    {
        var definition = new WorldBootstrapDefinition();
        definition.Simulation.Seed = 692;
        definition.Simulation.EnablePortalTopology = true;
        var island = new FragmentBootstrap { Id = 1 };
        island.Tiles.Add(new TileBootstrap { Q = 0, R = 0 });
        definition.Fragments.Add(island);
        return new WorldStateFactory().CreateTopology(definition);
    }

    private static PopulationArrivalMath.Landing[] ApartmentLandings(WorldState world) =>
        PortalWorld.ApartmentTiles(world.Seed).Select(tile => Landing(world, tile)).ToArray();

    private static PopulationArrivalMath.Landing Landing(WorldState world, TileCoord tile)
    {
        var point = world.Junctions.Items[StructurePlacement.CenterJunction(world, tile).Value];
        return new PopulationArrivalMath.Landing(tile, point.Id, point.Fragment, point.WorldPosition);
    }

    private static WorldObjectState CreateWardrobe(WorldState world)
    {
        const string id = "furniture.modern_wardrobe";
        world.Content.ObjectDefinitions[id] = new ObjectDefinition { Id = id, Tags = { ObjectTags.Wardrobe } };
        var at = Landing(world, PortalWorld.ApartmentCenter);
        return WorldObjectMutations.SpawnObject(world, id, at.Fragment, at.Tile, at.Junction);
    }
}
