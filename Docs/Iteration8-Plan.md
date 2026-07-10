# Iteration 8 Plan — Third NPC & Relationship Triangle

## Iteration Target

Two NPCs can only have one relationship. A third inhabitant turns the
relationship system (28.15B) into a social graph:

1. **NPC 3** spawns at (0,3) with its own need phase (well-rested, sociable)
   so routines desynchronize.
2. **Deliberate bed scarcity** (2 beds : 3 sleepers): nightly contention
   feeds the resentment mechanic and pushes pairwise relationships apart.
3. **Affinity-based partner choice** (spec 28.6): talk targets are picked by
   highest affinity, distance breaking ties — friendships self-select,
   disliked housemates get approached only via the loneliness override.
4. **Visual identity**: NPC bodies get id-based tints so three capsules are
   distinguishable in play mode.

Food supply: the first soak with two trees showed visible strain
(StatusStarving 28 per 10 days vs 4-6 with two NPCs; queueing at trees), so
the fallback lever was used — a **third tree** (id 107, east pocket at (3,2))
keeps supply comfortably above three-NPC demand.

## Code Touch Points

- `Simulation/Bootstrap/PrototypeWorldDefinitionFactory.cs`: NPC 3.
- `Simulation/Runtime/SimulationSystems.cs`: BuildTalkPlan target selection
  by affinity.
- `UnityPresentation/Rendering/HexWorldRenderer.cs`: per-id NPC body tint.

## Verification (headless harness, 10 game days)

1. All three NPCs survive (eat, sleep, no chronic starving) and all three
   both initiate and receive talks.
2. Pairwise affinities diverge: at least two pairs differ by > 0.2 at some
   point (the triangle is asymmetric — friends AND outsiders exist).
3. Bed contention produces InteractionBlocked/resentment without deadlock.
4. Day/night rhythm, memory, and leak checks from prior iterations hold.
