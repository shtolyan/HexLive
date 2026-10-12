using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Server;

/// <summary>§171.16: operator migration, under the host lock or with its service stopped.</summary>
public static class PlayerOnlyWorldPolicy
{
    public static int[] Apply(WorldState world, IEnumerable<int>? protectedNpcIds = null)
    {
        var players = world.PlayerCharacterCreations.Select(r => r.NpcId).ToHashSet();
        if (world.PlayerCharacterCreations.Any(r => !Guid.TryParseExact(r.PlayerId, "N", out _) || r.NpcId < PlayerCharacterCreation.FirstNpcId))
            throw new InvalidOperationException("Invalid player creation history; refuse migration.");
        if (protectedNpcIds?.Any(id => !players.Contains(id)) == true)
            throw new InvalidOperationException("Assigned character lacks creation receipt; refuse removal.");
        var config = world.CreationConfig ?? WorldCreation.Defaults(world);
        config.PlayersOnly = true;
        config.ShipwreckSurvivorEnabled = false;
        config.SeaRaidIntervalDays = 0;
        config.Characters.Clear();
        foreach (var camp in config.Camps) { camp.ArrivalIntervalDays = 0; camp.ControlArrivals = false; }
        world.CreationConfig = config;
        var removed = world.Entities.Npcs.Keys.Where(id => !players.Contains(id.Value)).OrderBy(id => id.Value).ToArray();
        foreach (var id in removed)
        {
            var result = AdminWorldCommands.Execute(world, new AdminCommand { Kind = "delete_npc", NpcId = id.Value });
            if (!result.Accepted) throw new InvalidOperationException("Could not remove non-player NPC " + id.Value);
        }
        foreach (var npc in world.Entities.Npcs.Values)
            foreach (var other in npc.Social.Relationships.Keys.Where(id => !players.Contains(id.Value)).ToArray())
                npc.Social.Relationships.Remove(other);
        return removed.Select(id => id.Value).ToArray();
    }
}
