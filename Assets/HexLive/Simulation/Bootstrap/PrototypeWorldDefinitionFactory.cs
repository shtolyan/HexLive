using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
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
                            Tile(1, 0, indoor: true),
                            Tile(2, 0, indoor: true),
                            Tile(3, 0, walkable: false, blocked: true),
                            Tile(0, 1, indoor: true),
                            Tile(1, 1, indoor: true),
                            Tile(2, 1, indoor: true),
                            Tile(3, 1, indoor: true),
                            Tile(-1, 2, indoor: true),
                            Tile(0, 2, indoor: true),
                            Tile(1, 2, walkable: false, blocked: true),
                            Tile(2, 2, indoor: true),
                            Tile(-1, 3, indoor: true),
                            Tile(0, 3, indoor: true),
                            Tile(1, 3, indoor: true),
                            Tile(2, 3, indoor: true)
                        }
                    }
                },
                Objects =
                {
                    Object(100, "food.apple", 1, 2, 3, GetPrimaryItemSlot()),
                    Object(101, "chair.basic", 1, 0, 2, GetRoleSlot(PointRole.Sit)),
                    Object(102, "bed.basic", 1, -1, 3, GetRoleSlot(PointRole.Sleep), GetNearestAccessSlot(new Float2(0f, -HexSpatialMath.InteriorRingRadius))),
                    Object(103, "clothing.coat", 1, 3, 1, GetSecondaryItemSlot())
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

        private static TileBootstrap Tile(int q, int r, bool walkable = true, bool indoor = false, bool blocked = false)
        {
            return new TileBootstrap
            {
                Q = q,
                R = r,
                Walkable = walkable,
                Indoor = indoor,
                Blocked = blocked
            };
        }

        private static ObjectBootstrap Object(int id, string definitionId, int fragmentId, int tileQ, int tileR, params int[] pointSlots)
        {
            return new ObjectBootstrap
            {
                Id = id,
                DefinitionId = definitionId,
                FragmentId = fragmentId,
                TileQ = tileQ,
                TileR = tileR,
                PointSlots = new List<int>(pointSlots)
            };
        }

        private static int GetPrimaryItemSlot()
        {
            return GetRoleSlot(PointRole.Item, rank: 0);
        }

        private static int GetSecondaryItemSlot()
        {
            return GetRoleSlot(PointRole.Item, rank: 1);
        }

        private static int GetRoleSlot(PointRole role, int rank = 0)
        {
            var slots = new List<int>();
            foreach (var template in HexPointLayout.GetInteriorTemplates())
            {
                if (template.Role == role)
                {
                    slots.Add(template.Slot);
                }
            }

            if (rank < 0 || rank >= slots.Count)
            {
                throw new KeyNotFoundException("Prototype interior slot for role was not found.");
            }

            return slots[rank];
        }

        private static int GetNearestAccessSlot(Float2 target)
        {
            var bestSlot = -1;
            var bestDistance = float.MaxValue;
            foreach (var template in HexPointLayout.GetInteriorTemplates())
            {
                if (template.Role != PointRole.Access)
                {
                    continue;
                }

                var distance = HexSpatialMath.Distance(template.Offset, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestSlot = template.Slot;
                }
            }

            if (bestSlot < 0)
            {
                throw new KeyNotFoundException("Prototype access slot was not found.");
            }

            return bestSlot;
        }
    }
}
