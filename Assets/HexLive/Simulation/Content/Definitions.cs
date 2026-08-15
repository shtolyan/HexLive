using System.Collections.Generic;

namespace HexLive.Simulation.Content
{

public sealed class ObjectDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<InteractionDefinition> Interactions { get; } = new();

    // §59-склад: начальное СОДЕРЖИМОЕ объекта (массив) — вода в дырявом
    // кокосе и т.п. При спавне уходит в WorldObjectState.ResourceAmount /
    // ItemInstance.ResourceAmount; ассеты объявляют это в разделе «Склад».
    public List<StoredResource> Storage { get; } = new();

    /// <summary>Заявленное количество ресурса на складе (0 — не заявлен).</summary>
    public float StoredAmount(StoredKind kind)
    {
        foreach (var stored in Storage)
        {
            if (stored.Kind == kind)
            {
                return stored.Amount;
            }
        }

        return 0f;
    }

    public List<string> Tags { get; } = new();

    // 0 = no per-NPC limit. A positive value is a content-authored carrying
    // invariant enforced by the shared inventory admission path. This belongs
    // to the item definition rather than AI goal-specific lists: any current
    // or future way of acquiring the item sees the same rule.
    public int MaxCarriedInstances { get; set; }

    // Spec 43: height of this object's shade blocker in ELEVATION steps
    // (0.55 wu each), read only for "Shade"-tagged objects. Must roughly match
    // the rendered mesh so the sim shadow lands where the player sees one
    // (palm_final is 3.9 wu tall -> 7 steps; the default 2 fits low canopies
    // like the tent).
    public float ShadeSteps { get; set; } = 2f;

    public ProduceDefinition? Produce { get; set; }

    // Spec 31C.7: > 0 blocks every junction within this world-unit radius
    // of the anchor (solid furniture); 0 blocks the anchor only.
    public float ObstacleRadius { get; set; }

    // Spec §113: ФИЗИЧЕСКИЙ радиус самой вещи в мировых единицах — то, во что
    // телу нельзя лечь. Намеренно ОТДЕЛЬНОЕ число от ObstacleRadius: тот про
    // проходимость и бывает много шире вещи (у костра это 0.55R угольного
    // кольца, сквозь которое не ходят, а сам огонь втрое меньше), так что
    // лежание по нему выгнало бы тело с гекса костра целиком. 0 = мерить по
    // ObstacleRadius (кровати и станции объявляют там свой честный габарит) и,
    // для Obstacle-вещей без габарита, по полу Spec49.LieSolidRadiusFloorFactor.
    public float SolidRadius { get; set; }

    // Spec 31A.5B: wearable metadata (null = not wearable).
    public WearLayer? Layer { get; set; }

    public List<BodyPart> Covers { get; } = new();

    // Spec §52: inventory slots this item grants while worn (garments only).
    // The pack has no base capacity — the body has 2 hands and each worn piece
    // adds its pockets. 0 for non-wearables and accessories with no pockets.
    public int InventoryCapacity { get; set; }
}

// Spec 31A.5B (molly port): three clothing layers.
public enum WearLayer
{
    Underwear,
    Wear,
    Outerwear,

    // Сумки: рюкзаки и подсумки, поверх всего. Порядок обязан совпадать с
    // VisualWearLayer — презентация приводит одно к другому по числу.
    Bags
}

// Spec 19.3C (molly bones, simplified): body zones for wounds and coverage.
public enum BodyPart
{
    Head,
    Torso,
    Pelvis,
    ArmL,
    ArmR,
    LegL,
    LegR
}

public sealed class ProduceDefinition
{
    public string ProducedDefinitionId { get; set; } = string.Empty;

    public int IntervalTicks { get; set; }

    public int MaxConcurrent { get; set; }

    public int MaxDistanceTiles { get; set; }
}

/// <summary>Виды складируемых ресурсов (типизировано — §59: никаких строк).
/// Новый вид = новый член enum.</summary>
public enum StoredKind
{
    Water = 0,
}

/// <summary>Одна строка «склада» объекта: что лежит и сколько. Дырявый кокос:
/// Water × 4 (глотка́). Runtime кладёт Amount в ResourceAmount при спавне.</summary>
public sealed class StoredResource
{
    public StoredKind Kind { get; set; } = StoredKind.Water;

    public float Amount { get; set; } = 1f;
}

public sealed class InteractionDefinition
{
    public string Id { get; set; } = string.Empty;

