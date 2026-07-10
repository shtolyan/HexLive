# Iteration 13 Plan — Thirst, Water & Fire (spec 29E)

## Iteration Target

Second survival resource with a quality ladder: risky raw water from ponds
vs safe boiled water that costs logistics (lighter + pot + firewood + a
tended campfire).

1. **Thirst need**: +0.025/slow tick, drink gate at 0.35, Dehydrated
   emergency status (0.85/0.60, +1.0 Drink boost).
2. **Ponds** (2, wilderness): Drink -0.6 with a 30 % sickness roll
   (Torso -0.15, Comfort -0.2).
3. **Campfire** (yard): fuel in ResourceAmount, FireSystem burns it;
   fueling consumes a carried log (+1200 ticks); lighting a dead fire
   needs the lighter; drinking boiled needs the pot carried.
4. **Goal chain** Drink / GatherTools / GatherWood / TendFire — emergent
   single-step goals; per-goal target tag filters keep food and wood
   pickups separate.
5. Firewood renewable via two deadfall producers (29A pipeline reused).
6. Inventory 2 → 4 slots.

## Code Touch Points

- NPCNeeds.Thirst; NeedsDecaySystem; NPCMind.IsDehydrated.
- InteractionEffects.ThirstDelta; InteractionType.Drink/Fuel.
- Content: water.pond, campfire.spot, tool.lighter, tool.pot,
  resource.firewood, forest.deadfall; bootstrap placements.
- DecisionSystem: 4 goal scores + statuses; PlanningSystem: goal→interaction
  map, tag filters, boiled-over-raw preference; ExecutionSystem: sickness
  roll, fuel logic; new FireSystem (Slow); registration ×2.
- Snapshot/exporter/panel: Thirst bar.

## Soak Results (seeds 12345 / 777, 10 game days)

- **The fire lives as an economy**: lit twice per soak, refueled 16-17 times
  from renewable deadfall wood, went out once when the keeper was busy,
  had fuel at cutoff. The lighter/pot/wood chain executes end-to-end.
- **The quality ladder is real but rationed**: ~155 raw drinks vs ~65
  boiled, with 47-49 sickness episodes as the price of raw. The bottleneck
  is honest scarcity: there is ONE pot for three NPCs — only its carrier
  drinks boiled. A second pot (or pot-sharing etiquette) is a deliberate
  future lever, same pattern as the coat.
- Sickness stacked onto combat produced the roughest life yet: seed 12345
  ends with NPC3's left leg at 0 (crippled, hobbling at 0.4 speed until it
  regenerates) — yet nobody died and all invariants held.
- Harness lesson: FireSystem was initially registered in the Unity runner
  but not in the harness — the fire burned forever and boiled water was
  free. Registration parity between runner and harness is now on the
  checklist.

## Verification (headless harness, 2 seeds)

1. Thirst cycles; both raw and boiled drinks occur; boiled dominates once
   tools are collected.
2. GotSick fires occasionally after pond water; FireLit/FireFueled/FireOut
   sequence observed; deadfall keeps wood renewable.
3. Survival, rhythm, structural invariants hold; seeds diverge.
