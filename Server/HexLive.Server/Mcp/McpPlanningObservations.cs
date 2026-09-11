using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Mcp;

internal static class McpPlanningObservations
{
    public static string InventoryDrop(WorldState world, int npcId, int index, string expectedDefinitionId, int approachRadiusTiles = GroundItemPlacementPreview.ApproachRadiusTiles)
    {
        if (!world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc))
            return JsonSerializer.Serialize(new { error = "NpcMissing", npcId });
        if (index < 0 || index >= npc.Inventory.Items.Count || npc.Inventory.Items[index].DefinitionId != expectedDefinitionId)
            return JsonSerializer.Serialize(new { error = "StaleInventoryItem", npcId });
        var found = GroundItemPlacementPreview.TryFindApproach(world, npc, npc.Inventory.Items[index],
            out var canDropHere, out var approach, out var junction, out var checkedOrigins, approachRadiusTiles);
        return JsonSerializer.Serialize(new { npcId, tick = world.Tick, sourceIndex = index, expectedDefinitionId,
            count = 1, canDropHere, found, reason = found ? "" : "NoNearbyDropSpot", checkedOrigins,
            approachRadiusTiles,
            approach = found ? new { x = approach.X, y = approach.Y } : null,
            dropJunction = found ? (int?)junction.Value : null, routeChecked = false, reserved = false });
    }

    public static object RestReadiness(WorldState world, NPCState npc)
    {
        var sleepReason = ExecutionSystem.GetSleepInterruptReason(world, npc, manualOrder: true) ?? "";
        return new
        {
            source = "nativeRestRules", sleepBodyReady = sleepReason.Length == 0,
            sleepBodyBlockReason = sleepReason, sleepSpaceChecked = false,
            adrenalineTicksRemaining = Math.Max(0L, npc.Mind.AdrenalineUntilTick - world.Tick),
            idleRestCooldownTicksRemaining = Math.Max(0L, npc.Mind.RestCooldownUntilTick - world.Tick),
            perceivedThreatCount = npc.Perception.Hostiles.Count + npc.Perception.Mobs.Count
        };
    }

    public static object RecentGiftResults(WorldState world, int npcId) => new
    {
        source = "SimulationEventBuffer", partialHistory = true,
        events = world.Events.Items.Where(e => e.EntityId == npcId && e.Type == "GiftGiven")
            .TakeLast(8).Select(e => new { sequence = e.Seq, tick = e.Tick, type = e.Type,
                details = e.Message.Length > 512 ? e.Message[..512] : e.Message }).ToArray()
    };

    public static object[] BuildMaterials(WorldObjectState site) => BuildSiteView.Materials(site).Where(r => r.Required > 0 || r.Delivered > 0)
        .Select(r => (object)new { definitionId = r.DefinitionId, required = r.Required, delivered = r.Delivered,
            remaining = r.Remaining, currentStageRemaining = r.CurrentStageRemaining }).ToArray();

    public static string BuildCatalog(string definitionId)
    {
        var entries = BuildCatalogDefinition.All.Where(e => e.PlacementKind == BuildCatalogPlacementKind.Furniture &&
            (definitionId.Length == 0 || e.DefinitionId == definitionId)).Select(e => new
            {
                definitionId = e.DefinitionId, requiresCompletedFloor = e.RequiresCompletedFloor,
                materials = BuildMaterials(BuildSiteView.FurnitureBillPreview(e.DefinitionId))
            }).ToArray();
        return JsonSerializer.Serialize(new { source = "BuildCatalog", specSections = new[] { "54", "120" }, entries });
    }

    public static object Needs(NPCState npc) => new
    {
        energy = new { value = npc.Needs.Energy, higherIsBetter = true, meaning = "wakefulness reserve; restored by sleep", specSection = "60" },
        stamina = new { value = npc.Needs.Stamina, higherIsBetter = true, meaning = "work reserve; recovery depends on rest and body condition", specSection = "40" },
        breath = new { value = npc.Needs.Breath, higherIsBetter = true, meaning = "sprint reserve; walking also restores it", specSection = "71" },
        hunger = npc.Needs.Hunger, thirst = npc.Needs.Thirst, blood = npc.Needs.Blood,
        hygiene = npc.Needs.Hygiene, health = npc.Health
    };

    public static object Execution(NPCState npc) => new
    {
        planStatus = npc.Plan.Status.ToString(), status = npc.Execution.Status.ToString(),
        interaction = npc.Execution.CurrentInteraction?.ToString(), targetObjectId = npc.Execution.TargetObject?.Value,
        failureReason = npc.Execution.FailureReason.ToString()
    };

    public static string Recipes(string definitionId)
    {
        var recipes = RecipeCatalog.ByGoal.Values.OrderBy(r => r.Goal.ToString(), StringComparer.Ordinal).ToArray();
        var catalog = recipes.Select(r => new
        {
            goal = r.Goal.ToString(), outputDefinitionId = r.OutputDefinitionId,
            inputs = r.Inputs.Select(i => new { definitionId = i.Id, count = i.Count }).ToArray(),
            station = r.Station, needsLitFire = r.NeedsLitFire, requiresNoRack = r.RequiresNoRack,
            baseWorkTicks = r.BaseWorkTicks
        }).ToArray();
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(catalog)))).ToLowerInvariant();
        if (definitionId.Length == 0)
            return JsonSerializer.Serialize(new { source = "RecipeCatalog", version, specSections = new[] { "54", "119" },
                recipes = catalog.Select(r => new { r.goal, r.outputDefinitionId }).ToArray() });
        return JsonSerializer.Serialize(new { source = "RecipeCatalog", version, specSections = new[] { "54", "119" },
            recipes = catalog.Where(r => r.outputDefinitionId == definitionId).ToArray() });
    }
}
