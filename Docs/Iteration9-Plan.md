# Iteration 9 Plan — Wilderness, Health, Dogs, Armor, Seed

## Iteration Target (spec 29C)

Real survival stakes and run-to-run variety, no visuals:

1. **World seed**: all chance rolls mix `WorldState.Seed`; different seeds →
   different lives, same seed → exact reproducibility.
2. **Wilderness**: the map grows from ~28 to ~100 tiles (generated bounding
   region around the hand-authored home); a far apple tree and two armor
   pieces reward exploration.
3. **Explore goal**: calm NPCs wander to random far junctions (seeded jitter
   makes urges vary), building spatial memory of the wild.
4. **Health**: 0..1, regen while fed, death removes the NPC (full cleanup).
5. **Dogs (2)**: roam wilderness, aggro within 2 tiles, chase, bite. One dog
   is a survivable-but-costly fight (auto strike-back); two at once are
   near-certain death. Respawn keeps the threat alive.
6. **Armor & overheating**: ArmorDelta absorbs damage; warmth now overheats
   above effective 20° — protection costs midday comfort. Dress hard-gated
   below ThermalDiscomfort 0.3.

## Code Touch Points

- `Simulation/Common/MathUtil.cs`: seeded hash overload.
- `Simulation/Core/WorldState.cs`: Seed, Dogs list.
- New `Simulation/Wildlife/DogState.cs`; `DogSystem` in SimulationSystems.cs.
- `Simulation/Agents/NpcState.cs`: Health, EquippedArmor, IsFighting.
- `Simulation/Content/Definitions.cs`: ArmorDelta.
- `PrototypeContentCatalog`: armor.leather, armor.heavy definitions.
- `PrototypeWorldDefinitionFactory`: wilderness generation, far tree, armor
  placement, seed plumb-through.
- `SimulationSystems.cs`: Explore scoring/planning, health regen, overheat
  model, fighting gates in Decision/Perception.
- Snapshot/exporter/panel: Health row; DogSnapshot list (data only —
  rendering deferred per "visuals separately").

## Balance Findings (first soak, all spec'd in 29C.4/29C.5)

1. **Equipment stacking**: Dress applied deltas additively — NPCs re-dressed
   every cold evening until warmth and armor hit 1.0 (invulnerable, dogs
   dealt zero, permanently overheated). Fix: warmth/armor apply as
   max(current, item).
2. **Overheat doom loop**: an overheated NPC (Thermal >= 0.3) still saw
   Dress as the cure and dressed *more*. Fix: Dress gated to the cold side
   (effective temperature < 14).
3. **Hyperactive wandering**: Explore at 0.1 + jitter@80 ticks produced 16
   trips/NPC/day and hungry, twitchy NPCs (78 starving episodes). Retuned to
   0.05 + jitter@160; strolls cut short by real needs are reclassified as
   benign in metrics (interruptible filler by design).

Observed after fixes (seeds 12345 / 777): all dogs eventually hunted down,
fight costs 0.2-0.5 HP, one simultaneous two-dog attack survived thanks to
heavy armor (0.5) — armor is exactly the difference between a scare and a
death. Seeds fully diverge (different spawns, targets, quarrel counts).

**Known v1 model quirks (accepted):** clothing items are not consumed by
Dress, so all NPCs can wear "the same" armor (the clothing model has no item
instances yet); dogs never win against an armored group — lethality returns
when packs (simultaneous aggro) meet unarmored wanderers far from home.

## Verification (headless harness, 2 seeds × 10 game days)

1. Seeds diverge: different event counts / final states.
2. Dog fights occur; at least one dog dies across seeds; an NPC death is
   possible and, when it happens, leaves no leaks (reservations, occupancy,
   caches) and the remaining NPCs keep living.
3. Wounded survivors heal back up (Health trajectory recovers).
4. Explore trips happen; armor gets discovered/worn in at least one seed.
5. Prior-iteration invariants hold for survivors (rhythm, talks, memory).
