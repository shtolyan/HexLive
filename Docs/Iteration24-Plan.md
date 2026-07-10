# Iteration 24 — World Objects: Bed, Palms, Water, Coconuts, Crabs

Spec: §31C (new, written first) + amendments referenced from it.

## Simulation changes (soak-verified, 2 seeds OK)

- **Coconuts replace apples**: `food.apple` → `food.coconut`, `tree.apple`
  retired; `tree.palm` gained `Produce { food.coconut, 100, 4, 1 }` — the
  home grove is palms now. Cracking mechanics deferred.
- **Crabs replace rabbits**: spawn only within 2 tiles of Water (river
  bank), HopChance 0.3 → 0.2 (scuttle), traces `CrabSpawned`/`CrabKilled`.
  Internal classes keep their names; loot unchanged.
- **Obstacles**: `tree.big` / `tree.palm` / `rock.boulder` block their
  anchor junctions (bootstrap + runtime spawn; symmetric unblock on
  despawn/felling — WorldObjectMutations.SetObstacleBlocking, topology
  version bump). Interaction plans with blocked-anchor targets approach a
  free passable neighbor instead of standing in the trunk.
- **Produce rot**: unclaimed coconuts despawn after 2400 ticks
  (`ProduceRotted`, ~200/run — unreachable drops no longer accumulate).

## Why rabbits were never seen

They were **not exported to the snapshot and not rendered at all** — same
for dogs. Both are now visible: `CrabSnapshot` list added, renderer draws
dogs (grey capsule + head) and crabs (red shell + claws) with the same
prev/curr pose interpolation as NPCs, sweeps included.

## Presentation

- **Bed** (`bed_b` from molly_copy, Spaceship pack): GUID-closure copy of
  mesh/materials/textures only (~24 assets; the script-borne deps — clips,
  outline shader — excluded with the stripped MonoBehaviours). Prefab
  authored to `Resources/HexLive/Objects/bed.basic.prefab`, its `point`
  child kept as the lying attach anchor.
- **Laying on the bed**: `Laying Breathless.fbx` copied; animator gains a
  `Laying` bool param + state (AnyState → Laying, Laying → Idle). While
  `CurrentInteraction == Sleep`, the renderer finds the bed within a tile,
  and `NpcActorView.SetLaying` snaps the body to the bed's attach point
  (the LateUpdate pin switches owner: renderer pose ↔ bed point).
- **Object prefab convention** (spec 31C.3): CreateObjectView tries
  `Resources/HexLive/Objects/<definitionId>` first (FBX or prefab), with
  **bounds-based auto-fit** to the hex metric (palms 2.2 R tall, bed 0.95 R
  footprint, food 0.12 R) and auto-grounding; primitives remain fallback.
- **Palm model**: Kenney Nature Kit (CC0, license file included) —
  `tree_palmDetailedTall.fbx` → `Resources/HexLive/Objects/tree.palm.fbx`;
  a second variant kept in ImportedActors/Kenney.
- **Water depth** (spec 31C.4): water hexes render sunken (−40 % tile
  height) with an alpha-blended URP material — waders sink to the ankles.
  **Sand**: walkable tiles within 1 of water render sand-colored banks.
- Coconut fallback primitive is husk-brown.

## Verification

- All assemblies compile; 2-seed soak green after every sim change.
- Play-mode checklist: sleeping girl lies on the bed, palms from the kit,
  coconuts drop/rot, crabs scuttle at the river and get hunted visibly,
  dogs visible, nobody clips through trunks, river shows depth with sandy
  banks.

## Post-play fixes (user report round 2)

1. **"Jana walks into a red sphere and vanishes"** — the transferred bed
   prefab carried a baked scene offset `{-20.6, 0, 3.5}` in its root: the
   bed visual and its lying attach point rendered 20 units away from the
   logical anchor, and SetLaying teleported the sleeper there. Root zeroed
   + the actor view now refuses attach points farther than ~3 body heights.
2. **Mystery red spheres / green cylinders** — the catch-all fallback
   (red sphere) covered ponds, campfire, tools, stones, graves, corpses.
   Fallbacks are now legible silhouettes: flat blue water discs, ember
   campfire disc, gray stones/boulders, brown tool/resource boxes, dark
   grave slabs, lying corpse capsules; unknown = neutral gray.
3. **NPCs ghosting through each other** — pathfinding now avoids junctions
   occupied by standing housemates (direct-path fallback when enclosed);
   a mover whose next step is held waits up to 40 ticks then re-paths
   (`MovementRepath`). Spec 24.3 amendment.
4. **Talk spacing & facing** — the initiator approaches a free junction
   ~0.9 hex radius from the partner (arm's length, was: adjacent sub-grid
   point ≈ inside each other), and both turn to face each other when the
   talk starts (spec 28.8 amendment). Soak side effect: healthier colony
   (fewer reservation collisions) — explores up ~5x, starving at 10-12,
   zero deaths on both seeds.

## Known follow-ups

- Kenney FBX materials may import gray-ish — a palette texture/material
  pass in the editor if so.
- Bed is sci-fi styled (source pack); swap when a tropical bed asset lands.
- Coconut cracking mechanics; crab claw fight-back; palm wind sway.
