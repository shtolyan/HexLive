using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§159: opt-in, idempotent materialisation of the Masha companion.</summary>
public static class MashaCompanionProfile
{
    public const string ProfileId = "masha";
    public const int ReservedNpcId = 901;
    public const int AuthoredStarterOutfitVersion = 1;

    // §159.1: exact authored look approved in WardrobeTest. The ids are the
    // base prototype variants (#0), not colourway aliases. This is applied
    // once, when Masha is materialised; ordinary wardrobe gameplay remains
    // free to change the physical instances afterwards.
    public static IReadOnlyList<string> AuthoredStarterOutfit { get; } =
        Array.AsReadOnly(new[]
        {
            "clothing.blouse_riot",
            "clothing.bracelet_luxury",
            "clothing.headband_primal",
            "clothing.pendant_amy",
            "clothing.shorts_osiris",
            "clothing.sneakers_sport",
            "underwear.briefs_cindy",
            "underwear.tights_deadly",
            "underwear.top_cindy",
            "gear.backpack_riot",
        });

    public static bool EnsureSpawned(WorldState world)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));

        if (world.MashaCompanionHasSpawned)
            world.SpawnedCharacterPresets.Add(ProfileId);

        var reserved = new EntityId(ReservedNpcId);
        if (world.Entities.Npcs.TryGetValue(reserved, out var live))
        {
            RequireMasha(live);
            UpgradeAuthoredStarterOutfit(world, live);
            world.MashaCompanionHasSpawned = true;
            world.SpawnedCharacterPresets.Add(ProfileId);
            return false;
        }
        if (world.Entities.Corpses.TryGetValue(reserved, out var corpse))
        {
            RequireMasha(corpse);
            world.MashaCompanionHasSpawned = true;
            world.SpawnedCharacterPresets.Add(ProfileId);
            return false;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            RejectWrongId(npc);
        }

        if (world.SpawnedCharacterPresets.Contains(ProfileId))
        {
            return false;
        }
        foreach (var npc in world.Entities.Corpses.Values)
        {
            RejectWrongId(npc);
        }

        if (!TryHome(world, out var home) ||
            (!PopulationArrivalMath.TryPickShoreLanding(
                 world, home, 2, 12, ReservedNpcId, 15901, out var landing) &&
             !PopulationArrivalMath.TryPickLanding(
                 world, home, ReservedNpcId, 15902, out landing)))
        {
            throw new InvalidOperationException(
                "Cannot place companion 'masha': no safe colony landing exists.");
        }

        var spawnedNpc = new NPCState
        {
            Id = reserved,
            ProfileId = ProfileId,
            UseAuthoredAppearance = true,
            DisplayName = ProfileId,
            ActorMesh = "Jana",
            SkinSet = "Marta",
            // Empty means the authored Jana prefab defaults, not a §74 roll.
            EyeColor = string.Empty,
            Hairstyle = string.Empty,
            VoiceBank = ProfileId,
            Faction = Faction.Colony,
            Fragment = landing.Fragment,
            Tile = landing.Tile,
            Position = landing.Position,
            CurrentJunction = landing.Junction,
        };

        spawnedNpc.Needs.Hunger = 0.35f;
        spawnedNpc.Needs.Thirst = 0.30f;
        spawnedNpc.Needs.Energy = 0.82f;
        spawnedNpc.Needs.Comfort = 0.42f;
        spawnedNpc.Needs.Social = 0.35f;
        spawnedNpc.Needs.ThermalDiscomfort = 0.15f;
        spawnedNpc.CompassionTrait = Spec53.TraitMin +
            MathUtil.Hash01(world.Seed, ReservedNpcId, 53, 5301) *
            (Spec53.TraitMax - Spec53.TraitMin);
        AttributeMath.Roll(spawnedNpc, world.Seed, ReservedNpcId);
        TraitMath.Roll(spawnedNpc, world.Seed, ReservedNpcId);

        spawnedNpc.Inventory.Items.Add(new ItemInstance(Content.GearCatalog.Bottle));
        spawnedNpc.Inventory.Items.Add(MedicalSupplyMath.CreateBandage(herbal: false));
        EquipAuthoredStarterOutfit(world, spawnedNpc);
        spawnedNpc.CharacterPresetVersion = AuthoredStarterOutfitVersion;
        EquipmentMath.Recalculate(world, spawnedNpc);
        PopulationArrivalMath.AddToWorld(world, spawnedNpc, landing.Junction);
        world.MashaCompanionHasSpawned = true;
        world.SpawnedCharacterPresets.Add(ProfileId);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, spawnedNpc.Id, "CompanionSpawned",
                $"Profile={ProfileId} Tile={landing.Tile.Q},{landing.Tile.R}");
        }
        return true;
    }

    private static void UpgradeAuthoredStarterOutfit(WorldState world, NPCState npc)
    {
        if (npc.CharacterPresetVersion >= AuthoredStarterOutfitVersion)
        {
            return;
        }

        // Preserve the physical old clothes instead of deleting them. An
        // approved piece already worn keeps its wetness/dirt/damage; everything
        // else is folded into the pack and normal overflow lands at her feet.
        var previous = new List<ItemInstance>(npc.WornItems);
        npc.WornItems.Clear();
        foreach (var definitionId in AuthoredStarterOutfit)
        {
            var existingIndex = previous.FindIndex(item =>
                string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                npc.WornItems.Add(previous[existingIndex]);
                previous.RemoveAt(existingIndex);
            }
            else
            {
                npc.WornItems.Add(CreateOwnedGarment(npc, definitionId));
            }
        }

        foreach (var oldGarment in previous)
        {
            npc.Inventory.Items.Add(oldGarment);
        }

        ValidateAuthoredStarterOutfit(world, npc);
        npc.CharacterPresetVersion = AuthoredStarterOutfitVersion;
        EquipmentMath.Recalculate(world, npc);
        InventoryMath.SpillOverflow(world, npc);
    }

    private static void EquipAuthoredStarterOutfit(WorldState world, NPCState npc)
    {
        foreach (var definitionId in AuthoredStarterOutfit)
        {
            npc.WornItems.Add(CreateOwnedGarment(npc, definitionId));
        }

        ValidateAuthoredStarterOutfit(world, npc);
    }

    private static ItemInstance CreateOwnedGarment(NPCState npc, string definitionId) =>
        new(definitionId) { OwnerId = npc.Id.Value };

    private static void ValidateAuthoredStarterOutfit(WorldState world, NPCState npc)
    {
        foreach (var definitionId in AuthoredStarterOutfit)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition) ||
                definition.Layer is null || !definition.HasTag("Clothing"))
            {
                throw new InvalidOperationException(
                    $"Masha authored outfit item '{definitionId}' is absent or not wearable.");
            }
        }

        // A catalog edit must not silently make two approved pieces fight for
        // one (layer, slot): the renderer would keep rebuilding that outfit.
        if (EquipmentMath.StripConflictingWorn(world, npc) != 0)
        {
            throw new InvalidOperationException(
                "Masha authored starter outfit contains conflicting wear slots.");
        }
    }

    private static bool TryHome(WorldState world, out TileCoord home)
    {
        if (world.FactionHomes.TryGetValue(Faction.Colony, out home)) return true;
        foreach (var pair in world.FactionHomes)
        {
            if (FactionRelations.IsColonyKind(pair.Key))
            {
                home = pair.Value;
                return true;
            }
        }

        home = default;
        return false;
    }

    private static void RejectWrongId(NPCState npc)
    {
        if (string.Equals(npc.ProfileId, ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Companion profile '{ProfileId}' already uses NPC{npc.Id.Value}; " +
                $"reserved id is NPC{ReservedNpcId}.");
        }
    }

    private static void RequireMasha(NPCState npc)
    {
        if (!string.Equals(npc.ProfileId, ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reserved companion id NPC{ReservedNpcId} belongs to profile " +
                $"'{npc.ProfileId}', not '{ProfileId}'.");
        }
    }
}

}
