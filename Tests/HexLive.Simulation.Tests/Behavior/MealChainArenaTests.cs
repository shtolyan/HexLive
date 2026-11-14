#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// §54.14A/§54.17: arena proofs that a roasted blank always ends as a meal.
/// Each scenario steps a small hand-authored island with the default system
/// registry until the hungry colonist's hunger actually drops from eating.
/// </summary>
public sealed class MealChainArenaTests
{
    private const int Radius = 5;

    [OneTimeSetUp]
    public void LoadShippedContent() => SimDataFile.Require(RepoPaths.SimData);

    private sealed class Arena
    {
        public SimulationEngine Engine = null!;
        public WorldState World => Engine.World;
        public NPCState Actor = null!;
        public long Seen;
        public readonly List<SimulationEvent> Log = new();

        public void Step(int ticks, Func<bool>? until = null)
        {
            for (var i = 0; i < ticks; i++)
            {
                if (until != null && until()) return;
                Engine.Step();
                foreach (var evt in World.Events.Items)
                {
                    if (evt.Seq <= Seen) continue;
                    Seen = evt.Seq;
                    Log.Add(evt);
                }
            }
        }

        public bool Logged(string type, EntityId? who = null, string? startsWith = null) =>
            Log.Exists(evt => evt.Type == type && (who == null || evt.EntityId == who.Value.Value) &&
                (startsWith == null || evt.Message.StartsWith(startsWith, StringComparison.Ordinal)));

        private static readonly HashSet<string> Noise = new()
        {
            "TickStart", "JunctionReached", "MovementStep", "MovementStepCarry", "EnteredTile",
            "PlanSkipped", "PerceivedObject", "PerceivedAgent", "ExecProgress"
        };

        public string Tail(int n = 40, string? focus = null)
        {
            var useful = Log.Where(evt => !Noise.Contains(evt.Type) &&
                (evt.Type != "GoalScored" || focus != null && evt.Message.StartsWith(focus, StringComparison.Ordinal)) &&
                (focus == null || evt.Type.Contains(focus) || evt.Message.Contains(focus))).ToList();
            return string.Join("\n", useful.Skip(Math.Max(0, useful.Count - n)).Select(evt =>
                $"{evt.Tick} {evt.EntityId} {evt.Type}: {evt.Message}"));
        }

        public WorldObjectState Place(string id, TileCoord tile, int slot = 0) =>
            WorldObjectMutations.SpawnObject(World, id, new FragmentId(1), tile,
                World.Tiles.Items[tile].Junctions[slot]);
    }

