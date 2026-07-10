# Iteration 19 Plan — Building: the Communal Hut (spec 35.3)

## Iteration Target

Hex-edge construction, native to the spatial model (walls = blocked edge
junctions — the mechanism the hand-authored home always used):

1. **One communal project**: a 1-tile hut, site seeded 5-7 tiles from home
   (all 6 neighbors walkable), door edge facing home; a construction.site
   object anchors all planning/interaction machinery.
2. **Pieces** (Build interaction, 30 ticks each): floor+roof (1 log +
   2 palm leaves) → 5 walls (1 log + 1 stone each; blocks the edge's
   shared junctions, bumps TopologyVersion) → door (2 logs; one junction
   stays passable, marked Junction.Door — humans only, animals never).
3. **Completion**: tile flips Indoor (+ sanctuary + new +4° indoor warmth),
   a bed spawns inside, the site object is removed.
4. Material drivers extend the gather chains; total bill 8 logs +
   5 stones + 2 palm leaves — a real colony effort fed by iteration 18.
5. Safety: FindNearestJunction skips blocked; NPCs standing where a wall
   goes up re-resolve to the nearest passable junction.

## Soak Results (seeds 12345 / 777)

**The hut was completed in BOTH seeds** (7 pieces, all six edges, +1 Indoor
tile, bed inside) — the first player-free construction in the world's
history. The connectivity cache rebuilt 7 times from real walls, exactly as
designed in 35.1.

**Balance lesson (spec'd)**: logs feed walls AND the hearth. The first soak
let material hauling outcompete survival: the fire died 6 times, 37 starving
/ 47 dehydrated episodes, and one death. Fix: **construction is peacetime
work** — the build chain pauses while the builder is hungry/thirsty (>= 0.5)
or the campfire is low. After the gate: hut still completes, starving 37→15,
zero deaths in both seeds. Watch item: wood economy is now structurally
tight (fuel at 0 at one cutoff) — more deadfall or plantable trees is the
future lever.

## Verification (headless harness, 2 seeds)

1. BuildProgress ×7 and HutCompleted in both seeds; Indoor tile count +1.
2. ConnectivityRebuilt fires from wall placement (7 per soak).
3. No animals inside the hut (Door junctions skipped by dogs/rabbits).
4. All structural invariants hold.
