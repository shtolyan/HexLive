using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
internal static class AdminWorldObjects
{
    internal static bool Handles(string kind) => kind is "spawn_npc" or "delete_npc" or "create_building"
        or "complete_building" or "stock_building" or "repair_building" or "demolish_building";
    internal static readonly string[] BuildingIds = { ContentIds.Hut1Hex, ContentIds.BedBasic,
        ContentIds.Workbench, ContentIds.Campfire, ContentIds.DryingRack, ContentIds.Wardrobe, ContentIds.WaterCollector };
    internal static AdminCommandResult Execute(WorldState world, AdminCommand c)
    {
        if ((c.Kind == "spawn_npc" || c.Kind == "create_building") && (!c.TileQ.HasValue || !c.TileR.HasValue))
            return AdminCommandResult.Reject(c, "MissingCoordinates");
        if (c.Kind == "spawn_npc")
        {
            if (c.Text.Length > 48 || c.Text.Any(char.IsControl)) return AdminCommandResult.Reject(c, "InvalidName");
            if (c.DefinitionId != "colonist") return AdminCommandResult.Reject(c, "UnknownPreset");
            if (!Enum.TryParse<Faction>(c.Target, true, out var faction) || !FactionRelations.IsGirlCamp(faction))
                return AdminCommandResult.Reject(c, "InvalidFaction");
            var tile = new TileCoord(c.TileQ.Value, c.TileR.Value);
            if (!world.Tiles.Items.ContainsKey(tile)) return AdminCommandResult.Reject(c, "UnknownTile");
            if (c.GroundX.HasValue != c.GroundZ.HasValue || (c.GroundX.HasValue &&
                (!AdminWorldCommands.Finite(c.GroundX.Value) || !AdminWorldCommands.Finite(c.GroundZ.Value))))
                return AdminCommandResult.Reject(c, "InvalidContext");
            Float2? nearPoint = c.GroundX.HasValue ? new Float2(c.GroundX.Value, c.GroundZ.Value) : (Float2?)null;
            var next = Math.Max(1000000, world.NextRuntimeObjectId);
            while (world.Entities.Npcs.ContainsKey(new EntityId(next)) || world.Entities.Corpses.ContainsKey(new EntityId(next))) next++;
            if (!PopulationArrivalMath.TryPickLanding(world, tile, next, 161, out var landing, nearestToHome: c.Location == "camera", nearPoint: nearPoint)) return AdminCommandResult.Reject(c, "NoSpawnLocation");
            var npc = ColonyArrivalSystem.CreateArrival(world, new EntityId(next), faction, landing);
            world.NextRuntimeObjectId = next + 1;
            if (!string.IsNullOrWhiteSpace(c.Text) && c.Text.Length <= 48 && !c.Text.Any(char.IsControl)) npc.DisplayName = c.Text;
            AdminWorldCommands.Execute(world, new AdminCommand { NpcId = next, Kind = "set_need", Target = "all" });
            return AdminCommandResult.Ok(c, next);
        }
        if (c.Kind == "delete_npc")
        {
            if (!world.Entities.Npcs.TryGetValue(new EntityId(c.NpcId), out var npc)) return AdminCommandResult.Reject(c, "NpcNotFound");
            if (npc.CarriedNpcId != null) KenshiRescueMath.DropSafely(world, npc, "Admin removal");
            ExecutionSystem.ReleaseClaims(world, npc);
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Plan.TargetAgentId == npc.Id)
                    PlanInterruption.TryAbort(world, other, InterruptionCause.ExecutionFailure, "Admin removed target");
                if (other.CarriedNpcId == npc.Id) other.CarriedNpcId = null;
                if (other.CarriedByNpcId == npc.Id) other.CarriedByNpcId = null;
                if (other.Mind.PendingTalkFrom == npc.Id) other.Mind.PendingTalkFrom = null;
                if (other.Mind.PendingRomanceFrom == npc.Id) other.Mind.PendingRomanceFrom = null;
                if (other.Mind.RomancePartnerNpcId == npc.Id) other.Mind.RomancePartnerNpcId = null;
                if (other.Mind.RomanceLeaderNpcId == npc.Id) other.Mind.RomanceLeaderNpcId = null;
                if (other.Mind.PendingAidFrom == npc.Id) other.Mind.PendingAidFrom = null;
                if (other.Mind.OrderedAidFor == npc.Id) other.Mind.OrderedAidFor = null;
                if (other.Mind.InterruptedRescuePatientId == npc.Id) other.Mind.InterruptedRescuePatientId = null;
                if (other.Mind.AidErrandFor == npc.Id) other.Mind.AidErrandFor = null;
                if (other.Mind.CombatAssistAttackerNpcId == npc.Id) other.Mind.CombatAssistAttackerNpcId = null;
                if (other.Mind.RaidTargetNpcId == npc.Id) other.Mind.RaidTargetNpcId = null;
                if (other.Mind.AbuseTargetNpcId == npc.Id) other.Mind.AbuseTargetNpcId = null;
                if (other.Mind.PendingAbuseFrom == npc.Id) other.Mind.PendingAbuseFrom = null;
                if (other.Mind.ExpulsionTargetNpcId == npc.Id) other.Mind.ExpulsionTargetNpcId = null;
                if (other.Mind.PendingExpulsionFrom == npc.Id) other.Mind.PendingExpulsionFrom = null;
                if (other.Mind.GroupHuntTargetNpcId == npc.Id) other.Mind.GroupHuntTargetNpcId = null;
                if (other.Mind.LootHelplessTargetNpcId == npc.Id) other.Mind.LootHelplessTargetNpcId = null;
                if (other.Mind.PendingLootedBy == npc.Id) other.Mind.PendingLootedBy = null;
                if (other.Mind.CombatOpponentNpcId == npc.Id) other.Mind.CombatOpponentNpcId = null;
                if (other.Mind.ManualAttackNpcId == npc.Id) other.Mind.ManualAttackNpcId = null;
                if (other.Mind.ProstheticAidTargetId == npc.Id) { other.Mind.ProstheticAidTargetId = null; other.Mind.ProstheticAidPart = null; }
            }
            foreach (var obj in world.Entities.Objects.Values)
            {
                if (obj.CurrentUser == npc.Id) { obj.CurrentUser = null; obj.IsOccupied = false; }
                if (obj.Owner == npc.Id) obj.Owner = null;
            }
            foreach (var entry in world.Reservations.Junctions.Where(p => p.Value.Owner == npc.Id).ToArray()) world.Reservations.Junctions.Remove(entry.Key);
            foreach (var entry in world.Occupancy.JunctionOwner.Where(p => p.Value == npc.Id).ToArray()) world.Occupancy.JunctionOwner[entry.Key] = null;
            foreach (var ids in world.Occupancy.EntitiesInTile.Values) ids.Remove(npc.Id);
            foreach (var ids in world.Caches.EntitiesByTile.Values) ids.Remove(npc.Id);
            foreach (var ids in world.Caches.EntitiesByFragment.Values) ids.Remove(npc.Id);
            world.PlayerControlledNpcs.Remove(npc.Id.Value);
            world.Entities.Npcs.Remove(npc.Id);
            return AdminCommandResult.Ok(c, c.NpcId);
        }
        WorldObjectState site;
        if (c.Kind == "create_building")
        {
            if (!BuildingIds.Contains(c.DefinitionId) || !world.Content.ObjectDefinitions.ContainsKey(c.DefinitionId)) return AdminCommandResult.Reject(c, "UnknownBuilding");
            var tile = new TileCoord(c.TileQ.Value, c.TileR.Value);
            if (!StructurePlacement.HexFreeForBuild(world, tile) || StructurePlacement.CenterJunction(world, tile) is not { } anchor)
                return AdminCommandResult.Reject(c, "InvalidBuildingLocation");
            if (c.DefinitionId == ContentIds.Hut1Hex)
            {
                site = BuildingBootstrap.CreateHutSite(world, tile, 0);
                if (site == null) return AdminCommandResult.Reject(c, "InvalidBuildingLocation");
            }
            else
            {
                site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite, world.Junctions.Items[anchor].Fragment, tile, anchor);
                site.BuildProduct = c.DefinitionId;
                BuildingBootstrap.ApplyFurnitureBill(site, c.DefinitionId, false);
            }
            var raised = ExecutionSystem.RaiseFurnitureSite(world, site, site.Fragment, anchor);
            return raised == null ? AdminCommandResult.Reject(c, "BuildFailed") : AdminCommandResult.Ok(c, raised.Id.Value);
        }
        if (!world.Entities.Objects.TryGetValue(new ObjectId(c.ObjectId), out site)) return AdminCommandResult.Reject(c, "ObjectNotFound");
        if (!BuildSiteMath.IsSite(site) && !BuildingIds.Contains(site.DefinitionId) && site.DefinitionId != ContentIds.HutPlan)
            return AdminCommandResult.Reject(c, "NotBuilding");
        if (c.Kind == "repair_building")
        {
            site.Durability = 1;
            foreach (var child in world.Entities.Objects.Values.Where(o => o.ArchitectureOwnerId == site.Id)) child.Durability = 1;
            return AdminCommandResult.Ok(c, site.Id.Value);
        }
        if (c.Kind == "stock_building")
        {
            if (!BuildSiteMath.IsSite(site)) return AdminCommandResult.Reject(c, "NotBuildSite");
            foreach (var material in BuildSiteMath.AllMaterials)
                for (var i = BuildSiteMath.TotalRemaining(site, material); i > 0; i--) site.Contents.Add(new ItemInstance(material));
            return AdminCommandResult.Ok(c, site.Id.Value);
        }
        if (c.Kind == "complete_building")
        {
            if (string.IsNullOrEmpty(site.BuildProduct)) return AdminCommandResult.Reject(c, "NotBuildSite");
            if (site.Junctions.Count == 0 || !world.Junctions.Items.ContainsKey(site.Junctions.First()))
                return AdminCommandResult.Reject(c, "InvalidBuildingLocation");
            var raised = ExecutionSystem.RaiseFurnitureSite(world, site, site.Fragment, site.Junctions.First());
            return raised == null ? AdminCommandResult.Reject(c, "BuildFailed") : AdminCommandResult.Ok(c, raised.Id.Value);
        }
        var footprint = BuildingBootstrap.FootprintTiles(world, site).ToArray();
        var parts = world.Entities.Objects.Values.Where(o => o.ArchitectureOwnerId == site.Id).Select(o => o.Id).ToArray();
        foreach (var other in world.Entities.Npcs.Values)
            if (other.Plan.TargetObjectId == site.Id || (other.Plan.TargetObjectId is { } target && parts.Contains(target)))
                PlanInterruption.TryAbort(world, other, InterruptionCause.ExecutionFailure, "Admin removed building");
        foreach (var id in parts) WorldObjectMutations.DespawnObject(world, id);
        WorldObjectMutations.DespawnObject(world, site.Id);
        if (site.DefinitionId == ContentIds.Hut1Hex || site.DefinitionId == ContentIds.HutPlan)
            foreach (var coord in footprint)
                if (world.Tiles.Items.TryGetValue(coord, out var tile)) { tile.Flags &= ~(HexLive.Simulation.Spatial.TileFlags.HasFloor | HexLive.Simulation.Spatial.TileFlags.Indoor | HexLive.Simulation.Spatial.TileFlags.Roofed); WorldTopology.NoteTile(world, coord); }
        world.DoorStateVersion++;
        return AdminCommandResult.Ok(c, site.Id.Value);
    }
}
}
