using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§111.13: кольцо из пяти станций вокруг лежащего тела.</summary>
public sealed class LyingStationTests
{
    private static NPCState LyingGirl(WorldState world, out NPCState[] others)
    {
        var all = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToList();
        var girl = all[0];
        others = all.Skip(1).ToArray();
        foreach (var other in others)
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R);
        }

        girl.Mind.FaintedUntilTick = world.Tick + 1000;
        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);
        Assert.That(girl.IsLyingDown(world.Tick), Is.True);
        return girl;
    }

    // Помощница считается «в сцене», иначе предикат живости её заявку не примет.
    private static void PutInScene(NPCState actor, NPCState body)
    {
        actor.Plan.TargetAgentId = body.Id;
        actor.Execution.Status = ExecutionStatus.InProgress;
    }

    private static float HeadingTowardsHead(NPCState body, int slot)
    {
        var delta = LyingStations.HeadPoint(body) - LyingStations.Point(body, slot);
        var degrees = HexSpatialMath.AngleDegrees(HexSpatialMath.Normalize(delta)) % 360f;
        return degrees < 0f ? degrees + 360f : degrees;
    }

    [Test]
    public void FiveStations_AreDistinctBodyLocalAndAllFaceTheHead()
    {
        var world = TestWorld.CreateWorld();
        var girl = LyingGirl(world, out _);

        var points = Enumerable.Range(0, LyingStations.Count)
            .Select(slot => LyingStations.Point(girl, slot))
            .ToArray();

        for (var a = 0; a < points.Length; a++)
        {
            for (var b = a + 1; b < points.Length; b++)
            {
                Assert.That(HexSpatialMath.Distance(points[a], points[b]),
                    Is.GreaterThan(0.30f),
                    $"Станции {a} и {b} обязаны быть РАЗНЫМИ точками — ради этого всё и делалось.");
            }
        }

        for (var slot = 0; slot < LyingStations.Count; slot++)
        {
            Assert.That(HexSpatialMath.Distance(points[slot], girl.Position),
                Is.LessThanOrEqualTo(InteractionReach.Aid),
                "Станция дальше предела помощи бесполезна: гейт старта её не примет.");

            // ⭐ Курс — на голову с ЛЮБОЙ станции, а не в центр тела.
            Assert.That(LyingStations.Heading(girl, slot),
                Is.EqualTo(HeadingTowardsHead(girl, slot)).Within(0.01f));
        }
    }

    [Test]
    public void FeetStation_IsExactlyTheOldSinglePoint()
    {
        var world = TestWorld.CreateWorld();
        var girl = LyingGirl(world, out _);

        // §111.9 r3 не отменён, а стал частным случаем общей формулы: ноги,
        // центр и голова коллинеарны, поэтому «на голову» здесь = Rotation+180.
        Assert.That(HexSpatialMath.Distance(
                LyingStations.Point(girl, LyingStations.FeetSlot),
                LyingSpot.InteractionFeet(girl)),
            Is.LessThan(0.0001f));
        Assert.That(LyingStations.Heading(girl, LyingStations.FeetSlot),
            Is.EqualTo(HeadingTowardsHead(girl, LyingStations.FeetSlot)).Within(0.01f));
        Assert.That(LyingStations.Heading(girl, LyingStations.FeetSlot),
            Is.EqualTo((girl.RotationDegrees + 180f) % 360f).Within(0.01f));
    }

    [Test]
    public void EveryStationSitsOnAJunctionTheBodyItselfClaims()
    {
        var world = TestWorld.CreateWorld();
        var girl = LyingGirl(world, out _);

        // ⭐ Ловушка, ради которой в планировщике живёт исключение: у тела на
        // ЗЕМЛЕ все пять станций стоят на узлах, которые бронь футпринта уже
        // забрала себе. Без исключения ни одну станцию нельзя было бы
        // забронировать, и фича молча не работала бы.
        for (var slot = 0; slot < LyingStations.Count; slot++)
        {
            var node = SpatialQueries.FindNearestJunction(
                world, LyingStations.Point(girl, slot));
            Assert.That(node, Is.Not.Null);
            Assert.That(girl.ClaimedJunctions.Contains(node.Value), Is.True,
                $"Станция {slot} стоит на узле вне брони тела — исключение в " +
                "планировщике перестало быть нужным, проверь §113.2.");
        }
    }

    [Test]
    public void FirstClaimerTakesTheFeetRegardlessOfWhatSheCameToDo()
    {
        var world = TestWorld.CreateWorld();
        var girl = LyingGirl(world, out var others);
        var looter = others[0];
        var healer = others[1];
        PutInScene(looter, girl);
        PutInScene(healer, girl);

        Assert.That(LyingStations.TryClaim(world, looter, girl, out var looterSlot), Is.True);
        Assert.That(LyingStations.TryClaim(world, healer, girl, out var healerSlot), Is.True);

        Assert.That(looterSlot, Is.EqualTo(LyingStations.FeetSlot));
        Assert.That(healerSlot, Is.Not.EqualTo(looterSlot));

        // Идемпотентность: сменившийся вид помощи (§53.7) не гоняет её вокруг тела.
        Assert.That(LyingStations.TryClaim(world, healer, girl, out var again), Is.True);
        Assert.That(again, Is.EqualTo(healerSlot));
    }

    [Test]
    public void ThreeActorsGetThreeDistinctPositions()
    {
        var world = TestWorld.CreateWorld();
        var girl = LyingGirl(world, out var others);
        Assert.That(others.Length, Is.GreaterThanOrEqualTo(3));

        var crew = others.Take(3).ToArray();
        foreach (var actor in crew)
        {
            PutInScene(actor, girl);
            Assert.That(LyingStations.TryClaim(world, actor, girl, out var slot), Is.True);
            LyingStations.Align(actor, girl, slot);
        }

        for (var a = 0; a < crew.Length; a++)
        {
            for (var b = a + 1; b < crew.Length; b++)
            {
                Assert.That(HexSpatialMath.Distance(crew[a].Position, crew[b].Position),
                    Is.GreaterThan(0.30f),
                    "Лутер, лекарь и собеседница обязаны стоять в разных точках — " +
                    "до §111.13 все трое стояли байт-в-байт в одной.");
            }

            Assert.That(crew[a].RotationDegrees,
                Is.EqualTo(HeadingTowardsHead(girl, crew[a].Execution.LyingStationSlot))
                    .Within(0.01f));
        }
    }

    [Test]
    public void SixthActorIsRefusedInsteadOfStacking()
    {
        var world = TestWorld.CreateWorld();
        var girl = LyingGirl(world, out var others);
        var holders = 0;
        foreach (var actor in others)
        {
            PutInScene(actor, girl);
            if (LyingStations.TryClaim(world, actor, girl, out _))
            {
                holders++;
            }
        }

        Assert.That(holders, Is.LessThanOrEqualTo(LyingStations.Count),
            "Потолок участников — число станций; разделять точку у ног запрещено.");

        // Свободных мест не осталось — отказ, а не «встань там же».
        if (holders == LyingStations.Count)
        {
            var extra = world.Entities.Npcs.Values.First(n =>
                !n.Id.Equals(girl.Id) && LyingStations.Held(world, n, girl) is null);
            PutInScene(extra, girl);
            Assert.That(LyingStations.TryClaim(world, extra, girl, out var refused), Is.False);
            Assert.That(refused, Is.EqualTo(-1));
        }
    }

    [Test]
    public void ReleasedAndStaleClaimsBothFreeTheStation()
    {
        var world = TestWorld.CreateWorld();
        var girl = LyingGirl(world, out var others);
        var helper = others[0];
        PutInScene(helper, girl);
        Assert.That(LyingStations.TryClaim(world, helper, girl, out var slot), Is.True);
        Assert.That(LyingStations.IsFree(world, girl, slot, null), Is.False);

        LyingStations.ReleaseStation(helper);
        Assert.That(LyingStations.IsFree(world, girl, slot, null), Is.True);

        // Страховка: заявка, про которую забыли, не держит место вечно —
        // план помощницы больше не указывает на это тело.
        Assert.That(LyingStations.TryClaim(world, helper, girl, out slot), Is.True);
        helper.Plan.TargetAgentId = null;
        helper.Execution.Status = ExecutionStatus.None;
        Assert.That(LyingStations.IsFree(world, girl, slot, null), Is.True);
        Assert.That(LyingStations.Held(world, helper, girl), Is.Null);
    }

    [Test]
    public void AStationIsRefusedWhenItWouldLandInsideAnotherBody()
    {
        var world = TestWorld.CreateWorld();
        var all = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToList();
        var girl = all[0];
        var neighbour = all[1];
        foreach (var other in all.Skip(2))
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R);
        }

        girl.Mind.FaintedUntilTick = world.Tick + 1000;
        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);

        // Соседка ложится ВПЛОТНУЮ поперёк одной из станций: точка внутри
        // чужого тела не предлагается — это та же мерка, что у укладки §113.2.
        neighbour.Mind.FaintedUntilTick = world.Tick + 1000;
        neighbour.Tile = girl.Tile;
        neighbour.Position = LyingStations.Point(girl, 1);
        neighbour.RotationDegrees = girl.RotationDegrees + 90f;

        Assert.That(LyingStations.IsUsable(world, girl, 1), Is.False);
        Assert.That(LyingStations.IsUsable(world, girl, LyingStations.FeetSlot), Is.True);
    }

    [Test]
    public void ReactiveLimbCareClaimsAStationBeforeInstallingTheRoute()
    {
        var world = TestWorld.CreateWorld();
        var patient = LyingGirl(world, out var others);
        var helper = others[0];
        PrepareSplintAssignment(world, patient, helper);

        new RescueSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.Splint));
            Assert.That(helper.Plan.TargetAgentId, Is.EqualTo(patient.Id));
            Assert.That(LyingStations.Held(world, helper, patient), Is.Not.Null,
                "A route to a lying patient must own its station at assignment time.");
        });
    }

    [Test]
    public void ReactiveLimbCareWithNoUsableStationIsNotAssignedAgain()
    {
        var world = TestWorld.CreateWorld();
        var patient = LyingGirl(world, out var others);
        var helper = others[0];
        PrepareSplintAssignment(world, patient, helper);

        var definition = world.Content.ObjectDefinitions["rock.boulder"];
        definition.SolidRadius = HexSpatialMath.HexRadius * 2f;
        var centre = StructurePlacement.CenterJunction(world, patient.Tile);
        Assert.That(centre, Is.Not.Null);
        WorldObjectMutations.SpawnObject(
            world, "rock.boulder", patient.Fragment, patient.Tile, centre.Value);
        Assert.That(Enumerable.Range(0, LyingStations.Count)
            .All(slot => !LyingStations.IsUsable(world, patient, slot)), Is.True,
            "Fixture must physically close every treatment station.");

        new RescueSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(helper.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Splint));
            Assert.That(helper.Plan.TargetAgentId, Is.Not.EqualTo(patient.Id));
            Assert.That(LyingStations.Held(world, helper, patient), Is.Null);
            Assert.That(patient.Mind.PendingAidFrom, Is.Null,
                "A refused station must not leave an aid promise behind.");
        });
    }

    private static void PrepareSplintAssignment(
        WorldState world, NPCState patient, NPCState helper)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Inventory.Items.Clear();
            npc.Plan.Status = PlanStatus.Completed;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.PendingAidFrom = null;
        }

        patient.Body.Parts[BodyPart.LegR] = 0.05f;
        patient.Body.Condition(BodyPart.LegR).BluntDamage = 0.95f;
        helper.Inventory.Items.Add(ContentIds.Splint);
    }
}

}
