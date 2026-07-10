# Iteration 30 — Gradual Needs & Longer Actions + the Starvation Fix

Spec: §29C.9 (gradual needs) and §29C.2 (starvation HP drain), written first.

## Two things

### A. Starvation / dehydration drain HP (critical bug)
A playtester found an NPC frozen at maxed-out needs, doing nothing. Cause:
no starvation damage existed — Health never fell, so a stuck agent (food/
water unreachable) never died and never freed its slot; it just hung.
Now while Hunger >= 0.95 OR Thirst >= 0.95 every body part loses 0.03/slow
tick (0.05 if both), Health follows the body mean, a destroyed vital ends
it. The 0.95 gate is above the 0.85 starving appraisal — healthy colonies
lose nothing; only a genuinely stuck agent drains to death (~200 s).
(Committed separately, 005f4e2.)

### B. Gradual needs & longer actions (Sims-style)
Needs now fill **visibly, tick by tick, across the action**, not in one
jump at the end. The total effect is split into `duration` equal shares
via a shared `ApplyEffectsScaled(npc, effects, 1/duration)`; each in-
progress tick applies one share, the last lands at completion — the sum
is exactly the authored effect.

- Covers Sit/Sleep (Comfort/Energy), Eat (Hunger), Drink-from-bottle
  (Thirst/Comfort) and ground rest (Comfort/Energy). Work interactions
  carry no need effect (zero share, harmless).
- Longer: Eat 8 -> 20 ticks (5 s), Drink 6 -> 16 (4 s), Talk 16 -> 40
  (10 s). Sit (70) / Sleep (100) already long; now their fill is gradual.
- Sickness (raw water) and the mutual social gain (Talk) still resolve
  once at completion — only personal-need relief drips.

## The stability trap (and the fix)

Gradual needs drain the CURRENT goal's own score during its action —
eating lowers Hunger, so Eat's score falls tick by tick. The decision
layer then switched away mid-meal/mid-nap: interrupts exploded (seed
31337: 782; raising the switch threshold to 0.5 barely helped — 415-623
across all seeds).

Fix: **while an action is InProgress, don't re-decide at all.** A guard at
the top of the decision loop holds the current goal until the (short)
action completes. This is safe because real emergencies don't flow through
goal scoring — a threatening dog sets Flee *reactively* via the fear path
(a separate system), and starvation/dehydration interrupt on the next
decision once the action ends. Interrupts fell to 43-133 (below even the
pre-gradual baseline), darkSleepRatio back to a healthy 51-65 %.

6 seeds green (12345/777/999/31337/555/42).
