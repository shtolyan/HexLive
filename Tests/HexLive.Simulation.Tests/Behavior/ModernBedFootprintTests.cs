using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// #755-757: the modern bed occupies EXACTLY bed.basic's 14 footprint
/// junctions (rails 0.6495 wu apart, rows 1.5 wu long), blocks them wherever
/// it stands, and its 0.63 x 1.47 wu model fills that footprint without
/// leaving it. In the apartment it stands like the hut bed: long side on the
/// wall, outer rail on the edge line, yaw 0°+60°k.
/// </summary>
public sealed class ModernBedFootprintTests
{
    private const string Bed = ContentIds.ModernBed;

    [Test]
    public void TheModernBedHasThePrimitiveBedsFootprintAndTheModelFitsInsideIt()
    {
        Assert.That(BlueprintFurnitureFootprints.LocalOffsets(Bed),
            Is.EqualTo(BlueprintFurnitureFootprints.LocalOffsets(ContentIds.BedBasic)));
        Assert.That(BlueprintFurnitureFootprints.LocalOffsets(Bed).Count, Is.EqualTo(14));
        Assert.That(PortableFurniture.BlocksFootprintEverywhere(Bed), Is.True);
        Assert.That(PortableFurniture.FootprintRadius(Bed),
            Is.EqualTo(PortableFurniture.FootprintRadius(ContentIds.BedBasic)));

        for (var yaw = 0; yaw < 6; yaw++)
        {
            var points = BlueprintFurnitureFootprints.CenteredWorldOffsets(Bed, yaw);
            var along = Axis(yaw);
            var across = new Float2(-along.Y, along.X);
            var halfLength = points.Max(p => System.MathF.Abs(Dot(p, along)));
            var halfWidth = points.Max(p => System.MathF.Abs(Dot(p, across)));
            Assert.That(halfLength, Is.EqualTo(.75f).Within(.0001f), $"yaw={yaw}");
            Assert.That(halfWidth, Is.EqualTo(PortalApartmentLayout.BedRailInset).Within(.0001f), $"yaw={yaw}");
            Assert.That(ModernLootCatalog.BedLength / 2f, Is.LessThan(halfLength), $"yaw={yaw}");
            Assert.That(ModernLootCatalog.BedWidth / 2f, Is.LessThan(halfWidth), $"yaw={yaw}");
            Assert.That(halfWidth - ModernLootCatalog.BedWidth / 2f, Is.LessThan(.011f), "nearly flush to the rails");
            Assert.That(halfLength - ModernLootCatalog.BedLength / 2f, Is.LessThan(.016f), "nearly flush to the end rows");
        }
    }

