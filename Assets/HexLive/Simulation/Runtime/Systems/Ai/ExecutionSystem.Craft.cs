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
    // Spec §54 (R2): recipe ingredient/gate check, sourced from RecipeCatalog so
    // the ingredient bill lives in one place (the §54 firewood→stick rewire
    // edits the catalog, not this switch). Returns false for any goal with no
    // catalog entry — preserving the old switch's `_ => false` default (e.g.
    // CraftBandage, deliberately not routed through the craft-start gate today).
    private static bool CraftGateOk(WorldState world, NPCState npc, WorldObjectState worldObject, GoalType goal)
    {
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            return false;
        }

        foreach (var ing in recipe.Inputs)
        {
            if (DecisionSystem.CountInventory(npc, ing.Id) < ing.Count)
            {
                return false;
            }
        }

        if (recipe.NeedsLitFire && worldObject.ResourceAmount <= 0f)
        {
            return false;
        }

        if (recipe.RequiresNoRack && DecisionSystem.RackExists(world))
        {
            return false;
        }

        return true;
    }

    private static bool CraftNeedsToolsOrWeapons(GoalType goal)
    {
        switch (goal)
        {
            case GoalType.CraftSpear:
            case GoalType.CraftAxe:
            case GoalType.CraftPickaxe:
            case GoalType.CraftRack:
            case GoalType.CraftTent:
            case GoalType.CraftBow:
            case GoalType.CraftArrows:
            case GoalType.CraftRope:
            case GoalType.CraftCloth:
            case GoalType.CraftKnife:
                return true;
            default:
                return false;
        }
    }

    // Spec §54 (R2): consume a recipe's inputs from the pack. Mirrors the exact
    // Remove-per-ingredient the effect switch used to do inline.
    private static void ConsumeRecipeInputs(NPCState npc, GoalType goal)
    {
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            return;
        }

        foreach (var ing in recipe.Inputs)
        {
            for (var i = 0; i < ing.Count; i++)
            {
                npc.Inventory.Items.Remove(ing.Id);
            }
        }
    }

    // §54.14 (r2): take ONE cooked chunk off the spit into the pack — the fire
    // (and any still-roasting raw meat) stays. Housemates share the crossbar,
    // so a single grab per trip keeps the spit a communal larder.
    private static void TakeMeatFromSpit(WorldState world, NPCState npc, WorldObjectState fire)
    {
        for (var i = 0; i < fire.Contents.Count; i++)
        {
            var item = fire.Contents[i];
            if (item.DefinitionId != "food.meat_cooked")
            {
                continue;
            }

            if (!InventoryMath.MakeRoomFor(world, npc, item.DefinitionId))
            {
                break; // no room — leave it hanging
            }

            fire.Contents.RemoveAt(i);
            npc.Inventory.Items.Add(item);
            fire.IsOccupied = false;
            fire.CurrentUser = null;
            Trace.Emit(world, npc.Id, "MeatTakenFromSpit",
                $"food.meat_cooked off the spit at Tile={fire.Tile.Q},{fire.Tile.R} " +
                $"left hanging={BuildSiteMath.HangingMeat(fire, "food.meat_cooked")} " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
            return;
        }

        fire.IsOccupied = false;
        fire.CurrentUser = null;
        Trace.Emit(world, npc.Id, "SpitTakeFailed",
            $"no takeable cooked meat on the spit at Tile={fire.Tile.Q},{fire.Tile.R}");
    }

    // §gear-craft: the item-output arm of the craft completion — shared by the
    // at-station path and the in-place path. Returns false for placed-object
    // crafts (rack/bed/tent), which the station switch handles.
    private static bool GrantCraftOutput(WorldState world, NPCState npc, GoalType goal)
    {
        switch (goal)
        {
            case GoalType.CraftBandage:
                npc.Needs.Bandages++;
                npc.Needs.HerbalBandages++; // spec 44: gathered plantain -> leaf-wrap decal
                Trace.Emit(world, npc.Id, "BandageCrafted",
                    $"Bandages={npc.Needs.Bandages} Herbal={npc.Needs.HerbalBandages}");
                return true;
            case GoalType.CraftSpear:
                GiveOrDrop(world, npc, "tool.spear");
                Trace.Emit(world, npc.Id, "CraftedSpear",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            // §54.14 (r2): CookMeat no longer grants here — the raw chunk is
            // hung on the spit at the station-craft arm and FireSystem roasts
            // it over time (a campfire-station recipe never crafts in place).
            case GoalType.CraftLeather:
                ResolveWearConflicts(world, npc, "clothing.leather_pants");
                npc.WornItems.Add("clothing.leather_pants");
                EquipmentMath.Recalculate(world, npc);
                Trace.Emit(world, npc.Id, "CraftedLeather",
                    $"Pants worn. Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                return true;
            case GoalType.CraftAxe:
                GiveOrDrop(world, npc, "tool.axe_stone");
                Trace.Emit(world, npc.Id, "CraftedAxe",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftPickaxe:
                GiveOrDrop(world, npc, "tool.pickaxe_stone");
                Trace.Emit(world, npc.Id, "CraftedPickaxe",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftBow:
                GiveOrDrop(world, npc, "tool.bow");
                Trace.Emit(world, npc.Id, "CraftedBow",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftArrows:
                GiveOrDrop(world, npc, "resource.arrow");
                GiveOrDrop(world, npc, "resource.arrow");
                GiveOrDrop(world, npc, "resource.arrow");
                Trace.Emit(world, npc.Id, "CraftedArrows",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftRope:
                GiveOrDrop(world, npc, "resource.rope");
                Trace.Emit(world, npc.Id, "CraftedRope",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftCloth:
                GiveOrDrop(world, npc, "resource.cloth");
                Trace.Emit(world, npc.Id, "CraftedCloth",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftKnife:
                GiveOrDrop(world, npc, "tool.knife");
                Trace.Emit(world, npc.Id, "CraftedKnife",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            default:
                return false;
        }
    }

    // §gear-craft v2: the in-place craft is a STAGED ritual, not a bare timer.
    //   1. Layout — the recipe inputs leave the pack and are laid out on the
    //      ground at her feet as ordinary world items (everyone sees the work
    //      spread out).
    //   2. Work — the Craft beat, twice the old workbench window (12 -> 24
    //      ticks = 6 s); the view kneels her into the craft-work clip.
    //   3. Take — the ingredients are used up, the finished item appears ON
    //      THE GROUND, and a short PickUp beat stoops her down to take it
    //      into hand/pack.
    // An interrupted craft leaves the laid-out pieces lying — they are normal
    // world objects, recoverable by the usual gather logic (no dupes: the
    // inputs left the inventory at layout time).
    private const int CraftInPlaceDurationTicks = 24;

    private const int CraftTakeDurationTicks = 6;

    // §gear-craft v2: which inventory ITEMS the craft lays on the ground for
    // the take beat. Non-item outputs (the bandage counter, leather worn
    // straight onto the body) return null and grant instantly at work's end.
    private static string[] CraftGroundOutputs(GoalType goal) => goal switch
    {
        GoalType.CraftSpear => new[] { "tool.spear" },
        GoalType.CraftAxe => new[] { "tool.axe_stone" },
        GoalType.CraftPickaxe => new[] { "tool.pickaxe_stone" },
        GoalType.CraftKnife => new[] { "tool.knife" },
        GoalType.CraftBow => new[] { "tool.bow" },
        GoalType.CraftArrows => new[] { "resource.arrow", "resource.arrow", "resource.arrow" },
        GoalType.CraftRope => new[] { "resource.rope" },
        GoalType.CraftCloth => new[] { "resource.cloth" },
        _ => null
    };

    // The legacy per-goal trace names, kept stable for soak metrics.
    private static string CraftedTraceName(GoalType goal) => goal switch
    {
        GoalType.CraftSpear => "CraftedSpear",
        GoalType.CraftAxe => "CraftedAxe",
        GoalType.CraftPickaxe => "CraftedPickaxe",
        GoalType.CraftKnife => "CraftedKnife",
        GoalType.CraftBow => "CraftedBow",
        GoalType.CraftArrows => "CraftedArrows",
        GoalType.CraftRope => "CraftedRope",
        GoalType.CraftCloth => "CraftedCloth",
        _ => "CraftedItem"
    };

    // §61: she kneels FACING the work — turn toward the centroid of the
    // laid-out pieces (beat 2) / the finished item (beat 3). Snap in the sim
    // (like PlaceAtEdge); the view smooths the turn visually.
    private static void FaceCraftLayout(WorldState world, NPCState npc)
    {
        var sumX = 0f;
        var sumY = 0f;
        var count = 0;
        foreach (var laidId in npc.Execution.CraftLayout)
        {
            if (!world.Entities.Objects.TryGetValue(laidId, out var laid) ||
                laid.Junctions.Count == 0 ||
                !world.Junctions.Items.TryGetValue(laid.Junctions[0], out var junction))
            {
                continue;
            }

            sumX += junction.WorldPosition.X;
            sumY += junction.WorldPosition.Y;
            count++;
        }

        if (count == 0)
        {
            return;
        }

        var delta = new Float2(sumX / count - npc.Position.X, sumY / count - npc.Position.Y);
        if (HexSpatialMath.Distance(Float2.Zero, delta) < 0.01f)
        {
            return; // the pile is underfoot — keep the current heading
        }

        var facing = HexSpatialMath.Normalize(delta);
        npc.Movement.DesiredDirection = facing;
        npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(facing);
        npc.RotationDegrees = npc.Movement.DesiredRotationDegrees;
    }

    private static void RunCraftInPlace(WorldState world, NPCState npc)
    {
        var goal = npc.Plan.Goal != GoalType.None ? npc.Plan.Goal : npc.Mind.CurrentGoal;
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", $"CraftInPlace: no recipe for {goal}");
            return;
        }

        // Beat 1 — layout: the inputs leave the pack and land on the ground.
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            foreach (var ing in recipe.Inputs)
            {
                if (DecisionSystem.CountInventory(npc, ing.Id) < ing.Count)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "ExecFailed",
                        $"CraftInPlace {goal}: missing {ing.Id} x{ing.Count}");
                    return;
                }
            }

            npc.Execution.CraftLayout.Clear();
            foreach (var ing in recipe.Inputs)
            {
                for (var i = 0; i < ing.Count; i++)
                {
                    var index = npc.Inventory.Items.IndexOf(ing.Id);
                    if (index < 0)
                    {
                        continue;
                    }

                    var input = npc.Inventory.Items[index];
                    npc.Inventory.Items.RemoveAt(index);
                    var laid = DropItemAtFeet(world, npc, input);
                    if (laid != null)
                    {
                        // Tracked: despawned (used up) when the work beat ends.
                        npc.Execution.CraftLayout.Add(laid.Id);
                    }
                    // No free spot: the piece stays in her lap — already paid,
                    // just never visible on the ground.
                }
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Craft;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + CraftInPlaceDurationTicks;
            FaceCraftLayout(world, npc); // §61: kneel TOWARD the laid-out pieces
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"CraftInPlace {goal} Duration={CraftInPlaceDurationTicks}ticks " +
                $"LaidOut={npc.Execution.CraftLayout.Count}");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress ||
            npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Beat 2 done — the work window just ended: the laid-out ingredients
        // are used up and the finished item lands on the ground beside her.
        if (npc.Execution.CurrentInteraction == InteractionType.Craft)
        {
            foreach (var laidId in npc.Execution.CraftLayout)
            {
                WorldObjectMutations.DespawnObject(world, laidId);
            }

            npc.Execution.CraftLayout.Clear();

            var outputs = CraftGroundOutputs(goal);
            if (outputs == null)
            {
                // Non-item output: grant instantly, no take beat.
                GrantCraftOutput(world, npc, goal);
                FinishCraftInPlace(world, npc, goal);
                return;
            }

            foreach (var outputId in outputs)
            {
                var crafted = DropItemAtFeet(world, npc, CreateYieldItem(world, outputId));
                if (crafted != null)
                {
                    npc.Execution.CraftLayout.Add(crafted.Id);
                }
                else
                {
                    // Nowhere to lay it — straight into the pack.
                    GiveOrDrop(world, npc, CreateYieldItem(world, outputId));
                }
            }

            npc.Execution.CurrentInteraction = InteractionType.PickUp;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + CraftTakeDurationTicks;
            FaceCraftLayout(world, npc); // §61: stoop TOWARD the finished item
            Trace.Emit(world, npc.Id, "CraftOutputLaid",
                $"{goal} -> [{string.Join(",", outputs)}] on the ground; " +
                $"take in {CraftTakeDurationTicks}ticks");
            return;
        }

        // Beat 3 done — she stoops and takes the finished item into the pack.
        foreach (var craftedId in npc.Execution.CraftLayout)
        {
            if (!world.Entities.Objects.TryGetValue(craftedId, out var crafted))
            {
                continue; // somebody took it first — the craft still ends
            }

            GiveOrDrop(world, npc, new ItemInstance(crafted.DefinitionId)
            {
                Wetness = crafted.Wetness,
                Durability = crafted.Durability,
                ResourceAmount = crafted.ResourceAmount,
                Dirtiness = crafted.Dirtiness,
                Bloodiness = crafted.Bloodiness
            });
            WorldObjectMutations.DespawnObject(world, craftedId);
        }

        npc.Execution.CraftLayout.Clear();
        Trace.Emit(world, npc.Id, CraftedTraceName(goal),
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
        FinishCraftInPlace(world, npc, goal);
    }

    private static void FinishCraftInPlace(WorldState world, NPCState npc, GoalType goal)
    {
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        Trace.Emit(world, npc.Id, "CycleReset",
            $"Goal->None Plan->Completed (crafted {goal} in place)");
    }
}

}
