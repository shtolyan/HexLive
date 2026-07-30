namespace HexLive.Simulation.Social
{

// Spec 28.15E: the subject of a Talk interaction. Chosen deterministically at
// talk start, biased by the participants' situation (hunger, cold, mood, the
// island's dangers) so the overhead bubble reflects what they'd plausibly be
// on about. Presentation maps each value to an emoji glyph; the sim only picks.
//
// NOTE: kept as a flat enum (no per-value payload) so it stays trivially
// serialisable in the snapshot and resume-safe. Emoji/colour/localised label
// live entirely in the presentation layer (TalkTopicVisuals).
public enum TalkTopic
{
    // Neutral filler — the default when nothing in particular is pressing.
    SmallTalk,

    // Island-survival themes (the game's setting — always in the pool).
    Escape,   // plans to leave — the raft, a passing ship, "we'll get off this rock"
    Sharks,   // the thing in the water; who dares swim
    Dogs,     // the wild dogs; keeping watch
    Weather,  // the cold, the rain, the sun
    Food,     // coconuts, the catch, who's cooking
    Fire,     // the campfire, keeping it lit, warmth

    // Relationship-coloured themes (picked from mutual affinity + mood).
    Home,     // homesickness, the life left behind
    Gossip,   // chatter about a third housemate
    Flirt,    // warmth, closeness, a spark
    Joke,     // teasing, laughter
    Grumble,  // a complaint, friction, an argument brewing

    // §67.10 personal complaints — NOT a shared subject but what THIS speaker
    // is going through: she tells her housemate she's starving / parched / hurt.
    // Picked per participant (each has her own CurrentTalkTopic), so a
    // conversation can be "I'm hungry" answered by "the fire's dying". Kept
    // last in the enum: values are ints in traces/snapshots and appending is
    // the only safe extension.
    Hunger,   // "I'm starving" — her own empty belly
    Thirst,   // "I'm parched"
    Pain,     // "I'm hurt" — an open wound
    Tired,    // "I'm dead on my feet"
    Cold      // "I'm freezing" — her own body, not the weather in general
}

}
