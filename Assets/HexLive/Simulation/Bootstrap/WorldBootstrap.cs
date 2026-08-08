using System.Collections.Generic;

namespace HexLive.Simulation.Bootstrap
{

public sealed class WorldBootstrapDefinition
{
    public SimulationBootstrapSettings Simulation { get; set; } = new();

    public EnvironmentBootstrap Environment { get; set; } = new();

    public List<FragmentBootstrap> Fragments { get; set; } = new();

    public List<ObjectBootstrap> Objects { get; set; } = new();

    public List<NpcBootstrap> Npcs { get; set; } = new();

    // §72: where each faction pitches its camp. Empty = a single-camp world,
    // which is exactly the pre-§72 behaviour (and what the test bootstraps want).
    public List<FactionHomeBootstrap> FactionHomes { get; set; } = new();
}

public sealed class FactionHomeBootstrap
{
    public Agents.Faction Faction { get; set; } = Agents.Faction.Colony;

    public int TileQ { get; set; }

    public int TileR { get; set; }

    // Stake a campfire build-site at the anchor. The camp then raises and lights
    // it through the ordinary §54 cold-start chain — no bespoke code.
    public bool StakeCampfireSite { get; set; } = true;
}

public sealed class SimulationBootstrapSettings
{
    public float TickDeltaTime { get; set; } = 0.25f;

    public int MediumTickInterval { get; set; } = 4;

    public int SlowTickInterval { get; set; } = 16;

    // Spec 29C.1: world seed — same seed reproduces the run exactly.
    public int Seed { get; set; } = 12345;

    // Temporary vertical slice for the architectural-building pass: prototype
    // islands start with one complete, usable 1-hex hut beside the colony
    // hearth. Small test bootstraps keep the old empty default.
    public bool SpawnCompletedTestHut { get; set; }
}

public sealed class EnvironmentBootstrap
{
    public float GlobalTemperature { get; set; } = 20f;
}

public sealed class FragmentBootstrap
{
    public int Id { get; set; }

    public List<TileBootstrap> Tiles { get; set; } = new();
}

public sealed class TileBootstrap
{
    public int Q { get; set; }

    public int R { get; set; }

    public bool Walkable { get; set; } = true;

    public bool Indoor { get; set; }

    public bool Blocked { get; set; }

    public bool Water { get; set; }

    // Spec 20.16: island elevation, 0..5.
    public int Elevation { get; set; } = 1;

    public List<int> BlockedSlots { get; set; } = new();
}

public sealed class ObjectBootstrap
{
    public int Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public int FragmentId { get; set; }

    public int TileQ { get; set; }

    public int TileR { get; set; }

    public List<int> JunctionSlots { get; set; } = new();
}

public sealed class NpcBootstrap
{
    public int Id { get; set; }

    // §74: leave DisplayName/ActorMesh EMPTY to have the world roll them from
    // the seed (ColonistAppearance). A filled value is authorial intent and is
    // never overwritten — which is how the outsider and every test scene keep
    // their fixed cast.
    public string DisplayName { get; set; } = string.Empty;

    public string ActorMesh { get; set; } = string.Empty;

    // §74: material donor / hair prefab / voice folder. Empty = rolled too.
    public string SkinSet { get; set; } = string.Empty;

    // §85: iris colour, an axis of its own. Empty = rolled.
    public string EyeColor { get; set; } = string.Empty;

    public string Hairstyle { get; set; } = string.Empty;

    public string VoiceBank { get; set; } = string.Empty;

    // §72: Colony (the girls) unless the world explicitly seeds an outsider.
    public Agents.Faction Faction { get; set; } = Agents.Faction.Colony;

    public int FragmentId { get; set; }

    public int TileQ { get; set; }

    public int TileR { get; set; }

    public float Hunger { get; set; }

    public float Thirst { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

    public float ThermalDiscomfort { get; set; }

    // §76: authored characteristics, same blank-means-roll rule as §74's
    // appearance. Empty = the world rolls all six from the seed; an entry here
    // pins that one attribute and leaves the rest rolled, which is how a
    // headless probe pits a deliberately strong girl against a deliberately
    // frail one without hunting for a seed that happens to produce them.
    //
    // NOTE a pinned value breaks the point-buy budget on purpose — it is
    // authorial intent, not a roll.
    public Dictionary<Agents.AttributeKind, float> Attributes { get; } = new();
}

}
