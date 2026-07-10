# Iteration 14 Plan — Hunting & Crafting (spec 29F)

## Iteration Target

Close the survival-deepening arc (12 → 13 → 14): prey, weapon, hunt, cook,
and the first crafted garment.

1. **Rabbits** (max 3, respawning): graze, hop, flee from NPCs; spooked
   after a missed strike. Never indoors.
2. **Hunting**: spear required; move-only chase; automatic 50 % kill roll on
   adjacency; kill auto-loots 1 raw meat + 1 hide.
3. **Crafting at the campfire** (recipe = goal): spear from a log; cooked
   meat from raw (fire must be lit); leather pants from 2 hides — worn
   immediately, first-ever leg armor (0.2).
4. **Raw meat is inedible** — no Eat interaction; the fire is the only path.
5. Inventory 4 → 7 slots.

## Code Touch Points

- `Simulation/Wildlife/DogState.cs`: RabbitState added (same file, no csproj
  churn); WorldState.Rabbits + NextRabbitId.
- `SimulationSystems.cs`: RabbitSystem (Medium); Hunt/CraftSpear/CookMeat/
  CraftLeather goals + scores; BuildHuntPlan (move-only chase); Craft
  interaction start-validation + completion recipes; registration ×2
  (runner AND harness — the iteration-13 parity lesson).
- Content: tool.spear, food.meat_raw, food.meat_cooked, resource.hide,
  clothing.leather_pants; campfire gains Craft.

## Soak Results (seeds 12345 / 777)

The full chain closed in both seeds: each NPC whittled a spear (3 per soak),
hunts produced kills and misses, meat got cooked and eaten (raw is
structurally inedible), and **leather pants were crafted and worn** — the
first leg armor in the world's history.

Two retunes from soak evidence:
1. Rabbit population 3/3600 → 4/2400: the first soak yielded one kill per
   10 days — encounters, not kill chance, were the bottleneck.
2. Pants recipe 2 hides → 1: loot goes to the killer, and two hides never
   accumulated on one NPC. One rabbit = one pair of rabbit pants.

Seed 777 also delivered the first **death by vital body part** (19.3C
mechanics as the cause of death, not HP depletion) — the survivors carried
on with all structural invariants intact.

## Verification (headless harness, 2 seeds)

1. Spear gets crafted; hunts happen (kills and misses both observed).
2. Meat gets cooked and eaten (never eaten raw — no such path exists).
3. Leather pants appear after 2 kills and are worn (leg armor > 0).
4. Rabbits never indoors; all prior invariants hold; seeds diverge.
