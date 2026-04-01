using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{
    public static class PrototypeWorldDefinitionFactory
    {
        public static WorldBootstrapDefinition Create()
        {
            return new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings
                {
                    TickDeltaTime = 0.25f,
                    MediumTickInterval = 4,
                    SlowTickInterval = 16
                },
                Environment = new EnvironmentBootstrap
                {
                    GlobalTemperature = 9f
                },
                Fragments =
                {
                    new FragmentBootstrap
                    {
                        Id = 1,
                        Tiles = new List<TileBootstrap>
                        {
                            Tile(0, 0, indoor: true),
                            // Long wall through tile (1,0): blocks right side
                            Tile(1, 0, indoor: true, blockedSlots: new[] { 6, 7, 13, 14, 20, 21 }),
                            Tile(2, 0, indoor: true),
                            Tile(3, 0, walkable: false, blocked: true),
                            // Vertical wall in tile (0,1)
                            Tile(0, 1, indoor: true, blockedSlots: new[] { 5, 11, 18, 25, 31 }),
                            // L-shaped wall in tile (1,1)
                            Tile(1, 1, indoor: true, blockedSlots: new[] { 15, 16, 17, 18, 25, 31 }),
                            // Diagonal wall in tile (2,1)
                            Tile(2, 1, indoor: true, blockedSlots: new[] { 9, 10, 17, 24, 27 }),
                            // Partial wall in tile (3,1)
                            Tile(3, 1, indoor: true, blockedSlots: new[] { 16, 17, 23, 24 }),
                            // Corridor wall in tile (-1,2)
                            Tile(-1, 2, indoor: true, blockedSlots: new[] { 12, 13, 19, 20 }),
                            Tile(0, 2, indoor: true),
                            Tile(1, 2, walkable: false, blocked: true),
                            // Wall in tile (2,2)
                            Tile(2, 2, indoor: true, blockedSlots: new[] { 11, 18, 25 }),
                            Tile(-1, 3, indoor: true),
                            Tile(0, 3, indoor: true),
                            // Small barrier in tile (1,3)
                            Tile(1, 3, indoor: true, blockedSlots: new[] { 17, 18, 19, 24, 25 }),
                            Tile(2, 3, indoor: true)
                        }
                    }
                },
                Objects =
                {
                    // Place objects at distinct interior point slots (ring 1 positions)
                    Object(100, "food.apple", 1, 2, 3, 1),
                    Object(101, "chair.basic", 1, 0, 2, 3),
                    Object(102, "bed.basic", 1, -1, 3, 5, 4),
                    Object(103, "clothing.coat", 1, 3, 1, 2)
                },
                Npcs =
                {
                    new NpcBootstrap
                    {
                        Id = 1,
                        FragmentId = 1,
                        TileQ = 0,
                        TileR = 0,
                        Hunger = 0.7f,
                        Energy = 0.45f,
                        Comfort = 0.35f,
                        Social = 0.2f,
                        ThermalDiscomfort = 0.6f
                    }
                }
            };
        }

        private static TileBootstrap Tile(int q, int r, bool walkable = true, bool indoor = false, bool blocked = false, params int[] blockedSlots)
        {
            return new TileBootstrap
            {
                Q = q,
                R = r,
                Walkable = walkable,
                Indoor = indoor,
                Blocked = blocked,
                BlockedSlots = new List<int>(blockedSlots)
            };
        }

        private static ObjectBootstrap Object(int id, string definitionId, int fragmentId, int tileQ, int tileR, params int[] junctionSlots)
        {
            return new ObjectBootstrap
            {
                Id = id,
                DefinitionId = definitionId,
                FragmentId = fragmentId,
                TileQ = tileQ,
                TileR = tileR,
                JunctionSlots = new List<int>(junctionSlots)
            };
        }
    }
}
