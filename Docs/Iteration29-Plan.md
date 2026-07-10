# Iteration 29 — Labor Takes Time & The Water Bottle

Spec: §35.2 (harvest durations) and §29H (the bottle), written first.

## Two user asks

1. **Work should not be instant.** Felling a tree and breaking rock read
   as a flick at 40/30/40 ticks. Doubled to **big tree 80, palm 60,
   boulder 80** (the saw still halves trees → a sawn palm is 30). 20 s of
   real chopping on screen.
2. **Everyone drinks from a bottle.** Drinking was a bare interaction at
   the water's edge. Now every NPC carries a personal `tool.bottle`,
   fills it at a source, and drinks from it — a two-step chain mirroring
   GetFood → Eat.

## The bottle (§29H)

- `tool.bottle` bootstrapped into every inventory; a real item (shows in
  the panel and in hand) but **never dropped on death** — a personal
  effect, so the world never litters and survivors can't hoard empties.
- `NPCState.BottleWater` = None / Raw / Boiled (one bottle per NPC → the
  fill state lives on the NPC, not the item instance).
- **GetWater** goal (score = Thirst): thirsty + bottle empty + a source
  reachable → walk to the best source and **FillBottle**. Boiled at a lit
  campfire with a pot, else Raw at a pond/river bank (reuses the old
  drink source-preference and bank-standing pathing).
- **Drink** goal (score = Thirst): thirsty + bottle full → a single
  in-place `DrinkBottle` step (no target, no reservation, like Eat).
  Thirst −0.7 raw / −0.85 boiled, boiled +0.05 Comfort, **raw keeps the
  30 % sickness roll** (moved here from the old water-edge Drink). Bottle
  empties.
- The dehydration emergency boost applies to BOTH goals — a parched NPC
  races to fill and to drink.

The pond/river/campfire objects now expose **FillBottle**, not Drink.

## Balance findings

- **The two-step nearly doubled hydration cost.** First soak: FillBottle
  10/12 + DrinkBottle 8 for −0.6/−0.8 meant ~1.8× the ticks-per-thirst of
  the old single drink → dehydration 67-113, seed 999 broke. Fixed by
  matching the OLD cost: FillBottle **6 (raw) / 8 (boiled)**, DrinkBottle
  **6**, relief bumped to **−0.7 / −0.85** (a bottleful is a real gulp).
  Dehydration back to 43-47 — actually healthier than the pre-bottle band.
- **The `boiledDrinks > 0` water gate went flaky.** With the bottle,
  whether an NPC fills Boiled vs Raw on a seed is stochastic (raw banks
  are often the nearer fill); seed 12345 boiled zero times though the fire
  ran (FireLit=4). Dropped the hard sub-check — the boiled capability is
  still proven by FireLit>0 + a pot in the world, and 3/4 seeds do boil.

## Presentation

- `LowPolyToolFactory` builds a small blue bottle with a cork (in hand +
  on ground). `HeldItemFor`: FillBottle and Drink both show the bottle.
  `SetInteraction`: FillBottle → crouch/scoop work pose.

## Result

6 seeds green (12345/777/999/31337/555/42), no wipes. Bottle fills and
drinks are 1:1 (~113/soak, no orphaned fills); both raw and boiled water
occur; harvest visibly takes ~20 s.
