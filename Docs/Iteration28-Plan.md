# Iteration 28 — Ground Rest: Ledge Sits, Grass Sleep, The Earned Bed

Spec: §29G (written first), 31C.7A amendments. The land itself is
furniture: sit on a scenic ledge with legs over the edge, lie at a free
hex center — and the bed is no longer given, it is crafted.

## Simulation

- **PlanStepType.GroundSit / GroundSleep** — move-only walk to a reserved
  junction, then a timed in-place interaction with no target object.
  Execution follows the codebase idiom (no step indices — `CurrentStepIndex`
  is never advanced anywhere): the dispatcher keys on the plan's LAST step
  and drives walk → arrival → rest; completion performs the canonical
  cycle reset (Execution back to None, plan/goal/movement cleared,
  junction occupancy AND reservation released).
- **Ledge sitting**: boundary junctions with >= 1 elevation difference
  (§20.16 terrain); preferred within ~4 tiles; the sitter faces the lower
  side. Comfort ledge +0.25 / plain +0.15 vs chair +0.4.
- **Ground sleep**: free hex center (walkable, dry, no objects), anchored
  to HOME (campfire) with **indoor floor preferred** — sanctuary
  (29C.4A) protects sleepers; Energy +0.5 (same as bed), Comfort 0
  (the bed's edge is comfort, not energy).
- **Lying claims a footprint**: junctions within 0.5 × hex radius live in
  `NPCState.ClaimedJunctions`; other NPCs' pathfinding avoids them.
  Claims release on completion, interruption (PlanInterruption.Abort) and
  death.
- **CraftBed** (goal 0.6): 2 logs + 3 palm leaves at a burning campfire →
  bedroll on a free junction by the fire. A palm chop yields exactly the
  kit; 0.6 outbids TendFire (≤ 0.55) in that window. Bootstrap beds
  removed — first nights are on the grass.

## Balance findings (the road to green)

1. **Paralysis**: the first dispatch read `Steps[CurrentStepIndex]` behind
   an `IsMoving` guard — the index never advances in this codebase and
   arrival doesn't clear `JunctionPath`; plans hung forever, colony
   starved (296 starving events). Rewrote against the real loop idiom.
2. **Nap-forever**: unconditional sleep availability = 73 naps/soak, no
   explores, fire never lit. Gate: Energy < 0.45 OR Evening/Night.
3. **Eaten by dogs**: self-anchored sleep spots put night camps in dog
   country (fights 215, full wipe). Anchor to the campfire; prefer indoor.
4. **Poverty trap**: ground sleep at +0.35 energy = 160 naps/soak.
   Energy +0.5 (a night is a night), comfort stays 0.
5. **Leisure outbidding chores**: Sit score (1-Comfort) ≥ 0.4 by its own
   gate's construction — halved to ×0.5 and made unavailable during sleep
   hours (Sit→Sleep churn was 47 interrupts).
6. **The bed ate the fire**: crafting from the last logs left seed 777
   fireless all soak → CraftBed requires fuel > 0. At score 0.35 the
   hearth always ate the kit first → 0.6.
7. Wanderlust comfort bar 0.5 → 0.35 (comfort is a luxury now).

## Harness

- `GroundRest:` metrics line (sits/sleeps/sitPlans/bedsCrafted); whitelist
  += GroundSitPlanned, GroundSleepPlanned, GroundSatDown, GroundSleptWell,
  BedCrafted; GroundSleptWell counts toward the sleep rhythm gate.
- Outings gate += GroundSitPlanned — a ledge sit is exactly the
  "non-chore outing" the gate asserts.

## Result (both seeds green, no deaths)

- 12345: bed crafted and used (ground sleeps 50, bed takes over), fire
  healthy (boiled 9, fuel banked), darkSleepRatio 62 %.
- 777: colony lives on grass sleep (135), fire lit, kit+fire never
  coincided inside the soak window — the bed is a luxury, not a gate.

## Presentation

No changes needed: `FindBedAttachPoint` returns null with no bed nearby →
`SetLaying(true, null)` lies at the NPC's own position; ground sit uses
the existing Sitting pose, already rotated by the sim toward the ledge's
low side.
