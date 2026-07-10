# Iteration 6 Plan — Day/Night Cycle & Climate Rhythm

## Iteration Target

Give the world a clock (spec 19.7A) so behavior gains a daily rhythm instead
of a flat tick march:

1. **Clock**: 2400-tick day (10 min real), quarters Morning/Day/Evening/Night,
   tick 0 = 06:00. `EnvironmentState` gains `TimeOfDayNormalized` + `Phase`.
2. **Temperature sinusoid**: 12 ± 6 °C, warmest 15:00, coldest 03:00. The
   existing 12° discomfort threshold now means comfortable days, cold nights —
   the coat becomes an evening ritual.
3. **Daylight fruit production**: trees drop only in Morning/Day; overdue
   timers fire at dawn ("morning apples").
4. **Night sleep bias**: Sleep gets +0.25 (Night) / +0.10 (Evening) through
   the previously dead `EnvironmentModifier` score field.
5. **Second bed** (id 106 @ (2,0)): two inhabitants who both want to sleep at
   night should not fight over one bed every single evening.

## Code Touch Points

- `Simulation/Core/EnvironmentState.cs`: clock fields + `DayPhase` enum.
- `Simulation/Runtime/SimulationSystems.cs`: new `EnvironmentSystem` (Slow,
  registered before needs/temperature), daylight gate in
  FruitProductionSystem, Sleep environment bonus in DecisionSystem.
- `UnityPresentation/Bootstrap/SimulationRunnerBehaviour.cs`: registration.
- `Simulation/Bootstrap/PrototypeWorldDefinitionFactory.cs`: bed 106.
- Snapshot/exporter/panel: clock + phase display.

## Balance Findings (from soak runs)

Daylight-only production destabilized the food economy in two steps; both
fixes are spec'd (29A.4, 29B.2):

1. **Supply**: the halved production window needed interval 160→100 and
   cap 3→4, and a more nourishing apple (Eat -0.45→-0.60) so one meal covers
   more dark ticks. Aggregate supply alone did NOT fix the famine —
2. **Timing**: NPCs harvested every apple the moment it dropped, so the
   ground stock was empty by dusk and the productionless night starved them
   nightly (StatusStarving 52-58 per 10 days). The restraint gate
   (GetFood hard-gated below Hunger 0.35) lets stock survive to dusk;
   starving events dropped to 4 per 10 days and dark-sleep ratio rose to 81 %.

Lesson recorded: in a rhythmic world, *when* food exists matters more than
*how much* — behavioral restraint beat supply increases.

## Verification (headless harness, 24000 ticks = 10 game days)

1. `ProduceDropped` events occur only in Morning/Day phases.
2. Sleep completions cluster in Evening/Night (> 50 % of sleeps).
3. Temperature range observed ≈ 6..18; thermal discomfort cycles instead of
   monotonic growth; Dress happens mostly in cold phases.
4. Survival, talks, and memory behavior from iterations 2–5 stay healthy;
   no reservation/occupancy leaks.
