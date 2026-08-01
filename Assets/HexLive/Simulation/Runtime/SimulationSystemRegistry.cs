namespace HexLive.Simulation.Runtime
{

/// <summary>
/// The canonical system list — what "the simulation" means, in one place.
/// <para>
/// This used to live in the Unity runner, which made it invisible to everything
/// that is not Unity: a headless probe or a server had to hand-copy 24 lines and
/// then silently drift from the game it was supposed to measure. Order matters
/// (registration order is execution order inside a tick layer), so a copy that is
/// merely *complete* is still not necessarily *correct*.
/// </para>
/// </summary>
public static class SimulationSystemRegistry
{
    /// <summary>
    /// Registers every system the shipped game runs, in the order it runs them.
    /// Effective per-<see cref="SimulationEngine.Step"/> order is Fast → Medium →
    /// Slow; within a layer, the order below.
    /// </summary>
    public static void RegisterDefaults(SimulationEngine engine)
    {
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        engine.Register(new PerceptionSystem());
        engine.Register(new DecisionSystem());
        engine.Register(new PlanningSystem());
        engine.Register(new MobSystem());
        engine.Register(new AnimalCombatSystem()); // 29C.3 v2: timed windup→hit→cooldown blows
        engine.Register(new PredationSystem()); // §56: kill-a-housemate-to-eat
        engine.Register(new ThreatAlertSystem()); // §62: spot the wolf early — ⚠️ cue, attack-first or detour
        engine.Register(new RabbitSystem());
        engine.Register(new WeatherSystem());
        engine.Register(new EnvironmentSystem());
        engine.Register(new NeedsDecaySystem());
        engine.Register(new TemperatureSystem());
        engine.Register(new MoistureSystem());
        engine.Register(new FruitProductionSystem());
        engine.Register(new FireSystem());
        engine.Register(new CorpseSystem());
        engine.Register(new MeatSpoilageSystem()); // §54: ground meat rots
        engine.Register(new DreamSystem()); // §64: advances the colony dream queue (campfire → own bed); before BedSiteSystem
        engine.Register(new BedSiteSystem()); // §54.2: stakes progressive bed build-sites
        engine.Register(new WaterCollectorSystem()); // §54.15: rain fills the parked bottle
        engine.Register(new HazardSystem()); // §50: prepared amputation hazards

        // NOT registered, on purpose-of-record rather than by decision: SharkSystem.
        // It is implemented (Systems/Wildlife/SharkSystem.cs, TickLayer.Medium) and
        // world.Sharks IS persisted by WorldSaveSerializer, so sharks are spawned and
        // saved — but never stepped, and have never been in any shipped run. Adding it
        // here would change live behaviour, which this move must not do. Left out to
        // preserve parity; revisit as its own change, not as a side effect of a refactor.
    }
}

}
