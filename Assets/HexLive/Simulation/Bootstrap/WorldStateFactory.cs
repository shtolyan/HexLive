using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{

public sealed class WorldStateFactory
{
    private int _nextJunctionValue = 1;
    private readonly Dictionary<(int, int), JunctionId> _junctionsByKey = new();

    public WorldState Create(WorldBootstrapDefinition bootstrap)
    {
        var world = new WorldState
        {
            Tick = 0,
            TickDeltaTime = bootstrap.Simulation.TickDeltaTime,
            Seed = bootstrap.Simulation.Seed,
            Mode = bootstrap.Simulation.Mode
        };

        foreach (var pair in PrototypeContentCatalog.CreateDefaults())
        {
            world.Content.ObjectDefinitions[pair.Key] = pair.Value;
        }

        // Per-object assets (WorldObjectConfig → WorldObjectLibrary): merge
        // asset-declared actions/tags over the defaults, add new object types.
        WorldObjectLibrary.ApplyTo(world.Content.ObjectDefinitions);
        // One canonical bed. Unity config overrides from an older editor session
        // may still register the retired ids; they are save-input aliases only.
        world.Content.ObjectDefinitions.Remove(ContentIds.BedLeaf);
        world.Content.ObjectDefinitions.Remove(ContentIds.HutBed);

        world.Environment.GlobalTemperature = bootstrap.Environment.GlobalTemperature;

        foreach (var fragmentBootstrap in bootstrap.Fragments)
        {
            AddFragment(world, fragmentBootstrap);
        }

        BuildAdjacency(world);
        BlockEdgeJunctions(world);
        BlockCliffAndSeaJunctions(world);
        OpenSwimRing(world);
        BuildStepDeltas(world);

        foreach (var objectBootstrap in bootstrap.Objects)
        {
            AddObject(world, objectBootstrap);
        }

        foreach (var npcBootstrap in bootstrap.Npcs)
        {
            AddNpc(world, npcBootstrap);
        }

        // §74: the colony is cast here, not authored. Runs after the whole
        // roster exists because name uniqueness is a property of the group.
        AssignAppearance(world);

        // §54.10: the communal hut (spec 35.3) is retired — it was never seen in
        // play, pulled logs/stones/time away from the things that matter, and its
        // only reward was a bed.basic that the progressive bed build-site now
        // supplies. No world.Project ⇒ NextBuildPiece stays null ⇒ GoalType.Build
        // never fires. (CreateBuildProject is left defined but unused.)
        // §72: camp anchors first — the hearth site, the private camp memory and
        // a beaten raider's retreat target all key off them.
        SeedFactionHomes(world, bootstrap);

        // §54 cold start: the hearth is built, not given. One per camp: the
        // outsider raises and lights his through the very same chain.
        if (bootstrap.FactionHomes.Count == 0)
        {
            CreateCampfireSite(world, new TileCoord(0, 4));
        }
        else
        {
            foreach (var home in bootstrap.FactionHomes)
            {
                if (home.StakeCampfireSite)
                {
                    CreateCampfireSite(world, new TileCoord(home.TileQ, home.TileR));
                }
            }
        }
        if (bootstrap.Simulation.SpawnCompletedTestHut)
        {
            BuildingBootstrap.SpawnCompletedTestHut(world, Faction.Colony);
        }
        // §146.5: на большом острове ни одной готовой постройки — только
        // редактируемый чертёж Hut1Hex у каждого лагеря. ДО SeedHomeKnowledge:
        // сайт в 2-4 гексах от костра попадает в стартовую память лагеря.
        if (world.Mode == GameMode.BigIsland)
        {
            BuildingBootstrap.StakeCampHutPlans(world);
        }
        if (bootstrap.Simulation.StakePlayerHutPlan)
        {
            // §120: staked at the plan's own authored facing — passing the
            // door's local outward yaw makes the building rotation exactly 0.
            BuildingBootstrap.CreateHutPlanSite(
                world,
                new TileCoord(
                    bootstrap.Simulation.PlayerHutPlanTileQ,
                    bootstrap.Simulation.PlayerHutPlanTileR),
                BuildingRules.DoorLocalOutwardYaw(ContentIds.HutPlan));
        }
        // §54.2: beds are woven at the campfire (CraftBed tiers) — the §52 bed
        // build-site is retired, so it's no longer seeded here.
        SeedHomeKnowledge(world);

        // Spec 29E.3: campfires start cold (ResourceAmount is fuel ticks).
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains("Campfire"))
            {
                obj.ResourceAmount = 0f;
            }
        }

        // Spec 31A.5B + 42: everyone starts dressed FOR THE WEATHER — random
        // (deterministic per seed+NPC) underwear beneath a random outer set.
        // At ~10 °C ambient the [16,22] comfort band demands ~+0.5 warmth
        // (top+pants+boots ≈ 0.5 → ~15 °C effective, mild chill that the
        // campfire covers); a seeded coin-flip jacket (+0.4) makes some girls
        // genuinely comfortable and leaves others chasing the fire — texture,
        // not a death sentence. Worn items are NOT world objects, so this
        // provisions the cold WITHOUT perturbing routes/placement.
        // Spec 42 (WarmUp era): castaways wash ashore in almost nothing —
        // random (deterministic per seed+NPC) briefs + bra and MAYBE shorts.
        // Clothing barely warms; the designed way through a cold night is the
        // campfire, not the wardrobe. §133.5 makes both underwear zones a hard
        // spawn invariant: nobody is born bare below or with an open chest.
        // Пулы ВЫВОДЯТСЯ из гардероба, а не перечисляются. Списком они были
        // ровно до тех пор, пока вещей было тридцать: после импорта их 685, и
        // каждая новая партия проходила бы мимо потерпевших молча — новую вещь
        // никто бы не увидел, пока её не впишут сюда руками.
        var startBriefs = StartPool(g => g.Layer == WearLayer.Underwear &&
                                      g.Covers.Contains(BodyPart.Pelvis) &&
                                      !g.Covers.Contains(BodyPart.Torso));
        // §52.9 / баг #124: ВЕРХ — это то, что закрывает торс и НЕ претендует на
        // таз. Без второго условия в пул «лифчиков» попадали трусы с завышенной
        // талией (`briefs_strappy_mat03` = Belly+Pelvis, то есть Covers
        // Torso+Pelvis), и розыгрыш выдавал девушке ДВА низа: 20.3% колонисток
        // на 201 сиде выходили на берег в паре, дерущейся за один слот таза.
        var startBras = StartPool(g => g.Layer == WearLayer.Underwear &&
                                    g.Covers.Contains(BodyPart.Torso) &&
                                    !g.Covers.Contains(BodyPart.Pelvis));
        // Верхний низ — только ЛЁГКИЙ: шорты и юбки проходят, джинсы (0.12) и
        // платья (0.10) нет. Никто не выходит на берег в шубе (§42).
        var startShorts = StartPool(g => g.Layer == WearLayer.Wear &&
                                         g.Covers.Contains(BodyPart.Pelvis) &&
                                         g.Warmth <= 0.06f);
        // ⭐ §139.2: НАРУЧИ И ОБУВЬ В СТАРТОВОМ НАБОРЕ. Волк грызёт руки и ноги,
        // и именно они решают судьбу: рука ниже 0.20 — и колонистка не может
        // открыть кокос, то есть умирает от жажды рядом с пальмой (замер §53.9,
        // seed 476005489). Стартовый набор при этом состоял из белья и шорт —
        // брони на конечностях НОЛЬ.
        //
        // Три условия пула не косметические, каждое закрывает свою дыру:
        //   Armor >= 0.1  — розыгрыш равновероятен по пулу (StartPool сортирует
        //     по id, а не по броне), так что «хоть что-то с бронёй» выдало бы
        //     блузку на 0.01 и гарантия оказалась бы враньём;
        //   Warmth <= 0.16 — остров жаркий, шуба на ногах платится жаждой
        //     (SweatThirstFactor), ровно как у шорт выше;
        //   !Pelvis (для ног) — таз уже занят шортами, а Wear молча пропускает
        //     вещь, чей слот занят (§52.9): половина «поножей» не надевалась бы
        //     вовсе, и в замере это выглядело бы как «броня не помогает».
        // Остаётся 6 наручей (0.15) и 50 пар обуви (0.10-0.18) — набор, который
        // и на вид читается как «выброшенная на берег», а не как латы.
        var startArmGuards = StartPool(g => g.Armor >= 0.1f &&
                                            g.Warmth <= 0.16f &&
                                            g.Covers.Contains(BodyPart.ArmL) &&
                                            g.Covers.Contains(BodyPart.ArmR));
        var startLegGuards = StartPool(g => g.Armor >= 0.1f &&
                                            g.Warmth <= 0.16f &&
                                            !g.Covers.Contains(BodyPart.Pelvis) &&
                                            g.Covers.Contains(BodyPart.LegL) &&
                                            g.Covers.Contains(BodyPart.LegR));
        // §133.2 r2: every starting colonist gets one real backpack. Only the
        // dedicated Bags layer is eligible; pouches and holsters are not
        // silently promoted into backpacks by having pockets.
        var startBackpacks = StartPool(g => g.Layer == WearLayer.Bags &&
                                            g.Category == GarmentCategory.Bag &&
                                            g.Id.StartsWith("gear.backpack_", StringComparison.Ordinal));

        // §72: чужак сходит на берег не потерпевшим, а бойцом — в своём
        // тактическом комплекте. Раздавать ему женское пляжное бельё было бы
        // не только нелепо на вид: без брони он гиб на всех сидах, дважды даже
        // не успев напасть (истёк кровью; загрызла собака на 1.6-й день).
        string[] outsiderKit =
        {
            "TonnyFlash", "FCO Pants Male", "FCO Belt Male", "FCO Gloves Male",
            "FAO Harness Male", "FCO Boots Male", "FCO Legs Straps Male",
            "FCO Knee Straps Male", "FCO Waist Strappy Male",
        };

        foreach (var npc in world.Entities.Npcs.Values)
        {
            var id = npc.Id.Value;
            if (!Runtime.FactionRelations.IsColonyKind(npc.Faction))
            {
                foreach (var piece in outsiderKit)
                {
                    npc.WornItems.Add(piece);
                }

                Runtime.EquipmentMath.StripConflictingWorn(world, npc);
                Runtime.EquipmentMath.Recalculate(world, npc);
                continue;
            }

            // §133.5 r2: panties and a separate chest cover are mandatory at
            // spawn. The previous 80% bra roll made roughly every fifth
            // castaway appear bare-chested. A shirt may still be added by the
            // ordinary wardrobe later; the bra is the deterministic baseline.
            Wear(npc, startBriefs, MathUtil.Hash01(world.Seed, id, 11, 4201));
            Wear(npc, startBras, MathUtil.Hash01(world.Seed, id, 13, 4203));

            if (MathUtil.Hash01(world.Seed, id, 14, 4204) < 0.5f)
            {
                Wear(npc, startShorts, MathUtil.Hash01(world.Seed, id, 15, 4205));
            }

            // §139.2: защита конечностей — не розыгрыш «повезло/не повезло», а
            // часть набора: её носят ВСЕ. Отдельные seeds (4206/4207), чтобы
            // добавление не сдвинуло розыгрыш белья выше и старые сиды остались
            // сравнимыми по одежде.
            Wear(npc, startArmGuards, MathUtil.Hash01(world.Seed, id, 16, 4206));
            Wear(npc, startLegGuards, MathUtil.Hash01(world.Seed, id, 17, 4207));
            Wear(npc, startBackpacks, MathUtil.Hash01(world.Seed, id, 18, 4208));

            // Пояс и подтяжки к сужению пула выше: розыгрыш кладёт вещи в
            // WornItems НАПРЯМУЮ, мимо ResolveWearConflicts, поэтому единственное,
            // что здесь удерживает §52.9, — эта проверка. Новая партия одежды с
            // неожиданными Covers не должна снова уметь одеть девушку в два низа.
            Runtime.EquipmentMath.StripConflictingWorn(world, npc);
            Runtime.EquipmentMath.Recalculate(world, npc);
        }

        return world;
    }

    // Один пул стартовой одежды: всё женское из ЖИВОГО гардероба, что подходит
    // под правило. Порядок — по id: пул участвует в seeded-розыгрыше, и любая
    // нестабильность порядка развела бы один и тот же сид на разные наряды
    // (а с сервером — сервер и клиента на разные миры).
    //
    // Пустой пул означал бы голых потерпевших, поэтому пустоту тут не молчат:
    // если гардероб не доехал, честнее раздеть одну зону, чем всех.
    // Розыгрыш по уже посчитанному 0..1. Пустой пул — не повод падать: одна
    // зона останется голой, остальные оденутся.
    private static void Wear(NPCState npc, string[] pool, float roll)
    {
        if (pool.Length == 0)
        {
            return;
        }

        var index = (int)(roll * pool.Length);
        npc.WornItems.Add(new ItemInstance(pool[index >= pool.Length ? pool.Length - 1 : index])
        {
            // §133: starting clothes are personal property from tick zero.
            OwnerId = npc.Id.Value
        });
    }

    private static string[] StartPool(Func<GarmentParams, bool> keep)
    {
        var pool = new List<string>();
        foreach (var garment in GarmentLibrary.Active)
        {
            if (garment == null || garment.Sex == GarmentSex.Male || !keep(garment))
            {
                continue;
            }

            pool.Add(garment.Id);
        }

        pool.Sort(StringComparer.Ordinal);
        return pool.ToArray();
    }

    // Spec 35.3: choose the communal hut site (seeded) — walkable, dry,
    // 5-7 tiles from home, all six neighbors present and walkable; the
    // door edge faces home. A construction.site object anchors the work.
    private static void CreateBuildProject(WorldState world)
    {
        var home = new TileCoord(0, 2);
        var candidates = new List<TileCoord>();
        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            if (!tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Blocked) ||
                tile.Flags.HasFlag(TileFlags.Indoor) ||
                tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(pair.Key, home);
            if (distance is < 5 or > 7)
            {
                continue;
            }

            var allNeighborsOk = true;
            foreach (var direction in HexDirection.All)
            {
                var neighbor = new TileCoord(pair.Key.Q + direction.DQ, pair.Key.R + direction.DR);
                if (!world.Tiles.Items.TryGetValue(neighbor, out var neighborTile) ||
                    !neighborTile.Flags.HasFlag(TileFlags.Walkable) ||
                    neighborTile.Flags.HasFlag(TileFlags.Water))
                {
                    allNeighborsOk = false;
                    break;
                }
            }

            if (allNeighborsOk)
            {
                candidates.Add(pair.Key);
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        candidates.Sort((x, y) => (x.Q * 1000 + x.R).CompareTo(y.Q * 1000 + y.R));
        var pick = (int)(MathUtil.Hash01(world.Seed, 35, 3, 1901) * candidates.Count);
        pick = Math.Min(pick, candidates.Count - 1);
        var site = candidates[pick];

        var doorEdge = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < HexDirection.All.Length; i++)
        {
            var direction = HexDirection.All[i];
            var neighbor = new TileCoord(site.Q + direction.DQ, site.R + direction.DR);
            var distance = HexSpatialMath.HexDistance(neighbor, home);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                doorEdge = i;
            }
        }

        world.Project = new BuildProject { Tile = site, DoorEdge = doorEdge };

        var siteTile = world.Tiles.Items[site];
        WorldObjectMutations.SpawnObject(world, "construction.site",
            new FragmentId(1), site, siteTile.Junctions[0]);
    }

    // Spec §52: stake the communal bed as a build-site next to the campfire —
    // an intent point every NPC knows from the start (SeedHomeKnowledge runs
    // right after). Bill = 3 logs + 2 stones; a hammer raises it once stocked.
    // Placed on a free junction of a tile neighbouring the hearth so it sits in
    // the yard, not on top of the fire.
    // §54.14 (r2) cold start: the yard's chosen hearth spot holds only the
    // MARK — a bare campfire build-site (BuildSiteMath.CampfireStages). The
    // colony piles the stage-1 sticks itself, the site raises into a cold
    // campfire, and TendFire lights it (lighter or friction). Nothing is
    // pre-built or handed out; the generator-chosen good location is kept.
    private static void CreateCampfireSite(WorldState world, TileCoord hearth)
    {
        if (!world.Tiles.Items.TryGetValue(hearth, out var tile))
        {
            return;
        }

        // §66: the hearth is a BUILD — it stands at the CENTRE of its hex, and
        // the beds are then staked around it on whole hexes of their own. (New
        // worlds only: an existing save keeps the junction its fire was born on.)
        var junction = StructurePlacement.CenterJunction(world, hearth);
        if (junction is { } centerId &&
            world.Junctions.Items.TryGetValue(centerId, out var centerJn) && centerJn.Blocked)
        {
            junction = null;
        }

        if (junction is null)
        {
            foreach (var jid in tile.Junctions)
            {
                if (world.Junctions.Items.TryGetValue(jid, out var jn) && !jn.Blocked)
                {
                    junction = jid;
                    break;
                }
            }
        }

        if (junction is not { } j)
        {
            return;
        }

        // §54.14 (r2): the hearth starts as a BARE marked build-site — nothing
        // is pre-built or pre-delivered. The spot is chosen (the colony knows
        // where the fire belongs, SeedHomeKnowledge), but the stick pile itself
        // must be hauled in: the moment stage 1 (9 sticks) lands the site raises
        // into a real cold campfire (ApplyFurnitureSite), and the upgrade stages
        // (stone ring, roasting spit) keep growing in place. GOAP treats this
        // site as survival-critical (hearthUrgent) — warmth, comfort and cooking
        // all gate on it.
        var site = WorldObjectMutations.SpawnObject(world, "build.site", new FragmentId(1), hearth, j);
        site.BuildProduct = "campfire.spot";
        site.BillSticks = SimBalance.CampfireBillSticks;
        site.BillStones = SimBalance.CampfireBillStones;
        site.BillRope = SimBalance.CampfireBillRope;

        // §54.9A: the site claims the footprint of the campfire it will become
        // (BuildProduct was empty at SpawnObject time — re-invoke now).
        WorldObjectMutations.SetObstacleBlocking(world, site, blocked: true);

        // §54.14 (r2): starter sticks scattered around the marked spot — the
        // colony still hauls and piles them itself, but the material is at
        // hand (see SimBalance.CampfireStarterSticks for why this must exist).
        var scattered = 0;
        for (var ring = 1; ring <= 2 && scattered < SimBalance.CampfireStarterSticks; ring++)
        {
            foreach (var dir in HexDirection.All)
            {
                if (scattered >= SimBalance.CampfireStarterSticks)
                {
                    break;
                }

                var coord = new TileCoord(hearth.Q + dir.DQ * ring, hearth.R + dir.DR * ring);
                if (!world.Tiles.Items.TryGetValue(coord, out var around) ||
                    around.Flags.HasFlag(TileFlags.Water))
                {
                    continue;
                }

                foreach (var jid in around.Junctions)
                {
                    if (scattered >= SimBalance.CampfireStarterSticks)
                    {
                        break;
                    }

                    if (world.Junctions.Items.TryGetValue(jid, out var jn) && !jn.Blocked)
                    {
                        WorldObjectMutations.SpawnObject(world, "resource.stick", new FragmentId(1), coord, jid);
                        scattered++;
                    }
                }
            }
        }
    }

    private static void CreateBedSite(WorldState world)
    {
        // §54: anchor the bed-site by the hearth — a finished campfire if one
        // exists, otherwise the campfire build-site (cold start).
        var campfire = FindObject(world, "campfire.spot") ?? FindHearthSite(world);
        if (campfire is null)
        {
            return;
        }

        // Collect free junctions on dry tiles neighbouring the hearth, so both
        // the bed-site and the hammers land on real, walkable spots.
        var spots = new System.Collections.Generic.List<(TileCoord Tile, JunctionId Junction)>();
        foreach (var dir in HexDirection.All)
        {
            var coord = new TileCoord(campfire.Tile.Q + dir.DQ, campfire.Tile.R + dir.DR);
            if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
                tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            foreach (var jid in tile.Junctions)
            {
                if (world.Junctions.Items.TryGetValue(jid, out var jn) && !jn.Blocked)
                {
                    spots.Add((coord, jid));
                    break;
                }
            }
        }

        if (spots.Count == 0)
        {
            return;
        }

        var site = WorldObjectMutations.SpawnObject(
            world, "build.site", new FragmentId(1), spots[0].Tile, spots[0].Junction);
        site.BuildProduct = "bed.basic";
        // §54.2: the premium bedroll bill = the assembled bed_basic_final prefab's
        // real pieces (4 log rails + stick slats + rope lashings + leaf mattress),
        // so the progressive site reveals piece-per-delivery into a whole bed.
        // ⚠ Logs are heavily contested (the hearth eats them first), so a log-billed
        // starter bed can stall — if it never finishes in soak, zero BillLogs here
        // (fall back to the old all-stone frame) rather than desyncing bill/model.
        site.BillLogs = SimBalance.BedBasicBillLogs;
        site.BillSticks = SimBalance.BedBasicBillSticks;
        site.BillRope = SimBalance.BedBasicBillRope;
        site.BillLeaves = SimBalance.BedBasicBillLeaves;

        // §52: two builder's hammers near the hearth — a second girl can build
        // while the first carries one off. Placed on the remaining free spots.
        for (var i = 1; i < spots.Count && i <= 2; i++)
        {
            WorldObjectMutations.SpawnObject(
                world, "tool.hammer", new FragmentId(1), spots[i].Tile, spots[i].Junction);
        }
    }

    private static WorldObjectState FindObject(WorldState world, string definitionId)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == definitionId)
            {
                return obj;
            }
        }

        return null;
    }

    // §54 cold start: the campfire build-site (the hearth before it's raised).
    private static WorldObjectState FindHearthSite(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.BuildProduct == "campfire.spot")
            {
                return obj;
            }
        }

        return null;
    }

    // Spec 27.18A: NPCs know their home layout at start — every bootstrap
    // object becomes a permanent memory record for every NPC.
    // §72: which camp, if any, this tile belongs to. A tile inside somebody's
    // camp is that camp's business; open wilderness belongs to nobody.
    private static Faction? CampOwnerOf(WorldState world, TileCoord tile)
    {
        foreach (var pair in world.FactionHomes)
        {
            if (HexSpatialMath.HexDistance(tile, pair.Value) <=
                HexLive.Simulation.Runtime.Spec72.CampKnowledgeRadiusTiles)
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static void SeedFactionHomes(WorldState world, WorldBootstrapDefinition bootstrap)
    {
        world.FactionHomes.Clear();
        foreach (var home in bootstrap.FactionHomes)
        {
            world.FactionHomes[home.Faction] = new TileCoord(home.TileQ, home.TileR);
        }
    }

    // Everyone starts knowing the island's wilderness — the palms, the boulders,
    // the deadfall. §72: what they do NOT start knowing is the inside of someone
    // else's camp. Without that gate the outsider walks off the boat with a
    // permanent map of the girls' hearth, beds and stores.
    private static void SeedHomeKnowledge(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            foreach (var obj in world.Entities.Objects.Values)
            {
                if (CampOwnerOf(world, obj.Tile) is { } campOwner && campOwner != npc.Faction)
                {
                    continue;
                }

                npc.Memory.KnownObjects[obj.Id] = new Memory.ObjectMemory
                {
                    Id = obj.Id,
                    DefinitionId = obj.DefinitionId,
                    Tile = obj.Tile,
                    Junction = obj.Junctions.Count > 0 ? obj.Junctions[0] : null,
                    IsPermanent = true,
                    LastSeenTick = 0
                };
            }
        }
    }

    private void AddFragment(WorldState world, FragmentBootstrap bootstrap)
    {
        var fragmentId = new FragmentId(bootstrap.Id);
        var fragment = new Fragment { Id = fragmentId };
        world.Fragments.Items[fragmentId] = fragment;

        foreach (var tileBootstrap in bootstrap.Tiles)
        {
            var coord = new TileCoord(tileBootstrap.Q, tileBootstrap.R);
            var tile = new Tile
            {
                Coord = coord,
                Flags = GetTileFlags(tileBootstrap),
                Elevation = tileBootstrap.Elevation
            };

            fragment.Tiles[coord] = tile;
            world.Tiles.Items[coord] = tile;

            world.Occupancy.EntitiesInTile[coord] = new List<EntityId>();
        }

        GenerateJunctions(world, fragmentId, fragment);

        foreach (var tileBootstrap in bootstrap.Tiles)
        {
            if (tileBootstrap.BlockedSlots.Count == 0)
            {
                continue;
            }

            var coord = new TileCoord(tileBootstrap.Q, tileBootstrap.R);
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            foreach (var slot in tileBootstrap.BlockedSlots)
            {
                if (slot >= 0 && slot < tile.Junctions.Count)
                {
                    var junctionId = tile.Junctions[slot];
                    if (world.Junctions.Items.TryGetValue(junctionId, out var junction))
                    {
                        junction.Blocked = true;
                    }
                }
            }
        }
    }

    private void GenerateJunctions(WorldState world, FragmentId fragmentId, Fragment fragment)
    {
        foreach (var pair in fragment.Tiles)
        {
            var tile = pair.Value;

            foreach (var template in HexPointLayout.GetInteriorTemplates())
            {
                var key = HexPointLayout.GetJunctionKeyPair(tile.Coord, template.SubAxial);
                var junction = CreateJunction(world, fragmentId, tile, template, key);
                tile.Junctions.Add(junction.Id);
            }

            foreach (var template in HexPointLayout.GetBoundaryTemplates())
            {
                var key = HexPointLayout.GetJunctionKeyPair(tile.Coord, template.SubAxial);

                if (_junctionsByKey.TryGetValue(key, out var existingId))
                {
                    var existing = world.Junctions.Items[existingId];
                    if (!existing.Tiles.Contains(tile.Coord))
                    {
                        existing.Tiles.Add(tile.Coord);
                    }

                    tile.Junctions.Add(existingId);
                }
                else
                {
                    var junction = CreateJunction(world, fragmentId, tile, template, key);
                    tile.Junctions.Add(junction.Id);
                }
            }
        }
    }

    private Junction CreateJunction(WorldState world, FragmentId fragmentId, Tile tile, JunctionTemplate template, (int, int) key)
    {
        var junction = new Junction
        {
            Id = new JunctionId(_nextJunctionValue++),
            Fragment = fragmentId,
            WorldPosition = HexSpatialMath.TileToWorld(tile.Coord) + template.Offset
        };
        junction.Tiles.Add(tile.Coord);

        world.Junctions.Items[junction.Id] = junction;
        world.Occupancy.JunctionOwner[junction.Id] = null;
        _junctionsByKey[key] = junction.Id;

        return junction;
    }

    private void BuildAdjacency(WorldState world)
    {
        foreach (var pair in _junctionsByKey)
        {
            var key = pair.Key;
            var junctionId = pair.Value;
            var junction = world.Junctions.Items[junctionId];

            foreach (var offset in HexPointLayout.NeighborKeyOffsets)
            {
                var neighborKey = (key.Item1 + offset.dx, key.Item2 + offset.dy);
                if (_junctionsByKey.TryGetValue(neighborKey, out var neighborId))
                {
                    if (!junction.Neighbors.Contains(neighborId))
                    {
                        junction.Neighbors.Add(neighborId);
                    }
                }
            }
        }
    }

    // §40.17 v2: bake the signed elevation change of every directed edge, so the
    // pathfinder can price a CROSSING instead of the seam junction it lands on.
    // Uses the same resolver as hop arming, which is the point: the route and the
    // execution cannot disagree about what counts as a jump. ~84 KB for the
    // prototype island's 14k junctions; runs last because tile elevations are
    // final by then (Blocked does not matter — a blocked edge is filtered by the
    // search, and its delta is still correct).
    private static void BuildStepDeltas(WorldState world)
    {
        foreach (var junction in world.Junctions.Items.Values)
        {
            var deltas = new sbyte[junction.Neighbors.Count];
            for (var i = 0; i < junction.Neighbors.Count; i++)
            {
                var delta = Navigation.HexPathfinder.ResolveStepDelta(
                    world, junction.Id, junction.Neighbors[i]);
                deltas[i] = (sbyte)System.Math.Max(-127, System.Math.Min(127, delta));
            }

            junction.NeighborStepDelta = deltas;
        }
    }

    // Spec 20.16: cliffs are junction blocks — a boundary junction whose
    // owning LAND tiles differ by more than one level is impassable, and
    // junctions living entirely on unwalkable sea are closed outright.
    private static void BlockCliffAndSeaJunctions(WorldState world)
    {
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0)
            {
                continue;
            }

            var anyWalkable = false;
            var minElevation = int.MaxValue;
            var maxElevation = int.MinValue;
            foreach (var coord in junction.Tiles)
            {
                if (!world.Tiles.Items.TryGetValue(coord, out var tile))
                {
                    continue;
                }

                if (tile.Flags.HasFlag(TileFlags.Walkable))
                {
                    anyWalkable = true;
                }

                minElevation = System.Math.Min(minElevation, tile.Elevation);
                maxElevation = System.Math.Max(maxElevation, tile.Elevation);
            }

            if (!anyWalkable)
            {
                junction.Blocked = true; // open sea
                continue;
            }

            if (junction.Tiles.Count > 1 && maxElevation - minElevation > 1)
            {
                junction.Blocked = true; // cliff face
            }
            else if (junction.Tiles.Count > 1 && maxElevation - minElevation == 1)
            {
                // Spec 40.17: a walkable junction straddling a single step is a
                // climb seam — crossable, but the pathfinder charges 2x.
                world.ClimbSeams.Add(junction.Id);
            }
        }
    }

    // Spec 40.18: open a one-deep swimmable ring — sea junctions that touch
    // walkable land become crossable (unblocked + tagged SwimJunctions), so the
    // pathfinder can enter the water at a steep cost. Deeper sea stays blocked,
    // so the ring is a dead-end until a second land mass gives it a far shore.
    private static void OpenSwimRing(WorldState world)
    {
        var opened = new System.Collections.Generic.List<Common.JunctionId>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (!junction.Blocked || !SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                continue;
            }

            foreach (var neighborId in junction.Neighbors)
            {
                if (world.Junctions.Items.TryGetValue(neighborId, out var neighbor) &&
                    !neighbor.Blocked && !SpatialQueries.IsAllWaterJunction(world, neighborId))
                {
                    opened.Add(junction.Id);
                    break;
                }
            }
        }

        foreach (var id in opened)
        {
            world.Junctions.Items[id].Blocked = false;
            world.SwimJunctions.Add(id);
        }

        OpenStraitCorridor(world);
    }

    // Spec 40.18: flood the SE strait box so a connected swim path bridges the
    // peninsula to the second island (the one-deep ring alone can't cross a full
    // water tile). Bounded to the SE corner the home colony never routes into.
    private static void OpenStraitCorridor(WorldState world)
    {
        // §146.4: пролив и второй островок — деталь острова Feud; его рамка
        // считается от Feud-констант MaxQ/MaxR и на другой карте не значит
        // ничего.
        if (world.Mode == GameMode.BigIsland)
        {
            return;
        }

        var opened = new System.Collections.Generic.List<Common.JunctionId>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (!junction.Blocked || !SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                continue;
            }

            var inStrait = junction.Tiles.Count > 0;
            foreach (var coord in junction.Tiles)
            {
                // §74: пролив живёт у ВОСТОЧНОГО КРАЯ, поэтому считается от
                // границ карты. С зашитыми 7..10 расширение острова оставило бы
                // его посреди суши, и второй островок стало бы не доплыть.
                if (coord.Q < PrototypeWorldDefinitionFactory.MaxQ - 3 ||
                    coord.Q > PrototypeWorldDefinitionFactory.MaxQ ||
                    coord.R < PrototypeWorldDefinitionFactory.MaxR - 6 ||
                    coord.R > PrototypeWorldDefinitionFactory.MaxR - 2)
                {
                    inStrait = false;
                    break;
                }
            }

            if (inStrait)
            {
                opened.Add(junction.Id);
            }
        }

        foreach (var id in opened)
        {
            world.Junctions.Items[id].Blocked = false;
            world.SwimJunctions.Add(id);
            world.StraitJunctions.Add(id); // spec 40.18 step 4: cheap crossing
        }
    }

    private static void BlockEdgeJunctions(WorldState world)
    {
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Tiles.Count == 1 && junction.Neighbors.Count < 6)
            {
                junction.Blocked = true;
            }
        }
    }

    private void AddObject(WorldState world, ObjectBootstrap bootstrap)
    {
        var tileCoord = new TileCoord(bootstrap.TileQ, bootstrap.TileR);
        if (!world.Tiles.Items.TryGetValue(tileCoord, out var tile))
        {
            throw new InvalidOperationException($"Object {bootstrap.Id} references missing tile {tileCoord}.");
        }

        var worldObject = new WorldObjectState
        {
            Id = new ObjectId(bootstrap.Id),
            DefinitionId = bootstrap.DefinitionId,
            Fragment = new FragmentId(bootstrap.FragmentId),
            Tile = tileCoord,
            ResourceAmount = 1f
        };

        foreach (var slot in bootstrap.JunctionSlots)
        {
            if (slot < 0 || slot >= tile.Junctions.Count)
            {
                throw new InvalidOperationException(
                    $"Object {bootstrap.Id} references invalid junction slot {slot} on tile {tileCoord}. " +
                    $"Available slot range: 0..{tile.Junctions.Count - 1}.");
            }

            worldObject.Junctions.Add(tile.Junctions[slot]);
        }

        world.Entities.Objects[worldObject.Id] = worldObject;
        if (!world.Caches.ObjectsByTile.TryGetValue(tileCoord, out var objects))
        {
            objects = new List<ObjectId>();
            world.Caches.ObjectsByTile[tileCoord] = objects;
        }

        objects.Add(worldObject.Id);

        // Spec 31C.1/31C.7: obstacles block their anchor (and footprint).
        WorldObjectMutations.SetObstacleBlocking(world, worldObject, blocked: true);

        if (worldObject.Id.Value >= world.NextRuntimeObjectId)
        {
            world.NextRuntimeObjectId = worldObject.Id.Value + 1;
        }
    }

    // §74: fill in whatever the bootstrap left blank — body mesh, material
    // donor, hairstyle, voice bank and name — from the world seed.
    //
    // Three rules keep this from surprising anyone:
    // - a FILLED field is authorial intent and is never touched, which is why
    //   the seven test-scene bootstraps and the §72 outsider are unaffected;
    // - only Colony rolls, because the pools are the four female actresses and
    //   the outsider's male body must not receive their skins or voices;
    // - ids are walked in ASCENDING ORDER, not dictionary order, so the name
    //   and look de-duplication resolve identically on every run and platform.
    private static void AssignAppearance(WorldState world)
    {
        var ids = new List<int>();
        var takenNames = new HashSet<string>();
        var takenLooks = new HashSet<string>();
        var takenHairstyles = new HashSet<string>();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            ids.Add(npc.Id.Value);
            if (!string.IsNullOrEmpty(npc.DisplayName))
            {
                takenNames.Add(npc.DisplayName);
            }

            // A hand-authored hairstyle is claimed up front so rolled girls
            // never duplicate it (hair is a unique axis, see Roll).
            if (!string.IsNullOrEmpty(npc.Hairstyle))
            {
                takenHairstyles.Add(npc.Hairstyle);
            }
        }

        ids.Sort();

        foreach (var id in ids)
        {
            if (!world.Entities.Npcs.TryGetValue(new EntityId(id), out var npc) ||
                !Runtime.FactionRelations.IsColonyKind(npc.Faction))
            {
                continue;
            }

            var look = ColonistAppearance.Roll(world.Seed, id, takenNames, takenLooks, takenHairstyles);

            if (string.IsNullOrEmpty(npc.ActorMesh))
            {
                npc.ActorMesh = look.Mesh;
            }

            if (string.IsNullOrEmpty(npc.SkinSet))
            {
                npc.SkinSet = look.SkinSet;
            }

            if (string.IsNullOrEmpty(npc.EyeColor))
            {
                npc.EyeColor = look.EyeColor;
            }

            if (string.IsNullOrEmpty(npc.Hairstyle))
            {
                npc.Hairstyle = look.Hairstyle;
            }

            if (string.IsNullOrEmpty(npc.VoiceBank))
            {
                npc.VoiceBank = look.VoiceBank;
            }

            if (string.IsNullOrEmpty(npc.DisplayName))
            {
                npc.DisplayName = look.NameId;
                takenNames.Add(npc.DisplayName);
            }

            // Claim the look as ASSEMBLED, not as rolled: a hand-authored field
            // above may have overridden part of it, and the next girl must be
            // compared against what this one actually looks like.
            takenLooks.Add(ColonistAppearance.LookKey(
                npc.ActorMesh, npc.SkinSet, npc.Hairstyle));
            takenHairstyles.Add(npc.Hairstyle);
        }
    }

    private void AddNpc(WorldState world, NpcBootstrap bootstrap)
    {
        var coord = new TileCoord(bootstrap.TileQ, bootstrap.TileR);
        var npc = new NPCState
        {
            Id = new EntityId(bootstrap.Id),
            DisplayName = bootstrap.DisplayName,
            ActorMesh = bootstrap.ActorMesh,
            SkinSet = bootstrap.SkinSet,
            EyeColor = bootstrap.EyeColor,
            Hairstyle = bootstrap.Hairstyle,
            VoiceBank = bootstrap.VoiceBank,
            Faction = bootstrap.Faction,
            Fragment = new FragmentId(bootstrap.FragmentId),
            Tile = coord,
            Position = HexSpatialMath.TileToWorld(coord)
        };

        npc.Needs.Hunger = bootstrap.Hunger;
        npc.Needs.Thirst = bootstrap.Thirst;
        npc.Needs.Energy = bootstrap.Energy;
        npc.Needs.Comfort = bootstrap.Comfort;
        npc.Needs.Social = bootstrap.Social;
        npc.Needs.ThermalDiscomfort = bootstrap.ThermalDiscomfort;

        // Spec §53: personality compassion weight, drawn once and fixed for life.
        // Spreads the colony from reserved (helps only when idle) to deeply
        // caring (breaks off her own chores to tend the hurt). Deterministic on
        // the world seed + npc id so a replay is identical.
        //
        // §72: an outsider draws from a colder band of his own — a compassionate
        // raider would never raid, and the §53 colony band starts at 0.35.
        var traitMin = HexLive.Simulation.Runtime.Spec53.TraitMin;
        var traitMax = HexLive.Simulation.Runtime.Spec53.TraitMax;
        if (!Runtime.FactionRelations.IsColonyKind(bootstrap.Faction))
        {
            traitMin = HexLive.Simulation.Runtime.Spec72.OutsiderCompassionMin;
            traitMax = HexLive.Simulation.Runtime.Spec72.OutsiderCompassionMax;
        }

        npc.CompassionTrait = traitMin +
            MathUtil.Hash01(world.Seed, bootstrap.Id, 53, 5301) * (traitMax - traitMin);

        // Spec §76: the six innate characteristics, same deal — deterministic on
        // seed + id, fixed for life. Rolled HERE and not in the §74 appearance
        // pass, because that pass is Colony-only and the outsider must have a
        // body too. Point-buy: the deviations sum to zero, so every survivor
        // carries the same budget in a different shape.
        //
        // An authored bootstrap value wins (blank-means-roll, the §74 rule): a
        // test scene that pins a girl's Strength keeps it.
        AttributeMath.Roll(npc, world.Seed, bootstrap.Id);
        ApplyAttributeOverrides(npc, bootstrap);

        // §126: черты характера. Тот же порядок и та же доктрина, что у §76:
        // сначала ролл от сида, потом авторские — заполненное поле бутстрапа
        // это намерение автора, а не бросок. Отличие одно: у черты нет
        // «среднего», поэтому авторский список ЗАМЕНЯЕТ ролл целиком, а не
        // перекрывает по одной (иначе «дайте мне заведомо безликую» было бы
        // невыразимо).
        TraitMath.Roll(npc, world.Seed, bootstrap.Id);
        ApplyTraitOverrides(npc, bootstrap);

        // Spec 29H: everyone carries a personal water bottle (starts empty) — the
        // only starting kit. §54 cold start: the spear is no longer handed out,
        // it must be crafted (1 stick at the fire), like every other tool.
        npc.Inventory.Items.Add(new Agents.ItemInstance("tool.bottle"));

        // §40.3 / §44: the starting first-aid reserve is real cargo. Every
        // dressing remains a separate instance, while identical wraps share a
        // visible ten-item stack (medkit gauzes first, then herbal wraps).
        //
        // ⭐ §139.5: запас УДВОЕН, 4 -> 8 повязок и 1 -> 2 таблетки. Основная
        // причина смерти колонистки за первые сутки — BledOut, и разбор смертей
        // показал, что умирают они С ПУСТОЙ аптечкой: у Лены в инвентаре к
        // концу остались бутылка, копьё, кирка, нож, молоток, пила и зажигалка,
        // а бинтов — ни одного. Значит упирались не в решение «перевязаться», а
        // в наличие. Повязки СТАКУЮТСЯ (одна ячейка на десяток), поэтому
        // удвоение почти ничего не стоит по слотам — то есть не приближает
        // §52-дедлок «полный рюкзак», которым уже отравлены походы за водой.
        for (var i = 0; i < 4; i++)
        {
            npc.Inventory.Items.Add(Runtime.MedicalSupplyMath.CreateBandage(herbal: false));
        }

        for (var i = 0; i < 4; i++)
        {
            npc.Inventory.Items.Add(Runtime.MedicalSupplyMath.CreateBandage(herbal: true));
        }

        npc.Inventory.Items.Add(Runtime.MedicalSupplyMath.CreatePill());
        npc.Inventory.Items.Add(Runtime.MedicalSupplyMath.CreatePill());

        // §72 / §79: the authored opening outsider keeps his established
        // machete+knife loadout. §72.14 treats recurring arrivals as their own
        // escalation sequence (axe -> spear -> machete), so adding waves does
        // not silently rebalance the already-soaked opening scenario.
        if (!Runtime.FactionRelations.IsColonyKind(bootstrap.Faction) &&
            HexLive.Simulation.Runtime.Spec72.OutsiderStartsArmed)
        {
            npc.Inventory.Items.Add(new Agents.ItemInstance("tool.machete"));
            npc.Inventory.Items.Add(new Agents.ItemInstance("tool.knife"));
        }
        world.Entities.Npcs[npc.Id] = npc;
        world.Occupancy.EntitiesInTile[coord].Add(npc.Id);

        if (!world.Caches.EntitiesByTile.TryGetValue(coord, out var tileEntities))
        {
            tileEntities = new List<EntityId>();
            world.Caches.EntitiesByTile[coord] = tileEntities;
        }

        tileEntities.Add(npc.Id);

        if (!world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var fragmentEntities))
        {
            fragmentEntities = new List<EntityId>();
            world.Caches.EntitiesByFragment[npc.Fragment] = fragmentEntities;
        }

        fragmentEntities.Add(npc.Id);
    }

    // §76: an authored characteristic wins over the roll — the §74 rule that a
    // filled field is authorial intent and is never overwritten. Applied AFTER
    // the roll so a bootstrap can pin one attribute and leave the other five
    // to the seed.
    private static void ApplyAttributeOverrides(NPCState npc, NpcBootstrap bootstrap)
    {
        if (bootstrap.Attributes.Count == 0)
        {
            return;
        }

        foreach (var pair in bootstrap.Attributes)
        {
            npc.Attributes.Set(pair.Key, MathUtil.Clamp01(pair.Value));
        }
    }

    // §126: authored traits REPLACE the roll (null = roll, see NpcBootstrap).
    // An unknown name is dropped rather than thrown: a test-scene typo must not
    // take the world down — but it must not silently hand out a different trait
    // either, so nothing is guessed.
    private static void ApplyTraitOverrides(NPCState npc, NpcBootstrap bootstrap)
    {
        if (bootstrap.Traits is null)
        {
            return;
        }

        npc.Traits.Clear();
        foreach (var name in bootstrap.Traits)
        {
            if (Agents.TraitSet.TryParse(name, out var kind))
            {
                npc.Traits.Add(kind);
            }
        }
    }

    private static TileFlags GetTileFlags(TileBootstrap bootstrap)
    {
        var flags = TileFlags.None;
        if (bootstrap.Walkable)
        {
            flags |= TileFlags.Walkable;
        }

        if (bootstrap.Blocked)
        {
            flags |= TileFlags.Blocked;
        }

        if (bootstrap.Indoor)
        {
            flags |= TileFlags.Indoor;
        }

        if (bootstrap.Water)
        {
            flags |= TileFlags.Water;
        }

        return flags;
    }
}

}
