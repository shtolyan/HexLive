using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class BandagePriorityTests
{
    private bool _enabled;

    [SetUp]
    public void EnableMedicalCore()
    {
        _enabled = Spec118.Enabled;
        Spec118.Enabled = true;
    }

    [TearDown]
    public void RestoreMedicalCore() => Spec118.Enabled = _enabled;

    private static (WorldState world, NPCState patient) Patient()
    {
        var world = TestWorld.CreateWorld();
        var patient = world.Entities.Npcs.Values.First();
        patient.Wounds.Clear();
        patient.GauzeZones.Clear();
        patient.BandagedZones.Clear();
        foreach (var part in patient.Body.Parts.Keys.ToArray())
        {
            patient.Body.Parts[part] = 1f;
            patient.Body.Condition(part).CriticalTrauma = 0f;
        }
        return (world, patient);
    }

    private static WoundState Cut(NPCState patient, BodyPart zone, float severity, float clot = 0f)
    {
        var wound = new WoundState
        {
            Id = patient.Wounds.Count + 1, Zone = zone, Severity = severity,
            Clot01 = clot, BleedFactor = 1f
        };
        patient.Wounds.Add(wound);
        return wound;
    }

    [TestCase(BodyPart.Head)]
    [TestCase(BodyPart.Torso)]
    [TestCase(BodyPart.Pelvis)]
    [TestCase(BodyPart.ArmL)]
    [TestCase(BodyPart.ArmR)]
    [TestCase(BodyPart.LegL)]
    [TestCase(BodyPart.LegR)]
    public void WorstPartOutranksBiggerFreshCutOnHealthierPart(BodyPart worst)
    {
        var (_, patient) = Patient();
        var healthier = worst == BodyPart.ArmL ? BodyPart.ArmR : BodyPart.ArmL;
        patient.Body.Parts[worst] = 0.2f;
        patient.Body.Parts[healthier] = 0.8f;
        var deepPartCut = Cut(patient, worst, 0.08f, clot: 1f);
        var freshCut = Cut(patient, healthier, 0.2f);

        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out var dressed), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(dressed.Zone, Is.EqualTo(worst));
            Assert.That(deepPartCut.Stabilized, Is.True);
            Assert.That(freshCut.Stabilized, Is.False);
            Assert.That(patient.GauzeZones, Is.EquivalentTo(new[] { worst }));
            Assert.That(patient.Body.Parts[worst], Is.EqualTo(0.2f), "A dressing does not restore HP instantly.");
        });
    }

    [Test]
    public void SeveralCutsOnOnePartOutrankOneBiggerCut_AndAllAreDressedTogether()
    {
        var (_, patient) = Patient();
        patient.Body.Parts[BodyPart.LegL] = 0.4f;
        patient.Body.Parts[BodyPart.ArmL] = 0.7f;
        for (var i = 0; i < 3; i++) Cut(patient, BodyPart.LegL, 0.2f);
        var arm = Cut(patient, BodyPart.ArmL, 0.3f);

        Assert.That(WoundMath.StabilizeMostDangerous(patient, true, out var first), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(first.Zone, Is.EqualTo(BodyPart.LegL));
            Assert.That(patient.Wounds.Where(w => w.Zone == BodyPart.LegL).All(w => w.Stabilized), Is.True);
            Assert.That(arm.Stabilized, Is.False);
            Assert.That(patient.BandagedZones, Is.EquivalentTo(new[] { BodyPart.LegL }));
        });
        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out var second), Is.True);
        Assert.That(second.Zone, Is.EqualTo(BodyPart.ArmL));
        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out _), Is.False);
    }

    [Test]
    public void CriticalDepthOrdersPartsEvenWhenBothHpBarsAreZero()
    {
        var (_, patient) = Patient();
        patient.Body.Parts[BodyPart.Torso] = patient.Body.Parts[BodyPart.Pelvis] = 0f;
        patient.Body.Condition(BodyPart.Torso).CriticalTrauma = 0.5f;
        patient.Body.Condition(BodyPart.Pelvis).CriticalTrauma = 0.1f;
        Cut(patient, BodyPart.Torso, 0.08f, clot: 1f);
        Cut(patient, BodyPart.Pelvis, 0.4f);

        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out var dressed), Is.True);
        Assert.That(dressed.Zone, Is.EqualTo(BodyPart.Torso));
    }

    [Test]
    public void CutsBeyondZeroHpDoNotCountCriticalDepthTwice()
    {
        var (_, patient) = Patient();
        patient.Body.Parts[BodyPart.Torso] = patient.Body.Parts[BodyPart.Pelvis] = 0f;
        patient.Body.Condition(BodyPart.Torso).CriticalTrauma = 0.4f;
        patient.Body.Condition(BodyPart.Pelvis).CriticalTrauma = 0.5f;
        for (var i = 0; i < 4; i++) Cut(patient, BodyPart.Torso, 0.35f);
        Cut(patient, BodyPart.Pelvis, 0.1f);

        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out var dressed), Is.True);
        Assert.That(dressed.Zone, Is.EqualTo(BodyPart.Pelvis));
    }

    [Test]
    public void SameDamageUsesActiveBleedingWithoutCountingCutHpLossTwice()
    {
        var (_, patient) = Patient();
        patient.Body.Parts[BodyPart.LegL] = patient.Body.Parts[BodyPart.ArmL] = 0.4f;
        Cut(patient, BodyPart.LegL, 0.6f, clot: 1f);
        Cut(patient, BodyPart.ArmL, 0.2f); // the rest of this part's HP loss is blunt damage

        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out var dressed), Is.True);
        Assert.That(dressed.Zone, Is.EqualTo(BodyPart.ArmL));
    }

    [Test]
    public void DressingSkipsDryStumpHealthyScratchHealedAndAlreadyTreatedZones()
    {
        var (_, patient) = Patient();
        patient.Body.Sever(BodyPart.LegL);
        Cut(patient, BodyPart.LegL, 0.7f, clot: 1f);
        patient.Body.Parts[BodyPart.Head] = 0.05f;
        Cut(patient, BodyPart.Head, 0.8f).Stabilized = true;
        patient.Body.Parts[BodyPart.Torso] = 0.1f;
        Cut(patient, BodyPart.Torso, 0.8f).Heal01 = 1f;
        patient.Body.Parts[BodyPart.Pelvis] = 0.1f; // only blunt damage, no cut
        Cut(patient, BodyPart.ArmR, 0.05f, clot: 1f);
        patient.Body.Parts[BodyPart.ArmL] = 0.8f;
        var treatable = Cut(patient, BodyPart.ArmL, 0.2f);

        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out var dressed), Is.True);
        Assert.That(dressed, Is.SameAs(treatable));
        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EqualInjuriesChooseSamePartRegardlessOfRecordAndPartOrder(bool reverse)
    {
        var (_, patient) = Patient();
        patient.Body.Parts[BodyPart.Head] = patient.Body.Parts[BodyPart.LegL] = 0.5f;
        Cut(patient, BodyPart.LegL, 0.2f, clot: 0.5f);
        Cut(patient, BodyPart.Head, 0.2f, clot: 0.5f);
        if (reverse)
        {
            patient.Wounds.Reverse();
            var parts = patient.Body.Parts.Reverse().ToArray();
            patient.Body.Parts.Clear();
            foreach (var p in parts) patient.Body.Parts.Add(p.Key, p.Value);
        }

        Assert.That(WoundMath.StabilizeMostDangerous(patient, false, out var dressed), Is.True);
        Assert.That(dressed.Zone, Is.EqualTo(BodyPart.Head));
    }

    [Test]
    public void HelperAndManualSelfTreatmentUseSameOrderAcrossAllSevenParts()
    {
        var (world, patient) = Patient();
        var helper = world.Entities.Npcs.Values.First(n => n != patient);
        Assert.That(ManualCommandExecutor.Apply(world, new SetManualControlCommand(patient.Id, true)).Accepted, Is.True);
        var order = System.Enum.GetValues<BodyPart>();
        foreach (var zone in order)
        {
            patient.Body.Parts[zone] = 0.1f * ((int)zone + 1);
            Cut(patient, zone, 0.03f * ((int)zone + 1));
        }
        patient.Inventory.Items.Clear();
        for (var i = 0; i < order.Length; i++) patient.Inventory.Items.Add(MedicalSupplyMath.CreateBandage(false));
        var execution = new ExecutionSystem();
        for (var i = 0; i < order.Length; i++)
        {
            if (i % 2 == 0)
            {
                var admitted = ManualCommandExecutor.Apply(world, new SelfActionCommand(patient.Id, SelfActionKind.TreatSelf));
                Assert.That(admitted.Accepted, Is.True, admitted.Reason);
                execution.Run(world);
                world.Tick = patient.Execution.EndTick;
                execution.Run(world);
            }
            else
            {
                ExecutionSystem.ApplyAidRelief(world, helper, patient, AidKind.Treat,
                    new AidSupply.Spend(ContentIds.Bandage, 0f, herbal: false));
            }
            Assert.That(patient.GauzeZones, Is.EquivalentTo(order.Take(i + 1)), "After each action only the next worst part is added.");
        }
    }

    [Test]
    public void SelfTreatmentRechecksInjuriesWhenTheActionCompletes()
    {
        var (world, patient) = Patient();
        Assert.That(ManualCommandExecutor.Apply(world, new SetManualControlCommand(patient.Id, true)).Accepted, Is.True);
        patient.Body.Parts[BodyPart.LegL] = 0.5f;
        var leg = Cut(patient, BodyPart.LegL, 0.5f);
        patient.Inventory.Items.Clear();
        patient.Inventory.Items.Add(MedicalSupplyMath.CreateBandage(false));
        Assert.That(ManualCommandExecutor.Apply(world, new SelfActionCommand(patient.Id, SelfActionKind.TreatSelf)).Accepted, Is.True);
        var execution = new ExecutionSystem();
        execution.Run(world);
        patient.Body.Parts[BodyPart.Torso] = 0.1f;
        var torso = Cut(patient, BodyPart.Torso, 0.9f);
        world.Tick = patient.Execution.EndTick;
        execution.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(torso.Stabilized, Is.True);
            Assert.That(leg.Stabilized, Is.False);
            Assert.That(MedicalSupplyMath.BandageCount(patient), Is.Zero);
        });
    }

    [Test]
    public void PlasterStillCoversOnlyOneWound()
    {
        var (_, patient) = Patient();
        var first = Cut(patient, BodyPart.Torso, 0.3f);
        var second = Cut(patient, BodyPart.Torso, 0.2f);
        Assert.That(WoundMath.PlasterMostDangerous(patient, out var plastered), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(plastered, Is.SameAs(first));
            Assert.That(first.Plastered, Is.True);
            Assert.That(second.Stabilized, Is.False);
            Assert.That(patient.GauzeZones, Is.Empty);
            Assert.That(patient.BandagedZones, Is.Empty);
        });
    }
}
