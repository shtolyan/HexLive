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

/// <summary>Regression coverage for auction/planner and construction queue
/// invariants found by the multi-seed soak audit.</summary>
public sealed class PlanningAvailabilityTests
{
    [Test]
    public void OccupiedCoolingCapacityDefersInsteadOfFailingEveryFortyTicks()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0);
        npc.CurrentJunction = anchor.Id;
        npc.Tile = anchor.Tiles[0];
        npc.Position = anchor.WorldPosition;
        foreach (var junction in world.Junctions.Items.Values)
        {
            junction.Blocked = true;
        }

        npc.Mind.CurrentGoal = GoalType.CoolOff;
        npc.Plan.Goal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;

        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(npc.Mind.Cooldowns.Any(c =>
                c.Goal == GoalType.CoolOff &&
                c.EndTick - world.Tick >=
                    Spec49.CoolOffDwellTicks * (Spec49.CoolOffMaxRearms + 1)), Is.True);
            Assert.That(world.Events.Items.Any(e => e.Type == "CoolOffDeferred"), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.Type == "PlanFailed" && e.Message.Contains("Goal=CoolOff")), Is.False);
        });
    }

    [Test]
    public void ExploreIsUnavailableWithoutAPlannerCandidate()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();

        npc.CurrentJunction = null;

        Assert.That(PlanningSystem.HasExploreCandidate(world, npc), Is.False,
            "The auction must not bid Explore when the planner has no start junction.");
    }

    [Test]
    public void ExploreNeverPlansTheJunctionAlreadyUnderfoot()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var currentId = npc.CurrentJunction ?? world.Junctions.Items.Values
            .First(j => !j.Blocked && j.Tiles.Count > 0).Id;
        npc.CurrentJunction = currentId;
        var current = world.Junctions.Items[currentId];
        foreach (var junction in world.Junctions.Items.Values)
        {
            junction.Blocked = !junction.Id.Equals(currentId);
        }

        // Reproduce the soak topology quirk: one large/shared junction can
        // report a representative tile several hexes from the actor while it
        // is still literally her current graph node.
        current.Tiles.Clear();
        current.Tiles.Add(new TileCoord(npc.Tile.Q + 3, npc.Tile.R));

        Assert.That(PlanningSystem.HasExploreCandidate(world, npc), Is.False,
            "Explore must mean movement, not a plan to the current junction.");
    }

    [Test]
    public void CriticalExploreMayCrossAStaleDangerMemory()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        world.Tick = SimBalance.BuildDangerFreshTicks + 1;
        npc.Memory.Dangers.Clear();
        npc.Perception.Hostiles.Clear();
        world.Mobs.Clear();
        var from = npc.CurrentJunction ?? world.Junctions.Items.Values
            .First(j => !j.Blocked && j.Tiles.Count > 0).Id;
        npc.CurrentJunction = from;
        var target = world.Junctions.Items.Values.First(j =>
            !j.Blocked && !j.Id.Equals(from) &&
            Connectivity.Reachable(world, from, j.Id, npc.Body.CanJump));
        // The compact unit fixture has no naturally 3-hex-distant point. The
        // explore predicate intentionally reads this representative tile,
        // while reachability remains the real graph edge selected above.
        target.Tiles.Clear();
        target.Tiles.Add(new TileCoord(npc.Tile.Q + 3, npc.Tile.R));
        npc.Memory.Dangers.Add(new HexLive.Simulation.Memory.DangerMemory
        {
            Tile = target.Tiles[0],
            Tick = 0
        });

        Assert.That(PlanningSystem.IsExploreCandidate(world, npc, target), Is.False,
            "Ordinary sightseeing still respects remembered danger.");

        npc.Mind.IsDehydrated = true;
        Assert.That(PlanningSystem.IsExploreCandidate(world, npc, target), Is.True,
            "A stale fear must not make certain dehydration preferable to discovery.");
    }

    [Test]
    public void EmergencyTraversalNeedsCriticalNeedAndTwoSupportingLegs()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Body.Parts[BodyPart.LegL] = 0.68f;
        npc.Body.Parts[BodyPart.LegR] = 0.79f;

        Assert.That(npc.Body.CanJump, Is.False,
            "Fixture must be below the ordinary two-leg jump threshold.");
        Assert.That(PlanningSystem.CanUseCriticalTraversal(npc), Is.False,
            "Ordinary travel must not receive the survival exception.");

        npc.Mind.IsDehydrated = true;
        Assert.That(PlanningSystem.CanUseCriticalTraversal(npc), Is.True,
            "A critical, still crawl-capable body may scramble off the trap.");

        npc.Body.Parts[BodyPart.LegL] =
            BodyState.CrawlLegFunctionThreshold - 0.01f;
        Assert.That(PlanningSystem.CanUseCriticalTraversal(npc), Is.False,
            "A collapsed or missing support leg remains physically unable to jump.");
    }

    [Test]
    public void GatherStoneRequiresThePickUpOfferUsedByThePlanner()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Perception.Objects.Clear();
        var stone = new PerceivedObject
        {
            Id = new ObjectId(int.MaxValue - 70),
            DefinitionId = ContentIds.Stone,
            IsReachable = true,
            Distance = 1f
        };
        npc.Perception.Objects.Add(stone);

        Assert.That(PlanningSystem.HasObjectCandidateForGoal(
            world, npc, GoalType.GatherStone), Is.False);

        stone.AvailableInteractions.Add(InteractionType.PickUp);
        Assert.That(PlanningSystem.HasObjectCandidateForGoal(
            world, npc, GoalType.GatherStone), Is.True);
    }

    [Test]
    public void ObjectBidRequiresAFreeReachableInteractionRim()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var current = npc.CurrentJunction;
        var anchor = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0 &&
            (current is null || !j.Id.Equals(current.Value)));
        var stone = WorldObjectMutations.SpawnObject(
            world, ContentIds.Stone, npc.Fragment, anchor.Tiles[0], anchor.Id);
        var rim = new System.Collections.Generic.List<JunctionId>();
        SpatialQueries.CollectStandableAround(
            world, anchor.Id, rim, 96, SpatialQueries.BesideReach(0f), stone,
            SpatialQueries.RimPurpose.Reach);
        Assert.That(rim, Is.Not.Empty, "Fixture needs a real interaction rim.");

        npc.Perception.Objects.Clear();
        var seen = Seen(stone);
        seen.AvailableInteractions.Add(InteractionType.PickUp);
        npc.Perception.Objects.Add(seen);

        foreach (var junction in rim)
        {
            world.Occupancy.JunctionOwner[junction] = new EntityId(int.MaxValue - 90);
        }

        Assert.That(PlanningSystem.HasObjectCandidateForGoal(
            world, npc, GoalType.GatherStone), Is.False,
            "Decision must not bid for an object which the planner cannot stand beside.");

        world.Occupancy.JunctionOwner[rim[0]] = null;
        Assert.That(PlanningSystem.HasObjectCandidateForGoal(
            world, npc, GoalType.GatherStone), Is.True,
            "Releasing one real rim point must make the same object available.");
    }

    [Test]
    public void DryClothesRequiresTheSameHangOfferAsItsPlanner()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var anchor = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0 &&
            (npc.CurrentJunction is null || !j.Id.Equals(npc.CurrentJunction.Value)));
        var rack = WorldObjectMutations.SpawnObject(
            world, ContentIds.DryingRack, npc.Fragment, anchor.Tiles[0], anchor.Id);
        var seen = Seen(rack);
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(seen);

        Assert.That(PlanningSystem.HasDryingDestination(world, npc), Is.False,
            "A rack tag alone must not open a goal the planner cannot execute.");

        seen.AvailableInteractions.Add(InteractionType.Hang);
        Assert.That(PlanningSystem.HasDryingDestination(world, npc), Is.True,
            "Adding the real Hang offer makes the same reachable rack usable.");
    }

    [Test]
    public void FireDryingRequiresAnExactPathStart()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var anchor = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0);
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, anchor.Tiles[0], anchor.Id);
        fire.ResourceAmount = 1f;
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(Seen(fire));
        npc.CurrentJunction = null;

        Assert.That(PlanningSystem.HasDryingDestination(world, npc), Is.False,
            "Broad perception reachability cannot replace the pathfinder's start node.");
    }

    [Test]
    public void DefendRequiresAnExactPathStart()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var attackerJunction = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0);
        npc.CurrentJunction = null;

        Assert.That(PlanningSystem.HasReachableDefendApproach(
            world, npc, attackerJunction.Id), Is.False,
            "A nearby attacker cannot resurrect Defend without a pathfinder start node.");
        Assert.That(CombatHelpSystem.CanReachAttacker(
            world, npc, null, npc.Id), Is.False,
            "The event-side gate must use the same exact availability predicate.");
    }

    [Test]
    public void DefendEventHonoursPathFailureCooldown()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var helper = npcs[0];
        var attacker = npcs[1];
        PlanningSystem.SetGoalCooldown(world, helper, GoalType.Defend);

        Assert.That(CombatHelpSystem.CanReachAttacker(
            world, helper, null, attacker.Id), Is.False,
            "A medium-pass help event must not immediately revive failed Defend.");
    }

    [Test]
    public void CoconutEmergencyDoesNotMistakeSawForBlade()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        Assert.That(junctions, Has.Length.EqualTo(2));
        var saw = WorldObjectMutations.SpawnObject(
            world, GearCatalog.Saw, npc.Fragment, junctions[0].Tiles[0], junctions[0].Id);
        var knife = WorldObjectMutations.SpawnObject(
            world, ContentIds.Knife, npc.Fragment, junctions[1].Tiles[0], junctions[1].Id);
        npc.Perception.Objects.Clear();
        var seenSaw = Seen(saw);
        seenSaw.AvailableInteractions.Add(InteractionType.PickUp);
        npc.Perception.Objects.Add(seenSaw);

        Assert.That(PlanningSystem.HasObjectCandidateForGoal(
            world, npc, GoalType.GatherTools), Is.True,
            "The saw remains a useful ordinary tool upgrade.");
        Assert.That(PlanningSystem.HasToolCandidateWithCapability(
            world, npc, GearCapability.Cut), Is.False,
            "A saw must not carry the coconut survival modifier.");

        var seenKnife = Seen(knife);
        seenKnife.AvailableInteractions.Add(InteractionType.PickUp);
        npc.Perception.Objects.Add(seenKnife);
        Assert.That(PlanningSystem.HasToolCandidateWithCapability(
            world, npc, GearCapability.Cut), Is.True,
            "The exact reachable knife opens the emergency tool chain.");
    }

    [Test]
    public void ProneNpcDoesNotBidGetWaterOnAProducerSheCannotHarvest()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var palm = world.Entities.Objects.Values.First(o =>
            world.Content.ObjectDefinitions.TryGetValue(o.DefinitionId, out var definition) &&
            definition.Produce?.ProducedDefinitionId == ContentIds.Coconut);
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(ContentIds.Knife);
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(Seen(palm));
        npc.Needs.Thirst = 0.9f;
        npc.Body.Sever(BodyPart.LegL);

        new DecisionSystem().Run(world);

        Assert.That(npc.Mind.LastScores.Single(s => s.Goal == GoalType.GetWater).FinalScore,
            Is.Zero,
            "Ползущая может подобрать кокос с земли, но не должна бесконечно идти собирать пальму стоя.");
    }

    [Test]
    public void EngagedHumanDefenderHoldsOneActivePlan()
    {
        var world = TestWorld.CreateWorld();
        var helper = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var attacker = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        var from = world.Junctions.Items.Values.First(j => j.Neighbors.Count > 0);
        var to = world.Junctions.Items[from.Neighbors[0]];
        helper.CurrentJunction = from.Id;
        helper.Tile = from.Tiles[0];
        helper.Position = from.WorldPosition;
        attacker.CurrentJunction = to.Id;
        attacker.Tile = to.Tiles[0];
        attacker.Position = to.WorldPosition;
        helper.Mind.CurrentGoal = GoalType.Defend;
        helper.Mind.CombatAssistAttackerNpcId = attacker.Id;
        helper.Mind.GoalLock = new GoalLock
        {
            Goal = GoalType.Defend,
            StartTick = world.Tick,
            EndTick = world.Tick + 40
        };
        helper.Plan.Status = PlanStatus.Invalid;

        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(helper.Plan.Steps, Has.Count.EqualTo(1));
            Assert.That(helper.Plan.Steps[0].Type, Is.EqualTo(PlanStepType.Wait));
            Assert.That(helper.Mind.CombatOpponentNpcId, Is.EqualTo(attacker.Id));
        });
    }

    [Test]
    public void DirectObjectWorkPointSkipsTheOccupiedLogAndSelectsAFreeOne()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var worker = npcs[0];
        var blocker = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                (worker.CurrentJunction is null ||
                 !j.Id.Equals(worker.CurrentJunction.Value)))
            .Take(2)
            .ToArray();
        Assert.That(junctions, Has.Length.EqualTo(2));

        blocker.CurrentJunction = junctions[0].Id;
        blocker.Tile = junctions[0].Tiles[0];
        blocker.Position = junctions[0].WorldPosition;
        SpatialMutations.OccupyJunction(world, junctions[0].Id, blocker.Id);
        // Reproduce the soak mismatch: CurrentJunction is authoritative to
        // MovementSystem even when the coarse occupancy table has gone stale.
        world.Occupancy.JunctionOwner[junctions[0].Id] = null;
        var occupiedLog = WorldObjectMutations.SpawnObject(
            world, ContentIds.Log, worker.Fragment,
            junctions[0].Tiles[0], junctions[0].Id);
        var freeLog = WorldObjectMutations.SpawnObject(
            world, ContentIds.Log, worker.Fragment,
            junctions[1].Tiles[0], junctions[1].Id);

        worker.Perception.Objects.Clear();
        var occupiedSeen = Seen(occupiedLog);
        occupiedSeen.Distance = 0.5f;
        occupiedSeen.AvailableInteractions.Add(InteractionType.Process);
        var freeSeen = Seen(freeLog);
        freeSeen.Distance = 2f;
        freeSeen.AvailableInteractions.Add(InteractionType.Process);
        worker.Perception.Objects.Add(occupiedSeen);
        worker.Perception.Objects.Add(freeSeen);

        Assert.That(PlanningSystem.HasObjectCandidateForGoal(
            world, worker, GoalType.SplitLog), Is.True);
        worker.Mind.CurrentGoal = GoalType.SplitLog;
        worker.Plan.Goal = GoalType.SplitLog;
        worker.Plan.Status = PlanStatus.Invalid;

        new PlanningSystem().Run(world);

        Assert.That(worker.Plan.TargetObjectId, Is.EqualTo(freeLog.Id),
            "The exact direct anchor is part of target availability.");

        worker.Perception.Objects.Remove(freeSeen);
        Assert.That(PlanningSystem.HasObjectCandidateForGoal(
            world, worker, GoalType.SplitLog), Is.False,
            "An occupied direct work point must not be promised by the auction.");
    }

    [Test]
    public void FirstWaterCollectorPrecedesComfortConstruction()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();

        var bed = Site(world, npc, junctions[0].Id, junctions[0].Tiles[0],
            ContentIds.BedBasic);
        var collector = Site(world, npc, junctions[1].Id, junctions[1].Tiles[0],
            ContentIds.WaterCollector);

        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(Seen(bed));
        npc.Perception.Objects.Add(Seen(collector));

        Assert.That(DecisionSystem.FindBuildSite(npc, world), Is.SameAs(collector),
            "Renewable water must not remain queued behind a personal bed.");
    }

    [Test]
    public void ArchitecturalShelterPrecedesWaterCollectors_Bug188()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();

        var collector = Site(world, npc, junctions[0].Id, junctions[0].Tiles[0],
            ContentIds.WaterCollector);
        collector.BillSticks = 8;
        var hut = Site(world, npc, junctions[1].Id, junctions[1].Tiles[0],
            ContentIds.HutPlan);
        hut.BillRope = 12;

        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(Seen(collector));
        npc.Perception.Objects.Add(Seen(hut));

        Assert.That(DecisionSystem.FindBuildSite(npc, world), Is.SameAs(hut),
            "A staked house is shelter infrastructure. If water collectors sit ahead of it, " +
            "the house's rope demand never reaches CraftRope and the visible walls stall.");
    }

    [Test]
    public void RopeCraftingReadsAlliedShelterDemandBeyondLocalPerception_Bug188()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(3)
            .ToArray();
        Assert.That(junctions, Has.Length.EqualTo(3));

        npc.Needs.Hunger = 0.1f;
        npc.Needs.Thirst = 0.1f;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;
        npc.Execution.Status = ExecutionStatus.None;

        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, junctions[0].Tiles[0],
            junctions[0].Id);
        var fiber = WorldObjectMutations.SpawnObject(
            world, ContentIds.Fiber, npc.Fragment, junctions[1].Tiles[0],
            junctions[1].Id);
        var hut = Site(world, npc, junctions[2].Id, junctions[2].Tiles[0],
            ContentIds.HutPlan);
        hut.BillRope = 1;

        npc.Perception.Objects.Clear();
        var seenFire = Seen(fire);
        seenFire.AvailableInteractions.Add(InteractionType.Craft);
        npc.Perception.Objects.Add(seenFire);
        var seenFiber = Seen(fiber);
        seenFiber.AvailableInteractions.Add(InteractionType.PickUp);
        npc.Perception.Objects.Add(seenFiber);

        new DecisionSystem().Run(world);

        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.CraftRope),
            "Rope production must answer allied shelter demand even when this NPC " +
            "sees the fiber and campfire but not the distant house site.");
    }

    [Test]
    public void BuildFurniturePlannerTargetsTheExactQueuedWaterCollector()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();

        var dryingRack = Site(world, npc, junctions[0].Id, junctions[0].Tiles[0],
            ContentIds.DryingRack);
        var collector = Site(world, npc, junctions[1].Id, junctions[1].Tiles[0],
            ContentIds.WaterCollector);
        var rackSeen = Seen(dryingRack);
        rackSeen.AvailableInteractions.Add(InteractionType.Build);
        var collectorSeen = Seen(collector);
        collectorSeen.AvailableInteractions.Add(InteractionType.Build);
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(rackSeen); // deliberately offered first
        npc.Perception.Objects.Add(collectorSeen);
        npc.Mind.CurrentGoal = GoalType.BuildFurniture;
        npc.Plan.Goal = GoalType.BuildFurniture;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Execution.Status = ExecutionStatus.None;

        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(collector.Id),
                "The generic nearest-object pass must not undo the construction queue.");
        });
    }

    [Test]
    public void WaterCollectorUsesTheSameHandBuiltRuleInDecisionAndExecution()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var junction = world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count > 0);
        var collector = Site(world, npc, junction.Id, junction.Tiles[0],
            ContentIds.WaterCollector);

        Assert.That(BuildSiteMath.NeedsHammer(world, collector), Is.False,
            "The authored HandBuilt tag must let a stocked collector be raised by hand.");
    }

    [Test]
    public void WaterCollectorAcceptsFutureStageBundlesWithoutReorderingStages()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var junction = world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count > 0);
        var collector = Site(world, npc, junction.Id, junction.Tiles[0],
            ContentIds.WaterCollector);
        collector.BillSticks = 8;
        collector.BillStones = 5;
        collector.BillRope = 8;
        collector.BillLeaves = 11;

        Assert.Multiple(() =>
        {
            Assert.That(BuildSiteMath.Remaining(
                collector, BuildSiteMath.MaterialSticks), Is.EqualTo(4),
                "The visual/current stage remains the four uprights.");
            Assert.That(BuildSiteMath.Needs(
                collector, BuildSiteMath.MaterialRope), Is.False,
                "Rope is not the current visual stage yet.");
            Assert.That(BuildSiteMath.AcceptsDelivery(
                collector, BuildSiteMath.MaterialRope), Is.True,
                "Prepared lashings may be stored before their visual stage opens.");
        });
    }

    [Test]
    public void GroundCraftPileSkipsAnActorOccupiedWorkPoint()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var crafter = npcs[0];
        var blocker = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                (crafter.CurrentJunction is null ||
                 !j.Id.Equals(crafter.CurrentJunction.Value)))
            .Take(2)
            .ToArray();
        Assert.That(junctions, Has.Length.EqualTo(2));

        blocker.CurrentJunction = junctions[0].Id;
        blocker.Tile = junctions[0].Tiles[0];
        blocker.Position = junctions[0].WorldPosition;
        var occupiedFiber = WorldObjectMutations.SpawnObject(
            world, ContentIds.Fiber, crafter.Fragment,
            junctions[0].Tiles[0], junctions[0].Id);
        var freeFiber = WorldObjectMutations.SpawnObject(
            world, ContentIds.Fiber, crafter.Fragment,
            junctions[1].Tiles[0], junctions[1].Id);

        crafter.Perception.Objects.Clear();
        var occupiedSeen = Seen(occupiedFiber);
        occupiedSeen.Distance = 0.5f;
        var freeSeen = Seen(freeFiber);
        freeSeen.Distance = 2f;
        crafter.Perception.Objects.Add(occupiedSeen);
        crafter.Perception.Objects.Add(freeSeen);

        Assert.That(DecisionSystem.FindGroundInputPile(
            crafter, world, ContentIds.Fiber, 1)?.Id, Is.EqualTo(freeFiber.Id),
            "A farther free work point must beat a nearer occupied destination.");

        crafter.Perception.Objects.Remove(freeSeen);
        Assert.That(DecisionSystem.FindGroundInputPile(
            crafter, world, ContentIds.Fiber, 1), Is.Null,
            "The auction must not promise an in-place craft at an occupied pile.");
    }

    [Test]
    public void CompletedIdlePlanIsStableUntilTheAuctionChangesTheGoal()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Mind.CurrentGoal = GoalType.Idle;
        npc.Plan.Goal = GoalType.Idle;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.CurrentStepIndex = 23;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Wait });

        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(npc.Plan.CurrentStepIndex, Is.EqualTo(23));
            Assert.That(npc.Plan.Steps, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void SocializeIsUnavailableWhenEveryArmLengthApproachIsOccupied()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var initiator = npcs[0];
        var partner = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        initiator.CurrentJunction = junctions[0].Id;
        initiator.Tile = junctions[0].Tiles[0];
        initiator.Position = junctions[0].WorldPosition;
        partner.CurrentJunction = junctions[1].Id;
        partner.Tile = junctions[1].Tiles[0];
        partner.Position = junctions[1].WorldPosition;
        foreach (var junction in world.Junctions.Items.Keys)
        {
            world.Occupancy.JunctionOwner[junction] =
                new EntityId(int.MaxValue - 610);
        }

        initiator.Perception.Agents.Clear();
        initiator.Perception.Agents.Add(new PerceivedAgent
        {
            Id = partner.Id,
            Tile = partner.Tile,
            Junction = partner.CurrentJunction,
            Distance = 1f,
            CanSee = true,
            IsReachable = true
        });
        initiator.Needs.Social = 0f;

        Assert.That(PlanningSystem.HasAvailableArmsLengthApproach(
            world, initiator, partner, partner.CurrentJunction.Value), Is.False);
        new DecisionSystem().Run(world);
        Assert.That(initiator.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Socialize));
    }

    private static WorldObjectState Site(
        WorldState world, NPCState owner, JunctionId junction, TileCoord tile,
        string product)
    {
        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, owner.Fragment, tile, junction);
        site.BuildProduct = product;
        site.Owner = owner.Id;
        return site;
    }

    private static PerceivedObject Seen(WorldObjectState site) => new()
    {
        Id = site.Id,
        DefinitionId = site.DefinitionId,
        Tile = site.Tile,
        IsReachable = true,
        Distance = 1f
    };
}

}
