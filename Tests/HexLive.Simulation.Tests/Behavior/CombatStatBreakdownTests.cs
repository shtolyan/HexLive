using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
    public sealed class CombatStatBreakdownTests
    {
        [Test]
        public void DamageAndCadence_UseWeaponSheetAndAllActorMultipliers()
        {
            const float limb = 0.75f;
            const float strength = 1.20f;
            const float combat = 1.10f;
            const float agilityRecovery = 0.80f;

            var stats = CombatStatBreakdown.For(
                GearCatalog.Machete, limb, strength, combat, agilityRecovery);
            var gear = GearCatalog.For(GearCatalog.Machete);
            gear.StrikeTimings(-1, out var hitDelay, out var duration, out var cooldown);

            Assert.That(stats.BaseDamage, Is.EqualTo(GearCatalog.Damage(GearCatalog.Machete)));
            Assert.That(stats.EffectiveDamage,
                Is.EqualTo(stats.BaseDamage * limb * strength * combat).Within(0.0001f));
            Assert.That(stats.RecoverySeconds,
                Is.EqualTo((duration - hitDelay + cooldown) * agilityRecovery).Within(0.0001f));
            Assert.That(stats.CycleSeconds,
                Is.EqualTo(hitDelay + stats.RecoverySeconds).Within(0.0001f));
            Assert.That(stats.AttacksPerMinute,
                Is.EqualTo(60f / stats.CycleSeconds).Within(0.0001f));
            Assert.That(stats.TwoHanded, Is.False);
            Assert.That(stats.Capabilities.HasFlag(GearCapability.Cut), Is.True);
        }

        [Test]
        public void SpearCardReportsTwoHandedWithoutReadingLegacyAttackSpeed()
        {
            var stats = CombatStatBreakdown.For(GearCatalog.Spear, 1f, 1f, 1f, 1f);

            Assert.That(stats.TwoHanded, Is.True);
            Assert.That(stats.CycleSeconds, Is.GreaterThan(stats.HitDelaySeconds));
            Assert.That(stats.AttacksPerMinute, Is.GreaterThan(0f));
        }
    }
}
