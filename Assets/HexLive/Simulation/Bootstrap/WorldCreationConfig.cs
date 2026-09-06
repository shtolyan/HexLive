using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Bootstrap
{
// The entire creation recipe belongs to one world. Never apply it to balance statics.
public sealed class WorldCreationConfig
{
    public int Version { get; set; } = 1;
    public long CatalogRevision { get; set; }
    public string WorldId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Seed { get; set; } = 12345;
    public GameMode Mode { get; set; }
    public string CreatorPlayerId { get; set; } = string.Empty;
    public Faction PlayerCamp { get; set; }
    public int PopulationLimit { get; set; } = 10;
    public int SeaRaidIntervalDays { get; set; }
    public List<CampCreationConfig> Camps { get; set; } = new();
    public List<CharacterCreationConfig> Characters { get; set; } = new();
    public CampCreationConfig Camp(Faction faction) => Camps.Find(c => c != null && c.Faction == faction);
    public bool Owns(NPCState npc)
    {
        if (npc.Faction != PlayerCamp) return false;
        var authored = Characters.Find(c => c.Id == npc.Id.Value);
        return authored != null ? authored.Enabled && authored.Controlled : Camp(PlayerCamp)?.ControlArrivals == true;
    }
}
public sealed class CampCreationConfig
{
    public Faction Faction { get; set; }
    public bool Enabled { get; set; } = true;
    public int PopulationLimit { get; set; } = 5;
    public int ArrivalIntervalDays { get; set; } = 7;
    public bool ControlArrivals { get; set; }
}
public sealed class CharacterCreationConfig
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Controlled { get; set; }
    public Faction Camp { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = "Molly";
    public string Skin { get; set; } = "Molly";
    public string Eyes { get; set; } = "blue";
    public string Hair { get; set; } = "none";
    // Empty preserves legacy deterministic colour; "prototype" explicitly selects the original.
    public string HairColour { get; set; } = string.Empty;
    public string Voice { get; set; } = "molly";
    public List<string> Clothing { get; set; } = new();
    public List<CreationValue> Attributes { get; set; } = new();
    public List<CreationValue> Skills { get; set; } = new();
    public List<string> Traits { get; set; } = new();
}
public sealed class CreationValue
{
    public string Id { get; set; } = string.Empty;
    public float Value { get; set; }
}
public sealed class CreationError
{
    public string Path { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public sealed class WorldCreationPlacementException : Exception
{
    public int CharacterId { get; }
    public WorldCreationPlacementException(int characterId) : base("No free spawn point for NPC " + characterId) => CharacterId = characterId;
}

// Engine-free, bounded transport for save/header consumers; HTTP uses ordinary JSON DTOs.
public static class WorldCreationCodec
{
    private static readonly XmlSerializer Serializer = new(typeof(WorldCreationConfig));
    public static string Encode(WorldCreationConfig config)
    {
        if (config == null) return string.Empty;
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        Serializer.Serialize(writer, config);
        return writer.ToString();
    }
    public static WorldCreationConfig Decode(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > 2_000_000) throw new InvalidDataException("World creation config too large.");
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        WorldCreationConfig result;
        try { result = (WorldCreationConfig)Serializer.Deserialize(reader); }
        catch (InvalidOperationException ex) { throw new InvalidDataException("Invalid world creation config.", ex); }
        if (result.Version != 1) throw new InvalidDataException("Unsupported world creation version.");
        return result;
    }
}

public static class WorldCreation
{
    public static WorldCreationConfig Defaults(WorldState world)
    {
        var config = new WorldCreationConfig
        {
            Seed = world.Seed, Mode = world.Mode,
            PopulationLimit = PopulationArrivalMath.MaxLivingNpcsFor(world.Mode),
            SeaRaidIntervalDays = world.Mode == GameMode.Islands ? Spec72.RaidWaveIntervalDays : 0,
        };
        foreach (var faction in world.FactionHomes.Keys.OrderBy(f => (int)f))
            config.Camps.Add(new CampCreationConfig
            {
                Faction = faction,
                PopulationLimit = faction == Faction.Outsiders ? WorldBalance.MaxOutsiderNpcs : PopulationArrivalMath.MaxCampNpcsFor(world.Mode),
                ArrivalIntervalDays = faction == Faction.Outsiders ? Spec72.RaidWaveIntervalDays : WorldBalance.ColonyArrivalIntervalDays,
            });
        foreach (var npc in world.Entities.Npcs.Values.OrderBy(n => n.Id.Value))
        {
            var entry = new CharacterCreationConfig
            {
                Id = npc.Id.Value, Camp = npc.Faction, ProfileId = npc.ProfileId,
                Name = npc.DisplayName, Body = npc.ActorMesh, Skin = npc.SkinSet,
                Eyes = npc.EyeColor, Hair = npc.Hairstyle, Voice = npc.VoiceBank,
                Clothing = npc.WornItems.Select(i => i.DefinitionId).ToList(),
                Controlled = npc.Faction == Faction.Colony,
                Attributes = AttributeSet.All.Select(k => new CreationValue { Id = k.ToString(), Value = npc.Attributes.Get(k) }).ToList(),
                Skills = SkillSet.All.Select(k => new CreationValue { Id = k.ToString(), Value = npc.Skills.Get(k) }).ToList(),
                Traits = TraitSet.All.Where(npc.Traits.Has).Select(k => k.ToString()).ToList(),
            };
            config.Characters.Add(entry);
        }
        return config;
    }

    public static List<CreationError> Validate(WorldCreationConfig config, WorldBootstrapDefinition original)
    {
        var errors = new List<CreationError>();
        void Error(string path, string code) => errors.Add(new CreationError { Path = path, Code = code });
        if (config == null) { Error("config", "required"); return errors; }
        if (config.Version != 1) Error("version", "unsupported");
        if (!Enum.IsDefined(typeof(GameMode), config.Mode)) Error("mode", "unknown");
        if (string.IsNullOrWhiteSpace(config.Name) || config.Name.Length > 100) Error("name", "length");
        if (!Guid.TryParseExact(config.CreatorPlayerId, "N", out _)) Error("creatorPlayerId", "invalid");
        if (config.PopulationLimit < 1 || config.PopulationLimit > 256) Error("populationLimit", "range");
        if (config.SeaRaidIntervalDays < 0 || config.SeaRaidIntervalDays > 3650) Error("seaRaidIntervalDays", "range");
        if (config.Camps == null || config.Characters == null) { Error("config", "required"); return errors; }
        if (config.Camps.Count > 7 || config.Characters.Count > 256) { Error("config", "capacity"); return errors; }
        var available = new HashSet<Faction>(original.FactionHomes.Select(c => c.Faction));
        var seen = new HashSet<Faction>();
        for (var i = 0; i < config.Camps.Count; i++)
        {
            var camp = config.Camps[i]; var path = "camps[" + i + "]";
            if (camp == null) { Error(path, "required"); continue; }
            if (!available.Contains(camp.Faction) || !seen.Add(camp.Faction)) Error(path, "unknownOrDuplicate");
            if (camp.PopulationLimit < 0 || camp.PopulationLimit > config.PopulationLimit) Error(path + ".populationLimit", "range");
            if (camp.ArrivalIntervalDays < 0 || camp.ArrivalIntervalDays > 3650) Error(path + ".arrivalIntervalDays", "range");
        }
        if (!seen.SetEquals(available)) Error("camps", "incomplete");
        if (!FactionRelations.IsGirlCamp(config.PlayerCamp) || config.Camp(config.PlayerCamp)?.Enabled != true) Error("playerCamp", "invalid");
        var ids = new HashSet<int>();
        for (var i = 0; i < config.Characters.Count; i++)
        {
            var npc = config.Characters[i]; var path = "characters[" + i + "]";
            if (npc == null) { Error(path, "required"); continue; }
            // Keep authored ids out of the runtime arrival bands (1000+).
            if (npc.Id < 1 || npc.Id >= 1000 || !ids.Add(npc.Id)) Error(path + ".id", "unknownOrDuplicate");
            if (!available.Contains(npc.Camp)) Error(path + ".camp", "unknown");
            if (!string.IsNullOrEmpty(npc.ProfileId) && (!CharacterPresetRegistry.ProfileIds.Contains(npc.ProfileId) || npc.Id != (npc.ProfileId == NikaCharacterProfile.ProfileId ? NikaCharacterProfile.ReservedNpcId : MashaCompanionProfile.ReservedNpcId))) Error(path + ".profileId", "invalid");
            if (npc.Controlled && npc.Camp != config.PlayerCamp) Error(path + ".controlled", "otherCamp");
            if (string.IsNullOrWhiteSpace(npc.Name) || npc.Name.Length > 64 || npc.Name.Any(char.IsControl)) Error(path + ".name", "length");
            if (!ColonistAppearance.Meshes.Contains(npc.Body) && npc.Body != "Kshishtof" && npc.Body != "Tonny") Error(path + ".body", "unknown");
            ValidateValues<AttributeKind>(npc.Attributes, path + ".attributes", Error);
            ValidateValues<SkillKind>(npc.Skills, path + ".skills", Error);
            if (npc.Traits == null || npc.Traits.Any(t => !Enum.TryParse<TraitKind>(t, out var k) || !Enum.IsDefined(typeof(TraitKind), k))) Error(path + ".traits", "unknown");
            if (npc.Clothing == null || npc.Clothing.Count > 64) { Error(path + ".clothing", "capacity"); continue; }
            var definitions = new Dictionary<string, ObjectDefinition>();
            GarmentLibrary.AppendDefinitions(definitions);
            var worn = new List<ObjectDefinition>();
            foreach (var id in npc.Clothing)
            {
                var garment = GarmentLibrary.Spawnable.FirstOrDefault(g => g.Id == id);
                var sex = npc.Body is "Kshishtof" or "Tonny" ? GarmentSex.Male : GarmentSex.Female;
                if (garment == null || !GarmentLibrary.FitsSex(sex, id)) { Error(path + ".clothing", "incompatible"); continue; }
                var definition = definitions[id];
                if (worn.Any(existing => WearSlotCatalog.Occupies(definition, existing))) Error(path + ".clothing", "slotConflict");
                worn.Add(definition);
            }
        }
        var active = config.Characters.Where(c => c != null && c.Enabled && config.Camp(c.Camp)?.Enabled == true).ToList();
        if (!active.Any(c => c.Controlled && c.Camp == config.PlayerCamp)) Error("characters", "noControlledCharacter");
        if (active.Count > config.PopulationLimit) Error("characters", "capacity");
        foreach (var camp in config.Camps.Where(c => c != null))
            if (active.Count(c => c.Camp == camp.Faction) > camp.PopulationLimit) Error("camps." + camp.Faction, "capacity");
        return errors;
    }
    private static void ValidateValues<T>(List<CreationValue> values, string path, Action<string, string> error) where T : struct
    {
        var ids = new HashSet<string>();
        if (values == null || values.Count > 32) { error(path, "capacity"); return; }
        foreach (var v in values)
            if (v == null || !Enum.TryParse<T>(v.Id, out var key) || !Enum.IsDefined(typeof(T), key) || !ids.Add(v.Id) ||
                float.IsNaN(v.Value) || float.IsInfinity(v.Value) || v.Value < 0 || v.Value > 1) error(path, "range");
    }

    public static WorldBootstrapDefinition Definition(WorldCreationConfig config)
    {
        var definition = PrototypeWorldDefinitionFactory.Create(config.Seed, config.Mode);
        definition.CreationConfig = config;
        definition.FactionHomes.RemoveAll(h => config.Camp(h.Faction)?.Enabled != true);
        if (config.Camp(Faction.Colony)?.Enabled != true)
        {
            definition.Simulation.SpawnCompletedTestHut = false;
            definition.Simulation.StakePlayerHutPlan = false;
        }
        var originalNpcs = definition.Npcs.ToDictionary(n => n.Id);
        definition.Npcs.Clear();
        foreach (var entry in config.Characters.Where(c => c.Enabled && config.Camp(c.Camp)?.Enabled == true).OrderBy(c => c.Id))
        {
            var home = definition.FactionHomes.First(h => h.Faction == entry.Camp);
            var tile = definition.Fragments.SelectMany(f => f.Tiles.Select(t => (f, t))).First(p => p.t.Q == home.TileQ && p.t.R == home.TileR);
            var npc = new NpcBootstrap
            {
                Id = entry.Id, ProfileId = entry.ProfileId, UseAuthoredAppearance = true,
                DisplayName = entry.Name, ActorMesh = entry.Body, SkinSet = entry.Skin,
                EyeColor = entry.Eyes, Hairstyle = entry.Hair, VoiceBank = entry.Voice,
                Faction = entry.Camp, FragmentId = tile.f.Id, TileQ = home.TileQ, TileR = home.TileR,
                Traits = entry.Traits.ToList(),
                Energy = originalNpcs.TryGetValue(entry.Id, out var original) ? original.Energy : .8f,
                Hunger = original?.Hunger ?? .2f, Thirst = original?.Thirst ?? .2f,
                Comfort = original?.Comfort ?? .5f, Social = original?.Social ?? .5f,
            };
            foreach (var v in entry.Attributes) npc.Attributes[Enum.Parse<AttributeKind>(v.Id)] = v.Value;
            definition.Npcs.Add(npc);
        }
        return definition;
    }

    public static void Apply(WorldState world, WorldCreationConfig config)
    {
        if (config == null) return;
        world.CreationConfig = config;
        foreach (var npc in world.Entities.Npcs.Values.OrderBy(n => n.Id.Value))
        {
            var entry = config.Characters.First(c => c.Id == npc.Id.Value);
            var home = world.FactionHomes[npc.Faction];
            if (!PopulationArrivalMath.TryPickLanding(world, home, npc.Id.Value, 16101, out var landing))
                throw new WorldCreationPlacementException(npc.Id.Value);
            HexLive.Simulation.Spatial.SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, landing.Tile);
            npc.Tile = landing.Tile; npc.Position = landing.Position;
            npc.Fragment = landing.Fragment; npc.CurrentJunction = landing.Junction;
            world.Occupancy.JunctionOwner[landing.Junction] = npc.Id;
            if (entry.ProfileId == MashaCompanionProfile.ProfileId)
            {
                MashaCompanionProfile.InitializeStartingNeedsAndSupplies(npc);
                npc.CharacterPresetVersion = MashaCompanionProfile.AuthoredStarterOutfitVersion;
                world.MashaCompanionHasSpawned = true;
                world.SpawnedCharacterPresets.Add(entry.ProfileId);
            }
            if (entry.ProfileId == NikaCharacterProfile.ProfileId)
            {
                npc.Needs.Hunger = .35f; npc.Needs.Thirst = .30f; npc.Needs.Energy = .82f;
                npc.Inventory.Items.Clear();
                npc.CharacterPresetVersion = 1;
                world.SpawnedCharacterPresets.Add(entry.ProfileId);
            }
            npc.HairColour = entry.HairColour;
            foreach (var v in entry.Attributes) npc.Attributes.Set(Enum.Parse<AttributeKind>(v.Id), v.Value);
            foreach (var v in entry.Skills) npc.Skills.Set(Enum.Parse<SkillKind>(v.Id), v.Value);
            npc.WornItems.Clear();
            foreach (var id in entry.Clothing) npc.WornItems.Add(new ItemInstance(id) { OwnerId = npc.Id.Value });
            EquipmentMath.Recalculate(world, npc);
            if (config.Owns(npc)) world.PlayerControlledNpcs.Add(npc.Id.Value);
        }
    }
}
}
