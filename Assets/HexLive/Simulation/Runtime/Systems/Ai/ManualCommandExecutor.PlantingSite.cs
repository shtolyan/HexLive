using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
internal static partial class ManualCommandExecutor
{
    private static void ApplyPlacePlantingSite(WorldState world, PlacePlantingSiteCommand command, AdmissionTracker admission)
    {
        if (!BuildCatalogDefinition.TryGet(command.CatalogId, out _) ||
            !PlantGrowthCatalog.TryGetBySeed(command.CatalogId, out _))
        { admission.Reject("UnknownProduct"); return; }
        if (!PlantGrowthSystem.CanPlantAt(world, command.Anchor, command.Tile))
        { admission.Reject("PlantingSpotOccupied"); return; }
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite,
            world.Junctions.Items[command.Anchor].Fragment, command.Tile, command.Anchor);
        site.BuildProduct = command.CatalogId;
        site.OwnerFaction = ConstructionAccess.Builder(world, admission.BuildFactions, command.Tile);
    }
}
}
