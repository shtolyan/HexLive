using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{
    public static class PrototypeWorldDefinitionFactory
    {
        public static WorldBootstrapDefinition Create(int seed = 12345)
        {
            var definition = new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings
                {
                    TickDeltaTime = 0.25f,
                    MediumTickInterval = 4,
                    SlowTickInterval = 16,
                    Seed = seed
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
                            Tile(2, 3, indoor: true),

                            // Outdoor yard wrapping the west and south edges of the
                            // house, plus an east pocket. Food only grows out here,
                            // forcing the house <-> yard survival loop (iteration 2).
                            Tile(-1, 0),
                            Tile(-2, 1),
                            Tile(-1, 1),
                            Tile(-2, 2),
                            Tile(-2, 3),
                            Tile(-2, 4),
                            Tile(-1, 4),
                            Tile(0, 4),
                            Tile(1, 4),
                            Tile(2, 4),
                            Tile(3, 2),
                            Tile(4, 1)
                        }
                    }
                },
                Objects =
                {
                    // Place objects at distinct interior point slots (ring 1 positions).
                    // Food is not placed directly: apple trees (104, 105) drop it.
                    Object(101, "chair.basic", 1, 0, 2, 3),
                    Object(102, "bed.basic", 1, -1, 3, 5, 4),
                    Object(103, "clothing.coat", 1, 3, 1, 2),
                    Object(104, "tree.palm", 1, -2, 2, 1),
                    Object(105, "tree.palm", 1, 1, 4, 1),
                    // Third tree (iteration 8): a third mouth needs a third
                    // tree — two left the food economy visibly strained.
                    Object(107, "tree.palm", 1, 3, 2, 1),
                    // Second bed (iteration 6): both NPCs want to sleep at
                    // night; one shared bed would mean nightly fights.
                    Object(106, "bed.basic", 1, 2, 0, 1),
                    // Wilderness rewards (iteration 9): a far tree and armor
                    // pieces — exploration pays (spec 29C.4/29C.5).
                    Object(108, "tree.palm", 1, 6, 1, 1),
                    Object(109, "armor.leather", 1, 5, 4, 2),
                    Object(110, "armor.heavy", 1, -3, 5, 2),
                    // Water & fire chain (iteration 13, spec 29E): two ponds
                    // in the wild, a campfire in the yard, basics near home.
                    Object(111, "water.pond", 1, 5, -1, 1),
                    Object(112, "water.pond", 1, -3, 3, 1),
                    Object(113, "campfire.spot", 1, 0, 4, 2),
                    Object(114, "tool.lighter", 1, -1, 0, 1),
                    Object(115, "tool.pot", 1, 2, 4, 1),
                    Object(116, "resource.firewood", 1, -1, 1, 2),
                    Object(117, "resource.firewood", 1, 3, 2, 3),
                    Object(118, "resource.firewood", 1, 5, 2, 1),
                    Object(119, "resource.firewood", 1, -2, 4, 2),
                    Object(120, "resource.firewood", 1, 6, 4, 1),
                    Object(121, "resource.firewood", 1, 1, -1, 1),
                    Object(122, "forest.deadfall", 1, 6, 3, 2),
                    Object(123, "forest.deadfall", 1, -4, 4, 2)
                },
                Npcs =
                {
                    new NpcBootstrap
                    {
                        Id = 1,
                        DisplayName = "Marta",
                        ActorMesh = "Marta",
                        FragmentId = 1,
                        TileQ = 0,
                        TileR = 0,
                        Hunger = 0.7f,
                        Thirst = 0.5f,
                        Energy = 0.45f,
                        Comfort = 0.35f,
                        Social = 0.5f,
                        ThermalDiscomfort = 0.6f
                    },
                    // Second inhabitant (iteration 3): different need profile,
                    // contends for the single bed/chair/coat and the apples.
                    new NpcBootstrap
                    {
                        Id = 2,
                        DisplayName = "Molly",
                        ActorMesh = "Molly",
                        FragmentId = 1,
                        TileQ = 2,
                        TileR = 0,
                        Hunger = 0.55f,
                        Thirst = 0.4f,
                        Energy = 0.5f,
                        Comfort = 0.4f,
                        Social = 0.6f,
                        ThermalDiscomfort = 0.5f
                    },
                    // Third inhabitant (iteration 8): well-rested and sociable,
                    // desynchronized from the others. Beds stay at two — the
                    // scarcity is deliberate (spec 33.4 iteration-8 note).
                    new NpcBootstrap
                    {
                        Id = 3,
                        // Jolie wears Jana's body mesh — the actor set has no
                        // Jolie; the name is the colony's, the body is Jana's.
                        DisplayName = "Jolie",
                        ActorMesh = "Jana",
                        FragmentId = 1,
                        TileQ = 0,
                        TileR = 3,
                        Hunger = 0.4f,
                        Thirst = 0.6f,
                        Energy = 0.75f,
                        Comfort = 0.55f,
                        Social = 0.45f,
                        ThermalDiscomfort = 0.35f
                    }
                }
            };

            AddWilderness(definition.Fragments[0]);
            AddRiver(definition.Fragments[0], seed);
            AddNaturalFeatures(definition, seed);
            return definition;
        }

        // Spec 29C: wilderness — a walkable outdoor region around the
        // hand-authored home, filled programmatically. Room to roam, room
        // for dogs to spawn far from anyone.
        private static void AddWilderness(FragmentBootstrap fragment)
        {
            var existing = new HashSet<(int q, int r)>();
            foreach (var tile in fragment.Tiles)
            {
                existing.Add((tile.Q, tile.R));
            }

            for (var q = -8; q <= 10; q++)
            {
                for (var r = -6; r <= 8; r++)
                {
                    if (existing.Contains((q, r)))
                    {
                        continue;
                    }

                    fragment.Tiles.Add(Tile(q, r));
                }
            }
        }

        // Spec 35.1: a seeded winding river of walkable Water shallows,
        // roughly vertical in world space, east of the home.
        private static void AddRiver(FragmentBootstrap fragment, int seed)
        {
            var byCoord = new Dictionary<(int q, int r), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            var wander = 0;
            for (var r = -6; r <= 8; r++)
            {
                var roll = MathUtil.Hash01(seed, r, 0, 1201);
                wander += roll < 0.33f ? -1 : roll > 0.66f ? 1 : 0;
                wander = System.Math.Max(-2, System.Math.Min(2, wander));

                // Keep world-x roughly constant: q + r/2 ~ 8 + wander.
                var q = 8 + wander - (r + 600) / 2 + 300;
                foreach (var dq in MathUtil.Hash01(seed, r, 1, 1201) < 0.4f ? new[] { 0, 1 } : new[] { 0 })
                {
                    if (byCoord.TryGetValue((q + dq, r), out var tile) &&
                        !tile.Indoor && !tile.Blocked)
                    {
                        tile.Water = true;
                        tile.BlockedSlots.Clear();
                    }
                }
            }
        }

        // Spec 35.1: seeded terrain props — inert until iterations 18/20.
        private static void AddNaturalFeatures(WorldBootstrapDefinition definition, int seed)
        {
            var fragment = definition.Fragments[0];
            var taken = new HashSet<(int q, int r)>();
            foreach (var existing in definition.Objects)
            {
                taken.Add((existing.TileQ, existing.TileR));
            }

            var candidates = new List<(int q, int r)>();
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Walkable && !tile.Blocked && !tile.Indoor && !tile.Water &&
                    !taken.Contains((tile.Q, tile.R)))
                {
                    candidates.Add((tile.Q, tile.R));
                }
            }

            var nextId = 124;
            void Place(string definitionId, int count, int salt, int slot)
            {
                for (var i = 0; i < count && candidates.Count > 0; i++)
                {
                    var pick = (int)(MathUtil.Hash01(seed, nextId, i, salt) * candidates.Count);
                    pick = System.Math.Min(pick, candidates.Count - 1);
                    var (q, r) = candidates[pick];
                    candidates.RemoveAt(pick);
                    definition.Objects.Add(Object(nextId++, definitionId, 1, q, r, slot));
                }
            }

            Place("rock.boulder", 8, 331, 1);
            Place("resource.stone", 12, 443, 2);
            Place("tree.big", 6, 557, 1);
            Place("tree.palm", 5, 661, 1);
            Place("tool.saw", 1, 773, 2); // spec 35.2: findable wilderness loot

            // Drink spots along the river: one river object per few water rows.
            var placedRiver = 0;
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Water && placedRiver < 5 && (tile.R + 6) % 3 == 0)
                {
                    definition.Objects.Add(Object(nextId++, "water.river", 1, tile.Q, tile.R, 1));
                    placedRiver++;
                }
            }
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
