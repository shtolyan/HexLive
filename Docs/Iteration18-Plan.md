# Iteration 18 Plan — Tools & Harvesting (spec 35.2)

## Iteration Target

The stone age arrives: tools unlock the terrain placed in iteration 17.

1. **Unified wood**: logs = resource.firewood (one resource for fire and
   future construction; deadfall renewable, felling is the bulk source).
2. **Tool chain**: GatherStone → CraftAxe (1 log + 1 stone) / CraftPickaxe
   (1 log + 2 stones) at the campfire workbench; the saw is findable
   wilderness loot (GatherTools now collects ANY reachable Tool not
   carried — which also recovers dropped gear).
3. **Harvest interaction**: big tree 40 ticks / palm 30 / boulder 40;
   trees need axe or saw (saw ÷2 duration), boulders need the pickaxe.
   Loot: big tree → 4 logs; palm → 2 logs + 3 palm leaves; boulder →
   4 stones. The object is consumed — shade dies with the tree.
4. Drivers: fire crisis with no ground wood → fell a tree; no palm leaves
   in hand → chop a palm (stocking for construction); pickaxe owner with
   < 2 stones → break a boulder. Apple trees are never targets.
5. Inventory 7 → 10 (the tool belt era).

## Soak Results (seeds 12345 / 777)

Full industry in both seeds: 2 axes + 1-2 pickaxes crafted, the saw found
and carried, 4-7 trees felled, 1-2 boulders broken, palm leaves stockpiled
(3-5 carried at cutoff) — construction materials exist in the world.

**Harshness observation:** industry lures NPCs deeper into the wild
(stones, palms, boulders are far from home), and dog-pair encounters rose —
seed 12345 lost two NPCs to vital wounds, seed 777 one. The colony still
functions (structural checks green, survivor healthy), but the pressure
curve now clearly points at iteration 19: **walls**. Building is not
decoration — it is the answer the world is demanding.

## Verification (headless harness, 2 seeds)

1. Axe/pickaxe crafted; saw found; trees felled (leaves looted); boulder
   broken; saw speed bonus applied by construction.
2. All structural invariants hold; wipes (if any) waive only
   quality-of-life checks.
