using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
    /// <summary>Deterministic mercy decision captured at human wind-up.</summary>
    internal readonly struct HumanStrikeDecision
    {
        public readonly BodyPart Part;
        public readonly float KillIntent;
        public readonly bool KillAuthorized;
        public readonly bool PotentiallyFatal;

        private HumanStrikeDecision(
            BodyPart part, float killIntent, bool killAuthorized, bool potentiallyFatal)
        {
            Part = part;
            KillIntent = killIntent;
            KillAuthorized = killAuthorized;
            PotentiallyFatal = potentiallyFatal;
        }

        public static HumanStrikeDecision Choose(
            WorldState world, NPCState attacker, NPCState target,
            float rawDamage, string weaponId)
        {
            var part = AmputateSystemHelpers.RedirectFromStump(
                target, MeleeSwing.PickHumanPart(world, attacker.Id.Value, target));
            var landed = BodyDamageResolver.PreviewLanded(world, target, part, rawDamage);
            var fatal = IsPotentiallyFatal(target, part, landed);
            var intent = CalculateKillIntent(attacker, target);
            var authorized = !Spec86.MercyEnabled || !fatal ||
                intent >= Spec86.KillIntentThreshold;
            return new HumanStrikeDecision(part, intent, authorized, fatal);
        }

        public static float CalculateKillIntent(NPCState attacker, NPCState target)
        {
            var hatred = MathUtil.Clamp01(-attacker.Social.GetOrCreate(target.Id).Affinity);
            return hatred * (1f - MathUtil.Clamp01(attacker.CompassionTrait));
        }

        public static bool IsPotentiallyFatal(NPCState target, BodyPart part, float landed)
        {
            landed = System.Math.Max(0f, landed);
            var destroysPart = target.Body.Parts[part] - landed <= 0f;
            if (BodyState.IsVital(part) && destroysPart)
            {
                return true;
            }

            // The current body model records overkill below zero as critical
            // depth. Reaching one is immediate death even if the displayed
            // vital bar was already pinned at its floor.
            if (BodyState.IsVital(part))
            {
                var overkill = System.Math.Max(0f, landed - target.Body.Parts[part]);
                if (target.Body.Condition(part).CriticalTrauma + overkill >= 1f)
                {
                    return true;
                }
            }

            return target.IsDying &&
                landed * Spec105.DamageReserveFactor >= target.Mind.DyingReserve;
        }

        public static float CapNonLethal(
            NPCState target, BodyPart part, float landed)
        {
            landed = System.Math.Max(0f, landed);

            var healthRoom = target.Health <= Spec86.MercyHealthFloor
                ? 0f
                : (target.Health - Spec86.MercyHealthFloor) * target.Body.Parts.Count;
            landed = System.Math.Min(landed, healthRoom);

            var partRoom = System.Math.Max(
                0f, target.Body.Parts[part] - Spec86.MercyPartFloor);
            landed = System.Math.Min(landed, partRoom);

            if (target.IsDying && Spec105.DamageReserveFactor > 0f)
            {
                var reserveRoom = System.Math.Max(
                    0f, target.Mind.DyingReserve - 0.001f) /
                    Spec105.DamageReserveFactor;
                landed = System.Math.Min(landed, reserveRoom);
            }

            // §116 records overkill as critical depth. A spared vital part may
            // approach the death boundary but never cross it on this blow.
            if (part is BodyPart.Head or BodyPart.Torso or BodyPart.Pelvis)
            {
                var criticalRoom = target.Body.Parts[part] + System.Math.Max(
                    0f, 0.999f - target.Body.Condition(part).CriticalTrauma);
                landed = System.Math.Min(landed, criticalRoom);
            }

            return System.Math.Max(0f, landed);
        }
    }
}
