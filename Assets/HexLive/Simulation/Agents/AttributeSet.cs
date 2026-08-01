namespace HexLive.Simulation.Agents
{

// Spec §76: the six innate characteristics. WHO she is — rolled once from the
// world seed at spawn and fixed for life, unlike Skills (what she has learned)
// which grow with practice.
//
// The enum is APPEND-ONLY: it is written to the save blob positionally and
// AttributeSet.All drives both the exporter and the UI tab.
public enum AttributeKind
{
    Strength,   // сила — melee damage, carry slots, heavy work
    Agility,    // ловкость — move/turn speed, swing tempo
    Endurance,  // выносливость — stamina pool & drain, breath, sleep
    Toughness,  // стойкость — incoming damage, bleeding, healing
    Hardiness,  // неприхотливость — hunger/thirst rates, thermal tolerance
    Wits        // смекалка — skill learning rate, craft speed
}

// Spec §76: six floats in 0..1, meaning "how far from the human average".
//
// Named properties on purpose, not a float[] or a Dictionary. MovementSystem
// and NeedsDecaySystem read these every tick per NPC (a field load beats a
// bounds-checked index and beats a hash by a mile), and — more importantly —
// `Attributes.Wits` greps to every consumer while `attrs[5]` greps to none.
// All/Get exist so the exporter and the UI can loop instead of copy-pasting
// six near-identical blocks that drift apart.
public sealed class AttributeSet
{
    public static readonly AttributeKind[] All =
    {
        AttributeKind.Strength,
        AttributeKind.Agility,
        AttributeKind.Endurance,
        AttributeKind.Toughness,
        AttributeKind.Hardiness,
        AttributeKind.Wits
    };

    // 0.5 is the §76 mean, so an UN-ROLLED body (a test scene bootstrap, a
    // pre-§76 save) has every multiplier at exactly 1.0 — the pre-§76 game,
    // byte for byte. Never change this default without re-soaking.
    public float Strength { get; set; } = 0.5f;

    public float Agility { get; set; } = 0.5f;

    public float Endurance { get; set; } = 0.5f;

    public float Toughness { get; set; } = 0.5f;

    public float Hardiness { get; set; } = 0.5f;

    public float Wits { get; set; } = 0.5f;

    public float Get(AttributeKind kind) => kind switch
    {
        AttributeKind.Strength => Strength,
        AttributeKind.Agility => Agility,
        AttributeKind.Endurance => Endurance,
        AttributeKind.Toughness => Toughness,
        AttributeKind.Hardiness => Hardiness,
        AttributeKind.Wits => Wits,
        _ => 0.5f
    };

    public void Set(AttributeKind kind, float value)
    {
        switch (kind)
        {
            case AttributeKind.Strength: Strength = value; break;
            case AttributeKind.Agility: Agility = value; break;
            case AttributeKind.Endurance: Endurance = value; break;
            case AttributeKind.Toughness: Toughness = value; break;
            case AttributeKind.Hardiness: Hardiness = value; break;
            case AttributeKind.Wits: Wits = value; break;
        }
    }
}

}
