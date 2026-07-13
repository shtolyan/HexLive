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
                    Object(103, "clothing.coat", 1, 3, 1, 2),
                    Object(104, "tree.palm", 1, -2, 2, 1),
                    Object(105, "tree.palm", 1, 1, 4, 1),
                    // Third tree (iteration 8): a third mouth needs a third
                    // tree — two left the food economy visibly strained.
                    Object(107, "tree.palm", 1, 3, 2, 1),
                    // Second bed (iteration 6): both NPCs want to sleep at
                    // night; one shared bed would mean nightly fights.
                    // Wilderness rewards (iteration 9): a far tree and armor
                    // pieces — exploration pays (spec 29C.4/29C.5).
                    Object(108, "tree.palm", 1, 6, 1, 1),
                    Object(109, "armor.leather", 1, 5, 4, 2),
                    Object(110, "armor.heavy", 1, -3, 5, 2),
                    // Water & fire chain (iteration 13, spec 29E): a campfire
                    // in the yard, basics near home. The two wilderness ponds
                    // (objects 111/112) are RETIRED: they spent their whole
                    // life as invisible anchors on dry grass, and once given
                    // real water they read as foam-filled puddles with no
                    // depression — the river (correctly anchored drink spots)
                    // is the raw-water source now.
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
                    Object(123, "forest.deadfall", 1, -4, 4, 2),
                    // AI-print wardrobe experiment: fal.ai-generated textile
                    // prints on tank-top/panty meshes — scattered near home.
                    Object(124, "clothing.top_tropic", 1, 2, 0, 3),
                    Object(125, "clothing.top_tiedye", 1, 4, 1, 3),
                    Object(126, "underwear.panty_leo", 1, 0, 1, 3),
                    Object(127, "underwear.panty_stars", 1, 1, 1, 3)
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
                        DisplayName = "Jana",
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
            AddIslandElevation(definition.Fragments[0], seed);
            AddRiver(definition.Fragments[0], seed);
            AddNaturalFeatures(definition, seed);
            return definition;
        }

        // Spec 20.16: the world is an island — seeded value noise times a
        // radial falloff carves sea, lowland, hills and mountains. The home
        // plateau is clamped so the colony never spawns on a cliff.
        private static void AddIslandElevation(FragmentBootstrap fragment, int seed)
        {
            // Map bounds q in [-8,10], r in [-6,8] -> world-space center.
            var center = HexSpatialMath.TileToWorld(new TileCoord(1, 1));
            var edge = HexSpatialMath.TileToWorld(new TileCoord(10, 1));
            var maxDist = System.Math.Abs(edge.X - center.X);

            var home = new TileCoord(0, 2);
            var hutSite = new TileCoord(5, -4);

            foreach (var tile in fragment.Tiles)
            {
                var coord = new TileCoord(tile.Q, tile.R);
                var world = HexSpatialMath.TileToWorld(coord);
                var dx = world.X - center.X;
                var dy = world.Y - center.Y;
                var dist = System.MathF.Sqrt(dx * dx + dy * dy) / maxDist;

                // Radial island falloff: 1 at the center, 0 past ~0.95.
                var falloff = MathUtil.Clamp01(1f - dist * dist * 1.15f);

                var noise = ValueNoise(seed, world.X * 0.13f, world.Y * 0.13f) * 0.55f +
                            ValueNoise(seed + 17, world.X * 0.34f, world.Y * 0.34f) * 0.45f;
                // Sharpen: squaring pushes midtones down, peaks stand out
                // and neighboring quantization steps jump 2+ (real cliffs).
                var height = (noise * noise * 1.4f + 0.3f) * falloff;

                var elevation = (int)System.MathF.Round(height * 6f);
                elevation = System.Math.Min(5, elevation);

                // The map's outer ring is always open sea — no straight-cut
                // coastline at the world bounds.
                if (tile.Q <= -8 || tile.Q >= 10 || tile.R <= -6 || tile.R >= 8)
                {
                    elevation = 0;
                }

                // Home plateau: never sea, never cliffside.
                var nearHome = HexSpatialMath.HexDistance(coord, home) <= 3 ||
                               HexSpatialMath.HexDistance(coord, hutSite) <= 3;
                if (nearHome)
                {
                    elevation = System.Math.Max(1, System.Math.Min(2, elevation));
                }

                // Spec 40.18: a small second island in the SE sea, reachable via
                // the swim strait opened in OpenStraitCorridor.
                if (coord.Q == 9 && (coord.R == 4 || coord.R == 5))
                {
                    elevation = System.Math.Max(elevation, 1);
                }

                if (elevation <= 0)
                {
                    // Open sea: visible water, but nobody swims off the island.
                    tile.Elevation = 0;
                    tile.Water = true;
                    tile.Walkable = false;
                }
                else
                {
                    tile.Elevation = elevation;
                }
            }
        }

        // Deterministic value noise: Hash01 lattice + bilinear interpolation.
        private static float ValueNoise(int seed, float x, float y)
        {
            var x0 = (int)System.MathF.Floor(x);
            var y0 = (int)System.MathF.Floor(y);
            var tx = x - x0;
            var ty = y - y0;
            var sx = tx * tx * (3f - 2f * tx);
            var sy = ty * ty * (3f - 2f * ty);

            var v00 = MathUtil.Hash01(seed, x0, y0, 7001);
            var v10 = MathUtil.Hash01(seed, x0 + 1, y0, 7001);
            var v01 = MathUtil.Hash01(seed, x0, y0 + 1, 7001);
            var v11 = MathUtil.Hash01(seed, x0 + 1, y0 + 1, 7001);

            var a = v00 + (v10 - v00) * sx;
            var b = v01 + (v11 - v01) * sx;
            return a + (b - a) * sy;
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
                        tile.Walkable = true; // the river stays wadable even where it crosses sea-marked coast
                        tile.Elevation = 0;   // spec 20.16: ALL water shares one level — the river meets the sea flush
                        tile.BlockedSlots.Clear();
                    }
                }
            }

            // Spec 20.16: rivers flow in valleys — banks clamp to lowland so
            // the waterline is always approachable (a cliff-walled river
            // starved the colony of drink spots on the first soak).
            foreach (var tile in fragment.Tiles)
            {
                if (!tile.Water || tile.Walkable == false)
                {
                    continue;
                }

                foreach (var direction in HexDirection.All)
                {
                    // Banks clamp to 1: the river sits at sea level (0) and a
                    // >1 step to the waterline would be a cliff — no drinking.
                    if (byCoord.TryGetValue((tile.Q + direction.DQ, tile.R + direction.DR), out var bank) &&
                        !bank.Water && bank.Walkable && bank.Elevation > 1)
                    {
                        bank.Elevation = 1;
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
            // Spec 40.12: more scattered gear — the wilds reward exploring, and
            // a found tool saves a craft. GatherTools already collects any
            // reachable Tool not carried.
            Place("tool.saw", 1, 991, 2);
            Place("herb.bush", 3, 1213, 1); // spec 44: healing herb

            // Spec 40.18 step 4: the ONLY pickaxe sits on the second island —
            // an island-exclusive tool a GatherTools NPC must cross the strait
            // for (plus a palm for food/wood), reached via the cheap strait.
            definition.Objects.Add(Object(nextId++, "tree.palm", 1, 9, 4, 1));
            definition.Objects.Add(Object(nextId++, "tool.pickaxe_stone", 1, 9, 5, 2));

            // Drink spots along the river — anchored on the dry BANK beside a
            // wadable river tile ("fill at the riverbank", spec 29H), never on
            // the water itself: an in-water anchor made every fill a soak,
            // and wet clothes give no warmth — soaks showed the colony dying
            // of hypothermia once the (dry) ponds were retired.
            var tilesByCoord = new Dictionary<(int q, int r), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                tilesByCoord[(tile.Q, tile.R)] = tile;
            }

            var neighborOffsets = new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1) };
            var placedRiver = 0;
            foreach (var tile in fragment.Tiles)
            {
                if (!tile.Water || !tile.Walkable || placedRiver >= 5 || (tile.R + 6) % 3 != 0)
                {
                    continue;
                }

                foreach (var (dq, dr) in neighborOffsets)
                {
                    if (tilesByCoord.TryGetValue((tile.Q + dq, tile.R + dr), out var bank) &&
                        !bank.Water && bank.Walkable)
                    {
                        definition.Objects.Add(Object(nextId++, "water.river", 1, bank.Q, bank.R, 1));
                        placedRiver++;
                        break;
                    }
                }
            }

            // Spec 40.15: the escape raft on the coast — a walkable, non-water
            // tile beside the sea, nearest to home (the way off the island).
            var raftCoords = new Dictionary<(int q, int r), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                raftCoords[(tile.Q, tile.R)] = tile;
            }

            var home = new TileCoord(0, 2);
            TileBootstrap raftTile = null;
            var raftBest = int.MaxValue;
            foreach (var tile in fragment.Tiles)
            {
                if (!tile.Walkable || tile.Water || tile.Indoor)
                {
                    continue;
                }

                var beside = false;
                foreach (var dir in HexDirection.All)
                {
                    if (raftCoords.TryGetValue((tile.Q + dir.DQ, tile.R + dir.DR), out var n) && n.Water)
                    {
                        beside = true;
                        break;
                    }
                }

                if (!beside)
                {
                    continue;
                }

                var d = HexSpatialMath.HexDistance(new TileCoord(tile.Q, tile.R), home);
                if (d < raftBest)
                {
                    raftBest = d;
                    raftTile = tile;
                }
            }

            if (raftTile is not null)
            {
                definition.Objects.Add(Object(nextId++, "vessel.raft", 1, raftTile.Q, raftTile.R, 1));
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
