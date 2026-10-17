using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §173: planted objects advance from visible seedlings to their ordinary
/// mature content definition. The planting tick remains the one age anchor for
/// every replacement, so an overdue plant catches up after its chunk wakes
/// instead of restarting the current stage.
/// </summary>
public sealed class PlantGrowthSystem : ISimulationSystem
{
    private readonly List<WorldObjectState> _tickable = new();
    private readonly List<WorldObjectState> _transitions = new();

    public string Name => nameof(PlantGrowthSystem);

    public TickLayer Layer => TickLayer.Slow;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.PerChunk;

    public void Run(WorldState world)
    {
        ChunkMath.CollectTickable(world, _tickable);
        _transitions.Clear();
        foreach (var plant in _tickable)
        {
            if (plant.IsOccupied ||
                !PlantGrowthCatalog.TryGetByStage(
                    plant.DefinitionId, out var profile, out _))
            {
                continue;
            }

            var ageTicks = System.Math.Max(0, world.Tick - plant.SpawnTick);
            var desiredDefinitionId = PlantGrowthCatalog.DefinitionAtAge(profile, ageTicks);
            if (desiredDefinitionId != plant.DefinitionId)
            {
                _transitions.Add(plant);
            }
        }

        foreach (var plant in _transitions)
        {
            if (!world.Entities.Objects.ContainsKey(plant.Id) ||
                plant.Junctions.Count == 0 ||
                !world.Junctions.Items.ContainsKey(plant.Junctions[0]) ||
                !PlantGrowthCatalog.TryGetByStage(
                    plant.DefinitionId, out var profile, out _))
            {
                continue;
            }

            var ageTicks = System.Math.Max(0, world.Tick - plant.SpawnTick);
            var desiredDefinitionId = PlantGrowthCatalog.DefinitionAtAge(profile, ageTicks);
            if (desiredDefinitionId == plant.DefinitionId ||
                !world.Content.ObjectDefinitions.ContainsKey(desiredDefinitionId))
            {
                continue;
            }

            var plantedAtTick = plant.SpawnTick;
            var tile = plant.Tile;
            var fragment = plant.Fragment;
            var anchor = plant.Junctions[0];
            WorldObjectMutations.DespawnObject(world, plant.Id);
            var grown = WorldObjectMutations.SpawnObject(
                world, desiredDefinitionId, fragment, tile, anchor);
            grown.SpawnTick = plantedAtTick;

            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "PlantGrew",
                    $"Obj={grown.Id.Value} Def={desiredDefinitionId} " +
                    $"Age={ageTicks} Tile={tile.Q},{tile.R}");
            }
        }
    }

    /// <summary>
    /// Consumes a ground seed and creates the first visible growth stage in
    /// the exact same slot. This is shared by execution and headless tests so
    /// planting cannot silently use a second definition table.
    /// </summary>
    public static WorldObjectState? TryPlant(WorldState world, WorldObjectState seed)
    {
        if (!PlantGrowthCatalog.TryGetBySeed(seed.DefinitionId, out var profile) ||
            profile.StageDefinitionIds.Length == 0 ||
            seed.Junctions.Count == 0 ||
            !world.Junctions.Items.ContainsKey(seed.Junctions[0]) ||
            !world.Content.ObjectDefinitions.ContainsKey(profile.StageDefinitionIds[0]))
        {
            return null;
        }

        var tile = seed.Tile;
        var fragment = seed.Fragment;
        var anchor = seed.Junctions[0];
        if (!WorldObjectMutations.DespawnObject(world, seed.Id))
        {
            return null;
        }

        return WorldObjectMutations.SpawnObject(
            world, profile.StageDefinitionIds[0], fragment, tile, anchor);
    }
}

}
