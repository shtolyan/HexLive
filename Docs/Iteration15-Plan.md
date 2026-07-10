# Iteration 15 Plan — Death, Grief & Remembrance (spec 28.15C)

## Iteration Target

Deaths (possible since iteration 9, occurring since iteration 10) finally
land socially:

1. **Corpse object** at the death site, decaying in 2 days; `CurrentUser`
   records whose body it is.
2. **Grief** on witnessing (<= 6 tiles) or discovering the corpse:
   Social/Comfort hit scaled by affinity toward the deceased, 1-day
   mourning period with social withdrawal, and the death site enters
   danger memory.
3. **Mourning**: a griever visits the body (Observe, 16 ticks) for closure —
   ends the mourning period early with a little comfort back.

## Code Touch Points

- Content: corpse.npc (Corpse tag, Observe interaction).
- `NpcMind`: GrievingUntilTick, GrievedCorpses.
- `SimulationSystems.cs`: corpse spawn + witnesses in RemoveDeadNpc;
  grief-on-discovery scan in DecisionSystem; Mourn goal (score, mapping,
  target filter); closure branch on Observe-at-corpse completion; grieving
  penalty in the Socialize modifier; CorpseSystem (Slow decay);
  registrations ×2 (runner + harness).

## Soak Results (seeds 12345 / 777)

Seed 777 (the seed with a death) produced a complete short story:

NPC3 died to dogs on day 7 at the east passage. Both survivors grieved
(witness + discovery paths both exercised); the death site entered their
danger memories. NPC2 made the pilgrimage and **began the mourning rite at
the body — and was attacked three ticks in by the same dog pack that killed
NPC3**, still patrolling the grave. Between the guard dogs and the two
survivors competing for the same junction (the corpse shares its anchor
with everything NPC3 dropped — heavy armor, spear, underwear), nobody
finished the rite, and the body decayed unmourned two days later.

Mechanism verification: grief triggers (2), mourning-rite start (traced
InteractionStarted Observe → corpse), corpse decay (1), danger-memory
marking, and grieving social withdrawal all exercised; the `Mourned`
closure trace is reachable but situational — the world does not owe anyone
a peaceful funeral. Tuning en route: Mourn score 0.35 → 0.7 (the pilgrimage
kept losing to thirst mid-walk), and witnesses now learn the corpse's
location (spatial memory seed), without which Mourn could never plan.

Seed 12345 (no deaths): zero grief events, no regressions. All structural
invariants hold in both.

## Verification (headless harness, 2 seeds)

1. Seed with a death (777): grieving triggers for survivors, mourning visit
   happens or grief times out, corpse decays (CorpseGone), the death tile
   is avoided.
2. Seed without deaths: nothing regresses.
3. All structural invariants hold.
