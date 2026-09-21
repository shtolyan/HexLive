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
    // catalog entry — preserving the old switch's `_ => false` default.
    private static bool CraftGateOk(WorldState world, NPCState npc, WorldObjectState worldObject, GoalType goal)
    {
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            return false;
        }

        if (Content.RecipeCatalog.UsesPersistentProject(goal))
        {
            return CraftProjectMath.CanBeginCycle(world, npc, goal, worldObject);
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

    private static bool CraftNeedsToolsOrWeapons(GoalType goal) =>
        AI.GoalCatalog.CraftNeedsHands(goal);

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
            if (item.DefinitionId != ContentIds.MeatCooked)
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
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "MeatTakenFromSpit",
                    $"food.meat_cooked off the spit at Tile={fire.Tile.Q},{fire.Tile.R} " +
                    $"left hanging={BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked)} " +
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
            }
            return;
        }

        fire.IsOccupied = false;
        fire.CurrentUser = null;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "SpitTakeFailed",
                $"no takeable cooked meat on the spit at Tile={fire.Tile.Q},{fire.Tile.R}");
        }
    }

    // §gear-craft: the item-output arm of the craft completion — shared by the
    // at-station path and the in-place path. Returns false for placed-object
    // crafts (rack/bed/tent), which the station switch handles.
    private static bool GrantCraftOutput(WorldState world, NPCState npc, GoalType goal)
    {
        switch (goal)
        {
            case GoalType.CraftBandage:
                GiveOrDrop(world, npc, MedicalSupplyMath.CreateBandage(herbal: true));
                Trace.Emit(world, npc.Id, "BandageCrafted",
                    $"Inventory={MedicalSupplyMath.BandageCount(npc)} " +
                    $"Herbal={MedicalSupplyMath.HerbalBandageCount(npc)}");
                return true;
            case GoalType.CraftSpear:
                GiveOrDrop(world, npc, ContentIds.Spear);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedSpear",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            // §54.14 (r2): CookMeat no longer grants here — the raw chunk is
            // hung on the spit at the station-craft arm and FireSystem roasts
            // it over time (a campfire-station recipe never crafts in place).
            case GoalType.CraftLeather:
                ResolveWearConflicts(world, npc, ContentIds.LeatherPants);
                npc.WornItems.Add(ContentIds.LeatherPants);
                EquipmentMath.Recalculate(world, npc);
                StowDisplacedGarments(world, npc); // §52.9 r2: displaced pants go to the pack, overflow to ground
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedLeather",
                        $"Pants worn. Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                }
                return true;
            case GoalType.CraftAxe:
                GiveOrDrop(world, npc, ContentIds.AxeStone);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedAxe",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            case GoalType.CraftPickaxe:
                GiveOrDrop(world, npc, ContentIds.PickaxeStone);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedPickaxe",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            case GoalType.CraftBow:
                GiveOrDrop(world, npc, ContentIds.Bow);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedBow",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            case GoalType.CraftArrows:
                GiveOrDrop(world, npc, ContentIds.Arrow);
                GiveOrDrop(world, npc, ContentIds.Arrow);
                GiveOrDrop(world, npc, ContentIds.Arrow);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedArrows",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            case GoalType.CraftRope:
                GiveOrDrop(world, npc, ContentIds.Rope);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedRope",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            case GoalType.CraftCloth:
                GiveOrDrop(world, npc, ContentIds.Cloth);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedCloth",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            case GoalType.CraftKnife:
                GiveOrDrop(world, npc, ContentIds.Knife);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CraftedKnife",
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                }
                return true;
            case GoalType.CraftSplint:
                GiveOrDrop(world, npc, ContentIds.Splint);
                return true;
            case GoalType.CraftWoodenArm:
                GiveOrDrop(world, npc, ContentIds.WoodenArm);
                return true;
            case GoalType.CraftWoodenLeg:
                GiveOrDrop(world, npc, ContentIds.WoodenLeg);
                return true;
            default:
                return false;
        }
    }

    // §gear-craft v2: the in-place craft is a STAGED ritual, not a bare timer.
    //   1. Layout — the recipe inputs become ordinary world items on the
    //      ground at her feet (everyone sees the work spread out). §84: pieces
    //      ALREADY lying within the craft ring are claimed as-is — only the
    //      shortfall leaves the pack, so a fiber cut off a yucca is never
    //      pocketed just to be laid back out.
    //   2. Work — the Craft beat, twice the old workbench window (12 -> 24
    //      ticks = 6 s); the view kneels her into the craft-work clip.
    //   3. Take — the ingredients are used up, the finished item appears ON
    //      THE GROUND, and a short PickUp beat stoops her down to take it
    //      into hand/pack.
    // An interrupted craft leaves the laid-out pieces lying — they are normal
    // world objects, recoverable by the usual gather logic (no dupes: the
    // inputs left the inventory at layout time; §84 marks the layout occupied
    // while the work runs and PlanInterruption releases it on abort).
    private const int CraftInPlaceDurationTicks = 24;

    private const int CraftTakeDurationTicks = 6;

    private static int CraftWorkBaseTicks(GoalType goal) =>
        Spec118.Enabled && goal == GoalType.CraftSplint
            ? Spec118.SplintCraftTicks
            : CraftInPlaceDurationTicks;

    // §gear-craft v2: which inventory ITEMS the craft lays on the ground for
    // the take beat. Non-item outputs (the bandage counter, leather worn
    // straight onto the body) return null and grant instantly at work's end.
    // §gear-craft v2: что крафт выкладывает на землю на такте «взять».
    // Таблица в GoalCatalog: раньше это был ТРЕТИЙ параллельный switch по цели
    // в одном файле, и все три надо было держать согласованными вручную.
    private static string[] CraftGroundOutputs(GoalType goal) =>
        AI.GoalCatalog.CraftGroundOutputs(goal);

    private static ItemInstance CreateCraftYieldItem(WorldState world, string outputId) =>
        outputId == ContentIds.Bandage
            ? MedicalSupplyMath.CreateBandage(herbal: true)
            : CreateYieldItem(world, outputId);

    // The legacy per-goal trace names, kept stable for soak metrics.
    private static string CraftedTraceName(GoalType goal) =>
        AI.GoalCatalog.CraftTraceName(goal);

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

    // §84: loose pieces of this definition lying within the craft ring — her
    // hex + ring-1, the yucca scatter radius — free to take: not in use by
    // anyone, not a build-site, no pocket contents riding inside. Sorted
    // nearest-first (id-tiebroken, deterministic) so the claim empties the
    // spots at her feet before the ring.
    private static System.Collections.Generic.List<WorldObjectState> GroundInputsNearby(
        WorldState world, NPCState npc, string definitionId)
    {
        var found = new System.Collections.Generic.List<WorldObjectState>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId != definitionId ||
                !obj.Fragment.Equals(npc.Fragment) ||
                obj.IsOccupied ||
                obj.Contents.Count > 0 ||
                !string.IsNullOrEmpty(obj.BuildProduct) ||
                HexSpatialMath.HexDistance(obj.Tile, npc.Tile) > 1)
            {
                continue;
            }

            found.Add(obj);
        }

        found.Sort((a, b) =>
        {
            var byDistance = HexSpatialMath.HexDistance(a.Tile, npc.Tile)
                .CompareTo(HexSpatialMath.HexDistance(b.Tile, npc.Tile));
            return byDistance != 0 ? byDistance : a.Id.Value.CompareTo(b.Id.Value);
        });
        return found;
    }

    private static void RunCraftInPlace(WorldState world, NPCState npc)
    {
        var goal = npc.Plan.Goal != GoalType.None ? npc.Plan.Goal : npc.Mind.CurrentGoal;
        if (!RecipeCatalog.UsesPersistentProject(goal) ||
            !RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe) ||
            !string.IsNullOrEmpty(recipe.Station))
        {
            RunLegacyCraftInPlace(world, npc);
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Plan.TargetJunctionId is { } walkTarget)
        {
            if (npc.Movement.IsMoving) return;
            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanningSystem.SetGoalCooldown(world, npc, goal);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                    $"CraftInPlace {goal}: project point unreachable");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }
            if (npc.CurrentJunction is not { } at || !at.Equals(walkTarget)) return;
        }

        // Bug #86: finishing a persistent project used to end the plan at
        // 100%, dropping ownership of the result. Keep the same worker in an
        // explicit take beat, mirroring the legacy ground-craft lifecycle.
        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.PickUp &&
            npc.Execution.CraftLayout.Count > 0)
        {
            if (world.Tick < npc.Execution.EndTick) return;

            foreach (var craftedId in npc.Execution.CraftLayout)
            {
                if (!world.Entities.Objects.TryGetValue(craftedId, out var crafted)) continue;
                TakeCompletedProjectOutput(world, npc, goal, crafted);
            }

            npc.Execution.CraftLayout.Clear();
            Trace.Emit(world, npc.Id, CraftedTraceName(goal),
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
            FinishCraftInPlace(world, npc, goal);
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (!CraftProjectMath.TryBeginCycle(world, npc, goal, null, out var project))
            {
                PlanningSystem.SetGoalCooldown(world, npc, goal);
                npc.Plan.Status = PlanStatus.Failed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecFailed",
                        $"CraftInPlace {goal}: no resumable project or complete bill");
                }
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Craft;
            npc.Execution.TargetObject = project.Id;
            npc.Execution.StartTick = world.Tick;
            var ticks = AttributeMath.WorkTicks(
                npc, Spec119.CraftCycleWork, InteractionType.Craft, goal);
            npc.Execution.EndTick = world.Tick + ticks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"CraftInPlace {goal} Project={project.Id.Value} Duration={ticks}ticks");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress) return;
        CraftProjectMath.UpdateCycleProgress(world, npc);
        if (world.Tick < npc.Execution.EndTick) return;

        var completedProjectId = npc.Execution.CraftProjectId;
        CraftProjectMath.CompleteCycle(world, npc, goal);
        SkillTrace.Award(world, npc, InteractionType.Craft,
            npc.Execution.EndTick - npc.Execution.StartTick);

        if (completedProjectId is { } resultId &&
            world.Entities.Objects.TryGetValue(resultId, out var result))
        {
            if (result.IsCraftProject && npc.Mind.ManualControl)
            {
                ResetCraftCycleExecution(npc);
                return;
            }

            if (result.IsCraftProject)
            {
                FinishCraftInPlace(world, npc, goal);
                return;
            }

            if (TryContinueCraftedBandageAsSelfTreatment(world, npc, goal, result))
            {
                return;
            }

            npc.Execution.CraftLayout.Clear();
            npc.Execution.CraftLayout.Add(resultId);
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.PickUp;
            npc.Execution.TargetObject = resultId;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + CraftTakeDurationTicks;
            FaceCraftLayout(world, npc);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CraftOutputReady",
                    $"{goal} Project={resultId.Value}; take in {CraftTakeDurationTicks}ticks");
            }
            return;
        }

        FinishCraftInPlace(world, npc, goal);
    }

    /// <summary>Final physical take shared by ground and station persistent
    /// crafts. False means the exact ready object stays where it was because
    /// the pack is genuinely full.</summary>
    private static bool TakeCompletedProjectOutput(
        WorldState world, NPCState npc, GoalType goal, WorldObjectState crafted)
    {
        if (goal == GoalType.CraftLeather &&
            crafted.DefinitionId == ContentIds.LeatherPants)
        {
            // Leather's one authored output adapter: it is worn immediately.
            ResolveWearConflicts(world, npc, ContentIds.LeatherPants);
            npc.WornItems.Add(new ItemInstance(ContentIds.LeatherPants)
            {
                Wetness = crafted.Wetness,
                Durability = crafted.Durability,
                ResourceAmount = crafted.ResourceAmount,
                WaterKind = crafted.WaterKind,
                LastAddedWaterKind = crafted.LastAddedWaterKind,
                Dirtiness = crafted.Dirtiness,
                Bloodiness = crafted.Bloodiness
            });
            WorldObjectMutations.DespawnObject(world, crafted.Id);
            EquipmentMath.Recalculate(world, npc);
            StowDisplacedGarments(world, npc);
            return true;
        }

        if (!InventoryMath.FitsWithoutEviction(world, npc, crafted.DefinitionId))
        {
            crafted.IsOccupied = false;
            crafted.CurrentUser = null;
            return false;
        }

        npc.Inventory.Items.Add(new ItemInstance(crafted.DefinitionId)
        {
            Wetness = crafted.Wetness,
            Durability = crafted.Durability,
            ResourceAmount = crafted.ResourceAmount,
            WaterKind = crafted.WaterKind,
            LastAddedWaterKind = crafted.LastAddedWaterKind,
            Dirtiness = crafted.Dirtiness,
            Bloodiness = crafted.Bloodiness
        });
        WorldObjectMutations.DespawnObject(world, crafted.Id);
        return true;
    }

    // §68 r2: once the physical bandage reaches 100%, a wounded crafter can
    // apply that exact object without a PickUp round-trip. This is keyed to the
    // treatment predicate, not current free slots: paying two herb leaves may
    // itself open a slot even though the pack was full when the chain began.
    private static bool TryContinueCraftedBandageAsSelfTreatment(
        WorldState world, NPCState npc, GoalType goal, WorldObjectState result)
    {
        if (goal != GoalType.CraftBandage ||
            result.DefinitionId != ContentIds.Bandage ||
            !DecisionSystem.SelfTreatmentIndicated(
                npc, MedicalSupplyMath.BandageCount(npc) + 1))
        {
            return false;
        }

        npc.Execution.CraftLayout.Clear();
        npc.Plan.Goal = GoalType.TreatWounds;
        npc.Mind.CurrentGoal = GoalType.TreatWounds;
        npc.Plan.TargetObjectId = result.Id;
        npc.Plan.TargetItemDefinitionId = ContentIds.Bandage;
        npc.Plan.TargetTile = result.Tile;
        npc.Plan.TargetJunctionId = null; // output lies in the crafter's ring
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.TreatSelf,
            TargetObject = result.Id,
            Interaction = InteractionType.TreatSelf
        });
        npc.Plan.CurrentStepIndex = 0;
        ResetCraftCycleExecution(npc);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CraftBandageDirectTreatment",
                $"Project={result.Id.Value} InventoryBandages={MedicalSupplyMath.BandageCount(npc)}");
        }

        // Claim immediately in the same execution turn: no other planner can
        // steal the output in the one-tick seam between Craft and TreatSelf.
        RunTreatSelf(world, npc);
        return true;
    }

    private static void ResetCraftCycleExecution(NPCState npc)
    {
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.CurrentStepIndex = 0;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
    }

    private static void BeginCraftOutputTake(WorldState world, NPCState npc)
    {
        if (npc.Execution.CraftLayout.Count == 0)
        {
            return;
        }

        var resultId = npc.Execution.CraftLayout[0];
        if (world.Entities.Objects.TryGetValue(resultId, out var result) &&
            TryContinueCraftedBandageAsSelfTreatment(
                world, npc, npc.Plan.Goal, result))
        {
            return;
        }

        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.CraftInPlace });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetObjectId = resultId;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.PickUp;
        npc.Execution.TargetObject = resultId;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + CraftTakeDurationTicks;
        FaceCraftLayout(world, npc);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CraftOutputReady",
                $"{npc.Plan.Goal} Project={resultId.Value}; take in {CraftTakeDurationTicks}ticks");
        }
    }

    private static void RunLegacyCraftInPlace(WorldState world, NPCState npc)
    {
        var goal = npc.Plan.Goal != GoalType.None ? npc.Plan.Goal : npc.Mind.CurrentGoal;
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            npc.Plan.Status = PlanStatus.Failed;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed", $"CraftInPlace: no recipe for {goal}");

            }
            return;
        }

        // §84: the plan may carry a walk — the craft happens AT the fiber
        // pile, not wherever the goal fired. Mirrors the RunGroundRestPlan
        // gate: wait out the walk, fail on Blocked, start only once she stands
        // on the plan's junction.
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Plan.TargetJunctionId is { } walkTarget)
        {
            if (npc.Movement.IsMoving)
            {
                return;
            }

            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanningSystem.SetGoalCooldown(world, npc, goal);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                    $"CraftInPlace {goal}: ground pile unreachable (path blocked)");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            if (npc.CurrentJunction is not { } atJunction || !atJunction.Equals(walkTarget))
            {
                return; // PathfindingSystem routes her next tick
            }
        }

        // Beat 1 — layout: pieces already lying within the craft ring are
        // claimed on the ground as-is (§84); only the shortfall leaves the
        // pack and lands at her feet.
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            // Validate the WHOLE bill first — nothing is claimed or paid until
            // every ingredient is covered by ground + pack together.
            var groundByIngredient =
                new System.Collections.Generic.List<System.Collections.Generic.List<WorldObjectState>>();
            for (var ingIndex = 0; ingIndex < recipe.Inputs.Count; ingIndex++)
            {
                var ing = recipe.Inputs[ingIndex];
                var ground = GroundInputsNearby(world, npc, ing.Id);
                groundByIngredient.Add(ground);
                if (ground.Count + DecisionSystem.CountInventory(npc, ing.Id) < ing.Count)
                {
                    // Cooldown the goal — without it a decision layer that
                    // still believes the craft is possible re-selects it every
                    // tick and the NPC stands in an ExecFailed loop (the
                    // bled-out-at-CraftBandage death class, Jul 2026).
                    PlanningSystem.SetGoalCooldown(world, npc, goal);
                    npc.Plan.Status = PlanStatus.Failed;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "ExecFailed",
                            $"CraftInPlace {goal}: missing {ing.Id} x{ing.Count}");
                    }
                    return;
                }
            }

            npc.Execution.CraftLayout.Clear();
            var fromGround = 0;
            for (var ingIndex = 0; ingIndex < recipe.Inputs.Count; ingIndex++)
            {
                var ing = recipe.Inputs[ingIndex];
                var ground = groundByIngredient[ingIndex];
                var claimed = 0;
                for (; claimed < ing.Count && claimed < ground.Count; claimed++)
                {
                    // §84: the lying piece IS the layout — claim it in place.
                    // Occupied marks it as HER work in progress so housemates
                    // neither gather it nor fold it into their own craft
                    // (double-claiming the same fiber duped a rope).
                    var piece = ground[claimed];
                    piece.IsOccupied = true;
                    piece.CurrentUser = npc.Id;
                    npc.Execution.CraftLayout.Add(piece.Id);
                }

                fromGround += claimed;
                for (var i = claimed; i < ing.Count; i++)
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
                        // §84: occupied for the same anti-steal reason as the
                        // ground-claimed pieces above.
                        laid.IsOccupied = true;
                        laid.CurrentUser = npc.Id;
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
            // §76: the §61 work beat scales with Wits and the Crafting trade —
            // the one duration outside the world-object path that a colonist can
            // actually get better at.
            var craftTicks = AttributeMath.WorkTicks(
                npc, CraftWorkBaseTicks(goal), InteractionType.Craft, goal);
            npc.Execution.EndTick = world.Tick + craftTicks;
            FaceCraftLayout(world, npc); // §61: kneel TOWARD the laid-out pieces
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"CraftInPlace {goal} Duration={craftTicks}ticks " +
                    $"LaidOut={npc.Execution.CraftLayout.Count} FromGround={fromGround}");
            }
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
                var crafted = DropItemAtFeet(world, npc, CreateCraftYieldItem(world, outputId));
                if (crafted != null)
                {
                    npc.Execution.CraftLayout.Add(crafted.Id);
                }
                else
                {
                    // Nowhere to lay it — straight into the pack.
                    GiveOrDrop(world, npc, CreateCraftYieldItem(world, outputId));
                }
            }

            npc.Execution.CurrentInteraction = InteractionType.PickUp;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + CraftTakeDurationTicks;
            FaceCraftLayout(world, npc); // §61: stoop TOWARD the finished item
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CraftOutputLaid",
                    $"{goal} -> [{string.Join(",", outputs)}] on the ground; " +
                    $"take in {CraftTakeDurationTicks}ticks");
            }
            return;
        }

        // Beat 3 done — she stoops and takes the finished item into the pack.
        foreach (var craftedId in npc.Execution.CraftLayout)
        {
            if (!world.Entities.Objects.TryGetValue(craftedId, out var crafted))
            {
                continue; // somebody took it first — the craft still ends
            }

            TakeCompletedProjectOutput(world, npc, goal, crafted);
        }

        npc.Execution.CraftLayout.Clear();
        Trace.Emit(world, npc.Id, CraftedTraceName(goal),
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
        // §76: the §61 in-place craft is its own execution path and never
        // reaches the world-object completion hook, so it pays its own XP.
        // Credited on the WORK beat's length, not the stoop-and-take beat —
        // picking the thing up is not what taught her anything — and on the
        // length she ACTUALLY worked, matching the world-object path: getting
        // faster at a trade has to slow how fast you keep getting faster, or
        // the two paths reward practice differently for no reason.
        SkillTrace.Award(world, npc, InteractionType.Craft,
            AttributeMath.WorkTicks(npc, CraftWorkBaseTicks(goal), InteractionType.Craft, goal));
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
        if (npc.Mind.ManualControl)
        {
            npc.Mind.LastManualInputTick = world.Tick;
        }
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CycleReset",
                $"Goal->None Plan->Completed (crafted {goal} in place)");
        }
    }
}

}
