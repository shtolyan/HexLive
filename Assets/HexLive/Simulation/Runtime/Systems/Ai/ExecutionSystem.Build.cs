using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    // §77: where in the deposit animation the load actually changes hands. The
    // gather clip (X Bot@Gathering Objects, 5.97 s) stoops down about halfway
    // through, so that is where the material leaves her hand and appears in the
    // site's pile — the two halves of one motion, not a pop at the very end.
    // Must match NpcActorView/HexWorldRenderer's copy of the same fraction if
    // presentation ever needs to phase on it.
    internal const float BuildHandoffFraction = 0.5f;

    // Move every material the site still wants out of her hands into its pile.
    // Returns how many items changed hands, so the caller can stay silent on a
    // no-op (the completion pass runs after the §77 handoff already delivered).
    private static int DepositAtFurnitureSite(WorldState world, NPCState npc, WorldObjectState site)
    {
        var moved = 0;
        foreach (var mat in BuildSiteMath.AllMaterials)
        {
            while (BuildSiteMath.AcceptsDelivery(site, mat))
            {
                var carried = npc.Inventory.Items.Find(i => i.DefinitionId == mat);
                if (carried is null)
                {
                    break;
                }

                npc.Inventory.Items.Remove(carried);
                site.Contents.Add(carried);
                if (BuildSiteMath.IsArchitecturalBuilding(site.BuildProduct))
                {
                    BuildingRules.SyncHutElements(world, site);
                }
                moved++;
            }
        }

        // §120: a wall becomes an obstacle the moment ANY of its pieces is
        // delivered, so the topology is re-derived on the delivery — not at
        // completion. Idempotent and cheap (51 modules, integer lattice), and
        // running it here means the site and the raised building can never
        // disagree about which junctions the same walls block.
        if (moved > 0 && site.BuildProduct == ContentIds.HutPlan)
        {
            Bootstrap.BuildingBootstrap.RepairPlanTopology(world, site);
        }

        if (moved > 0 && BuildSiteMath.IsFreeArchitectureSite(site))
        {
            Blueprints.FreeArchitectureRules.SyncProgress(world, site);
        }

        if (BuildSiteMath.IsArchitecturalBuilding(site.BuildProduct) &&
            BuildingRules.FloorComplete(world, site))
        {
            // §120: the plan's footprint is as many hexes as the player floored
            // (three for hut_player_v1). One tile was the canonical hut's own
            // number, not a property of "a building".
            foreach (var footprintTile in Bootstrap.BuildingBootstrap.FootprintTiles(world, site))
            {
                if (world.Tiles.Items.TryGetValue(footprintTile, out var floorTile))
                {
                    floorTile.Flags |= TileFlags.HasFloor;
                }
            }
        }

        return moved;
    }

    private static void EmitSiteDelivered(WorldState world, NPCState npc, WorldObjectState site)
    {
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "SiteDelivered",
                $"{site.BuildProduct}: logs {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialLogs)}/{site.BillLogs} " +
                $"stones {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialStones)}/{site.BillStones} " +
                $"leaves {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialLeaves)}/{site.BillLeaves} " +
                $"sticks {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialSticks)}/{site.BillSticks} " +
                $"rope {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialRope)}/{site.BillRope} " +
                $"boards {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialBoards)}/{site.BillBoards}");
        }
    }

    // §77: the mid-animation handoff, called from the in-progress branch. The
    // load lands in the pile HERE; the raise (and the §54.14 campfire birth)
    // still waits for the end of the clip, because both despawn the object she
    // is standing at and must not happen under her hands.
    private static void RunFurnitureSiteHandoff(WorldState world, NPCState npc, WorldObjectState site)
    {
        if (npc.Execution.BuildDeposited || BuildSiteMath.IsStocked(site))
        {
            return;
        }

        if (DepositAtFurnitureSite(world, npc, site) > 0)
        {
            npc.Execution.BuildDeposited = true;
            EmitSiteDelivered(world, npc, site);
        }
    }

    // Spec §52: one visit to a furniture build-site. If it still wants
    // materials, deposit whatever needed items are in hand (partial delivery is
    // fine — many NPCs top it up over many trips). Once fully stocked, a builder
    // WITH A HAMMER raises the piece: the product spawns at the site's junction
    // and the site despawns. The hammer is a tool — it is NOT consumed.
    private static void ApplyFurnitureSite(WorldState world, NPCState npc, WorldObjectState site)
    {
        var stockedBefore = BuildSiteMath.IsStocked(site);
        // §77: normally the load is already in the pile (mid-animation handoff)
        // and this deposits nothing. It stays here as the fallback for the
        // interaction too short to reach the handoff, and for a save reloaded
        // mid-deposit — one SiteDelivered per visit either way.
        if (!stockedBefore && !npc.Execution.BuildDeposited)
        {
            DepositAtFurnitureSite(world, npc, site);
            EmitSiteDelivered(world, npc, site);
        }

        // §54.14: a campfire site raises EARLY — the moment the stage-1 stick
        // pile is delivered it becomes a real (cold, lightable) campfire that
        // keeps the open bill and accepts the upgrade stages in place. The
        // bootstrap hearth is born past this point already.
        if (site.DefinitionId == ContentIds.BuildSite && site.BuildProduct == ContentIds.Campfire &&
            BuildSiteMath.Delivered(site, BuildSiteMath.MaterialSticks) >= BuildSiteMath.CampfireStage1Sticks)
        {
            var fireJunction = site.Junctions.Count > 0 ? site.Junctions[0] : npc.CurrentJunction;
            var fireTile = site.Tile;
            WorldObjectMutations.DespawnObject(world, site.Id);
            if (fireJunction is { } fj)
            {
                var fire = WorldObjectMutations.SpawnObject(
                    world, ContentIds.Campfire, npc.Fragment, fireTile, fj);
                fire.ResourceAmount = 0f; // born cold — light it like any fire
                fire.BuildProduct = ContentIds.Campfire;
                fire.Variant = site.Variant;  // §120.2: the household hearth stays one
                fire.RotationDegrees = site.RotationDegrees; // §66: the site's facing is the piece's
                ApplyIndoorFurnitureFootprint(world, fire);
                fire.BillSticks = site.BillSticks;
                fire.BillStones = site.BillStones;
                fire.BillRope = site.BillRope;
                fire.Contents.AddRange(site.Contents);
                Trace.Emit(world, npc.Id, "FurnitureBuilt",
                    $"campfire.spot raised at stage 1, Tile={fireTile.Q},{fireTile.R} (upgrades continue in place)");
            }

            return;
        }

        // Raise it if it is now stocked and a hammer is at hand — carried, or
        // simply lying at the build site (the tool waits at the workbench). This
        // keeps the hammer a real requirement without demanding the one girl who
        // stocks the last stone also happen to be carrying it.
        var hammerAtSite = false;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!Content.GearCatalog.For(obj.DefinitionId).Has(Content.GearCapability.Hammer))
            {
                continue;
            }

            if (obj.Tile.Equals(site.Tile) ||
                HexSpatialMath.HexDistance(obj.Tile, site.Tile) <= 1)
            {
                hammerAtSite = true;
                break;
            }
        }

        // Spec §54: костёр складывают из камней, а циновку, сушилку (§35.5B) и
        // водосбор (§54.15) вяжут руками. Жёсткая мебель по-прежнему требует
        // молотка.
        //
        // Правило переехало на КОНТЕНТ (тег HandBuilt). Здесь оно было списком
        // идентификаторов через отрицание, то есть свойство вещи хранилось в
        // исполнителе: новая постройка молча получала «нужен молоток» и узнать
        // об этом можно было только по тому, что её никто не строит.
        var needsHammer = BuildSiteMath.NeedsHammer(world, site);
        if (BuildSiteMath.IsStocked(site) &&
            (!needsHammer ||
             Content.GearCatalog.HasCapability(npc.Inventory.Items, Content.GearCapability.Hammer) ||
             hammerAtSite))
        {
            // §54.14: an upgraded-in-place piece (the campfire) IS its own
            // product — completion just closes the bill. Despawn/respawn here
            // would snuff the live fire and reset its fuel.
            if (site.DefinitionId == site.BuildProduct)
            {
                if (BuildSiteMath.IsFreeArchitectureSite(site))
                {
                    Blueprints.FreeArchitectureRules.Complete(world, site);
                    Trace.Emit(world, npc.Id, "FurnitureBuilt",
                        $"{site.DefinitionId} direct architecture finished at " +
                        $"Tile={site.Tile.Q},{site.Tile.R}");
                    return;
                }
                if (BuildSiteMath.IsArchitecturalBuilding(site.BuildProduct))
                {
                    // An edited, already raised house builds its delta in
                    // place. Closing the bill must still recompute Indoor,
                    // footprint furniture and topology just like first raise.
                    Bootstrap.BuildingBootstrap.CompleteHut(world, site);
                }
                site.BuildProduct = string.Empty;
                Trace.Emit(world, npc.Id, "FurnitureBuilt",
                    $"{site.DefinitionId} upgrades finished in place at Tile={site.Tile.Q},{site.Tile.R}");
                return;
            }

            var product = site.BuildProduct;
            var tile = site.Tile;
            var owner = site.Owner;
            RaiseFurnitureSite(world, site, npc.Fragment, npc.CurrentJunction);

            Trace.Emit(world, npc.Id, "FurnitureBuilt",
                $"{product} raised at Tile={tile.Q},{tile.R}" +
                (owner is { } ow ? $" for colonist {ow.Value}" : string.Empty));
        }
    }

    /// <summary>
    /// Ядро подъёма готовой мебельной площадки — БЕЗ NPC: этим же путём
    /// стартовый дом (§120.3 r2) поднимает мебель своего чертежа при
    /// worldgen'е. Второй реализации подъёма быть не должно — забытая деталь
    /// (footprint, вариант, владелец, workbench-junction) ломается молча.
    /// </summary>
    internal static WorldObjectState RaiseFurnitureSite(
        WorldState world, WorldObjectState site,
        FragmentId fragment, JunctionId? fallbackJunction)
    {
        var junction = site.Junctions.Count > 0 ? site.Junctions[0] : fallbackJunction;
        var tile = site.Tile;
        var product = site.BuildProduct;
        // §64: a personal bed's ownership rides from the site onto the
        // finished piece — this is what makes the raised bed hers.
        var owner = site.Owner;
        // §66: so does the yaw the site was staked at — the bed must come up
        // lying side-on to the fire, not on whatever default the prefab has.
        var yaw = site.RotationDegrees;
        var variant = site.Variant;
        // Bug #188: a player blueprint is instance data.  Losing this id while
        // replacing build.site with building.hut_plan made the finished house
        // fall back to the current committed plan: the already raised walls
        // stayed one-hex, while CompleteHut staked furniture from a three-hex
        // plan.  Carry the exact draft identity across the replacement.
        var blueprintId = site.BlueprintId;
        var architectureOwner = site.Id;
        WorldObjectMutations.DespawnObject(world, site.Id);
        if (junction is not { } j)
        {
            return null;
        }

        var raised = WorldObjectMutations.SpawnObject(world, product, fragment, tile, j);
        raised.Owner = owner;
        raised.BlueprintId = blueprintId;
        raised.RotationDegrees = yaw;
        if (!string.IsNullOrEmpty(variant)) raised.Variant = variant;
        ApplyIndoorFurnitureFootprint(world, raised);
        BuildingRules.ReparentElements(world, architectureOwner, raised);
        if (product == ContentIds.Workbench)
        {
            raised.CraftJunction = StructurePlacement.WorkbenchJunction(
                world, tile, j, yaw);
        }
        else if (BuildSiteMath.IsArchitecturalBuilding(product))
        {
            Bootstrap.BuildingBootstrap.CompleteHut(world, raised);
        }

        return raised;
    }

    /// <summary>
    /// §120: indoor furniture owns its authored physical junctions, while the
    /// architecture owns walls and the door corridor. Replace the generic
    /// radius applied by SpawnObject with the exact placement footprint; the
    /// committed constructor has already validated that it does not seal its
    /// portal. Outdoors the ordinary obstacle contract remains unchanged.
    /// </summary>
    internal static void ApplyIndoorFurnitureFootprint(WorldState world, WorldObjectState raised)
    {
        if (raised == null ||
            !world.Tiles.Items.TryGetValue(raised.Tile, out var tile) ||
            !tile.Flags.HasFlag(TileFlags.Indoor))
        {
            return;
        }

        WorldObjectMutations.SetAuthoredFurnitureBlocking(world, raised, blocked: true);
    }

    // Spec 35.3: consume the bill and place the pending piece; walls block
    // edge junctions (TopologyVersion++), the door keeps one passable
    // junction marked Door; completion flips the tile Indoor and rewards
    // the colony with a wilderness bed.
    private static void ApplyBuildPiece(WorldState world, NPCState npc)
    {
        var project = world.Project;
        var piece = DecisionSystem.NextBuildPiece(world);
        if (project is null || piece is not { } bill)
        {
            return;
        }

        for (var i = 0; i < bill.Logs; i++) npc.Inventory.Items.Remove(ContentIds.Log);
        for (var i = 0; i < bill.Stones; i++) npc.Inventory.Items.Remove(ContentIds.Stone);
        for (var i = 0; i < bill.Leaves; i++) npc.Inventory.Items.Remove(ContentIds.PalmLeaf);

        if (bill.Kind == "Floor")
        {
            project.FloorDone = true;
            if (world.Tiles.Items.TryGetValue(project.Tile, out var floorTile))
            {
                floorTile.Flags |= TileFlags.HasFloor;
            }
        }
        else
        {
            var direction = HexDirection.All[bill.Edge];
            var neighbor = new TileCoord(project.Tile.Q + direction.DQ, project.Tile.R + direction.DR);
            var keepDoor = bill.Kind == "Door";
            var doorKept = false;
            if (world.Tiles.Items.TryGetValue(project.Tile, out var siteTile))
            {
                // Spec 35.3: pick the doorway first — it must be a mid-edge
                // junction (shared by exactly the site and the door
                // neighbor). Corner junctions are already walled by the
                // adjacent edges; marking one as the door seals the hut.
                if (keepDoor)
                {
                    foreach (var junctionId in siteTile.Junctions)
                    {
                        if (world.Junctions.Items.TryGetValue(junctionId, out var candidate) &&
                            candidate.Tiles.Contains(neighbor) && candidate.Tiles.Count == 2 &&
                            !candidate.Blocked)
                        {
                            candidate.Door = true;
                            doorKept = true;
                            break;
                        }
                    }
                }

                foreach (var junctionId in siteTile.Junctions)
                {
                    if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                        !junction.Tiles.Contains(neighbor) || junction.Tiles.Count < 2)
                    {
                        continue;
                    }

                    if (!junction.Blocked && !junction.Door)
                    {
                        // Nudge anyone standing where the wall goes up.
                        foreach (var bystander in world.Entities.Npcs.Values)
                        {
                            if (bystander.CurrentJunction is { } cj && cj.Equals(junctionId))
                            {
                                bystander.CurrentJunction = null;
                            }
                        }

                        junction.Blocked = true;
                    }
                }
            }

            if (keepDoor && !doorKept)
            {
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "DoorPlacementFailed",
                        $"No free mid-edge junction on edge {bill.Edge} — hut may be sealed");
                }
            }

            project.EdgeDone[bill.Edge] = true;
            world.TopologyVersion++;
        }

        Trace.Emit(world, npc.Id, "BuildProgress",
            $"{bill.Kind}{(bill.Edge >= 0 ? $" edge {bill.Edge}" : "")} placed at " +
            $"Tile={project.Tile.Q},{project.Tile.R}");

        if (DecisionSystem.NextBuildPiece(world) is null)
        {
            project.Completed = true;
            if (world.Tiles.Items.TryGetValue(project.Tile, out var hutTile))
            {
                // §35.4 r2: достроенная хижина — это и санктуарий, и настоящая
                // крыша. Второй флаг несёт только солнце и дождь.
                hutTile.Flags |= TileFlags.Indoor | TileFlags.Roofed;
                if (hutTile.Junctions.Count > 1)
                {
                    WorldObjectMutations.SpawnObject(world, ContentIds.BedBasic,
                        npc.Fragment, project.Tile, hutTile.Junctions[1]);
                }
            }

            foreach (var obj in world.Entities.Objects.Values)
            {
                if (obj.DefinitionId == ContentIds.ConstructionSite)
                {
                    WorldObjectMutations.DespawnObject(world, obj.Id);
                    break;
                }
            }

            world.TopologyVersion++;
            Trace.EmitSystem(world, "HutCompleted",
                $"Hut at Tile={project.Tile.Q},{project.Tile.R} — indoor sanctuary with a bed");
        }
    }

}

}
