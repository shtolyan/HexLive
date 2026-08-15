using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.UnityPresentation.BuildHutTest
{
    /// <summary>
    /// The §120 build-the-hut sandbox: one hex flower, three colonists, an
    /// unbuilt hut plan and an unbuilt hearth, and every material the two bills
    /// ask for lying on the ground OUTSIDE the house.
    ///
    /// Deliberately contains no UnityEngine reference. The scene marker feeds it
    /// to the ordinary game bootstrap, and a headless probe runs the identical
    /// world — so "can they actually finish it?" is answered by stepping the
    /// real engine, not by looking at the scene.
    /// </summary>
    public static class BuildHutTestWorld
    {
        /// <summary>The hut plan is staked here; the flower is centred on it.</summary>
        public static readonly TileCoord HutTile = new TileCoord(0, 0);

        /// <summary>
        /// The hearth stands on its OWN hex. It used to share HutTile with the
        /// house, which was true while the house was one hex wide and a plan we
        /// could not build: a build-site is a structure, so a hearth site on the
        /// footprint makes StructurePlacement.HexFreeForBuild refuse the house
        /// outright and nothing would ever be staked.
        /// </summary>
        public static readonly TileCoord HearthTile = new TileCoord(1, -1);

        /// <summary>
        /// The hexes the committed plan actually covers, staked at rotation 0.
        /// Read from the plan, never retyped: {(-1,1),(0,0),(0,1)} today.
        /// </summary>
        public static IReadOnlyList<TileCoord> Footprint => _footprint ??= BuildFootprint();

        private static IReadOnlyList<TileCoord> _footprint;

        private static IReadOnlyList<TileCoord> BuildFootprint() =>
            BuildingBootstrap.FootprintTiles(ContentIds.HutPlan, HutTile, 0f);

        /// <summary>
        /// Ring 0-1 is the flower the player asked for. Ring 2 is padding and
        /// is NOT decoration: WorldStateFactory.BlockEdgeJunctions seals every
        /// junction that belongs to one tile with fewer than six neighbours, so
        /// a bare 7-hex flower would come up with its own rim unwalkable. The
        /// padding ring takes that sealing instead and the flower stays whole.
        /// </summary>
        public const int FlowerRadius = 1;
        // Ring 3 exists because ring 1 is now the BUILDING (three hexes) plus
        // the hearth, so the ~780 scattered items need rings 2-3 to sit on.
        public const int PaddingRadius = 3;

        /// <summary>
        /// Materials are scattered from this ring outwards, so nothing lands
        /// inside the house, under its walls, or on the hearth's own hex. The
        /// player's plan owns THREE hexes, all of them in ring 1 — a resource
        /// on the anchor hex's centre junction alone is enough to make
        /// HexFreeForBuild refuse the whole house.
        /// </summary>
        public const int ResourceMinRing = 2;

        /// <summary>
        /// §29H caps a colonist at ONE bottle (MaxCarriedInstances = 1) and the
        /// water itself lives on the NPC (NpcState.BottleWater), not in the
        /// ground object — so a heap of bottles is not a water supply. The pond
        /// below is. The count is the player's, kept as one constant.
        /// </summary>
        public const int BottleCount = 100;
        public const int CookedMeatCount = 60;

        /// <summary>One in hand each, plus spares for a hammer left in a field.</summary>
        public const int HammerCount = 3 + 3;

        // Every material both bills ask for, with slack so a dropped stick or a
        // stick burned as firewood cannot deadlock the build. The house bill is
        // the PLAN's (151 sticks / 101 boards / 51 rope / 30 leaves), read from
        // BlueprintBuildingPlan.Bill rather than retyped; the hearth bill is
        // SimBalance's campfire bill (12 sticks / 18 stones / 2 rope).
        public const float MaterialSlack = 1.6f;

        /// <summary>The committed plan's own bill — never a hand-copied number.</summary>
        public static (int Sticks, int Boards, int Rope, int Leaves) HouseBill =>
            BlueprintBuildingPlan.Bill(CommittedBuildingPlans.PlayerHutModules);

        public static WorldBootstrapDefinition Create(int seed = 12345)
        {
            var definition = new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings
                {
                    TickDeltaTime = 0.25f,
                    MediumTickInterval = 4,
                    SlowTickInterval = 16,
                    Seed = seed,
                    // The whole point is that they build it themselves.
                    SpawnCompletedTestHut = false,
                    StakePlayerHutPlan = true,
                    PlayerHutPlanTileQ = HutTile.Q,
                    PlayerHutPlanTileR = HutTile.R
                },
                Environment = new EnvironmentBootstrap
                {
                    // Warm enough that nobody stops building to go get warm.
                    GlobalTemperature = 24f
                }
            };

            var tiles = new List<TileBootstrap>();
            foreach (var coord in Ring(PaddingRadius))
                tiles.Add(new TileBootstrap { Q = coord.Q, R = coord.R, Walkable = true, Elevation = 1 });
            definition.Fragments.Add(new FragmentBootstrap { Id = 1, Tiles = tiles });

            // The hearth site comes from the faction home, which is the same
            // call the ordinary game makes: StakeCampfireSite leaves an UNBUILT
            // campfire build-site with its 12/18/2 bill, not a lit fire.
            definition.FactionHomes.Add(new FactionHomeBootstrap
            {
                Faction = Faction.Colony,
                TileQ = HearthTile.Q,
                TileR = HearthTile.R,
                StakeCampfireSite = true
            });

            AddColonists(definition);
            AddMaterials(definition);
            return definition;
        }

        private static void AddColonists(WorldBootstrapDefinition definition)
        {
            // Three girls, one per outer flower petal so they do not start on
            // top of each other. Appearance/name/voice stay blank: §74 rolls
            // and de-duplicates them exactly as it does in the ordinary game.
            // Off the footprint and off the hearth hex: a colonist standing on
            // the anchor's centre junction is not a structure, but starting them
            // inside the walls is a needless way to make a route report Blocked.
            var seats = new[] { new TileCoord(1, 0), new TileCoord(-1, 0), new TileCoord(0, -1) };
            for (var index = 0; index < seats.Length; index++)
            {
                definition.Npcs.Add(new NpcBootstrap
                {
                    Id = index + 1,
                    Faction = Faction.Colony,
                    FragmentId = 1,
                    TileQ = seats[index].Q,
                    TileR = seats[index].R,
                    Hunger = 0.10f,
                    Thirst = 0.10f,
                    Energy = 0.95f,
                    Comfort = 0.90f,
                    Social = 0.85f,
                    ThermalDiscomfort = 0f
                });
            }
        }

        private static void AddMaterials(WorldBootstrapDefinition definition)
        {
            var id = 1000;
            var slots = new JunctionSpread();

            void Scatter(string definitionId, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    var (tile, slot) = slots.Next();
                    definition.Objects.Add(new ObjectBootstrap
                    {
                        Id = id++,
                        DefinitionId = definitionId,
                        FragmentId = 1,
                        TileQ = tile.Q,
                        TileR = tile.R,
                        JunctionSlots = new List<int> { slot }
                    });
                }
            }

            var bill = HouseBill;
            // §120: the plan's own furniture (3 beds, the hearth, the wardrobe)
            // is staked as ordinary build-sites once the house is raised, so its
            // bill is part of what the sandbox has to have lying around. Read
            // from the same code that stamps those sites — never retyped — and
            // it is what brings LOGS into this world at all: a bed is log-framed
            // and the palms here are water (§64.9), not lumber.
            var furniture = BuildingBootstrap.PlanFurnitureBill();
            Scatter(ContentIds.Stick,
                Slack(bill.Sticks) + Slack(SimBalance.CampfireBillSticks) + Slack(furniture.Sticks));
            Scatter(ContentIds.Board, Slack(bill.Boards) + Slack(furniture.Boards));
            Scatter(ContentIds.Rope,
                Slack(bill.Rope) + Slack(SimBalance.CampfireBillRope) + Slack(furniture.Rope));
            Scatter(ContentIds.PalmLeaf, Slack(bill.Leaves) + Slack(furniture.Leaves));
            Scatter(ContentIds.Stone, Slack(SimBalance.CampfireBillStones) + Slack(furniture.Stones));
            Scatter(ContentIds.Log, Slack(furniture.Logs));

            // Tools. A hammer is not a nicety: BuildSiteMath.NeedsHammer means
            // no piece of the hut goes up without one in hand.
            Scatter(GearCatalog.Hammer, HammerCount);
            Scatter(ContentIds.Knife, 3);
            Scatter(ContentIds.AxeStone, 2);

            // Food and water.
            //
            // ⚠ The POND IS NOT A WATER SUPPLY. `water.pond` is retired content
            // (WorldSaveSerializer.MigrateRetiredContent despawns every one of
            // them on load, "the girls keep hiking to ghosts for water") and no
            // goal in DecisionSystem drinks from a Water-tagged world object at
            // all: `waterSourceReachable` is coconuts or a rain collector, full
            // stop. Measured with the pond as the only source: worst thirst
            // 1.00 and 2 of 3 colonists dead by tick 40 000.
            //
            // So the sandbox plants PALMS — renewable water and food, opened
            // with the knives already scattered above — plus a handful of loose
            // coconuts so day one is not spent walking. §64.9: a palm is WATER,
            // and the colony must not fell it.
            Scatter(PalmId, PalmCount);
            Scatter(ContentIds.Coconut, CoconutCount);
            Scatter(ContentIds.MeatCooked, CookedMeatCount);
            Scatter(ContentIds.Bottle, BottleCount);
            // Kept only so the sandbox still LOOKS like a watering hole; it is
            // inert (see above), never the reason anybody survives.
            Scatter(PondId, 1);
        }

        /// <summary>There is no ContentIds entry for the pond — it is a
        /// catalog-only definition (PrototypeContentCatalog "water.pond").</summary>
        private const string PondId = "water.pond";

        /// <summary>The colony's actual water: coconuts off renewable palms.</summary>
        private const string PalmId = "tree.palm";

        public const int PalmCount = 10;
        public const int CoconutCount = 40;

        private static int Slack(int billed) => (int)(billed * MaterialSlack) + 1;

        /// <summary>Every tile within <paramref name="radius"/> of the hut.</summary>
        private static IEnumerable<TileCoord> Ring(int radius)
        {
            for (var q = -radius; q <= radius; q++)
            for (var r = -radius; r <= radius; r++)
            {
                if (System.Math.Abs(q + r) > radius) continue;
                yield return new TileCoord(HutTile.Q + q, HutTile.R + r);
            }
        }

        /// <summary>
        /// Walks the junctions of the flower's outer petals, never the hut hex,
        /// so a dropped resource can never end up inside the house or under a
        /// wall the colonists are about to raise. Interior slots only (0..36):
        /// the r=4 boundary ring is shared between tiles and is where the walls
        /// themselves land.
        /// </summary>
        private sealed class JunctionSpread
        {
            private const int InteriorSlots = 37;
            private readonly List<TileCoord> _tiles = new();
            private int _step;

            public JunctionSpread()
            {
                for (var q = -PaddingRadius; q <= PaddingRadius; q++)
                for (var r = -PaddingRadius; r <= PaddingRadius; r++)
                {
                    if (System.Math.Abs(q + r) > PaddingRadius) continue;
                    var distance = HexDistance(q, r);
                    if (distance < ResourceMinRing) continue;   // never inside the house
                    _tiles.Add(new TileCoord(HutTile.Q + q, HutTile.R + r));
                }
                _tiles.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
            }

            /// <summary>
            /// Deals round-robin across tiles first and only then moves to the
            /// next slot, so the pile spreads over the whole ring instead of
            /// burying one petal. A stride coprime with the slot count keeps
            /// successive items off neighbouring junctions.
            /// </summary>
            public (TileCoord Tile, int Slot) Next()
            {
                var tile = _tiles[_step % _tiles.Count];
                var pass = _step / _tiles.Count;
                var slot = pass * 11 % InteriorSlots;
                _step++;
                return (tile, slot);
            }
        }

        private static int HexDistance(int q, int r) =>
            (System.Math.Abs(q) + System.Math.Abs(r) + System.Math.Abs(q + r)) / 2;
    }
}
