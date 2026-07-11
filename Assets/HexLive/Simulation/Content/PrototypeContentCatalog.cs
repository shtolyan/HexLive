using System.Collections.Generic;

namespace HexLive.Simulation.Content
{

public static class PrototypeContentCatalog
{
    public static IReadOnlyDictionary<string, ObjectDefinition> CreateDefaults()
    {
        return new Dictionary<string, ObjectDefinition>
        {
            ["food.coconut"] = new ObjectDefinition
            {
                Id = "food.coconut",
                DisplayName = "Apple",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "eat.apple",
                        Type = InteractionType.Eat,

                        // Spec 29C.9 (iter 30): a proper meal, eaten mouthful
                        // by mouthful (hunger fills gradually), not a gulp.
                        DurationTicks = 20,
                        // -0.60 since iteration 6: nights produce nothing, so a
                        // meal must carry an NPC through more dark ticks.
                        Effects = { HungerDelta = -0.6f, ComfortDelta = 0.05f }
                    },
                    new InteractionDefinition
                    {
                        Id = "pickup.apple",
                        Type = InteractionType.PickUp,

                        DurationTicks = 4
                    }
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
                Tags = { "Campfire" },
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
                        DurationTicks = 80
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
            ["tree.big"] = new ObjectDefinition
            {
                Id = "tree.big",
                DisplayName = "Big Tree",
                // Shade in iteration 20 — dies with the tree (spec 35.2).
                Tags = { "Flora", "Shade", "BigTree", "Obstacle" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "chop.big",
                        Type = InteractionType.Harvest,

                        // Spec 35.2 (iter 29): felling a whole tree takes a while.
                        DurationTicks = 80
                    }
                }
            },
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
                        DurationTicks = 60
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
                    IntervalTicks = 100,
                    MaxConcurrent = 4,
                    MaxDistanceTiles = 1
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
                        Effects = { HungerDelta = -0.9f, ComfortDelta = 0.1f }
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
            ["clothing.leather_pants"] = new ObjectDefinition
            {
                Id = "clothing.leather_pants",
                DisplayName = "Leather Pants",
                Layer = WearLayer.Wear,
                Covers = { BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "dress.leather_pants",
                        Type = InteractionType.Dress,

                        DurationTicks = 12,
                        Effects = { ThermalDelta = -0.1f, WarmthDelta = 0.2f, ArmorDelta = 0.2f }
                    }
                },
                Tags = { "Clothing", "Armor" }
            },
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
            ["resource.firewood"] = new ObjectDefinition
            {
                Id = "resource.firewood",
                DisplayName = "Firewood",
                Tags = { "Firewood" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.firewood",
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
                    ProducedDefinitionId = "resource.firewood",
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
                        Effects = { ComfortDelta = 0.4f, EnergyDelta = 0.1f }
                    }
                },
                Tags = { "Chair" }
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
                        Effects = { EnergyDelta = 0.5f, ComfortDelta = 0.2f }
                    }
                },
                Tags = { "Bed", "Obstacle" }
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
                        Effects = { EnergyDelta = 0.45f, ComfortDelta = 0.1f }
                    }
                },
                Tags = { "Bed" }
            },
            ["clothing.coat"] = new ObjectDefinition
            {
                Id = "clothing.coat",
                DisplayName = "Coat",
                Layer = WearLayer.Wear,
                Covers = { BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "dress.coat",
                        Type = InteractionType.Dress,

                        DurationTicks = 10,
                        Effects = { ThermalDelta = -0.3f, WarmthDelta = 0.4f }
                    }
                },
                Tags = { "Clothing" }
            },
            // Spec 31A.5B: everyone starts in one of these (per-NPC instance).
            ["underwear.cloth"] = new ObjectDefinition
            {
                Id = "underwear.cloth",
                DisplayName = "Cloth Underwear",
                Layer = WearLayer.Underwear,
                Covers = { BodyPart.Torso, BodyPart.Pelvis },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "dress.underwear",
                        Type = InteractionType.Dress,

                        DurationTicks = 8,
                        Effects = { WarmthDelta = 0.05f }
                    }
                },
                Tags = { "Clothing" }
            },
            // Spec 29C.4/31A.5B: armor absorbs bites on the parts it covers;
            // warmth stacks across layers — protection costs midday comfort.
            ["armor.leather"] = new ObjectDefinition
            {
                Id = "armor.leather",
                DisplayName = "Leather Armor",
                Layer = WearLayer.Outerwear,
                Covers = { BodyPart.Torso },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "dress.leather",
                        Type = InteractionType.Dress,

                        DurationTicks = 12,
                        Effects = { ThermalDelta = -0.1f, WarmthDelta = 0.15f, ArmorDelta = 0.3f }
                    }
                },
                Tags = { "Clothing", "Armor" }
            },
            ["armor.heavy"] = new ObjectDefinition
            {
                Id = "armor.heavy",
                DisplayName = "Heavy Armor",
                Layer = WearLayer.Outerwear,
                Covers = { BodyPart.Torso, BodyPart.Pelvis },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "dress.heavy",
                        Type = InteractionType.Dress,

                        DurationTicks = 16,
                        Effects = { ThermalDelta = -0.1f, WarmthDelta = 0.25f, ArmorDelta = 0.5f }
                    }
                },
                Tags = { "Clothing", "Armor" }
            }
        };
    }
}

}
