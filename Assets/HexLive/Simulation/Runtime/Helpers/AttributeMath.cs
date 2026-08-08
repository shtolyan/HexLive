using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// Spec §76: the ONE place an innate attribute turns into a number the sim
// multiplies by. Every function here returns EXACTLY 1.0f when
// Spec76.AttributeSpread is 0 (or Enabled is off) — that is the kill switch,
// and it is why a spread-0 soak must be tick-identical to pre-§76 HEAD.
//
// Sited beside EquipmentMath / MeleeSwing / BuildSiteMath deliberately: a
// modifier that lives in the system that consumes it is a modifier that gets
// silently duplicated in the next system. Grep this file to see the complete
// list of what being strong/fast/tough actually buys.
internal static class AttributeMath
{
    // ---- The roll -----------------------------------------------------------

    // §76.2 — POINT-BUY, not a free roll. Six raw hashes are turned into six
    // deviations about their own mean, so they sum to ZERO by construction:
    // Σ attributes = 6 × Mean for every body, at every spread. On a colony of
    // three (§75) a free roll is a lottery — one all-low girl is a third of the
    // labour force, and the soak win-rate starts tracking the seed instead of
    // the design. A fixed budget makes SPECIALISTS instead of winners and
    // losers, and keeps the colony's total capability off the seed entirely.
    //
    // Normalising by the largest deviation makes the band EXACTLY
    // [Mean − Spread, Mean + Spread]: Clamp01 never fires, so the budget is
    // exact rather than "exact until someone rolls a 0.99". It also means every
    // girl has precisely one standout attribute — which is what makes her
    // legible on the character sheet.
    public static void Roll(NPCState npc, int seed, int npcId)
    {
        if (!Spec76.Enabled || Spec76.AttributeSpread <= 0f)
        {
            // Every attribute keeps its 0.5 default ⇒ every multiplier below is
            // exactly 1.0f ⇒ the pre-§76 game, byte for byte.
            return;
        }

        var raw = new float[AttributeSet.All.Length];
        var sum = 0f;
        for (var i = 0; i < raw.Length; i++)
        {
            // Salt block (76, 7601..7606) — §74 owns 7401..7405, §53 owns 5301.
            raw[i] = MathUtil.Hash01(seed, npcId, 76, 7601 + i);
            sum += raw[i];
        }

        var mean = sum / raw.Length;
        var maxAbs = 0f;
        for (var i = 0; i < raw.Length; i++)
        {
            maxAbs = System.MathF.Max(maxAbs, System.MathF.Abs(raw[i] - mean));
        }

        if (maxAbs < 1e-4f)
        {
            // Six identical hashes: astronomically unlikely, but dividing by it
            // would be a NaN colonist. She is simply average.
            return;
        }

        for (var i = 0; i < raw.Length; i++)
        {
            npc.Attributes.Set(
                AttributeSet.All[i],
                Spec76.AttributeMean + Spec76.AttributeSpread * (raw[i] - mean) / maxAbs);
        }
    }

    // ---- Training -----------------------------------------------------------

    // §76.13: the body conditions itself. `effort` is the raw amount of the
    // taxing thing (ticks worked, damage taken, blows landed), already scaled
    // by its own Spec76 rate at the call site.
    //
    // No cap at 1.0 and no cap at the roll band: the roll is a starting hand.
    // The brake is the distance left to AttributeTrainCeiling, squared — so the
    // first points come easily and the last ones effectively never arrive. That
    // shape is also why the sheet shows a bare number and no bar: there is a
    // ceiling in the maths, but not one the player should read as a finish line.
    //
    // Only ever RAISES. Nothing in §76 takes an attribute away — a body that
    // stops working does not un-learn being strong, it is only out-paced.
    public static void Train(NPCState npc, AttributeKind kind, float effort)
    {
        if (!Spec76.Enabled || !Spec76.AttributeTrainEnabled || effort <= 0f)
        {
            return;
        }

        var ceiling = Spec76.AttributeTrainCeiling;
        var current = npc.Attributes.Get(kind);
        if (ceiling <= 0f || current >= ceiling)
        {
            return;
        }

        var headroom = (ceiling - current) / ceiling;
        var next = current + effort * headroom * headroom;
        npc.Attributes.Set(kind, System.MathF.Min(ceiling, next));
    }