    private static (SimulationEngine Engine, NPCState Walker, WorldObjectState Bed, JunctionKey Key,
        HashSet<JunctionId> Footprint) PlaceBed(int yawStep)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var walker = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).First();
        walker.Inventory.Items.Add(new ItemInstance(Bed) { OwnerId = walker.Id.Value });
        Junction anchor = null;
        foreach (var tile in world.Tiles.Items.Keys
                     .Where(t => HexSpatialMath.HexDistance(t, walker.Tile) is >= 1 and <= 3)
                     .OrderBy(t => HexSpatialMath.HexDistance(t, walker.Tile)).ThenBy(t => t.Q).ThenBy(t => t.R))
        {
            if (world.Tiles.Items[tile].Flags.HasFlag(TileFlags.Indoor)) continue;
            var centre = HexSpatialMath.TileToWorld(tile);
            anchor = world.Tiles.Items[tile].Junctions.Select(id => world.Junctions.Items[id])
                .Where(j => HexSpatialMath.Distance(j.WorldPosition, centre) < .01f)
                .FirstOrDefault(j => FurniturePlacement.Validate(world, Bed, j, yawStep) == null &&
                    Around(world, FurniturePlacement.KeyOf(j.WorldPosition), yawStep).All(p => p != null && !p.Blocked));
            if (anchor != null) break;
        }

        Assert.That(anchor, Is.Not.Null);
        var key = FurniturePlacement.KeyOf(anchor.WorldPosition);
        var receipt = engine.ApplyManualCommand(new PlaceFurnitureCommand(Bed, key.XKey, key.YKey, yawStep));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);
        var bed = world.Entities.Objects.Values.Single(o => o.DefinitionId == Bed);
        var footprint = FurniturePlacement.FootprintKeys(ContentIds.BedBasic, key, yawStep)
            .Select(k => FurniturePlacement.TryFindJunction(world, k, out var j) ? j.Id : default).ToHashSet();
        return (engine, walker, bed, key, footprint);
    }

    // Two points four lattice columns either side of the middle row (±1.30 wu).
    private static Junction[] Around(WorldState world, JunctionKey key, int yawStep)
    {
        var result = new Junction[2];
        for (var side = 0; side < 2; side++)
        {
            var local = BlueprintGeometry.RotateJunctionOffset(new JunctionKey(side == 0 ? -4 : 4, 0), yawStep);
            result[side] = FurniturePlacement.TryFindJunction(world,
                new JunctionKey(key.XKey + local.XKey, key.YKey + local.YKey), out var j) ? j : null;
        }
        return result;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void APlacedModernBedBlocksBedBasicsFourteenPointsAndAWalkGoesAround(int yawStep)
    {
        var (engine, walker, bed, key, footprint) = PlaceBed(yawStep);
        var world = engine.World;
        Assert.That(footprint.Count, Is.EqualTo(14));
        Assert.That(bed.BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True,
            $"blocked={bed.BlockedJunctions.Count}");
        Assert.That(footprint.All(id => world.Junctions.Items[id].Blocked), Is.True);

        var ends = Around(world, key, yawStep);
        Teleport(world, walker, ends[0]);
        walker.Mind.ManualControl = true;
        var receipt = engine.ApplyManualCommand(new MoveToCommand(walker.Id, ends[1].WorldPosition));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);

        var anchor = world.Junctions.Items[bed.Junctions[0]].WorldPosition;
        var centre = anchor + BlueprintFurnitureFootprints.CentroidOffset(Bed, yawStep);
        var along = Axis(yawStep);
        var path = new List<int>();
        var inside = new List<string>();
        for (var tick = 0; tick < 240; tick++)
        {
            engine.Step();
            if (walker.CurrentJunction is { } current && (path.Count == 0 || path[^1] != current.Value))
                path.Add(current.Value);
            var d = walker.Position - centre;
            var localY = Dot(d, along);
            var localX = Dot(d, new Float2(-along.Y, along.X));
            if (System.MathF.Abs(localX) < ModernLootCatalog.BedWidth / 2f &&
                System.MathF.Abs(localY) < ModernLootCatalog.BedLength / 2f)
                inside.Add($"t{tick}({localX:0.00},{localY:0.00})");
            if (HexSpatialMath.Distance(walker.Position, ends[1].WorldPosition) < .01f) break;
        }

        TestContext.Out.WriteLine($"yaw={yawStep} path: {string.Join(" ", path)}");
        Assert.That(HexSpatialMath.Distance(walker.Position, ends[1].WorldPosition), Is.LessThan(.01f), "never arrived");
        Assert.That(path.Where(v => footprint.Contains(new JunctionId(v))), Is.Empty, "The route stepped on a bed junction.");
        Assert.That(inside, Is.Empty, "The body was interpolated through the bed: " + string.Join(", ", inside));
    }

    [Test]
    public void PickingTheBedUpReleasesItsFootprintAndLoadStampsA0121PlacedBed()
    {
        var (engine, _, bed, _, footprint) = PlaceBed(1);
        var world = engine.World;

        // 0.1.121: a placed modern bed blocked nothing.
        WorldObjectMutations.ReleaseOwnedBlocking(world, bed);
        Assert.That(footprint.Any(id => world.Junctions.Items[id].Blocked), Is.False);
        var loaded = RoundTrip(world, () => TestWorld.CreateWorld(world.Seed));
        var restored = loaded.Entities.Objects[bed.Id];
        Assert.That(restored.BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True);
        Assert.That(footprint.All(id => loaded.Junctions.Items[id].Blocked), Is.True);

        WorldObjectMutations.DespawnObject(loaded, restored.Id);
        Assert.That(footprint.Any(id => loaded.Junctions.Items[id].Blocked), Is.False);
    }

    [TestCase(0)]
    [TestCase(7)]
    [TestCase(13)]
    public void TheApartmentBedLiesAlongAWallOnTheLatticeAndBlocksItsFootprint(int seed)
    {
        var world = CreatePortalWorld(seed);
        for (var night = 1; night <= 4; night++)
        {
            world.Tick = night * WorldBalance.DayLengthTicks;
            Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
            var serial = world.Portal.VisitSerial;
            var context = $"seed={seed} visit={serial}";
            var bed = world.Entities.Objects.Values.Single(o => o.DefinitionId == Bed &&
                PortalWorld.TryGetApartmentFurnitureVisit(o.Variant, out _));
            AssertApartmentBed(world, bed, serial, context);
        }
    }

    [TestCase(0)]
    [TestCase(7)]
    public void A0121ApartmentBedIsReseatedOnLoad(int seed)
    {
        var world = CreatePortalWorld(seed);
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
        var serial = world.Portal.VisitSerial;
        var bed = world.Entities.Objects.Values.Single(o => o.DefinitionId == Bed &&
            PortalWorld.TryGetApartmentFurnitureVisit(o.Variant, out _));
        var anchor = bed.Junctions[0];
        var rotation = bed.RotationDegrees;

        // The 0.1.121 stamp: head on the wall at 30°+60°k, on the junction
        // nearest the old 0.904 wu-deep centre, blocking nothing.
        WorldObjectMutations.ReleaseOwnedBlocking(world, bed);
        var centre = HexSpatialMath.TileToWorld(bed.Tile);
        Assert.That(PortalWorld.TryGetApartmentFurniturePlacement(Bed, bed.Tile, seed, out var slot, out _, serial));
        var normal = HexSpatialMath.Normalize(slot - centre);
        var old = centre + normal * (HexSpatialMath.HexApothem - .10f - .904f);
        bed.Junctions[0] = world.Tiles.Items[bed.Tile].Junctions.Select(id => world.Junctions.Items[id])
            .Where(j => !j.Blocked).OrderBy(j => HexSpatialMath.Distance(j.WorldPosition, old)).First().Id;
        bed.RotationDegrees = (rotation + 90f) % 360f;

        var loaded = RoundTrip(world, () => CreatePortalWorld(seed));
        var restored = loaded.Entities.Objects[bed.Id];
        Assert.That(restored.Junctions[0], Is.EqualTo(anchor));
        Assert.That(restored.RotationDegrees, Is.EqualTo(rotation));
        AssertApartmentBed(loaded, restored, serial, $"seed={seed} reloaded");
    }

    private static void AssertApartmentBed(WorldState world, WorldObjectState bed, int serial, string context)
    {
        Assert.That(bed.RotationDegrees % 60f, Is.EqualTo(0f).Within(.001f), context);
        var yaw = FurniturePlacement.YawStepOf(bed.RotationDegrees);
        var anchor = world.Junctions.Items[bed.Junctions[0]];
        var footprint = FurniturePlacement.FootprintKeys(ContentIds.BedBasic, FurniturePlacement.KeyOf(anchor.WorldPosition), yaw)
            .Select(k => FurniturePlacement.TryFindJunction(world, k, out var j) ? j : null).ToArray();
        Assert.That(footprint.All(j => j != null), Is.True, context);
        Assert.That(bed.BlockedJunctions.ToHashSet().SetEquals(footprint.Where(j => !j.Door).Select(j => j.Id)),
            Is.True, context);
        Assert.That(footprint.All(j => j.Blocked || j.Door), Is.True, context);

        // Centre: one rail inset from the wall's edge line, toward a real wall.
        var tileCentre = HexSpatialMath.TileToWorld(bed.Tile);
        var centre = anchor.WorldPosition + BlueprintFurnitureFootprints.CentroidOffset(Bed, yaw);
        Assert.That(HexSpatialMath.Distance(centre, tileCentre),
            Is.EqualTo(HexSpatialMath.HexApothem - PortalApartmentLayout.BedRailInset).Within(.001f), context);
        var normal = HexSpatialMath.Normalize(centre - tileCentre);
        var outside = HexSpatialMath.WorldToTile(tileCentre + normal * (2f * HexSpatialMath.HexApothem));
        Assert.That(PortalWorld.HasWall(bed.Tile, outside, world.Seed, serial), Is.True, context);
        var along = Axis(yaw);
        Assert.That(System.MathF.Abs(Dot(along, normal)), Is.LessThan(.001f), "long side parallel to the wall");

        // The 0.63 x 1.47 model stays inside the hexagon in every corner.
        var across = new Float2(-along.Y, along.X);
        foreach (var sx in new[] { -1f, 1f })
        foreach (var sy in new[] { -1f, 1f })
        {
            var corner = centre - tileCentre + across * (sx * ModernLootCatalog.BedWidth / 2f) +
                along * (sy * ModernLootCatalog.BedLength / 2f);
            for (var edge = 0; edge < 6; edge++)
            {
                var radians = edge * 60f * System.MathF.PI / 180f;
                var edgeNormal = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
                Assert.That(Dot(corner, edgeNormal), Is.LessThan(HexSpatialMath.HexApothem), context);
            }
        }
    }

    private static Float2 Axis(int yawStep) => HexSpatialMath.Normalize(BlueprintGeometry.JunctionToWorld(
        BlueprintGeometry.RotateJunctionOffset(new JunctionKey(0, 2), yawStep)));

    private static float Dot(Float2 a, Float2 b) => a.X * b.X + a.Y * b.Y;

    private static void Teleport(WorldState world, NPCState npc, Junction point)
    {
        if (npc.CurrentJunction is { } old) SpatialMutations.FreeJunction(world, old, npc.Id);
        var tile = point.Tiles.Contains(HexSpatialMath.WorldToTile(point.WorldPosition))
            ? HexSpatialMath.WorldToTile(point.WorldPosition) : point.Tiles[0];
        SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, tile);
        SpatialMutations.OccupyJunction(world, point.Id, npc.Id);
        npc.CurrentJunction = point.Id;
        npc.Tile = tile;
        npc.Position = point.WorldPosition;
    }

    private static WorldState CreatePortalWorld(int seed)
    {
        var bootstrap = new WorldBootstrapDefinition();
        bootstrap.Simulation.Seed = seed;
        bootstrap.Simulation.EnablePortalTopology = true;
        var island = new FragmentBootstrap { Id = 1 };
        for (var q = -4; q <= 4; q++)
        for (var r = -4; r <= 4; r++)
            if (HexSpatialMath.HexDistance(new TileCoord(q, r), TileCoord.Zero) <= 4)
                island.Tiles.Add(new TileBootstrap { Q = q, R = r });
        bootstrap.Fragments.Add(island);
        var world = new WorldStateFactory().CreateTopology(bootstrap);
        world.FactionHomes[Faction.Colony] = TileCoord.Zero;
        return world;
    }

    private static WorldState RoundTrip(WorldState source, System.Func<WorldState> create)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(source, writer, WorldSaveSerializer.BlobVersion);
        stream.Position = 0;
        var loaded = create();
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }
}
