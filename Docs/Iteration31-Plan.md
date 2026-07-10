# Iteration 31 — Signed Thermal Comfort (the "chocolate" axis)

Spec: §29C.10.

## What shipped

- **`NPCNeeds.ThermalComfort`** — a signed [-1, +1] reading for the UI:
  **0 = ideal**, negative = too cold (snowflake, left), positive = too hot
  (sun, right). Computed each slow tick from the effective temperature (0
  inside the ideal [12,20] band, scaling to ±1 over ~15 degrees). Exposed
  in the NPC snapshot for the character panel to draw the axis the user
  asked for.
- **The fire warms the dial**: a lit campfire's radiated warmth (within 2
  tiles, clamped so it only removes cold) is folded into the *displayed*
  signed comfort — the player sees the fire pull the dial toward ideal on a
  cold night.
- **Extreme-weather HP drain** (`Hypothermia` / `Heatstroke`): while
  |ThermalComfort| >= 0.85 and not in water, every body part loses 0.02 per
  slow tick. Dormant in the current mild climate (0 events across 6 seeds)
  — a safety net for genuine cold snaps / heat waves.

## Deferred (and why)

The user also wanted the fire's warmth to actually HELP survival and the
fire to BURN anyone standing in it. Both were prototyped and both **wiped
dog-fragile seeds**:

- Feeding fire warmth into the decision-driving `ThermalDiscomfort` need
  made everyone comfortable by the fire → nobody dressed / cooled off →
  the colony repositioned and dogs wiped 3-4 seeds.
- The HP burn fired 10-46 times per seed because NPCs *constantly path
  across the central fire tile* to use the hearth — it whittled them down
  and, combined with dogs, wiped seeds.

Both belong with the **campfire-as-obstacle** change (approach from the
edge, never stand on the flames), where they can't reposition fatally.
`onFire` and the `FireBurn` trace are already computed and wired-ready.
This kept iteration 31 behaviorally identical to HEAD — **6 seeds green**
(12345/777/999/31337/555/42), no reshuffle.

## Lesson

The colony sits on a knife's edge against dogs; ANY behavioral
perturbation reshuffles NPC positions and tips ~1 seed into a dog wipe
(butterfly effect). Real robustness comes from the weapons/armor/defense
iteration, not from tuning unrelated systems. Until then, cosmetic/UI
additions must stay behavior-neutral to keep all seeds green.