    // The attribute a finished job conditions — the same map that decides how
    // fast she does it, so the thing she is good at is the thing she trains.
    public static void TrainFromWork(NPCState npc, InteractionType type, GoalType goal, int durationTicks)
    {
        if (durationTicks > 0)
        {
            Train(npc, WorkAttribute(type, goal), Spec76.AttributeTrainPerWorkTick * durationTicks);
        }
    }

    // ---- The shape of every multiplier --------------------------------------

    // `1 + (attr − Mean) × gain`. At Mean the deviation is 0 and this is
    // exactly 1f — the float multiply is exact, not merely close.
    private static float Mult(NPCState npc, AttributeKind kind, float gain)
    {
        if (!Spec76.Enabled)
        {
            return 1f;
        }

        return 1f + (npc.Attributes.Get(kind) - Spec76.AttributeMean) * gain;
    }

    // Same, with the sign flipped: more of the attribute means LESS of the
    // thing. Used where the good outcome is a smaller number (damage taken,
    // bleeding, how long a job takes).
    private static float InverseMult(NPCState npc, AttributeKind kind, float gain)
    {
        if (!Spec76.Enabled)
        {
            return 1f;
        }

        return 1f - (npc.Attributes.Get(kind) - Spec76.AttributeMean) * gain;
    }

    private static float Skill(NPCState npc, SkillKind kind)
    {
        if (!Spec76.Enabled || !Spec76.SkillsEnabled)
        {
            return 0f;
        }

        return MathUtil.Clamp01(npc.Skills.Get(kind));
    }

    // ---- Combat -------------------------------------------------------------

    // Strength (innate) × Combat (learned). Consumed ONLY through
    // NPCState.StrikeFactor() — never call this at a damage site directly, or
    // the next combat system to be written will forget one of the two.
    public static float MeleeStrengthMult(NPCState npc) =>
        Mult(npc, AttributeKind.Strength, Spec76.MeleeDamageGain);

    public static float MeleeCombatMult(NPCState npc) =>
        1f + Skill(npc, SkillKind.Combat) * Spec76.SkillDamageGain;

    public static float MeleeDamageMult(NPCState npc) =>
        MeleeStrengthMult(npc) * MeleeCombatMult(npc);

    // Toughness. There is no max-HP in this game — body parts are hard 1.0 and
    // clamped by Clamp01 in a dozen places — so "more hit points" is modelled
    // where it is mathematically identical and structurally cheap: less damage
    // arriving. Consumed ONLY through EquipmentMath.Mitigate().
    public static float IncomingDamageMult(NPCState npc) =>
        System.MathF.Max(0f, InverseMult(npc, AttributeKind.Toughness, Spec76.IncomingDamageGain));

    // Agility shortens the whole windup→hit→cooldown cycle (§29C.3).
    public static float AttackCooldownMult(NPCState npc) =>
        System.MathF.Max(0.1f, InverseMult(npc, AttributeKind.Agility, Spec76.AttackCooldownGain));

    // ---- Movement -----------------------------------------------------------

    public static float MoveSpeedMult(NPCState npc) =>
        Mult(npc, AttributeKind.Agility, Spec76.MoveSpeedGain);

    public static float TurnSpeedMult(NPCState npc) =>
        Mult(npc, AttributeKind.Agility, Spec76.TurnSpeedGain);

    // §71 sprint reserve: the endurant girl spends breath slower.
    public static float BreathDrainMult(NPCState npc) =>
        System.MathF.Max(0f, InverseMult(npc, AttributeKind.Endurance, Spec76.BreathGain));

    // ---- Body upkeep --------------------------------------------------------

    // Endurance raises the stamina ceiling and slows the work drain.
    public static float StaminaCeilingMult(NPCState npc) =>
        Mult(npc, AttributeKind.Endurance, Spec76.StaminaCeilingGain);

