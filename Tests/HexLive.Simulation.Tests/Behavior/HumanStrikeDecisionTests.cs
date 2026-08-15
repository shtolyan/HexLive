using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
    public sealed class HumanCombatLethalityTests
    {
        [Test]
        public void MaximumCompassion_DoesNotCapPotentiallyFatalCombatDamage()
        {
            var world = TestWorld.CreateWorld(91231);
            var actor = world.Entities.Npcs.Values.First();
            var target = world.Entities.Npcs.Values.Skip(1).First();
            actor.CompassionTrait = 1f;
            actor.Social.GetOrCreate(target.Id).Affinity = 1f;
            actor.PendingHumanStrikeTargetId = target.Id;
            actor.PendingHumanStrikePart = BodyPart.Head;
            target.Body.Parts[BodyPart.Head] = 0.2f;

            MeleeSwing.ApplyHumanBlow(
                world, actor, target, 10f, string.Empty, "CompassionLethalityTest");

            Assert.Multiple(() =>
            {
                Assert.That(target.Body.Parts[BodyPart.Head], Is.Zero,
                    "Сострадание не должно оставлять legacy-пол части тела.");
                Assert.That(target.Body.Condition(BodyPart.Head).CriticalTrauma,
                    Is.GreaterThan(0f),
                    "Потенциально смертельный overkill был обрезан до nonlethal-урона.");
                Assert.That(actor.PendingHumanStrikeTargetId, Is.Null,
                    "Разрешённый удар должен освободить сохранённую цель замаха.");
            });
        }

        [Test]
        public void SaveLoadDuringWindup_PreservesOnlyTargetAndPart()
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
            Assert.Multiple(() =>
            {
                Assert.That(restored.PendingHumanStrikeTargetId, Is.EqualTo(target.Id));
                Assert.That(restored.PendingHumanStrikePart, Is.EqualTo(BodyPart.Pelvis));
                Assert.That(restored.StrikeLandsAtTick, Is.EqualTo(actor.StrikeLandsAtTick));
                Assert.That(restored.Mind.CombatOpponentNpcId, Is.EqualTo(target.Id));
                Assert.That(restored.Mind.ForcedMeleeWeaponId, Is.EqualTo("tool.machete"));
            });
        }
    }
}
