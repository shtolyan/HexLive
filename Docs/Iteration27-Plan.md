# Iteration 27 — The Island: Tile Elevation, Cliffs & Sea

Spec: §20.16 (written first). The world becomes a real island: sea on all
sides, beaches, grass lowland, brown hills, mountains — some climbable by
natural ramps, some sheer.

## Simulation

- `Tile.Elevation` (0..5) through bootstrap → factory → snapshot.
- **Generation** (seeded, deterministic): two-octave value noise (Hash01
  lattice + bilinear interpolation, sharpened by squaring so quantization
  steps produce real 2-level jumps) × radial island falloff; map perimeter
  is forced open sea (no straight-cut coastline). Sea = Water +
  `Walkable=false` — nobody swims off the island; the river and pond stay
  walkable shallows.
- **River valleys**: river tiles carve to elevation 1 and clamp their banks
  to ≤2 — the first soak showed cliff-walled riverbanks starving the colony
  of drink spots (dehydrated 126).
- **Cliffs**: `BlockCliffAndSeaJunctions` — a boundary junction whose land
  tiles differ by >1 level is Blocked (the same mechanism as walls/trunks:
  pathing, connectivity, overlays inherit it); junctions entirely on
  unwalkable sea are closed. 1-level slopes stay walkable — where the noise
  is gentle, ramp paths lead up the hills.
- **Home plateau**: tiles within 3 of home/hut site clamp to 1-2, never sea.

## Presentation

- Hexes render as prisms (top = 0.2 + elevation × 0.55, skirts to ground);
  biome palette: grass lowland → dry grass → hill scrub → bare rock →
  mountain top; sand ring by water adjacency (existing); water surfaces
  sunken; a 400×400 sea plane extends the ocean past the world bounds.
- Every movable and object takes Y from its tile top (`GroundY` registry);
  pose interpolation smooths level changes.

## Harness

New `Island:` metrics + structural gate: home connectivity component must
cover ≥50 % of passable junctions (some unreachable crags are the point;
a shattered island is a bug). Observed: sea 93-95 tiles, lowland ~155,
hills ~25, peaks 6-16, coverage 99-100 % (777 has its first cut-off crags).

## Balance findings

- Sharper relief v2 after v1 produced zero cliffs (noise too gentle).
- Perimeter sea ate ~7 % of land → palms carry one more coconut
  (MaxConcurrent 4→5) to keep the food table set.
- Interrupt budget 80→90/NPC: four goals joined the roster since that
  calibration and island routes are longer — more legitimate mid-route
  re-decisions (storms show 300+).

Both seeds green; verified visually in-editor: sea ring, sandy coast,
green plains, brown hills, palms and the colony living on top.