    public static float StaminaDrainMult(NPCState npc) =>
        System.MathF.Max(0f, InverseMult(npc, AttributeKind.Endurance, Spec76.StaminaDrainGain));

    // Hardiness — "she can go longer without". Scales the hunger/thirst
    // metabolism term and the sleep drain: the whole "не есть, не пить, не
    // спать" axis in one attribute.
    public static float MetabolismMult(NPCState npc) =>
        System.MathF.Max(0f, InverseMult(npc, AttributeKind.Hardiness, Spec76.MetabolismGain));

    // Hardiness again: how hard heat and cold press on her.
    public static float ThermalPressureMult(NPCState npc) =>
        System.MathF.Max(0f, InverseMult(npc, AttributeKind.Hardiness, Spec76.ThermalToleranceGain));

    // Endurance: how fast the energy need drains — the "can stay up all night"
    // half of the stat.
    public static float EnergyDrainMult(NPCState npc) =>
        System.MathF.Max(0f, InverseMult(npc, AttributeKind.Endurance, Spec76.StaminaDrainGain));

    // ---- Injury -------------------------------------------------------------

    // Toughness: wounds close and flesh knits faster. This is the PATIENT's own
    // constitution and nothing else — a healer's Medicine is a separate axis,
    // applied at the treat site (TreatPowerMult), so the two never stack here
    // by accident.
    public static float HealRateMult(NPCState npc) =>
        Mult(npc, AttributeKind.Toughness, Spec76.HealRateGain);

    public static float BleedMult(NPCState npc) =>
        System.MathF.Max(0f, InverseMult(npc, AttributeKind.Toughness, Spec76.BleedGain));

    // §105: насколько дольше ОНА держится на грани. Кровь и разбитая грудь —
    // это Стойкость (та же ось, что заживление и свёртываемость); голод и
    // жажда — Неприхотливость, «она может дольше не есть и не пить».
    // Умножает ОКНО, поэтому больше — лучше, и обе ветки берут прямой Mult.
    public static float DyingHoldMult(NPCState npc, DyingCause cause)
    {
        if (!Spec105.DyingEnabled)
        {
            return 1f;
        }

        var kind = cause switch
        {
            DyingCause.Starvation or DyingCause.Dehydration => AttributeKind.Hardiness,
            _ => AttributeKind.Toughness
        };

        // Пол на четверти окна: даже самая хилая девушка успевает упасть и
        // побыть спасаемой, иначе «умирает» вырождается обратно в мгновенную
        // смерть на нижнем краю разброса.
        return System.MathF.Max(0.25f, Mult(npc, kind, Spec105.HoldGain));
    }

    // The healer's skill, applied to relief delivered to someone else (or to
    // herself via §68 self-treat).
    // §105 r3: до какого уровня ЭТИ руки вообще могут довести зону. Новичок
    // латает до Spec53.TreatCapNovice, мастер — до единицы; между ними прямая.
    //
    // Со снятыми навыками потолка нет: он выражает УМЕНИЕ, и без системы
    // умений ему не на чем стоять — кил-свитч §76 обязан возвращать
    // до-§105-r3 поведение, а не запирать всех на потолке новичка.
    public static float TreatCap(NPCState healer)
    {
        if (!Spec76.Enabled || !Spec76.SkillsEnabled)
        {
            return 1f;
        }

        var novice = MathUtil.Clamp01(Spec53.TreatCapNovice);
        return novice + (1f - novice) * Skill(healer, SkillKind.Medicine);
    }

    public static float TreatPowerMult(NPCState npc) =>
        1f + Skill(npc, SkillKind.Medicine) * Spec76.SkillHealGain;

    public static float SocialGainMult(NPCState npc) =>
        1f + Skill(npc, SkillKind.Social) * Spec76.SkillSocialGain;

    // ---- Carrying -----------------------------------------------------------

    // A band, not a curve: slots are integers, so a strong girl gets exactly
    // one more pocket and that is the whole mechanic.
    public static int CarrySlotBonus(NPCState npc)
    {
        if (!Spec76.Enabled)
        {
            return 0;
        }

        return npc.Attributes.Strength >= Spec76.CarrySlotThreshold ? 1 : 0;
    }

