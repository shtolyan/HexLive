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

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.TreatSelf)
            {
                RunTreatSelf(world, npc);
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

            if (lastStep is { Type: PlanStepType.RedressAfterBathe })
            {
                RunRedressAfterBathe(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.GroundSit or PlanStepType.GroundSleep or PlanStepType.GroundCool })
            {
                RunGroundRestPlan(world, npc, lastStep);
                continue;
            }

            // §80: маршрутизация по ЦЕЛИ, а не по «раз есть агент — значит
            // разговор». Прежняя развилка была «Aid или всё остальное = Talk», и
            // §72 в неё провалился: план налёта тоже носит TargetAgentId, так что
            // налётчик приходил к своей жертве и запускал против неё
            // InteractionType.Talk — с обменом репликами и РОСТОМ симпатии,
            // пока RaidSystem этажом ниже вёл с ней бой.
            //
            // У налёта взаимодействия нет вовсе: план — это дорога, а удары
            // выдаёт RaidSystem. Поэтому он проваливается ниже, в RunMoveOnly.
            if (npc.Plan.TargetAgentId is not null &&
                npc.Mind.CurrentGoal != GoalType.Raid)
            {
                if (npc.Mind.CurrentGoal == GoalType.Aid)
                {
                    RunAid(world, npc);
                }
                else if (npc.Mind.CurrentGoal == GoalType.Abuse)
                {
                    RunAbuse(world, npc);
                }
                // §111: обыск лежащего. Ветка обязана стоять ДО фолбэка на
                // Talk — по той же причине, по которой её понадобилось заводить
                // налёту: план тоже носит TargetAgentId, и без неё лутер
                // «разговаривал» бы с телом без сознания.
                else if (npc.Mind.CurrentGoal == GoalType.LootHelpless)
                {
                    RunLootHelpless(world, npc);
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
                // Spec 26.3 r2: a Blocked route must FAIL the plan, not fall
                // through — with an empty JunctionPath the old gate started the
                // interaction from wherever she stood (campfires hammered up
                // from across the camp). Mirrors RunGroundRestPlan.
                if (npc.Movement.Status == MovementStatus.Blocked)
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Target {worldObject.DefinitionId} unreachable (path blocked)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec 26.3 r2: arrival is literal — standing ON the plan's
                // junction. "Not currently walking" is no proof she got there.
                if (npc.Plan.TargetJunctionId is { } wantJunction &&
                    (npc.CurrentJunction is not { } atJunction || !atJunction.Equals(wantJunction)))
                {
                    continue; // PathfindingSystem re-routes next tick
                }

                // Distance guarantee (user: craft/harvest/build must all happen at
                // the smallest hop, never a whole hex out). The literal gate above
                // proves she stands ON the reserved junction; this proves that
                // junction actually hugs the object — within its footprint plus one
                // sub-grid step, AND on the same side of every impassable border
                // (§26.6A r4: a cliff face or hut wall is only ~0.75 wu thick, so
                // straight-line distance alone let her work through it). A spot
                // that fails either test means the object is walled/cliffed off and
                // was being reached ACROSS the gap, so fail + retarget instead of
                // interacting from afar. Belt-and-braces over the planner cap:
                // covers remembered targets and every interaction kind.
                if (!InteractionReach.CheckObjectStart(world, npc, worldObject,
                        definition.ObstacleRadius))
                {
                    npc.Memory.Shun(worldObject.Id, world.Tick + AiBalance.ShunTicks);
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Target {worldObject.DefinitionId} not adjacently reachable (too far to interact)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

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
                    // Jul 2026: remember the contested object briefly so the
                    // next plan reaches for a DIFFERENT source (e.g. pierce a
                    // fresh coconut) instead of re-targeting this one forever.
                    npc.Memory.Shun(worldObject.Id, world.Tick + AiBalance.ShunTicks);
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
                        DecisionSystem.CountInventory(npc, ContentIds.Log) >= bill.Logs &&
                        DecisionSystem.CountInventory(npc, ContentIds.Stone) >= bill.Stones &&
                        DecisionSystem.CountInventory(npc, ContentIds.PalmLeaf) >= bill.Leaves;
                    if (!billOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc, "Cannot build (materials missing or hut done)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 35.2: trees need an axe or saw; boulders need the pickaxe.
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
                    var hasWoodNow = npc.Inventory.Items.Contains(ContentIds.Stick);
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
                    var canFrictionLight = npc.Mind.NightSleepUntilRested ||
                        npc.Needs.ThermalComfort < -0.35f ||
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

                // Face the work (spec 26.3 r2): builds/fuel run from the rim of
                // the site's blocked footprint — hammering while looking away
                // read as detached. Skip when she stands ON the anchor (seats,
                // beds): a zero-length direction has no meaningful angle.
                var anchorPosition = worldObject.Junctions.Count > 0 &&
                    world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchorJunction)
                        ? anchorJunction.WorldPosition
                        : HexSpatialMath.TileToWorld(worldObject.Tile);
                if (HexSpatialMath.Distance(anchorPosition, npc.Position) > 0.05f)
                {
                    var faceDirection = HexSpatialMath.Normalize(new Float2(
                        anchorPosition.X - npc.Position.X, anchorPosition.Y - npc.Position.Y));
                    npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(faceDirection);
                    npc.RotationDegrees = npc.Movement.DesiredRotationDegrees;
                }

                // §66.5 + §111.9 r3: the renderer pins a sleeping body to the
                // bed's centre and rotation. Mirror that authored pose in the
                // simulation before anybody targets the sleeper for aid/loot;
                // the free rim junction remains only the route/occupancy
                // anchor, never the visible body's position or direction.
                if (interaction.Type == InteractionType.Sleep)
                {
                    LyingSpot.AlignBodyToObject(world, npc, worldObject, anchorPosition);
                }

                npc.Execution.Status = ExecutionStatus.InProgress;
                npc.Execution.CurrentInteraction = interaction.Type;
                npc.Execution.TargetObject = worldObject.Id;
                npc.Execution.StartTick = world.Tick;
                npc.Execution.BuildDeposited = false; // §77: this visit still owes its load
                // §76 + §79: how long the job takes — first the TOOL doing it
                // (§79: the gear that carries the required capability sets the
                // pace: machete 2 → half, stone axe 1 → authored, knife 0.75 →
                // longer), then her own hands (Strength/Wits + the learned
                // trade). Both stages round instead of truncating, and at
                // multiplier 1f both are exactly the authored number.
                var toolSpeedMult = Content.GearCatalog.BestSpeedMultFor(
                    npc.Inventory.Items,
                    Content.GearCatalog.RequiredCapabilities(interaction, definition));
                var workTicks = AttributeMath.WorkTicks(
                    npc, Content.GearCatalog.ScaleTicks(interaction.DurationTicks, toolSpeedMult),
                    interaction.Type, npc.Plan.Goal);
                npc.Execution.EndTick = world.Tick + workTicks;
                worldObject.IsOccupied = true;
                // Spec 28.15C: a corpse's CurrentUser records whose body it
                // is — mourning must not overwrite it.
                if (!CorpseMath.IsHumanDead(definition))
                {
                    worldObject.CurrentUser = npc.Id;
                }
                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.OccupyJunction(world, jId, npc.Id);
                }

                Trace.Emit(world, npc.Id, "InteractionStarted",
                    $"{interaction.Type} -> {worldObject.DefinitionId} " +
                    // §79: the AUTHORED duration and what the tool in her hands
                    // actually made of it — a soak must be able to see that the
                    // machete really did halve the job.
                    $"Duration={workTicks}ticks ({workTicks * world.TickDeltaTime:F1}s) " +
                    $"Authored={interaction.DurationTicks} Tool=x{toolSpeedMult:0.##} " +
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
                // Also repair/resynchronise a sleep restored from a save made
                // before §111.9 r3. The bed pose is an invariant for the whole
                // interaction, not only a one-shot adjustment at its start.
                if (npc.Execution.CurrentInteraction == InteractionType.Sleep)
                {
                    var sleepAnchor = worldObject.Junctions.Count > 0 &&
                        world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var sleepJunction)
                            ? sleepJunction.WorldPosition
                            : HexSpatialMath.TileToWorld(worldObject.Tile);
                    LyingSpot.AlignBodyToObject(world, npc, worldObject, sleepAnchor);

                    // §49 parity: object-backed sleep used to ignore the same
                    // critical wake conditions that ground sleep honors. Abort
                    // promptly, but retain NightSleepUntilRested so after the
                    // crisis is handled she returns to finish the recharge.
                    if (HasSleepInterrupt(world, npc, alreadyAsleep: true))
                    {
                        Trace.Emit(world, npc.Id, "SleepInterrupted",
                            $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                            $"Danger={npc.Memory.Dangers.Count}");
                        PlanInterruption.Abort(world, npc, "Critical need interrupted sleep");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

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

                    // §77: halfway through the deposit clip the carried
                    // materials go into the build-site's pile — her hand empties
                    // and the pile grows on the beat the animation stoops down,
                    // instead of both snapping at the very end of the visit.
                    if (npc.Execution.CurrentInteraction == InteractionType.Build &&
                        progress >= BuildHandoffFraction &&
                        BuildSiteMath.IsSite(worldObject))
                    {
                        RunFurnitureSiteHandoff(world, npc, worldObject);
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

                // §49.1 / §49.9: beds and leaf mats re-arm exactly like ground
                // sleep. Keep the bed claim and authored lying pose, and do not
                // run interaction completion (which would stand her up) until
                // the nightly full-energy condition or a critical interrupt.
                if (completedInteraction.Type == InteractionType.Sleep &&
                    ShouldKeepSleeping(world, npc))
                {
                    npc.Execution.StartTick = world.Tick;
                    npc.Execution.EndTick = world.Tick + completedInteraction.DurationTicks;
                    Trace.Emit(world, npc.Id, "SleepContinued",
                        $"Surface={worldObject.DefinitionId} Energy={npc.Needs.Energy:F2} " +
                        $"Comfort={npc.Needs.Comfort:F2}");
                    continue;
                }

                // Spec 29H: filling the bottle charges it (raw at a bank,
                // boiled at a lit campfire) — thirst is quenched only on Drink.
                if (!ApplyInteractionCompletion(
                        world, npc, worldObject, definition, completedInteraction, needsBefore))
                {
                    continue;
                }

                // §49.9 / bug #25: beds and ground sleep obey the SAME re-arm
                // rule. The ground path already continued in place; object
                // sleep used to complete here, stand up and re-plan after each
                // block. Keep the bed occupied and restart the authored block
                // until energy is full or a real sleep interrupt appears.
                if (completedInteraction.Type == InteractionType.Sleep &&
                    ShouldKeepSleeping(world, npc))
                {
                    var sleepBlockTicks = System.Math.Max(1, completedTotal);
                    npc.Execution.StartTick = world.Tick;
                    npc.Execution.EndTick = world.Tick + sleepBlockTicks;
                    Trace.Emit(world, npc.Id, "SleepContinued",
                        $"Object={worldObject.DefinitionId} Energy={npc.Needs.Energy:F2} " +
                        $"Comfort={npc.Needs.Comfort:F2}");
                    continue;
                }

                SkillTrace.Award(world, npc, completedInteraction.Type,
                    npc.Execution.EndTick - npc.Execution.StartTick);

                // Spec 41.5: waking from a bed = stand and come to your
                // senses for a beat before the next errand.
                if (completedInteraction.Type == InteractionType.Sleep)
                {
                    npc.Mind.WakeGraceUntilTick = world.Tick + AiBalance.WakeGraceTicks;
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

    // Эффекты завершённого взаимодействия — по одному рукаву на глагол.
    //
    // ⭐ Возвращает FALSE, если рукав отменил попытку. Раньше здесь стояло
    // `continue` пятью уровнями вложенности ниже, и оно относилось к внешнему
    // циклу ПО NPC: молча пропускало начисление навыка и весь сброс цикла в
    // сорока строках отсюда. Узнать об этом можно было, только проследив
    // метод целиком. Теперь это написано в сигнатуре.
    private static bool ApplyInteractionCompletion(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        if (completedInteraction.Type == InteractionType.FillBottle)
        {
            npc.BottleWater = definition.Tags.Contains("RawWater")
                ? WaterKind.Raw : WaterKind.Boiled;
            // Spec §52: one fill = several gulps; refill only when dry.
            npc.BottleCharges = SimBalance.BottleCapacity;
            Trace.Emit(world, npc.Id, "BottleFilled",
                $"{npc.BottleWater} x{npc.BottleCharges} from {worldObject.DefinitionId}");
        }

        // §54.15: park the carried empty bottle in the collector's
        // vessel slot — it becomes a world object on the collector's
        // junction (the drying-rack Hang idiom) and fills while it
        // rains (WaterCollectorSystem).
        if (completedInteraction.Type == InteractionType.PlaceVessel)
        {
            if (!CompletePlaceVessel(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
        }

        // §54.15: draw the collected rain. The bottleless placer (or
        // anyone, once the owner is dead) walks off with the bottle;
        // a housemate with her OWN empty bottle pours the water over
        // instead — the parked bottle stays and keeps collecting.
        if (completedInteraction.Type == InteractionType.TakeVessel)
        {
            if (!CompleteTakeVessel(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
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
                EndTick = world.Tick + AiBalance.SitCooldownTicks
            });
        }

        if (completedInteraction.Type == InteractionType.PickUp)
        {
            if (!CompletePickUp(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
        }
        else if (completedInteraction.Type == InteractionType.Dress)
        {
            if (!CompleteDress(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
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
                BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatRaw) +
                BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatCooked) <
                SimBalance.CampfireSpitCapacity)
            {
                ConsumeRecipeInputs(npc, npc.Plan.Goal);
                // ResourceAmount doubles as roast progress (ticks).
                worldObject.Contents.Add(new ItemInstance(ContentIds.MeatRaw));
                Trace.Emit(world, npc.Id, "MeatHungOnSpit",
                    $"food.meat_raw on the spit at Tile={worldObject.Tile.Q},{worldObject.Tile.R} " +
                    $"hanging raw={BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatRaw)} " +
                    $"cooked={BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatCooked)}");
            }
            else
            {
                Trace.Emit(world, npc.Id, "SpitHangFailed",
                    $"spitComplete={BuildSiteMath.CampfireSpitComplete(worldObject)} " +
                    $"hooksUsed={BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatRaw) + BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatCooked)}" +
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
                        PlaceCraftedFurniture(world, npc, worldObject, ContentIds.BedLeaf);
                        Trace.Emit(world, npc.Id, "BedCrafted", "A leaf sleeping-mat");
                        break;
                    case GoalType.CraftTent:
                        // Spec 40.14: 4 leaves woven into a shade canopy.
                        PlaceCraftedFurniture(world, npc, worldObject, ContentIds.Tent);
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
            var deposited = DecisionSystem.CountInventory(npc, ContentIds.Log);
            npc.Inventory.Items.RemoveAll(i => i.DefinitionId == ContentIds.Log);
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
            if (!CompleteHarvest(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
        }
        else if (completedInteraction.Type == InteractionType.Process)
        {
            if (!CompleteProcess(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
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
                return false;
            }
        }
        else if (completedInteraction.Type == InteractionType.Eat &&
                 worldObject.DefinitionId == ContentIds.CoconutOpen)
        {
            WorldObjectMutations.DespawnObject(world, worldObject.Id);
            Trace.Emit(world, npc.Id, "CoconutEaten", $"{worldObject.DefinitionId} consumed");
        }
        else if (completedInteraction.Type == InteractionType.Butcher)
        {
            if (!CompleteButcher(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
        }
        else if (completedInteraction.Type == InteractionType.Fuel)
        {
            // Spec 29E.3 / §54: one stick per fueling, half a day of fire.
            npc.Inventory.Items.Remove(ContentIds.Stick);
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
                 CorpseMath.IsHumanDead(definition))
        {
            // Spec 28.15C: closure — the mourning period ends early.
            npc.Mind.GrievingUntilTick = world.Tick;
            worldObject.IsOccupied = false; // owner (CurrentUser) preserved
            Trace.Emit(world, npc.Id, "Mourned",
                $"Paid respects to NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"}");
        }
        else if (completedInteraction.Type == InteractionType.Loot)
        {
            if (!CompleteLoot(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
        }
        else if (completedInteraction.Type == InteractionType.Hang)
        {
            if (!CompleteHang(world, npc, worldObject, definition,
                completedInteraction, needsBefore))
            {
                return false;
            }
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

        // §76: the ONE hook covering the whole world-object path —
        // harvesting, building, cooking at the fire, butchering, fire
        // tending, bottle filling. Paid on the ticks she ACTUALLY spent,
        // so getting faster at a trade also slows how fast she keeps
        // improving at it.

        return true;
    }

    // Рукава возвращают FALSE только чтобы ОТМЕНИТЬ попытку. True означает
    // «сделано, иди дальше»: после цепочки рукавов в ApplyInteractionCompletion
    // идёт безусловный хвост — освободить узел и эмитить InteractionCompleted, —
    // и пропускать его нельзя. Первый заход как раз пропускал: рукав делал
    // return, узлы переставали освобождаться, и golden поймал это расхождением
    // в решениях на 72-м тике.
    private static bool CompletePlaceVessel(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        var bottleItem = npc.Inventory.Items.Find(
            i => i.DefinitionId == WaterCollectorMath.VesselId);
        if (bottleItem is null || npc.BottleWater != WaterKind.None ||
            worldObject.Junctions.Count == 0 ||
            WaterCollectorMath.FindVessel(world, worldObject) is not null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
            Trace.Emit(world, npc.Id, "ExecFailed",
                "PlaceVessel: no empty bottle to park, or the slot is taken");
            return false;
        }

        npc.Inventory.Items.Remove(bottleItem);
        var vessel = WorldObjectMutations.SpawnObject(
            world, WaterCollectorMath.VesselId, npc.Fragment,
            worldObject.Tile, worldObject.Junctions[0]);
        vessel.Owner = npc.Id; // remembers whose bottle waits here
        vessel.ResourceAmount = 0f;
        Trace.Emit(world, npc.Id, "VesselPlaced",
            $"tool.bottle parked in collector {worldObject.Id.Value}");

        return true;
    }

    private static bool CompleteTakeVessel(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        var vessel = WaterCollectorMath.FindVessel(world, worldObject);
        if (vessel is null || !WaterCollectorMath.CanTake(world, npc, vessel))
        {
            npc.Plan.Status = PlanStatus.Failed;
            PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
            Trace.Emit(world, npc.Id, "ExecFailed",
                "TakeVessel: nothing collected yet, or the bottle is spoken for");
            return false;
        }

        var charges = WaterCollectorMath.ChargesIn(vessel);
        var pouredOver = npc.Inventory.Items.Exists(
            i => i.DefinitionId == WaterCollectorMath.VesselId);
        if (pouredOver)
        {
            vessel.ResourceAmount = 0f; // stays parked, keeps collecting
        }
        else
        {
            WorldObjectMutations.DespawnObject(world, vessel.Id);
            npc.Inventory.Items.Add(new ItemInstance(WaterCollectorMath.VesselId));
        }

        npc.BottleWater = WaterKind.Rain;
        npc.BottleCharges = charges;
        Trace.Emit(world, npc.Id, "VesselTaken",
            $"Rain x{charges} from collector {worldObject.Id.Value}" +
            (pouredOver ? " (poured over)" : " (bottle reclaimed)"));

        return true;
    }

    private static bool CompletePickUp(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
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
            return false;
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

        return true;
    }

    private static bool CompleteDress(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        // Spec 31A.5B: one item per (layer, body part) — dressing
        // over an occupied slot takes the old garment off. §52.7: the
        // displaced piece is only COLLECTED here; it is dropped after
        // the new garment is on and capacity recomputed, so its
        // pockets relocate into the new garment first.
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
        // §52.9 r2: now that the new garment is on and the pack capacity
        // is live, put the displaced garment(s) away — into the pack if
        // a pocket is free, on the ground only when it is not. Items
        // that still fit stayed in the pack (backed by the new
        // garment's pockets); the true overflow rides down inside the
        // dropped piece (lowest importance first).
        StowDisplacedGarments(world, npc);
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
            EndTick = world.Tick + AiBalance.DressCooldownTicks
        });

        return true;
    }

    private static bool CompleteHarvest(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        // Spec 35.2 / §54 (R1): the object is consumed; loot is
        // declared as data (Yields) and scatters on the ground.
        ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
        if (definition.Tags.Contains("Boulder"))
        {
            // §80: число берётся из самой добычи. Зашитая «4» врала
            // (валун даёт 5) во всех строках трейса, а по этим
            // строкам и читают соаки.
            var stoneYield = 0;
            foreach (var drop in completedInteraction.Yields)
            {
                if (drop.DefinitionId == ContentIds.Stone)
                {
                    stoneYield += drop.Count;
                }
            }

            Trace.Emit(world, npc.Id, "BoulderBroken",
                $"{worldObject.DefinitionId} at Tile={worldObject.Tile.Q},{worldObject.Tile.R} " +
                $"-> {stoneYield} stones");
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
            WorldObjectMutations.SpawnObject(world, ContentIds.PalmStump, stumpFragment, stumpTile, sj);
        }

        return true;
    }

    private static bool CompleteProcess(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        if (definition.Tags.Contains("Coconut"))
        {
            var nextObject = ReplaceWithYields(world, npc, worldObject, completedInteraction.Yields);
            Trace.Emit(world, npc.Id, "CoconutProcessed",
                $"{worldObject.DefinitionId} -> {nextObject?.DefinitionId ?? "nothing"}");

            if (nextObject is not null &&
                TryContinueWorldPlanAfterInteraction(world, npc, nextObject, completedInteraction.Type))
            {
                return false;
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

        return true;
    }

    private static bool CompleteButcher(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        // Spec §54: knife a carcass/corpse — the body is consumed. §54.17 r2:
        // meat goes into the butcher's pack (hide still scatters); see the
        // yield declarations. Butchering a housemate costs comfort
        // (cannibalism).
        ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
        var wasCorpse = definition.Tags.Contains("Corpse");
        if (wasCorpse && SimBalance.CannibalismEnabled)
        {
            // Comfort is satisfaction (higher = better) — the penalty
            // subtracts.
            npc.Needs.Comfort = MathUtil.Clamp(
                npc.Needs.Comfort - SimBalance.CannibalismComfortPenalty, 0f, 1f);
        }

        // §28.15C v3: нож — ЕДИНСТВЕННОЕ, что убирает человеческое тело с
        // острова. Значит здесь же кончается и само тело, и всё, что на нём
        // осталось: вещи не исчезают вместе с ней, а вываливаются под ноги —
        // разделывающая раздела её раньше, чем взялась за мясо.
        if (wasCorpse && CorpseMath.BodyOf(world, worldObject) is { } body)
        {
            foreach (var item in body.WornItems)
            {
                DropItemAtFeet(world, npc, item);
            }

            foreach (var item in body.Inventory.Items)
            {
                DropItemAtFeet(world, npc, item);
            }

            world.Entities.Corpses.Remove(body.Id);
        }

        Trace.Emit(world, npc.Id, "Butchered",
            $"{worldObject.DefinitionId} (variant={worldObject.Variant}) -> meat + hide" +
            (wasCorpse ? " [cannibalism]" : string.Empty));
        WorldObjectMutations.DespawnObject(world, worldObject.Id);

        return true;
    }

    private static bool CompleteHang(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
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
            // §66: the rack stands at a yaw, and its hanger slots turn
            // with it — a garment hung on it must take the same yaw or
            // it floats beside the rails instead of on them.
            hung.RotationDegrees = worldObject.RotationDegrees;
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

        return true;
    }

    // §28.15F: обобрать тело — ОДНА вещь за подход, карманы раньше одежды
    // (порядок держит CorpseMath: ёмкость карманов покойной даётся её же
    // одеждой). Тело остаётся лежать пустым; убирает его только нож.
    private static bool CompleteLoot(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        var body = CorpseMath.BodyOf(world, worldObject);
        var spoil = CorpseMath.NextSpoil(world, worldObject, out var source);
        if (spoil is null)
        {
            // Кто-то успел раньше. Не провал плана — просто здесь уже пусто.
            worldObject.IsOccupied = false;
            Trace.Emit(world, npc.Id, "LootEmpty",
                $"NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"} has nothing left");
            return true;
        }

        if (!InventoryMath.MakeRoomFor(world, npc, spoil.DefinitionId))
        {
            worldObject.IsOccupied = false;
            Trace.Emit(world, npc.Id, "LootBlocked",
                $"Def={spoil.DefinitionId} " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
                $"({npc.Inventory.UsedSlots}/{npc.Inventory.Capacity})");
            return false;
        }

        CorpseMath.TakeSpoil(world, worldObject, spoil, source);
        npc.Inventory.Items.Add(spoil);
        worldObject.IsOccupied = false; // CurrentUser хранит id покойной и на теле, и на останках

        Trace.Emit(world, npc.Id, "Looted",
            $"Def={spoil.DefinitionId} from={source.ToString().ToLowerInvariant()} " +
            $"NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"} " +
            $"left={(body is not null ? body.WornItems.Count + body.Inventory.Items.Count : worldObject.Contents.Count)} " +
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
            $"({npc.Inventory.UsedSlots}/{npc.Inventory.Capacity})");

        return true;
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
            SocialCueSignals.Stamp(world, npc,
                npc.Mind.CombatAssistDogId.HasValue ? "HelpCryAssistArrived:dog" : "HelpCryAssistArrived:npc",
                null);
            Trace.Emit(world, npc.Id, "HelpCryAssistArrived",
                "Reached attacker and joined the fight");
            return;
        }

        // Spec 29F.2: пришла, а краб уже отпрыгнул — погоня ПРОДОЛЖАЕТСЯ, не
        // начинается заново. Сброс в None здесь ронял охоту в полный аукцион
        // (NPC стоит тик-другой, краб уходит) — вечный пинг-понг. План уже
        // Completed, так что PlanningSystem перестроит погоню следующим
        // Medium-тиком; цель честно падает, лишь когда краба не видно.
        if (npc.Mind.CurrentGoal == GoalType.Hunt &&
            DecisionSystem.NearestVisibleRabbit(npc, world) is not null)
        {
            Trace.Emit(world, npc.Id, "HuntContinues",
                "Arrived but the crab moved on — keep chasing");
            return;
        }

        // §108: та же беда, что у §29F.2 краба, только цель бежит осмысленно.
        // Сброс в None здесь ронял охоту на первом же прибытии: он отходит на
        // узел, две подруги «пришли» и тут же теряли цель, а третья оставалась
        // одна — охота разваливалась с Reason=PartyCollapsed, ни разу не дойдя
        // до удара (арена 313: три пакта подряд, ноль столкновений). Погоню
        // продолжает следующий Medium-тик, а кончает её GroupHuntSystem.
        if (npc.Mind.CurrentGoal == GoalType.GroupHunt &&
            npc.Mind.GroupHuntTargetNpcId is not null)
        {
            Trace.Emit(world, npc.Id, "GroupHuntContinues",
                "Arrived but he moved on — keep after him");
            return;
        }

        if (npc.Mind.CurrentGoal == GoalType.Expel &&
            (npc.Mind.ExpulsionTargetNpcId is not null ||
             npc.Mind.PendingExpulsionFrom is not null))
        {
            Trace.Emit(world, npc.Id, "CampExpelContinues",
                "Arrived but the live expulsion scene owns the goal");
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

        var carcass = WorldObjectMutations.SpawnObject(world, ContentIds.CarcassAnimal, fragment, tile, junction);
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
                    // §54.17 r2: a pocketed yield may bump a less important
                    // item (importance-guarded) — a pack full of sticks must
                    // not send fresh meat to rot on the ground.
                    InventoryMath.MakeRoomFor(world, npc, drop.DefinitionId);
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

        // §26.6A r5: the coconut drop is scored by legal approaches
        // (FruitProductionSystem) — scatter deliberately is NOT. The same change
        // here was tried and MEASURED: 30 seeds × 10 days moved the CONTROL arm
        // (rule off) 91/120 → 84/120 as well, i.e. it reshuffled seeds instead of
        // feeding anyone, unlike the coconut drop which left the control at
        // exactly 91 and lifted the rule arm 81 → 84. Scatter runs on every
        // harvest, so it costs a rim BFS per candidate junction for a benefit
        // nothing could demonstrate. Revisit only with a measurement, not a hunch.
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
        => DropItemAtFeet(world, npc, item, underFoot: false);

    // underFoot=true lays the item on the NPC's OWN junction (directly beneath
    // her) instead of a scattered neighbour — used when undressing so the doffed
    // garment appears in the same cell she is standing in (user request), not one
    // cell over.
    internal static WorldObjectState DropItemAtFeet(
        WorldState world, NPCState npc, ItemInstance item, bool underFoot)
    {
        if (TryFindDropSpotAtFeet(world, npc, underFoot, out var dropTile, out var dropJunction))
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
        bool underFoot,
        out TileCoord tile,
        out JunctionId junction)
    {
        // underFoot: lay it exactly where she stands — same cell, right beneath
        // her (undressing). A ground garment is passable, so sharing her junction
        // is fine; it stays put when she steps off.
        if (underFoot && npc.CurrentJunction is { } feet &&
            world.Junctions.Items.ContainsKey(feet))
        {
            tile = npc.Tile;
            junction = feet;
            return true;
        }

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

    // Spec §60/§29G/§40.13 — THE single place that decides where a body on the
    // ground comes to rest. Every collapse / faint / ground-sleep path calls
    // this, so the invariant holds everywhere at once: a lying body ALWAYS lies
    // on its own tile's centre ROW (r3 — the exact geometric centre when the hex
    // is hers alone), never a rim junction and never the raw mid-stride spot
    // it dropped on. The position is pinned unconditionally and DECOUPLED from
    // junction occupancy on purpose: the per-site "nearest FREE junction" scans
    // that used to place the body EXCLUDED the very junction it stood on
    // (IsJunctionFree counts self-occupancy — and the lying footprint it just
    // claimed — as taken), so they could never return the centre and always
    // drifted the body sideways / off the tile edge. Footprint and occupancy
    // bookkeeping stay the caller's job; this owns only the resting position.
    //
    // §29G r3 «спальные места»: the centre is a ROW of berths, not a point.
    // Two bodies that end up on the same hex used to land on the exact same
    // spot at whatever yaw the walk left them with — one girl inside another,
    // both askew. Now the tile holds a small rank of parallel berths: the
    // first sleeper sets the heading (her own facing, snapped to a hex axis so
    // she lies straight along the hex, never across a corner) and takes the
    // middle; every later body copies that heading EXACTLY and takes the next
    // free berth beside her — right, then left. Three girls read as three
    // housemates bedded down side by side instead of one blurred pile.
    //
    // §113: и то же самое место обходит КРУПНЫЕ ВЕЩИ — костёр, валун, кровать.
    // Геометрия (порядок мест, повороты по осям гекса, габарит тела) целиком
    // живёт в LyingSpot; здесь остаётся только «записать решение в тело», чтобы
    // у вопроса «где лежит упавшая» по-прежнему был ровно один ответчик.
    internal static void LieDownCentered(WorldState world, NPCState npc)
    {
        if (!Spec49.SleepBerths)
        {
            npc.Position = HexSpatialMath.TileToWorld(npc.Tile);
            return;
        }

        var placement = LyingSpot.Solve(world, npc);
        var radians = placement.Heading * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));

        npc.Position = placement.Position;
        npc.RotationDegrees = placement.Heading;
        npc.Movement.DesiredRotationDegrees = placement.Heading;
        npc.Movement.DesiredDirection = forward;

        Trace.Emit(world, npc.Id, "LieDownBerth",
            $"Tile={npc.Tile.Q},{npc.Tile.R} Slot={placement.Slot} " +
            $"Heading={placement.Heading:F0} Fit={(placement.Clear ? "Clear" : "Stacked")}");
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
        // The perch settles her onto the seat tile at the edge. Only ever nudge
        // her ONE adjacent tile across a SINGLE elevation step — she must have
        // walked up here. Never yank her across a gap or drop her off a height
        // onto the seat (user: can't jump from a mountain onto the stump/ledge,
        // can't seat from afar). If the seat tile isn't a gentle step from where
        // she stands, skip the relocation and let her sit where she actually is.
        if (npc.Tile != standTile)
        {
            var gentleStep =
                HexSpatialMath.HexDistance(npc.Tile, standTile) <= 1 &&
                world.Tiles.Items.TryGetValue(npc.Tile, out var fromTile) &&
                world.Tiles.Items.TryGetValue(standTile, out var toTile) &&
                System.Math.Abs(fromTile.Elevation - toTile.Elevation) <= 1;
            if (!gentleStep)
            {
                npc.Movement.DesiredDirection = facing;
                npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(facing);
                npc.RotationDegrees = npc.Movement.DesiredRotationDegrees;
                return;
            }

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
