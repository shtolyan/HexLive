using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
/// <summary>§149: the player's authored WardrobeTest body, independent of the Masha agent.</summary>
public static class NikaCharacterProfile
{
    public const string ProfileId = "nika";
    public const int ReservedNpcId = 902;
    public static IReadOnlyList<string> Outfit { get; } = Array.AsReadOnly(new[]
    {
        "clothing.boots_riot", "clothing.croptop_idol", "clothing.cuffs_anarchy",
        "clothing.helmet_racing", "clothing.jeans_skinny", "clothing.necklace_beads",
        "clothing.vest_stars", "underwear.briefs_luxury", "underwear.top_luxury"
    });

    public static bool EnsureSpawned(WorldState world)
    {
        var id = new EntityId(ReservedNpcId);
        if (world.Entities.Npcs.TryGetValue(id, out var existing) ||
            world.Entities.Corpses.TryGetValue(id, out existing))
        {
            if (existing.ProfileId != ProfileId) throw new InvalidOperationException("Nika id collision.");
            world.SpawnedCharacterPresets.Add(ProfileId);
            return false;
        }
        if (world.SpawnedCharacterPresets.Contains(ProfileId)) return false;
        foreach (var npc in world.Entities.Npcs.Values)
            if (npc.ProfileId == ProfileId) throw new InvalidOperationException("Duplicate Nika preset.");
        if (!world.FactionHomes.TryGetValue(Faction.Colony, out var home) ||
            !PopulationArrivalMath.TryPickLanding(world, home, ReservedNpcId, 14902, out var landing))
            throw new InvalidOperationException("No safe Nika landing.");
        var created = new NPCState
        {
            Id = id, ProfileId = ProfileId, DisplayName = "Ника", UseAuthoredAppearance = true,
            ActorMesh = "Marta", SkinSet = "", EyeColor = "", Hairstyle = "JenniferHair",
            VoiceBank = "marta", CharacterPresetVersion = 1, Faction = Faction.Colony,
            Fragment = landing.Fragment, Tile = landing.Tile, Position = landing.Position,
            CurrentJunction = landing.Junction
        };
        created.Needs.Hunger = 0.35f;
        created.Needs.Thirst = 0.30f;
        created.Needs.Energy = 0.82f;
        AttributeMath.Roll(created, world.Seed, ReservedNpcId);
        TraitMath.Roll(created, world.Seed, ReservedNpcId);
        foreach (var garment in Outfit)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(garment, out var definition) ||
                definition.Layer == null || !definition.HasTag("Clothing"))
                throw new InvalidOperationException("Missing Nika garment: " + garment);
            created.WornItems.Add(new ItemInstance(garment) { OwnerId = ReservedNpcId });
        }
        if (EquipmentMath.StripConflictingWorn(world, created) != 0)
            throw new InvalidOperationException("Nika outfit has conflicting slots.");
        EquipmentMath.Recalculate(world, created);
        PopulationArrivalMath.AddToWorld(world, created, landing.Junction);
        world.SpawnedCharacterPresets.Add(ProfileId);
        return true;
    }
}
}