    // ---- Work ---------------------------------------------------------------

    // How long a job takes, as a multiplier on its authored DurationTicks.
    // Below 1 = faster. Two independent terms: the innate attribute the job
    // leans on, and the learned trade.
    //
    // NOTE the caller contract: the two stages stay SEPARATE — the gear re-paces
    // the authored ticks first (GearCatalog.ScaleTicks), and this multiplies the
    // whole tick count that came out. Folding them into one division would make
    // the hands and the tool trade rounding with each other, and neither could
    // be bisected on its own. (§79 made the gear stage round rather than
    // truncate; before that anything between 1 and 2 collapsed to no bonus.)
    public static float WorkDurationMult(NPCState npc, InteractionType type, GoalType goal)
    {
        if (!Spec76.Enabled)
        {
            return 1f;
        }

        var attribute = WorkAttribute(type, goal);
        var mult = InverseMult(npc, attribute, Spec76.WorkSpeedGain);

        var skill = SkillMath.For(type, goal);
        if (skill.HasValue)
        {
            mult *= 1f - Skill(npc, skill.Value) * Spec76.SkillWorkSpeedGain;
        }

        // A job can never take less than a fifth of its authored time, however
        // gifted she is — the animations and the §61 staged craft read as
        // broken below that.
        return System.MathF.Max(0.2f, mult);
    }

    // ---- Perks --------------------------------------------------------------

    // §76.6: an attribute at either end of the band earns a named badge — a
    // gift at the top, a flaw at the bottom. Resolved SIM-SIDE into finished
    // localization keys ("perk.strength.high") because the bands are Spec76
    // tuning knobs: a view that re-derived them would quietly disagree with the
    // simulation the moment either was moved.
    //
    // Deliberately not EffectKinds — every effect in the catalog is a transient
    // classification and the panel draws one chip per entry, so twelve
    // permanent chips would swamp the row and change what the chip row MEANS.
    // The §76 character sheet is the visibility surface these get (§48.6).
    public static void CollectPerks(NPCState npc, System.Collections.Generic.List<string> results)
    {
        if (!Spec76.Enabled)
        {
            return;
        }

        foreach (var kind in AttributeSet.All)
        {
            var value = npc.Attributes.Get(kind);
            if (value >= Spec76.PerkHighBand)
            {
                results.Add($"perk.{kind.ToString().ToLowerInvariant()}.high");
            }
            else if (value <= Spec76.PerkLowBand)
            {
                results.Add($"perk.{kind.ToString().ToLowerInvariant()}.low");
            }
        }
    }

    // ---- Work ---------------------------------------------------------------

    // The caller-facing form: an already-computed tick count in, a tick count
    // out. Every duration site should go through THIS rather than doing its own
    // rounding, so the "never float-divide, always multiply-then-round" rule is
    // stated once and cannot drift.
    //
    // The Math.Max(1, …) floor is load-bearing, not defensive: `total =
    // EndTick − StartTick` is the denominator ExecutionSystem uses to spread an
    // interaction's need effects across its run.
    public static int WorkTicks(NPCState npc, int baseTicks, InteractionType type, GoalType goal)
    {
        if (baseTicks <= 0)
        {
            return baseTicks;
        }

        return System.Math.Max(1, (int)System.MathF.Round(
            baseTicks * WorkDurationMult(npc, type, goal)));
    }

    // Which attribute a verb leans on. Swinging an axe into a palm is Strength;
    // whittling a spear is Wits; walking a bottle to the sea is neither, so it
    // falls to Endurance (staying on your feet all day IS the work).
    private static AttributeKind WorkAttribute(InteractionType type, GoalType goal)
    {
        switch (type)
        {
            case InteractionType.Harvest:
            case InteractionType.Process:
            case InteractionType.Build:
            case InteractionType.Butcher:
                return AttributeKind.Strength;

            case InteractionType.Craft:
                return AttributeKind.Wits;

            case InteractionType.TreatSelf:
            case InteractionType.TreatOther:
            case InteractionType.MedicateOther:
                return AttributeKind.Wits;

            default:
                return AttributeKind.Endurance;
        }
    }
}

}
