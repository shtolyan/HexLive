# Iteration 21 — Weather, Rain & Wet Clothes

Spec: §35.5 (concretized first, per spec-first workflow).

## Goal

Weather becomes a survival pressure: seeded rain fronts wet clothes, wet
clothes lose all warmth and slow the NPC down, and everything dries — by sun,
by campfire, or fastest on a craftable drying rack.

## What was built

- **Item instances**: `ItemInstance { DefinitionId, Wetness, Durability }`
  replaces the bare definition-id strings in `NPCState.WornItems` and
  `InventoryState.Items`. Implicit string conversions plus
  DefinitionId-equality keep all ~60 legacy call sites (recipes, checks,
  traces) compiling with identical semantics. Ground objects carry
  `WorldObjectState.Wetness`, so state survives drop → pickup → dress
  round-trips (and the death-drop). Durability rides along at 1.0 until 35.6.
- **WeatherSystem** (slow): per-day seeded schedule — rain day iff
  `Hash01(seed, d, 17, 3301) < 0.45`, start/duration hashed likewise; the
  system derives `IsRaining` statelessly and traces transitions.
- **Rain effects**: UV ×0.5 and −3° (EnvironmentSystem); rain wets worn and
  carried items of NPCs outdoors (+0.04/slow tick) and wearable ground
  objects outdoors; standing in the river wets at +0.15.
- **MoistureSystem** (slow): wetting + drying for every instance everywhere.
  Drying 0.02/slow tick × best of (sun ×3 / lit campfire ×4 / rack ×5);
  recalculates equipment each tick since warmth now moves.
- **Wet garment** (Wetness > 0.5): zero warmth, movement ×0.9 per wet worn
  item (floor ×0.8), `SoakedThrough` trace.
- **Drying rack**: `station.drying_rack`, `CraftRack` goal (2 logs at the
  campfire, placed on a free fireside junction). `DryClothes` goal: hang the
  wettest garment on a free rack (`Hang` interaction, `ItemHung`) — hung
  clothes are ownerless loot per the standing rule — or, with no free rack,
  a move-only trip to the lit fire (`FireDryPlanned`).
- Snapshot/debug panel: RAIN marker next to the clock.

## Soak findings (fixed spec-first)

1. **Per-tick rain roll mixed badly.** `Hash01(seed, tick, 0, 3301) < 0.003`
   on the 16-tick slow stride gave seed 777 **zero** fronts in ten days
   (seed 12345 got 4). Replaced with the per-day schedule above — verified
   2-4 rain days per 10 across six seeds before coding.
2. **CraftRack at 0.1 base never won** — 811 available ticks, zero
   selections. Raised to 0.3 (+0.2 when raining/wet): infrastructure
   competes like CraftLeather, not like filler.
3. **Harness "explores > 5" gate obsolete**: aimless strolling is
   legitimately displaced by purposeful outings (sun siestas, drying
   trips). Gate now asserts `explores + coolOffs + dryTrips > 5`.

## Verification (10 game days, harness)

| Seed | Result | Rains | Soaked | Rack | Hung | Fire-dry trips |
|---|---|---|---|---|---|---|
| 12345 | OK, no deaths | 4 | 51 | crafted | 9 | 23 |
| 777 | OK (wipe waiver: dogs took NPC2/3; fights=95) | 3 | 18 | crafted | 0 | 0 |

End-of-run wet-worn count is 0 on both seeds — everything dries. Structural
checks (incl. HutOpen) green. New events whitelisted in the harness
(`RainStarted/RainStopped/SoakedThrough/ItemHung/RackCrafted/FireDryPlanned`)
and both new systems registered in runner **and** harness (parity rule).

## Deferred

- Durability wear + destruction, bow & arrows → iteration 22 (§35.6).
- Rain visuals/sound, puddles, wildlife reacting to rain — presentation era.
- Pots collecting rainwater — someday-maybe.
