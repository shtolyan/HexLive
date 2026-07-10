# Iteration 22 — Durability & Bow (closes the §35 arc)

Spec: §35.6 (concretized first, per spec-first workflow).

## Goal

Clothing wears out and falls apart — crafting keeps the colony clothed; the
bow gives hunting and the fear arc a ranged answer.

## What was built

- **Passive wear**: every worn garment loses 0.01 durability per worn
  game-day (MoistureSystem — the per-item condition pass). Carried, hung,
  ground items and tools do not wear.
- **Damage wear**: each dog bite costs every garment covering the bitten
  part 0.05 durability — armor absorbs health, the cloth gets chewed anyway.
- **Destruction**: at durability <= 0 the item is rags — removed outright,
  `ItemDestroyed`, Comfort −0.1, equipment recalculated.
- **Durability round-trip**: `WorldObjectState.Durability` added so state
  survives drop → pickup → dress (see finding 1).
- **Bow** (`tool.bow`): `CraftBow` — 2 logs + 1 hide at the campfire, score
  0.3 (CraftLeather 0.35 outranks it for the first hide: pants first).
- **Arrows** (`resource.arrow`): `CraftArrows` — 1 log → 3 arrows when the
  quiver is empty.
- **Ranged hunting**: bow + arrow shoots a rabbit from ≤ 3 tiles (no
  adjacency chase): hit 0.6 → kill + loot + 40 % arrow recovery from the
  carcass; miss → arrow lost, rabbit spooked. Hunt availability: spear
  **or** (bow + arrow).
- **Cover fire**: an archer housemate (not the chase target, not fighting)
  within 3 tiles shoots a *chasing* dog once per pass — hit 0.5, damage
  0.35. The fear arc gains an answer.

## Soak findings (fixed spec-first)

1. **Free repairs through dress churn.** Ground objects carried only
   `Wetness`; durability reset to 1.0 on every drop → pickup. NPCs swap
   garments constantly (ItemReplaced ~100/run), so the wear economy leaked
   to zero — the synthetic threadbare shirt (0.02) came back as new.
   `WorldObjectState.Durability` added and threaded through all five
   transfer points (pickup, dress, drop, death drop, rack hang).
2. **Bows don't arise organically in 10 days** — hides are scarce (few
   rabbit kills, pants eat the first hide). Mechanics verified via a
   seed-gated synthetic scenario (**4343**, pattern of 4242's corpse): NPC1
   starts with bow + 12 arrows + threadbare clothes.

## Verification (10 game days)

Canonical protocol (both **OK**, all structural checks green):

| Seed | Deaths | minWornDurability | Notes |
|---|---|---|---|
| 12345 | none | 0.81 | wear accrues through churn |
| 777 | NPC2,3 (dogs; waiver) | 0.66 | bite-wear dominates |

Synthetic archer seed 4343: 7 bow shots, 4 rabbit kills, 3 arrows recovered,
2 arrow batches re-crafted when the quiver drained, 9 cover-fire shots —
**both dogs killed, zero NPC deaths**, threadbare shirt destroyed by passive
wear (`ItemDestroyed`). QoL gates are noisy on this scenario (starving=38)
— like 4242, it is a mechanism testbed, not part of the two-seed protocol.

## §35 arc status — COMPLETE

35.1 seamless world · 35.2 tools & harvest · 35.3 hex-edge building ·
35.4 sun/UV/shade · 35.5 rain & wetness · 35.6 durability & bow — all
implemented, spec'd, and soak-verified.

## Deferred

- Bow vs. approaching (not yet chasing) dogs; hunting dogs for meat.
- Durability affecting warmth/armor before destruction.
- All visuals/presentation for iterations 9-22 (user directive: mechanics
  first) — the largest outstanding block.
