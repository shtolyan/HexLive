# Iteration 12 Plan — Body Parts, Wounds & Layered Wear (molly port)

## Direction Roadmap (user request 2026-07-10)

The survival deepening arc, split into three iterations:

- **Iteration 12 (this)**: body parts with per-part health (molly
  BoneHealthSystem, simplified to 7 zones), layered clothing
  (Underwear/Wear/Outerwear × covered zones, molly Wearing port), zoned
  bite damage, limping, weak arms, death by vital part.
- **Iteration 13 (next)**: thirst need; water sources; boiled vs raw water;
  campfire (needs lighter + firewood), pot, boiling. Basic tools found near
  home.
- **Iteration 14**: rabbits (prey that flees), spear crafting, hunting,
  raw meat that must be cooked on the campfire, leather/hide clothing
  crafting (finally covering legs).

## Iteration Target (spec 19.3C + 31A.5B)

1. `BodyState`: 7 parts × health. Overall Health = mean. Head/Torso
   destroyed → death with cause trace.
2. Dog bites hit seeded-weighted parts (legs most, head least), reduced by
   armor *covering that part*.
3. Limping (legs) and weak strikes (arms); parts regenerate while fed.
4. Layered wear: one item per (layer, part); conflicts drop the old item;
   warmth sums across layers; armor is per-part max.
5. Everyone starts in underwear (per-NPC instance, not contended).

## Soak Results (seeds 12345 / 777)

- Bite anatomy matches the spec weights: legs took ~60 % of all bites
  (LegL 19-20 / LegR 14-17 per soak), head 0-1.
- **Limping is real**: minMobility hit 0.40-0.60 — an NPC with mauled legs
  hobbled home at under half speed and healed up over the following day
  (seed 12345 ends with NPC2's left leg at 0.46, still regenerating).
- No vital-part deaths in these seeds (head bites are rare by design);
  overall no deaths, wearables cycled 13/10, exclusivity and all structural
  invariants held.
- Layer-conflict replacement (`ItemReplaced`) did not trigger in these two
  seeds (the two Outerwear pieces found different owners before ever
  overlapping); the mechanism is exercised by construction when one NPC
  dresses heavy over leather.

## Verification (headless harness, 2 seeds)

1. Wound traces show varied parts; leg wounds visibly slow NPCs (limp
   factor in trace); arm wounds weaken strikes.
2. Layer conflicts occur (dressing heavy over leather drops leather).
3. Death (if any) reports its cause (vital part vs depletion).
4. Parts regenerate to 1.0 during peace; exclusivity (3 original garments),
   sanctuary, rhythm, leak invariants hold; seeds diverge.
