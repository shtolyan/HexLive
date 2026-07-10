# Iteration 2 Plan — Survival Loop (Trees, Inventory, Starving, Yard)

## Core Constraint (unchanged from Iteration 1)

The simulation core is a standalone C# domain layer:
- All simulation logic lives outside Unity-specific code (`HexLive.Simulation`).
- Unity is only a presentation, input, and debug host.
- Spec-first: spec.md sections 29A (Flora & Produce), 29B (Inventory v1),
  23.8/23.17 (Starving numbers), 29A.3 (runtime object lifecycle), 31.15
  (snapshot-diff spawn/despawn) define this iteration; code implements them.

## Iteration Target

Minimal survival loop for one NPC:
- apple trees outdoors periodically drop apples (runtime object spawning)
- NPC picks apples up into a 2-slot inventory (`GetFood` goal, `PickUp` interaction)
- NPC eats from inventory in place (`Eat` goal, `ConsumeInventoryItem` step)
- critical hunger (Starving, >= 0.85 / < 0.60 hysteresis) interrupts the
  current plan cleanly and forces food-seeking; no death
- map extended with an outdoor yard (~12 tiles) holding 2 trees; the indoor
  bootstrap apple is removed, forcing a house ↔ yard life loop

## Tuning Numbers

| Parameter | Value |
|---|---|
| Tree production interval | 160 ticks (40 s) |
| Max concurrent apples per tree | 3 |
| Drop spot | free non-blocked junction, walkable tile within distance 1 |
| Inventory capacity | 2 slots (definition ids) |
| PickUp duration | 4 ticks |
| Eat from inventory | item's Eat interaction (apple: 8 ticks, Hunger -0.45) |
| Starving enter / clear | Hunger >= 0.85 / < 0.60 |
| Starving boost | +1.0 EmergencyModifier to Eat / GetFood |
| GetFood gate | inventory has space and contains no food |
| Runtime object ids | from 1000 up (bootstrap ids stay < 1000) |

## Phases

0. spec.md updates + this document (done first).
1. Runtime object lifecycle: `WorldState.NextRuntimeObjectId`,
   `WorldObjectMutations.SpawnObject/DespawnObject`, production runtime
   fields on `WorldObjectState`, `InteractionType.PickUp`, `ProduceDefinition`.
2. Interaction-selection-by-type refactor (`PlanStep.Interaction`), replacing
   the hardcoded `Interactions[0]` in ExecutionSystem. Behavior-preserving.
3. Inventory + PickUp + GetFood + ConsumeInventoryItem across
   Decision/Planning/Execution; `food.apple` gains PickUp; new `tree.apple`.
4. `FruitProductionSystem` (Slow layer, registered after TemperatureSystem).
5. Starving hysteresis + `PlanInterruption.Abort` cleanup helper (also fixes
   the pre-existing occupancy/reservation leak on mid-interaction goal flips)
   + reservation release in the despawned-target failure branch.
6. Presentation: stale object view sweep in HexWorldRenderer, tree visual,
   `IsStarving` + `InventoryItems` in snapshot, debug panel inventory line
   and STARVING badge.
7. World: yard tiles, trees (ids 104, 105), remove bootstrap apple (id 100).

## Verification Checklist (play mode)

1. Yard renders as outdoor tiles with 2 trees; no ground apples indoors at start.
2. First apple appears near a tree within ~40 s; per-tree apples never exceed 3.
3. Hungry NPC: GetFood → walks house → yard → PickUp (~1 s) → apple view
   disappears → debug panel Inventory shows `food.apple`.
4. Next decision: Eat in place (8 ticks), Hunger drops ~0.45, inventory empties.
5. Starving while sleeping: STARVING badge appears, sleep interrupts
   (`GoalInterrupted` trace), bed occupancy clears, NPC seeks food, badge
   clears after eating.
6. Trace shows `ObjectSpawned` → `ItemPickedUp` → `ObjectDespawned` →
   `ItemConsumed`, and `StatusStarving` transitions.
7. 10-minute soak at max speed: no orphaned reservation/occupancy markers,
   no console errors.

## Pre-existing Bugs Fixed Along the Way

- `HexDirection.AllDirections` was declared before the named direction fields;
  static initializers run in textual order, so `HexDirection.All` returned six
  zero vectors and `SpatialQueries.GetNeighbors` found nothing. Latent until
  FruitProductionSystem became its first caller.
- `NPCExecutionState.Status` was never reset from `Completed` back to `None`,
  so the start gate in ExecutionSystem blocked every interaction after the
  first completed one. Masked before by the infinite apple and interrupts;
  now both cycle-reset paths return the status to `None`.
- A goal change over an active plan used to leak the target object's
  `IsOccupied` and the junction reservation (no cleanup on replan); fixed by
  `PlanInterruption.Abort`.

## Deliberate Simplifications

- Junction reservation stays at 48 ticks; TryReserveJunction allows
  same-owner refresh. Bump to 96 only if long yard walks demonstrably fail.
- `ResourceAmount` remains unused for food (an apple is one unit, despawned
  on pickup); the field stays for future multi-charge sources.
- Apples may drop on unreachable junctions; they count against the cap and
  are simply never targeted (bounded, observable via trace).
