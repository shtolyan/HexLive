using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Bootstrap
{
// §171.10: private save-only receipt. Never include account/request ids in a viewer snapshot.
public sealed class PlayerCharacterCreationReceipt
{
    public string PlayerId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public int NpcId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Faction Camp { get; set; }
}

public static class PlayerCharacterCreation
{
    // Proposed point-buy balance, pending the player's approval (§171.10).
    public const float DefaultSkillBudget = 3f;
    // Reserved above all scheduled arrival bands and the 900–902 story characters.
    public const int FirstNpcId = 1_000_000_000;
    public const int ReceiptCapacity = 100_000;

    // Server-assigned, never the player's choice: a world still on an older catalogue
    // (registry revision pending) may lack a starter. Skip it rather than refuse the
    // whole character with an error about clothes the player never picked.
    public static List<string> StartingClothing(string body)
    {
        var male = body is "Kshishtof" or "Tonny";
        var wanted = male
            ? new[] { "underwear.briefs_primal_male" }
            : new[] { "underwear.briefs_primal", "clothing.top_primal" };
        var sex = male ? GarmentSex.Male : GarmentSex.Female;
        return wanted.Where(id => GarmentLibrary.IsSpawnable(id) && GarmentLibrary.FitsSex(sex, id)).ToList();
    }

    public static bool IsAvailableCamp(WorldState world, Faction camp) =>
        FactionRelations.IsGirlCamp(camp) && world.FactionHomes.ContainsKey(camp) &&
        (world.CreationConfig == null || world.CreationConfig.Camp(camp)?.Enabled == true);

    public static bool HasSpace(WorldState world, Faction camp) => !world.Completed && IsAvailableCamp(world, camp) &&
        PopulationArrivalMath.HasRoom(world, camp) &&
        PopulationArrivalMath.TryPickLanding(world, world.FactionHomes[camp], FirstNpcId, 17110, out _);

    // Called only by the host command boundary, with the world locked between complete ticks.
    public static string TryCreate(WorldState world, string playerId, string requestId,
        CharacterCreationConfig character, out PlayerCharacterCreationReceipt receipt)
    {
        receipt = world.PlayerCharacterCreations.FirstOrDefault(r => r.PlayerId == playerId && r.RequestId == requestId);
        if (receipt != null) return string.Empty;
        if (world.PlayerCharacterCreations.Any(r => r.PlayerId == playerId && world.Entities.Npcs.ContainsKey(new EntityId(r.NpcId))))
            return "character_alive";
        if (!IsAvailableCamp(world, character.Camp)) return "camp_unavailable";
        if (!PopulationArrivalMath.HasRoom(world, character.Camp)) return "camp_full";
        if (world.PlayerCharacterCreations.Count >= ReceiptCapacity) return "creation_history_full";
        var id = world.PlayerCharacterCreations.Count == 0 ? FirstNpcId : world.PlayerCharacterCreations.Max(r => r.NpcId) + 1;
        if (id < FirstNpcId) return "creation_history_full";
        while (world.Entities.Npcs.ContainsKey(new EntityId(id)) || world.Entities.Corpses.ContainsKey(new EntityId(id)) ||
               world.Entities.Objects.ContainsKey(new ObjectId(id)))
        {
            if (id == int.MaxValue) return "creation_history_full";
            id++;
        }
        if (!PopulationArrivalMath.TryPickLanding(world, world.FactionHomes[character.Camp], id, 17110, out var landing))
            return "no_free_spawn";
        var npc = ColonyArrivalSystem.CreateArrival(world, new EntityId(id), character.Camp, landing, character);
        world.PlayerControlledNpcs.Add(id);
        receipt = new PlayerCharacterCreationReceipt { PlayerId = playerId, RequestId = requestId,
            NpcId = id, Name = npc.DisplayName, Camp = npc.Faction };
        world.PlayerCharacterCreations.Add(receipt);
        return string.Empty;
    }
}
}
