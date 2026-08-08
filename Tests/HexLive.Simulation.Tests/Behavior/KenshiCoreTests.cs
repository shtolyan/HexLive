using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>Focused executable contract for spec §118.</summary>
[NonParallelizable]
public sealed class KenshiCoreTests
{
    private bool _enabled;

    [SetUp]
    public void EnableKenshiCore()
    {
        _enabled = Spec118.Enabled;
        Spec118.Enabled = true;
    }

    [TearDown]
    public void RestoreFeatureFlag() => Spec118.Enabled = _enabled;

    [Test]
    public void Fists_CreateBluntDamageWithoutBloodOrOpenWound()
    {
        var world = TestWorld.CreateWorld();
        var target = world.Entities.Npcs.Values.First();
        var blood = target.Needs.Blood;

        var result = BodyDamageResolver.ApplyLanded(
            world, target, BodyPart.Torso, 0.20f,
            DamageProfile.ForGear(GearCatalog.Fist), "test fists");

        Assert.Multiple(() =>
        {
            Assert.That(result.Cut, Is.Zero.Within(0.0001f));
            Assert.That(result.Blunt, Is.EqualTo(0.20f).Within(0.0001f));
            Assert.That(target.Wounds, Is.Empty);
            Assert.That(target.Needs.Blood, Is.EqualTo(blood).Within(0.0001f));
            Assert.That(target.Body.Condition(BodyPart.Torso).BluntDamage,
                Is.EqualTo(0.20f).Within(0.0001f));
        });
    }

