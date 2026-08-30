namespace HexLive.Simulation.Agents
{

// Spec §76: the eight learned trades. WHAT she can do — starts at 0 and grows
// with practice, unlike Attributes (who she is) which are fixed at spawn.
//
// The enum is APPEND-ONLY: it is written to the save blob positionally and
// SkillSet.All drives both the exporter and the UI tab.
public enum SkillKind
{
    Combat,       // бой — landed melee hits
    Harvesting,   // добыча — Harvest / Process (chopping, mining, yucca)
    Crafting,     // ремесло — Craft goals
    Building,     // строительство — Build / BuildFurniture
    Cooking,      // готовка — CookMeat
    Medicine,     // врачевание — TreatSelf / TreatOther / MedicateOther
    Survival,     // выживание — fire tending, bottle filling, butchering
    Social,       // общение — Talk / ConsoleOther
    Athletics     // §76.14 (bug #304) — бег и физическая работа: дыхание, выносливость
}

// Spec §76: eight floats in 0..1, displayed to the player as levels 0..10.
// Same named-property rationale as AttributeSet — these are read on the hot
// duration path and must stay greppable. No decay: a trade once learned is
// not forgotten, only out-paced by the girls who kept practising.
public sealed class SkillSet
{
    public static readonly SkillKind[] All =
    {
        SkillKind.Combat,
        SkillKind.Harvesting,
        SkillKind.Crafting,
        SkillKind.Building,
        SkillKind.Cooking,
        SkillKind.Medicine,
        SkillKind.Survival,
        SkillKind.Social,
        SkillKind.Athletics
    };

    // Everyone starts a rank amateur — 0 means "no bonus at all", so an
    // un-rolled or pre-§76 body is the pre-§76 game exactly.
    public float Combat { get; set; }

    public float Harvesting { get; set; }

    public float Crafting { get; set; }

    public float Building { get; set; }

    public float Cooking { get; set; }

    public float Medicine { get; set; }

    public float Survival { get; set; }

    public float Social { get; set; }

    // §76.14 (bug #304): бег и физическая работа. Дольше дыхание при беге,
    // быстрее раскрывается Выносливость.
    public float Athletics { get; set; }

    public float Get(SkillKind kind) => kind switch
    {
        SkillKind.Combat => Combat,
        SkillKind.Harvesting => Harvesting,
        SkillKind.Crafting => Crafting,
        SkillKind.Building => Building,
        SkillKind.Cooking => Cooking,
        SkillKind.Medicine => Medicine,
        SkillKind.Survival => Survival,
        SkillKind.Social => Social,
        SkillKind.Athletics => Athletics,
        _ => 0f
    };

    public void Set(SkillKind kind, float value)
    {
        switch (kind)
        {
            case SkillKind.Combat: Combat = value; break;
            case SkillKind.Harvesting: Harvesting = value; break;
            case SkillKind.Crafting: Crafting = value; break;
            case SkillKind.Building: Building = value; break;
            case SkillKind.Cooking: Cooking = value; break;
            case SkillKind.Medicine: Medicine = value; break;
            case SkillKind.Survival: Survival = value; break;
            case SkillKind.Social: Social = value; break;
            case SkillKind.Athletics: Athletics = value; break;
        }
    }
}

}
