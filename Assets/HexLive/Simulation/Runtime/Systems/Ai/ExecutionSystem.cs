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

public sealed partial class ExecutionSystem : ISimulationSystem
{
    public string Name => nameof(ExecutionSystem);

    public TickLayer Layer => TickLayer.Fast;

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

    // Spec §49: raw-water gut-rot. A bout adds SicknessDamagePerBout to the
    // torso-damage budget (matches the old instant lump) and shows the 🤢 icon
    // for SicknessDurationTicks (~6 game-hours). The budget is capped so
    // overlapping bouts can't grind the torso into the ground.
    private static int SicknessDurationTicks => SimBalance.SicknessDurationTicks;

    private static float SicknessDamagePerBout => SimBalance.SicknessDamagePerBout;

    private static float SicknessDamageBudgetCap => SimBalance.SicknessDamageBudgetCap;

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