    private static Arena Build(int npcs = 1)
    {
        var definition = new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 777001 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 23f },
            FactionHomes = { new FactionHomeBootstrap { StakeCampfireSite = false } }
        };
        var fragment = new FragmentBootstrap { Id = 1 };
        for (var q = -Radius; q <= Radius; q++)
        for (var r = -Radius; r <= Radius; r++)
        {
            if (Math.Abs(q + r) > Radius) continue;
            fragment.Tiles.Add(new TileBootstrap { Q = q, R = r, Elevation = 1, Walkable = true });
        }
        definition.Fragments.Add(fragment);
        for (var i = 0; i < npcs; i++)
        {
            definition.Npcs.Add(new NpcBootstrap
            {
                Id = i + 1, DisplayName = "Arena" + i, ActorMesh = i == 0 ? "Marta" : "Jana",
                UseAuthoredAppearance = true, Faction = Faction.Colony, FragmentId = 1,
                TileQ = i == 0 ? 0 : -1, TileR = 0, Hunger = 0.6f, Thirst = 0f, Energy = 1f,
                Social = 1f, Comfort = 0.8f, Traits = new List<string>()
            });
        }

        var world = new WorldStateFactory().Create(definition);
        foreach (var npc in world.Entities.Npcs.Values) npc.Inventory.Items.Clear();
        var clock = new SimulationClock();
        clock.Resume();
        var engine = new SimulationEngine(world, new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval
        }, clock);
        DefinitionIdTable.Build(world.Content);
        SimulationSystemRegistry.RegisterDefaults(engine);
        SimTrace.EnableAll();
        return new Arena { Engine = engine, Actor = world.Entities.Npcs[new EntityId(1)] };
    }

    private static bool HasCooked(NPCState npc) =>
        npc.Inventory.Items.Exists(item => item.DefinitionId == ContentIds.MeatCooked);

    private static void AssertAte(Arena arena, NPCState npc, float startHunger)
    {
        Assert.That(arena.Logged("MeatEaten", npc.Id), Is.True,
            () => $"No cooked portion was eaten. Hunger={npc.Needs.Hunger:F2} tick={arena.World.Tick} " +
                $"inv=[{string.Join(",", npc.Inventory.Items.Select(i => i.DefinitionId + ":" + i.ResourceAmount))}]\n" +
                arena.Tail());
        Assert.That(npc.Needs.Hunger, Is.LessThan(startHunger));
    }

    // 1. Blank + knife, no surface anywhere: serves on the ground and eats.
    [Test]
    public void NoSurfaceAnywhereServesOnTheGroundAndEats()
    {
        var arena = Build();
        var npc = arena.Actor;
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRoasted));
        arena.Step(900, () => arena.Logged("MeatEaten", npc.Id));
        AssertAte(arena, npc, .6f);
        Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.MeatRoasted), Is.False);
    }

    // 2. Blank + knife with a free stump nearby: the stump is used.
    [Test]
    public void FreeStumpNearbyIsUsedInsteadOfTheGround()
    {
        var arena = Build();
        var npc = arena.Actor;
        var stump = arena.Place(ContentIds.PalmStump, new TileCoord(2, -1));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRoasted));
        var onStump = false;
        var onGround = false;
        for (var i = 0; i < 900 && !arena.Logged("MeatEaten", npc.Id); i++)
        {
            arena.Step(1);
            if (npc.Execution.CurrentInteraction == InteractionType.Serve)
            {
                onStump |= npc.Execution.TargetObject == stump.Id;
                onGround |= npc.Execution.TargetObject == null;
            }
        }
        AssertAte(arena, npc, .6f);
        Assert.That(onStump, Is.True, "Serving must happen at the free stump");
        Assert.That(onGround, Is.False);
    }

    // 2b. Furniture surfaces block their footprint (furniture obstacle rule);
    // she must still reach the rim and serve there, not fall back to the ground.
    [TestCase(ContentIds.Workbench)]
    [TestCase(ContentIds.ModernTable)]
    public void BlockingFurnitureSurfaceIsReachedAndUsed(string surfaceId)
    {
        var arena = Build();
        var npc = arena.Actor;
        var surface = arena.Place(surfaceId, new TileCoord(2, -1));
        // A carried kitchen table is stamped the way manual placement does it.
        if (surfaceId == ContentIds.ModernTable)
            ExecutionSystem.ApplyIndoorFurnitureFootprint(arena.World, surface);
        Assert.That(arena.World.Junctions.Items[surface.Junctions[0]].Blocked, Is.True,
            "the furniture footprint must be blocked in this arena, or the case proves nothing");
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRoasted));
        var onSurface = false;
        var onGround = false;
        for (var i = 0; i < 900 && !arena.Logged("MeatEaten", npc.Id); i++)
        {
            arena.Step(1);
            if (npc.Execution.CurrentInteraction == InteractionType.Serve)
            {
                onSurface |= npc.Execution.TargetObject == surface.Id;
                onGround |= npc.Execution.TargetObject == null;
            }
        }
        AssertAte(arena, npc, .6f);
        Assert.That(onSurface, Is.True, () => $"Serving must happen at the {surfaceId}\n" + arena.Tail());
        Assert.That(onGround, Is.False);
    }

    // 3a. Moderate hunger, knife + axe, no surface, palm nearby: chop, then serve on the new stump.
    [Test]
    public void ModerateHungerChopsAPalmForAStumpThenServesOnIt()
    {
        var arena = Build();
        var npc = arena.Actor;
        var palm = arena.Place(ContentIds.Palm, new TileCoord(2, -1));
        palm.NextProductionTick = int.MaxValue; // no coconuts: only the meat can feed her
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.AxeStone));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRoasted));
        var servedOnStump = false;
        for (var i = 0; i < 2400 && !arena.Logged("MeatEaten", npc.Id); i++)
        {
            arena.Step(1);
            if (npc.Execution.CurrentInteraction == InteractionType.Serve &&
                npc.Execution.TargetObject is { } target &&
                arena.World.Entities.Objects.TryGetValue(target, out var surface) &&
                surface.DefinitionId == ContentIds.PalmStump)
                servedOnStump = true;
        }
        AssertAte(arena, npc, .6f);
        Assert.That(arena.World.Entities.Objects.ContainsKey(palm.Id), Is.False, "The palm must be felled");
        Assert.That(servedOnStump, Is.True, "Serving must happen on the stump the chop left");
    }

    // 3b. Same, but starving: serve on the ground at once, never chop.
    [Test]
    public void StarvingSkipsTheChopAndServesOnTheGround()
    {
        var arena = Build();
        var npc = arena.Actor;
        npc.Needs.Hunger = .9f;
        var palm = arena.Place(ContentIds.Palm, new TileCoord(2, -1));
        palm.NextProductionTick = int.MaxValue; // no coconuts: only the meat can feed her
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.AxeStone));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRoasted));
        arena.Step(900, () => arena.Logged("MeatEaten", npc.Id));
        AssertAte(arena, npc, .9f);
        Assert.That(arena.World.Entities.Objects.ContainsKey(palm.Id), Is.True, "A starving girl must not chop first");
    }

    // 4. Blanks only inside a chest: fetch, serve, eat.
    [Test]
    public void BlanksStoredInAChestAreFetchedServedAndEaten()
    {
        var arena = Build();
        var npc = arena.Actor;
        var chest = arena.Place(ContentIds.StorageChest, new TileCoord(2, -1));
        StorageChest.SetLid(chest, true);
        chest.Contents.Add(new ItemInstance(ContentIds.MeatRoasted));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        arena.Step(1500, () => arena.Logged("MeatEaten", npc.Id));
        AssertAte(arena, npc, .6f);
        Assert.That(chest.Contents.Any(i => i.DefinitionId == ContentIds.MeatRoasted), Is.False);
    }

    // 5. Caretaker holding only a blank feeds an unconscious hungry ward.
    [Test]
    public void CaretakerWithOnlyABlankFeedsAnUnconsciousWard()
    {
        var arena = Build(npcs: 2);
        var helper = arena.Actor;
        var ward = arena.World.Entities.Npcs[new EntityId(2)];
        helper.Needs.Hunger = 0f;
        helper.Needs.Compassion = 0f;
        helper.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        helper.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRoasted));
        // A starving colonist collapses dying (§105) — unconscious and helpless.
        ward.Needs.Hunger = .97f;
        MortalityHelpers.EnterDying(arena.World, ward, DyingCause.Starvation);
        Assert.That(ward.IsUnconscious(arena.World.Tick), Is.True);
        var fed = false;
        for (var i = 0; i < 1500 && !fed && ward.Health > 0f; i++)
        {
            arena.Step(1);
            fed = ward.Needs.Hunger < .9f;
        }
        Assert.That(fed, Is.True, () =>
            $"Ward hunger={ward.Needs.Hunger:F2} helper goal={helper.Mind.CurrentGoal} errand={helper.Mind.AidErrandKind} " +
            $"inv=[{string.Join(",", helper.Inventory.Items)}] seen=[{string.Join(";", helper.Perception.Agents.Select(a => $"{a.Id.Value}:{a.AidKind}:{a.Suffering:F2}:busy={a.IsBusy}:reach={a.IsReachable}:mov={a.IsMoving}"))}] ward={ward.Execution.CurrentInteraction}/{ward.Execution.Status}\n" + arena.Tail(20, "Aid"));
    }

    // 5b. Caretaker with a whole coconut and an opener reaches an edible half for the ward.
    [Test]
    public void CaretakerWithAWholeCoconutAndABladeFeedsAnUnconsciousWard()
    {
        var arena = Build(npcs: 2);
        var helper = arena.Actor;
        var ward = arena.World.Entities.Npcs[new EntityId(2)];
        helper.Needs.Hunger = 0f;
        helper.Needs.Compassion = 0f;
        helper.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        helper.Inventory.Items.Add(new ItemInstance(ContentIds.Coconut));
        // A starving colonist collapses dying (§105) — unconscious and helpless.
        ward.Needs.Hunger = .97f;
        MortalityHelpers.EnterDying(arena.World, ward, DyingCause.Starvation);
        Assert.That(ward.IsUnconscious(arena.World.Tick), Is.True);
        var fed = false;
        for (var i = 0; i < 1500 && !fed && ward.Health > 0f; i++)
        {
            arena.Step(1);
            fed = ward.Needs.Hunger < .9f;
        }
        Assert.That(fed, Is.True, () =>
            $"Ward hunger={ward.Needs.Hunger:F2} helper goal={helper.Mind.CurrentGoal} " +
            $"inv=[{string.Join(",", helper.Inventory.Items)}] seen=[{string.Join(";", helper.Perception.Agents.Select(a => $"{a.Id.Value}:{a.AidKind}:{a.Suffering:F2}:busy={a.IsBusy}:reach={a.IsReachable}:mov={a.IsMoving}"))}] ward={ward.Execution.CurrentInteraction}/{ward.Execution.Status}\n" + arena.Tail(20, "Aid"));
    }

    private static WorldState RoundTrip(WorldState world, int version)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(bytes))
            WorldSaveSerializer.Read(restored, reader);
        return restored;
    }

    // 6a. A v95 partially eaten cooked limb with a small remainder still serves into a bite.
    [Test]
    public void LegacyNibbledLimbLoadsAndServesIntoAtLeastOneBite()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        actor.Inventory.Items.Clear();
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        actor.Inventory.Items.Add(new ItemInstance(HumanMeatCatalog.ArmCooked) { ResourceAmount = 150f });
        var restored = RoundTrip(world, 95);
        var npc = restored.Entities.Npcs[actor.Id];
        var limb = npc.Inventory.Items.Single(i => i.DefinitionId == HumanMeatCatalog.ArmCooked);
        Assert.That(MealServing.PortionGrams(limb), Is.GreaterThanOrEqualTo(FoodMath.BiteGrams));

        var engine = TestWorld.CreateEngine();
        var live = engine.World.Entities.Npcs[actor.Id];
        live.Inventory.Items.Clear();
        live.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        live.Inventory.Items.Add(limb);
        engine.Commands.Enqueue(new SetManualControlCommand(live.Id, true));
        engine.Step();
        var admission = engine.ApplyManualCommand(new ManageInventoryCommand(live.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 1, HumanMeatCatalog.ArmCooked), InventoryAction.Serve));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        for (var i = 0; i < 300 && live.Inventory.Items.Contains(limb); i++) engine.Step();
        Assert.That(live.Inventory.Items.Any(i => ReferenceEquals(i, limb)), Is.False);
        Assert.That(live.Inventory.Items.Where(i => i.DefinitionId == ContentIds.MeatCooked)
            .Sum(i => FoodMath.GramsRemaining(i.DefinitionId, i.ResourceAmount)), Is.GreaterThanOrEqualTo(FoodMath.BiteGrams),
            "Serving must never delete the blank while producing nothing");
    }

    // 6b. A v95 pack slot of eight cooked meat loads back into one slot.
    [Test]
    public void LegacyPackSlotOfEightCookedMeatStaysOneSlot()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        actor.Inventory.Items.Clear();
        for (var i = 0; i < 8; i++) actor.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked));
        Assert.That(actor.Inventory.UsedSlots, Is.EqualTo(1));
        var restored = RoundTrip(world, 95).Entities.Npcs[actor.Id];
        Assert.That(restored.Inventory.Items.Count(i => i.DefinitionId == ContentIds.MeatRoasted), Is.EqualTo(8));
        Assert.That(restored.Inventory.UsedSlots, Is.EqualTo(1));
    }

    [Test]
    public void BlanksStackLikeCookedMeatButAWholeWolfTakesItsOwnSlot()
    {
        Assert.That(InventoryState.StackSizeFor(ContentIds.MeatRoasted), Is.EqualTo(InventoryState.MeatStackSize));
        Assert.That(InventoryState.StackSizeFor(ContentIds.CrabRoasted), Is.EqualTo(InventoryState.MeatStackSize));
        Assert.That(InventoryState.IsStackable(ContentIds.WolfCarcassRaw), Is.False);
        Assert.That(InventoryState.IsStackable(ContentIds.WolfCarcassCooked), Is.False);
        Assert.That(InventoryState.IsStackable(ContentIds.WolfHide), Is.True, "Other resources keep the bulk stack");
    }

    [Test]
    public void StackCellReportsCountAndTotalGrams()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 });
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 120 });
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 });
        var snapshot = HexLive.Simulation.Debug.WorldSnapshotExporter.Export(world);
        var cell = snapshot.Npcs.Single(n => n.Id.Value == npc.Id.Value).InventoryContainers
            .SelectMany(c => c.Slots).Single(s => s.ItemDefinitionId == ContentIds.MeatCooked);
        Assert.That(cell.StackCount, Is.EqualTo(3));
        Assert.That(cell.Grams, Is.EqualTo(720));
    }

    // 7a. Eating from a stack always continues the one started piece.
    [Test]
    public void EatingFromAStackKeepsExactlyOneStartedPiece()
    {
        var arena = Build();
        var npc = arena.Actor;
        npc.Needs.Hunger = .95f;
        var full1 = new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 };
        var full2 = new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 };
        var started = new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 250 };
        npc.Inventory.Items.AddRange(new[] { full1, full2, started });
        var maxStarted = 0;
        for (var i = 0; i < 400 && npc.Inventory.Items.Count(it => it.DefinitionId == ContentIds.MeatCooked) == 3; i++)
        {
            arena.Step(1);
            maxStarted = Math.Max(maxStarted, npc.Inventory.Items.Count(it =>
                it.DefinitionId == ContentIds.MeatCooked && FoodMath.GramsRemaining(it.DefinitionId, it.ResourceAmount) < 300));
        }
        Assert.That(maxStarted, Is.EqualTo(1), "Eating must continue the started piece, never open a new one");
        Assert.That(npc.Inventory.Items.Any(i => ReferenceEquals(i, started)), Is.False, "The started piece is finished first");
        Assert.That(full1.ResourceAmount, Is.EqualTo(300f));
    }

    // 7b. A counted transfer moves the fullest pieces and keeps the started one.
    [Test]
    public void CountedDropMovesTheFullPiecesFirst()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true));
        engine.Step();
        npc.Inventory.Items.Clear();
        var started = new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 150 };
        var full1 = new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 };
        var full2 = new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 };
        npc.Inventory.Items.AddRange(new[] { started, full1, full2 });
        var admission = engine.ApplyManualCommand(new ManageInventoryCommand(npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, ContentIds.MeatCooked), InventoryAction.Drop, 2));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        for (var i = 0; i < 200 && npc.Inventory.Items.Count > 1; i++) engine.Step();
        Assert.That(npc.Inventory.Items, Has.Count.EqualTo(1));
        Assert.That(npc.Inventory.Items.Single(), Is.SameAs(started), "The started piece stays; the full ones move");
    }

    // 7c. Save/load keeps a partially eaten portion's grams.
    [Test]
    public void PartiallyEatenPortionKeepsItsGramsThroughSaveAndLoad()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        actor.Inventory.Items.Clear();
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 });
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 130 });
        var restored = RoundTrip(world, WorldSaveSerializer.BlobVersion).Entities.Npcs[actor.Id];
        Assert.That(restored.Inventory.Items.Select(i => i.ResourceAmount).OrderBy(a => a), Is.EqualTo(new[] { 130f, 300f }));
    }

    // 8. A blank and a ready coconut half: the ready food is eaten first.
    [Test]
    public void ReadyFoodInThePackIsEatenBeforeServingABlank()
    {
        var arena = Build();
        var npc = arena.Actor;
        arena.Place(ContentIds.PalmStump, new TileCoord(2, -1));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRoasted));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));
        var servedFirst = false;
        for (var i = 0; i < 400 && npc.Inventory.Items.Any(it => it.DefinitionId == ContentIds.CoconutOpen); i++)
        {
            arena.Step(1);
            servedFirst |= npc.Execution.CurrentInteraction == InteractionType.Serve;
        }
        Assert.That(npc.Inventory.Items.Any(it => it.DefinitionId == ContentIds.CoconutOpen), Is.False,
            () => "The coconut half was not eaten\n" + arena.Tail());
        Assert.That(servedFirst, Is.False, "She must not walk off to serve while ready food is in her pack");
        Assert.That(npc.Inventory.Items.Any(it => it.DefinitionId == ContentIds.MeatRoasted), Is.True);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void PackMealUsesNearbyStumpOrContinuesWhenItDisappears(bool drink, bool removeSeat)
    {
        var arena = Build();
        var npc = arena.Actor;
        var world = arena.World;
        npc.Needs.Hunger = .15f;
        npc.Needs.Thirst = .15f;
        npc.Needs.Comfort = .35f;
        var stump = arena.Place(ContentIds.PalmStump, new TileCoord(1, 0));
        arena.Engine.ApplyManualCommand(new SetManualControlCommand(npc.Id, true));
        arena.Step(16);
        var supply = drink ? ContentIds.Bottle : ContentIds.MeatCooked;
        npc.Inventory.Items.Add(new ItemInstance(supply)
        {
            ResourceAmount = drink ? 2f : 300f,
            WaterKind = WaterKind.Boiled
        });
        var action = drink ? SelfActionKind.DrinkFromPack : SelfActionKind.EatFromPack;
        Assert.That(arena.Engine.ApplyManualCommand(new SelfActionCommand(npc.Id, action)).Accepted, Is.True);
        arena.Step(80, () => npc.Execution.ConsumptionSeat == stump.Id);
        Assert.That(npc.Execution.ConsumptionSeat, Is.EqualTo(stump.Id), () => arena.Tail());
        var stand = npc.Plan.TargetJunctionId;
        Assert.That(stand, Is.Not.Null);
        Assert.That(stand, Is.Not.EqualTo(stump.Junctions[0]));
        if (removeSeat) WorldObjectMutations.DespawnObject(world, stump.Id);

        var verb = drink ? InteractionType.Drink : InteractionType.Eat;
        arena.Step(160, () => npc.Execution.CurrentInteraction == verb);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(verb), () => arena.Tail());
        var comfort = npc.Needs.Comfort;
        var view = HexLive.Simulation.Debug.WorldSnapshotExporter.Export(world).Npcs.Single(n => n.Id == npc.Id);
        if (removeSeat)
        {
            Assert.That(view.TargetObjectId, Is.Null);
            Assert.That(npc.Plan.TargetJunctionId, Is.Null);
        }
        else
        {
            Assert.That(npc.CurrentJunction, Is.EqualTo(stand));
            Assert.That(view.TargetObjectId, Is.EqualTo(stump.Id.Value));
            arena.Step(5);
            Assert.That(stump.CurrentUser, Is.EqualTo(npc.Id));
            Assert.That(npc.Needs.Comfort, Is.GreaterThan(comfort));
        }
        arena.Step(100, () => npc.Execution.CurrentInteraction != verb);
        Assert.That(npc.Execution.ConsumptionSeat, Is.Null);
        Assert.That(world.Reservations.Junctions.TryGetValue(stand!.Value, out var claim) && claim.Owner == npc.Id,
            Is.False);
        if (removeSeat) Assert.That(world.Entities.Objects.ContainsKey(stump.Id), Is.False);
        else Assert.That(stump.CurrentUser, Is.Null);
    }

    [TestCase(AidKind.Hydrate, ContentIds.PalmStump)]
    [TestCase(AidKind.Feed, "furniture.modern_chair")]
    public void AidKeepsTheRecipientsFurnitureSeat(AidKind kind, string seatId)
    {
        var arena = Build(2);
        var helper = arena.Actor;
        var patient = arena.World.Entities.Npcs[new EntityId(2)];
        var seat = arena.Place(seatId, new TileCoord(-1, 1));
        arena.Engine.ApplyManualCommand(new SetManualControlCommand(helper.Id, true));
        arena.Engine.ApplyManualCommand(new SetManualControlCommand(patient.Id, true));
        patient.Needs.Hunger = .75f;
        patient.Needs.Thirst = .7f;
        arena.Step(2);
        var sit = arena.Engine.ApplyManualCommand(new InteractCommand(patient.Id, seat.Id, InteractionType.Sit));
        Assert.That(sit.Accepted, Is.True, sit.Reason);
        arena.Step(180, () => patient.Execution.CurrentInteraction == InteractionType.Sit);
        Assert.That(FurnitureSeats.CurrentMealSeat(arena.World, patient), Is.SameAs(seat), () => arena.Tail());

        var drinking = kind == AidKind.Hydrate;
        var portion = new ItemInstance(drinking ? ContentIds.Bottle : ContentIds.MeatCooked)
            { ResourceAmount = drinking ? 2 : 300, WaterKind = WaterKind.Boiled };
        helper.Inventory.Items.Add(portion);
        var originalFacing = patient.RotationDegrees;
        var admission = arena.Engine.ApplyManualCommand(new AidPersonCommand(helper.Id, patient.Id, kind));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var verb = drinking ? InteractionType.HydrateOther : InteractionType.FeedOther;
        arena.Step(150, () => helper.Execution.CurrentInteraction == verb);
        Assert.That(helper.Execution.CurrentInteraction, Is.EqualTo(verb), () => arena.Tail());
        Assert.That(patient.RotationDegrees, Is.EqualTo(originalFacing).Within(.001f));
        Assert.That(FurnitureSeats.CurrentMealSeat(arena.World, patient), Is.SameAs(seat));

        var before = drinking ? patient.Needs.Thirst : patient.Needs.Hunger;
        arena.Step(50, () => helper.Execution.CurrentInteraction != verb);
        Assert.That(drinking ? patient.Needs.Thirst : patient.Needs.Hunger, Is.LessThan(before - .1f));
        Assert.That(portion.ResourceAmount, Is.EqualTo(drinking ? 1f : 200f).Within(.01f));
        Assert.That(FurnitureSeats.CurrentMealSeat(arena.World, patient), Is.SameAs(seat));
    }

    // 9. A manual Serve order with no surface is accepted and completes on the ground.
    [Test]
    public void ManualServeOrderWithoutASurfaceCompletesOnTheGround()
    {
        var arena = Build();
        var npc = arena.Actor;
        npc.Needs.Hunger = 0f;
        arena.Engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true));
        arena.Step(1);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        var blank = new ItemInstance(ContentIds.MeatRoasted);
        npc.Inventory.Items.Add(blank);
        var admission = arena.Engine.ApplyManualCommand(new ManageInventoryCommand(npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 1, ContentIds.MeatRoasted), InventoryAction.Serve));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var flagged = false;
        for (var i = 0; i < 300 && !HasCooked(npc); i++)
        {
            arena.Step(1);
            if (npc.Execution.CurrentInteraction == InteractionType.Serve)
            {
                var snapshot = HexLive.Simulation.Debug.WorldSnapshotExporter.Export(arena.World);
                flagged |= snapshot.Npcs.Single(n => n.Id.Value == npc.Id.Value).ServingOnGround;
            }
        }
        Assert.That(HasCooked(npc), Is.True, () => arena.Tail());
        Assert.That(flagged, Is.True, "The view must be told she serves on the ground (plant-pose placeholder)");
        Assert.That(npc.Inventory.Items.Any(i => ReferenceEquals(i, blank)), Is.False);
    }
}
