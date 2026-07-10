# Iteration 16 Plan — Burial & Graves (spec 28.15D)

## Iteration Target

Close the death arc: bodies get buried, fear becomes remembrance.

1. **Bury**: any housemate aware of a corpse digs a grave (20 ticks, no
   tool). Corpse → permanent grave (whose it is preserved); burier gets
   closure (+0.15 Comfort, mourning ends).
2. **Sanctification**: burial removes the death tile from every living
   NPC's danger memory — the grave is sacred, not scary.
3. **Remembrance visits**: the Mourn goal widens — lonely NPCs
   (Social < 0.35) visit known graves for Comfort +0.1 / Social +0.15.
   The dead keep a lasting social presence.
4. No inheritance concept: the deceased's dropped gear is ownerless loot —
   anyone picks it up through the normal PickUp/Dress goals.

## Code Touch Points

- `Definitions.cs`: InteractionType.Bury.
- Content: Bury interaction on corpse.npc; new grave.npc (Grave tag,
  Observe 12 ticks, Comfort +0.1).
- `NpcMind`: GoalType.Bury.
- `SimulationSystems.cs`: Bury/Mourn scoring, goal→interaction map, target
  filters (Bury → Corpse; Mourn → Corpse or Grave), completion branches
  (Bury: despawn corpse + spawn grave + closure + community-wide danger
  cleanup; Observe-at-Grave: VisitedGrave + Social gain).
- No new systems, no new registrations — graves never decay because
  CorpseSystem only touches the Corpse tag.

## Soak Results (seeds 12345, 777, 42, 555, 2024, 7, 31337)

Burial is code-complete and shares its whole approach path with the
verified Mourn flow; its completion has not yet occurred *naturally*:

- Deaths are rare (2 of 7 seeds), and where they happen, the killer pack
  keeps patrolling the site — in seed 777 the body decayed unburied under
  dog guard; the world does not owe anyone a funeral.
- **Seed 7 is the apocalypse seed**: two NPCs killed by vital wounds, and
  at cutoff the last survivor — right leg destroyed, hobbling at 0.4 —
  stands fighting two dogs over his housemates' bodies at the western tree.
  169 leg bites over the soak. Grief fired three times; nobody had a quiet
  hour to dig.

Harness lessons encoded this iteration:
1. **A colony wipe is a valid outcome, not a defect** — quality-of-life
   checks are waived while a wipe is in progress; structural checks are
   never waived.
2. Corpse/grave `CurrentUser` stores the deceased's identity by design and
   is excluded from the orphaned-occupancy leak check.

Per the user: dropped gear is **ownerless loot** — no inheritance concept
exists or is planned; anyone picks it up via normal goals.

## Verification (headless harness, 2 seeds)

1. Death seed (777): burial happens (or the obstruction is honestly
   traced), the grave persists to cutoff, the death tile leaves danger
   memories, remembrance visits occur.
2. No-death seed (12345): zero grave events, no regressions.
3. All structural invariants hold.
