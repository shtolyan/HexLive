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
                    // Spec §54 COLD START: nothing is pre-built or handed out at
                    // home. The yard keeps only the natural resources — palms
                    // (food/wood/leaves) and deadfall (ready sticks). The hearth is
                    // a build-site the colony raises from stones (CreateCampfireSite),
                    // the raft is a coastal build-marker, and every tool/garment is
                    // gathered, found in the wild, or crafted. Retired from the
                    // yard: the finished campfire, lighter, pot, starting wood,
                    // chair, coat, armor and the wardrobe (pot/lighter/armor are
                    // now findable wilderness loot — see AddNaturalFeatures).
                    Object(104, "tree.palm", 1, -2, 2, 1),
                    Object(105, "tree.palm", 1, 1, 4, 1),
                    // §54.2: only big palms now (small palm retired).
                    Object(107, "tree.palm", 1, 3, 2, 1),
                    Object(108, "tree.palm", 1, 6, 1, 1),
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
                    },
                    // Fourth inhabitant (Jolly, imported from molly_copy): the
                    // redhead. Starts hungry and cold but rested — another
                    // desynchronized profile, so the four never queue for the
                    // same need at once. Beds still stay scarce (spec 33.4).
                    new NpcBootstrap
                    {
                        Id = 4,
                        DisplayName = "Jolly",
                        ActorMesh = "Jolly",
                        FragmentId = 1,
                        TileQ = 2,
                        TileR = 3,
                        Hunger = 0.65f,
                        Thirst = 0.45f,
                        Energy = 0.6f,
                        Comfort = 0.45f,
                        Social = 0.7f,
                        ThermalDiscomfort = 0.55f
                    }
                }
            };

            AddWilderness(definition.Fragments[0]);
            AddIslandElevation(definition.Fragments[0], seed);
            AddSeaChannel(definition.Fragments[0], seed);
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

        // §55: rivers are retired — what was the winding river is now an ordinary
        // sea inlet. The same seeded channel is carved as UNWALKABLE deep water
        // (sea = Water without Walkable), so NPCs swim it rather than wade, and
        // it is no longer drinkable. The one-deep swim ring (WorldStateFactory)
        // still opens this ≤2-wide channel so it never boxes anyone in.
        private static void AddSeaChannel(FragmentBootstrap fragment, int seed)
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
                        tile.Walkable = false; // §55: deep sea — swum, not waded, and undrinkable
                        tile.Elevation = 0;    // spec 20.16: ALL water shares one level — flush with the sea
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

            // More stone in the world: the campfire's stone ring alone wants 18,
            // and stones are also eaten by the axe/pickaxe/knife recipes — the old
            // 8 boulders + 12 loose ran the hearth short. Boulders yield 5 each
            // (needs a pickaxe), loose stones are free to pick up.
            Place("rock.boulder", 24, 331, 1);
            Place("resource.stone", 48, 443, 2);
            // §54.2: the old big tree is RETIRED, and the small palm too — only the
            // big palm (3 logs + crown) spawns now.
            Place("tree.palm", 6, 661, 1);        // big palms (3 logs)
            Place("tree.palm", 6, 557, 1);        // (was small palms — now big)
            Place("tool.saw", 1, 773, 2); // spec 35.2: findable wilderness loot
            // Spec 40.12: more scattered gear — the wilds reward exploring, and
            // a found tool saves a craft. GatherTools already collects any
            // reachable Tool not carried.
            Place("tool.saw", 1, 991, 2);
            Place("herb.bush", 3, 1213, 1); // spec 44: healing herb
            Place("plant.yucca", 18, 1327, 1); // spec §54: yucca — cut for fiber (rope/cloth); consumed, so seed plenty (beds need 8 rope = 8 fiber each)
            Place("tool.knife", 1, 1451, 2); // spec §54: one findable knife bootstraps butchering
            Place("tool.hammer", 2, 1489, 1); // spec §54.2: findable hammers raise the bed build-sites
            // Spec §54 cold start: the home conveniences are no longer handed
            // out — the pot (boiling), lighter (a spark) and armor are findable
            // wilderness loot instead, so the wilds still reward exploring.
            Place("tool.pot", 1, 1579, 2);
            Place("tool.lighter", 1, 1663, 1);
            Place("armor.leather", 1, 1741, 2);
            Place("armor.heavy", 1, 1823, 2);

            // Spec 40.18 step 4: the ONLY pickaxe sits on the second island —
            // an island-exclusive tool a GatherTools NPC must cross the strait
            // for (plus a palm for food/wood), reached via the cheap strait.
            definition.Objects.Add(Object(nextId++, "tree.palm", 1, 9, 4, 1));
            definition.Objects.Add(Object(nextId++, "tool.pickaxe_stone", 1, 9, 5, 2));

            // §55: river drink-anchors retired — rivers are gone and water is no
            // longer drinkable. Thirst is quenched by cracking a coconut instead
            // (see food.coconut's Drink interaction).

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
