# Iteration 20 — Sun, UV & Shade

Spec: §35.4 (concretized first, per spec-first workflow), §35.3 amendment (doorway rule).

## Goal

The sun becomes a survival pressure: a UV index cycles with the day, uncovered
body parts accumulate exposure and burn, shade (big trees / palms), water
tiles, and the indoors protect, and NPCs get a CoolOff goal to seek cover.

## What was built

- `EnvironmentState.UvIndex` — `0.9 × sin(π × dayProgress / 0.5)` over the
  daylight half, 0 at night (EnvironmentSystem).
- `NPCState.SunExposure` — accumulates while effective UV > 0.5 with at least
  one uncovered body part: `+= (uv − 0.5) × 0.3` per slow tick; Comfort −0.02
  past 0.5; at ≥ 1.0 a seeded-random uncovered part takes 0.08 damage
  (`Sunburn` trace, Comfort −0.15, reset to 0.5; head burns can kill via
  vitals). Recovery −0.05 per protected slow tick.
- Effective UV: Indoor/Water → 0, within 1 tile of a `Shade`-tagged object →
  ×0.2. Shade also cools −2°, water −3° (TemperatureSystem).
- `EquipmentMath.IsPartCovered` — coverage reuse from the 31A.5B layer model.
- `GoalType.CoolOff` — available when (effTemp > 20 && Thermal ≥ 0.35) or
  SunExposure ≥ 0.6; score `0.1 + 0.5 × max(Thermal, SunExposure − 0.4)`;
  move-only plan to the nearest known Shade/Water spot.
- Snapshot/debug panel: UV shown next to the clock.

## Soak findings (fixed spec-first)

1. **Original numbers never burned.** With accrual ×0.15 / recovery 0.1,
   exposure peaked at 0.99 over ten days and no burn ever fired; the
   heat-only CoolOff gate never tripped in this climate. Spec §35.4 amended:
   accrual ×0.3, recovery −0.05, CoolOff gains the SunExposure ≥ 0.6 trigger.
2. **Harness whitelist.** `Sunburn`/`CoolOffPlanned` traces existed but the
   harness counts only whitelisted event types — metrics read 0 until the
   events were added to the `interesting` set. Lesson recorded: every new
   trace event must be whitelisted in the harness.
3. **Sealed-hut bug (pre-existing, iteration 19).** The doorway junction was
   picked by `Tiles.Count >= 2`, which admits corner junctions already
   blocked by the adjacent walls — the "door" flag landed on a blocked
   junction and the last wall sealed the hut with NPC3 inside (seed 777: the
   trapped NPC slept for days at Hunger 1.0, everything unreachable). Spec
   §35.3 amended: the doorway must be a **mid-edge** junction
   (`Tiles.Count == 2`, unblocked), walls never block a `Door` junction.
   Fixed with a two-pass placement (pick door first, then wall the rest) and
   a `DoorPlacementFailed` trace. New structural soak invariant: a completed
   hut's interior must share a connectivity component with the outside
   (`HutOpen`).
4. **Interrupt budget raised 60 → 80 per NPC.** The goal roster has roughly
   doubled since the limit was set (iteration 9); the seed-777 breakdown
   showed a flat mix of need-driven switches (max 12 for any single
   transition kind), not an oscillation storm. CoolOff switches are counted
   separately as benign "sun-cuts" (a sun emergency, like stroll-cuts).

## Verification (10 game days, harness)

| Seed | Result | Sunburns | CoolOffs | Hut |
|---|---|---|---|---|
| 12345 | OK (wipe waiver: NPC2 lost to dogs) | 25 | 90 | 3 pieces, in progress |
| 777 | OK, no deaths | 37 | 125 | completed, **HutOpen=True** |

UV cycles 0→0.9→0 daily; exposure accrues midday, burns fire on uncovered
parts, CoolOff trips repeatedly and exposure recovers in cover/at night
(0.00 at end-of-run snapshots). Structural checks green on both seeds.

Seed 4242 (synthetic-corpse Mourn scenario) fails quality-of-life gates
(starving=41, interrupts=248) — grief pinned by the synthetic corpse disrupts
the colony's rhythm; structural checks pass. Not part of the two-seed
iteration protocol; left as a known-rough scenario.

## Deferred

- Hats (head coverage) — the head currently always burns.
- Rain halving UV → iteration 21 (§35.5).
- Wetness/drying interplay with sun → iteration 21.
