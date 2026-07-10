# Iteration 11 Plan — Wearables as Items & Undress

## Iteration Target (spec 31A.5A)

Fix the shared-armor model quirk and complete the clothing decision loop:

1. **Worn list**: `NPCState.WornItems`; warmth/armor recomputed as max over
   worn items (Dress effects no longer mutate sticky floats).
2. **Dress consumes**: the garment despawns from the world into WornItems —
   exclusive ownership, clothes become contended like beds.
3. **Undress**: hot (effTemp > 20, Thermal >= 0.4) and safe → take off the
   warmest removable item; it spawns back into the world at the NPC's feet.
   Armor is not removable while a danger memory is fresh — protection beats
   comfort under threat.
4. **Death drops**: everything worn spawns at the death site — loot where
   the dogs won.

## Code Touch Points

- `Simulation/Agents/NpcState.cs`: WornItems list.
- `Simulation/Runtime/SimulationSystems.cs`: equipment recompute helper;
  Dress completion consumes; Undress goal/plan/step (`UndressItem`,
  in-place); Decision availability for Undress; DogSystem death drops.
- `Simulation/AI/NpcMind.cs` / `NpcPlanState.cs`: GoalType.Undress,
  PlanStepType.UndressItem.
- Snapshot/exporter/panel: Worn row.

## Soak Findings

**The death-trap wipe (seed 777, first run):** danger memory guarded strolls
but not meals. A dog pair camped the east fruit tree; three hungry NPCs
walked there one after another and died at the same tile within 700 ticks —
the second victim even looted the first one's dropped armor on the way in.
Fix (spec 29C.4A): GetFood targets and forage destinations within 2 tiles of
fresh danger are skipped — unless Starving (desperation overrides caution).
Post-fix the colony survives that seed, paying for caution with a few extra
short hunger episodes instead of its life.

**Wardrobe economy (seed 12345):** 13 dressings / 10 undressings per 10
days, zero exclusivity violations, and the three wearables settled into
distinct owners (heavy armor / leather / bare coat) — emergent per-NPC
survival profiles: the coat-only NPC recorded the closest call (min HP 0.32).

## Verification (headless harness, 2 seeds × 10 game days)

1. Exclusivity: at any moment at most one NPC has each wearable's stats;
   worn items are absent from the world.
2. Undress events occur on warm days; danger-fresh NPCs keep armor on.
3. Items migrate (undressed at non-original tiles) and get re-worn by
   others.
4. On death, worn items appear as world objects at the death tile.
5. Structural/rhythm/survival invariants hold; seeds diverge.
