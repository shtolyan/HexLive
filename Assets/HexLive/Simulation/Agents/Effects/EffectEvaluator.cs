using System.Collections.Generic;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Agents.Effects
{
    // Spec §48: the classifier. Given a survivor's live state it emits the set
    // of status effects she is under right now. It only ever READS — every
    // threshold here decides which ICON shows, never what the simulation does,
    // so the tuned balance in SimulationSystems is untouched. The thresholds
    // mirror the points where the sim's own logic already bites (bleeding when
    // a fresh wound sits on a part below 0.4, HP-draining heat/cold at |0.85|,
    // the starving/dehydrated hysteresis flags, the winded floor at 0.15) so
    // an icon appears exactly when the underlying consequence kicks in.
    public static class EffectEvaluator
    {
        // Classification cut-points (UI only — NOT simulation tuning).
        private const float FreshWound = 0.3f;      // Heal01 below this = still open
        private const float HurtPart = 0.4f;        // part HP that starts bleeding
        private const float InjuredPart = 0.6f;     // a part this low reads as "injured"
        private const float LegHobble = 0.6f;       // leg HP that reads as "hobbled"
        private const float ThermalDanger = 0.85f;  // |ThermalComfort| that drains HP
        private const float ThermalMild = 0.4f;     // |ThermalComfort| worth flagging
        // Over 0.5 is where the sim starts docking Comfort and the skin burns,
        // then it caps at 1.0 (an HP burn) and RESETS to 0.5 — so the useful
        // "baking dangerously" window is > 0.5, not the momentary 1.0 spike.
        private const float SunOverexposed = 0.5f;
        private const float HighUv = 0.6f;          // effective UV the panel calls "high"
        private const float SunburnShow = 0.3f;     // Sunburn redness worth flagging
        private const float WetShow = 0.5f;         // worn Wetness that kills warmth
        private const float WindedFloor = 0.15f;    // stamina floor = winded
        private const float StressShow = 0.6f;      // stress climbing toward collapse
        private const float FilthyShow = 0.3f;      // hygiene this low = grubby
        private const float LonelyShow = 0.2f;      // social this low = lonely
        private const float MiserableShow = 0.2f;   // comfort this low = miserable
        private const float WellFedShow = 0.15f;    // hunger this low = freshly full
        private const float RestedShow = 0.85f;     // energy AND stamina above = rested
        private const float ContentShow = 0.8f;     // comfort above = at ease

        // Collect the active effects for one NPC into <paramref name="results"/>
        // (cleared first). <paramref name="currentTick"/> resolves the timed
        // knocked-out / grieving windows; <paramref name="effectiveUv"/> is the
        // UV actually hitting her now (0 indoors/in water/at night, shade-cut)
        // — the same value the panel shows, computed by the caller.
        // <paramref name="nearLitFire"/> is set by the caller when a burning
        // campfire is within warming range (spec §49.8) — it drives the Cozy buff.
        public static void Collect(
            NPCState npc, int currentTick, float effectiveUv, bool nearLitFire,
            bool restingInBed,
            List<ActiveEffect> results)
        {
            results.Clear();
            if (npc == null)
            {
                return;
            }

            var needs = npc.Needs;

            // ── Injury / blood ────────────────────────────────────────────
            // Bleeding takes precedence over the milder "injured": a fresh
            // wound on a mauled part is what actually drains Blood in the sim.
            var bleeding = false;
            var maxWoundSeverity = 0f;
            foreach (var wound in npc.Wounds)
            {
                if (wound.Heal01 >= FreshWound)
                {
                    continue;
                }

                var partHp = npc.Body.Parts.TryGetValue(wound.Zone, out var hp) ? hp : 1f;
                if (partHp < HurtPart)
                {
                    bleeding = true;
                }

                var sev = wound.Severity * (1f - wound.Heal01);
                if (sev > maxWoundSeverity)
                {
                    maxWoundSeverity = sev;
                }
            }

            if (bleeding)
            {
                results.Add(new ActiveEffect(EffectKind.Bleeding, 1f - needs.Blood));
            }
            else if (npc.Wounds.Count > 0 || WorstPart(npc) < InjuredPart)
            {
                results.Add(new ActiveEffect(EffectKind.Injured, Clamp01(1f - WorstPart(npc))));
            }

            // Dressed wound: the bleed is stopped and it's closing (buff).
            if (npc.BandagedZones.Count > 0 || npc.GauzeZones.Count > 0)
            {
                results.Add(new ActiveEffect(EffectKind.Bandaged, 1f));
            }

            var minLeg = Min(Part(npc, BodyPart.LegL), Part(npc, BodyPart.LegR));
            if (minLeg < LegHobble)
            {
                results.Add(new ActiveEffect(EffectKind.Hobbled, 1f - minLeg));
            }

            // Spec §50: a limb gone for good — reads the stored Severed set, so
            // like Hobbled it's a pure classification (the sim already applied
            // the consequence when the limb came off).
            if (npc.Body.AnySevered)
            {
                results.Add(new ActiveEffect(EffectKind.Maimed, 1f));
            }

            if (currentTick < npc.Mind.AdrenalineUntilTick)
            {
                results.Add(new ActiveEffect(EffectKind.Adrenaline, 1f));
            }

            // ── Environment: sun & temperature ────────────────────────────
            // Standing under a high UV index right now — an at-a-glance warning
            // before the exposure even builds (shade/water/indoors read 0 UV).
            if (effectiveUv >= HighUv)
            {
                results.Add(new ActiveEffect(EffectKind.StrongSun, effectiveUv));
            }

            if (npc.SunExposure > SunOverexposed)
            {
                results.Add(new ActiveEffect(EffectKind.Sunstroke, npc.SunExposure));
            }

            if (needs.Sunburn > SunburnShow)
            {
                results.Add(new ActiveEffect(EffectKind.Sunburnt, needs.Sunburn));
            }

            // Two-tier thermal: the mild Hot/Cold flags the discomfort early,
            // the severe Heatstroke/Freezing marks the HP-draining edge.
            if (needs.ThermalComfort >= ThermalDanger)
            {
                results.Add(new ActiveEffect(EffectKind.Heatstroke, needs.ThermalComfort));
            }
            else if (needs.ThermalComfort >= ThermalMild)
            {
                results.Add(new ActiveEffect(EffectKind.Hot, needs.ThermalComfort));
            }
            else if (needs.ThermalComfort <= -ThermalDanger)
            {
                results.Add(new ActiveEffect(EffectKind.Freezing, -needs.ThermalComfort));
            }
            else if (needs.ThermalComfort <= -ThermalMild)
            {
                results.Add(new ActiveEffect(EffectKind.Cold, -needs.ThermalComfort));
            }

            var maxWetness = 0f;
            foreach (var item in npc.WornItems)
            {
                if (item.Wetness > maxWetness)
                {
                    maxWetness = item.Wetness;
                }
            }

            if (maxWetness > WetShow)
            {
                results.Add(new ActiveEffect(EffectKind.Soaked, maxWetness));
            }

            // Spec §49.8: sat by a lit campfire — its glow slowly tops up comfort
            // (the sim gives a small awake trickle, more if she sleeps by it), and
            // the panel shows a cheery "Cozy" chip so the warmth reads at a glance.
            if (nearLitFire)
            {
                results.Add(new ActiveEffect(EffectKind.Cozy, 1f));
            }

            // ── Survival needs at the danger edge ─────────────────────────
            // Spec §60: a coma outranks the short faint — one "out cold" chip
            // at a time, the deeper one.
            var comatose = npc.Mind.ComaCause != AI.ComaCause.None;
            if (comatose)
            {
                results.Add(new ActiveEffect(EffectKind.Coma, 1f));
            }

            var fainted = !comatose && currentTick < npc.Mind.FaintedUntilTick;
            if (fainted)
            {
                results.Add(new ActiveEffect(EffectKind.Fainted, 1f));
            }

            if (npc.Mind.IsStarving)
            {
                results.Add(new ActiveEffect(EffectKind.Starving, needs.Hunger));
            }

            if (npc.Mind.IsDehydrated)
            {
                results.Add(new ActiveEffect(EffectKind.Dehydrated, needs.Thirst));
            }

            // Spec §49: gut-rot from raw water — shown for the whole DoT window
            // (Mind.SickUntilTick), not the instant it was drunk.
            if (currentTick < npc.Mind.SickUntilTick)
            {
                results.Add(new ActiveEffect(EffectKind.Sick, 1f));
            }

            // Winded reads only while conscious — a faint already says it louder.
            if (!fainted && !comatose && needs.Stamina < WindedFloor)
            {
                results.Add(new ActiveEffect(EffectKind.Exhausted, 1f - needs.Stamina / WindedFloor));
            }

            if (needs.Hunger < WellFedShow)
            {
                results.Add(new ActiveEffect(EffectKind.WellFed, 1f - needs.Hunger / WellFedShow));
            }

            if (needs.Energy > RestedShow && needs.Stamina > RestedShow)
            {
                results.Add(new ActiveEffect(EffectKind.Rested, Min(needs.Energy, needs.Stamina)));
            }

            // §54.11: asleep in a bed she (or a housemate) built — the bed's bonus
            // is speeding her recovery, so the panel shows a "Snug" buff. The
            // caller sets restingInBed only while she's actually lying on a bed.
            if (restingInBed)
            {
                results.Add(new ActiveEffect(EffectKind.Snug, 1f));
            }

            // ── Mind & wellbeing ──────────────────────────────────────────
            if (currentTick < npc.Mind.GrievingUntilTick)
            {
                results.Add(new ActiveEffect(EffectKind.Grieving, 1f));
            }

            if (needs.Stress >= StressShow)
            {
                results.Add(new ActiveEffect(EffectKind.Stressed, needs.Stress));
            }

            if (needs.Social < LonelyShow)
            {
                results.Add(new ActiveEffect(EffectKind.Lonely, 1f - needs.Social / LonelyShow));
            }

            if (needs.Comfort < MiserableShow)
            {
                results.Add(new ActiveEffect(EffectKind.Miserable, 1f - needs.Comfort / MiserableShow));
            }
            else if (needs.Comfort > ContentShow)
            {
                results.Add(new ActiveEffect(EffectKind.Content, needs.Comfort));
            }

            // ── Hygiene ───────────────────────────────────────────────────
            if (needs.Hygiene < FilthyShow)
            {
                results.Add(new ActiveEffect(EffectKind.Filthy, 1f - needs.Hygiene / FilthyShow));
            }
        }

        private static float Part(NPCState npc, BodyPart part) =>
            npc.Body.Parts.TryGetValue(part, out var v) ? v : 1f;

        private static float WorstPart(NPCState npc)
        {
            var worst = 1f;
            foreach (var v in npc.Body.Parts.Values)
            {
                if (v < worst)
                {
                    worst = v;
                }
            }

            return worst;
        }

        private static float Min(float a, float b) => a < b ? a : b;

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
