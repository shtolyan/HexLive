namespace HexLive.Simulation.AI
{

// Spec §64: a "dream" (мечта) — a colony-driven, ordered aspiration that steers
// what the settlement BUILDS once basic survival is handled. All NPCs share one
// ordered dream queue; the sim advances it as each dream is fulfilled. A dream
// sits ABOVE ordinary comfort desires but is only pursued when needs are met
// (the free-hands surplus), so survival always outranks it.
//
// Append-only: saves store ints (mirror ComaCause). New dreams go on the end and
// into SpecDream.DefaultQueue.
public enum DreamType
{
    None = 0,
    Campfire = 1, // colony-wide: raise & light the first hearth
    OwnBed = 2    // per-NPC: each colonist wants her own personal bed
}

}
