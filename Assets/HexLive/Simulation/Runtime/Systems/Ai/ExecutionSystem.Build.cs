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
    // Spec §52: one visit to a furniture build-site. If it still wants
    // materials, deposit whatever needed items are in hand (partial delivery is
    // fine — many NPCs top it up over many trips). Once fully stocked, a builder
    // WITH A HAMMER raises the piece: the product spawns at the site's junction
    // and the site despawns. The hammer is a tool — it is NOT consumed.
    private static void ApplyFurnitureSite(WorldState world, NPCState npc, WorldObjectState site)
    {
        var stockedBefore = BuildSiteMath.IsStocked(site);
        if (!stockedBefore)
        {
            // Deposit each material the site still needs, one at a time.
            foreach (var mat in BuildSiteMath.AllMaterials)
            {
                while (BuildSiteMath.Needs(site, mat))
                {
                    var carried = npc.Inventory.Items.Find(i => i.DefinitionId == mat);
                    if (carried is null)
                    {
                        break;
                    }

                    npc.Inventory.Items.Remove(carried);
                    site.Contents.Add(carried);
                }
            }

            Trace.Emit(world, npc.Id, "SiteDelivered",
                $"{site.BuildProduct}: logs {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialLogs)}/{site.BillLogs} " +
                $"stones {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialStones)}/{site.BillStones} " +
                $"leaves {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialLeaves)}/{site.BillLeaves} " +
                $"sticks {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialSticks)}/{site.BillSticks} " +
                $"rope {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialRope)}/{site.BillRope}");
        }

        // §54.14: a campfire site raises EARLY — the moment the stage-1 stick
        // pile is delivered it becomes a real (cold, lightable) campfire that
        // keeps the open bill and accepts the upgrade stages in place. The
        // bootstrap hearth is born past this point already.
        if (site.DefinitionId == "build.site" && site.BuildProduct == "campfire.spot" &&
            BuildSiteMath.Delivered(site, BuildSiteMath.MaterialSticks) >= BuildSiteMath.CampfireStage1Sticks)
        {
            var fireJunction = site.Junctions.Count > 0 ? site.Junctions[0] : npc.CurrentJunction;
            var fireTile = site.Tile;
            WorldObjectMutations.DespawnObject(world, site.Id);
            if (fireJunction is { } fj)
            {
                var fire = WorldObjectMutations.SpawnObject(
                    world, "campfire.spot", npc.Fragment, fireTile, fj);
                fire.ResourceAmount = 0f; // born cold — light it like any fire
                fire.BuildProduct = "campfire.spot";
                fire.RotationDegrees = site.RotationDegrees; // §66: the site's facing is the piece's
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

        // Spec §54: a campfire is piled from stones, and the leaf mat and the
        // drying rack (§35.5B) are hand-lashed. Rigid furniture still needs
        // the builder's hammer.
        var needsHammer = site.BuildProduct is not ("campfire.spot" or "bed.leaf" or "station.drying_rack");
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
                site.BuildProduct = string.Empty;
                Trace.Emit(world, npc.Id, "FurnitureBuilt",
                    $"{site.DefinitionId} upgrades finished in place at Tile={site.Tile.Q},{site.Tile.R}");
                return;
            }

            var junction = site.Junctions.Count > 0 ? site.Junctions[0] : npc.CurrentJunction;
            var tile = site.Tile;
            var product = site.BuildProduct;
            // §64: a personal bed's ownership rides from the site onto the
            // finished piece — this is what makes the raised bed hers.
            var owner = site.Owner;
            // §66: so does the yaw the site was staked at — the bed must come up
            // lying side-on to the fire, not on whatever default the prefab has.
            var yaw = site.RotationDegrees;
            WorldObjectMutations.DespawnObject(world, site.Id);
            if (junction is { } j)
            {
                var raised = WorldObjectMutations.SpawnObject(world, product, npc.Fragment, tile, j);
                raised.Owner = owner;
                raised.RotationDegrees = yaw;
            }

            Trace.Emit(world, npc.Id, "FurnitureBuilt",
                $"{product} raised at Tile={tile.Q},{tile.R}" +
                (owner is { } ow ? $" for colonist {ow.Value}" : string.Empty));
        }
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

        for (var i = 0; i < bill.Logs; i++) npc.Inventory.Items.Remove("resource.log");
        for (var i = 0; i < bill.Stones; i++) npc.Inventory.Items.Remove("resource.stone");
        for (var i = 0; i < bill.Leaves; i++) npc.Inventory.Items.Remove("resource.palm_leaf");

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
                Trace.EmitSystem(world, "DoorPlacementFailed",
                    $"No free mid-edge junction on edge {bill.Edge} — hut may be sealed");
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
                hutTile.Flags |= TileFlags.Indoor;
                if (hutTile.Junctions.Count > 1)
                {
                    WorldObjectMutations.SpawnObject(world, "bed.basic",
                        npc.Fragment, project.Tile, hutTile.Junctions[1]);
                }
            }

            foreach (var obj in world.Entities.Objects.Values)
            {
                if (obj.DefinitionId == "construction.site")
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
