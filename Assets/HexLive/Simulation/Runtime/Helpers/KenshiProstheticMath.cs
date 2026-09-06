using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§116 splint and prosthetic eligibility, supplies and outcomes.</summary>
internal static class KenshiProstheticMath
{
    private static readonly BodyPart[] Limbs =
    {
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    internal static bool TryFindSplintPart(NPCState patient, out BodyPart part)
    {
        part = BodyPart.ArmL;
        var worst = float.MaxValue;
        var found = false;
        foreach (var limb in Limbs)
        {
            var condition = patient.Body.Condition(limb);
            if (patient.Body.IsSevered(limb) || condition.SplintSupport > 0f ||
                (condition.CriticalTrauma <= 0f && patient.Body.Parts[limb] >= 0.5f) ||
                HasUnstabilizedWound(patient, limb))
            {
                continue;
            }

            var effective = patient.Body.Parts[limb] - condition.CriticalTrauma;
            if (effective < worst)
            {
                worst = effective;
                part = limb;
                found = true;
            }
        }

        return found;
    }

    /// <summary>§121.9: есть ли вообще отсечённая конечность — чистый вопрос о
    /// ТЕЛЕ, без кровати и припасов. Нужен ручному приказу, чтобы отличить
    /// честное «нечем/не так лежит» (NoSupplies) от «нечего лечить»
    /// (NoLimbDamage).</summary>
    internal static bool HasSeveredLimb(NPCState patient)
    {
        foreach (var limb in Limbs)
        {
            if (patient.Body.IsSevered(limb))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryFindProstheticPart(
        WorldState world, NPCState helper, NPCState patient,
        out BodyPart part, out string itemId, out bool repair)
    {
        part = BodyPart.ArmL;
        itemId = string.Empty;
        repair = false;

        if (!TryPatientBed(world, patient, out var bed))
        {
            return false;
        }

        foreach (var limb in Limbs)
        {
            if (!patient.Body.IsSevered(limb) || HasUnstabilizedWound(patient, limb))
            {
                continue;
            }

            var device = patient.Body.Condition(limb).Prosthetic;
            if (device is not null)
            {
                if (device.Condition >= device.MaxCondition - 0.001f ||
                    !HasRepairSupplies(helper, device.Mechanical))
                {
                    continue;
                }

                part = limb;
                itemId = device.DefinitionId;
                repair = true;
                return true;
            }

            var arm = limb is BodyPart.ArmL or BodyPart.ArmR;
            var mechanical = arm ? ContentIds.MechanicalArm : ContentIds.MechanicalLeg;
            var wooden = arm ? ContentIds.WoodenArm : ContentIds.WoodenLeg;
            if (bed.DefinitionId == ContentIds.BedBasic && HasItem(helper, mechanical))
            {
                part = limb;
                itemId = mechanical;
                return true;
            }

            if (HasItem(helper, wooden))
            {
                part = limb;
                itemId = wooden;
                return true;
            }
        }

        return false;
    }

    internal static bool TryPatientBed(
        WorldState world, NPCState patient, out WorldObjectState bed)
    {
        bed = null;
        return patient.Execution.CurrentInteraction == InteractionType.Sleep &&
            patient.Execution.TargetObject is { } objectId &&
            world.Entities.Objects.TryGetValue(objectId, out bed) &&
            KenshiRescueMath.IsBed(bed);
    }

    internal static int TreatmentTicks(NPCState helper, GoalType goal, string itemId)
    {
        if (goal == GoalType.Splint)
        {
            return WoundMath.BandageTicks(helper);
        }

        return IsMechanical(itemId)
            ? Spec118.MechanicalProstheticInstallTicks
            : Spec118.WoodenProstheticInstallTicks;
    }

    internal static bool ApplySplint(
        WorldState world, NPCState helper, NPCState patient, BodyPart part)
    {
        if (!helper.Inventory.Items.Remove(ContentIds.Splint) ||
            patient.Body.IsSevered(part) || HasUnstabilizedWound(patient, part))
        {
            return false;
        }

        // §50: шина на ногу с нулевой функцией тоже поднимает лежачую — значит
        // и ей нужно время на клип вставания (см. MortalityHelpers).
        var wasProne = patient.Body.IsProne;
        var medicine = MathUtil.Clamp01(helper.Skills.Medicine);
        patient.Body.Condition(part).SplintSupport =
            Spec118.SplintSupportNovice +
            (Spec118.SplintSupportExpert - Spec118.SplintSupportNovice) * medicine;
        MortalityHelpers.GrantStandUpGrace(world, patient, wasProne);
        return true;
    }

    internal static bool FitOrRepair(
        WorldState world, NPCState helper, NPCState patient,
        BodyPart part, string itemId, bool repair)
    {
        if (!TryPatientBed(world, patient, out var bed) ||
            !patient.Body.IsSevered(part) || HasUnstabilizedWound(patient, part))
        {
            return false;
        }

        var condition = patient.Body.Condition(part);
        // §50: лежала ли она ДО починки/установки. Нога, которая снова держит,
        // поднимает тело с земли — а подъём это клип, и ему нужно время (см.
        // GrantStandUpGrace ниже).
        var wasProne = patient.Body.IsProne;
        if (repair)
        {
            if (condition.Prosthetic is not { } existing ||
                !SpendRepairSupplies(helper, existing.Mechanical))
            {
                return false;
            }

            existing.Condition = existing.MaxCondition;
            MortalityHelpers.GrantStandUpGrace(world, patient, wasProne);
            return true;
        }

        var mechanical = IsMechanical(itemId);
        if (mechanical && bed.DefinitionId != ContentIds.BedBasic)
        {
            return false;
        }

        if (!helper.Inventory.Items.Remove(itemId))
        {
            return false;
        }

        condition.Prosthetic = AdminProsthetics.CreateDevice(part, itemId);
        EquipmentMath.RecalculateCapacity(world, patient);
        MortalityHelpers.GrantStandUpGrace(world, patient, wasProne);
        return true;
    }

    internal static bool HasUnstabilizedWound(NPCState patient, BodyPart part)
    {
        foreach (var wound in patient.Wounds)
        {
            if (wound.Zone == part && wound.Heal01 < 1f && !wound.Stabilized)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRepairSupplies(NPCState helper, bool mechanical) =>
        mechanical
            ? HasItem(helper, ContentIds.MechanicalPart)
            : HasItem(helper, ContentIds.Board) && HasItem(helper, ContentIds.Rope);

    private static bool SpendRepairSupplies(NPCState helper, bool mechanical)
    {
        if (mechanical)
        {
            return helper.Inventory.Items.Remove(ContentIds.MechanicalPart);
        }

        if (!HasRepairSupplies(helper, mechanical: false))
        {
            return false;
        }

        helper.Inventory.Items.Remove(ContentIds.Board);
        helper.Inventory.Items.Remove(ContentIds.Rope);
        return true;
    }

    internal static bool HasItem(NPCState npc, string definitionId) =>
        npc.Inventory.Items.Contains(definitionId);

    /// <summary>
    /// A pledged helper who already has the replacement must prepare the
    /// conscious prone patient on a bed instead of waiting for an unrelated
    /// sleep cycle. Installation itself still has the stricter bed/stump gates.
    /// </summary>
    internal static bool TryGetPledgedBedTransport(
        WorldState world, NPCState helper, NPCState patient,
        out BodyPart part, out string itemId)
    {
        part = BodyPart.ArmL;
        itemId = string.Empty;
        if (helper.Mind.ProstheticAidTargetId != patient.Id ||
            helper.Mind.ProstheticAidPart is not { } pledgedPart ||
            patient.Health <= 0f || patient.IsBeingCarried ||
            !patient.Body.IsSevered(pledgedPart) ||
            patient.Body.Condition(pledgedPart).Prosthetic is not null ||
            HasUnstabilizedWound(patient, pledgedPart) ||
            TryPatientBed(world, patient, out _))
        {
            return false;
        }

        var arm = pledgedPart is BodyPart.ArmL or BodyPart.ArmR;
        var wooden = arm ? ContentIds.WoodenArm : ContentIds.WoodenLeg;
        var mechanical = arm ? ContentIds.MechanicalArm : ContentIds.MechanicalLeg;
        if (HasItem(helper, wooden))
        {
            part = pledgedPart;
            itemId = wooden;
            return true;
        }

        if (HasItem(helper, mechanical))
        {
            part = pledgedPart;
            itemId = mechanical;
            return true;
        }

        return false;
    }

    internal static bool IsMechanical(string itemId) =>
        itemId is ContentIds.MechanicalArm or ContentIds.MechanicalLeg;
}

}
