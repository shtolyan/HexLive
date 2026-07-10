# Iteration 17 Plan — Seamless World Foundation (spec §20 rework + §35.1)

## Iteration Target

First step of the Open World arc (§35): the spec's fragment/portal model is
retired — the world is one seamless hex field — and the map triples with
terrain that the following iterations will make interactive.

1. **Spec rework**: Fragment/FragmentLink retired as gameplay concepts
   (seamless world declaration at the head of §20); FragmentId remains a
   technical constant until a cleanup pass.
2. **World ~285 tiles** (q -8..10, r -6..8), home preserved, wilderness
   around, all one continuous space.
3. **River**: seeded winding line of Water tiles (new TileFlags.Water) —
   walkable shallows, a RawWater drink source; cooling/wetness arrive in
   §35.4/35.5.
4. **Terrain props** (seeded): boulders, small pickable stones, big shade
   trees, palms — inert until iterations 18/20.
5. **Performance**: perception reachability moves from per-object BFS to an
   O(1) connected-component cache (invalidated by topology changes — i.e.
   future wall building). Path BFS remains only for actual movement.

## Soak Results (seeds 12345 / 777)

- World: 285 tiles / 13 949 junctions (3× the old map), river carved with
  seed-dependent shape (17 vs 24 water tiles across seeds), all props and
  5 river drink spots placed. Home life, water/fire chain, hunting, social
  dynamics, rhythm — all invariants green in both seeds.
- **Connectivity cache is a ~40× speedup**: harness runtime dropped from
  ~150 s to ~4 s despite the tripled world. The old runs were dominated by
  per-object reachability BFS in perception all along; components are now
  O(1) per check and rebuilt only on topology changes (i.e., future wall
  building).
- Observed balance note for §35 iterations: 2 dogs dilute in a 3× world
  (14 fight passes vs ~50) — predator population may need scaling with
  area once building gives NPCs stronger defenses.

## Verification (headless harness, 2 seeds)

1. Harness runtime stays acceptable on the tripled world.
2. All prior invariants hold (survival, rhythm, social, structure).
3. River exists and is drinkable; props placed; seeds diverge in placement.
