using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
    public sealed class HumanStrikeDecisionTests
    {
        [TestCase(-1f, 0f, 1f)]
        [TestCase(-1f, 0.5f, 0.5f)]
        [TestCase(-0.5f, 0.2f, 0.4f)]
        [TestCase(0.5f, 0f, 0f)]
        public void KillIntent_IsHatredTimesInverseCompassion(
            float affinity, float compassion, float expected)
        {
            var world = TestWorld.CreateWorld();
            var pair = world.Entities.Npcs.Values.Take(2).ToArray();
            pair[0].Social.GetOrCreate(pair[1].Id).Affinity = affinity;
            pair[0].CompassionTrait = compassion;

            Assert.That(HumanStrikeDecision.CalculateKillIntent(pair[0], pair[1]),
                Is.EqualTo(expected).Within(0.0001f));
        }

        [TestCase(BodyPart.Head)]
        [TestCase(BodyPart.Torso)]
        [TestCase(BodyPart.Pelvis)]
        public void VitalPartCrossingZero_IsPotentiallyFatal(BodyPart part)
        {
            var target = TestWorld.CreateWorld().Entities.Npcs.Values.First();
            target.Body.Parts[part] = 0.2f;
            Assert.That(HumanStrikeDecision.IsPotentiallyFatal(target, part, 0.21f), Is.True);
        }

        [Test]
        public void VitalCriticalDepthCrossingOne_IsPotentiallyFatal()
        {
            var target = TestWorld.CreateWorld().Entities.Npcs.Values.First();
            target.Body.Parts[BodyPart.Torso] = 0.05f;
            target.Body.Condition(BodyPart.Torso).CriticalTrauma = 0.94f;

            Assert.That(HumanStrikeDecision.IsPotentiallyFatal(
                target, BodyPart.Torso, 0.12f), Is.True);
        }

        [Test]
        public void RefusedFatalHit_LeavesPartAndDyingReserveAboveTheirFloors()
        {
            var target = TestWorld.CreateWorld().Entities.Npcs.Values.First();
            target.Body.Parts[BodyPart.Head] = 0.2f;
            target.Mind.DyingCause = DyingCause.VitalCrushed;
            target.Mind.DyingReserve = 0.02f;

            var capped = HumanStrikeDecision.CapNonLethal(target, BodyPart.Head, 1f);

            Assert.That(target.Body.Parts[BodyPart.Head] - capped,
                Is.GreaterThanOrEqualTo(Spec86.MercyPartFloor - 0.0001f));
            Assert.That(capped * Spec105.DamageReserveFactor,
                Is.LessThan(target.Mind.DyingReserve));
        }

        [Test]
        public void SaveLoadDuringWindup_PreservesPartAndMercyDecision()
        {
            var world = TestWorld.CreateWorld(91231);
            var actor = world.Entities.Npcs.Values.First();
            var target = world.Entities.Npcs.Values.Skip(1).First();
            actor.StrikeLandsAtTick = world.Tick + 5;
            actor.StrikeReadyAtTick = world.Tick + 9;
            actor.AttackAnimUntilTick = world.Tick + 8;
            actor.SwingStartTick = world.Tick;
            actor.SwingStrikeIndex = 2;
            actor.PendingHumanStrikeTargetId = target.Id;
            actor.PendingHumanStrikePart = BodyPart.Pelvis;
            actor.PendingHumanStrikeKillAuthorized = false;
            actor.PendingHumanStrikeKillIntent = 0.42f;
            actor.Mind.CombatOpponentNpcId = target.Id;
            actor.Mind.ForcedMeleeWeaponId = "tool.machete";

            using var blob = new MemoryStream();
            using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, true))
            {
                WorldSaveSerializer.Write(world, writer);
            }

            blob.Position = 0;
            var loaded = TestWorld.CreateWorld(91231);
            using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, true))
            {
                WorldSaveSerializer.Read(loaded, reader);
            }

            var restored = loaded.Entities.Npcs[actor.Id];
            Assert.That(restored.PendingHumanStrikeTargetId, Is.EqualTo(target.Id));
            Assert.That(restored.PendingHumanStrikePart, Is.EqualTo(BodyPart.Pelvis));
            Assert.That(restored.PendingHumanStrikeKillAuthorized, Is.False);
            Assert.That(restored.PendingHumanStrikeKillIntent, Is.EqualTo(0.42f).Within(0.0001f));
            Assert.That(restored.StrikeLandsAtTick, Is.EqualTo(actor.StrikeLandsAtTick));
            Assert.That(restored.Mind.CombatOpponentNpcId, Is.EqualTo(target.Id));
            Assert.That(restored.Mind.ForcedMeleeWeaponId, Is.EqualTo("tool.machete"));
        }
    }
}
