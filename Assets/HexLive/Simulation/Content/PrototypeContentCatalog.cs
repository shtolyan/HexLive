using System.Collections.Generic;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Content
{

public static class PrototypeContentCatalog
{
    public static IReadOnlyDictionary<string, ObjectDefinition> CreateDefaults()
    {
        var defs = new Dictionary<string, ObjectDefinition>
        {
            // A whole coconut is inert food/water: it must lie on the ground
            // and be opened with a blade before anyone can drink it.
            ["food.coconut"] = new ObjectDefinition
            {
                Id = "food.coconut",
                DisplayName = "Coconut",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pierce.coconut",
                        Type = InteractionType.Process,
                        DurationTicks = SimBalance.CoconutProcessDurationTicks,
                        Yields = { new HarvestDrop { DefinitionId = "food.coconut_pierced", Count = 1, Scatter = false } }
                    },
                    new InteractionDefinition
                    {
                        Id = "pickup.coconut",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                },
                Tags = { "Food", "Coconut" }
            },
            ["food.coconut_pierced"] = new ObjectDefinition
            {
                Id = "food.coconut_pierced",
                DisplayName = "Pierced Coconut",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "drink.coconut_pierced",
                        Type = InteractionType.Drink,
                        DurationTicks = SimBalance.DrinkBottleDurationTicks,
                        Effects = { ThirstDelta = -SimBalance.CoconutThirst, ComfortDelta = 0.05f }
                    },
                    new InteractionDefinition
                    {
                        Id = "split.coconut",
                        Type = InteractionType.Process,
                        DurationTicks = SimBalance.CoconutProcessDurationTicks,
                        Yields = { new HarvestDrop { DefinitionId = "food.coconut_open", Count = 2, Scatter = false } }
                    },
                    new InteractionDefinition
                    {
                        Id = "pickup.coconut_pierced",
                        Type = InteractionType.PickUp,
                        DurationTicks = 4
                    }
                },
                Tags = { "Coconut", "CoconutWater" }
            },
            // Split coconut halves: edible flesh, no drinkable water.
            ["food.coconut_open"] = new ObjectDefinition
            {
                Id = "food.coconut_open",
                DisplayName = "Split Coconut",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "eat.coconut_open",
                        Type = InteractionType.Eat,
                        DurationTicks = 20,
                        Effects = { HungerDelta = -SimBalance.CoconutHunger, ComfortDelta = 0.05f }
                    },
                    new InteractionDefinition
                    {
                        Id = "pickup.coconut_open",
                        Type = InteractionType.PickUp,
                        DurationTicks = 4
                    },
                },
                Tags = { "Food" }
            },
            // Spec 29E: water & fire chain.
            ["water.pond"] = new ObjectDefinition
            {
                Id = "water.pond",
                DisplayName = "Pond",
                Tags = { "Water", "RawWater" },
                Interactions =
                {
                    // Spec 29H: fill the bottle (raw) — thirst is quenched only
                    // when she drinks from it later, in place.
                    new InteractionDefinition
                    {
                        Id = "fill.raw",
                        Type = InteractionType.FillBottle,

                        // Spec 29H: fill+drink must cost about one old drink —
                        // 6 + 6 ticks ~ the old single 10-tick raw drink.
                        DurationTicks = 6
                    }
                }
            },
            ["campfire.spot"] = new ObjectDefinition
            {
                Id = "campfire.spot",
                DisplayName = "Campfire",
                // Obstacle: nobody walks THROUGH the fire pit — and §47:
                // not through the EMBER RING around it either. The radius
                // blocks the anchor plus one junction ring (~one hex row),
                // so routes bend around the fire zone instead of clipping
                // the flames. Interactions still work: the beside-arrival
                // BFS (CollectStandableAround) walks through the blocked
                // cluster to the first standable rim, which stays within
                // 1 tile of the fire — full +8° warmth reach.
                Tags = { "Campfire", "Obstacle" },
                // 0.8R blocks the anchor + the tile's interior junction ring
                // (the fire hex itself is solid) while leaving the corner
                // junctions — the lattice the camp actually walks and sleeps
                // on — passable. 1.05R swallowed the fireside beds: Sleep
                // plans failed 87-224 times/seed, sleepless girls met the
                // night raids in the open (bites x5-10), wins fell 6/12→3/12.
                ObstacleRadius = 0.8f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius,
                Interactions =
                {
                    // Spec 29H: fill the bottle with boiled (safe) water; the
                    // thirst/comfort payoff lands when she drinks it later.
                    new InteractionDefinition
                    {
                        Id = "fill.boiled",
                        Type = InteractionType.FillBottle,

                        DurationTicks = 8
                    },
                    new InteractionDefinition
                    {
                        Id = "fuel.fire",
                        Type = InteractionType.Fuel,

                        DurationTicks = 8
                    },
                    // Spec 42: huddle by the flames — the WarmUp goal parks
                    // here while the fire (a real heat source now) melts the
                    // chill away; comfort seals the ritual.
                    new InteractionDefinition
                    {
                        Id = "warm.by.fire",
                        Type = InteractionType.Observe,
                        DurationTicks = 120,
                        Effects = { ComfortDelta = 0.1f }
                    },
                    // Spec 29F.3: the campfire doubles as the workbench;
                    // the recipe is selected by the crafting goal.
                    new InteractionDefinition
                    {
                        Id = "craft.at.fire",
                        Type = InteractionType.Craft,

                        DurationTicks = 12
                    }
                }
            },
            // Spec 35.1: seamless-world terrain.
            ["water.river"] = new ObjectDefinition
            {
                Id = "water.river",
                DisplayName = "River",
                Tags = { "Water", "RawWater" },
                Interactions =
                {
                    // Spec 29H: fill the bottle (raw) at the riverbank.
                    new InteractionDefinition
                    {
                        Id = "fill.river",
                        Type = InteractionType.FillBottle,

                        DurationTicks = 6
                    }
                }
            },
            ["rock.boulder"] = new ObjectDefinition
            {
                Id = "rock.boulder",
                DisplayName = "Boulder",
                Tags = { "Boulder", "Obstacle" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "mine.boulder",
                        Type = InteractionType.Harvest,

                        // Spec 35.2 (iter 29): breaking rock is real labor.
                        DurationTicks = 80,
                        // Spec §54: 4 stones scatter on the ground around the rock.
                        Yields = { new HarvestDrop { DefinitionId = "resource.stone", Count = 4, Scatter = true } }
                    }
                }
            },
            ["resource.stone"] = new ObjectDefinition
            {
                Id = "resource.stone",
                DisplayName = "Stone",
                Tags = { "Stone" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.stone",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            // §54.2: the old "tree.big" is retired — the two tree types are now
            // the big and small palms below.
            ["tree.palm"] = new ObjectDefinition
            {
                Id = "tree.palm",
                DisplayName = "Palm",
                // Spec 31C.1: obstacle — the trunk blocks its anchor junction.
                Tags = { "Flora", "Shade", "Palm", "Obstacle" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "chop.palm",
                        Type = InteractionType.Harvest,

                        // Spec 35.2 (iter 29): a palm is 60 by axe, 30 by saw.
                        DurationTicks = 60,
                        // Spec §54.2: a BIG palm (3 trunk segments) fells into 3
                        // logs + the leaf CROWN, all scattered on the ground. The
                        // crown is then chopped further to release the leaves.
                        Yields =
                        {
                            new HarvestDrop { DefinitionId = "resource.log", Count = 3, Scatter = true },
                            new HarvestDrop { DefinitionId = "resource.palm_crown", Count = 1, Scatter = true }
                        }
                    }
                },
                // Spec 31C.1: coconuts drop where apples used to grow.
                Produce = new ProduceDefinition
                {
                    ProducedDefinitionId = "food.coconut",
                    // Spec 29F.4 (iter 32): coconuts were TOO plentiful (5 per
                    // palm) — the colony just ate coconuts and never hunted.
                    // Trimmed to 3 / slower so hunger climbs into the hunt
                    // window and crab meat / hides (leather armor) matter,
                    // without starving the colony (2/170 was too harsh — crabs
                    // hug the far river and can't fully replace fruit).
                    // Still coconut-only in practice: standing cap halved
                    // (4 -> 2) so the ground pile is smaller at start, and the
                    // drop interval tripled (100 -> 300) so new coconuts fall
                    // far less often — pushing hunger into the hunt window.
                    IntervalTicks = 300,
                    MaxConcurrent = 2,
                    MaxDistanceTiles = 1
                }
            },
            // Spec §54.2: a SMALLER palm — 2 trunk segments → 2 logs + the crown.
            // Same "Palm" tag so the harvest/coconut behaviour is identical; only
            // the log count (and the assembled height) differ.
            ["tree.palm_small"] = new ObjectDefinition
            {
                Id = "tree.palm_small",
                DisplayName = "Palm",
                Tags = { "Flora", "Shade", "Palm", "Obstacle" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "chop.palm_small",
                        Type = InteractionType.Harvest,
                        DurationTicks = 45,
                        Yields =
                        {
                            new HarvestDrop { DefinitionId = "resource.log", Count = 2, Scatter = true },
                            new HarvestDrop { DefinitionId = "resource.palm_crown_small", Count = 1, Scatter = true }
                        }
                    }
                },
                Produce = new ProduceDefinition
                {
                    ProducedDefinitionId = "food.coconut",
                    IntervalTicks = 300,
                    MaxConcurrent = 2,
                    MaxDistanceTiles = 1
                }
            },
            // Spec §54.2: the palm CROWN (верхушка) — the leafy top that lands when
            // a palm is felled. Chop it (Process, with an axe) to release the loose
            // palm leaves. Two sizes: the BIG palm's crown is fuller and yields
            // more leaves than the SMALL palm's — the frond count on the tree and
            // the drop match per size.
            ["resource.palm_crown"] = new ObjectDefinition
            {
                Id = "resource.palm_crown",
                DisplayName = "Palm crown",
                Tags = { "PalmCrown", "Resource" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "chop.crown",
                        Type = InteractionType.Process,
                        DurationTicks = 24,
                        Yields = { new HarvestDrop { DefinitionId = "resource.palm_leaf", Count = SimBalance.BigPalmCrownLeaves, Scatter = true } }
                    }
                }
            },
            ["resource.palm_crown_small"] = new ObjectDefinition
            {
                Id = "resource.palm_crown_small",
                DisplayName = "Palm crown",
                Tags = { "PalmCrown", "Resource" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "chop.crown_small",
                        Type = InteractionType.Process,
                        DurationTicks = 20,
                        Yields = { new HarvestDrop { DefinitionId = "resource.palm_leaf", Count = SimBalance.SmallPalmCrownLeaves, Scatter = true } }
                    }
                }
            },
            // Spec 35.3: the communal hut anchor.
            ["construction.site"] = new ObjectDefinition
            {
                Id = "construction.site",
                DisplayName = "Construction Site",
                Tags = { "BuildSite" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "build.piece",
                        Type = InteractionType.Build,

                        DurationTicks = 30
                    }
                }
            },
            // Spec §52: a furniture build-site — an "intent point" every NPC
            // knows about. Materials are hauled in (deposited into its Contents),
            // then a builder with a hammer raises the piece. One Build
            // interaction, product/bill carried per-instance on the object.
            ["build.site"] = new ObjectDefinition
            {
                Id = "build.site",
                DisplayName = "Build Site",
                Tags = { "BuildSite", "FurnitureSite" },
                Interactions =
                {
                    // One verb: at the site, a builder either deposits the
                    // materials in hand or (if stocked, with a hammer) raises the
                    // piece — the handler decides by the site's state (§52).
                    new InteractionDefinition
                    {
                        Id = "build.furniture",
                        Type = InteractionType.Build,
                        DurationTicks = 36 // ×3 slower (longer hammer strikes)
                    }
                }
            },
            // Spec §52: the builder's hammer — a multi-use Tool, like the
            // lighter/pot. Raising any piece at a build-site needs one in hand.
            ["tool.hammer"] = new ObjectDefinition
            {
                Id = "tool.hammer",
                DisplayName = "Hammer",
                Tags = { "Tool", "Hammer" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.hammer",
                        Type = InteractionType.PickUp,
                        DurationTicks = 4
                    }
                }
            },
            // Spec 35.2: tools & harvest resources.
            ["tool.axe_stone"] = new ObjectDefinition
            {
                Id = "tool.axe_stone",
                DisplayName = "Stone Axe",
                Tags = { "Tool", "Axe" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.axe",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["tool.pickaxe_stone"] = new ObjectDefinition
            {
                Id = "tool.pickaxe_stone",
                DisplayName = "Stone Pickaxe",
                Tags = { "Tool", "Pickaxe" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.pickaxe",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["tool.saw"] = new ObjectDefinition
            {
                Id = "tool.saw",
                DisplayName = "Saw",
                Tags = { "Tool", "Saw" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.saw",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["resource.palm_leaf"] = new ObjectDefinition
            {
                Id = "resource.palm_leaf",
                DisplayName = "Palm Leaf",
                Tags = { "PalmLeaf" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.palm_leaf",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            // Spec 28.15C: a housemate's body. CurrentUser records whose.
            ["corpse.npc"] = new ObjectDefinition
            {
                Id = "corpse.npc",
                DisplayName = "Body",
                Tags = { "Corpse" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "mourn.body",
                        Type = InteractionType.Observe,

                        DurationTicks = 16,
                        Effects = { ComfortDelta = 0.1f }
                    },
                    new InteractionDefinition
                    {
                        Id = "bury.body",
                        Type = InteractionType.Bury,

                        DurationTicks = 20,
                        Effects = { ComfortDelta = 0.15f }
                    },
                    // Spec §54: a housemate's body can be butchered for meat + hide
                    // (cannibalism) — dark, gated behind starvation + a comfort hit.
                    new InteractionDefinition
                    {
                        Id = "butcher.body",
                        Type = InteractionType.Butcher,
                        DurationTicks = SimBalance.ButcherDurationTicks,
                        Yields =
                        {
                            new HarvestDrop { DefinitionId = "food.meat_raw", Count = SimBalance.CarcassMeatYield, Scatter = true },
                            new HarvestDrop { DefinitionId = "resource.hide", Count = 1, Scatter = true }
                        }
                    }
                }
            },
            // Spec §54: an animal carcass — left on the map when a beast dies.
            // Knife it (Butcher) for meat + hide; rots on the CorpseSystem clock.
            // Variant records the animal (dog/rabbit) for the renderer.
            ["carcass.animal"] = new ObjectDefinition
            {
                Id = "carcass.animal",
                DisplayName = "Carcass",
                Tags = { "Carcass", "Decays" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "butcher.carcass",
                        Type = InteractionType.Butcher,
                        DurationTicks = SimBalance.ButcherDurationTicks,
                        Yields =
                        {
                            new HarvestDrop { DefinitionId = "food.meat_raw", Count = SimBalance.CarcassMeatYield, Scatter = true },
                            new HarvestDrop { DefinitionId = "resource.hide", Count = 1, Scatter = true }
                        }
                    }
                }
            },
            // Spec 28.15D: permanent — CorpseSystem only decays the Corpse tag.
            ["grave.npc"] = new ObjectDefinition
            {
                Id = "grave.npc",
                DisplayName = "Grave",
                Tags = { "Grave" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "visit.grave",
                        Type = InteractionType.Observe,

                        DurationTicks = 12,
                        Effects = { ComfortDelta = 0.1f }
                    }
                }
            },
            // Spec §50: a limb that came off a survivor. CurrentUser records
            // whose (which actor mesh); Variant records which limb. Tagged
            // "Decays" so CorpseSystem rots it away on the body clock — but
            // without the mourn/bury interactions a corpse carries.
            ["body.limb_severed"] = new ObjectDefinition
            {
                Id = "body.limb_severed",
                DisplayName = "Severed limb",
                Tags = { "Decays", "Gore" }
            },
            // Spec §50: a prepared amputation hazard — a reef/trap tile. Not an
            // Obstacle (she can step onto it); HazardSystem takes a leg on
            // contact. Placed by a scene bootstrap / map author.
            ["hazard.trap"] = new ObjectDefinition
            {
                Id = "hazard.trap",
                DisplayName = "Trap",
                Tags = { "Hazard" }
            },
            // Spec 35.5: the drying rack — crafted, placed near the fire,
            // dries one hung garment at x5.
            ["station.drying_rack"] = new ObjectDefinition
            {
                Id = "station.drying_rack",
                DisplayName = "Drying rack",
                Tags = { "Station", "Rack" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "hang.rack",
                        Type = InteractionType.Hang,

                        DurationTicks = 8
                    }
                }
            },
            // Spec 35.6: ranged hunting.
            ["tool.bow"] = new ObjectDefinition
            {
                Id = "tool.bow",
                DisplayName = "Bow",
                Tags = { "Tool", "Weapon" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.bow",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["resource.arrow"] = new ObjectDefinition
            {
                Id = "resource.arrow",
                DisplayName = "Arrow",
                Tags = { "Resource" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.arrow",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            // Spec 29F: hunting & crafting items.
            ["tool.spear"] = new ObjectDefinition
            {
                Id = "tool.spear",
                DisplayName = "Spear",
                Tags = { "Tool", "Weapon" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.spear",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["food.meat_raw"] = new ObjectDefinition
            {
                Id = "food.meat_raw",
                DisplayName = "Raw Meat",
                // Deliberately NO Eat interaction: raw meat is inedible —
                // the fire is the only path to calories (spec 29F.3).
                Tags = { "RawMeat" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.meat_raw",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["food.meat_cooked"] = new ObjectDefinition
            {
                Id = "food.meat_cooked",
                DisplayName = "Cooked Meat",
                Tags = { "Food" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "eat.meat",
                        Type = InteractionType.Eat,

                        DurationTicks = 8,
                        Effects = { HungerDelta = -SimBalance.CookedMeatHunger, ComfortDelta = 0.1f }
                    },
                    new InteractionDefinition
                    {
                        Id = "pickup.meat_cooked",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["resource.hide"] = new ObjectDefinition
            {
                Id = "resource.hide",
                DisplayName = "Rabbit Hide",
                Tags = { "Hide" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.hide",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            // Spec §42: all wearables now live in GarmentLibrary (materialized
            // below via AppendDefinitions) — the values come from the
            // GarmentCatalog ScriptableObject at runtime.
            ["tool.lighter"] = new ObjectDefinition
            {
                Id = "tool.lighter",
                DisplayName = "Lighter",
                Tags = { "Tool" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.lighter",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            ["tool.pot"] = new ObjectDefinition
            {
                Id = "tool.pot",
                DisplayName = "Pot",
                Tags = { "Tool" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.pot",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            // Spec 40.3: a bandage — first aid. Auto-applied when bleeding to
            // dress the worst wound and stem blood loss. A carried consumable.
            ["item.bandage"] = new ObjectDefinition
            {
                Id = "item.bandage",
                DisplayName = "Bandage",
                Tags = { "Medicine" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.bandage",
                        Type = InteractionType.PickUp,
                        DurationTicks = 4
                    }
                }
            },
            // Spec 29H: the personal water bottle — a definition so it renders
            // and shows in the panel; the fill state lives on the NPC.
            ["tool.bottle"] = new ObjectDefinition
            {
                Id = "tool.bottle",
                DisplayName = "Bottle",
                Tags = { "Tool" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.bottle",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
                }
            },
            // Spec §54: resource.firewood is retired, split into two materials.
            // A LOG is the chop output (builds, raft, premium bed) and can be
            // Processed (with an axe) into 4 STICKS. A STICK is the fuel/craft
            // currency (fire, tools, arrows). Both carry the shared "Wood" tag so
            // one GatherWood goal collects either.
            ["resource.log"] = new ObjectDefinition
            {
                Id = "resource.log",
                DisplayName = "Log",
                Tags = { "Log", "Wood", "Resource" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.log",
                        Type = InteractionType.PickUp,
                        DurationTicks = 4
                    },
                    // Spec §54: chop a log into sticks in the field. Sticks scatter
                    // on the ground around the chopping spot.
                    new InteractionDefinition
                    {
                        Id = "split.log",
                        Type = InteractionType.Process,
                        DurationTicks = SimBalance.LogSplitDurationTicks,
                        Yields = { new HarvestDrop { DefinitionId = "resource.stick", Count = SimBalance.LogSplitYield, Scatter = true } }
                    }
                }
            },
            ["resource.stick"] = new ObjectDefinition
            {
                Id = "resource.stick",
                DisplayName = "Stick",
                Tags = { "Stick", "Wood", "Resource" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.stick",
                        Type = InteractionType.PickUp,
                        DurationTicks = 4
                    }
                }
            },
            ["forest.deadfall"] = new ObjectDefinition
            {
                Id = "forest.deadfall",
                DisplayName = "Deadfall",
                Tags = { "Flora" },
                Produce = new ProduceDefinition
                {
                    // Spec §54: deadfall sheds ready STICKS (no splitting needed) —
                    // the early-game fuel shortcut.
                    ProducedDefinitionId = "resource.stick",
                    IntervalTicks = 300,
                    MaxConcurrent = 3,
                    MaxDistanceTiles = 1
                }
            },
            ["chair.basic"] = new ObjectDefinition
            {
                Id = "chair.basic",
                DisplayName = "Chair",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "sit.chair",
                        Type = InteractionType.Sit,

                        // Spec 31C.7A: a proper breather, not a fidget.
                        DurationTicks = 70,
                        Effects = { ComfortDelta = SimBalance.ChairComfort, EnergyDelta = SimBalance.ChairEnergy }
                    }
                },
                Tags = { "Chair" }
            },
            // §54.2: a felled palm leaves a STUMP — a low obstacle you can perch
            // on (sit like on a ledge). Spawned in place when the palm is chopped
            // (id has no "tree" so it isn't treated as a fellable tree).
            ["stump.palm"] = new ObjectDefinition
            {
                Id = "stump.palm",
                DisplayName = "Stump",
                ObstacleRadius = 0.3f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius,
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "sit.stump",
                        Type = InteractionType.Sit,
                        DurationTicks = 70,
                        Effects = { ComfortDelta = SimBalance.GroundSitComfortLedge, EnergyDelta = SimBalance.ChairEnergy }
                    }
                },
                Tags = { "Stump", "Obstacle" }
            },
            ["bed.basic"] = new ObjectDefinition
            {
                Id = "bed.basic",
                DisplayName = "Bed",
                // Spec 31C.7: the bedroll is solid furniture.
                ObstacleRadius = 0.3f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius, // bedroll core (spec 31C.7: full footprint starved home traffic)
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "sleep.bed",
                        Type = InteractionType.Sleep,

                        // Spec 31C.7A: real sleep blocks, not catnaps.
                        DurationTicks = 100,
                        // Spec §49: comfort no longer lives on the interaction —
                        // it's the unified sleep-comfort formula in NeedsDecaySystem
                        // (surface + fire + sun + rain). Energy still lands here.
                        Effects = { EnergyDelta = SimBalance.BedEnergy } // spec 42: full night ~6h
                    }
                },
                Tags = { "Bed", "Obstacle" }
            },
            // Spec 40.15: the escape raft — a coastal build the colony hauls
            // logs to. At RaftTarget logs it can sail off the island.
            ["vessel.raft"] = new ObjectDefinition
            {
                Id = "vessel.raft",
                DisplayName = "Raft",
                Tags = { "Raft" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "build.raft",
                        Type = InteractionType.BuildRaft,
                        DurationTicks = 24
                    }
                }
            },
            // Spec 40.14: a sun shelter — a leaf canopy that shades the tiles
            // around it (reuses the Shade system: shade cuts UV to 20 %). A bed
            // can sit beneath it. Woven from 4 palm leaves at the fire.
            ["shelter.tent"] = new ObjectDefinition
            {
                Id = "shelter.tent",
                DisplayName = "Tent",
                Tags = { "Shade", "Shelter" }
            },
            // Spec 40.14: the cheap tier-1 sleeping mat — woven from 3 palm
            // leaves (no logs). A little better than bare grass, well short of
            // the bedroll. Not an obstacle (a flat mat you can step over).
            ["bed.leaf"] = new ObjectDefinition
            {
                Id = "bed.leaf",
                DisplayName = "Leaf mat",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "sleep.leaf",
                        Type = InteractionType.Sleep,
                        DurationTicks = 100,
                        // Spec §49: comfort moved to the unified sleep formula.
                        Effects = { EnergyDelta = SimBalance.LeafBedEnergy } // spec 42
                    }
                },
                Tags = { "Bed" }
            },
            // Spec 31A.5B: everyone starts in "underwear.cloth" — that garment,
            // the coat, the armors and the imported wardrobe now all live in
            // GarmentLibrary (see AppendDefinitions at the end of this method).
        };

        // Spec 44: healing herb — a bush that sheds pickable leaves; two
        // leaves craft one herbal bandage at the campfire.
        defs["herb.bush"] = new ObjectDefinition
        {
            Id = "herb.bush",
            DisplayName = "Healing herb",
            Tags = { "Flora", "HerbBush" },
            Produce = new ProduceDefinition
            {
                ProducedDefinitionId = "resource.herb_leaf",
                IntervalTicks = 240,
                MaxConcurrent = 2,
                MaxDistanceTiles = 1
            }
        };
        defs["resource.herb_leaf"] = new ObjectDefinition
        {
            Id = "resource.herb_leaf",
            DisplayName = "Herb leaf",
            Tags = { "Herb" },
            Interactions =
            {
                new InteractionDefinition
                {
                    Id = "pickup.herb",
                    Type = InteractionType.PickUp,
                    DurationTicks = 4
                }
            }
        };

        // Spec §54: the yucca — the cordage plant, straight out of Stranded
        // Deep. You CUT it with a blade (a knife or an axe — not a saw/pickaxe)
        // and its fibers scatter on the ground; craft them into rope (lashing)
        // and cloth at the campfire.
        defs["plant.yucca"] = new ObjectDefinition
        {
            Id = "plant.yucca",
            DisplayName = "Yucca",
            Tags = { "Flora", "Yucca" },
            Interactions =
            {
                new InteractionDefinition
                {
                    Id = "cut.yucca",
                    Type = InteractionType.Harvest,
                    DurationTicks = 16,
                    Yields = { new HarvestDrop { DefinitionId = "resource.fiber", Count = SimBalance.FiberPerPlant, Scatter = true } }
                }
            }
        };
        defs["resource.fiber"] = new ObjectDefinition
        {
            Id = "resource.fiber",
            DisplayName = "Plant fiber",
            Tags = { "Fiber", "Resource" },
            Interactions =
            {
                new InteractionDefinition { Id = "pickup.fiber", Type = InteractionType.PickUp, DurationTicks = 4 }
            }
        };
        defs["resource.rope"] = new ObjectDefinition
        {
            Id = "resource.rope",
            DisplayName = "Rope",
            Tags = { "Rope", "Resource" },
            Interactions =
            {
                new InteractionDefinition { Id = "pickup.rope", Type = InteractionType.PickUp, DurationTicks = 4 }
            }
        };
        defs["resource.cloth"] = new ObjectDefinition
        {
            Id = "resource.cloth",
            DisplayName = "Cloth",
            Tags = { "Cloth", "Resource" },
            Interactions =
            {
                new InteractionDefinition { Id = "pickup.cloth", Type = InteractionType.PickUp, DurationTicks = 4 }
            }
        };
        // Spec §54: the knife — a multi-use Tool required to butcher a carcass.
        defs["tool.knife"] = new ObjectDefinition
        {
            Id = "tool.knife",
            DisplayName = "Knife",
            Tags = { "Tool", "Knife" },
            Interactions =
            {
                new InteractionDefinition { Id = "pickup.knife", Type = InteractionType.PickUp, DurationTicks = 4 }
            }
        };

        // Spec §42: fold in the whole wearable wardrobe from the shared
        // library (built-in defaults, or the GarmentCatalog asset when the
        // Unity presentation layer applied it at startup).
        GarmentLibrary.AppendDefinitions(defs);
        return defs;
    }
}

}
