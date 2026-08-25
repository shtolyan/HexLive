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
                        RequiredCapabilities = { GearCapability.Cut },
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
                Tags = { "Coconut", "CoconutWater" },
                // §59-склад: вода внутри — 4 глотка (CoconutWaterCapacity).
                Storage = { new StoredResource { Kind = StoredKind.Water, Amount = SimBalance.CoconutWaterCapacity } }
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
                // §63 r2: + FurnitureSite — the LIVE fire keeps its open §54.14
                // upgrade bill (stone ring → spit), but without this tag the
                // BuildFurniture planner could never TARGET it: stones for the
                // ring were gathered, then had nowhere to go (0/18 delivered
                // across every 25-day soak since the staged campfire shipped).
                Tags = { "Campfire", "Obstacle", "FurnitureSite", ObjectTags.HandBuilt },
                // 0.55R (0.825 wu) blocks the anchor + the first two point
                // rings (0.375 / 0.65-0.75 wu) — nobody paths through the
                // flames — while the 1.10-1.18 wu ring stays standable, so
                // building/cooking/fueling/warming happen close to the stones
                // instead of two rings out (user: work distance must be
                // minimal). MEASURED ladder (same 10 seeds x 15d, alive):
                // 0.8R = 10/30 but all fire work at 2.09-2.25 wu (read as
                // hammering the campfire from afar); 0.4R = work at 0.75 wu
                // but 2/30 alive — the wide ember disc doubles as the camp's
                // night shield, dogs shredded the colony without it; 0.55R
                // keeps the shield (9/30, noise vs 0.8R) at HALF the work
                // distance. History: 1.05R swallowed the fireside beds (Sleep
                // failed 87-224x/seed). Bed placement stays honest at any
                // radius: FootprintClear rejects a bed whose 1.39 wu disc
                // overlaps the fire's blocked points.
                ObstacleRadius = 0.55f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius,
                // §113: сам ОГОНЬ втрое уже угольного кольца выше. Рендер даёт
                // костру 0.55R по ширине (ObjectFit), то есть радиус ~0.28R;
                // 0.30R — он же плюс ладонь запаса. По этому числу тело обходит
                // огонь, ложась на гексе костра сбоку, а не уходит с гекса.
                SolidRadius = 0.30f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius,
                Interactions =
                {
                    // §63 r2: deposit/raise the §54.14 upgrade stages (stone
                    // ring, spit posts) at the LIVE fire — same one-verb flow
                    // as build.site (ApplyFurnitureSite decides by state).
                    new InteractionDefinition
                    {
                        Id = "build.upgrade",
                        Type = InteractionType.Build,
                        // §77: ONE gather cycle (see build.site below).
                        DurationTicks = 24
                    },
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
                    new InteractionDefinition
                    {
                        Id = "ignite.fire",
                        Type = InteractionType.Ignite,
                        DurationTicks = 8,
                        RequiredCapabilities = { GearCapability.Ignite }
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
                    },
                    // §54.14: the fire is ALSO its own build-site while its
                    // upgrade bill is open (stone ring, roasting spit) —
                    // deliveries land through the same furniture-build verb.
                    // Hand-piled: no hammer at any stage.
                    new InteractionDefinition
                    {
                        Id = "build.furniture",
                        Type = InteractionType.Build,
                        DurationTicks = 24
                    },
                    // §54.14 (r2): take a cooked chunk off the roasting spit.
                    // PickUp on the FIRE is intercepted in execution — it
                    // transfers one food.meat_cooked out of Contents; the fire
                    // itself is never pocketable (no Food tag, and GetFood only
                    // targets it while cooked meat hangs).
                    new InteractionDefinition
                    {
                        Id = "take.from.spit",
                        Type = InteractionType.PickUp,
                        DurationTicks = 6
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
                // §113: валун закрывает только свой узел (ObstacleRadius 0), но
                // на экране это глыба 0.45R в поперечнике — тело обходит её,
                // ложась рядом, а не сквозь. 0.25R = её радиус плюс запас.
                SolidRadius = 0.25f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius,
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "mine.boulder",
                        Type = InteractionType.Harvest,

                        // Spec 35.2 (iter 29): breaking rock is real labor.
                        DurationTicks = 80,
                        // §54.14: the boulder model IS five packed chunks — the
                        // pickaxe breaks it into exactly those 5 stones, scattered.
                        Yields = { new HarvestDrop { DefinitionId = "resource.stone", Count = 5, Scatter = true } }
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
                // Spec 31C.1 / §29A r2: obstacle — same footprint as the stump
                // it leaves (0.3R), so the trunk blocks its anchor junction AND
                // the ring around it; felling swaps one blocker for the other.
                Tags = { "Flora", "Shade", "Palm", "Obstacle" },
                ObstacleRadius = 0.3f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius,
                // Spec 43: palm_final mesh is 3.9 wu tall (~7 elevation steps).
                ShadeSteps = 7f,
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
                    // §54.15: halved AGAIN (300 -> 600, cap 2 -> 1) now that the
                    // water collector covers thirst — coconuts stop being the
                    // island's bottomless canteen. NOTE: tree.palm is overridden
                    // by Resources/HexLive/WorldObjects/palm.asset — keep the
                    // asset's producer values in sync with these.
                    IntervalTicks = 600,
                    MaxConcurrent = 1,
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
                // §29A r2: same trunk footprint as the big palm and the stump.
                ObstacleRadius = 0.3f * HexLive.Simulation.Spatial.HexSpatialMath.HexRadius,
                // Spec 43: two trunk segments instead of three (~2.7 wu tall).
                ShadeSteps = 5f,
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
                    // §54.15: halved with the big palm (see its comment).
                    IntervalTicks = 600,
                    MaxConcurrent = 1,
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
                        // §77: ONE cycle of the deposit animation and no more.
                        // The view plays X Bot@Gathering Objects (179 frames @
                        // 30 fps = 5.97 s) on a loop for the whole interaction;
                        // at 0.25 s/tick, 24 ticks = 6.0 s covers it exactly.
                        // The old 36 (9 s) restarted the clip and cut it off
                        // halfway — a visible hitch, every delivery.
                        DurationTicks = 24
                    }
                }
            },
            // §119: the first dedicated crafting station. Its exact 0.98×0.76
            // model is assembled from the numbered FBX hierarchy; this live
            // object supplies the single fixed work point used by all crafters.
            [ContentIds.Workbench] = new ObjectDefinition
            {
                Id = ContentIds.Workbench,
                DisplayName = "Workbench",
                Tags = { "Workbench", "CraftStation", "Furniture", "Obstacle" },
                ObstacleRadius = Spec119.WorkbenchObstacleRadius,
                SolidRadius = Spec119.WorkbenchObstacleRadius,
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "craft.workbench",
                        Type = InteractionType.Craft,
                        DurationTicks = Spec119.CraftCycleWork
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
                    // §28.15F: обобрать тело — одна вещь за подход. Дольше
                    // обычного подбора (4 такта): вещь надо СНЯТЬ с человека,
                    // а не поднять с земли.
                    new InteractionDefinition
                    {
                        Id = "loot.body",
                        Type = InteractionType.Loot,

                        DurationTicks = 20
                    },
                    // Spec §54: a housemate's body can be butchered for meat + hide
                    // (cannibalism) — dark, gated behind starvation + a comfort hit.
                    new InteractionDefinition
                    {
                        Id = "butcher.body",
                        Type = InteractionType.Butcher,
                        RequiredCapabilities = { GearCapability.Butcher },
                        DurationTicks = SimBalance.ButcherDurationTicks,
                        Yields =
                        {
                            // §54.17 r2: meat goes INTO the butcher's pack
                            // (Scatter=false → GiveOrDrop), not onto the
                            // ground. Field soaks showed ground chunks are
                            // never picked up — GetFood is gated off while any
                            // food is in the pack (a coconut always is), so
                            // every kill rotted where it fell. Carried meat
                            // does not spoil and waits for a lit fire; the
                            // full-pack fallback still drops at her feet.
                            new HarvestDrop { DefinitionId = "food.meat_raw", Count = SimBalance.CarcassMeatYield, Scatter = false },
                            new HarvestDrop { DefinitionId = "resource.hide", Count = 1, Scatter = true }
                        }
                    }
                }
            },
            // §28.15C v4: через двое визуальных суток тяжёлый NPCState
            // уходит, а на том же якоре остаются скелет и один мешок-
            // контейнер. Он хранит все вещи в Contents, не спавня их по одной.
            ["remains.human"] = new ObjectDefinition
            {
                Id = "remains.human",
                // Player-facing name lives in I2: item.remains_human.name.
                DisplayName = string.Empty,
                Tags = { ObjectTags.Remains },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "mourn.remains",
                        Type = InteractionType.Observe,
                        DurationTicks = 16,
                        Effects = { ComfortDelta = 0.1f }
                    },
                    new InteractionDefinition
                    {
                        Id = "loot.remains_bag",
                        Type = InteractionType.Loot,
                        DurationTicks = 8
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
                        RequiredCapabilities = { GearCapability.Butcher },
                        DurationTicks = SimBalance.ButcherDurationTicks,
                        Yields =
                        {
                            // §54.17 r2: same as butcher.body above — meat to
                            // the pack, hide to the ground.
                            new HarvestDrop { DefinitionId = "food.meat_raw", Count = SimBalance.CarcassMeatYield, Scatter = false },
                            new HarvestDrop { DefinitionId = "resource.hide", Count = 1, Scatter = true }
                        }
                    }
                }
            },
            // §28.15C v3: могила ВЫВЕДЕНА ИЗ ОБОРОТА — хоронить больше некому и
            // незачем, тело остаётся лежать там, где упало. Определение живёт
            // дальше, но БЕЗ взаимодействий: без него старый сейв, в котором
            // могилы успели появиться, не нашёл бы для них описания при
            // загрузке. Ничто в мире её больше не порождает.
            ["grave.npc"] = new ObjectDefinition
            {
                Id = "grave.npc",
                DisplayName = "Grave",
                Tags = { "Grave" }
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
                Tags = { "Station", "Rack", ObjectTags.HandBuilt },
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
            // §133: гардероб — домашняя родня сушилки. Стоит у стены хижины,
            // держит те же 8 вещей на своём джанкшене и сушит их от очага, а не
            // от солнца. Тег "Rack" здесь несущий: он бесплатно включает
            // гардероб во всё, что уже умеет искать сушилку (DryClothes,
            // RackIsFull), — отдельной ветки «а ещё бывает гардероб» нет.
            // Obstacle НЕ ставим: комната в один гекс, дверь и кровати заперли
            // бы сами себя (идиома кроватей из BuildingBootstrap).
            ["furniture.wardrobe"] = new ObjectDefinition
            {
                Id = "furniture.wardrobe",
                DisplayName = "Wardrobe",
                // HandBuilt здесь НЕТ намеренно: этот тег отвечает на вопрос
                // «можно ли достроить руками, без молотка», а гардероб пока не
                // строится вовсе — он появляется готовым вместе с хижиной.
                Tags = { "Station", "Rack", ObjectTags.Wardrobe },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "hang.wardrobe",
                        Type = InteractionType.Hang,

                        DurationTicks = 8
                    }
                }
            },
            // ⭐ §118.2: аптечка — ящик с расходной медициной, стоит в хижине у
            // гардероба. Это КОНТЕЙНЕР, а не станция: её можно взять в слот и
            // унести, как рюкзак, поэтому запас лечится там, где он нужен.
            //
            // Obstacle НЕ ставим (как у кровати и гардероба, §133): комната в
            // один гекс, и любой лишний занятый джанкшен запирает дверь или
            // койку. Ящик стоит у стены и никому не мешает ходить.
            ["item.medkit"] = new ObjectDefinition
            {
                Id = "item.medkit",
                DisplayName = "First aid kit",
                Tags = { "Container", "Portable" },
                MaxCarriedInstances = 1,
            },
            // §54.15: the water collector — a staged fireside build-site like
            // the rack (4 planted uprights → a stone stand → the top rim →
            // rope lashings → the leaf funnel). The funnel sheds rain inward
            // to a drip point over the stand, where a container left in the
            // WC_point slot catches it. Obstacle: it occupies its junction.
            ["station.water_collector"] = new ObjectDefinition
            {
                Id = "station.water_collector",
                DisplayName = "Water collector",
                Tags = { "Station", "Obstacle", ObjectTags.HandBuilt },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "vessel.place",
                        Type = InteractionType.PlaceVessel,
                        DurationTicks = 8
                    },
                    new InteractionDefinition
                    {
                        Id = "vessel.take",
                        Type = InteractionType.TakeVessel,
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
                // §54.17: "Food" lives HERE too, not only in the Unity asset
                // override (meat_raw.asset isFood) — a world built from the
                // bare catalog (unit tests, probes) must also let GetFood
                // pick the chunk up, or the whole cook chain dies at step 1.
                Tags = { "RawMeat", "Food" },
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
            ["item.pill"] = new ObjectDefinition
            {
                Id = "item.pill",
                DisplayName = "Pill",
                Tags = { "Medicine" },
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "pickup.pill",
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
                MaxCarriedInstances = 1,
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
                        RequiredCapabilities = { GearCapability.ChopWood },
                        DurationTicks = SimBalance.LogSplitDurationTicks,
                        Yields =
                        {
                            new HarvestDrop
                            {
                                DefinitionId = "resource.stick",
                                Count = SimBalance.LogSplitYield,
                                Scatter = true
                            }
                        }
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
                        Effects = { ComfortDelta = SimBalance.ChairComfort }
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
                        Effects = { ComfortDelta = SimBalance.GroundSitComfortLedge }
                    }
                },
                Tags = { "Stump", "Obstacle" }
            },
            ["bed.basic"] = new ObjectDefinition
            {
                Id = "bed.basic",
                DisplayName = "Bed",
                // Spec 31C.7 / §54.9A: the bedroll is solid furniture and claims
                // its PHYSICAL footprint (bed_basic_final measures 1.20×2.20 wu
                // → half-diagonal 1.25) so nothing else is placed across it.
                ObstacleRadius = 1.25f,
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "sleep.bed",
                        Type = InteractionType.Sleep,

                        // Spec 31C.7A: real sleep blocks, not catnaps.
                        DurationTicks = 100,
                        // §49/§54.11 r2: comfort and Energy no longer live on
                        // the timed interaction. NeedsDecaySystem owns both the
                        // unified sleep-comfort formula and the single 4-hour
                        // bed Energy clock.
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
            },
            // §121.1: куст — легальная цель ручного приказа. Обдирается
            // руками (без capability-гейта) и УНИЧТОЖАЕТСЯ, рассыпав листья:
            // мгновенная добыча против возобновляемого источника — осознанный
            // размен, который решает игрок. ИИ этой интеракцией не пользуется
            // (ни одна цель аукциона не строит план на Harvest куста).
            Interactions =
            {
                new InteractionDefinition
                {
                    Id = "strip.herb",
                    Type = InteractionType.Harvest,
                    DurationTicks = 8,
                    Yields = { new HarvestDrop { DefinitionId = "resource.herb_leaf", Count = 3, Scatter = true } }
                }
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
                    // §84: the yucca falls like a tree, only ~6× quicker — the
                    // big palm is 60 authored ticks, so the stalk is 10 (§79
                    // then re-paces by the blade: knife 0.75 → 13, axe 10,
                    // machete 5).
                    DurationTicks = 10,
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
        // Spec §79: the iron machete — the only non-stone-age blade on the
        // island. Nobody crafts it (there is no iron here): the outsider brings
        // it ashore, and the colony can only take it off his body.
        defs["tool.machete"] = new ObjectDefinition
        {
            Id = "tool.machete",
            DisplayName = "Machete",
            Tags = { "Tool", "Machete", "Weapon" },
            Interactions =
            {
                new InteractionDefinition { Id = "pickup.machete", Type = InteractionType.PickUp, DurationTicks = 4 }
            }
        };

        // §116: medical supports and limb replacements are ordinary inventory
        // objects. Side/installation state lives on the patient, not the item.
        void AddPickupItem(string id, string name, params string[] tags)
        {
            var definition = new ObjectDefinition { Id = id, DisplayName = name };
            foreach (var tag in tags) definition.Tags.Add(tag);
            definition.Interactions.Add(new InteractionDefinition
            {
                Id = $"pickup.{id}",
                Type = InteractionType.PickUp,
                DurationTicks = 4
            });
            defs[id] = definition;
        }

        AddPickupItem(ContentIds.Board, "Board", "Wood", "Resource");
        AddPickupItem(ContentIds.Splint, "Splint", "Medicine", "Splint");
        AddPickupItem(ContentIds.WoodenArm, "Wooden arm", "Medicine", "Prosthetic");
        AddPickupItem(ContentIds.WoodenLeg, "Wooden leg", "Medicine", "Prosthetic");
        AddPickupItem(ContentIds.MechanicalArm, "Mechanical arm", "Medicine", "Prosthetic", "Mechanical");
        AddPickupItem(ContentIds.MechanicalLeg, "Mechanical leg", "Medicine", "Prosthetic", "Mechanical");
        AddPickupItem(ContentIds.MechanicalPart, "Mechanical part", "Resource", "Mechanical");

        // Architectural buildings own a whole hex footprint. Their integrated
        // sleeping spots are real Sleep targets but deliberately do not claim
        // the furniture obstacle radius: two compact cots fit inside one hut.
        defs[ContentIds.Hut1Hex] = new ObjectDefinition
        {
            Id = ContentIds.Hut1Hex,
            DisplayName = "Palm hut",
            Tags = { "Building", ObjectTags.Shelter, ObjectTags.Shade, ObjectTags.HandBuilt }
        };

        // §120: тот же объект-агрегат, но модули берутся из утверждённого
        // игроком чертежа. Теги ОБЯЗАНЫ совпадать с hut_1hex: HandBuilt —
        // это то, чем BuildSiteMath.NeedsHammer отличает «вяжут руками» от
        // «нужен молоток», и потеря тега тихо превратила бы дом в стройку,
        // которую невозможно поднять без инструмента в радиусе гекса.
        defs[ContentIds.HutPlan] = new ObjectDefinition
        {
            Id = ContentIds.HutPlan,
            DisplayName = "Palm house",
            Tags = { "Building", ObjectTags.Shelter, ObjectTags.Shade, ObjectTags.HandBuilt }
        };

        // §120 v2: constructor cubes are genuine world objects. They live on
        // the Architecture placement layer, so they deliberately carry no
        // furniture Obstacle tag; the building topology owns edge blocking.
        void AddArchitecturePiece(string id, string name)
        {
            defs[id] = new ObjectDefinition
            {
                Id = id,
                DisplayName = name,
                Tags = { "Architecture", ObjectTags.HandBuilt }
            };
        }
        AddArchitecturePiece("architecture.support.wood", "Wooden support");
        AddArchitecturePiece("architecture.floor.board", "Board floor section");
        AddArchitecturePiece("architecture.wall.wood", "Wooden wall section");
        AddArchitecturePiece("architecture.window.wood", "Wooden window section");
        AddArchitecturePiece("architecture.door.wood", "Wooden door section");
        AddArchitecturePiece("architecture.roof.palm", "Palm roof section");

        // Spec §42: fold in the whole wearable wardrobe from the shared
        // library (built-in defaults, or the GarmentCatalog asset when the
        // Unity presentation layer applied it at startup).
        GarmentLibrary.AppendDefinitions(defs);

        // CraftLeather has always awarded this legacy id, but no wardrobe
        // entry authored a matching world definition. §119 makes the output a
        // physical 0% object, so it must remain pickable/dressable even when a
        // GarmentCatalog asset does not contain the old survival garment.
        if (!defs.ContainsKey(ContentIds.LeatherPants))
        {
            var leatherPants = new ObjectDefinition
            {
                Id = ContentIds.LeatherPants,
                DisplayName = "Hide Pants",
                Layer = WearLayer.Wear,
                InventoryCapacity = 4
            };
            leatherPants.Covers.Add(BodyPart.Pelvis);
            leatherPants.Covers.Add(BodyPart.LegL);
            leatherPants.Covers.Add(BodyPart.LegR);
            leatherPants.Tags.Add("Clothing");
            leatherPants.Tags.Add("Armor");
            leatherPants.Interactions.Add(new InteractionDefinition
            {
                Id = "dress." + ContentIds.LeatherPants,
                Type = InteractionType.Dress,
                DurationTicks = 8,
                Effects = { WarmthDelta = 0.16f, ArmorDelta = 0.08f }
            });
            leatherPants.Interactions.Add(new InteractionDefinition
            {
                Id = "pickup." + ContentIds.LeatherPants,
                Type = InteractionType.PickUp,
                DurationTicks = 4
            });
            defs[ContentIds.LeatherPants] = leatherPants;
        }
        return defs;
    }
}

}