    [Test]
    public void WoodenProstheticBoards_AreObtainableFromLogProcessing()
    {
        var world = TestWorld.CreateWorld();
        var processes = world.Content.ObjectDefinitions[ContentIds.Log].Interactions
            .Where(i => i.Type == InteractionType.Process).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(processes.SelectMany(i => i.Yields).Any(y =>
                y.DefinitionId == ContentIds.Stick && y.Count == SimBalance.LogSplitYield),
                Is.True);
            Assert.That(processes.Any(i =>
                i.RequiredCapabilities.Contains(GearCapability.Saw) &&
                i.Yields.Any(y =>
                    y.DefinitionId == ContentIds.Board && y.Count == 2 && y.Scatter)),
                Is.True);
        });
    }

    [Test]
    public void RepeatedHits_FocusTheZoneAndDecayOneStepAfter480Ticks()
    {
        var world = TestWorld.CreateWorld();
        var target = world.Entities.Npcs.Values.First();
        var profile = DamageProfile.ForGear(GearCatalog.Fist);

        BodyDamageResolver.ApplyLanded(
            world, target, BodyPart.ArmL, 0.01f, profile, "focus one");
        BodyDamageResolver.ApplyLanded(
            world, target, BodyPart.ArmL, 0.01f, profile, "focus two");
        Assert.That(target.Body.Condition(BodyPart.ArmL).HitBias,
            Is.EqualTo(3f).Within(0.0001f));

        world.Tick = 480;
        BodyDamageResolver.DecayAllHitBias(world, target);
        Assert.That(target.Body.Condition(BodyPart.ArmL).HitBias,
            Is.EqualTo(2f).Within(0.0001f));
    }

    [Test]
    public void Machete_SplitsDamageAndImmediatelyDrainsProfiledBlood()
    {
        var world = TestWorld.CreateWorld();
        var target = world.Entities.Npcs.Values.First();
        target.Wounds.Clear();
        target.Needs.Blood = 1f;

        var result = BodyDamageResolver.ApplyLanded(
            world, target, BodyPart.ArmL, 0.20f,
            DamageProfile.ForGear(GearCatalog.Machete), "test machete");

        var expectedCut = 0.20f * 0.85f;
        var expectedBlood = 1f - expectedCut * 1.20f * 0.20f;
        Assert.Multiple(() =>
        {
            Assert.That(result.Cut, Is.EqualTo(expectedCut).Within(0.0001f));
            Assert.That(result.Blunt, Is.EqualTo(0.03f).Within(0.0001f));
            Assert.That(target.Wounds, Has.Count.EqualTo(1));
            Assert.That(target.Wounds[0].BleedFactor, Is.EqualTo(1.20f).Within(0.0001f));
            Assert.That(target.Needs.Blood, Is.EqualTo(expectedBlood).Within(0.0001f));
        });
    }

    [Test]
    public void Bandage_StabilizesWorstWoundWithoutRestoringHpOrBlood()
    {
        var world = TestWorld.CreateWorld();
        var all = world.Entities.Npcs.Values.ToList();
        var patient = all[0];
        var healer = all[1];
        patient.Wounds.Clear();
        BodyDamageResolver.ApplyLanded(
            world, patient, BodyPart.LegL, 0.30f,
            DamageProfile.ForGear(GearCatalog.Machete), "test cut");
        var hp = patient.Body.Parts[BodyPart.LegL];
        var blood = patient.Needs.Blood;

        ExecutionSystem.ApplyAidRelief(world, healer, patient, AidKind.Treat,
            new AidSupply.Spend(ContentIds.Bandage, 0f, herbal: true));

        Assert.Multiple(() =>
        {
            Assert.That(patient.Wounds.Single().Stabilized, Is.True);
            Assert.That(patient.Wounds.Single().Clot01, Is.EqualTo(1f));
            Assert.That(patient.Body.Parts[BodyPart.LegL], Is.EqualTo(hp).Within(0.0001f));
            Assert.That(patient.Needs.Blood, Is.EqualTo(blood).Within(0.0001f));
        });
    }

    [Test]
    public void OpenCut_BleedsAndClotsByToughnessFormula()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Attributes.Toughness = 0f;
        patient.Needs.Blood = 1f;
        patient.Needs.Hunger = 1f; // isolate bleeding from natural blood refill
        patient.Wounds.Add(new WoundState
        {
            Id = 1, Zone = BodyPart.ArmL, Severity = 0.50f,
            Heal01 = 0f, Clot01 = 0f, Stabilized = false, BleedFactor = 1.2f
        });

        KenshiMedicalMath.Tick(world, patient);

        var expectedLoss = 0.50f * 1.2f * 0.012f * 1.3f;
        Assert.Multiple(() =>
        {
            Assert.That(patient.Needs.Blood, Is.EqualTo(1f - expectedLoss).Within(0.0001f));
            Assert.That(patient.Wounds.Single().Clot01,
                Is.EqualTo(Spec118.ClotPerSlowTickLowToughness).Within(0.0001f));
        });
    }

    [Test]
    public void UntreatedDeepCut_DegeneratesAwakeButBasicBedStopsIt()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Attributes.Toughness = 0f;
        patient.Needs.Hunger = 1f;
        patient.Wounds.Add(new WoundState
        {
            Id = 1, Zone = BodyPart.LegL, Severity = 0.40f,
            Heal01 = 0f, Clot01 = 0f, Stabilized = false, BleedFactor = 1f
        });
        var before = patient.Body.Parts[BodyPart.LegL];
        KenshiMedicalMath.Tick(world, patient);
        Assert.That(patient.Body.Parts[BodyPart.LegL], Is.LessThan(before));

        var junction = world.Tiles.Items[patient.Tile].Junctions[0];
        var bed = WorldObjectMutations.SpawnObject(
            world, ContentIds.BedBasic, patient.Fragment, patient.Tile, junction);
        patient.Execution.CurrentInteraction = InteractionType.Sleep;
        patient.Execution.TargetObject = bed.Id;
        var inBed = patient.Body.Parts[BodyPart.LegL];
        KenshiMedicalMath.Tick(world, patient);
        Assert.That(patient.Body.Parts[BodyPart.LegL], Is.EqualTo(inBed).Within(0.0001f));
    }

    [Test]
    public void FatalDegeneration_IsTerminalUntilCorpseSweep_Bug54()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Body.Parts[BodyPart.Torso] = 0f;
        patient.Body.Condition(BodyPart.Torso).CriticalTrauma = 0.99f;
        patient.Wounds.Add(new WoundState
        {
            Id = 54,
            Zone = BodyPart.Torso,
            Severity = 1f,
            Heal01 = 0f,
            Clot01 = 0f,
            Stabilized = false,
            BleedFactor = 1f
        });
        patient.Mind.DyingCause = DyingCause.VitalCrushed;
        patient.Health = System.Math.Max(patient.Body.Mean(), Spec105.BodyFloor);
        patient.Needs.Hunger = 1f;
        patient.Needs.Thirst = 1f;

        KenshiMedicalMath.Tick(world, patient);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Health, Is.Zero,
                "Fatal wound degeneration must leave the death latch at zero.");
            Assert.That(world.Events.Items.Count(e =>
                e.Type == "VitalPartDestroyed" && e.EntityId == patient.Id.Value), Is.EqualTo(1),
                "Medical progression must not kill/resurrect/kill again in one slow tick.");
        });

        new NeedsDecaySystem().Run(world);
        Assert.That(patient.Health, Is.Zero);
        Assert.That(world.Events.Items.Any(e =>
            e.Type == "Collapsed" && e.EntityId == patient.Id.Value), Is.False,
            "Starvation must not reopen dying after the terminal result.");

        new MobSystem().Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(patient.Id), Is.False);
        Assert.That(world.Entities.Corpses.ContainsKey(patient.Id), Is.True);
    }

    [Test]
    public void BasicBed_RecoversBluntDamageAtEightTimesAwakeRate()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Body.Parts[BodyPart.LegR] = 0.20f;
        patient.Body.Condition(BodyPart.LegR).BluntDamage = 0.80f;
        var junction = world.Tiles.Items[patient.Tile].Junctions[0];
        var bed = WorldObjectMutations.SpawnObject(
            world, ContentIds.BedBasic, patient.Fragment, patient.Tile, junction);
        patient.Execution.CurrentInteraction = InteractionType.Sleep;
        patient.Execution.TargetObject = bed.Id;

        KenshiMedicalMath.Tick(world, patient);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Body.Parts[BodyPart.LegR], Is.EqualTo(0.28f).Within(0.0001f));
            Assert.That(patient.Body.Condition(BodyPart.LegR).BluntDamage,
                Is.EqualTo(0.72f).Within(0.0001f));
        });
    }

    [Test]
    public void VitalZero_KnocksOutButCriticalDepthOneKills()
    {
        var world = TestWorld.CreateWorld();
        var target = world.Entities.Npcs.Values.First();

        BodyDamageResolver.ApplyLanded(world, target, BodyPart.Torso, 1f,
            DamageProfile.ForGear(GearCatalog.Fist), "test knockout");

        Assert.Multiple(() =>
        {
            Assert.That(target.Health, Is.GreaterThan(0f));
            Assert.That(target.IsDying, Is.True);
            Assert.That(target.Mind.FaintedUntilTick,
                Is.GreaterThanOrEqualTo(world.Tick + Spec118.VitalKnockoutTicks));
            Assert.That(target.Body.Condition(BodyPart.Torso).CriticalTrauma, Is.Zero);
        });

        BodyDamageResolver.ApplyLanded(world, target, BodyPart.Torso, 1f,
            DamageProfile.ForGear(GearCatalog.Fist), "test fatal depth");
        Assert.That(target.Health, Is.Zero);
        Assert.That(target.Body.Condition(BodyPart.Torso).CriticalTrauma, Is.EqualTo(1f));
    }

    [Test]
    public void RecoveryComaThreshold_ScalesWithToughnessAndGatesWake()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Attributes.Toughness = 0f;
        Assert.That(BodyDamageResolver.ComaThreshold(patient), Is.EqualTo(0.10f).Within(0.0001f));
        patient.Attributes.Toughness = 1f;
        Assert.That(BodyDamageResolver.ComaThreshold(patient), Is.EqualTo(0.85f).Within(0.0001f));

        world.Tick = 100;
        patient.Mind.DyingCause = DyingCause.VitalCrushed;
        patient.Mind.FaintedUntilTick = 80;
        patient.Needs.Blood = 0.50f;
        patient.Body.BloodDeficit = 0f;
        foreach (var vital in BodyState.VitalParts) patient.Body.Parts[vital] = 0.06f;
        patient.Body.Condition(BodyPart.Torso).CriticalTrauma = 0.84f;
        MortalityHelpers.TickDying(world, patient);
        Assert.That(patient.IsDying, Is.False,
            "At toughness 1, trauma below 0.85 may wake once vitals and blood are safe.");
    }

    [Test]
    public void ExhaustionCollapse_StaysOnTheTileWhereTheBodyFell()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        var fallenTile = patient.Tile;

        // Make the old "safe tile" scan reject the current hex. Before bug
        // #53 it then moved the unconscious body to a neighbouring hex.
        var objectJunction = world.Tiles.Items[fallenTile].Junctions
            .First(junction => patient.CurrentJunction is null ||
                !junction.Equals(patient.CurrentJunction.Value));
        WorldObjectMutations.SpawnObject(
            world, ContentIds.Stick, patient.Fragment, fallenTile, objectJunction);

        NeedsDecaySystem.EnterComa(world, patient, ComaCause.Exhaustion);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Tile, Is.EqualTo(fallenTile),
                "Collapse is a posture change; only rescue may move a body to another hex.");
            Assert.That(patient.Mind.ComaCause, Is.EqualTo(ComaCause.Exhaustion));
        });
    }

    [Test]
    public void ExpiredFaint_GetsWakeGraceAndCannotRearmInTheSameTick()
    {
        var world = TestWorld.CreateWorld();
        world.Tick = 100;
        var patient = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        patient.Mind.FaintedUntilTick = world.Tick - 1;
        patient.Mind.ConvalescentUntilTick = world.Tick + 100;
        patient.Needs.Stamina = 0f;
        patient.Needs.Hunger = 0.95f;
        patient.Needs.Blood = 1f;

        new DecisionSystem().Run(world);
        Assert.That(patient.Mind.WakeGraceUntilTick,
            Is.EqualTo(world.Tick + AiBalance.WakeGraceTicks));

        new NeedsDecaySystem().Run(world);
        Assert.That(patient.Mind.FaintedUntilTick, Is.Zero,
            "NeedsDecay must not turn the same wake edge back into another fall.");
    }

    [Test]
    public void CuttingCriticalDepth_SeversLimbAndLeavesRescuableStump()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Needs.Blood = 1f;

        BodyDamageResolver.ApplyLanded(world, patient, BodyPart.ArmL, 2f,
            DamageProfile.ForGear(GearCatalog.Machete), "test amputation");

        Assert.Multiple(() =>
        {
            Assert.That(patient.Body.IsSevered(BodyPart.ArmL), Is.True);
            Assert.That(patient.Health, Is.GreaterThan(0f));
            Assert.That(patient.Wounds.Any(w => w.Zone == BodyPart.ArmL && !w.Stabilized), Is.True);
            Assert.That(patient.Needs.Blood + patient.Body.BloodDeficit, Is.LessThan(1f));
        });
    }

    [Test]
    public void Splint_AddsFunctionalSupportWithoutHealingTheLimb()
    {
        var world = TestWorld.CreateWorld();
        var all = world.Entities.Npcs.Values.ToList();
        var patient = all[0];
        var healer = all[1];
        patient.Body.Parts[BodyPart.LegR] = 0.05f;
        patient.Body.Condition(BodyPart.LegR).BluntDamage = 0.95f;
        healer.Skills.Medicine = 1f;
        healer.Inventory.Items.Add(ContentIds.Splint);

        var applied = KenshiProstheticMath.ApplySplint(
            healer, patient, BodyPart.LegR);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.True);
            Assert.That(patient.Body.Parts[BodyPart.LegR], Is.EqualTo(0.05f));
            Assert.That(patient.Body.Condition(BodyPart.LegR).SplintSupport,
                Is.EqualTo(Spec118.SplintSupportExpert));
            Assert.That(patient.Body.LimbFunction(BodyPart.LegR),
                Is.EqualTo(Spec118.SplintSupportExpert));
        });
    }

    [Test]
    public void Prosthetic_TakesConditionDamageWithoutBloodAndBreaksToStump()
    {
        var world = TestWorld.CreateWorld();
        var target = world.Entities.Npcs.Values.First();
        target.Body.Sever(BodyPart.ArmR);
        target.Body.Condition(BodyPart.ArmR).Prosthetic = new ProstheticState
        {
            DefinitionId = ContentIds.WoodenArm,
            Part = BodyPart.ArmR,
            Condition = 0.30f,
            MaxCondition = 0.60f,
            Function = Spec118.WoodenArmFunction,
            Mechanical = false
        };
        var blood = target.Needs.Blood;
        var garmentBlood = target.WornItems.Sum(item => item.Bloodiness);

        var first = BodyDamageResolver.ApplyLanded(world, target, BodyPart.ArmR, 0.20f,
            DamageProfile.ForGear(GearCatalog.Machete), "test prosthetic");
        Assert.Multiple(() =>
        {
            Assert.That(first.HitProsthetic, Is.True);
            Assert.That(target.Body.Condition(BodyPart.ArmR).Prosthetic.Condition,
                Is.EqualTo(0.10f).Within(0.0001f));
            Assert.That(target.Needs.Blood, Is.EqualTo(blood));
            Assert.That(target.Wounds, Is.Empty);
            Assert.That(target.WornItems.Sum(item => item.Bloodiness),
                Is.EqualTo(garmentBlood).Within(0.0001f));
        });

        BodyDamageResolver.ApplyLanded(world, target, BodyPart.ArmR, 0.11f,
            DamageProfile.ForGear(GearCatalog.Machete), "test prosthetic break");
        Assert.That(target.Body.Condition(BodyPart.ArmR).Prosthetic, Is.Null);
        Assert.That(target.Body.LimbFunction(BodyPart.ArmR), Is.Zero);
    }

    [Test]
    public void FittedLeg_ExportsFunctionalPostureInsteadOfBareStumpCrawl()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Body.Sever(BodyPart.LegL);
        patient.Body.Condition(BodyPart.LegL).Prosthetic = new ProstheticState
        {
            DefinitionId = ContentIds.WoodenLeg,
            Part = BodyPart.LegL,
            Condition = Spec118.WoodenProstheticDurability,
            MaxCondition = Spec118.WoodenProstheticDurability,
            Function = Spec118.WoodenLegFunction,
            Mechanical = false
        };

        var snapshot = WorldSnapshotExporter.Export(world).Npcs
            .Single(entry => entry.Id == patient.Id);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Body.IsProne, Is.False);
            Assert.That(snapshot.PostureHint, Is.EqualTo("Limp"),
                "Рабочая деревянная нога должна поднять персонажа с земли, " +
                "но всё ещё оставлять заметную хромоту.");
            Assert.That(snapshot.BodyPartConditions.Single(entry => entry.Part == BodyPart.LegL)
                .Prosthetic, Is.Not.Null);
        });
    }

    [Test]
    public void SaveV28_RoundTripsTypedTraumaWoundsProstheticsAndCarryLinks()
    {
        var world = TestWorld.CreateWorld(118028);
        var all = world.Entities.Npcs.Values.ToList();
        var carrier = all[0];
        var patient = all[1];
        carrier.CarriedNpcId = patient.Id;
        patient.CarriedByNpcId = carrier.Id;
        patient.Body.BloodDeficit = 0.37f;
        patient.Body.Sever(BodyPart.LegL);
        patient.Body.Condition(BodyPart.LegL).CriticalTrauma = 0.61f;
        patient.Body.Condition(BodyPart.LegL).Prosthetic = new ProstheticState
        {
            DefinitionId = ContentIds.MechanicalLeg,
            Part = BodyPart.LegL,
            Condition = 0.73f,
            MaxCondition = 1.20f,
            Function = 0.80f,
            Mechanical = true
        };
        patient.Wounds.Add(new WoundState
        {
            Id = 77,
            Zone = BodyPart.Torso,
            Severity = 0.42f,
            Heal01 = 0.13f,
            Clot01 = 0.66f,
            Stabilized = true,
            BleedFactor = 1.2f,
            Seed = 991
        });

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(118028);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var loadedCarrier = loaded.Entities.Npcs[carrier.Id];
        var loadedPatient = loaded.Entities.Npcs[patient.Id];
        Assert.Multiple(() =>
        {
            Assert.That(loadedCarrier.CarriedNpcId, Is.EqualTo(patient.Id));
            Assert.That(loadedPatient.CarriedByNpcId, Is.EqualTo(carrier.Id));
            Assert.That(loadedPatient.Body.BloodDeficit, Is.EqualTo(0.37f).Within(0.0001f));
            Assert.That(loadedPatient.Body.Condition(BodyPart.LegL).CriticalTrauma,
                Is.EqualTo(0.61f).Within(0.0001f));
            Assert.That(loadedPatient.Body.Condition(BodyPart.LegL).Prosthetic.DefinitionId,
                Is.EqualTo(ContentIds.MechanicalLeg));
            Assert.That(loadedPatient.Wounds.Single(w => w.Id == 77).Clot01,
                Is.EqualTo(0.66f).Within(0.0001f));
            Assert.That(loadedPatient.Wounds.Single(w => w.Id == 77).Stabilized, Is.True);
        });
    }

    [Test]
    public void RescueAuction_ClaimsOnlyAnUnconsciousAlly()
    {
        var world = TestWorld.CreateWorld();
        var colony = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony).ToList();
        var hostile = world.Entities.Npcs.Values
            .First(n => n.Faction != Faction.Colony);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.PendingAidFrom = null;
            npc.Health = System.Math.Max(0.2f, npc.Body.Mean());
        }

        hostile.Mind.DyingCause = DyingCause.VitalCrushed;
        hostile.Body.Parts[BodyPart.Torso] = 0f;
        hostile.Health = hostile.Body.Mean();
        new RescueSystem().Run(world);
        Assert.That(colony.Any(n => n.Plan.TargetAgentId == hostile.Id), Is.False,
            "Enemy bodies remain loot-only; colony rescue never claims them.");

        hostile.Mind.DyingCause = DyingCause.None;
        var patient = colony[0];
        patient.Mind.DyingCause = DyingCause.VitalCrushed;
        patient.Body.Parts[BodyPart.Torso] = 0f;
        patient.Health = patient.Body.Mean();
        new RescueSystem().Run(world);
        Assert.That(colony.Any(n => n.Id != patient.Id &&
            n.Mind.CurrentGoal == GoalType.Rescue && n.Plan.TargetAgentId == patient.Id), Is.True);
    }

    [Test]
    public void BrokenCarryLink_DropsPatientAndClearsBothSides()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values.Take(2).ToList();
        var carrier = pair[0];
        var patient = pair[1];
        carrier.CarriedNpcId = patient.Id;
        patient.CarriedByNpcId = null; // corrupted half-link

        KenshiRescueMath.SyncAll(world);

        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.Null);
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(patient.CurrentJunction, Is.Not.Null,
                "Safe drop anchors the patient on a free lying footprint.");
        });

        // The same invariant must be repaired on load, before any fast tick
        // has a chance to run SyncAll.
        carrier.CarriedNpcId = patient.Id;
        patient.CarriedByNpcId = null;
        patient.Mind.ComaCause = ComaCause.BloodLoss;
        patient.CurrentJunction = null;
        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }
        blob.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Entities.Npcs[carrier.Id].CarriedNpcId, Is.Null);
            Assert.That(loaded.Entities.Npcs[patient.Id].CarriedByNpcId, Is.Null);
            Assert.That(loaded.Entities.Npcs[patient.Id].CurrentJunction, Is.Not.Null);
        });
    }

    [Test]
    public void OwnMedicalCrisis_EndsPlayDeadAndTrainsToughnessOnce()
    {
        var world = TestWorld.CreateWorld();
        world.Tick = 1;
        var survivor = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var enemy = world.Entities.Npcs.Values.First(n => n.Faction != survivor.Faction);
        enemy.Tile = survivor.Tile;
        enemy.Position = survivor.Position;
        survivor.Needs.Blood = 1f;
        survivor.Body.BloodDeficit = 0f;
        survivor.Mind.IsStarving = false;
        survivor.Mind.IsDehydrated = false;
        survivor.Wounds.Clear();
        Assert.That(MortalityHelpers.TryStartPlayDead(world, survivor), Is.True);

        survivor.Body.BloodDeficit = 0.10f;
        var before = survivor.Attributes.Toughness;
        Assert.That(MortalityHelpers.ShouldDangerouslyRise(
            world, survivor, out var reason), Is.True);
        MortalityHelpers.EndPlayDead(world, survivor, reason);

        Assert.Multiple(() =>
        {
            Assert.That(survivor.Mind.PlayDeadSinceTick, Is.Zero);
            Assert.That(survivor.Attributes.Toughness, Is.GreaterThan(before));
        });
    }
}

}