    public InteractionType Type { get; set; }

    // §gear: the SKILLS this action accepts — TYPED capabilities, ANY-OF: a
    // log splits under an axe (ChopWood) OR a knife (Cut) when both are
    // listed. Empty = no tool needed. Execution gates GENERICALLY: the actor
    // must hold any gear granting at least one listed capability — content
    // names VERBS (enum), never tools and never free strings.
    public List<GearCapability> RequiredCapabilities { get; } = new();

    public int DurationTicks { get; set; }

    public InteractionEffects Effects { get; } = new();

    // Spec §54: data-driven harvest/process/butcher output. When non-empty, the
    // completion handler spawns these drops (scattered on the ground) instead of
    // the old hardcoded tag switch. One verb, one yield list — a palm's chop
    // drops logs+leaves, a log's Process drops sticks, a carcass's Butcher drops
    // meat+hide. Empty ⇒ this verb yields nothing (e.g. Eat/Sit/Sleep).
    public List<HarvestDrop> Yields { get; } = new();
}

// Spec §54: one line of a Yields table — "spawn Count of DefinitionId". Scatter
// = land on distinct nearby junctions (logs around the stump) rather than pile
// at the actor's feet.
public sealed class HarvestDrop
{
    public string DefinitionId { get; set; } = string.Empty;

    public int Count { get; set; } = 1;

    public bool Scatter { get; set; } = true;
}

public sealed class InteractionEffects
{
    public float HungerDelta { get; set; }

    public float EnergyDelta { get; set; }

    public float ComfortDelta { get; set; }

    public float ThermalDelta { get; set; }

    public float ThirstDelta { get; set; }

    public float WarmthDelta { get; set; }

    // Spec 29C.4: fraction of incoming damage absorbed when equipped.
    public float ArmorDelta { get; set; }
}

public enum InteractionType
{
    Eat,
    Drink,
    PickUp,
    Sit,
    Sleep,
    Dress,
    Undress,
    Fuel,
    Craft,
    Harvest,
    Process,    // spec §54: split a log into sticks (in the field, needs an axe)
    Butcher,    // spec §54: knife a carcass/corpse into meat + hide
    Build,
    // §28.15C v3: похороны сняты. Ординал остаётся занят — сейв хранит текущее
    // взаимодействие числом, и вырезание середины перемаркировало бы все
    // последующие в каждом существующем сейве.
    Bury,
    Observe,
    Talk,
    Hang,
    FillBottle, // spec 29H: charge the water bottle at a source
    BuildRaft,  // spec 40.15: haul logs to the escape raft
    FeedOther,     // spec 53: share a meal with a starving housemate
    TreatOther,    // spec 53: dress a wounded housemate's wound
    MedicateOther, // spec 53: hand a pill to a sick / gravely weak housemate
    ConsoleOther,  // spec 53: sit with a grieving / stressed housemate
    CoolOff,       // spec 35.4: dwell in shade/water to shed heat
    HydrateOther,  // spec 53: bring water to a parched housemate
    WashClothes,   // gathering loop with a garment held at the shoreline
    // §54.15: the water collector's vessel slot (the WC_point marker on the
    // stone stand). PlaceVessel sets an empty container down under the leaf
    // funnel; TakeVessel lifts it back off. Declared here so the station's
    // content definition can name the two verbs; the decision/execution
    // wiring (who carries a bottle there, and rain filling it over time)
    // is the next step and does not read these yet.
    PlaceVessel,
    TakeVessel,
    // §68: self first-aid — the wounded girl dresses her OWN wounds with a
    // bandage from her pack (the mirror of TreatOther, which only ever let a
    // HOUSEMATE do it). Appended at the end: saves store interactions by
    // ordinal.
    TreatSelf,
    // §81: сцена насилия. Дописано в конец — сейв хранит взаимодействия
    // ординалом.
    Abuse,
    // §28.15F: обобрать тело — снять с покойной ОДНУ вещь. Сначала карманы,
    // потом одежда; за каждой вещью надо прийти отдельно. Дописано в конец, по
    // той же причине.
    Loot,
    // §116 append-only medical/rescue verbs.
    PickUpPerson,
    PutInBed,
    Splint,
    FitProsthetic,
    // §137: праздный отдых — незанятая колонистка садится на землю там, где
    // стоит. Дописано в конец, по той же причине, что и всё выше: сейв хранит
    // текущее взаимодействие ординалом.
    Rest
}

}
