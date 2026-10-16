using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Runtime.Blueprints;

// Export production module keys/poses, never reconstruct a house from a mockup.
var plan = args.Contains("--one-hex") ? BuiltInBuildingBlueprints.Hut1Hex() : CommittedBuildingPlans.PlayerHut;
var modules = BlueprintBuildingPlan.Modules(plan);
var anchor = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(BlueprintBuildingPlan.AnchorTile(plan)));
var result = new
{
    blueprintId = plan.BlueprintId,
    source = "BlueprintBuildingPlan.Modules",
    modules = modules.Select(m => new
    {
        key = m.Key, kind = m.Kind.ToString(), x = m.LocalX, z = m.LocalZ, yaw = m.LocalYaw,
        sticks = m.Sticks, boards = m.Boards, rope = m.Rope, leaves = m.Leaves
    }),
    furniture = plan.Furniture.Select(f =>
    {
        var node = BlueprintGeometry.JunctionToWorld(f.PrimaryJunction);
        var centroid = BlueprintFurnitureFootprints.CentroidOffset(f.DefinitionId, f.YawStep);
        return new { definitionId = f.DefinitionId, x = node.X-anchor.X+centroid.X,
            z = node.Y-anchor.Y+centroid.Y, yaw = f.YawStep*60 };
    })
};
var destination = Path.GetFullPath("Build/ConstructionReview-20261016/production-plan.json");
Directory.CreateDirectory(Path.GetDirectoryName(destination));
File.WriteAllText(destination, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Production plan {plan.BlueprintId}: {modules.Count} modules → {destination}");
