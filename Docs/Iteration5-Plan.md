# Iteration 5 Plan — Spatial Memory & Limited Perception

## Iteration Target

Kill the omniscient NPC. Spec 22.7 always demanded local perception; the
implementation ignored it. This iteration implements the perception radius
and the spatial memory (spec 27.11 / new 27.18A) that makes it livable:

1. **Perception radius**: objects are live-perceived only within hex
   distance 2; agents stay globally perceived (one shared home, v1).
2. **Spatial memory**: every sighting upserts a per-object record
   (definition, tile, junction, LastSeenTick). Remembered objects feed the
   same perception list flagged `FromMemory`, occupancy assumed free.
3. **Seeded home knowledge**: NPCs start knowing all bootstrap objects
   (furniture, trees) permanently — they live here. Runtime discoveries
   (apples) expire 2400 ticks after last seen.
4. **Negative evidence & stale discovery**: seeing a remembered spot empty
   removes the record; walking to a remembered-but-gone object fails the
   plan, forgets, re-decides.
5. **Foraging**: GetFood with no known apple walks to the nearest known
   producer (move-only plan); arrival brings drops into view. Already at the
   tree with no fruit → normal goal cooldown ("wait by the tree").
6. **Occupancy guard at interaction start**: memory can promise a free bed
   that is actually taken — execution now fails politely instead of
   overwriting the occupant.

## Code Touch Points

- `Simulation/Memory/MemoryState.cs`: reworked to `KnownObjects` dictionary
  (spec 27.18A); old unused layered-list stub removed.
- `Simulation/Common/` or `Spatial/HexSpatialMath.cs`: axial hex distance.
- `Simulation/AI/PerceptionSnapshot.cs`: `PerceivedObject.DefinitionId`,
  `FromMemory`.
- `Simulation/Runtime/SimulationSystems.cs`: PerceptionSystem radius +
  memory pipeline; DecisionSystem GetFood forage availability; PlanningSystem
  forage branch; ExecutionSystem move-only plan handler, occupancy start
  guard, forget-on-missing-target.
- `Simulation/Bootstrap/WorldStateFactory.cs`: seed permanent memories.
- Snapshot/exporter/panel: known-object count + list.

## Verification (headless harness)

1. Both NPCs still survive and talk with radius-2 perception.
2. Forage trips occur (`ForageArrived` traces) and lead to pickups.
3. Stale-memory discoveries occur (`MemoryForgotten` after walking to an
   apple the other NPC took) without deadlocks.
4. No occupancy stomping: `InteractionBlocked` traces instead of two NPCs
   in one bed; no reservation/occupancy leaks at cutoff.
