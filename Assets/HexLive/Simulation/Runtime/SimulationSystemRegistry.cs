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
        engine.Register(new RescueSystem());
        engine.Register(new DecisionSystem());
        engine.Register(new ProstheticAidSystem()); // §119: persistent compassion chain
        engine.Register(new PlanningSystem());
        engine.Register(new MobSystem());
        // §72.14: create a due attacker before RaidSystem scans for hunters, so
        // a landing at a boundary joins the ordinary faction/GOAP machinery in
        // the same medium pass.
        engine.Register(new RaidWaveSystem());
        // §115: после MobSystem (перезажечь боевую сцепку), до RaidSystem
        // (чтобы общая метла пар увидела живое обоснование Expel).
        engine.Register(new CampExpulsionSystem());
        // §72: AFTER MobSystem — it clears IsFighting for every NPC each medium
        // pass, so anything that sets the latch has to run later.
        engine.Register(new RaidSystem()); // §72: the outsider's hunt and the colony's answer
        // §121: AFTER RaidSystem, and for the same latch reason as GroupHunt —
        // MobSystem clears IsFighting for everyone each medium pass, so the
        // player's attack order has to re-assert it later than anyone who might
        // dissolve the pair. After AnswerBlows too: a manual NPC standing idle
        // answers blows by the ordinary §109 path, and only then does her own
        // order (if any) get its say.
        engine.Register(new ManualOrderSystem());
        // §108: AFTER RaidSystem for the same reason it runs after MobSystem —
        // the latch has to be the last word on IsFighting. After the raid, not
        // before: if he is mid-raid when the party arrives, his own scene stays
        // in charge of that tick and the hunt simply piles on.
        engine.Register(new GroupHuntSystem()); // §108: the girls' pact and the beating
        // §72: BEFORE AnimalCombatSystem — a body has one swing slot, and a man
        // with a knife outranks a dog for that tick of attention.
        engine.Register(new HumanCombatSystem()); // §72: timed human-vs-human blows
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

        // §30.15: LAST, and it is the one system whose position does not matter —
        // it only reads and emits. It goes at the end so it sees the tick as it
        // finally settled, rather than half-way through someone else's pass.
        engine.Register(new StuckDiagnosticSystem());

        // §122: сторож ПЕТЕЛЬ, а не застоя. Стоит после §30.15 и тоже в фазе 1
        // ничего не меняет — но слой у него Fast, а не Slow, и это не описка:
        // Slow идёт раз в 16 тиков, а цель успевает быть выбранной и брошенной
        // за четыре, так что реестр намерений пропускал бы завершения — то
        // единственное, на чём держится отличие работы от петли.
        engine.Register(new LoopDiagnosticSystem());

        // NOT registered, on purpose-of-record rather than by decision: SharkSystem.
        // It is implemented (Systems/Wildlife/SharkSystem.cs, TickLayer.Medium) and
        // world.Sharks IS persisted by WorldSaveSerializer, so sharks are spawned and
        // saved — but never stepped, and have never been in any shipped run. Adding it
        // here would change live behaviour, which this move must not do. Left out to
        // preserve parity; revisit as its own change, not as a side effect of a refactor.
    }
}

}
