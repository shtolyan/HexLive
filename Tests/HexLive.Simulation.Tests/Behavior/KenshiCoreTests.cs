using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Navigation;
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
    public void ClottedIntactCut_RemainsAftercareAndExportsAsDryNotHealed()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Needs.Blood = 1f;
        patient.Needs.Hunger = 0f;
        patient.Needs.Thirst = 0f;
        patient.Wounds.Add(new WoundState
        {
            Id = 1181,
            Zone = BodyPart.LegL,
            Severity = 0.08f,
            Heal01 = 0f,
            Clot01 = 1f,
            Stabilized = false,
            BleedFactor = 1.2f,
            Seed = 1181
        });

        var kind = AidAssessment.Assess(patient, world.Tick, out var severity);
        var snapshot = WorldSnapshotExporter.Export(world).Npcs
            .Single(entry => entry.Id == patient.Id);
        var visualHeal = float.Parse(snapshot.Wounds.Single().Split('|')[2],
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(MortalityHelpers.IsBleeding(patient), Is.False);
            Assert.That(WoundMath.NeedsAftercare(patient), Is.True);
            Assert.That(DecisionSystem.SelfTreatBurden(patient), Is.GreaterThan(0f));
            Assert.That(kind, Is.EqualTo(AidKind.Treat),
                "An ally must still be able to finish care after natural clotting.");
            Assert.That(severity, Is.GreaterThanOrEqualTo(Spec53.SelfTreatBurdenThreshold));
            Assert.That(snapshot.OpenWounds.Single().Heal01, Is.Zero,
                "Dry presentation must not fake authoritative medical healing.");
            Assert.That(visualHeal, Is.EqualTo(WoundMath.ClottedVisualHealFloor)
                .Within(0.001f));
        });
    }

    [Test]
    public void ClottedStump_IsInjuredButNotReportedAsActivelyBleeding()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Needs.Blood = 1f;
        patient.Body.Sever(BodyPart.ArmR);
        var wound = new WoundState
        {
            Id = 1182,
            Zone = BodyPart.ArmR,
            Severity = Spec118.StumpWoundSeverity,
            Heal01 = 0f,
            Clot01 = 1f,
            Stabilized = false,
            BleedFactor = 1.4f
        };
        patient.Wounds.Add(wound);
        var effects = new System.Collections.Generic.List<ActiveEffect>();

        EffectEvaluator.Collect(patient, world.Tick, 0f, false, false, effects);
        var snapshot = WorldSnapshotExporter.Export(world).Npcs
            .Single(entry => entry.Id == patient.Id);
        var visualHeal = float.Parse(snapshot.Wounds.Single().Split('|')[2],
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(effects.Any(effect => effect.Kind == EffectKind.Bleeding), Is.False,
                "A dry stump must not keep a red bleeding status forever.");
            Assert.That(effects.Any(effect => effect.Kind == EffectKind.Injured), Is.True,
                "The remaining injury must still be visible as an injury.");
            Assert.That(visualHeal, Is.EqualTo(WoundMath.ClottedStumpVisualHealFloor)
                .Within(0.001f),
                "A dry stump must keep only a faint scar, not the wet-cut presentation.");
        });

        wound.Clot01 = 0.5f;
        effects.Clear();
        EffectEvaluator.Collect(patient, world.Tick, 0f, false, false, effects);
        Assert.That(effects.Any(effect => effect.Kind == EffectKind.Bleeding), Is.True,
            "The status must return while the stump is actually losing blood.");
    }

    [Test]
    public void LowBloodWithoutTreatableWound_DoesNotSpendAnotherBandage()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Needs.Blood = 0.4f;

        Assert.Multiple(() =>
        {
            Assert.That(WoundMath.NeedsAftercare(patient), Is.False);
            Assert.That(DecisionSystem.SelfTreatBurden(patient), Is.Zero);
            Assert.That(AidAssessment.Assess(patient, world.Tick, out _),
                Is.Not.EqualTo(AidKind.Treat));
        });
    }

    [Test]
    public void Bandage_PrefersActiveBleedBeforeDeeperClottedCut()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Wounds.Add(new WoundState
        {
            Id = 1, Zone = BodyPart.LegL, Severity = 0.8f,
            Heal01 = 0f, Clot01 = 1f, Stabilized = false, BleedFactor = 1.2f
        });
        patient.Wounds.Add(new WoundState
        {
            Id = 2, Zone = BodyPart.ArmL, Severity = 0.08f,
            Heal01 = 0f, Clot01 = 0.5f, Stabilized = false, BleedFactor = 1.2f
        });

        Assert.That(WoundMath.StabilizeMostDangerous(
            patient, herbal: false, out var stabilized), Is.True);
        Assert.That(stabilized.Id, Is.EqualTo(2));
    }

    [Test]
    public void Bandage_ChoosesDeepestWhenAllOpenCutsAreClotted()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Wounds.Add(new WoundState
        {
            Id = 1, Zone = BodyPart.ArmL, Severity = 0.05f,
            Heal01 = 0f, Clot01 = 1f, Stabilized = false, BleedFactor = 1.2f
        });
        patient.Wounds.Add(new WoundState
        {
            Id = 2, Zone = BodyPart.LegL, Severity = 0.30f,
            Heal01 = 0f, Clot01 = 1f, Stabilized = false, BleedFactor = 1.2f
        });

        Assert.That(WoundMath.StabilizeMostDangerous(
            patient, herbal: true, out var stabilized), Is.True);
        Assert.That(stabilized.Id, Is.EqualTo(2));
    }

    [Test]
    public void ClottedStumpWound_ClosesGraduallyWithoutRestoringTheLimb()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.Attributes.Toughness = 0f;
        patient.Body.Sever(BodyPart.ArmR);
        patient.Wounds.Add(new WoundState
        {
            Id = 50,
            Zone = BodyPart.ArmR,
            Severity = Spec118.StumpWoundSeverity,
            Heal01 = 0f,
            Clot01 = 0.999f,
            Stabilized = false,
            BleedFactor = 1.4f,
            Seed = 5050
        });

        KenshiMedicalMath.Tick(world, patient);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Wounds, Has.Count.EqualTo(1));
            Assert.That(patient.Wounds[0].Clot01, Is.EqualTo(1f));
            Assert.That(patient.Wounds[0].Heal01, Is.GreaterThan(0f).And.LessThan(1f),
                "A clotted stump mark must fade over time instead of disappearing at once.");
            Assert.That(patient.Wounds[0].Stabilized, Is.False,
                "Natural scar closure must not pretend that a dressing was applied.");
            Assert.That(WoundMath.NeedsAftercare(patient), Is.False,
                "A dry stump scars by itself and must not consume another bandage.");
            Assert.That(patient.Body.Parts[BodyPart.ArmR], Is.Zero);
            Assert.That(patient.Body.IsSevered(BodyPart.ArmR), Is.True);
        });

        // §118.8: рубцевание идёт сутками (1/3000 шкалы за slow tick), культя
        // severity 0.35 закрывается за ~1050 slow ticks на ногах.
        for (var i = 0; i < 1200 && patient.Wounds.Count > 0; i++)
        {
            KenshiMedicalMath.Tick(world, patient);
        }

        Assert.Multiple(() =>
        {
            Assert.That(patient.Wounds, Is.Empty,
                "The healed record must be removed so its texture stamp is rebuilt away.");
            Assert.That(patient.Body.Parts[BodyPart.ArmR], Is.Zero,
                "Closing the stump wound must never regenerate a severed arm.");
            Assert.That(patient.Body.IsSevered(BodyPart.ArmR), Is.True);
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
    // §118.8: кровать лечит вдвое быстрее бодрствования (было ×8 — «минус
    // грудь» закрывалась за полчаса; теперь лечение меряется сутками).
    public void BasicBed_RecoversBluntDamageAtTwiceAwakeRate()
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

        var step = Spec118.BluntRecoveryPerSlowTick * Spec118.BasicBedHealMultiplier;
        Assert.Multiple(() =>
        {
            Assert.That(Spec118.BasicBedHealMultiplier, Is.EqualTo(2f).Within(0.0001f),
                "§118.8: bed heals at twice the awake rate — a day for the full bar.");
            Assert.That(patient.Body.Parts[BodyPart.LegR],
                Is.EqualTo(0.20f + step).Within(0.0001f));
            Assert.That(patient.Body.Condition(BodyPart.LegR).BluntDamage,
                Is.EqualTo(0.80f - step).Within(0.0001f));
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
    public void TinyLegRegenWhileStillCrawling_DoesNotRearmWakeGrace()
    {
        var world = TestWorld.CreateWorld();
        world.Tick = 100;
        var patient = world.Entities.Npcs.Values.First();
        patient.Mind.WakeGraceUntilTick = 0;
        patient.Body.Parts[BodyPart.LegL] = 0.0033f;
        patient.Body.Parts[BodyPart.LegR] = 0.14f;

        MortalityHelpers.GrantStandUpGrace(world, patient, wasProne: true);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Body.IsProne, Is.False,
                "The legacy zero-HP posture edge has technically cleared.");
            Assert.That(patient.Body.IsCrawling, Is.True,
                "Both legs are still far below the shared standing threshold.");
            Assert.That(patient.Mind.WakeGraceUntilTick, Is.Zero,
                "Tiny regen on a repeatedly damaged leg must not freeze decisions forever.");
        });

        patient.Body.Parts[BodyPart.LegL] = 0.8f;
        patient.Body.Parts[BodyPart.LegR] = 0.8f;
        MortalityHelpers.GrantStandUpGrace(world, patient, wasProne: true);
        Assert.That(patient.Mind.WakeGraceUntilTick,
            Is.EqualTo(world.Tick + AiBalance.WakeGraceTicks));
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
    public void ProneAmputee_WithAUsableHand_CanChooseSelfBandaging()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Body.Sever(BodyPart.LegL);
        patient.Wounds.Clear();
        patient.Wounds.Add(new WoundState
        {
            Id = 1183,
            Zone = BodyPart.LegL,
            Severity = Spec118.StumpWoundSeverity,
            Heal01 = 0f,
            Clot01 = 0.4f,
            Stabilized = false,
            BleedFactor = 1.2f
        });
        patient.Inventory.Items.RemoveAll(item => item.DefinitionId == ContentIds.Bandage);
        patient.Inventory.Items.Add(MedicalSupplyMath.CreateBandage(herbal: true));
        patient.Inventory.Items.Add(MedicalSupplyMath.CreateBandage(herbal: true));
        patient.Plan.Status = PlanStatus.Completed;
        patient.Execution.Status = ExecutionStatus.None;
        patient.Mind.CurrentGoal = GoalType.None;

        new DecisionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Body.IsProne, Is.True);
            Assert.That(patient.Body.HasUsableHand, Is.True);
            Assert.That(patient.Mind.LastScores.Single(score =>
                score.Goal == GoalType.TreatWounds).FinalScore, Is.GreaterThan(0f),
                "A missing leg must not make carried bandages unusable.");
            Assert.That(patient.Mind.CurrentGoal, Is.EqualTo(GoalType.TreatWounds));
        });
    }

    [Test]
    public void ReadyPledgedProsthetic_TransportsAwakePatientToBed()
    {
        var world = TestWorld.CreateWorld();
        var colonists = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony).Take(2).ToList();
        var patient = colonists[0];
        var helper = colonists[1];
        patient.Body.Sever(BodyPart.LegL);
        patient.Wounds.Clear();
        patient.Plan.Status = PlanStatus.Completed;
        patient.Execution.Status = ExecutionStatus.None;
        patient.Mind.CurrentGoal = GoalType.None;
        helper.Inventory.Items.Clear();
        helper.Inventory.Items.Add(ContentIds.WoodenLeg);
        helper.Mind.ProstheticAidTargetId = patient.Id;
        helper.Mind.ProstheticAidPart = BodyPart.LegL;
        helper.Plan.Status = PlanStatus.Completed;
        helper.Execution.Status = ExecutionStatus.None;
        helper.Mind.CurrentGoal = GoalType.None;

        new RescueSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(KenshiRescueMath.NeedsRescue(world, patient), Is.False,
                "This is medical preparation, not critical-coma rescue.");
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.Rescue));
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(helper.Plan.TargetAgentId, Is.EqualTo(patient.Id));
            Assert.That(patient.Mind.PendingAidFrom, Is.EqualTo(helper.Id));
        });
    }

    [Test]
    public void ProstheticPledgeDoesNotResurrectGatherToolsDuringFailureCooldown()
    {
        var world = TestWorld.CreateWorld();
        var colonists = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony).Take(2).ToArray();
        var patient = colonists[0];
        var helper = colonists[1];
        patient.Body.Sever(BodyPart.LegL);
        patient.Wounds.Clear();
        helper.Inventory.Items.Clear();
        helper.Mind.ProstheticAidTargetId = patient.Id;
        helper.Mind.ProstheticAidPart = BodyPart.LegL;
        helper.Mind.CurrentGoal = GoalType.None;
        helper.Plan.Status = PlanStatus.Completed;
        helper.Execution.Status = ExecutionStatus.None;
        helper.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = GoalType.GatherTools,
            EndTick = world.Tick + 100
        });

        new ProstheticAidSystem().Run(world);

        Assert.That(helper.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.GatherTools),
            "Внешний исполнитель обещания протеза обязан уважать cooldown провалившегося пути.");
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
            world, healer, patient, BodyPart.LegR);

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
    public void AbuseCombat_DropsCarriedPatientThenResumesThatRescueAfterScene()
    {
        var world = TestWorld.CreateWorld();
        world.Tick = 40;
        var colony = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony).Take(2).ToList();
        var carrier = colony[0];
        var patient = colony[1];
        var abuser = world.Entities.Npcs.Values.First(n => n.Faction != carrier.Faction);

        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Plan.Status = PlanStatus.Active; // keep unrelated helpers out of the auction
            npc.Execution.Status = ExecutionStatus.None;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.PendingAidFrom = null;
        }

        patient.Mind.ComaCause = ComaCause.BloodLoss;
        patient.Health = System.Math.Max(0.2f, patient.Body.Mean());
        carrier.CarriedNpcId = patient.Id;
        carrier.Mind.CurrentGoal = GoalType.Rescue;
        carrier.Plan.Goal = GoalType.Rescue;
        carrier.Plan.TargetAgentId = patient.Id;
        patient.CarriedByNpcId = carrier.Id;
        patient.Mind.PendingAidFrom = carrier.Id;
        carrier.Mind.PendingAbuseFrom = abuser.Id;
        abuser.Tile = carrier.Tile;
        abuser.Position = carrier.Position;

        PlanInterruption.TryAbortForCombat(world, carrier, InterruptionCause.CombatVictim,
            $"Abused by NPC{abuser.Id.Value}");
        carrier.Mind.CurrentGoal = GoalType.None; // mirrors the abuse call site

        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.Null,
                "The abuse scene must free the carrier's hands before combat.");
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(patient.CurrentJunction, Is.Not.Null,
                "The interrupted patient must be physically anchored on the ground.");
            Assert.That(carrier.Mind.InterruptedRescuePatientId, Is.EqualTo(patient.Id));
            Assert.That(patient.Mind.PendingAidFrom, Is.EqualTo(carrier.Id),
                "The exact patient stays reserved while the rescuer is in the scene.");
            Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
        });

        MeleeSwing.TryAdvanceSwing(world, carrier, inReach: true, out _, out _);
        Assert.That(carrier.StrikeLandsAtTick, Is.GreaterThan(world.Tick),
            "Once the patient is down, the carrier must be able to start a counter-swing.");

        new RescueSystem().Run(world);
        Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Invalid),
            "A cowed mark must not restart carrying in the middle of an abuse scene.");
        new DecisionSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(carrier.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
                "The ordinary goal auction must stay closed while the abuse scene owns her.");
        });

        carrier.Mind.PendingAbuseFrom = null;
        carrier.IsFighting = false;
        new RescueSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(carrier.Mind.CurrentGoal, Is.EqualTo(GoalType.Rescue));
            Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(carrier.Plan.TargetAgentId, Is.EqualTo(patient.Id));
            Assert.That(carrier.Mind.InterruptedRescuePatientId, Is.Null);
            Assert.That(patient.Mind.PendingAidFrom, Is.EqualTo(carrier.Id));
        });
    }

    [Test]
    public void OrdinaryReplan_DropsPatientWithoutKeepingACombatResumePromise()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony).Take(2).ToList();
        var carrier = pair[0];
        var patient = pair[1];
        patient.Mind.ComaCause = ComaCause.BloodLoss;
        carrier.CarriedNpcId = patient.Id;
        patient.CarriedByNpcId = carrier.Id;
        carrier.Mind.CurrentGoal = GoalType.Rescue;
        carrier.Plan.Goal = GoalType.Rescue;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Plan.TargetAgentId = patient.Id;

        PlanInterruption.TryAbort(world, carrier, InterruptionCause.Auction, "test replacement plan");

        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.Null);
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(patient.CurrentJunction, Is.Not.Null);
            Assert.That(carrier.Mind.InterruptedRescuePatientId, Is.Null,
                "Only a temporary combat interruption promises an automatic return.");
            Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
        });
    }

    [Test]
    public void TransientCarryPathFailure_WaitsForRetryBudgetBeforeDroppingPatient()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony).Take(2).ToList();
        var carrier = pair[0];
        var patient = pair[1];
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Execution.Status = ExecutionStatus.None;
        }

        patient.Mind.ComaCause = ComaCause.BloodLoss;
        patient.CurrentJunction = null;
        carrier.CarriedNpcId = patient.Id;
        patient.CarriedByNpcId = carrier.Id;
        carrier.Mind.CurrentGoal = GoalType.Rescue;
        carrier.Plan.Goal = GoalType.Rescue;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.Plan.TargetJunctionId = carrier.CurrentJunction;
        carrier.Movement.SetStatus(MovementStatus.Blocked);
        carrier.Movement.BlockedWaitTicks = 1;

        KenshiRescueMath.SyncAll(world);
        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.EqualTo(patient.Id),
                "Attempt 1/4 is transient; the patient must stay in the carrier's arms.");
            Assert.That(patient.CarriedByNpcId, Is.EqualTo(carrier.Id));
            Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Active));
        });

        PlanInterruption.TryAbort(world, carrier, InterruptionCause.PathFailure, "path retry limit reached");
        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.Null);
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(patient.CurrentJunction, Is.Not.Null,
                "The terminal route failure still puts the patient down safely.");
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
