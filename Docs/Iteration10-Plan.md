# Iteration 10 Plan — Fear, Flight & Danger Memory

## Iteration Target (spec 29C.4A)

Iteration 9 left combat as stand-and-trade and armor as a warmth accident.
This iteration adds the survival-decision layer:

1. **Sanctuary**: dogs never enter Indoor tiles (roam/chase/spawn all skip
   them). Home is safe by construction.
2. **Flight**: Health < 0.5 or two adjacent dogs → the NPC stops trading
   hits and runs for the nearest indoor junction (move-only Flee plan, held
   unconditionally). Bites still land while running — escape has a price.
3. **Danger memory**: attacks record {tile, tick} (TTL 1 day, cap 8).
   Explore destinations near fresh dangers are filtered out.
4. **Threat-aware dressing**: fresh danger + no armor → Dress becomes
   available regardless of weather, scored at fear-need 0.6, and the planner
   prefers the highest-armor item over the nearest — the NPC arms up
   *because the world got dangerous*, accepting overheating.

## Code Touch Points

- `Simulation/AI/NpcMind.cs`: GoalType.Flee.
- `Simulation/Memory/MemoryState.cs`: DangerMemory list.
- `Simulation/Runtime/SimulationSystems.cs`: DogSystem (sanctuary filter,
  attacker counting, StartFlee, no strike-back while fleeing, danger
  recording), DecisionSystem (Flee hold, threat-aware Dress availability,
  danger pruning), PlanningSystem (Explore danger filter, Dress armor
  preference).

## Soak Results (seeds 12345 / 777, 10 game days each)

Seed 12345 delivered the first death in the simulation's history — and it
was a *story*, not a bug: a dog pair (spawned together at the day-3 respawn)
hunted the yard, all three NPCs fled repeatedly (8 flights, mostly at
Attackers=2), the pack kept re-acquiring targets, and eventually cornered
NPC1 at distance 0 — bites land while fleeing, and this time the door was
too far. Cleanup after death was total (0 orphaned reservations/occupancy/
caches); the two survivors lived on normally.

Seed 777: no deaths, only 2 flights, one dog alive at cutoff — a completely
different life. Sanctuary held in both (0 indoor-dog violations); 36-38
danger memories recorded; explore avoidance active.

## Verification (headless harness, 2 seeds × 10 game days)

1. Flee events occur and the runner survives them (reaches indoors, heals).
2. DangerRemembered traces appear; explore destinations avoid fresh danger.
3. Threat-driven Dress happens in warm weather after an attack.
4. Dogs are never observed on Indoor tiles.
5. Structure/leak/rhythm invariants hold; seeds diverge.
