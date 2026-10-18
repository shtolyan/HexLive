using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §113.8 + §64.10 — жалоба игрока с Сингапура (сид 629209070): девушки ложатся
/// на пол дома рядом со свободной своей кроватью, и голова торчит из стены.
/// </summary>
public sealed class HomeBedSleepTests
{
    private const float Apothem = 1.2990381f; // HexRadius * √3 / 2

    private static NPCState[] Colonists(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .ToArray();

    private static void PutOnJunction(WorldState world, NPCState npc, Junction junction)
    {
        if (npc.CurrentJunction is { } old)
        {
            SpatialMutations.FreeJunction(world, old, npc.Id);
        }

        var previous = npc.Tile;
        npc.Tile = junction.Tiles[0];
        SpatialMutations.MoveEntityToTile(world, npc.Id, previous, npc.Tile);
        npc.Position = junction.WorldPosition;
        npc.CurrentJunction = junction.Id;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        SpatialMutations.OccupyJunction(world, junction.Id, npc.Id);
    }

    private static Float2[] BodyCorners(NPCState npc)
    {
        var radians = npc.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        var along = forward * LyingSpot.BodyHalfLength;
        var side = lateral * LyingSpot.BodyHalfWidth;
        return new[]
        {
            npc.Position + along + side, npc.Position + along - side,
            npc.Position - along + side, npc.Position - along - side
        };
    }

    // Pointy-top hex: the three edge normals are 0°, 60° and 120°.
    private static float HexOverhang(Float2 point, Float2 centre)
    {
        var d = point - centre;
        var worst = float.MinValue;
        foreach (var degrees in new[] { 0f, 60f, 120f })
        {
            var radians = degrees * (System.MathF.PI / 180f);
            var along = System.MathF.Abs(d.X * System.MathF.Cos(radians) + d.Y * System.MathF.Sin(radians));
            worst = System.MathF.Max(worst, along - Apothem);
        }

        return worst;
    }

    [Test]
    public void HutWall_NoPartOfAGroundBodyLeavesTheRoom()
    {
        var world = TestWorld.CreateWorld();
        var hut = TestWorld.StartHut(world);
        var room = hut.Tile;
        var centre = HexSpatialMath.TileToWorld(room);
        // An empty room: the staked furniture sites would fill it on their own
        // and hide what this test is about — the walls.
        foreach (var obj in world.Entities.Objects.Values
                     .Where(o => o.Tile.Equals(room) && o.ArchitectureElements.Count == 0 &&
                                 !o.Id.Equals(hut.Id))
                     .ToList())
        {
            WorldObjectMutations.DespawnObject(world, obj.Id);
        }

        var girl = Colonists(world)[0];
        foreach (var other in world.Entities.Npcs.Values.Where(n => !n.Id.Equals(girl.Id)))
        {
            other.Tile = new TileCoord(room.Q + 6, room.R);
        }

        // Start beside each wall in turn: the nearest node is always a rim
        // node, and its axis-snapped heading used to run straight out through
        // the planks (or slip between two wall nodes 0.375 wu apart).
        var rim = world.Tiles.Items[room].Junctions
            .Select(id => world.Junctions.Items[id])
            .Where(j => !j.Blocked && j.Tiles.Count == 1 &&
                        HexSpatialMath.Distance(j.WorldPosition, centre) > 1.0f)
            .OrderBy(j => j.Id.Value)
            .ToList();
        Assert.That(rim, Is.Not.Empty);
        var checkedPoses = 0;
        foreach (var start in rim)
        {
            PutOnJunction(world, girl, start);
            var outward = start.WorldPosition - centre;
            girl.RotationDegrees = System.MathF.Atan2(outward.Y, outward.X) * 180f / System.MathF.PI;
            ExecutionSystem.ReleaseClaims(world, girl);
            if (!ExecutionSystem.TryLieDownOnGround(world, girl))
            {
                continue;
            }

            checkedPoses++;
            foreach (var corner in BodyCorners(girl))
            {
                Assert.That(HexOverhang(corner, centre), Is.LessThanOrEqualTo(0.0005f),
                    $"Body from rim node {start.Id.Value} pokes {HexOverhang(corner, centre):F3} wu " +
                    $"through the hut wall (pose {girl.Position.X - centre.X:F3}," +
                    $"{girl.Position.Y - centre.Y:F3} heading {girl.RotationDegrees:F0}).");
            }
        }

        Assert.That(checkedPoses, Is.GreaterThan(0), "An empty one-hex room must still fit a body.");
    }

    [Test]
    public void WallNodes_ABodyCannotSlipBetweenTwoOfThem()
    {
        // Сейв Сингапура: узел (0.974, −0.5625), курс 0 — тело 0.36 wu шириной
        // проходило между узлами окна (1.299, −0.75) и (1.299, −0.375) и
        // торчало наружу на 0.335 wu.
        var world = TestWorld.CreateWorld();
        var girl = Colonists(world)[0];
        var centre = HexSpatialMath.TileToWorld(girl.Tile);
        var spot = centre + new Float2(0.9743f, -0.5625f);
        var across = (Forward: new Float2(1f, 0f), Lateral: new Float2(0f, 1f));
        var along = (Forward: new Float2(0f, 1f), Lateral: new Float2(-1f, 0f));
        var alongBefore = LyingSpot.BodyClear(world, girl, girl.Tile, spot, along.Forward, along.Lateral);
        var wallNodes = 0;
        foreach (var id in world.Tiles.Items[girl.Tile].Junctions)
        {
            var junction = world.Junctions.Items[id];
            var d = junction.WorldPosition - centre;
            if (d.X > Apothem - 0.01f && d.Y > -0.8f && d.Y < -0.3f)
            {
                junction.Blocked = true;
                wallNodes++;
            }
        }

        Assert.That(wallNodes, Is.EqualTo(2), "The window run of the save: (1.299, −0.75) and (1.299, −0.375).");
        Assert.That(LyingSpot.BodyClear(world, girl, girl.Tile, spot, across.Forward, across.Lateral),
            Is.False, "Two blocked wall nodes 0.375 wu apart must not let a 0.36 wu body through.");
        Assert.That(LyingSpot.BodyClear(world, girl, girl.Tile, spot, along.Forward, along.Lateral),
            Is.EqualTo(alongBefore),
            "Lying ALONG the wall the same node keeps 0.145 wu to the run — the wall must not reject it.");
    }

    [Test]
    public void RaisedBed_InheritsTheCampsPermanentMemoryOfItsSite()
    {
        var world = TestWorld.CreateWorld();
        var owner = Colonists(world)[0];
        var stranger = Colonists(world)[1];
        var tile = world.Tiles.Items.Values
            .Where(t => t.Flags.HasFlag(TileFlags.Walkable) && !t.Flags.HasFlag(TileFlags.Water) &&
                        StructurePlacement.CenterJunction(world, t.Coord) is not null)
            .OrderBy(t => HexSpatialMath.HexDistance(t.Coord, owner.Tile))
            .ThenBy(t => t.Coord.Q).ThenBy(t => t.Coord.R)
            .First(t => HexSpatialMath.HexDistance(t.Coord, owner.Tile) >= 2);
        var anchor = StructurePlacement.CenterJunction(world, tile.Coord).Value;
        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, owner.Fragment, tile.Coord, anchor);
        site.BuildProduct = ContentIds.BedBasic;
        site.Owner = owner.Id;
        owner.Memory.KnownObjects[site.Id] = new ObjectMemory
        {
            Id = site.Id, DefinitionId = site.DefinitionId, Tile = site.Tile,
            Junction = anchor, IsPermanent = true, LastSeenTick = world.Tick
        };
        stranger.Memory.KnownObjects.Remove(site.Id);

        var bed = ExecutionSystem.RaiseFurnitureSite(world, site, owner.Fragment, null);

        Assert.That(bed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(owner.Memory.KnownObjects.ContainsKey(site.Id), Is.False,
                "The site is gone; its record must not linger as a ghost.");
            Assert.That(owner.Memory.KnownObjects.TryGetValue(bed.Id, out var record), Is.True,
                "The raised bed is a new object id — the site's record has to move onto it.");
            Assert.That(record?.IsPermanent, Is.True,
                "A permanent site makes a permanent bed, or the 256-record cap evicts it.");
            Assert.That(record?.Junction, Is.EqualTo(anchor));
        });
    }

    [Test]
    public void SleepPlanner_RecallsAnEvictedOwnBedInsteadOfTheFloor()
    {
        var engine = TestWorld.CreateEngine(1104);
        for (var i = 0; i < 4; i++) engine.Step();
        var world = engine.World;
        var girl = Colonists(world)[0];

        // Every bed she knows of is forgotten, as after the memory cap.
        foreach (var known in girl.Memory.KnownObjects.Values
                     .Where(r => ContentIds.IsBed(r.DefinitionId) ||
                                 r.DefinitionId == ContentIds.BuildSite)
                     .Select(r => r.Id).ToList())
        {
            girl.Memory.KnownObjects.Remove(known);
        }
        girl.Perception.Objects.RemoveAll(p =>
            ContentIds.IsBed(p.DefinitionId) || p.DefinitionId == ContentIds.BuildSite);

        var start = girl.CurrentJunction.Value;
        var tile = world.Tiles.Items.Values
            .Where(t => t.Flags.HasFlag(TileFlags.Walkable) && !t.Flags.HasFlag(TileFlags.Water) &&
                        (!world.Caches.ObjectsByTile.TryGetValue(t.Coord, out var objs) || objs.Count == 0))
            .Where(t => ColonyQueries.InCamp(world, t.Coord, girl.Faction) &&
                        StructurePlacement.CenterJunction(world, t.Coord) is { } c &&
                        Connectivity.Reachable(world, start, c, girl.Body.CanJump))
            .OrderBy(t => HexSpatialMath.HexDistance(t.Coord, girl.Tile))
            .ThenBy(t => t.Coord.Q).ThenBy(t => t.Coord.R)
            .First(t => HexSpatialMath.HexDistance(t.Coord, girl.Tile) >= 2);
        var bed = WorldObjectMutations.SpawnObject(
            world, ContentIds.BedBasic, girl.Fragment, tile.Coord,
            StructurePlacement.CenterJunction(world, tile.Coord).Value);
        bed.Owner = girl.Id;

        girl.Needs.Energy = 0.2f;
        girl.Mind.CurrentGoal = GoalType.Sleep;
        girl.Plan.Steps.Clear();
        girl.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(girl.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(girl.Plan.Steps.Select(s => s.Type),
                Does.Not.Contain(PlanStepType.GroundSleep),
                "She knows where her bed is; the floor is only for a camp without one.");
            Assert.That(girl.Plan.TargetObjectId, Is.EqualTo(bed.Id));
            Assert.That(girl.Memory.KnownObjects.TryGetValue(bed.Id, out var record) &&
                        record.IsPermanent, Is.True);
        });
    }

    [Test]
    public void ManualSleepOrder_InAHexWithHerBed_UsesTheBed()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = Colonists(world)[0];
        girl.Needs.Hunger = 0f;
        girl.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(girl.Id, true));
        engine.Step();

        var tile = girl.Tile;
        var centre = StructurePlacement.CenterJunction(world, tile);
        Assume.That(centre, Is.Not.Null);
        if (girl.CurrentJunction is { } standing && standing.Equals(centre.Value))
        {
            var aside = world.Tiles.Items[tile].Junctions
                .Select(id => world.Junctions.Items[id])
                .Where(j => !j.Blocked && j.Tiles.Count == 1 &&
                            HexSpatialMath.Distance(j.WorldPosition, world.Junctions.Items[centre.Value].WorldPosition) > 1.0f)
                .OrderBy(j => j.Id.Value).First();
            PutOnJunction(world, girl, aside);
        }

        foreach (var obj in world.Entities.Objects.Values
                     .Where(o => o.Tile.Equals(tile) && ContentIds.IsBed(o.DefinitionId)).ToList())
        {
            WorldObjectMutations.DespawnObject(world, obj.Id);
        }
        var bed = WorldObjectMutations.SpawnObject(
            world, ContentIds.BedBasic, girl.Fragment, tile, centre.Value);
        bed.Owner = girl.Id;
        new PerceptionSystem().Run(world);

        var admission = ManualCommandExecutor.Apply(
            world, new SelfActionCommand(girl.Id, SelfActionKind.GroundSleep));

        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"GroundSleep отклонён: {admission.Reason}");
        Assert.Multiple(() =>
        {
            Assert.That(girl.Mind.CurrentGoal, Is.EqualTo(GoalType.Sleep),
                "The order keeps its native goal (§138), bed or ground.");
            Assert.That(girl.Plan.Steps[^1].Type, Is.EqualTo(PlanStepType.Interact));
            Assert.That(girl.Plan.Steps[^1].Interaction, Is.EqualTo(InteractionType.Sleep));
            Assert.That(girl.Plan.TargetObjectId, Is.EqualTo(bed.Id),
                "«Лечь спать» next to her own bed means the bed, not the floor beside it.");
        });

        for (var i = 0; i < 120 && girl.Execution.CurrentInteraction != InteractionType.Sleep; i++)
        {
            engine.Step();
        }

        Assert.That(girl.Execution.TargetObject, Is.EqualTo(bed.Id));
    }
}

}
