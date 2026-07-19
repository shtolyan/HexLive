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

public sealed class ExecutionSystem : ISimulationSystem
{
    public string Name => nameof(ExecutionSystem);

    public TickLayer Layer => TickLayer.Fast;

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

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status != PlanStatus.Active)
            {
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.GetFood &&
                (npc.Inventory.FindFirstFood(world.Content) is not null ||
                 DecisionSystem.HasInventoryCoconutMeal(npc)))
            {
                PlanInterruption.Abort(world, npc, "Food already available in inventory");
                npc.Mind.CurrentGoal = GoalType.None;
                Trace.Emit(world, npc.Id, "PlanAborted",
                    "GetFood stopped: inventory food is available");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.GetWater &&
                DecisionSystem.HasInventoryCoconutWater(npc))
            {
                PlanInterruption.Abort(world, npc, "Water already available in inventory");
                npc.Mind.CurrentGoal = GoalType.None;
                Trace.Emit(world, npc.Id, "PlanAborted",
                    "GetWater stopped: inventory water is available");
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.ConsumeInventoryItem)
            {
                RunConsumeInventoryItem(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.DropInventoryItem)
            {
                RunDropInventoryItem(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.UndressItem)
            {
                RunUndressItem(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.DrinkBottle)
            {
                RunDrinkBottle(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.CraftInPlace)
            {
                RunCraftInPlace(world, npc);
                continue;
            }

            // Spec 29G: ground rest plans have no target object — the last
            // step says what to do once the walk (if any) is over.
            var lastStep = npc.Plan.Steps.Count > 0 ? npc.Plan.Steps[npc.Plan.Steps.Count - 1] : null;
            if (lastStep is { Type: PlanStepType.PrepareBathe })
            {
                RunPrepareBathe(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.SwimBathe })
            {
                RunSwimBathe(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.WashClothes })
            {
                RunWashClothes(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.GroundSit or PlanStepType.GroundSleep or PlanStepType.GroundCool })
            {
                RunGroundRestPlan(world, npc, lastStep);
                continue;
            }

            if (npc.Plan.TargetAgentId is not null)
            {
                // Spec §53: aid plans also carry a TargetAgentId — route them to
                // the aid handler; everything else agent-targeted is a talk.
                if (npc.Mind.CurrentGoal == GoalType.Aid)
                {
                    RunAid(world, npc);
                }
                else
                {
                    RunTalk(world, npc);
                }
                continue;
            }

            if (npc.Plan.TargetObjectId is null)
            {
                if (npc.Plan.Steps.Count > 0 &&
                    npc.Plan.Steps[npc.Plan.Steps.Count - 1].Type == PlanStepType.MoveToJunction)
                {
                    RunMoveOnly(world, npc);
                }

                continue;
            }

            if (!world.Entities.Objects.TryGetValue(npc.Plan.TargetObjectId.Value, out var worldObject))
            {
                // The target may be a remembered object we have not reached yet
                // (spec 27.18A): keep walking; judge the belief only at arrival.
                var atTarget = npc.Plan.TargetJunctionId is { } targetJ &&
                    npc.CurrentJunction is { } currentJ && currentJ.Equals(targetJ);
                if (!atTarget && npc.Execution.Status != ExecutionStatus.InProgress)
                {
                    continue;
                }

                // Arrived (or was mid-interaction) and the object is gone:
                // stale memory discovered — forget, release, re-decide.
                if (npc.Memory.KnownObjects.Remove(npc.Plan.TargetObjectId.Value))
                {
                    Trace.Emit(world, npc.Id, "MemoryForgotten",
                        $"Obj={npc.Plan.TargetObjectId.Value.Value} Stale (arrived, object gone)");
                }

                Trace.Emit(world, npc.Id, "ExecFailed",
                    $"TargetObject={npc.Plan.TargetObjectId.Value.Value} not found in world (despawned?)");
                PlanInterruption.Abort(world, npc, "Target object despawned mid-plan");
                npc.Mind.CurrentGoal = GoalType.None;
                continue;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
            {
                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "ExecFailed",
                    $"Definition={worldObject.DefinitionId} not found in catalog");
                continue;
            }

            if (npc.Movement.IsMoving)
            {
                Trace.Emit(world, npc.Id, "ExecWaitingForMovement",
                    $"Status={npc.Movement.Status} PathStep={npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}");
                continue;
            }

            if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
            {
                Trace.Emit(world, npc.Id, "ExecWaitingForArrival",
                    $"MovementStatus={npc.Movement.Status} (not Arrived)");
                continue;
            }

            if (npc.Execution.Status == ExecutionStatus.None)
            {
                // Memory promised a free object; reality may disagree
                // (spec 27.18A): never stomp another NPC's occupancy.
                if (worldObject.IsOccupied && worldObject.CurrentUser != npc.Id)
                {
                    // Spec 28.15B: scarcity breeds friction — resent the occupant.
                    if (worldObject.CurrentUser is { } occupant)
                    {
                        var resentRel = npc.Social.GetOrCreate(occupant);
                        resentRel.Affinity = MathUtil.Clamp(resentRel.Affinity - 0.08f, -1f, 1f);
                        npc.Execution.LastTalkResultTick = world.Tick;
                        npc.Execution.LastTalkAffinityDelta = -0.08f;
                        SocialCueSignals.Stamp(world, npc, "Resentment", occupant);
                        Trace.Emit(world, npc.Id, "RelationshipChanged",
                            $"NPC{npc.Id.Value}->NPC{occupant.Value} Aff={resentRel.Affinity:F2} " +
                            $"(-0.08 resentment: {worldObject.DefinitionId} taken)");
                    }

                    Trace.Emit(world, npc.Id, "InteractionBlocked",
                        $"{worldObject.DefinitionId} occupied by " +
                        $"NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"}");
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Target {worldObject.DefinitionId} occupied on arrival");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                var interaction = ResolveInteraction(definition, GetPlannedInteractionType(npc.Plan));
                if (interaction is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "ExecFailed",
                        $"No interaction of planned type on {worldObject.DefinitionId}");
                    continue;
                }

                // Spec 35.5: hanging needs a wet worn garment and a free rack.
                if (interaction.Type == InteractionType.Hang)
                {
                    var wetWorn = FindWettestWornItem(npc);
                    if (wetWorn is null || wetWorn.Wetness <= 0.5f || RackIsFull(world, worldObject))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            "Cannot hang (nothing wet or rack full)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 29F.3: recipe ingredients validated at start.
                if (interaction.Type == InteractionType.Craft)
                {
                    if (!npc.Body.CanUseToolsOrWeapons && CraftNeedsToolsOrWeapons(npc.Plan.Goal))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot craft {npc.Plan.Goal} (no legs)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    // Spec §54 (R2): ingredients/gate sourced from RecipeCatalog.
                    // Same arm set as before — CraftBandage stays out (=> false),
                    // preserving today's behaviour that its craft-start gate never
                    // passes here.
                    var craftOk = npc.Plan.Goal switch
                    {
                        GoalType.CraftSpear => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CookMeat => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftLeather => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftAxe => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftPickaxe => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftRack => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftBed => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftTent => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftBow => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftArrows => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftRope => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftCloth => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftKnife => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        _ => false
                    };

                    if (!craftOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot craft ({npc.Plan.Goal}: ingredients or fire missing)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 35.3: building needs the full bill for the pending piece.
                // Spec 35.3: the HUT piece-placement (GoalType.Build) needs the full
                // wall/floor bill in hand at start. InteractionType.Build is SHARED
                // with GoalType.BuildFurniture, though — a furniture build-site
                // (campfire, bed) instead accepts a partial delivery of whatever
                // material it still needs (ApplyFurnitureSite), so it must NOT be
                // gated on the hut bill (that wrongly aborted bed deliveries, whose
                // leaves/sticks don't satisfy a hut piece).
                if (interaction.Type == InteractionType.Build && !BuildSiteMath.IsSite(worldObject))
                {
                    var pendingPiece = DecisionSystem.NextBuildPiece(world);
                    var billOk = pendingPiece is { } bill &&
                        DecisionSystem.CountInventory(npc, "resource.log") >= bill.Logs &&
                        DecisionSystem.CountInventory(npc, "resource.stone") >= bill.Stones &&
                        DecisionSystem.CountInventory(npc, "resource.palm_leaf") >= bill.Leaves;
                    if (!billOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc, "Cannot build (materials missing or hut done)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 35.2: trees need an axe or saw; boulders need the pickaxe.
                var harvestDurationDivisor = 1;
                if (interaction.Type == InteractionType.Harvest)
                {
                    if (!npc.Body.CanUseToolsOrWeapons)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot harvest {worldObject.DefinitionId} (no legs)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    var isBoulder = definition.Tags.Contains("Boulder");
                    // Spec §54: yucca is cut with a BLADE — a knife or an axe (not
                    // a saw or pickaxe); trees still need an axe/saw; boulders the
                    // pickaxe.
                    var isYucca = definition.Tags.Contains("Yucca");
                    var hasChopTool = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood);
                    var hasBlade = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.Cut);
                    var toolOk = isBoulder
                        ? Content.GearCatalog.HasCapability(
                            npc.Inventory.Items, Content.GearCapability.Mine)
                        : isYucca
                            ? hasBlade
                            : hasChopTool;
                    if (!toolOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot harvest {worldObject.DefinitionId} (missing tool)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    // Spec 35.2: faster felling comes from the gear sheet
                    // (saw = 2), not from an id check — any future power tool
                    // declares its own multiplier.
                    if (!isBoulder)
                    {
                        var speedMult = Content.GearCatalog.BestHarvestSpeedMult(npc.Inventory.Items);
                        if (speedMult > 1f)
                        {
                            harvestDurationDivisor = (int)speedMult;
                        }
                    }
                }

                // §gear: the DATA-DRIVEN skill gate, ANY-OF. The interaction
                // lists the capabilities it accepts — a log splits under an
                // axe (ChopWood) OR a knife (Cut); one generic check, no
                // per-verb code. Legacy per-type checks below run ONLY when
                // the interaction declares nothing (older content).
                if (interaction.RequiredCapabilities.Count > 0 &&
                    !DecisionSystem.HasAnyCapability(npc, interaction.RequiredCapabilities))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Cannot {interaction.Id} on {worldObject.DefinitionId} " +
                        $"(no gear with any of [{string.Join("/", interaction.RequiredCapabilities)}])");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec §54: splitting a log into sticks needs a chopping tool.
                // (Legacy path — skipped when the interaction DECLARES its
                // accepted capabilities; the any-of gate above already ran.)
                if (interaction.Type == InteractionType.Process &&
                    interaction.RequiredCapabilities.Count == 0)
                {
                    var isCoconut = definition.Tags.Contains("Coconut");
                    // §50-prone: coconuts are light hand-work — allowed lying.
                    // Heavy processing (log splitting) still needs standing.
                    if (!isCoconut && !npc.Body.CanUseToolsOrWeapons)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot process {worldObject.DefinitionId} (no legs)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    var hasChopTool = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood);
                    var hasCoconutBlade = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.Cut);
                    if (isCoconut ? !hasCoconutBlade : !hasChopTool)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot split {worldObject.DefinitionId} (missing tool)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                if (interaction.Type == InteractionType.Drink &&
                    definition.Tags.Contains("CoconutWater") &&
                    worldObject.ResourceAmount <= 0f)
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Cannot drink {worldObject.DefinitionId} (already drained)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec §54: butchering a carcass/corpse needs a knife in hand.
                // (Legacy path — declared interactions use the any-of gate.)
                if (interaction.Type == InteractionType.Butcher &&
                    interaction.RequiredCapabilities.Count == 0 &&
                    !Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.Butcher))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Cannot butcher {worldObject.DefinitionId} (no knife)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec 29E.3: fueling needs a carried log; lighting a dead
                // fire additionally needs the lighter — UNLESS she's cold
                // enough to friction/hand-drill it (§45 r5).
                if (interaction.Type == InteractionType.Fuel)
                {
                    var hasWoodNow = npc.Inventory.Items.Contains("resource.stick");
                    // §45 r5 parity: the DECISION layer already lets a genuinely
                    // cold girl SELECT TendFire without the one colony lighter
                    // (canFrictionLight = ThermalComfort < -0.35). Execution must
                    // honour the same rule or the brain sends her to the pit and
                    // this gate bounces her right back — the fire-probe soak
                    // showed 6007 "ready to light" freezing ticks converting to
                    // only 7 FireLit because the friction path was never wired
                    // into the Fuel interaction (only the lighter-carrier lit).
                    // §54.14 (r2): same hysteresis as the decision layer — the
                    // walk over must not revoke the drill (LastFreezingTick is
                    // stamped in DecisionSystem each freezing tick).
                    var canFrictionLight = npc.Needs.ThermalComfort < -0.35f ||
                        world.Tick - npc.Mind.LastFreezingTick < SimBalance.FrictionLightGraceTicks;
                    var missingLighter = worldObject.ResourceAmount <= 0f &&
                        !Content.GearCatalog.HasCapability(
                            npc.Inventory.Items, Content.GearCapability.Ignite) &&
                        !canFrictionLight;
                    if (!hasWoodNow || missingLighter)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot fuel fire (wood={hasWoodNow} lighterMissing={missingLighter})");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                npc.Execution.Status = ExecutionStatus.InProgress;
                npc.Execution.CurrentInteraction = interaction.Type;
                npc.Execution.TargetObject = worldObject.Id;
                npc.Execution.StartTick = world.Tick;
                npc.Execution.EndTick = world.Tick + interaction.DurationTicks / harvestDurationDivisor;
                worldObject.IsOccupied = true;
                // Spec 28.15C: a corpse's CurrentUser records whose body it
                // is — mourning must not overwrite it.
                if (!definition.Tags.Contains("Corpse"))
                {
                    worldObject.CurrentUser = npc.Id;
                }
                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.OccupyJunction(world, jId, npc.Id);
                }

                Trace.Emit(world, npc.Id, "InteractionStarted",
                    $"{interaction.Type} -> {worldObject.DefinitionId} " +
                    $"Duration={interaction.DurationTicks}ticks ({interaction.DurationTicks * world.TickDeltaTime:F1}s) " +
                    $"EndTick={npc.Execution.EndTick} " +
                    $"Effects=[H={interaction.Effects.HungerDelta:+0.00;-0.00} " +
                    $"E={interaction.Effects.EnergyDelta:+0.00;-0.00} " +
                    $"C={interaction.Effects.ComfortDelta:+0.00;-0.00} " +
                    $"T={interaction.Effects.ThermalDelta:+0.00;-0.00} " +
                    $"W={interaction.Effects.WarmthDelta:+0.00;-0.00}]");
                continue;
            }

            if (npc.Execution.Status == ExecutionStatus.InProgress)
            {
                // Spec 42: the fire died mid-huddle — a dead pit warms nobody,
                // so warming (and boiling) at it stops NOW instead of playing
                // out the full interaction at a cold fireplace.
                if (npc.Execution.CurrentInteraction is InteractionType.Observe
                        or InteractionType.FillBottle &&
                    definition.Tags.Contains("Campfire") &&
                    worldObject.ResourceAmount <= 0f)
                {
                    PlanInterruption.Abort(world, npc, "Fire went out mid-interaction");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                var remaining = npc.Execution.EndTick - world.Tick;
                if (remaining > 0)
                {
                    var total = npc.Execution.EndTick - npc.Execution.StartTick;
                    var progress = total > 0 ? 1f - (float)remaining / total : 1f;
                    // Spec 29C.9: needs fill gradually over the action (Sims-
                    // style), not in a jump at the end. Each in-progress tick
                    // applies one duration-share; the final share lands at
                    // completion (total = duration shares = the full effect).
                    var inProgressInteraction = ResolveInteraction(definition, npc.Execution.CurrentInteraction);
                    if (inProgressInteraction is not null && total > 0)
                    {
                        ApplyEffectsScaled(npc, inProgressInteraction.Effects, 1f / total);
                    }

                    if (SimTrace.Verbose)
                    {
                        Trace.Emit(world, npc.Id, "ExecProgress",
                            $"{npc.Execution.CurrentInteraction} Progress={progress:P0} " +
                            $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                    }

                    continue;
                }

                var completedInteraction = ResolveInteraction(definition, npc.Execution.CurrentInteraction);
                if (completedInteraction is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "ExecFailed",
                        $"Interaction {npc.Execution.CurrentInteraction} vanished from {worldObject.DefinitionId}");
                    continue;
                }

                var needsBefore = Trace.FormatNeeds(npc.Needs);
                // Spec 29C.9: the final duration-share of the gradual fill —
                // the earlier shares landed tick-by-tick during the action.
                var completedTotal = npc.Execution.EndTick - npc.Execution.StartTick;
                ApplyEffectsScaled(npc, completedInteraction.Effects,
                    completedTotal > 0 ? 1f / completedTotal : 1f);

                // Spec 29H: filling the bottle charges it (raw at a bank,
                // boiled at a lit campfire) — thirst is quenched only on Drink.
                if (completedInteraction.Type == InteractionType.FillBottle)
                {
                    npc.BottleWater = definition.Tags.Contains("RawWater")
                        ? WaterKind.Raw : WaterKind.Boiled;
                    // Spec §52: one fill = several gulps; refill only when dry.
                    npc.BottleCharges = SimBalance.BottleCapacity;
                    Trace.Emit(world, npc.Id, "BottleFilled",
                        $"{npc.BottleWater} x{npc.BottleCharges} from {worldObject.DefinitionId}");
                }

                var needsAfter = Trace.FormatNeeds(npc.Needs);

                npc.Execution.Status = ExecutionStatus.Completed;
                npc.Execution.LastCompletedTick = world.Tick;
                // Spec 31C.8: the snapshot must not report a finished interaction —
                // the view would keep the pose while the body walks away.
                npc.Execution.CurrentInteraction = null;

                // Spec 31C.7A: after a proper rest she gets on with her day.
                if (completedInteraction.Type == InteractionType.Sit)
                {
                    npc.Mind.Cooldowns.Add(new GoalCooldown
                    {
                        Goal = GoalType.Sit,
                        EndTick = world.Tick + 240
                    });
                }

                if (completedInteraction.Type == InteractionType.PickUp)
                {
                    // §54.14 (r2): PickUp on the CAMPFIRE takes one cooked chunk
                    // off the spit — the fire itself never leaves the ground.
                    if (definition.Tags.Contains("Campfire"))
                    {
                        TakeMeatFromSpit(world, npc, worldObject);
                    }
                    // Spec §52: "gathering a tool" that rides in a dropped
                    // garment's pockets — rifle the pockets and leave the
                    // garment (with any non-tool stash) on the ground.
                    else if (npc.Plan.Goal == GoalType.GatherTools &&
                        worldObject.Contents.Count > 0 &&
                        !definition.Tags.Contains("Tool"))
                    {
                        RecoverStashedTools(world, npc, worldObject);
                    }
                    else if (!InventoryMath.MakeRoomFor(world, npc, worldObject.DefinitionId))
                    {
                        worldObject.IsOccupied = false;
                        worldObject.CurrentUser = null;
                        Trace.Emit(world, npc.Id, "PickupBlocked",
                            $"Def={worldObject.DefinitionId} Obj={worldObject.Id.Value} " +
                            $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
                            $"({npc.Inventory.UsedSlots}/{npc.Inventory.Capacity})");
                        continue;
                    }
                    else
                    {
                        // Item moves from world to inventory; the world object is gone,
                        // so occupancy flags die with it (spec 29B.2).
                        npc.Inventory.Items.Add(new ItemInstance(worldObject.DefinitionId)
                        {
                            Wetness = worldObject.Wetness,
                            Durability = worldObject.Durability,
                            ResourceAmount = worldObject.ResourceAmount,
                            Dirtiness = worldObject.Dirtiness,
                            Bloodiness = worldObject.Bloodiness
                        });
                        WorldObjectMutations.DespawnObject(world, worldObject.Id);
                        Trace.Emit(world, npc.Id, "ItemPickedUp",
                            $"Def={worldObject.DefinitionId} Obj={worldObject.Id.Value} " +
                            $"Inventory=[{string.Join(",", npc.Inventory.Items)}] ({npc.Inventory.Items.Count}/{npc.Inventory.Capacity})");
                    }
                }
                else if (completedInteraction.Type == InteractionType.Dress)
                {
                    // Spec 31A.5B: one item per (layer, body part) — dressing
                    // over an occupied slot takes the old garment off first.
                    ResolveWearConflicts(world, npc, worldObject.DefinitionId);

                    // Spec 31A.5A: dressing consumes the world object — only
                    // one NPC can wear this garment.
                    npc.WornItems.Add(new ItemInstance(worldObject.DefinitionId)
                    {
                        Wetness = worldObject.Wetness,
                        Durability = worldObject.Durability,
                        Dirtiness = worldObject.Dirtiness,
                        Bloodiness = worldObject.Bloodiness
                    });
                    // Spec §52: putting the garment back on recovers whatever it
                    // was carrying — the pockets pour into the pack (capacity just
                    // grew by this garment's slots); anything still over spills.
                    _dressPourScratch.Clear();
                    _dressPourScratch.AddRange(worldObject.Contents);
                    worldObject.Contents.Clear();
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    EquipmentMath.Recalculate(world, npc);
                    foreach (var stashed in _dressPourScratch)
                    {
                        GiveOrDrop(world, npc, stashed);
                    }
                    if (_dressPourScratch.Count > 0)
                    {
                        Trace.Emit(world, npc.Id, "StashRecovered",
                            $"{worldObject.DefinitionId} returned [{string.Join(",", _dressPourScratch)}]");
                    }
                    Trace.Emit(world, npc.Id, "ItemWorn",
                        $"Def={worldObject.DefinitionId} Worn=[{string.Join(",", npc.WornItems)}] " +
                        $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");

                    // Spec 42: one wardrobe stop per while — never chain-dress.
                    // A cold girl with no real warmth in reach pinned Dress at
                    // score 1.0 forever (thermal=1.00, exec=InProgress at soak
                    // end) and starved the fire/water chain WITH THE LIGHTER IN
                    // HER POCKET. The cooldown opens a window for TendFire &
                    // GetWater between wardrobe attempts.
                    npc.Mind.Cooldowns.RemoveAll(c => c.Goal == GoalType.Dress);
                    npc.Mind.Cooldowns.Add(new GoalCooldown
                    {
                        Goal = GoalType.Dress,
                        EndTick = world.Tick + 160
                    });
                }
                else if (completedInteraction.Type == InteractionType.Craft &&
                         npc.Plan.Goal == GoalType.CookMeat &&
                         definition.Tags.Contains("Campfire"))
                {
                    // §54.14 (r2): "cooking" = HANGING the raw chunk on the
                    // spit. The roast itself runs in FireSystem while the fire
                    // burns; the cooked chunk stays on the crossbar until a
                    // hungry housemate takes it (GetFood → PickUp).
                    if (BuildSiteMath.CampfireSpitComplete(worldObject) &&
                        BuildSiteMath.HangingMeat(worldObject, "food.meat_raw") +
                        BuildSiteMath.HangingMeat(worldObject, "food.meat_cooked") <
                        SimBalance.CampfireSpitCapacity)
                    {
                        ConsumeRecipeInputs(npc, npc.Plan.Goal);
                        // ResourceAmount doubles as roast progress (ticks).
                        worldObject.Contents.Add(new ItemInstance("food.meat_raw"));
                        Trace.Emit(world, npc.Id, "MeatHungOnSpit",
                            $"food.meat_raw on the spit at Tile={worldObject.Tile.Q},{worldObject.Tile.R} " +
                            $"hanging raw={BuildSiteMath.HangingMeat(worldObject, "food.meat_raw")} " +
                            $"cooked={BuildSiteMath.HangingMeat(worldObject, "food.meat_cooked")}");
                    }
                    else
                    {
                        Trace.Emit(world, npc.Id, "SpitHangFailed",
                            $"spitComplete={BuildSiteMath.CampfireSpitComplete(worldObject)} " +
                            $"hooksUsed={BuildSiteMath.HangingMeat(worldObject, "food.meat_raw") + BuildSiteMath.HangingMeat(worldObject, "food.meat_cooked")}" +
                            $"/{SimBalance.CampfireSpitCapacity}");
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Craft)
                {
                    // Spec 29F.3 / §54 (R2): inputs consumed from RecipeCatalog
                    // via ConsumeRecipeInputs; the per-goal arm below only places
                    // the OUTPUT (tool / worn / furniture / side-effect). The
                    // firewood→stick rewire (§54 phase 1) edits the catalog, not
                    // these arms.
                    ConsumeRecipeInputs(npc, npc.Plan.Goal);
                    // Item-output crafts share one grant helper (also used by
                    // the in-place path); placed furniture keeps needing the
                    // station object it is raised beside.
                    if (!GrantCraftOutput(world, npc, npc.Plan.Goal))
                    {
                        switch (npc.Plan.Goal)
                        {
                            case GoalType.CraftRack:
                                PlaceRack(world, npc, worldObject);
                                break;
                            case GoalType.CraftBed:
                                // Spec §54.2: the campfire bed is the leaf MAT (8 leaves
                                // + 2 stick rails — consumed via the catalog). The
                                // premium bedroll is built at a progressive build-site,
                                // not here.
                                PlaceCraftedFurniture(world, npc, worldObject, "bed.leaf");
                                Trace.Emit(world, npc.Id, "BedCrafted", "A leaf sleeping-mat");
                                break;
                            case GoalType.CraftTent:
                                // Spec 40.14: 4 leaves woven into a shade canopy.
                                PlaceCraftedFurniture(world, npc, worldObject, "shelter.tent");
                                Trace.Emit(world, npc.Id, "TentCrafted", "A leaf sun shelter");
                                break;
                        }
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Build)
                {
                    // Spec §52: a furniture site accepts a delivery or is raised;
                    // the hut anchor runs the classic piece-placement.
                    if (BuildSiteMath.IsSite(worldObject))
                    {
                        ApplyFurnitureSite(world, npc, worldObject);
                    }
                    else
                    {
                        ApplyBuildPiece(world, npc);
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.BuildRaft)
                {
                    // Spec 40.15: every carried log goes into the raft; at the
                    // target the colony can sail off the island.
                    var deposited = DecisionSystem.CountInventory(npc, "resource.log");
                    npc.Inventory.Items.RemoveAll(i => i.DefinitionId == "resource.log");
                    world.RaftProgress = System.Math.Min(WorldState.RaftTarget, world.RaftProgress + deposited);
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                    Trace.Emit(world, npc.Id, "RaftProgress",
                        $"+{deposited} logs -> {world.RaftProgress}/{WorldState.RaftTarget}");
                    if (world.RaftProgress >= WorldState.RaftTarget)
                    {
                        world.Completed = true;
                        Trace.EmitSystem(world, "RaftLaunched",
                            "The raft is finished — the colony can leave the island!");
                    }
                }
                else if (completedInteraction.Type == InteractionType.Harvest)
                {
                    // Spec 35.2 / §54 (R1): the object is consumed; loot is
                    // declared as data (Yields) and scatters on the ground.
                    ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
                    if (definition.Tags.Contains("Boulder"))
                    {
                        Trace.Emit(world, npc.Id, "BoulderBroken",
                            $"{worldObject.DefinitionId} at Tile={worldObject.Tile.Q},{worldObject.Tile.R} -> 4 stones");
                    }
                    else
                    {
                        Trace.Emit(world, npc.Id, "TreeChopped",
                            $"{worldObject.DefinitionId} felled -> logs scattered");
                    }

                    // §54.2: a felled palm leaves a sit-able stump obstacle at its
                    // spot. Capture its placement, despawn the palm (unblocks its
                    // junction), then spawn the stump there (re-blocks it).
                    var leavesStump = definition.Tags.Contains("Palm");
                    var stumpTile = worldObject.Tile;
                    var stumpFragment = worldObject.Fragment;
                    var stumpJunction = worldObject.Junctions.Count > 0
                        ? (JunctionId?)worldObject.Junctions[0] : null;

                    WorldObjectMutations.DespawnObject(world, worldObject.Id);

                    if (leavesStump && stumpJunction is { } sj)
                    {
                        WorldObjectMutations.SpawnObject(world, "stump.palm", stumpFragment, stumpTile, sj);
                    }
                }
                else if (completedInteraction.Type == InteractionType.Process)
                {
                    if (definition.Tags.Contains("Coconut"))
                    {
                        var nextObject = ReplaceWithYields(world, npc, worldObject, completedInteraction.Yields);
                        Trace.Emit(world, npc.Id, "CoconutProcessed",
                            $"{worldObject.DefinitionId} -> {nextObject?.DefinitionId ?? "nothing"}");

                        if (nextObject is not null &&
                            TryContinueWorldPlanAfterInteraction(world, npc, nextObject, completedInteraction.Type))
                        {
                            continue;
                        }
                    }
                    else
                    {
                    // Spec §54: a Process consumes the object and scatters its
                    // yields — a log → sticks, a palm crown → leaves.
                    ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
                    var isCrown = definition.Tags.Contains("PalmCrown");
                    var yieldCount = 0;
                    foreach (var d in completedInteraction.Yields) yieldCount += d.Count;
                    Trace.Emit(world, npc.Id, isCrown ? "CrownChopped" : "LogSplit",
                        isCrown
                            ? $"{worldObject.DefinitionId} -> {yieldCount} leaves"
                            : $"{worldObject.DefinitionId} -> {SimBalance.LogSplitYield} sticks");
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    }
                }
                else if (completedInteraction.Type == InteractionType.Drink &&
                         definition.Tags.Contains("CoconutWater"))
                {
                    worldObject.ResourceAmount = System.MathF.Max(0f, worldObject.ResourceAmount - 1f);
                    Trace.Emit(world, npc.Id, "CoconutDrank",
                        $"{worldObject.DefinitionId} water left={worldObject.ResourceAmount:F0}");

                    if (TryContinueWorldPlanAfterInteraction(world, npc, worldObject, completedInteraction.Type))
                    {
                        continue;
                    }
                }
                else if (completedInteraction.Type == InteractionType.Eat &&
                         worldObject.DefinitionId == "food.coconut_open")
                {
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    Trace.Emit(world, npc.Id, "CoconutEaten", $"{worldObject.DefinitionId} consumed");
                }
                else if (completedInteraction.Type == InteractionType.Butcher)
                {
                    // Spec §54: knife a carcass/corpse — meat + hide scatter on the
                    // ground; the body is consumed. Butchering a housemate costs
                    // comfort (cannibalism).
                    ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
                    var wasCorpse = definition.Tags.Contains("Corpse");
                    if (wasCorpse && SimBalance.CannibalismEnabled)
                    {
                        // Comfort is satisfaction (higher = better) — the penalty
                        // subtracts.
                        npc.Needs.Comfort = MathUtil.Clamp(
                            npc.Needs.Comfort - SimBalance.CannibalismComfortPenalty, 0f, 1f);
                    }

                    Trace.Emit(world, npc.Id, "Butchered",
                        $"{worldObject.DefinitionId} (variant={worldObject.Variant}) -> meat + hide" +
                        (wasCorpse ? " [cannibalism]" : string.Empty));
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                }
                else if (completedInteraction.Type == InteractionType.Fuel)
                {
                    // Spec 29E.3 / §54: one stick per fueling, half a day of fire.
                    npc.Inventory.Items.Remove("resource.stick");
                    var wasLit = worldObject.ResourceAmount > 0f;
                    worldObject.ResourceAmount += 1200f;
                    Trace.Emit(world, npc.Id, wasLit ? "FireFueled" : "FireLit",
                        $"{worldObject.DefinitionId} Fuel={worldObject.ResourceAmount:F0} ticks");
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Observe &&
                         npc.Plan.Goal == GoalType.HaulToFire &&
                         definition.Tags.Contains("Campfire"))
                {
                    // Spec §52: set the low-value item down at the hearth (a
                    // fireside stockpile) — the pack has room again, and the item
                    // waits here to be reclaimed by normal pickup later.
                    var victim = InventoryMath.LowestImportanceDroppable(world, npc);
                    if (victim is not null)
                    {
                        npc.Inventory.Items.Remove(victim);
                        DropItemAtFeet(world, npc, victim);
                        Trace.Emit(world, npc.Id, "StashedAtFire",
                            $"{victim.DefinitionId} set by the fire (freed a slot)");
                    }
                }
                else if (completedInteraction.Type == InteractionType.Observe &&
                         definition.Tags.Contains("Corpse"))
                {
                    // Spec 28.15C: closure — the mourning period ends early.
                    npc.Mind.GrievingUntilTick = world.Tick;
                    worldObject.IsOccupied = false; // owner (CurrentUser) preserved
                    Trace.Emit(world, npc.Id, "Mourned",
                        $"Paid respects to NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"}");
                }
                else if (completedInteraction.Type == InteractionType.Observe &&
                         definition.Tags.Contains("Grave"))
                {
                    // Spec 28.15D: remembrance — the dead keep a social presence.
                    npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + 0.15f);
                    worldObject.IsOccupied = false; // owner preserved
                    Trace.Emit(world, npc.Id, "VisitedGrave",
                        $"Of NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"} " +
                        $"Social={npc.Needs.Social:F2}");
                }
                else if (completedInteraction.Type == InteractionType.Hang)
                {
                    // Spec 35.5: the wettest garment moves onto the rack —
                    // an ownerless world object that dries at x5.
                    var wetWorn = FindWettestWornItem(npc);
                    if (wetWorn is not null && worldObject.Junctions.Count > 0)
                    {
                        npc.WornItems.Remove(wetWorn);
                        EquipmentMath.Recalculate(world, npc);
                        var hung = WorldObjectMutations.SpawnObject(
                            world, wetWorn.DefinitionId, npc.Fragment,
                            worldObject.Tile, worldObject.Junctions[0]);
                        hung.Wetness = wetWorn.Wetness;
                        hung.Durability = wetWorn.Durability;
                        hung.Dirtiness = wetWorn.Dirtiness;
                        hung.Bloodiness = wetWorn.Bloodiness;
                        Trace.Emit(world, npc.Id, "ItemHung",
                            $"{wetWorn.DefinitionId} Wetness={wetWorn.Wetness:F2} on rack " +
                            $"Obj={worldObject.Id.Value}");
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Bury)
                {
                    // Spec 28.15D: corpse -> permanent grave; the place is
                    // sanctified — fear leaves every living memory.
                    var deceased = worldObject.CurrentUser;
                    var graveJunction = worldObject.Junctions.Count > 0
                        ? worldObject.Junctions[0]
                        : npc.CurrentJunction ?? default;
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    var grave = WorldObjectMutations.SpawnObject(
                        world, "grave.npc", npc.Fragment, worldObject.Tile, graveJunction);
                    grave.CurrentUser = deceased;

                    foreach (var living in world.Entities.Npcs.Values)
                    {
                        living.Memory.Dangers.RemoveAll(dg => dg.Tile == worldObject.Tile);
                    }

                    npc.Mind.GrievingUntilTick = world.Tick;
                    Trace.Emit(world, npc.Id, "Buried",
                        $"NPC{deceased?.Value.ToString() ?? "?"} laid to rest at " +
                        $"Tile={worldObject.Tile.Q},{worldObject.Tile.R}");
                }
                else
                {
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }

                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.FreeJunction(world, jId, npc.Id);
                    SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
                }

                Trace.Emit(world, npc.Id, "InteractionCompleted",
                    $"{completedInteraction.Type} on {worldObject.DefinitionId} " +
                    $"Duration={npc.Execution.EndTick - npc.Execution.StartTick}ticks " +
                    $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}]");

                // Spec 41.5: waking from a bed = stand and come to your
                // senses for a beat before the next errand.
                if (completedInteraction.Type == InteractionType.Sleep)
                {
                    npc.Mind.WakeGraceUntilTick = world.Tick + 12;
                }

                npc.Plan.Status = PlanStatus.Completed;
                npc.Plan.Steps.Clear();
                npc.Plan.TargetObjectId = null;
                npc.Plan.TargetJunctionId = null;
                npc.Plan.TargetTile = null;
                npc.Plan.TargetItemDefinitionId = null;
                npc.Plan.TargetAgentId = null;
                npc.Mind.CurrentGoal = GoalType.None;
                // Back to None so the next plan's interaction can start
                // (Completed would block the start gate forever).
                npc.Execution.Status = ExecutionStatus.None;
                npc.Execution.CurrentInteraction = null;
                npc.Execution.TargetObject = null;
                npc.Execution.StartTick = 0;
                npc.Execution.EndTick = 0;
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;

                Trace.Emit(world, npc.Id, "CycleReset",
                    $"Goal->None Plan->Completed Execution->Cleared Movement->Cleared (ready for next decision)");
            }
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
            WorldObjectMutations.DespawnObject(world, site.Id);
            if (junction is { } j)
            {
                WorldObjectMutations.SpawnObject(world, product, npc.Fragment, tile, j);
            }

            Trace.Emit(world, npc.Id, "FurnitureBuilt",
                $"{product} raised at Tile={tile.Q},{tile.R}");
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

    // Move-only plan (spec 27.18A foraging): no interaction — the plan
    // completes on arrival, letting perception refresh from the new spot.
    private static void RunMoveOnly(WorldState world, NPCState npc)
    {
        if (npc.Movement.IsMoving)
        {
            return;
        }

        var atTarget = npc.Plan.TargetJunctionId is { } targetJ &&
            npc.CurrentJunction is { } currentJ && currentJ.Equals(targetJ);
        if (!atTarget)
        {
            return;
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        Trace.Emit(world, npc.Id, "MoveOnlyArrived",
            $"Junction={npc.Plan.TargetJunctionId?.Value.ToString() ?? "-"} " +
            $"Tile={npc.Tile.Q},{npc.Tile.R} (looking around)");

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        if (npc.Mind.CurrentGoal == GoalType.Defend)
        {
            SocialCueSignals.Stamp(world, npc, "HelpCryAssistArrived", npc.Id);
            Trace.Emit(world, npc.Id, "HelpCryAssistArrived",
                "Reached attacker and joined the fight");
            return;
        }

        npc.Mind.CurrentGoal = GoalType.None;
        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed (move-only plan arrived)");
    }

    // Spec 28.15A: agent-targeted Talk. The listener stays passive — only the
    // initiator runs this state machine; both sides receive gains at the end.
    // Spec 29C.9 (iter 30): a real conversation, not a one-second exchange.
    // Spec §49: linger longer (40->90) but each chat sates less (0.40->0.20,
    // 0.25->0.12) — they STAND and talk visibly, yet still want another chat
    // later instead of one exchange topping the bar off for the day. The gap
    // is filled by the passive ambient-social trickle (NeedsDecaySystem).
    private static int TalkDurationTicks => Spec49.TalkDuration;
    private static float TalkInitiatorSocialGain => Spec49.TalkInitGain;
    private static float TalkListenerSocialGain => Spec49.TalkListenGain;
    private const float TalkRelationshipGain = 0.075f;

    // Spec 28.15B: quarrels and refusal-by-dislike.
    private const float QuarrelInitiatorSocialGain = 0.15f;
    private const float QuarrelListenerSocialGain = 0.10f;
    private const float QuarrelAffinityLoss = 0.18f;
    private const float QuarrelEmbarrassment = 0.30f;
    private const float RefusalAffinityThreshold = -0.25f;
    private const float LonelinessOverrideThreshold = 0.25f;
    private const float RejectionAffinityPenalty = 0.075f;

    private static void RunTalk(WorldState world, NPCState npc)
    {
        if (npc.Plan.TargetAgentId is not { } targetId ||
            !world.Entities.Npcs.TryGetValue(targetId, out var target))
        {
            AbortTalk(world, npc, "Talk target vanished");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            var talkRange = HexSpatialMath.HexRadius * 4f;
            var distance = HexSpatialMath.Distance(npc.Position, target.Position);
            if (distance > talkRange)
            {
                AbortTalk(world, npc,
                    $"Target NPC{targetId.Value} out of range (Dist={distance:F2} > {talkRange:F2})");
                return;
            }

            // Spec 28.9 (v1 acceptance): busy, starving, or walking-through
            // listeners refuse — and so do listeners who dislike the initiator
            // (28.15B), unless they are lonely enough for a reconciliation.
            // Spec 28.8: conversation partners turn to face each other.
            var faceDelta = new Float2(
                target.Position.X - npc.Position.X,
                target.Position.Y - npc.Position.Y);
            var faceDirection = HexSpatialMath.Normalize(faceDelta);
            npc.RotationDegrees = HexSpatialMath.AngleDegrees(faceDirection);
            target.RotationDegrees = HexSpatialMath.AngleDegrees(
                new Float2(-faceDirection.X, -faceDirection.Y));

            // §60: a listener who collapsed while the initiator was walking
            // over counts as busy — the neutral "sorry, busy" refusal, no
            // resentment (you can't be insulted by a coma).
            var targetBusy = (target.Execution.Status == ExecutionStatus.InProgress &&
                target.Execution.CurrentInteraction != InteractionType.Talk) ||
                target.IsUnconscious(world.Tick);
            var listenerAffinity = target.Social.GetOrCreate(npc.Id).Affinity;
            var dislikes = listenerAffinity < RefusalAffinityThreshold &&
                target.Needs.Social >= LonelinessOverrideThreshold;
            if (targetBusy || target.Mind.IsStarving || target.Movement.IsMoving || dislikes)
            {
                // Spec 28.10/28.15B: only a *personal* refusal breeds resentment;
                // "sorry, busy" is not an insult (a lesson from the first soak:
                // penalizing neutral refusals created an irreversible spiral).
                var rejectedRel = npc.Social.GetOrCreate(target.Id);
                if (dislikes)
                {
                    rejectedRel.Affinity = MathUtil.Clamp(
                        rejectedRel.Affinity - RejectionAffinityPenalty, -1f, 1f);
                    npc.Execution.LastTalkResultTick = world.Tick;
                    npc.Execution.LastTalkAffinityDelta = -RejectionAffinityPenalty;
                    Trace.Emit(world, npc.Id, "RelationshipChanged",
                        $"NPC{npc.Id.Value}->NPC{target.Id.Value} " +
                        $"Aff={rejectedRel.Affinity:F2} (-{RejectionAffinityPenalty:F2}) " +
                        $"after talk refusal");
                }

                SocialCueSignals.Stamp(world, npc, "TalkRejected", target.Id);
                SocialCueSignals.Stamp(world, target, "TalkRefused", npc.Id);
                Trace.Emit(world, npc.Id, "InteractionRejected",
                    $"Talk rejected by NPC{targetId.Value} " +
                    $"(Busy={targetBusy} Starving={target.Mind.IsStarving} " +
                    $"Moving={target.Movement.IsMoving} Dislikes={dislikes} " +
                    $"ListenerAff={listenerAffinity:F2}) MyAff->{rejectedRel.Affinity:F2}");
                AbortTalk(world, npc, $"Talk rejected by NPC{targetId.Value}");
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Talk;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + TalkDurationTicks;
            // Spec 28.15E: pick the conversation subject (context-biased,
            // deterministic) and carry it on the initiator — the presentation
            // shows the matching emoji over the speaker's head for the talk.
            var topic = PickTalkTopic(world, npc, target);
            npc.Execution.CurrentTalkTopic = topic;
            target.Execution.CurrentTalkTopic = topic;
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            Trace.Emit(world, npc.Id, "TalkStarted",
                $"With NPC{targetId.Value} Topic={topic} Duration={TalkDurationTicks}ticks " +
                $"({TalkDurationTicks * world.TickDeltaTime:F1}s) Dist={distance:F2}");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            if (remaining > 0)
            {
                return;
            }

            // Spec 28.15B: talk outcome roll. Cranky participants quarrel;
            // mutual affinity protects. Deterministic (hash-based).
            var initiatorRel = npc.Social.GetOrCreate(target.Id);
            var listenerRel = target.Social.GetOrCreate(npc.Id);
            var irritability = 0.5f * (
                System.Math.Max(npc.Needs.Hunger, 1f - npc.Needs.Energy) +
                System.Math.Max(target.Needs.Hunger, 1f - target.Needs.Energy));
            var mutualAffinity = (initiatorRel.Affinity + listenerRel.Affinity) * 0.5f;
            var friendshipProtection = 0.25f * System.Math.Max(0f, mutualAffinity);
            var hostilitySpice = 0.10f * System.Math.Max(0f, -mutualAffinity);
            var quarrelChance = MathUtil.Clamp(
                0.10f + 0.35f * irritability - friendshipProtection + hostilitySpice,
                0.05f, 0.60f);
            var roll = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, target.Id.Value);
            var quarreled = roll < quarrelChance;

            initiatorRel.Familiarity = MathUtil.Clamp01(initiatorRel.Familiarity + TalkRelationshipGain);
            listenerRel.Familiarity = MathUtil.Clamp01(listenerRel.Familiarity + TalkRelationshipGain);

            if (quarreled)
            {
                npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + QuarrelInitiatorSocialGain);
                target.Needs.Social = MathUtil.Clamp01(target.Needs.Social + QuarrelListenerSocialGain);
                initiatorRel.Affinity = MathUtil.Clamp(initiatorRel.Affinity - QuarrelAffinityLoss, -1f, 1f);
                listenerRel.Affinity = MathUtil.Clamp(listenerRel.Affinity - QuarrelAffinityLoss, -1f, 1f);
                npc.Social.Embarrassment = MathUtil.Clamp01(npc.Social.Embarrassment + QuarrelEmbarrassment);
                target.Social.Embarrassment = MathUtil.Clamp01(target.Social.Embarrassment + QuarrelEmbarrassment);
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.Socialize);
                SocialCueSignals.Stamp(world, npc, "TalkQuarrel", target.Id);
                SocialCueSignals.Stamp(world, target, "TalkQuarrel", npc.Id);

                Trace.Emit(world, npc.Id, "TalkQuarreled",
                    $"With NPC{targetId.Value} Chance={quarrelChance:F2} Roll={roll:F2} " +
                    $"Irritability={irritability:F2} MutualAff={mutualAffinity:F2} " +
                    $"Social={npc.Needs.Social:F2} TargetSocial={target.Needs.Social:F2}");
            }
            else
            {
                npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + TalkInitiatorSocialGain);
                target.Needs.Social = MathUtil.Clamp01(target.Needs.Social + TalkListenerSocialGain);
                initiatorRel.Affinity = MathUtil.Clamp(initiatorRel.Affinity + TalkRelationshipGain, -1f, 1f);
                listenerRel.Affinity = MathUtil.Clamp(listenerRel.Affinity + TalkRelationshipGain, -1f, 1f);
                SocialCueSignals.Stamp(world, npc, "TalkSuccess", target.Id);
                SocialCueSignals.Stamp(world, target, "TalkSuccess", npc.Id);

                Trace.Emit(world, npc.Id, "TalkCompleted",
                    $"With NPC{targetId.Value} Chance={quarrelChance:F2} Roll={roll:F2} " +
                    $"SocialGain=[{TalkInitiatorSocialGain:F2}/{TalkListenerSocialGain:F2}] " +
                    $"Social={npc.Needs.Social:F2} TargetSocial={target.Needs.Social:F2}");
            }

            // Spec 28.15E: stamp the outcome on BOTH so a Sims-style "+/-"
            // relationship pop can float over each head (both relationships
            // moved). Sign = direction, magnitude = single vs double glyph.
            var affinityDelta = quarreled ? -QuarrelAffinityLoss : TalkRelationshipGain;
            npc.Execution.LastTalkResultTick = world.Tick;
            npc.Execution.LastTalkAffinityDelta = affinityDelta;
            target.Execution.LastTalkResultTick = world.Tick;
            target.Execution.LastTalkAffinityDelta = affinityDelta;

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;
            // Spec 31C.8: the snapshot must not report a finished interaction —
            // the view would keep the pose while the body walks away.
            npc.Execution.CurrentInteraction = null;
            // Spec 28.15E: talk's over — drop the topic so the bubble clears.
            npc.Execution.CurrentTalkTopic = null;
            target.Execution.CurrentTalkTopic = null;

            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.FreeJunction(world, jId, npc.Id);
                SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
            }
            Trace.Emit(world, npc.Id, "RelationshipChanged",
                $"NPC{npc.Id.Value}->NPC{target.Id.Value} " +
                $"Fam={initiatorRel.Familiarity:F2} (+{TalkRelationshipGain:F2}) " +
                $"Aff={initiatorRel.Affinity:F2} ({affinityDelta:+0.00;-0.00})");
            Trace.Emit(world, target.Id, "RelationshipChanged",
                $"NPC{target.Id.Value}->NPC{npc.Id.Value} " +
                $"Fam={listenerRel.Familiarity:F2} (+{TalkRelationshipGain:F2}) " +
                $"Aff={listenerRel.Affinity:F2} ({affinityDelta:+0.00;-0.00})");

            if (target.Mind.PendingTalkFrom is { } inviterId && inviterId.Equals(npc.Id))
            {
                target.Mind.PendingTalkFrom = null;
            }

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetAgentId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.PathIndex = 0;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (talked)");
        }
    }

    private static void AbortTalk(WorldState world, NPCState npc, string reason)
    {
        // Spec 28.15E: a dropped talk clears its topic so no bubble lingers.
        npc.Execution.CurrentTalkTopic = null;
        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Socialize);
        PlanInterruption.Abort(world, npc, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }

    // Spec §53: what a suffering NPC most needs help with right now, and how
    // badly (0..1). Mirrors the perception build so a helper re-checks on
    // arrival — she may have recovered, worsened, or died on the way over.
    private static AidKind AssessAidKind(NPCState t, int tick, out float severity)
    {
        severity = 0f;
        if (t.Health <= 0f)
        {
            return AidKind.None;
        }

        var treatSev = t.Wounds.Count > 0 || t.Needs.Blood < 0.6f
            ? System.Math.Max(1f - t.Needs.Blood, 1f - t.Health)
            : 0f;
        var medSev = t.Mind.SickUntilTick > tick
            ? 0.6f
            : (t.Health < 0.4f && t.Wounds.Count == 0 ? 1f - t.Health : 0f);
        var hydrateSev = t.Needs.Thirst >= 0.55f ? t.Needs.Thirst : 0f;
        var feedSev = t.Needs.Hunger >= 0.55f ? t.Needs.Hunger : 0f;
        var consoleSev = tick < t.Mind.GrievingUntilTick ? 0.5f : 0f;
        if (t.Needs.Stress > 0.6f)
        {
            consoleSev = System.Math.Max(consoleSev, t.Needs.Stress * 0.6f);
        }

        var kind = AidKind.Treat;
        severity = treatSev;
        if (medSev > severity) { severity = medSev; kind = AidKind.Medicate; }
        if (hydrateSev > severity) { severity = hydrateSev; kind = AidKind.Hydrate; }
        if (feedSev > severity) { severity = feedSev; kind = AidKind.Feed; }
        if (consoleSev > severity) { severity = consoleSev; kind = AidKind.Console; }
        return severity <= 0f ? AidKind.None : kind;
    }

    // Spec §53: apply the help to the TARGET (no item is spent — the relief is
    // applied directly, so aid can never bankrupt the colony). Both sides' bond
    // is credited by the caller.
    private static void ApplyAidRelief(WorldState world, NPCState helper, NPCState target, AidKind kind)
    {
        switch (kind)
        {
            case AidKind.Feed:
                target.Needs.Hunger = MathUtil.Clamp01(target.Needs.Hunger - Spec53.FeedRelief);
                break;

            case AidKind.Hydrate:
                target.Needs.Thirst = MathUtil.Clamp01(target.Needs.Thirst - Spec53.HydrateRelief);
                break;

            case AidKind.Treat:
            {
                // Lift every intact wounded part and stop the bleed, and drop a
                // gauze wrap decal on the treated zones (mirrors self first-aid).
                var parts = new System.Collections.Generic.List<BodyPart>(target.Body.Parts.Keys);
                foreach (var part in parts)
                {
                    if (target.Body.IsSevered(part))
                    {
                        continue;
                    }
                    if (target.Body.Parts[part] < 1f)
                    {
                        target.Body.Parts[part] = MathUtil.Clamp01(target.Body.Parts[part] + Spec53.TreatHeal);
                        target.GauzeZones.Add(part);
                    }
                }
                foreach (var wound in target.Wounds)
                {
                    wound.Heal01 = MathUtil.Clamp01(wound.Heal01 + Spec53.TreatHeal);
                }
                target.Needs.Blood = MathUtil.Clamp01(target.Needs.Blood + Spec53.TreatBlood);
                break;
            }

            case AidKind.Medicate:
                target.Health = MathUtil.Clamp01(target.Health + Spec53.MedicateHeal);
                target.Needs.Blood = MathUtil.Clamp01(target.Needs.Blood + Spec53.TreatBlood * 0.5f);
                // A dose settles the sickness window and its remaining damage.
                target.Mind.SickUntilTick = 0;
                target.Mind.SicknessDamageRemaining = 0f;
                break;

            case AidKind.Console:
                target.Needs.Stress = MathUtil.Clamp01(target.Needs.Stress - Spec53.ConsoleStressRelief);
                // Sitting with her shortens the mourning a little.
                if (world.Tick < target.Mind.GrievingUntilTick)
                {
                    target.Mind.GrievingUntilTick = System.Math.Max(
                        world.Tick, target.Mind.GrievingUntilTick - 600);
                }
                // Comforting someone eases the comforter's own tension a touch.
                helper.Needs.Stress = MathUtil.Clamp01(
                    helper.Needs.Stress - Spec53.ConsoleStressRelief * 0.3f);
                break;
        }
    }

    // Spec §53: walk-up-and-help execution. Structured like RunTalk (arrive,
    // begin, run for AidDuration, complete) but with no refusal roll — a
    // sufferer doesn't turn help away — and a care outcome instead of a chat.
    private static void RunAid(WorldState world, NPCState npc)
    {
        if (npc.Plan.TargetAgentId is not { } targetId ||
            !world.Entities.Npcs.TryGetValue(targetId, out var target))
        {
            AbortAid(world, npc, "Aid target vanished");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            var aidRange = HexSpatialMath.HexRadius * 4f;
            var distance = HexSpatialMath.Distance(npc.Position, target.Position);
            if (distance > aidRange)
            {
                AbortAid(world, npc,
                    $"Target NPC{targetId.Value} out of aid range (Dist={distance:F2} > {aidRange:F2})");
                return;
            }

            // Re-check on arrival: she may have recovered / died on the way.
            var kindNow = AssessAidKind(target, world.Tick, out var severity);
            if (kindNow == AidKind.None || severity < Spec53.SufferingThreshold)
            {
                AbortAid(world, npc, $"NPC{targetId.Value} no longer needs aid");
                return;
            }

            // Turn to face her — a caring stance (both turn toward each other).
            var faceDelta = new Float2(
                target.Position.X - npc.Position.X, target.Position.Y - npc.Position.Y);
            var faceDirection = HexSpatialMath.Normalize(faceDelta);
            npc.RotationDegrees = HexSpatialMath.AngleDegrees(faceDirection);
            target.RotationDegrees = HexSpatialMath.AngleDegrees(
                new Float2(-faceDirection.X, -faceDirection.Y));

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = kindNow switch
            {
                AidKind.Feed => InteractionType.FeedOther,
                AidKind.Hydrate => InteractionType.HydrateOther,
                AidKind.Treat => InteractionType.TreatOther,
                AidKind.Medicate => InteractionType.MedicateOther,
                _ => InteractionType.ConsoleOther
            };
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec53.AidDuration;
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            SocialCueSignals.Stamp(world, npc, "AidStarted", target.Id);
            SocialCueSignals.Stamp(world, target, "AidStarted", npc.Id);
            Trace.Emit(world, npc.Id, "AidStarted",
                $"Kind={kindNow} With NPC{targetId.Value} Severity={severity:F2} " +
                $"Duration={Spec53.AidDuration}ticks");
            if (kindNow == AidKind.Treat)
            {
                StabilizeBleedingOnAidStart(world, npc, target);
            }
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            if (remaining > 0)
            {
                return;
            }

            var kind = npc.Execution.CurrentInteraction switch
            {
                InteractionType.FeedOther => AidKind.Feed,
                InteractionType.HydrateOther => AidKind.Hydrate,
                InteractionType.TreatOther => AidKind.Treat,
                InteractionType.MedicateOther => AidKind.Medicate,
                _ => AidKind.Console
            };

            ApplyAidRelief(world, npc, target, kind);

            // Both relationships rise — kindness under hardship bonds hard.
            var helperRel = npc.Social.GetOrCreate(target.Id);
            var wardRel = target.Social.GetOrCreate(npc.Id);
            var gain = Spec53.AidRelationshipGain;
            helperRel.Affinity = MathUtil.Clamp(helperRel.Affinity + gain, -1f, 1f);
            wardRel.Affinity = MathUtil.Clamp(wardRel.Affinity + gain, -1f, 1f);
            helperRel.Familiarity = MathUtil.Clamp01(helperRel.Familiarity + gain);
            wardRel.Familiarity = MathUtil.Clamp01(wardRel.Familiarity + gain);
            helperRel.Trust = MathUtil.Clamp01(helperRel.Trust + gain);
            wardRel.Trust = MathUtil.Clamp01(wardRel.Trust + gain);

            // Reuse the Sims-style "+/-" pop over both heads (spec 28.15E).
            npc.Execution.LastTalkResultTick = world.Tick;
            npc.Execution.LastTalkAffinityDelta = gain;
            target.Execution.LastTalkResultTick = world.Tick;
            target.Execution.LastTalkAffinityDelta = gain;
            SocialCueSignals.Stamp(world, npc, "AidCompleted", target.Id);
            SocialCueSignals.Stamp(world, target, "AidCompleted", npc.Id);

            // Helping settles the helper's own compassion.
            npc.Needs.Compassion = MathUtil.Clamp01(npc.Needs.Compassion + Spec53.AidSelfRestore);

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;
            npc.Execution.CurrentInteraction = null;

            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.FreeJunction(world, jId, npc.Id);
                SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
            }

            Trace.Emit(world, npc.Id, "Aided",
                $"Kind={kind} NPC{npc.Id.Value}->NPC{target.Id.Value} " +
                $"Trust={helperRel.Trust:F2} (+{gain:F2}) Fam={helperRel.Familiarity:F2} (+{gain:F2}) " +
                $"Aff={helperRel.Affinity:F2} (+{gain:F2}) MyCompassion={npc.Needs.Compassion:F2}");
            Trace.Emit(world, target.Id, "RelationshipChanged",
                $"NPC{target.Id.Value}->NPC{npc.Id.Value} " +
                $"Trust={wardRel.Trust:F2} (+{gain:F2}) Fam={wardRel.Familiarity:F2} (+{gain:F2}) " +
                $"Aff={wardRel.Affinity:F2} (+{gain:F2}) aided");

            if (target.Mind.PendingAidFrom is { } aiderId && aiderId.Equals(npc.Id))
            {
                target.Mind.PendingAidFrom = null;
            }

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetAgentId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.PathIndex = 0;

            Trace.Emit(world, npc.Id, "CycleReset", "Goal->None (aided)");
        }
    }

    private static void StabilizeBleedingOnAidStart(WorldState world, NPCState helper, NPCState target)
    {
        var stabilized = false;
        foreach (var wound in target.Wounds)
        {
            if (wound.Heal01 < 0.3f && wound.Severity >= 0.05f)
            {
                wound.Heal01 = 0.3f;
                stabilized = true;
            }
        }

        if (!stabilized)
        {
            return;
        }

        target.Needs.Blood = MathUtil.Clamp01(target.Needs.Blood + Spec53.TreatBlood * 0.5f);
        Trace.Emit(world, helper.Id, "AidStabilized",
            $"NPC{target.Id.Value} bleeding stemmed (Blood={target.Needs.Blood:F2})");
    }

    private static void AbortAid(WorldState world, NPCState npc, string reason)
    {
        // Release the claim so the sufferer is free for another helper.
        if (npc.Plan.TargetAgentId is { } tid &&
            world.Entities.Npcs.TryGetValue(tid, out var t) &&
            t.Mind.PendingAidFrom is { } aider && aider.Equals(npc.Id))
        {
            t.Mind.PendingAidFrom = null;
        }
        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Aid);
        PlanInterruption.Abort(world, npc, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }

    // Spec 28.15E: choose the subject of a talk. A weighted, deterministic draw
    // biased by the pair's situation — hungry housemates talk food, cold ones
    // talk weather/fire, scared ones talk dogs, friends flirt and joke, rivals
    // grumble — with the island's escape/shark themes always simmering. The
    // roll is a stateless hash of (tick, pair) so it's resume-safe and both the
    // sim and any replay agree. Presentation turns the value into an emoji.
    private static readonly TalkTopic[] TalkTopicOrder =
    {
        TalkTopic.SmallTalk, TalkTopic.Escape, TalkTopic.Sharks, TalkTopic.Dogs,
        TalkTopic.Weather, TalkTopic.Food, TalkTopic.Fire, TalkTopic.Home,
        TalkTopic.Gossip, TalkTopic.Flirt, TalkTopic.Joke, TalkTopic.Grumble
    };

    private static readonly float[] TalkTopicWeights = new float[12];

    private static TalkTopic PickTalkTopic(WorldState world, NPCState npc, NPCState target)
    {
        // Shared situation of the two participants (means, so one cranky/hungry
        // party still tilts the subject without dominating).
        var hunger = 0.5f * (npc.Needs.Hunger + target.Needs.Hunger);
        var social = 0.5f * (npc.Needs.Social + target.Needs.Social);
        var stress = 0.5f * (npc.Needs.Stress + target.Needs.Stress);
        var thermal = 0.5f * (npc.Needs.ThermalComfort + target.Needs.ThermalComfort);
        var cold = System.Math.Max(0f, -thermal);
        var hot = System.Math.Max(0f, thermal);
        var rain = world.Environment.IsRaining ? 1f : 0f;
        var mutualAff = 0.5f * (
            npc.Social.GetOrCreate(target.Id).Affinity +
            target.Social.GetOrCreate(npc.Id).Affinity);
        var like = System.Math.Max(0f, mutualAff);
        var dislike = System.Math.Max(0f, -mutualAff);

        // Weights are parallel to TalkTopicOrder. Base keeps every subject
        // possible; context terms make the fitting ones far likelier.
        var w = TalkTopicWeights;
        w[0] = 1.00f;                                   // SmallTalk
        w[1] = 0.60f + 0.60f * System.Math.Max(cold, rain); // Escape (misery → wanting off the rock)
        w[2] = 0.40f;                                   // Sharks (the island's ever-present menace)
        w[3] = 0.40f + 1.00f * stress;                  // Dogs (fear talk)
        w[4] = 0.30f + 1.20f * rain + 1.00f * cold + 0.80f * hot; // Weather
        w[5] = 0.30f + 1.50f * hunger;                  // Food
        w[6] = 0.30f + 1.00f * cold;                    // Fire (warmth)
        w[7] = 0.50f + 0.80f * (1f - social);           // Home (homesick when starved of company)
        w[8] = 0.50f;                                   // Gossip
        w[9] = 0.20f + 1.20f * like;                    // Flirt (they warm to each other)
        w[10] = 0.40f + 0.80f * like;                   // Joke
        w[11] = 0.30f + 1.00f * dislike + 0.60f * hunger; // Grumble (dislike / crankiness)

        var total = 0f;
        for (var i = 0; i < w.Length; i++)
        {
            total += w[i];
        }

        // Independent salt (5501) so the topic roll doesn't correlate with the
        // quarrel roll on the same (tick, pair).
        var roll = MathUtil.Hash01(world.Seed, world.Tick,
            npc.Id.Value * 31 + target.Id.Value, 5501) * total;
        for (var i = 0; i < w.Length; i++)
        {
            roll -= w[i];
            if (roll <= 0f)
            {
                return TalkTopicOrder[i];
            }
        }

        return TalkTopic.SmallTalk;
    }

    // Spec 31A.5B: remove and drop any worn item sharing (layer, part) with
    // the garment about to be worn — molly BodyBones.Equip semantics.
    private static readonly System.Collections.Generic.List<string> _conflictScratch = new();

    private static void ResolveWearConflicts(WorldState world, NPCState npc, string newItemId)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(newItemId, out var newDefinition) ||
            newDefinition.Layer is not { } newLayer)
        {
            return;
        }

        _conflictScratch.Clear();
        foreach (var wornId in npc.WornItems)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(wornId, out var wornDefinition) ||
                wornDefinition.Layer != newLayer)
            {
                continue;
            }

            foreach (var part in newDefinition.Covers)
            {
                if (wornDefinition.Covers.Contains(part))
                {
                    _conflictScratch.Add(wornId);
                    break;
                }
            }
        }

        foreach (var conflictId in _conflictScratch)
        {
            var conflictItem = npc.WornItems.Find(i => i.DefinitionId == (string)conflictId) ??
                new ItemInstance(conflictId);
            npc.WornItems.Remove(conflictItem);
            DropItemAtFeet(world, npc, conflictItem);
            Trace.Emit(world, npc.Id, "ItemReplaced",
                $"{conflictId} taken off (layer conflict with {newItemId})");
        }
    }

    // §35.5B: the rack holds up to SimBalance.RackCapacity garments — the
    // wearables at its junction, one per hanger slot on the assembled prefab.
    internal static bool RackIsFull(WorldState world, WorldObjectState rack)
    {
        if (rack.Junctions.Count == 0)
        {
            return true;
        }

        var hung = 0;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Id.Value != rack.Id.Value && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(rack.Junctions[0]) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Layer is not null)
            {
                hung++;
                if (hung >= SimBalance.RackCapacity)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static ItemInstance? FindWettestWornItem(NPCState npc)
    {
        ItemInstance? wettest = null;
        foreach (var item in npc.WornItems)
        {
            if (wettest is null || item.Wetness > wettest.Wetness)
            {
                wettest = item;
            }
        }

        return wettest;
    }

    // Spec 29G: crafted furniture lands on a free junction by the fire.
    private static void PlaceCraftedFurniture(WorldState world, NPCState npc, WorldObjectState campfire, string definitionId)
    {
        var spot = FindSpacedFurnitureSpot(world, campfire, definitionId) ?? npc.CurrentJunction;
        if (spot is not { } junction)
        {
            return;
        }

        WorldObjectMutations.SpawnObject(world, definitionId, npc.Fragment, npc.Tile, junction);
    }

    // Spec 35.7 (iter 33): the camp is no longer a heap. Crafted furniture
    // still hugs the fire (keeping travel cheap — the economy is tight), but
    // never lands right on top of another bed/rack: prefer a fireside junction
    // that isn't within a tile of an existing piece, widening the search ring
    // only if the near ones are all taken.
    private static readonly string[] OtherFurnitureTags = { "Bed", "Rack" };

    private static JunctionId? FindSpacedFurnitureSpot(
        WorldState world, WorldObjectState campfire, string definitionId)
    {
        if (campfire.Junctions.Count == 0)
        {
            return null;
        }

        // §54.9A: the piece must PHYSICALLY fit — its footprint (ObstacleRadius
        // measured off the real prefab) may not cross boulders, palms, the
        // ember ring, or other furniture.
        var footprint = world.Content.ObjectDefinitions.TryGetValue(definitionId, out var placedDef)
            ? placedDef.ObstacleRadius
            : 0f;

        var anchor = campfire.Junctions[0];
        // Pass 1: a free junction on the standable rim just OUTSIDE the
        // fire's blocked ember ring (§47). GetPassableNeighbors would look
        // at the anchor's immediate neighbors — all inside the blocked ring
        // now — so we use the same beside-arrival BFS the planner uses:
        // furniture lands in the passable zone with a natural offset from
        // the flames, still fireside-close.
        SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch);
        foreach (var neighbor in _furnitureRimScratch)
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                world.Junctions.Items.TryGetValue(neighbor, out var j) && j.Tiles.Count > 0 &&
                !IsNearOtherFurniture(world, j.Tiles[0]) &&
                SpatialQueries.FootprintClear(world, j, footprint))
            {
                return neighbor;
            }
        }

        // Pass 2: any junction 2 tiles out that's clear of other furniture.
        JunctionId? ring = null;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                !world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Walkable) || tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            if (HexSpatialMath.HexDistance(junction.Tiles[0], campfire.Tile) == 2 &&
                !IsNearOtherFurniture(world, junction.Tiles[0]) &&
                SpatialQueries.FootprintClear(world, junction, footprint))
            {
                ring = junction.Id;
                break;
            }
        }

        // Pass 3: fall back to any free rim junction (heap beats nowhere).
        if (ring is null)
        {
            SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch);
            foreach (var neighbor in _furnitureRimScratch)
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor))
                {
                    return neighbor;
                }
            }
        }

        return ring;
    }

    private static readonly System.Collections.Generic.List<JunctionId> _furnitureRimScratch = new();

    // Within 1 tile of an existing bed or rack (the campfire itself is fine
    // to sit beside — we only want to avoid stacking furniture on furniture).
    private static bool IsNearOtherFurniture(WorldState world, TileCoord tile)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            foreach (var tag in OtherFurnitureTags)
            {
                if (def.Tags.Contains(tag) && HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Spec 35.5: the crafted rack goes onto a free junction next to the
    // campfire; when the fireside is crowded it lands at the crafter's feet.
    private static void PlaceRack(WorldState world, NPCState npc, WorldObjectState campfire)
    {
        // Spec 35.7: spaced away from the fire and the bed (no more heap).
        var spot = FindSpacedFurnitureSpot(world, campfire, "station.drying_rack") ?? npc.CurrentJunction;
        if (spot is not { } junction)
        {
            GiveOrDrop(world, npc, "resource.stick");
            GiveOrDrop(world, npc, "resource.stick");
            Trace.Emit(world, npc.Id, "ExecFailed", "CraftRack: nowhere to place the rack");
            return;
        }

        var rack = WorldObjectMutations.SpawnObject(
            world, "station.drying_rack", npc.Fragment, npc.Tile, junction);
        Trace.Emit(world, npc.Id, "RackCrafted",
            $"Obj={rack.Id.Value} Junction={junction.Value}");
    }

    // Spec 29F: into the inventory, or at the feet when full.
    internal static void GiveOrDrop(WorldState world, NPCState npc, ItemInstance item)
    {
        if (npc.Inventory.HasSpace)
        {
            npc.Inventory.Items.Add(item);
        }
        else
        {
            DropItemAtFeet(world, npc, item);
        }
    }

    // Spec §54: a slain animal leaves a carcass on the map — knife it for meat
    // + hide. Rots on the CorpseSystem clock (Decays tag + ResourceAmount).
    internal static void SpawnCarcass(WorldState world, TileCoord tile, JunctionId junction, string variant)
    {
        var fragment = new FragmentId(1);
        foreach (var any in world.Entities.Npcs.Values)
        {
            fragment = any.Fragment;
            break;
        }

        var carcass = WorldObjectMutations.SpawnObject(world, "carcass.animal", fragment, tile, junction);
        carcass.ResourceAmount = SimBalance.CarcassDecayTicks;
        carcass.SpawnTick = world.Tick;
        carcass.Variant = variant;
        Trace.EmitSystem(world, "CarcassSpawned",
            $"{variant} carcass at Tile={tile.Q},{tile.R}");
    }

    // Spec §54 (R1): materialize a data-driven Yields list. Scatter drops land
    // as distinct ground objects at free junctions around the harvested/split
    // spot (logs & leaves around the stump, sticks around the split log); a
    // non-Scatter drop falls back to the legacy inventory-first GiveOrDrop.
    internal static void ApplyHarvestYields(
        WorldState world, NPCState npc, WorldObjectState source,
        System.Collections.Generic.IReadOnlyList<HarvestDrop> yields)
    {
        var used = new System.Collections.Generic.HashSet<JunctionId>();
        foreach (var drop in yields)
        {
            for (var i = 0; i < drop.Count; i++)
            {
                if (!drop.Scatter)
                {
                    GiveOrDrop(world, npc, CreateYieldItem(world, drop.DefinitionId));
                    continue;
                }

                var (tile, junction) = FindScatterSpot(world, source, used);
                if (junction is { } j)
                {
                    var spawned = WorldObjectMutations.SpawnObject(
                        world, drop.DefinitionId, source.Fragment, tile, j);
                    spawned.SpawnTick = world.Tick;
                    used.Add(j);
                }
                else
                {
                    // No free spot in the ring — don't lose the item, hand it over.
                    GiveOrDrop(world, npc, CreateYieldItem(world, drop.DefinitionId));
                }
            }
        }
    }

    private static ItemInstance CreateYieldItem(WorldState world, string definitionId)
    {
        var item = new ItemInstance(definitionId);
        // §59-склад: начальный запас из декларации (вода дырявого кокоса).
        if (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def))
        {
            var water = def.StoredAmount(Content.StoredKind.Water);
            if (water > 0f)
            {
                item.ResourceAmount = water;
            }
        }

        return item;
    }

    private static WorldObjectState? ReplaceWithYields(
        WorldState world,
        NPCState npc,
        WorldObjectState source,
        System.Collections.Generic.IReadOnlyList<HarvestDrop> yields)
    {
        if (yields.Count == 0 || source.Junctions.Count == 0)
        {
            WorldObjectMutations.DespawnObject(world, source.Id);
            return null;
        }

        var tile = source.Tile;
        var junction = source.Junctions[0];
        var fragment = source.Fragment;
        WorldObjectMutations.DespawnObject(world, source.Id);

        WorldObjectState? primary = null;
        var used = new System.Collections.Generic.HashSet<JunctionId> { junction };
        foreach (var drop in yields)
        {
            for (var i = 0; i < drop.Count; i++)
            {
                var spawnTile = tile;
                var spawnJunction = junction;
                if (primary is not null)
                {
                    var scatter = FindScatterSpot(world, source, used);
                    if (scatter.Item2 is not { } freeJunction)
                    {
                        GiveOrDrop(world, npc, CreateYieldItem(world, drop.DefinitionId));
                        continue;
                    }

                    spawnTile = scatter.Item1;
                    spawnJunction = freeJunction;
                    used.Add(freeJunction);
                }

                var spawned = WorldObjectMutations.SpawnObject(world, drop.DefinitionId, fragment, spawnTile, spawnJunction);
                spawned.SpawnTick = world.Tick;
                // §59-склад: заявленный запас; без склада — ноль (расходники).
                spawned.ResourceAmount =
                    world.Content.ObjectDefinitions.TryGetValue(drop.DefinitionId, out var dropDef)
                        ? dropDef.StoredAmount(Content.StoredKind.Water)
                        : 0f;
                primary ??= spawned;
            }
        }

        return primary;
    }

    private static bool TryContinueWorldPlanAfterInteraction(
        WorldState world,
        NPCState npc,
        WorldObjectState target,
        InteractionType completed)
    {
        var completedIndex = -1;
        for (var i = npc.Plan.CurrentStepIndex; i < npc.Plan.Steps.Count; i++)
        {
            var step = npc.Plan.Steps[i];
            if (step.Type == PlanStepType.Interact && step.Interaction == completed)
            {
                completedIndex = i;
                break;
            }
        }

        if (completedIndex < 0)
        {
            return false;
        }

        var nextInteract = -1;
        for (var i = completedIndex + 1; i < npc.Plan.Steps.Count; i++)
        {
            if (npc.Plan.Steps[i].Type == PlanStepType.Interact)
            {
                nextInteract = i;
                break;
            }
        }

        if (nextInteract < 0)
        {
            return false;
        }

        npc.Plan.CurrentStepIndex = nextInteract;
        npc.Plan.TargetObjectId = target.Id;
        npc.Plan.TargetTile = target.Tile;
        var continuedJunction = npc.Plan.TargetJunctionId;
        if (continuedJunction is null && target.Junctions.Count > 0)
        {
            continuedJunction = target.Junctions[0];
        }

        npc.Plan.TargetJunctionId = continuedJunction;
        for (var i = nextInteract; i < npc.Plan.Steps.Count; i++)
        {
            if (npc.Plan.Steps[i].Type == PlanStepType.Interact)
            {
                npc.Plan.Steps[i].TargetObject = target.Id;
                npc.Plan.Steps[i].TargetJunction = continuedJunction;
            }
        }

        target.IsOccupied = true;
        target.CurrentUser = npc.Id;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        Trace.Emit(world, npc.Id, "PlanContinues",
            $"{completed} complete; next step={npc.Plan.Steps[nextInteract].Interaction} on {target.DefinitionId}");
        return true;
    }

    // Spec §54: a free junction on the harvested tile or a neighbour, skipping
    // junctions already claimed by earlier drops this call so the yields land
    // at DISTINCT points (the scattered look).
    private static (TileCoord, JunctionId?) FindScatterSpot(
        WorldState world, WorldObjectState source,
        System.Collections.Generic.HashSet<JunctionId> used)
    {
        var tiles = new System.Collections.Generic.List<TileCoord> { source.Tile };
        foreach (var neighbor in SpatialQueries.GetNeighbors(world, source.Tile))
        {
            tiles.Add(neighbor);
        }

        foreach (var tileCoord in tiles)
        {
            if (!SpatialQueries.IsTileWalkable(world, tileCoord) ||
                !world.Tiles.Items.TryGetValue(tileCoord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (used.Contains(junctionId) ||
                    !SpatialQueries.IsJunctionPassable(world, junctionId) ||
                    !SpatialQueries.IsJunctionFree(world, junctionId))
                {
                    continue;
                }

                return (tileCoord, junctionId);
            }
        }

        return (source.Tile, null);
    }

    // Spec 35.5: dropped items keep their instance state on the ground.
    internal static WorldObjectState DropItemAtFeet(WorldState world, NPCState npc, ItemInstance item)
    {
        if (TryFindDropSpotAtFeet(world, npc, out var dropTile, out var dropJunction))
        {
            var dropped = WorldObjectMutations.SpawnObject(
                world, item.DefinitionId, npc.Fragment, dropTile, dropJunction);
            dropped.Wetness = item.Wetness;
            dropped.Durability = item.Durability;
            dropped.ResourceAmount = item.ResourceAmount;
            dropped.Dirtiness = item.Dirtiness;
            dropped.Bloodiness = item.Bloodiness;
            return dropped;
        }

        return null;
    }

    private static bool TryFindDropSpotAtFeet(
        WorldState world,
        NPCState npc,
        out TileCoord tile,
        out JunctionId junction)
    {
        // Prefer a genuinely free ground junction near the actor so dropped
        // items become ordinary world objects immediately. The actor's current
        // junction is usually occupied by the actor, so keep it as a fallback
        // rather than the first choice.
        var source = new WorldObjectState
        {
            Tile = npc.Tile,
            Fragment = npc.Fragment
        };
        var used = new System.Collections.Generic.HashSet<JunctionId>();
        if (npc.CurrentJunction is { } current)
        {
            used.Add(current);
        }

        var scatter = FindScatterSpot(world, source, used);
        if (scatter.Item2 is { } freeJunction)
        {
            tile = scatter.Item1;
            junction = freeJunction;
            return true;
        }

        if (npc.CurrentJunction is { } fallback &&
            world.Junctions.Items.ContainsKey(fallback))
        {
            tile = npc.Tile;
            junction = fallback;
            return true;
        }

        if (world.Tiles.Items.TryGetValue(npc.Tile, out var tileState))
        {
            foreach (var candidate in tileState.Junctions)
            {
                if (SpatialQueries.IsJunctionPassable(world, candidate))
                {
                    tile = npc.Tile;
                    junction = candidate;
                    return true;
                }
            }
        }

        tile = npc.Tile;
        junction = default;
        return false;
    }

    // Spec §52: take a garment off the body and lay it on the ground carrying
    // its pockets. Any pocket item that no longer fits the (now smaller) pack
    // rides down inside the dropped garment — the NPC still knows where its
    // bottle is and can fetch it later without dressing.
    internal static WorldObjectState DropGarmentWithContents(
        WorldState world, NPCState npc, ItemInstance garment)
    {
        _garmentSpillScratch.Clear();
        var inv = npc.Inventory;
        var guard = 0;
        while (inv.UsedSlots > inv.Capacity && guard++ < 64)
        {
            var victim = InventoryMath.LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                break;
            }

            inv.Items.Remove(victim);
            _garmentSpillScratch.Add(victim);
        }

        var dropped = DropItemAtFeet(world, npc, garment);
        if (dropped != null && _garmentSpillScratch.Count > 0)
        {
            dropped.Contents.AddRange(_garmentSpillScratch);
            Trace.Emit(world, npc.Id, "StashedInGarment",
                $"{garment.DefinitionId} holds [{string.Join(",", _garmentSpillScratch)}]");
        }

        return dropped;
    }

    private static readonly System.Collections.Generic.List<ItemInstance> _garmentSpillScratch = new();

    private static readonly System.Collections.Generic.List<ItemInstance> _dressPourScratch = new();

    private static readonly System.Collections.Generic.List<string> _stashRecoverScratch = new();

    // Spec §52: rifle a dropped garment's pockets for the tools the NPC lacks —
    // the tools come home, the garment (and any non-tool stash) stays on the
    // ground. This is how a knife left in an undressed jacket comes back
    // without re-dressing (seed 1104049673: both knives rode a doffed jacket
    // to the ground and their owner died of thirst two hexes away).
    private static void RecoverStashedTools(WorldState world, NPCState npc, WorldObjectState stash)
    {
        _stashRecoverScratch.Clear();
        for (var i = stash.Contents.Count - 1; i >= 0; i--)
        {
            var item = stash.Contents[i];
            if (npc.Inventory.Items.Contains(item.DefinitionId) ||
                !world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) ||
                !def.Tags.Contains("Tool") ||
                !InventoryMath.MakeRoomFor(world, npc, item.DefinitionId))
            {
                continue;
            }

            stash.Contents.RemoveAt(i);
            npc.Inventory.Items.Add(item);
            _stashRecoverScratch.Add(item.DefinitionId);
        }

        stash.IsOccupied = false;
        stash.CurrentUser = null;
        Trace.Emit(world, npc.Id, "StashRecovered",
            $"{stash.DefinitionId} pockets returned [{string.Join(",", _stashRecoverScratch)}] " +
            $"left [{string.Join(",", stash.Contents)}] " +
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
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

    // Spec 31A.5A: take off a worn item in place; it drops to the world at
    // the NPC's feet, retrievable by anyone.
    // §Wardrobe-anim: 8 ticks = 2.0s at 0.25s/tick — matches the dress window.
    private const int UndressDurationTicks = 8;

    // §Wardrobe-anim: the fraction of a dress/undress window at which the
    // garment changes hands. Dressing: gather (before) -> don the piece (after).
    // Undressing: doff the piece (before) -> gather it up off the body (after),
    // so the garment leaves the body here and is only dropped at the very end.
    internal const float WardrobeHandoffFraction = 0.5f;

    // Spec 29G: drive a ground rest plan — walk to the reserved spot (the
    // PathfindingSystem does the walking), then rest in place.
    private static void RunGroundRestPlan(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            if (npc.Movement.IsMoving)
            {
                return;
            }

            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                PlanInterruption.Abort(world, npc, "Ground rest spot unreachable");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var atTarget = npc.Plan.TargetJunctionId is { } target &&
                npc.CurrentJunction is { } current && current.Equals(target);
            if (!atTarget)
            {
                return;
            }
        }

        if (step.Type == PlanStepType.GroundSit)
        {
            RunGroundRest(world, npc, step, InteractionType.Sit, 70,
                step.TargetJunction is { } lg && PlanningSystem.IsLedgeId(world, lg)
                    ? SimBalance.GroundSitComfortLedge : SimBalance.GroundSitComfort,
                SimBalance.GroundSitEnergy);
        }
        else if (step.Type == PlanStepType.GroundCool)
        {
            // Spec 35.4: dwell in shade/water shedding heat — no comfort/energy
            // gain, the cooling is delivered for free by TemperatureSystem now
            // that she's standing on a genuinely cool tile.
            RunGroundCool(world, npc, step);
        }
        else
        {
            // Sleep restores as well as a bed (a night is a night) — the
            // bed's edge is comfort, not energy. +0.35 energy here produced
            // a poverty trap: 160 naps/soak and no time to live.
            RunGroundRest(world, npc, step, InteractionType.Sleep, 100, 0f, SimBalance.GroundSleepEnergy); // spec 42
        }
    }

    // Spec 29G: rest on the land — a timed in-place interaction with no
    // object. Lying claims the body's footprint so housemates path around.
    private static void RunGroundRest(
        WorldState world, NPCState npc, PlanStep step,
        InteractionType kind, int durationTicks, float comfort, float energy)
    {
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (step.TargetJunction is { } reserved &&
                !SpatialMutations.TryReserveJunction(
                    world, reserved, npc.Id, world.Tick, durationTicks + 8))
            {
                PlanInterruption.Abort(world, npc, "Ground rest edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = kind;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);

                if (kind == InteractionType.Sit && world.Junctions.Items.TryGetValue(spot, out var ledge) &&
                    PlanningSystem.TryGetEdgeSeatGeometry(
                        world, ledge, waterOnly: false, out var standTile, out var facing))
                {
                    PlaceAtEdge(world, npc, ledge, standTile, facing);
                }

                if (kind == InteractionType.Sleep)
                {
                    ClaimLyingFootprint(world, npc, spot);
                }
            }

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{kind} on the ground Duration={durationTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // Spec 29C.9: comfort/energy recover gradually while she rests — the
        // whole point of "you can watch it fill", not a jump on standing up.
        var restShare = durationTicks > 0 ? 1f / durationTicks : 1f;
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfort * restShare);
        npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + energy * restShare);

        var interruptedSleep = kind == InteractionType.Sleep && HasSleepInterrupt(world, npc);
        if (!interruptedSleep && npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        if (interruptedSleep)
        {
            Trace.Emit(world, npc.Id, "SleepInterrupted",
                $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                $"Danger={npc.Memory.Dangers.Count}");
        }

        // Spec §49: sleep the night in ONE continuous lie. Instead of ending the
        // block, standing (wake-grace + get-up clip), re-planning a spot and
        // dropping back down — the "empty get-up" churn that was 57% of night
        // get-ups — re-arm the block in place, holding the footprint claim. She
        // only truly wakes when rested enough, dawn breaks, or a real need
        // (hunger/thirst/cold/danger) crosses its threshold and the decision
        // system takes over.
        if (kind == InteractionType.Sleep && ShouldKeepSleeping(world, npc))
        {
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;
            Trace.Emit(world, npc.Id, "SleepContinued",
                $"Energy={npc.Needs.Energy:F2} Comfort={npc.Needs.Comfort:F2}");
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, kind == InteractionType.Sleep ? "GroundSleptWell" : "GroundSatDown",
            $"Comfort+{comfort:F2} Energy+{energy:F2}");

        // Spec 41.5: wake up standing still for a beat — no sprinting off
        // the grass; the get-up clip plays out during the grace.
        if (kind == InteractionType.Sleep)
        {
            npc.Mind.WakeGraceUntilTick = world.Tick + 12;
        }

        if (kind == InteractionType.Sit)
        {
            npc.Mind.Cooldowns.Add(new GoalCooldown
            {
                Goal = GoalType.Sit,
                EndTick = world.Tick + 240
            });
        }

        // Canonical cycle reset (same as InteractionCompleted): Execution
        // back to None or the next interaction's start gate never opens.
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (ground rest done)");
    }

    // Spec §49: should a finished sleep block re-arm in place (keep lying)
    // rather than stand and re-plan? Yes while she is still tired (nearly full
    // energy at night, or genuinely spent by day) AND no real need has crossed
    // its action threshold — the same thresholds at which Eat/Drink/Dress
    // become attractive, so she wakes exactly when there is something to do.
    private static float SleepWakeEnergyDay => SimBalance.SleepEnergyThreshold;
    private static float SleepInterruptHunger => SimBalance.SleepInterruptHunger;
    private static float SleepInterruptThirst => SimBalance.SleepInterruptThirst;
    // NOTE: cold is deliberately NOT a wake trigger — mild cold at night is the
    // norm and she usually can't fix it, so waking just produced the "empty
    // get-up" churn; sleeping through it is what a real body does (§49.1).
    private static bool ShouldKeepSleeping(WorldState world, NPCState npc)
    {
        if (!Spec49.Rearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the sleep — then the decision
        // system takes over. Everything else: keep lying. NOTE: cold is NOT a
        // wake trigger — a near-naked girl on a 6° night sits at max thermal
        // discomfort she usually can't fix, so waking her only produced the
        // "empty get-up" churn; the cold HP hit lands whether she's up or lying,
        // and lying still conserves. (A fire she could tend is a daytime chore.)
        if (HasSleepInterrupt(world, npc))
        {
            return false;
        }

        // Spec §49: sleep THROUGH the night in one lie (the user's ask — "let
        // them sleep more") — no energy cap after dark. By day, only nap while
        // genuinely tired.
        var night = world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
        return night || npc.Needs.Energy < SleepWakeEnergyDay;
    }

    // §49-parity: the DECISION layer reads this too — going to sleep while an
    // interrupt condition is already true produced the lie-down/stand-up loop
    // (Molly, thirst 0.79 ≥ 0.6: the first sleep tick woke her, the auction
    // put her right back to bed, forever).
    internal static bool HasSleepInterrupt(WorldState world, NPCState npc) =>
        world.Tick < npc.Mind.AdrenalineUntilTick ||
        npc.Memory.Dangers.Count > 0 ||
        npc.Needs.Hunger >= SleepInterruptHunger ||
        npc.Needs.Thirst >= SleepInterruptThirst;

    // Spec 35.4: dwell in the shade / shallows shedding heat. This is the
    // cool-off twin of RunGroundRest — a timed in-place interaction with no
    // object and no comfort/energy payoff; the cooling itself is delivered by
    // TemperatureSystem because the plan parked her on a genuinely cool tile
    // (shaded or water). At the end of each beat it re-arms in place (like the
    // sleep re-arm) until she has actually cooled, a more urgent need crosses,
    // or the safety cap trips — instead of completing→None and re-winning the
    // goal at zero margin every tick (the old None→CoolOff churn, ~40% of all).
    private static void RunGroundCool(WorldState world, NPCState npc, PlanStep step)
    {
        var bathing = npc.Plan.Goal == GoalType.Bathe;
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);
            }

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{(bathing ? "Bathe" : "CoolOff")} Duration={Spec49.CoolOffDwellTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Re-arm the dwell in place (hold the junction occupancy + reservation)
        // while still hot and nothing more urgent calls — bounded by CoolOffMaxRearms
        // so a fallback tile that never actually cools can't freeze her here forever.
        if (Spec49.CoolRearm &&
            npc.Mind.CoolRearmCount < Spec49.CoolOffMaxRearms &&
            (bathing ? ShouldKeepBathing(npc) : ShouldKeepCooling(world, npc)))
        {
            npc.Mind.CoolRearmCount++;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;
            Trace.Emit(world, npc.Id, bathing ? "BatheContinued" : "CoolContinued",
                $"Rearm={npc.Mind.CoolRearmCount} Hygiene={npc.Needs.Hygiene:F2} " +
                $"ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2}");
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, bathing ? "Bathed" : "CooledOff",
            $"Hygiene={npc.Needs.Hygiene:F2} ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2} " +
            $"Rearms={npc.Mind.CoolRearmCount}");

        // A short refractory window so she doesn't instantly re-select CoolOff even
        // if discomfort still hovers just under the clear edge (mirrors Sit's 240t).
        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = bathing ? GoalType.Bathe : GoalType.CoolOff,
            EndTick = world.Tick + SimBalance.CoolOffSettleTicks
        });

        // Canonical cycle reset (same as RunGroundRest / InteractionCompleted).
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.CoolRearmCount = 0;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (cool-off done)");
    }

    private static bool ShouldKeepBathing(NPCState npc) =>
        npc.Needs.Hygiene < 0.95f || EquipmentMath.AverageDirtiness(npc) > 0.05f;

    private static void RunPrepareBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } shore || !current.Equals(shore))
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.Undress)
        {
            var garment = npc.Execution.HeldGarment;
            if (garment is null && npc.Plan.TargetItemDefinitionId is { } definitionId)
            {
                for (var i = npc.WornItems.Count - 1; i >= 0; i--)
                {
                    if (npc.WornItems[i].DefinitionId == definitionId)
                    {
                        garment = npc.WornItems[i];
                        break;
                    }
                }
            }

            if (garment is null)
            {
                PlanInterruption.Abort(world, npc, "Bathe: active garment disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)(world.Tick - npc.Execution.StartTick) / total : 1f;
            if (npc.Execution.HeldGarment is null && progress >= WardrobeHandoffFraction)
            {
                npc.WornItems.Remove(garment);
                npc.Execution.HeldGarment = garment;
                EquipmentMath.Recalculate(world, npc);
            }

            if (world.Tick < npc.Execution.EndTick)
            {
                return;
            }

            npc.WornItems.Remove(garment);
            DropGarmentWithContents(world, npc, garment);
            npc.Execution.HeldGarment = null;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Plan.TargetItemDefinitionId = null;
            EquipmentMath.Recalculate(world, npc);
            return;
        }

        if (npc.WornItems.Count > 0)
        {
            var garment = npc.WornItems[npc.WornItems.Count - 1];
            npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            npc.Execution.HeldGarment = null;
            return;
        }

        Junction swim = null;
        var bestDistance = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Water) ||
                !Connectivity.Reachable(world, shore, junction.Id))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                swim = junction;
            }
        }

        if (swim is null)
        {
            PlanInterruption.Abort(world, npc, "Bathe: no reachable water junction");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        SpatialMutations.ReleaseJunctionReservation(world, shore, npc.Id);
        npc.Plan.TargetJunctionId = swim.Id;
        npc.Plan.TargetTile = swim.Tiles[0];
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = swim.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.SwimBathe, TargetJunction = swim.Id });
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        Trace.Emit(world, npc.Id, "BatheReady", $"Naked; swimming to {swim.Id.Value}");
    }

    private static void RunSwimBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target || !current.Equals(target))
        {
            return;
        }

        if (npc.WornItems.Count > 0)
        {
            PlanInterruption.Abort(world, npc, "Bathe requires complete undressing");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + SimBalance.BatheDurationTicks;
            Trace.Emit(world, npc.Id, "BatheStarted",
                $"Duration={SimBalance.BatheDurationTicks} ticks (one game hour)");
            return;
        }

        npc.Needs.Hygiene = MathUtil.Clamp01(npc.Needs.Hygiene +
            1f / SimBalance.BatheDurationTicks);
        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        npc.Needs.Hygiene = 1f;
        FinishPersonalCare(world, npc, target, GoalType.Bathe, "Bathed");
    }

    private static void RunWashClothes(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target || !current.Equals(target))
        {
            return;
        }

        if (npc.Plan.TargetObjectId is not { } objectId ||
            !world.Entities.Objects.TryGetValue(objectId, out var garment) ||
            !world.Junctions.Items.TryGetValue(target, out var edge) ||
            !PlanningSystem.TryGetEdgeSeatGeometry(
                world, edge, waterOnly: true, out var standTile, out var facing))
        {
            SpatialMutations.FreeJunction(world, target, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            PlanInterruption.Abort(world, npc, "WashClothes edge or garment disappeared");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Hold the exact edge midpoint and its water-facing normal throughout
        // the gathering clip; other systems cannot slowly turn the washer away.
        PlaceAtEdge(world, npc, edge, standTile, facing);

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (!SpatialMutations.TryReserveJunction(world, target, npc.Id, world.Tick,
                    SimBalance.WashClothesDurationTicks + 8))
            {
                PlanInterruption.Abort(world, npc, "WashClothes edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            SpatialMutations.OccupyJunction(world, target, npc.Id);
            garment.Wetness = 1f;
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.WashClothes;
            npc.Execution.TargetObject = objectId;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + SimBalance.WashClothesDurationTicks;
            npc.Execution.HeldGarment = new ItemInstance(garment.DefinitionId)
            {
                Wetness = 1f,
                Durability = garment.Durability,
                Dirtiness = garment.Dirtiness,
                Bloodiness = garment.Bloodiness
            };
            return;
        }

        // Dirtiness is the combined contamination score. Bloodiness remains a
        // separate visual layer, but fades over the same washing progress.
        var remainingTicks = System.Math.Max(0, npc.Execution.EndTick - world.Tick);
        var retainedContamination = remainingTicks / (remainingTicks + 1f);
        garment.Dirtiness = MathUtil.Clamp01(garment.Dirtiness * retainedContamination);
        garment.Bloodiness = MathUtil.Clamp01(garment.Bloodiness * retainedContamination);
        garment.Wetness = 1f;
        if (npc.Execution.HeldGarment is { } held)
        {
            held.Dirtiness = garment.Dirtiness;
            held.Bloodiness = garment.Bloodiness;
            held.Wetness = garment.Wetness;
        }
        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        garment.Dirtiness = 0f;
        garment.Bloodiness = 0f;
        garment.Wetness = 1f;
        npc.Execution.HeldGarment = null;
        SpatialMutations.FreeJunction(world, target, npc.Id);
        FinishPersonalCare(world, npc, step.TargetJunction, GoalType.WashClothes,
            $"ClothesWashed {garment.DefinitionId} (fully wet)");
    }

    private static void FinishPersonalCare(WorldState world, NPCState npc, JunctionId? junction,
        GoalType goal, string trace)
    {
        if (junction is { } occupied)
        {
            SpatialMutations.ReleaseJunctionReservation(world, occupied, npc.Id);
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.Cooldowns.Add(new GoalCooldown { Goal = goal, EndTick = world.Tick + 40 });
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        Trace.Emit(world, npc.Id, trace, $"Hygiene={npc.Needs.Hygiene:F2}");
    }

    // Spec 35.4: should the finished cool-off beat re-arm in place? Yes while she
    // is still hot AND no more-urgent need/threat has crossed its threshold —
    // reusing the sleep-interrupt thresholds so she leaves the shade exactly when
    // there's something better to do. Stops once cooled below the clear edge.
    private static bool ShouldKeepCooling(WorldState world, NPCState npc)
    {
        if (!Spec49.CoolRearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the dwell — the decision system
        // then takes over (she'll re-pick CoolOff only if still overheated and the
        // settle cooldown has lapsed).
        if (npc.Memory.Dangers.Count > 0 ||
            npc.Needs.Hunger >= SleepInterruptHunger ||
            npc.Needs.Thirst >= SleepInterruptThirst)
        {
            return false;
        }

        // Cooled enough: both the heat discomfort and the sun-exposure meter have
        // fallen below their clear edges (hysteresis vs the 0.35 entry).
        if (npc.Needs.ThermalDiscomfort < SimBalance.CoolOffClearThreshold &&
            npc.SunExposure < SimBalance.CoolOffSunClear)
        {
            return false;
        }

        return true;
    }

    // Spec 29G: the lying body covers junctions within half a hex radius.
    internal static void ClaimLyingFootprint(WorldState world, NPCState npc, JunctionId center)
    {
        npc.ClaimedJunctions.Clear();
        if (!world.Junctions.Items.TryGetValue(center, out var origin))
        {
            return;
        }

        var radius = HexSpatialMath.HexRadius * 0.5f;
        var radiusSq = radius * radius;
        foreach (var coord in origin.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || junction.Blocked)
                {
                    continue;
                }

                var dx = junction.WorldPosition.X - origin.WorldPosition.X;
                var dy = junction.WorldPosition.Y - origin.WorldPosition.Y;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    npc.ClaimedJunctions.Add(junctionId);
                }
            }
        }
    }

    internal static void ReleaseClaims(WorldState world, NPCState npc)
    {
        npc.ClaimedJunctions.Clear();
    }

    private static void PlaceAtEdge(
        WorldState world, NPCState npc, Junction edge, TileCoord standTile, Float2 facing)
    {
        if (npc.Tile != standTile)
        {
            var previous = npc.Tile;
            npc.Tile = standTile;
            SpatialMutations.MoveEntityToTile(world, npc.Id, previous, standTile);
        }

        npc.Position = edge.WorldPosition;
        npc.Movement.DesiredDirection = facing;
        npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(facing);
        npc.RotationDegrees = npc.Movement.DesiredRotationDegrees;
    }

    private static void RunUndressItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (itemId is null || !npc.WornItems.Contains(itemId))
            {
                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "ExecFailed",
                    $"UndressItem: '{itemId ?? "-"}' is not worn");
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.TargetObject = null;
            npc.Execution.HeldGarment = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Undress -> {itemId} Duration={UndressDurationTicks}ticks");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            // §Wardrobe-anim beat 1 -> 2: at the handoff the piece comes OFF the
            // body and INTO the hand — warmth/armor drop here — but it isn't laid
            // on the floor until the gather beat finishes (see below).
            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var elapsed = world.Tick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)elapsed / total : 1f;
            if (npc.Execution.HeldGarment is null &&
                progress >= WardrobeHandoffFraction &&
                itemId is not null && npc.WornItems.Contains(itemId))
            {
                var doffed = npc.WornItems.Find(i => i.DefinitionId == itemId) ??
                    new ItemInstance(itemId);
                npc.WornItems.Remove(doffed);
                EquipmentMath.Recalculate(world, npc);
                npc.Execution.HeldGarment = doffed;
                Trace.Emit(world, npc.Id, "GarmentInHand",
                    $"Undress {itemId} doffed to hand " +
                    $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
            }

            if (npc.Execution.EndTick - world.Tick > 0)
            {
                return;
            }

            // Beat 2 finished: the garment gathered up off the body lands on
            // the floor (preserving the doffed instance's wetness/durability).
            var wornItem = npc.Execution.HeldGarment ??
                npc.WornItems.Find(i => i.DefinitionId == itemId) ??
                new ItemInstance(itemId);
            npc.WornItems.Remove(wornItem);
            npc.Execution.HeldGarment = null;
            EquipmentMath.Recalculate(world, npc);
            // Spec §52: the garment carries down whatever pocket items no longer
            // fit — they wait inside it on the ground, retrievable later.
            DropGarmentWithContents(world, npc, wornItem);

            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            Trace.Emit(world, npc.Id, "ItemUndressed",
                $"{itemId} dropped at Tile={npc.Tile.Q},{npc.Tile.R} " +
                $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed (undressed)");
        }
    }

    private static void RunDropInventoryItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", "DropInventoryItem: no target item");
            return;
        }

        ItemInstance? item = null;
        foreach (var carried in npc.Inventory.Items)
        {
            if (carried.DefinitionId == itemId)
            {
                item = carried;
                break;
            }
        }

        if (item is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", $"DropInventoryItem: '{itemId}' not in inventory");
            return;
        }

        npc.Inventory.Items.Remove(item);
        var dropped = DropItemAtFeet(world, npc, item);
        if (dropped is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", $"DropInventoryItem: no drop junction for '{itemId}'");
            return;
        }

        npc.Plan.TargetObjectId = dropped.Id;
        npc.Plan.TargetTile = dropped.Tile;
        npc.Plan.TargetJunctionId = dropped.Junctions.Count > 0 ? dropped.Junctions[0] : npc.CurrentJunction;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.Steps.RemoveAt(0);
        npc.Plan.CurrentStepIndex = 0;

        foreach (var step in npc.Plan.Steps)
        {
            if (step.Type == PlanStepType.Interact)
            {
                step.TargetObject = dropped.Id;
                step.TargetJunction = npc.Plan.TargetJunctionId;
            }
        }

        Trace.Emit(world, npc.Id, "ItemDropped",
            $"{itemId} placed on ground Obj={dropped.Id.Value} for {npc.Plan.Goal}");
    }

    // In-place consumption from inventory (spec 29B.3): no world object,
    // no junction reservation, executable wherever the NPC stands.
    private static void RunConsumeInventoryItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null ||
            !world.Content.ObjectDefinitions.TryGetValue(itemId, out var itemDefinition))
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: item '{itemId ?? "-"}' not in inventory or unknown definition");
            return;
        }

        // §55: in-place consume now covers both Eat (food) and Drink (crack a
        // coconut). The verb comes from the plan step, so one handler serves
        // both; a Drink can carry Yields (the opened-coconut husk).
        var verb = npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Interaction.HasValue
            ? npc.Plan.Steps[0].Interaction.Value
            : InteractionType.Eat;
        var interaction = ResolveInteraction(itemDefinition, verb);
        if (interaction is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: '{itemId}' has no {verb} interaction");
            return;
        }

        var item = FindConsumableInventoryItem(npc, itemId, verb, itemDefinition);
        if (item is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: '{itemId}' not available for {verb}");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = verb;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + interaction.DurationTicks;

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{verb} (inventory) -> {itemId} " +
                $"Duration={interaction.DurationTicks}ticks ({interaction.DurationTicks * world.TickDeltaTime:F1}s) " +
                $"EndTick={npc.Execution.EndTick} " +
                $"Effects=[H={interaction.Effects.HungerDelta:+0.00;-0.00} " +
                $"T={interaction.Effects.ThirstDelta:+0.00;-0.00} " +
                $"C={interaction.Effects.ComfortDelta:+0.00;-0.00}]");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            if (remaining > 0)
            {
                var progress = total > 0 ? 1f - (float)remaining / total : 1f;
                // Spec 29C.9: hunger drops mouthful by mouthful, not in a jump.
                if (total > 0)
                {
                    ApplyEffectsScaled(npc, interaction.Effects, 1f / total);
                }

                if (SimTrace.Verbose)
                {
                    Trace.Emit(world, npc.Id, "ExecProgress",
                        $"{verb} (inventory) Progress={progress:P0} " +
                        $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                }

                return;
            }

            var needsBefore = Trace.FormatNeeds(npc.Needs);
            ApplyEffectsScaled(npc, interaction.Effects, total > 0 ? 1f / total : 1f);
            if (IsPortableCoconutDrink(itemDefinition, verb))
            {
                item.ResourceAmount = System.MathF.Max(0f, item.ResourceAmount - 1f);
            }
            else
            {
                npc.Inventory.Items.Remove(item);
                // §55: a consumed item may transform rather than vanish — cracking a
                // coconut (Drink) yields the opened husk straight into the hand.
                foreach (var yield in interaction.Yields)
                {
                    for (var n = 0; n < yield.Count; n++)
                    {
                        npc.Inventory.Items.Add(yield.DefinitionId);
                    }
                }
            }
            var needsAfter = Trace.FormatNeeds(npc.Needs);

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;

            Trace.Emit(world, npc.Id, "ItemConsumed",
                $"{itemId} {verb} from inventory " +
                $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}] " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]" +
                (IsPortableCoconutDrink(itemDefinition, verb)
                    ? $" CoconutWaterLeft={item.ResourceAmount:F0}"
                    : string.Empty));

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (ate from inventory)");
        }
    }

    private static ItemInstance? FindConsumableInventoryItem(
        NPCState npc,
        string definitionId,
        InteractionType verb,
        ObjectDefinition definition)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId != definitionId)
            {
                continue;
            }

            if (IsPortableCoconutDrink(definition, verb) && item.ResourceAmount <= 0f)
            {
                continue;
            }

            return item;
        }

        return null;
    }

    private static bool IsPortableCoconutDrink(ObjectDefinition definition, InteractionType verb) =>
        verb == InteractionType.Drink && definition.Tags.Contains("CoconutWater");

    // Spec 29H: drink in place from the carried bottle — thirst quenched,
    // raw water carries the 30 % sickness roll, then the bottle empties.
    private static int DrinkBottleDurationTicks => SimBalance.DrinkBottleDurationTicks;
    // Spec §49: raw-water gut-rot. A bout adds SicknessDamagePerBout to the
    // torso-damage budget (matches the old instant lump) and shows the 🤢 icon
    // for SicknessDurationTicks (~6 game-hours). The budget is capped so
    // overlapping bouts can't grind the torso into the ground.
    private static int SicknessDurationTicks => SimBalance.SicknessDurationTicks;
    private static float SicknessDamagePerBout => SimBalance.SicknessDamagePerBout;
    private static float SicknessDamageBudgetCap => SimBalance.SicknessDamageBudgetCap;

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

    private static void RunDrinkBottle(WorldState world, NPCState npc)
    {
        if (npc.BottleWater == WaterKind.None)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", "DrinkBottle: the bottle is empty");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Drink;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + DrinkBottleDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Drink (bottle:{npc.BottleWater}) Duration={DrinkBottleDurationTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // A bottleful is a real drink: relief raw 0.7 / boiled 0.85 (so the
        // two-step chain matches the old single drink, 29H). Spec 29C.9: the
        // thirst drops gulp by gulp across the duration, not in one jump.
        var boiled = npc.BottleWater == WaterKind.Boiled;
        var thirstTotal = boiled ? SimBalance.DrinkThirstBoiled : SimBalance.DrinkThirstRaw;
        var comfortTotal = boiled ? SimBalance.DrinkComfortBoiled : 0f;
        var share = 1f / DrinkBottleDurationTicks;
        npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst - thirstTotal * share);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfortTotal * share);

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Spec 29H: raw water is a gamble — sickness roll (moved here from
        // the old water-edge Drink now that filling and drinking are split).
        // §45 r4: eased 30%/-0.15 -> 15%/-0.08. The colony drinks raw
        // 70-105 times per 15 days (boiled is ~2% of drinks — the fire is
        // dead ~90% of the time), so the old odds ground through 3-5 full
        // torsos per soak: half of all deaths were "Torso destroyed by
        // sickness". The gamble stays (chronic cough), but expected damage
        // (~0.012/drink) now sits within fed-regen's budget instead of
        // being a guaranteed death sentence for a fireless colony.
        if (!boiled)
        {
            var sickRoll = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 833);
            if (sickRoll < SimBalance.RawWaterSickChance && !Spec49.SickDoT)
            {
                // Baseline path (pre-§49): instant lump — torso -0.08 (floor 0.1,
                // only above 0.2) + comfort -0.2. Kept for harness A/B bisect.
                var beforeTorso = npc.Body.Parts[BodyPart.Torso];
                if (npc.Body.Parts[BodyPart.Torso] > 0.2f)
                {
                    npc.Body.Parts[BodyPart.Torso] =
                        System.Math.Max(0.1f, npc.Body.Parts[BodyPart.Torso] - 0.08f);
                }
                npc.Health = npc.Body.Mean();
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.2f);
                if (npc.Body.VitalDestroyed(out var sickVital0))
                {
                    npc.Health = 0f;
                    Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"{sickVital0} destroyed by sickness");
                }
                DamageReactionSystemHelpers.GrantAdrenaline(
                    world, npc, beforeTorso - npc.Body.Parts[BodyPart.Torso], "Sickness");
                Trace.Emit(world, npc.Id, "GotSick", $"Raw water (Roll={sickRoll:F2}) instant");
            }
            else if (sickRoll < SimBalance.RawWaterSickChance)
            {
                // Spec §49: don't lump the harm here. Add a BOUNDED torso-damage
                // budget the DoT pays down over the next hours (visible 🤢), and
                // open the icon/malaise window. The budget cap is the key: a
                // thirsty colony drinking raw back-to-back opens overlapping
                // windows, and without the cap the DoT would grind continuously
                // (this exact bug wiped seed 12345). Capped, total harm ≈ the old
                // instant -0.08 model, so the §45/§46 balance holds.
                npc.Mind.SicknessDamageRemaining = System.Math.Min(
                    SicknessDamageBudgetCap, npc.Mind.SicknessDamageRemaining + SicknessDamagePerBout);
                npc.Mind.SickUntilTick =
                    System.Math.Max(npc.Mind.SickUntilTick, world.Tick) + SicknessDurationTicks;
                Trace.Emit(world, npc.Id, "GotSick",
                    $"Raw water (Roll={sickRoll:F2}) DmgBudget={npc.Mind.SicknessDamageRemaining:F2}");
            }
        }

        // Spec §52: spend one gulp; the bottle only empties when the last is gone.
        npc.BottleCharges--;
        var driedOut = npc.BottleCharges <= 0;
        Trace.Emit(world, npc.Id, "DrankBottle",
            $"{(boiled ? "Boiled" : "Raw")} water Thirst={npc.Needs.Thirst:F2} Left={System.Math.Max(0, npc.BottleCharges)}");
        if (driedOut)
        {
            npc.BottleWater = WaterKind.None;
            npc.BottleCharges = 0;
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (drank from bottle)");
    }

    private static void ApplyEffects(NPCState npc, InteractionEffects effects)
    {
        ApplyEffectsScaled(npc, effects, 1f);
    }

    // Spec 29C.9: apply a fraction of an interaction's effect — used to drip
    // the need relief gradually across the action's duration (Sims-style).
    private static void ApplyEffectsScaled(NPCState npc, InteractionEffects effects, float k)
    {
        npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger + effects.HungerDelta * k);
        npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst + effects.ThirstDelta * k);
        npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + effects.EnergyDelta * k);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + effects.ComfortDelta * k);
        npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + effects.ThermalDelta * k);
        // Spec 31A.5A: warmth/armor are no longer touched here — they are
        // recomputed from the worn-items list by EquipmentMath.
    }

    private static InteractionType? GetPlannedInteractionType(NPCPlanState plan)
    {
        var start = plan.CurrentStepIndex;
        if (start < 0) start = 0;
        if (start > plan.Steps.Count) start = plan.Steps.Count;
        for (var i = start; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Type == PlanStepType.Interact && step.Interaction.HasValue)
            {
                return step.Interaction;
            }
        }

        return null;
    }

    private static InteractionDefinition? ResolveInteraction(ObjectDefinition definition, InteractionType? type)
    {
        if (type is null)
        {
            return definition.Interactions.Count > 0 ? definition.Interactions[0] : null;
        }

        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == type.Value)
            {
                return interaction;
            }
        }

        return null;
    }
}

}
