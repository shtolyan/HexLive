using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{
    /// <summary>
    /// Pure, target-independent melee numbers. Both MeleeSwing and the item
    /// card consume this exact calculation; target armor is intentionally not
    /// part of it.
    /// </summary>
    public readonly struct CombatStatBreakdown
    {
        public readonly float BaseDamage;
        public readonly float LimbMultiplier;
        public readonly float StrengthMultiplier;
        public readonly float CombatMultiplier;
        public readonly float EffectiveDamage;
        public readonly float CutFraction;
        public readonly float BloodLossMultiplier;
        public readonly float BaseCutDamage;
        public readonly float BaseBluntDamage;
        public readonly float EffectiveCutDamage;
        public readonly float EffectiveBluntDamage;
        public readonly float InstantBloodLoss;
        public readonly float HitDelaySeconds;
        public readonly float AttackDurationSeconds;
        public readonly float AgilityRecoveryMultiplier;
        public readonly float RecoverySeconds;
        public readonly float CycleSeconds;
        public readonly float AttacksPerMinute;
        public readonly float DamagePerSecond;
        public readonly bool TwoHanded;
        public readonly GearCapability Capabilities;

        private CombatStatBreakdown(
            float baseDamage,
            float limbMultiplier,
            float strengthMultiplier,
            float combatMultiplier,
            float hitDelaySeconds,
            float attackDurationSeconds,
            float cooldownSeconds,
            float agilityRecoveryMultiplier,
            float cutFraction,
            float bloodLossMultiplier,
            bool twoHanded,
            GearCapability capabilities)
        {
            BaseDamage = baseDamage;
            LimbMultiplier = limbMultiplier;
            StrengthMultiplier = strengthMultiplier;
            CombatMultiplier = combatMultiplier;
            EffectiveDamage = baseDamage * limbMultiplier * strengthMultiplier * combatMultiplier;
            CutFraction = cutFraction;
            BloodLossMultiplier = bloodLossMultiplier;
            BaseCutDamage = baseDamage * CutFraction;
            BaseBluntDamage = baseDamage - BaseCutDamage;
            EffectiveCutDamage = EffectiveDamage * CutFraction;
            EffectiveBluntDamage = EffectiveDamage - EffectiveCutDamage;
            InstantBloodLoss = EffectiveCutDamage * BloodLossMultiplier *
                Spec118.InstantBloodLossFactor;
            HitDelaySeconds = hitDelaySeconds;
            AttackDurationSeconds = attackDurationSeconds;
            AgilityRecoveryMultiplier = agilityRecoveryMultiplier;
            RecoverySeconds = System.Math.Max(0f,
                (attackDurationSeconds - hitDelaySeconds + cooldownSeconds) *
                agilityRecoveryMultiplier);
            CycleSeconds = System.Math.Max(0.0001f, hitDelaySeconds + RecoverySeconds);
            AttacksPerMinute = 60f / CycleSeconds;
            DamagePerSecond = EffectiveDamage / CycleSeconds;
            TwoHanded = twoHanded;
            Capabilities = capabilities;
        }

        internal static CombatStatBreakdown For(NPCState actor, string weaponId, int strikeIndex = -1) =>
            For(
                weaponId,
                actor.Body.LimbStrikeFactor(),
                AttributeMath.MeleeStrengthMult(actor),
                AttributeMath.MeleeCombatMult(actor),
                AttributeMath.AttackCooldownMult(actor),
                strikeIndex);

        public static CombatStatBreakdown For(
            string weaponId,
            float limbMultiplier,
            float strengthMultiplier,
            float combatMultiplier,
            float agilityRecoveryMultiplier,
            int strikeIndex = -1)
        {
            var gear = GearCatalog.For(weaponId);
            gear.StrikeTimings(strikeIndex, out var hitDelay, out var duration, out var cooldown);
            return new CombatStatBreakdown(
                GearCatalog.Damage(weaponId),
                limbMultiplier,
                strengthMultiplier,
                combatMultiplier,
                hitDelay,
                duration,
                cooldown,
                agilityRecoveryMultiplier,
                gear.CutFraction,
                gear.BloodLossMultiplier,
                gear.TwoHanded,
                gear.Capabilities);
        }
    }
}
