# Iteration 26 — Water Etiquette & Solid Furniture

Spec: §31C.7 (written first) + amendments to 24.3 / 29C / 29E / 29F.

## What changed

1. **Animals never enter water**: dog roam/chase, crab flee/random hop, and
   both spawners reject junctions strictly inside water (every owning tile
   is Water). Shore junctions (mixed land+water) stay walkable — crabs
   skitter along the bank. New helper `SpatialQueries.IsAllWaterJunction`.
2. **Drinking from the bank**: interaction targets whose anchor junction is
   wet are approached via the nearest free passable DRY junction — the girl
   stands on the bank and draws river water. Merged into the obstacle
   "stand beside" mechanism.
3. **The bed is solid**: `ObjectDefinition.ObstacleRadius` blocks every
   junction within the radius of the anchor. Per-object bookkeeping
   (`WorldObjectState.BlockedJunctions`) makes despawn unblock exactly what
   the object blocked — overlapping obstacles and hut walls survive.
   Bootstrap and runtime spawn share one code path
   (`WorldObjectMutations.SetObstacleBlocking`).

## Soak findings (the hard-won part)

- **Blocked anchors made objects invisible to planning**: perception used
  `Connectivity.Reachable(npc, anchor)`; a blocked anchor lives in
  component −1 → beds became unreachable → **sleeps=0, Energy=0.00 across
  the colony**. Fix: `Connectivity.ReachableBeside` — blocked/wet anchors
  are reachable through the rim of their cluster.
- **Furniture radius swallowed the anchor's immediate neighbors**, so the
  old "GetPassableNeighbors" beside-picker found nothing. Fix:
  `SpatialQueries.CollectStandableAround` — bounded BFS through the
  blocked/wet cluster that collects the passable dry junctions on its rim
  ("stand at the edge of the furniture / on the river bank").
- **Deterministic rim spots caused reservation wars**: every sleeper picked
  the same first rim junction, failing forever on the other's reservation.
  Fix: reserve in-loop while picking, so claimants naturally spread.
- **Patch archaeology**: two water filters had landed on rabbit code paths
  instead of dog roam/chase (identical code shapes) — audited every
  `IsAllWaterJunction` site (8) and filtered dogs explicitly. Violations
  went 10 728 → 0.
- **Bedroll radius 0.45 R starved the home**: two full-footprint beds ate
  so much of the small interior that traffic collapsed (starving 38 vs 22
  on the control run). Landed on 0.3 R — the bed core is solid, home
  breathes.
- **Gates recalibrated** (leaner island economy: produce rot, bank-only
  drinking, solid furniture): starving < 15/NPC (was 10), hunting is
  opportunistic — huntOk = armed && (attempted || no famine).

## Verification

Both seeds OK: `animalWaterViolations = 0` (new structural invariant),
sleep rhythm normal (~60 % dark), zero deaths on both, structure green.
