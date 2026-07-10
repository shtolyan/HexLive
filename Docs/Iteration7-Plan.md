# Iteration 7 Plan — Conflicts & Relationship Dynamics

## Iteration Target

Break the eternal-friendship saturation (iterations 4–6: Talk always
succeeds, Affinity climbs monotonically to 1.0 and stays). Per spec 28.15B:

1. **Quarrels**: talk outcome rolled deterministically (hash of tick +
   participant ids). Chance grows with the crankier participant's
   irritability (hungry/tired NPCs are snappy) and shrinks with mutual
   affinity. Quarrel: Affinity -0.12 both ways, Embarrassment +0.30,
   reduced Social gain, initiator gets a Socialize cooldown.
2. **Signed affinity** (-1..+1): dislike is now representable.
3. **Refusal by dislike**: listeners refuse initiators they dislike
   (affinity < -0.25) — unless lonely (Social < 0.25): reconciliation talks.
   Rejection costs the initiator -0.05 affinity toward the rejector.
4. **Resentment from scarcity**: finding your bed/food occupied by another
   NPC costs -0.08 affinity toward the occupant.
5. **Embarrassment**: +0.30 per quarrel, decays 0.02/slow tick, subtracts
   from the Socialize score — post-quarrel withdrawal.

## Code Touch Points

- `Simulation/Runtime/SimulationSystems.cs`: outcome roll + quarrel path in
  RunTalk, refusal-by-dislike + loneliness override, rejected-initiator
  penalty, resentment in the InteractionBlocked guard, embarrassment decay
  in NeedsDecaySystem, Socialize modifier in DecisionSystem.
- `Simulation/Common/MathUtil.cs`: signed clamp + stateless hash-to-float.
- Affinity clamp sites move from Clamp01 to [-1, 1].

## Balance Findings (three soak-run lessons, all spec'd in 28.15B)

1. **Neutral refusals must not offend.** Penalizing every rejection (-0.05,
   including "busy"/"walking") produced 157 rejections × -0.05 — an
   irreversible hostility spiral that no amount of successful talks could
   outweigh. Only *personal* (dislike) refusals now cost affinity.
2. **Talk economics must be net-positive at neutral.** The
   crankier-participant-max irritability made 41 % of talks quarrel;
   with -0.12 per quarrel vs +0.05 per success, relationships could only
   fall. Mean irritability + friendship protection + capped hostility spice
   put a calm neutral talk at ~20 % quarrel risk (slightly net-positive) and
   a reconciliation success at ~75 %.
3. **Grudge drift must be asymmetric.** Symmetric drift (0.002/slow tick)
   healed grudges but also outran friendship gains — warmth capped at ~+0.2.
   Negative side 0.003, positive side 0.001: resentment cools in a day or
   two, friendship needs only light upkeep.

Final soak: affinity traveled -0.29..+0.33 with quarrels (22), successes
(50), and recovery cycles; both NPCs socially and physically healthy.

## Verification (headless harness, 10 game days)

1. Quarrels occur (> 3) and refusals/resentment appear in traces.
2. Affinity trajectory is non-monotonic: dips below 0 at least once AND
   talks continue afterward (no permanent social collapse).
3. Survival and the day/night rhythm from iteration 6 stay healthy.
4. No reservation/occupancy leaks.
