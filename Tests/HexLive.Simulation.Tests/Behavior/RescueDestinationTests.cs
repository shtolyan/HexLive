using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class RescueDestinationTests
{
    [Test]
    public void PatientWithoutBed_UsesHelpersBedBeforeSharedBed_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        RemoveObjects(world, obj => KenshiRescueMath.IsBed(obj));

        var sites = ReachableBuildSites(world, helper, patient);
        Assert.That(sites.Count, Is.GreaterThanOrEqualTo(2));

        var helperBed = SpawnUsableBed(world, helper, patient, sites, helper.Id);

        helperBed.IsOccupied = true;
        helperBed.CurrentUser = helper.Id;
        var shared = SpawnUsableBed(world, helper, patient, sites, owner: null);
        helperBed.IsOccupied = false;
        helperBed.CurrentUser = null;

        var found = KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(destination, Is.SameAs(helperBed),
                "When the patient has no bed, the rescuer's bed outranks a shared bed.");
            Assert.That(helperBed.CurrentUser, Is.EqualTo(patient.Id));
        });
    }

    [Test]
    public void NoBeds_ColdCampfireStillProvidesSafeGround_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        RemoveObjects(world, obj =>
            KenshiRescueMath.IsBed(obj) || obj.DefinitionId == ContentIds.Campfire);

        var fireSite = ReachableBuildSites(world, helper, patient)[0];
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, helper.Fragment, fireSite.Tile, fireSite.Junction);
        fire.ResourceAmount = 0f;

        var found = KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out var approach,
            out var destinationTile);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(destination, Is.SameAs(fire),
                "A cold hearth remains the camp anchor for a bedless rescue.");
            Assert.That(world.Tiles.Items[destinationTile].Flags.HasFlag(TileFlags.Water), Is.False);
            Assert.That(HexPathfinder.FindPath(
                    world, helper.CurrentJunction!.Value, approach,
                    PathfindingSystem.OtherActorJunctions(world, helper),
                    weightClimb: true, canJump: false),
                Is.Not.Empty, "Pickup must be preceded by the actual no-jump carry route.");
        });

        KenshiRescueMath.BeginCarry(
            world, helper, patient, destination, approach, destinationTile);
        Assert.Multiple(() =>
        {
            Assert.That(helper.CarriedNpcId, Is.EqualTo(patient.Id));
            Assert.That(patient.CarriedByNpcId, Is.EqualTo(helper.Id));
            Assert.That(helper.Plan.TargetTile, Is.EqualTo(destinationTile));
        });
    }

    [Test]
    public void NoBedsAndNoCampfire_UsesFactionHomeGround_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        RemoveObjects(world, obj =>
            KenshiRescueMath.IsBed(obj) || obj.DefinitionId == ContentIds.Campfire);

        Assert.That(ColonyQueries.Home(world, patient.Faction), Is.Not.Null);
        var found = KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out var approach,
            out var destinationTile);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(destination, Is.Null);
            Assert.That(HexSpatialMath.HexDistance(
                    destinationTile, ColonyQueries.Home(world, patient.Faction)!.Value),
                Is.LessThanOrEqualTo(Spec72.MaxCampRadiusTiles));
            Assert.That(LyingSpot.CanSolveOnTile(world, patient, destinationTile), Is.True);
        });

        Assert.DoesNotThrow(() => KenshiRescueMath.BeginCarry(
            world, helper, patient, destination, approach, destinationTile));
        Assert.That(helper.RescueDestinationObjectId, Is.Null);
        Assert.That(helper.CarriedNpcId, Is.EqualTo(patient.Id));
    }

    private static (NPCState Helper, NPCState Patient) RescuePair(WorldState world)
    {
        var pair = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .Take(2)
            .ToList();
        Assert.That(pair, Has.Count.EqualTo(2));
        foreach (var npc in pair)
        {
            if (npc.CurrentJunction is null)
            {
                npc.CurrentJunction = SpatialQueries.FindNearestJunction(world, npc.Position);
            }

            Assert.That(npc.CurrentJunction, Is.Not.Null);
        }

        return (pair[0], pair[1]);
    }

    private static List<(TileCoord Tile, JunctionId Junction)> ReachableBuildSites(
        WorldState world, NPCState helper, NPCState patient)
    {
        var sites = new List<(TileCoord Tile, JunctionId Junction)>();
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (tile.Coord == helper.Tile || tile.Coord == patient.Tile ||
                !ColonyQueries.InCamp(world, tile.Coord, patient.Faction) ||
                !StructurePlacement.HexFreeForBuild(world, tile.Coord) ||
                StructurePlacement.CenterJunction(world, tile.Coord) is not { } center ||
                HexPathfinder.FindPath(
                    world, helper.CurrentJunction!.Value, center, null,
                    weightClimb: true, canJump: false).Count == 0)
            {
                continue;
            }

            sites.Add((tile.Coord, center));
        }

        sites.Sort((a, b) =>
        {
            var byDistance = HexSpatialMath.HexDistance(helper.Tile, a.Tile)
                .CompareTo(HexSpatialMath.HexDistance(helper.Tile, b.Tile));
            if (byDistance != 0)
            {
                return byDistance;
            }

            var byQ = a.Tile.Q.CompareTo(b.Tile.Q);
            return byQ != 0 ? byQ : a.Tile.R.CompareTo(b.Tile.R);
        });
        return sites;
    }

    private static WorldObjectState SpawnUsableBed(
        WorldState world, NPCState helper, NPCState patient,
        List<(TileCoord Tile, JunctionId Junction)> sites, EntityId? owner)
    {
        for (var i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            if (!StructurePlacement.HexFreeForBuild(world, site.Tile))
            {
                continue;
            }

            var bed = WorldObjectMutations.SpawnObject(
                world, ContentIds.BedBasic, new FragmentId(1), site.Tile, site.Junction);
            bed.Owner = owner;
            var found = KenshiRescueMath.TryFindDestination(
                world, helper, patient, out var selected, out var approach, out _);
            if (found)
            {
                SpatialMutations.ReleaseJunctionReservation(world, approach, helper.Id);
            }

            if (found && selected == bed)
            {
                if (bed.CurrentUser == patient.Id)
                {
                    bed.IsOccupied = false;
                    bed.CurrentUser = null;
                }

                sites.RemoveAt(i);
                return bed;
            }

            WorldObjectMutations.DespawnObject(world, bed.Id);
        }

        Assert.Fail("Test world has no physically reachable bed placement.");
        return null;
    }

    private static void RemoveObjects(
        WorldState world, System.Func<WorldObjectState, bool> predicate)
    {
        foreach (var id in world.Entities.Objects.Values
                     .Where(predicate).Select(obj => obj.Id).ToList())
        {
            WorldObjectMutations.DespawnObject(world, id);
        }
    }
}

}
