# Iteration 3 Plan — Decision Stability & Second NPC

## Iteration Target

1. **Decision stability** (spec 23.8–23.10 concretized): stop the
   Sleep↔GetFood goal flapping observed in iteration 2's soak run.
   - Goal lock: 24 ticks on adopting any non-Idle goal; broken only by a
     score lead > 0.5 (Starving boost crosses it, ordinary drift does not).
   - Switch delta: after the lock expires, a competing goal needs a
     score lead > 0.15 while a plan is active; free switch when idle.
   - Failure cooldowns: PlanFailed / ReservationFailed put the goal on a
     40-tick cooldown, hard-gated in scoring.
2. **Second NPC**: id 2 spawns at (2,0) with a different need profile.
   Bed/chair/coat become contended:
   - target selection skips objects occupied by *another* NPC
     (occupied-by-self stays valid, spec 24.3);
   - junction reservations remain the race arbiter;
   - a lost race → cooldown → NPC does something else instead of spinning.

## Tuning Numbers

| Parameter | Value |
|---|---|
| Goal lock duration | 24 ticks (6 s) |
| Lock override delta (emergency) | 0.5 |
| Switch delta (active plan, lock expired) | 0.15 |
| Failure cooldown | 40 ticks (10 s) |
| NPC 2 start | tile (2,0), Hunger 0.55, Energy 0.5, Comfort 0.4, Thermal 0.5 |

## Code Touch Points

- `Simulation/AI/PerceptionSnapshot.cs`: `PerceivedObject.OccupiedBy`.
- `Simulation/Runtime/SimulationSystems.cs`:
  - PerceptionSystem fills `OccupiedBy` from `CurrentUser`;
  - DecisionSystem: cooldown pruning + hard gate, hold-or-adopt selection
    (lock / switch delta), lock placement on adoption; availability treats
    occupied-by-self as available;
  - PlanningSystem: candidate filter skips objects occupied by others;
    failure paths add `GoalCooldown`.
- `Simulation/Bootstrap/PrototypeWorldDefinitionFactory.cs`: NPC id 2.
- `Simulation/Debug/WorldSnapshot.cs` + exporter: `GoalLockEndTick`,
  `CooldownGoals` for the debug panel; panel shows lock/cooldown rows.

## Verification (headless harness + play mode)

1. Soak 24000 ticks with 2 NPCs: both NPCs keep eating (ItemConsumed per
   entity > 0), both survive with sane final needs.
2. GoalInterrupted count drops by an order of magnitude vs iteration 2
   (80 → expect < 15, mostly genuine Starving emergencies).
3. Sleep interactions complete (InteractionCompleted with Sleep present).
4. Contention observable: ReservationFailed / PlanFailed → GoalCooldown →
   different activity, no deadlock, no reservation/occupancy leaks at end.
5. Play mode: two capsules moving, debug panel shows lock/cooldown state.

## Known Limitations (accepted)

- Debug panel inspects the first NPC only; the second is observable via the
  renderer and trace log. NPC selection UI is future work.
- Social need still has no decay and no Socialize goal — the two NPCs
  coexist but do not interact socially yet (spec 28 remains dormant).
