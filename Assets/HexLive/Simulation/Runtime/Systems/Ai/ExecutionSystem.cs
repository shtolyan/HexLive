using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem : ISimulationSystem
{
    public string Name => nameof(ExecutionSystem);

    public TickLayer Layer => TickLayer.Fast;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status != PlanStatus.Active)
            {
                continue;
            }

            // §81.15 / #250: pause, do not abort, a manual order while the
            // abuse scene owns its mark. Timed interactions use absolute ticks,
            // so slide their window by this frozen fast tick as well.
            if (AbuseMath.SceneOwnsManualMark(world, npc))
            {
                if (npc.Execution.Status == ExecutionStatus.InProgress)
                {
                    npc.Execution.StartTick++;
                    npc.Execution.EndTick++;
                }

                continue;
            }

            // Bug #95 / spec 41.5: manual orders are allowed to wake a sleeper
            // and remain queued, but no interaction may start over the GetUp
            // clip. This also covers an order whose target is already underfoot
            // and therefore needs no pathfinding.
            if (world.Tick < npc.Mind.WakeGraceUntilTick)
            {
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.GetFood &&
                (npc.Inventory.FindFirstFood(world.Content) is not null ||
                 DecisionSystem.HasInventoryCoconutMeal(npc)))
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Food already available in inventory");
                npc.Mind.CurrentGoal = GoalType.None;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanAborted",
                        "GetFood stopped: inventory food is available");
                }
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.GetWater &&
                DecisionSystem.HasInventoryCoconutWater(npc))
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Water already available in inventory");
                npc.Mind.CurrentGoal = GoalType.None;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanAborted",
                        "GetWater stopped: inventory water is available");
                }
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

            // §133: раздевание может иметь ногу «дойти домой», поэтому смотрим на
            // ПОСЛЕДНИЙ шаг (для раздевания на месте это тот же самый шаг).
            if (npc.Plan.Steps.Count > 0 &&
                npc.Plan.Steps[npc.Plan.Steps.Count - 1].Type == PlanStepType.UndressItem)
            {
                RunUndressItem(world, npc, npc.Plan.Steps[npc.Plan.Steps.Count - 1]);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type is
                PlanStepType.PlayerWearInventory or
                PlanStepType.PlayerStowWorn or
                PlanStepType.PlayerDropCarried or
                PlanStepType.PlayerDropWorn)
            {
                RunPlayerInventory(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[^1].Type is
                PlanStepType.PlayerTakeCarried or
                PlanStepType.PlayerTakeWorn or
                PlanStepType.PlayerTakeAndWearCarried or
                PlanStepType.PlayerTakeAndWearWorn or
                PlanStepType.PlayerGiveCarried or
                PlanStepType.PlayerGiveWorn)
            {
                RunPlayerInventoryTransfer(world, npc);
                continue;
            }

            // §128.5: обмен с вещью — своя ветка, потому что второй стороной
            // тут не человек, а объект мира.
            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[^1].Type is
                PlanStepType.PlayerTakeFromContainer or
                PlanStepType.PlayerTakeAndWearFromContainer or
                PlanStepType.PlayerGiveToContainer)
            {
                RunPlayerContainerTransfer(world, npc);
                continue;
            }

            // §55.4 (bug #317): перелив воды кокосов в бутылку — шаг на месте
            // ПЕРЕД питьём; по завершении снимает себя из плана, и Steps[0]
            // становится DrinkBottle.
            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.FillVessel)
            {
                RunFillVessel(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.DrinkBottle)
            {
                RunDrinkBottle(world, npc);
                continue;
            }

            // §68 r2: a ground/stashed dressing can add a walk before the
            // treatment beat; the final step owns the special executor.
            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[^1].Type == PlanStepType.TreatSelf)
            {
                RunTreatSelf(world, npc);
                continue;
            }

            // §138 / bug #197: a remote ground bill adds a movement leg before
            // the in-place craft, so the terminal step owns this executor.
            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[^1].Type == PlanStepType.CraftInPlace)
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

            // §133: подойти к хозяйке и спросить, можно ли надеть её вещь.
            if (lastStep is { Type: PlanStepType.AskWearPermission })
            {
                RunAskWearPermission(world, npc, lastStep);
                continue;
            }

            // §133: подобрать забытую одежду и отнести её домой.
            if (lastStep is { Type: PlanStepType.StowCarriedGarment })
            {
                RunStowCarriedGarment(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.GroundSit or PlanStepType.GroundSleep or PlanStepType.GroundCool })
            {
                RunGroundRestPlan(world, npc, lastStep);
                continue;
            }

            // §137: праздный отдых — сесть там, где стоишь. Ходьбы у плана нет
            // вовсе, поэтому он не идёт через RunGroundRestPlan (тот сначала
            // ждёт прибытия на забронированный узел).
            if (lastStep is { Type: PlanStepType.IdleRest })
            {
                RunIdleRest(world, npc);
                continue;
            }

            if (lastStep is { Type: PlanStepType.PickUpPerson } &&
                npc.Mind.CurrentGoal == GoalType.PlayerOrder)
            {
                RunManualPersonPickup(world, npc);
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
                if (npc.Mind.CurrentGoal == GoalType.Rescue)
                {
                    RunRescue(world, npc);
                }
                else if (npc.Mind.CurrentGoal is GoalType.Splint or GoalType.FitProsthetic)
                {
                    RunLimbCare(world, npc);
                }
                else if (npc.Mind.CurrentGoal == GoalType.Aid)
                {
                    RunAid(world, npc);
                }
                else if (npc.Mind.CurrentGoal == GoalType.Abuse)
                {
                    RunAbuse(world, npc);
                }
                else if (npc.Mind.CurrentGoal == GoalType.Romance)
                {
                    RunRomance(world, npc);
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
                    npc.Memory.Version++; // §22.7: кэш вида памяти обязан увидеть удаление
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "MemoryForgotten",
                            $"Obj={npc.Plan.TargetObjectId.Value.Value} Stale (arrived, object gone)");
                    }
                }

                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecFailed",
                        $"TargetObject={npc.Plan.TargetObjectId.Value.Value} not found in world (despawned?)");
                }
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Target object despawned mid-plan");
                npc.Mind.CurrentGoal = GoalType.None;
                continue;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
            {
                npc.Plan.Status = PlanStatus.Failed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecFailed",
                        $"Definition={worldObject.DefinitionId} not found in catalog");
                }
                continue;
            }

            if (BuildSiteMath.UsesGenericSiteInteractions(worldObject) &&
                world.Content.ObjectDefinitions.TryGetValue(ContentIds.BuildSite, out var siteDefinition))
            {
                definition = siteDefinition;
            }

            if (npc.Movement.IsMoving)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecWaitingForMovement",
                        $"Status={npc.Movement.Status} PathStep={npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}");
                }
                continue;
            }

            if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecWaitingForArrival",
                        $"MovementStatus={npc.Movement.Status} (not Arrived)");
                }
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
                    npc.Memory.Shun(worldObject.Id, world.Tick + AiBalance.ShunTicks);
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
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
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
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
                        npc.Social.MarkInteraction(occupant, world.Tick);
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
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                        $"Target {worldObject.DefinitionId} occupied on arrival");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                var interaction = ResolveInteraction(
                    world, npc, definition, GetPlannedInteractionType(npc.Plan),
                    GetPlannedInteractionId(npc.Plan));
                if (interaction is null)
                {
                    // §54.2 r2 (#168): раньше здесь оставался только Failed — и
                    // план строился к тому же объекту снова и снова. Пока
                    // фолбэк хватал ЛЮБОЙ первый глагол, пустышек не бывало;
                    // теперь бывают (пальма без «потрясти»), и цикл надо
                    // закрыть тем же способом, что и все соседние отказы:
                    // пометить объект, остудить цель и отпустить её.
                    npc.Memory.Shun(worldObject.Id, world.Tick + AiBalance.ShunTicks);
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                        $"Nothing to do with {worldObject.DefinitionId} for {npc.Plan.Goal}");
                    npc.Mind.CurrentGoal = GoalType.None;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "ExecFailed",
                            $"No interaction of planned type on {worldObject.DefinitionId}");
                    }
                    continue;
                }

                // Spec 35.5: hanging needs a wet worn garment and a free rack.
                if (interaction.Type == InteractionType.Hang)
                {
                    var wetWorn = FindWettestWornItem(npc);
                    if (wetWorn is null || wetWorn.Wetness <= 0.5f || RackIsFull(world, worldObject))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
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
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Cannot craft {npc.Plan.Goal} (no legs)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    // Spec §54 (R2): ingredients/gate sourced from RecipeCatalog.
                    // In-place recipes (including bandages) use RunCraftInPlace;
                    // this switch is only the station-backed interaction path.
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
                        GoalType.CraftSplint => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftWoodenArm => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftWoodenLeg => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        _ => false
                    };

                    if (!craftOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
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
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Cannot build (materials missing or hut done)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 35.2: trees need an axe or saw; boulders need the pickaxe.
                if (interaction.Type == InteractionType.Harvest)
                {
                    // §44/#221: stripping the medicinal bush is the one
                    // hand-harvest. Its data declares no capability on purpose;
                    // do not route it through the tree/boulder tool blanket.
                    var isHerbBush = definition.HasTag("HerbBush");
                    if (!isHerbBush && !npc.Body.CanUseToolsOrWeapons)
                    {
                        if (npc.Plan.TargetObjectId is { } producer)
                        {
                            npc.Memory.Shun(producer, world.Tick + AiBalance.ShunTicks);
                        }
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Cannot harvest {worldObject.DefinitionId} (cannot use harvesting tools)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    var isBoulder = definition.HasTag("Boulder");
                    // Spec §54: yucca is cut with a BLADE — a knife or an axe (not
                    // a saw or pickaxe); trees still need an axe/saw; boulders the
                    // pickaxe.
                    var isYucca = definition.HasTag("Yucca");
                    var hasChopTool = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood);
                    var hasBlade = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.Cut);
                    var toolOk = isHerbBush
                        ? true
                        : isBoulder
                        ? Content.GearCatalog.HasCapability(
                            npc.Inventory.Items, Content.GearCapability.Mine)
                        : isYucca
                            ? hasBlade
                            : hasChopTool;
                    if (!toolOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Cannot harvest {worldObject.DefinitionId} (missing tool)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Process always needs a functional hand. Coconut work is the
                // light exception that remains possible while prone; every
                // other Process action is heavy work and also needs standing.
                if (interaction.Type == InteractionType.Process)
                {
                    var isLightCoconutWork = definition.HasTag("Coconut");
                    if (!npc.Body.HasUsableHand ||
                        (!isLightCoconutWork && !npc.Body.CanUseToolsOrWeapons))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Cannot process {worldObject.DefinitionId} " +
                            (npc.Body.HasUsableHand ? "(cannot stand)" : "(no usable hand)"));
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
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
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
                    var isCoconut = definition.HasTag("Coconut");
                    var hasChopTool = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood);
                    var hasCoconutBlade = DecisionSystem.HasCoconutBlade(npc);
                    if (isCoconut ? !hasCoconutBlade : !hasChopTool)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Cannot split {worldObject.DefinitionId} (missing tool)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                if (interaction.Type == InteractionType.Drink &&
                    definition.HasTag("CoconutWater") &&
                    worldObject.ResourceAmount <= 0f)
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                        $"Cannot drink {worldObject.DefinitionId} (already drained)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec §54: butchering a carcass/corpse needs a knife in hand.
                // (Legacy path — declared interactions use the any-of gate.)
                if (interaction.Type == InteractionType.Butcher &&
                    interaction.RequiredCapabilities.Count == 0 &&
                    (!npc.Body.CanUseToolsOrWeapons ||
                     !Content.GearCatalog.HasCapability(
                         npc.Inventory.Items, Content.GearCapability.Butcher)))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                        $"Cannot butcher {worldObject.DefinitionId} (no knife)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec 29E.3: fueling needs a carried log; lighting a dead
                // fire additionally needs the lighter — UNLESS she's cold
                // enough to friction/hand-drill it (§45 r5).
                if (interaction.Type == InteractionType.Fuel)
                {
                    var manualColdStocking = npc.Plan.Goal == GoalType.PlayerOrder &&
                        worldObject.ResourceAmount <= 0f;
                    var carriedFuel = ContainerLootMath.FindCarriedCampfireFuel(world, npc);
                    var hasWoodNow = carriedFuel is not null ||
                        (!manualColdStocking &&
                         ContainerLootMath.HasQueuedCampfireFuel(world, worldObject));
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
                    // §126/§49 r2: ночной затвор снят, его место занял тот же
                    // порог сна, что читает решение (парность обязательна —
                    // иначе она пришла бы разжигать и передумала на месте).
                    var canFrictionLight =
                        npc.Needs.Energy < TraitMath.EffectiveSleepThreshold(npc) ||
                        npc.Needs.ThermalComfort < -0.35f ||
                        world.Tick - npc.Mind.LastFreezingTick < SimBalance.FrictionLightGraceTicks;
                    var missingLighter = !manualColdStocking &&
                        worldObject.ResourceAmount <= 0f &&
                        !Content.GearCatalog.HasCapability(
                            npc.Inventory.Items, Content.GearCapability.Ignite) &&
                        !canFrictionLight;
                    var fuelBufferFull = manualColdStocking && carriedFuel is not null &&
                        !ContainerLootMath.CanAccept(
                            world, worldObject, new[] { carriedFuel });
                    if (!hasWoodNow || missingLighter || fuelBufferFull)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Cannot fuel fire (wood={hasWoodNow} " +
                            $"lighterMissing={missingLighter} bufferFull={fuelBufferFull})");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                if (interaction.Type == InteractionType.Ignite &&
                    (worldObject.ResourceAmount > 0f ||
                     !ContainerLootMath.HasQueuedCampfireFuel(world, worldObject)))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                        $"Cannot ignite fire (lit={worldObject.ResourceAmount > 0f} " +
                        $"queuedFuel={ContainerLootMath.HasQueuedCampfireFuel(world, worldObject)})");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Furniture uses a deliberate two-beat entry: arrive at the
                // reserved rim, turn with the back to the anchor, and only
                // then switch on Sit/Sleep.  Manual and autonomous plans both
                // reach this same gate, so the bed's authored lying formula is
                // never entered from a different orientation.
                var anchorPosition = worldObject.Junctions.Count > 0 &&
                    world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchorJunction)
                        ? anchorJunction.WorldPosition
                        : HexSpatialMath.TileToWorld(worldObject.Tile);
                if (interaction.Type is InteractionType.Sit or InteractionType.Sleep)
                {
                    if (!TryTurnBackToFurniture(world, npc, worldObject, anchorPosition))
                    {
                        continue;
                    }
                }
                else if (HexSpatialMath.Distance(anchorPosition, npc.Position) > 0.05f)
                {
                    var faceDirection = HexSpatialMath.Normalize(new Float2(
                        anchorPosition.X - npc.Position.X, anchorPosition.Y - npc.Position.Y));
                    npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(faceDirection);
                    npc.RotationDegrees = npc.Movement.DesiredRotationDegrees;
                }

                if (interaction.Type == InteractionType.Craft &&
                    RecipeCatalog.UsesPersistentProject(npc.Plan.Goal) &&
                    !CraftProjectMath.TryBeginCycle(
                        world, npc, npc.Plan.Goal, worldObject, out _))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                        $"Cannot begin persistent craft project ({npc.Plan.Goal})");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // §76 + §79: how long the job takes — first the TOOL doing it
                // (§79: the gear that carries the required capability sets the
                // pace: machete 2 → half, stone axe 1 → authored, knife 0.75 →
                // longer), then her own hands (Strength/Wits + the learned
                // trade). Both stages round instead of truncating, and at
                // multiplier 1f both are exactly the authored number.
                var toolSpeedMult = Content.GearCatalog.BestSpeedMultFor(
                    npc.Inventory.Items,
                    Content.GearCatalog.RequiredCapabilities(interaction, definition));
                var authoredWorkTicks = RecipeCatalog.UsesPersistentProject(npc.Plan.Goal)
                    ? Spec119.CraftCycleWork
                    : npc.Plan.Goal == GoalType.CraftSplint
                        ? Spec118.SplintCraftTicks
                        : interaction.DurationTicks;
                var workTicks = AttributeMath.WorkTicks(
                    npc, Content.GearCatalog.ScaleTicks(authoredWorkTicks, toolSpeedMult),
                    interaction.Type, npc.Plan.Goal);
                if (interaction.Type == InteractionType.Sleep)
                {
                    // The whole transition is atomic and shared with rescue
                    // and deterministic test scenes. No caller is allowed to
                    // assemble a bed sleeper from pose/state/claim fragments.
                    if (!BedSleep.TryEnter(
                            world, npc, worldObject, world.Tick + workTicks,
                            npc.CurrentJunction))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Cannot enter bed {worldObject.Id.Value}");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }
                else
                {
                    npc.Execution.Status = ExecutionStatus.InProgress;
                    npc.Execution.CurrentInteraction = interaction.Type;
                    npc.Execution.TargetObject = worldObject.Id;
                    npc.Execution.StartTick = world.Tick;
                    npc.Execution.EndTick = world.Tick + workTicks;
                    npc.Execution.BuildDeposited = false; // §77: this visit still owes its load
                    worldObject.IsOccupied = true;
                    // Spec 28.15C: a corpse's CurrentUser records whose body it
                    // is — mourning must not overwrite it.
                    if (!CorpseMath.IsHumanDead(definition))
                    {
                        worldObject.CurrentUser = npc.Id;
                    }
                }
                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.OccupyJunction(world, jId, npc.Id);
                }

                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "InteractionStarted",
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
                }
                continue;
            }

            if (npc.Execution.Status == ExecutionStatus.InProgress)
            {
                if (npc.Execution.CurrentInteraction == InteractionType.Craft &&
                    RecipeCatalog.UsesPersistentProject(npc.Plan.Goal))
                {
                    CraftProjectMath.UpdateCycleProgress(world, npc);
                }

                // Also repair/resynchronise a sleep restored from a save made
                // before §111.9 r3. The bed pose is an invariant for the whole
                // interaction, not only a one-shot adjustment at its start.
                if (npc.Execution.CurrentInteraction == InteractionType.Sleep)
                {
                    if (!BedSleep.MaintainPose(world, npc, worldObject))
                    {
                        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                            $"Bed pose unavailable for {worldObject.Id.Value}");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    // §49 parity: object-backed sleep used to ignore the same
                    // critical wake conditions that ground sleep honors. Abort
                    // promptly; §49 r2 не нужен латч, чтобы она вернулась
                    // досыпать, — энергия всё ещё под порогом, и ставка сна
                    // поднимется сама, как только кризис снят.
                    var sleepInterruptReason = GetSleepInterruptReason(
                        world, npc, alreadyAsleep: true);
                    if (sleepInterruptReason is not null)
                    {
                        if (SimTrace.Enabled)
                        {
                            Trace.Debug(world, npc.Id, "SleepInterrupted",
                                $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                                $"Danger={npc.Memory.Dangers.Count}");
                        }
                        PlanInterruption.TryAbort(
                            world, npc, SleepInterruptionCause(sleepInterruptReason),
                            $"Sleep interrupted: {sleepInterruptReason}");
                        StampSleepRefusal(world, npc, sleepInterruptReason);
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 42: the fire died mid-huddle — a dead pit warms nobody,
                // so warming (and boiling) at it stops NOW instead of playing
                // out the full interaction at a cold fireplace.
                if (npc.Execution.CurrentInteraction is InteractionType.Observe
                        or InteractionType.FillBottle &&
                    definition.HasTag("Campfire") &&
                    worldObject.ResourceAmount <= 0f &&
                    npc.Plan.Goal != GoalType.HaulToFire)
                {
                    PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, "Fire went out mid-interaction");
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
                    var inProgressInteraction = ResolveInteraction(
                        world, npc, definition, npc.Execution.CurrentInteraction,
                        GetPlannedInteractionId(npc.Plan));
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

                    if (SimTrace.Enabled)
                    {
                        if (SimTrace.Enabled)
                        {
                            Trace.Debug(world, npc.Id, "ExecProgress",
                                $"{npc.Execution.CurrentInteraction} Progress={progress:P0} " +
                                $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                        }
                    }

                    continue;
                }

                var completedInteraction = ResolveInteraction(
                    world, npc, definition, npc.Execution.CurrentInteraction,
                    GetPlannedInteractionId(npc.Plan));
                if (completedInteraction is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "ExecFailed",
                            $"Interaction {npc.Execution.CurrentInteraction} vanished from {worldObject.DefinitionId}");
                    }
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
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "SleepContinued",
                            $"Surface={worldObject.DefinitionId} Energy={npc.Needs.Energy:F2} " +
                            $"Comfort={npc.Needs.Comfort:F2}");
                    }
                    continue;
                }

                // The authored lying pose is centred on the bed, but waking is
                // a standing action. Never clear Sleep while the body is still
                // inside furniture/a wall: return to the reserved approach node
                // or find the nearest free junction (legacy-save fallback).
                if (completedInteraction.Type == InteractionType.Sleep &&
                    !LyingSpot.TryStandAfterObjectSleep(world, npc, worldObject))
                {
                    npc.Execution.StartTick = world.Tick;
                    npc.Execution.EndTick = world.Tick + 1;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "WakeStandDeferred",
                            $"Bed={worldObject.Id.Value} no free standing junction");
                    }
                    continue;
                }

                // Spec 29H: filling the bottle charges it (raw at a bank,
                // boiled at a lit campfire) — thirst is quenched only on Drink.
                if (!ApplyInteractionCompletion(
                        world, npc, worldObject, definition, completedInteraction, needsBefore))
                {
                    continue;
                }

                // §138 + §119.2: one manual click owns one whole persistent
                // item. Autonomous workers still return to the auction between
                // work slices, but BOTH control modes keep ownership across the
                // final 100% -> physical PickUp beat. Otherwise station crafts
                // left their output on the table and immediately bid for a new
                // bill (the bandage carpet).
                if (completedInteraction.Type == InteractionType.Craft &&
                    RecipeCatalog.UsesPersistentProject(npc.Plan.Goal))
                {
                    if (npc.Execution.CraftLayout.Count > 0)
                    {
                        SkillTrace.Award(world, npc, completedInteraction.Type,
                            npc.Execution.EndTick - npc.Execution.StartTick);
                        BeginCraftOutputTake(world, npc);
                        continue;
                    }

                    if (npc.Mind.ManualControl)
                    {
                        var unfinished = CraftProjectMath.FindReachableProject(
                            world, npc, npc.Plan.Goal);
                        if (unfinished != null)
                        {
                            SkillTrace.Award(world, npc, completedInteraction.Type,
                                npc.Execution.EndTick - npc.Execution.StartTick);
                            ResetCraftCycleExecution(npc);
                            continue;
                        }
                    }
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
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "SleepContinued",
                            $"Object={worldObject.DefinitionId} Energy={npc.Needs.Energy:F2} " +
                            $"Comfort={npc.Needs.Comfort:F2}");
                    }
                    continue;
                }

                SkillTrace.Award(world, npc, completedInteraction.Type,
                    npc.Execution.EndTick - npc.Execution.StartTick);

                if (npc.Mind.ManualControl &&
                    completedInteraction.Type == InteractionType.Craft &&
                    RecipeCatalog.IsItemOutputGoal(npc.Plan.Goal))
                {
                    npc.Mind.LastManualInputTick = world.Tick;
                }

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

                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "CycleReset",
                        $"Goal->None Plan->Completed Execution->Cleared Movement->Cleared (ready for next decision)");
                }
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
            // §134: an already-running order loaded from an old save cannot
            // conjure boiled water after the interaction was removed.
            if (definition.HasTag("Campfire"))
            {
                npc.Plan.Status = PlanStatus.Failed;
                return false;
            }
            var bottle = BottleInventoryMath.FirstEmpty(npc);
            if (bottle is null)
            {
                npc.Plan.Status = PlanStatus.Failed;
                return false;
            }
            var kind = definition.HasTag("RawWater") ? WaterKind.Raw : WaterKind.Boiled;
            BottleInventoryMath.SetContents(bottle, kind, SimBalance.BottleCapacity);
            Trace.Emit(world, npc.Id, "BottleFilled",
                $"{kind} x{BottleInventoryMath.Charges(bottle)} from {worldObject.DefinitionId}");
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
                 definition.HasTag("Campfire"))
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
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "SpitHangFailed",
                        $"spitComplete={BuildSiteMath.CampfireSpitComplete(worldObject)} " +
                        $"hooksUsed={BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatRaw) + BuildSiteMath.HangingMeat(worldObject, ContentIds.MeatCooked)}" +
                        $"/{SimBalance.CampfireSpitCapacity}");
                }
            }

            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
        }
        else if (completedInteraction.Type == InteractionType.Craft &&
                 RecipeCatalog.UsesPersistentProject(npc.Plan.Goal))
        {
            var projectId = npc.Execution.CraftProjectId;
            CraftProjectMath.CompleteCycle(world, npc, npc.Plan.Goal);
            if (projectId is { } completedId &&
                world.Entities.Objects.TryGetValue(completedId, out var completedProject) &&
                !completedProject.IsCraftProject)
            {
                npc.Execution.CraftLayout.Clear();
                npc.Execution.CraftLayout.Add(completedId);
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
                        PlaceCraftedFurniture(world, npc, worldObject, ContentIds.BedBasic);
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
                 definition.HasTag("CoconutWater"))
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
            // Bug #228: a player's «add fuel» on a cold pit only stocks its
            // visible buffer. Ignition is a separate Ignite order. Autonomous
            // TendFire intentionally keeps its single-step lighter/friction
            // chain, so separating the menu does not break the NPC brain.
            //
            // §151.2 r2 (bug #293): приказ игрока кладёт дрова в ВИДИМУЮ
            // очередь и у ГОРЯЩЕГО костра. Раньше они растворялись в счётчике
            // огня, и вещь для игрока просто исчезала. Суммарное время горения
            // от этого не меняется: FireSystem сам берёт следующую вещь из
            // очереди, как только активное топливо выходит. Автономный
            // TendFire по-прежнему жжёт немедленно.
            var wasLit = worldObject.ResourceAmount > 0f;
            var playerFuelOrder = npc.Plan.Goal == GoalType.PlayerOrder;
            var carriedFuel = playerFuelOrder
                ? ContainerLootMath.FindCarriedCampfireFuel(world, npc)
                : null;
            var queueFuel = carriedFuel is not null &&
                ContainerLootMath.CanAccept(world, worldObject, new[] { carriedFuel });
            if (!queueFuel && !wasLit && playerFuelOrder)
            {
                // Bug #235: отказ — тоже конец сцены, и закрыть его надо
                // целиком: заявку с очага, узел подхода и саму сцену.
                return FailHearthScene(world, npc, worldObject,
                    $"Cannot stock {worldObject.DefinitionId} " +
                    $"(wood={carriedFuel is not null} buffer full)");
            }

            if (queueFuel)
            {
                ContainerLootMath.GiveToContainer(
                    world, worldObject, npc, new[] { carriedFuel });
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "FireFuelQueued",
                        $"{worldObject.DefinitionId} queued={carriedFuel.DefinitionId}");
                }
                worldObject.IsOccupied = false;
                worldObject.CurrentUser = null;
                // ⭐ Bug #235: здесь стоял `return true` — и он уносил рукав МИМО
                // хвоста метода, единственного места, где освобождается УЗЕЛ
                // подхода. Занятость узла бессрочна (в отличие от брони) и
                // снимается только явным FreeJunction или смертью владелицы, так
                // что каждый приказ «подбросить дров» отнимал у костра один
                // подход навсегда. Уходить из рукава раньше хвоста нельзя.
            }
            else
            {
                // Spec 29E.3 / §54 / §151: autonomous TendFire — and a player's
                // order whose visible queue is already full — consume fuel
                // immediately; buffered logs preserve their 4x value.
                if (!ContainerLootMath.TryConsumeCarriedCampfireFuel(
                        world, npc, out var fuel) &&
                    !ContainerLootMath.TryConsumeCampfireFuel(
                        world, worldObject, out fuel))
                {
                    // Bug #235: палку успели потратить между планом и завершением —
                    // подкинуть нечего. Отпускаем очаг, иначе он остаётся занят
                    // навсегда именно за той, кто пришла его поддержать.
                    return FailHearthScene(world, npc, worldObject,
                        $"Cannot fuel {worldObject.DefinitionId} (no wood left on arrival)");
                }
                worldObject.ResourceAmount += fuel;
                Trace.Emit(world, npc.Id, wasLit ? "FireFueled" : "FireLit",
                    $"{worldObject.DefinitionId} Fuel={worldObject.ResourceAmount:F0} ticks");
                worldObject.IsOccupied = false;
                worldObject.CurrentUser = null;
            }
        }
        else if (completedInteraction.Type == InteractionType.Ignite)
        {
            if (worldObject.ResourceAmount > 0f ||
                !ContainerLootMath.TryConsumeCampfireFuel(
                    world, worldObject, out var fuel))
            {
                // Bug #235: костёр уже зажгли (или буфер опустел) — розжигу
                // нечего делать, но занятость обязана уйти вместе со сценой.
                return FailHearthScene(world, npc, worldObject,
                    $"Cannot ignite {worldObject.DefinitionId} " +
                    $"(lit={worldObject.ResourceAmount > 0f} or buffer empty)");
            }

            worldObject.ResourceAmount = fuel;
            Trace.Emit(world, npc.Id, "FireLit",
                $"{worldObject.DefinitionId} Fuel={worldObject.ResourceAmount:F0} ticks");
            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
        }
        else if (completedInteraction.Type == InteractionType.Observe &&
                 npc.Plan.Goal == GoalType.HaulToFire &&
                 definition.HasTag("Campfire"))
        {
            // Spec §52: set the low-value item down at the hearth (a
            // fireside stockpile) — the pack has room again, and the item
            // waits here to be reclaimed by normal pickup later.
            var victim = InventoryMath.LowestImportanceDroppable(world, npc);
            if (victim is not null)
            {
                InventoryMath.RemoveReference(npc.Inventory.Items, victim);
                DropItemAtFeet(world, npc, victim);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "StashedAtFire",
                        $"{victim.DefinitionId} set by the fire (freed a slot)");
                }
            }

            // ⭐ Bug #235 «костёр занят». Единственный рукав костра, который
            // забывал снять заявку на УСПЕШНОМ пути, — и потому стрелял не
            // изредка, а после КАЖДОЙ штатной заначки у очага. Хвост метода
            // занятость не трогает (он освобождает только узел), а
            // PlanInterruption отпускает объект лишь пока
            // Execution.Status == InProgress — здесь он уже Completed. Костёр
            // так и оставался IsOccupied=true с CurrentUser той, кто просто
            // положила рядом вещь: приказ игрока отбивался Reject(Occupied),
            // автономные TendFire/CookMeat/FillBottle ловили
            // InteractionBlocked, шунили очаг и копили обиду на «занявшую»
            // (§28.15B), а огонь тух — подкинуть дров было некому.
            ReleaseHearthClaim(npc, worldObject);
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
        else if (definition.HasTag("HerbBush"))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "HerbStripped",
                    $"{worldObject.DefinitionId} stripped -> leaves scattered");
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

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InteractionCompleted",
                $"{completedInteraction.Type} on {worldObject.DefinitionId} " +
                $"Duration={npc.Execution.EndTick - npc.Execution.StartTick}ticks " +
                $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}]");
        }

        // §76: the ONE hook covering the whole world-object path —
        // harvesting, building, cooking at the fire, butchering, fire
        // tending, bottle filling. Paid on the ticks she ACTUALLY spent,
        // so getting faster at a trade also slows how fast she keeps
        // improving at it.

        return true;
    }

    /// <summary>
    /// Bug #235: снять заявку сцены с очага. Отдельный метод, потому что забыть
    /// эти две строки можно в КАЖДОМ рукаве костра по отдельности — а забытые,
    /// они не «подтекают», а запирают объект навсегда: хвост
    /// <see cref="ApplyInteractionCompletion"/> освобождает только узел, и к
    /// моменту рукава <c>Execution.Status</c> уже <c>Completed</c>, так что
    /// страховка <c>PlanInterruption</c> (она смотрит только на
    /// <c>InProgress</c>) сюда не дотягивается.
    /// <para>
    /// Чужую заявку не трогаем: если очаг числится за кем-то другим, эта сцена
    /// его не занимала и освобождать его не её дело.
    /// </para>
    /// </summary>
    private static void ReleaseHearthClaim(NPCState npc, WorldObjectState worldObject)
    {
        if (worldObject.CurrentUser is { } holder && !holder.Equals(npc.Id))
        {
            return;
        }

        worldObject.IsOccupied = false;
        worldObject.CurrentUser = null;
    }

    /// <summary>
    /// Bug #235: закрыть СОРВАННУЮ сцену у очага целиком. Снять заявку с костра
    /// мало — рукав, вернувший <c>false</c>, не закрывает больше ничего: хвост
    /// <see cref="ApplyInteractionCompletion"/> пропущен (а он единственный
    /// освобождает узел подхода), план остаётся <c>Active</c>, а
    /// <c>Execution.Status</c> — <c>Completed</c>. Этого состояния не читает НИ
    /// ОДНА ветка исполнителя: старт ждёт <c>None</c>, продолжение — только
    /// <c>InProgress</c>. Колонистка застывала навсегда, держа узел у костра, а
    /// у ручной ещё и планировщик выключен — вытащить её оттуда было некому.
    /// <para>
    /// Поэтому отказ уходит тем же путём, что и все соседние отказы у объектов:
    /// <c>TryAbort</c> распускает план, execution, узел и бронь. Причина
    /// <c>ExecutionFailure</c> проходит и у ручного персонажа (§121.5) и заодно
    /// говорит игроку, что его приказ сорвался.
    /// </para>
    /// </summary>
    private static bool FailHearthScene(
        WorldState world, NPCState npc, WorldObjectState worldObject, string reason)
    {
        ReleaseHearthClaim(npc, worldObject);
        PlanInterruption.TryAbort(
            world, npc, InterruptionCause.ExecutionFailure, reason);
        npc.Mind.CurrentGoal = GoalType.None;
        return false;
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
        var bottleItem = BottleInventoryMath.FirstEmpty(npc);
        if (bottleItem is null ||
            worldObject.Junctions.Count == 0 ||
            WaterCollectorMath.FindVessel(world, worldObject) is not null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed",
                    "PlaceVessel: no empty bottle to park, or the slot is taken");
            }
            return false;
        }

        if (!InventoryMath.RemoveReference(npc.Inventory.Items, bottleItem))
        {
            npc.Plan.Status = PlanStatus.Failed;
            return false;
        }
        var vessel = WorldObjectMutations.SpawnObject(
            world, WaterCollectorMath.VesselId, npc.Fragment,
            worldObject.Tile, worldObject.Junctions[0]);
        vessel.Owner = npc.Id; // remembers whose bottle waits here
        vessel.ResourceAmount = 0f;
        vessel.WaterKind = WaterKind.None;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "VesselPlaced",
                $"tool.bottle parked in collector {worldObject.Id.Value}");
        }

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
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed",
                    "TakeVessel: nothing collected yet, or the bottle is spoken for");
            }
            return false;
        }

        var charges = WaterCollectorMath.ChargesIn(vessel);
        var collectedKind = vessel.WaterKind == WaterKind.None
            ? WaterKind.Rain
            : vessel.WaterKind;
        var bottle = BottleInventoryMath.FirstEmpty(npc);
        var pouredOver = bottle is not null;
        if (pouredOver)
        {
            vessel.ResourceAmount = 0f; // stays parked, keeps collecting
            vessel.WaterKind = WaterKind.None;
        }
        else
        {
            WorldObjectMutations.DespawnObject(world, vessel.Id);
            bottle = new ItemInstance(WaterCollectorMath.VesselId);
            npc.Inventory.Items.Add(bottle);
        }

        BottleInventoryMath.SetContents(bottle, collectedKind, charges);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "VesselTaken",
                $"Rain x{charges} from collector {worldObject.Id.Value}" +
                (pouredOver ? " (poured over)" : " (bottle reclaimed)"));
        }

        return true;
    }

    private static bool CompletePickUp(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        if (worldObject.IsCraftProject)
        {
            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PickupBlocked",
                    $"Craft project {worldObject.Id.Value} is only " +
                    $"{worldObject.CraftWorkDone}/{worldObject.CraftWorkRequired} ready");
            }
            return false;
        }

        // §54.14 (r2): PickUp on the CAMPFIRE takes one cooked chunk
        // off the spit — the fire itself never leaves the ground.
        if (definition.HasTag("Campfire"))
        {
            TakeMeatFromSpit(world, npc, worldObject);
        }
        else if (npc.Plan.Goal == GoalType.CraftLeather &&
                 worldObject.DefinitionId == ContentIds.LeatherPants)
        {
            // Leather's authored completion adapter is immediate wear. Reusing
            // a finished pair found on the ground must behave exactly like the
            // pair this worker has just completed, not strand it in her pack.
            return CompleteDress(
                world, npc, worldObject, definition, completedInteraction, needsBefore);
        }
        // §119.2: the generic craft-output planner may target a dropped
        // garment whose pockets hold the requested result. Take exactly one
        // recipe output and leave the container plus unrelated contents.
        else if (RecipeCatalog.UsesPersistentProject(npc.Plan.Goal) &&
                 worldObject.DefinitionId != RecipeCatalog.OutputOf(npc.Plan.Goal) &&
                 worldObject.Contents.Count > 0)
        {
            if (!RecoverCraftOutputFromStash(
                    world, npc, worldObject,
                    RecipeCatalog.OutputOf(npc.Plan.Goal)))
            {
                return false;
            }
        }
        // Spec §52: "gathering a tool" that rides in a dropped
        // garment's pockets — rifle the pockets and leave the
        // garment (with any non-tool stash) on the ground.
        else if (npc.Plan.Goal == GoalType.GatherTools &&
            worldObject.Contents.Count > 0 &&
            !definition.HasTag("Tool"))
        {
            RecoverStashedTools(world, npc, worldObject);
        }
        else if (!InventoryMath.MakeRoomForGoal(
            world, npc, npc.Plan.Goal, worldObject.DefinitionId))
        {
            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PickupBlocked",
                    $"Def={worldObject.DefinitionId} Obj={worldObject.Id.Value} " +
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
                    $"({npc.Inventory.UsedSlots}/{npc.Inventory.Capacity})");
            }
            return false;
        }
        else
        {
            // Bug #312: взять чужое на приватной земле чужого лагеря — кража:
            // событие + удар по отношению свидетельниц. Считается ДО деспавна,
            // пока вещь ещё несёт тайл и владельца.
            var theft = TheftMath.IsTheft(world, npc, worldObject);

            // Item moves from world to inventory; the world object is gone,
            // so occupancy flags die with it (spec 29B.2).
            var picked = new ItemInstance(worldObject.DefinitionId)
            {
                Wetness = worldObject.Wetness,
                Durability = worldObject.Durability,
                ResourceAmount = worldObject.DefinitionId == ContentIds.Bottle &&
                    worldObject.WaterKind == WaterKind.None
                        ? 0f
                        : worldObject.ResourceAmount,
                WaterKind = worldObject.WaterKind,
                Dirtiness = worldObject.Dirtiness,
                Bloodiness = worldObject.Bloodiness,
                // §133: поднятая вещь несёт владельца дальше — иначе одежда
                // теряла бы хозяйку каждый раз, как её несут в руках.
                OwnerId = definition.Layer != null
                    ? ClothingOwnership.ResolveOnTake(world, npc, worldObject)
                    : 0
            };
            npc.Inventory.Items.Add(picked);
            if (theft)
            {
                TheftMath.OnStolen(world, npc, worldObject);
            }

            WorldObjectMutations.DespawnObject(world, worldObject.Id);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ItemPickedUp",
                    $"Def={worldObject.DefinitionId} Obj={worldObject.Id.Value} " +
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}] ({npc.Inventory.Items.Count}/{npc.Inventory.Capacity})");
            }
        }

        return true;
    }

    internal static bool CompleteDress(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        ObjectDefinition definition, InteractionDefinition completedInteraction,
        string needsBefore)
    {
        var restoringSelectedOutfit =
            OutfitMaintenanceMath.CanRestorePinnedPiece(world, npc, worldObject);
        if (npc.Mind.OutfitLocked && !restoringSelectedOutfit)
        {
            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "DressBlocked",
                    $"Obj={worldObject.Id.Value} Reason=OutfitLocked");
            }
            return false;
        }

        // §133: чужое надевают только с разрешения, и разрешение ОДНОРАЗОВОЕ —
        // сгорает здесь же. Планировщик до сюда чужое без «да» не пропускает;
        // это последний рубеж на случай, если вещь сменила хозяйку по дороге.
        var explicitPlayerDress = npc.Mind.ManualControl &&
            npc.Plan.Goal == GoalType.PlayerOrder;
        if (!restoringSelectedOutfit && !explicitPlayerDress &&
            ClothingOwnership.FellowOwner(world, npc, worldObject) is { } fellowOwner)
        {
            if (!PlanningSystem.HasWearGrant(npc, worldObject.Id, world.Tick))
            {
                worldObject.IsOccupied = false;
                worldObject.CurrentUser = null;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "DressBlocked",
                        $"Obj={worldObject.Id.Value} belongs to NPC{fellowOwner.Id.Value} " +
                        "without permission");
                }

                return false;
            }

            npc.Mind.WearGrants.RemoveAll(g => g.Item.Equals(worldObject.Id));
        }

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
            ResourceAmount = worldObject.ResourceAmount,
            WaterKind = worldObject.WaterKind,
            Dirtiness = worldObject.Dirtiness,
            Bloodiness = worldObject.Bloodiness,
            // §133: ничейное и трофейное становится её собственным, вещь
            // подруги остаётся подругиной (одолжила — не присвоила).
            OwnerId = ClothingOwnership.ResolveOnTake(world, npc, worldObject)
        });
        // Spec §52: putting the garment back on recovers whatever it
        // was carrying — the pockets pour into the pack (capacity just
        // grew by this garment's slots); anything still over spills.
        _dressPourScratch.Clear();
        _dressPourScratch.AddRange(worldObject.Contents);
        worldObject.Contents.Clear();
        if (restoringSelectedOutfit)
        {
            OutfitMaintenanceMath.MarkWorn(world, npc, worldObject.Id);
        }
        WorldObjectMutations.DespawnObject(world, worldObject.Id);
        EquipmentMath.Recalculate(world, npc);
        foreach (var stashed in _dressPourScratch)
        {
            GiveOrDrop(world, npc, stashed);
        }
        if (_dressPourScratch.Count > 0)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "StashRecovered",
                    $"{worldObject.DefinitionId} returned [{string.Join(",", _dressPourScratch)}]");
            }
        }
        // §52.9 r2: now that the new garment is on and the pack capacity
        // is live, put the displaced garment(s) away — into the pack if
        // a pocket is free, on the ground only when it is not. Items
        // that still fit stayed in the pack (backed by the new
        // garment's pockets); the true overflow rides down inside the
        // dropped piece (lowest importance first).
        StowDisplacedGarments(world, npc);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ItemWorn",
                $"Def={worldObject.DefinitionId} Worn=[{string.Join(",", npc.WornItems)}] " +
                $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
        }

        // Spec 42: one wardrobe stop per while — never chain-dress.
        // A cold girl with no real warmth in reach pinned Dress at
        // score 1.0 forever (thermal=1.00, exec=InProgress at soak
        // end) and starved the fire/water chain WITH THE LIGHTER IN
        // HER POCKET. The cooldown opens a window for TendFire &
        // GetWater between wardrobe attempts.
        npc.Mind.Cooldowns.RemoveAll(c => c.Goal == GoalType.Dress);
        if (!restoringSelectedOutfit)
        {
            npc.Mind.Cooldowns.Add(new GoalCooldown
            {
                Goal = GoalType.Dress,
                EndTick = world.Tick + AiBalance.DressCooldownTicks
            });
        }

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
        if (definition.HasTag("Boulder"))
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
        var leavesStump = definition.HasTag("Palm");
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
        if (definition.HasTag("Coconut"))
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
        var isCrown = definition.HasTag("PalmCrown");
        var yieldCount = 0;
        var sticks = 0;
        var boards = 0;
        foreach (var d in completedInteraction.Yields)
        {
            yieldCount += d.Count;
            if (d.DefinitionId == ContentIds.Stick) sticks += d.Count;
            if (d.DefinitionId == ContentIds.Board) boards += d.Count;
        }
        Trace.Emit(world, npc.Id, isCrown ? "CrownChopped" : "LogSplit",
            isCrown
                ? $"{worldObject.DefinitionId} -> {yieldCount} leaves"
                : $"{worldObject.DefinitionId} -> {sticks} sticks" +
                  (boards > 0 ? $" + {boards} boards" : string.Empty));
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
        var wasCorpse = definition.HasTag("Corpse");
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
            InventoryMath.RemoveReference(npc.WornItems, wetWorn);
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
            // §133: своя одежда на сушилке/в гардеробе остаётся своей —
            // сушится не «общественная», а конкретно её вещь.
            hung.Owner = wetWorn.OwnerId != 0
                ? new EntityId(wetWorn.OwnerId)
                : npc.Id;
            OutfitMaintenanceMath.TrackGroundPiece(world, npc, wetWorn, hung);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ItemHung",
                    $"{wetWorn.DefinitionId} Wetness={wetWorn.Wetness:F2} on rack " +
                    $"Obj={worldObject.Id.Value}");
            }
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
        var spoil = CorpseMath.NextSpoil(world, npc, worldObject, out var source);
        if (spoil is null)
        {
            // Кто-то успел раньше. Не провал плана — просто здесь уже пусто.
            worldObject.IsOccupied = false;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "LootEmpty",
                    $"NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"} has nothing left");
            }
            return true;
        }

        if (!InventoryMath.MakeRoomFor(world, npc, spoil.DefinitionId))
        {
            worldObject.IsOccupied = false;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "LootBlocked",
                    $"Def={spoil.DefinitionId} " +
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
                    $"({npc.Inventory.UsedSlots}/{npc.Inventory.Capacity})");
            }
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

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "MoveOnlyArrived",
                $"Junction={npc.Plan.TargetJunctionId?.Value.ToString() ?? "-"} " +
                $"Tile={npc.Tile.Q},{npc.Tile.R} (looking around)");
        }

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
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "HuntContinues",
                    "Arrived but the crab moved on — keep chasing");
            }
            return;
        }

        // §109.6 / bug #90: an Abuse prowl is a chain of move-only search
        // legs, not a finished intention. Clearing the goal on every arrival
        // opened one ordinary auction between legs: Drink won it, then Abuse
        // immediately won back, producing a deterministic 20-tick loop.
        // Planning owns the end of the search (NoMark / no next prowl point);
        // arrival owns only this leg while the drive still exists.
        if (npc.Mind.CurrentGoal == GoalType.Abuse && AbuseMath.Drive(npc) > 0f)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "AbuseProwlContinues",
                    "Arrived at search point — keep the Abuse intent for the next leg");
            }
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
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "GroupHuntContinues",
                    "Arrived but he moved on — keep after him");
            }
            return;
        }

        if (npc.Mind.CurrentGoal == GoalType.Expel &&
            (npc.Mind.ExpulsionTargetNpcId is not null ||
             npc.Mind.PendingExpulsionFrom is not null))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CampExpelContinues",
                    "Arrived but the live expulsion scene owns the goal");
            }
            return;
        }

        // §121: ТЕ ЖЕ ГРАБЛИ, третий раз (краб §29F.2, сговор §108, выгон
        // §117). Подход к цели приказа — тоже move-only план, и сброс в None
        // здесь гасил приказ атаки на первом же прибытии: она доходила до
        // соседней клетки, теряла цель и вставала столбом, хотя противник
        // стоял в шаге. Приказ кончает ManualOrderSystem — когда противник
        // мёртв, лежит или исчез, а не когда доигран один подход.
        if (npc.Mind.CurrentGoal == GoalType.PlayerAttack &&
            (npc.Mind.ManualAttackNpcId is not null || npc.Mind.ManualAttackMobId is not null))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ManualAttackContinues",
                    "Arrived — the player's attack order owns the goal");
            }
            return;
        }

        // §121: приказ ИДТИ, наоборот, здесь и заканчивается — «дошла» и есть
        // его исполнение. Цель гасится общим путём ниже, а ManualOrderSystem
        // на следующем среднем проходе только подтвердит, что приказ доигран.
        //
        // §121.7: завершение приказа продлевает lease внимания ЗДЕСЬ ЖЕ —
        // этот путь минует SweepFinishedOrder (цель уже None), и без штампа
        // поход длиннее таймаута «истекал» в момент прибытия: два тика спустя
        // её отпускало под ИИ.
        if (npc.Mind.CurrentGoal == GoalType.PlayerOrder)
        {
            ManualControlMath.RenewInactivityLease(world, npc);
        }

        npc.Mind.CurrentGoal = GoalType.None;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed (move-only plan arrived)");
        }
    }

    // Spec 29F: into the inventory, or at the feet when full.
    internal static void GiveOrDrop(WorldState world, NPCState npc, ItemInstance item)
    {
        if (InventoryMath.FitsWithoutEviction(world, npc, item.DefinitionId))
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
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "CarcassSpawned",
                $"{variant} carcass at Tile={tile.Q},{tile.R}");
        }
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

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanContinues",
                $"{completed} complete; next step={npc.Plan.Steps[nextInteract].Interaction} on {target.DefinitionId}");
        }
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
            dropped.WaterKind = item.WaterKind;
            dropped.Dirtiness = item.Dirtiness;
            dropped.Bloodiness = item.Bloodiness;
            // §133: владение переживает границу «надето/лежит» — вещь на земле
            // помнит хозяйку, поэтому подруга спросит разрешение, а не наденет.
            dropped.Owner = item.OwnerId != 0 ? new EntityId(item.OwnerId) : null;
            return dropped;
        }

        return null;
    }

    internal static bool TryFindDropSpotAtFeet(
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

    // §110.9: crying is a conscious choice of posture. If a free bed is
    // literally at hand, use its authored pose; otherwise use the same safe
    // ground solver as every other lying state.
    internal static bool TryLieDownForCrying(
        WorldState world, NPCState npc, int cryingUntilTick)
    {
        return LyingSpot.TryUseNearbyBed(world, npc, cryingUntilTick, out _) ||
            TryLieDownOnGround(world, npc);
    }

    // §60.2a r5: an involuntary fall prefers the ground on the CURRENT tile.
    // In a furnished one-hex house that has no legal ground rectangle, a free
    // bed at arm's reach is the only non-overlapping local surface; using it is
    // still a posture change, never a route to another hex.
    internal static bool TryLieDownForCollapse(
        WorldState world, NPCState npc, int restUntilTick)
    {
        return TryLieDownOnGround(world, npc) ||
            LyingSpot.TryUseNearbyBed(world, npc, restUntilTick, out _);
    }

    // §60/§29G/§40.13/§113 r2: one entry for every ground-lying state. It no
    // longer promises the centre: LyingSpot chooses among all 37 interior nodes
    // by full-body support/collision geometry. Beds use BedSleep.TryEnter instead.
    internal static bool TryLieDownOnGround(WorldState world, NPCState npc)
    {
        if (!LyingSpot.TrySolve(world, npc, out var placement))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "LieDownSpot",
                    $"Tile={npc.Tile.Q},{npc.Tile.R} Fit=NoSpace");
            }
            return false;
        }

        var radians = placement.Heading * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));

        npc.Position = placement.Position;
        npc.RotationDegrees = placement.Heading;
        npc.Movement.DesiredRotationDegrees = placement.Heading;
        npc.Movement.DesiredDirection = forward;
        ClaimLyingFootprint(world, npc);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "LieDownSpot",
                $"Tile={npc.Tile.Q},{npc.Tile.R} Node={placement.NodeSlot} " +
                $"Heading={placement.Heading:F0} Fit=Clear");
        }
        return true;
    }

    // §29G r4 / §113 r2: path avoidance follows the same oriented rectangle as
    // collision, including the supported part protruding onto a level neighbour.
    internal static void ClaimLyingFootprint(WorldState world, NPCState npc)
    {
        npc.ClaimedJunctions.Clear();
        var padding = HexSpatialMath.HexRadius / HexPointLayout.BoundaryRadius * 0.5f;
        for (var i = -1; i < HexDirection.All.Length; i++)
        {
            var coord = i < 0
                ? npc.Tile
                : new TileCoord(
                    npc.Tile.Q + HexDirection.All[i].DQ,
                    npc.Tile.R + HexDirection.All[i].DR);
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

                if (LyingSpot.ContainsBodyPoint(npc, junction.WorldPosition, padding) &&
                    !npc.ClaimedJunctions.Contains(junctionId))
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

    // Сколько тиков живёт бронь узла, на котором идёт сцена. Ровно столько же
    // просит планировщик, когда бронирует подход.
    internal const int SceneJunctionHoldTicks = 48;

    /// <summary>
    /// §111.13: удержать узел, на котором персонаж СЕЙЧАС работает, пока сцена
    /// не кончилась. Бронь выдаётся на 48 тиков, а помощь §53 длится 70, обыск
    /// §111 — до 240, лечение конечности и того дольше: всю вторую половину
    /// сцены узел формально свободен, и держала его только безусловная запись
    /// владельца. С одним участником это не всплывало; с несколькими второй
    /// пришедший забирал просроченную бронь, а освобождение первого становилось
    /// no-op. Поэтому бронь перевзводится в том же месте, где сцена и так
    /// повторяет свою позу каждый тик.
    /// </summary>
    internal static void HoldSceneJunction(WorldState world, NPCState npc)
    {
        if (npc.Plan.TargetJunctionId is not { } junction)
        {
            return;
        }

        SpatialMutations.TryReserveJunction(
            world, junction, npc.Id, world.Tick, SceneJunctionHoldTicks);
        SpatialMutations.TryOccupyJunction(world, junction, npc.Id);
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
        if (effects.HungerDelta != 0f)
        {
            RecordInteractionImpact(
                npc, NeedKind.Hunger, EffectKind.Eating, effects.HungerDelta < 0f);
        }
        if (effects.ThirstDelta != 0f)
        {
            RecordInteractionImpact(
                npc, NeedKind.Thirst, EffectKind.Drinking, effects.ThirstDelta < 0f);
        }
        // §42 / bug #150: positive Energy is sleep-only. Keep negative effects
        // data-compatible, but an old/external catalog cannot quietly turn a
        // chair, stump, meal or any future awake interaction into recovery.
        var energyDelta = effects.EnergyDelta <= 0f ||
            npc.Execution.CurrentInteraction == InteractionType.Sleep
                ? effects.EnergyDelta
                : 0f;
        npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + energyDelta * k);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + effects.ComfortDelta * k);
        if (energyDelta != 0f)
        {
            RecordInteractionImpact(
                npc,
                NeedKind.Energy,
                energyDelta > 0f ? EffectKind.Sleeping : EffectKind.Working,
                energyDelta > 0f);
        }
        if (effects.ComfortDelta != 0f)
        {
            RecordInteractionImpact(
                npc,
                NeedKind.Comfort,
                npc.Execution.CurrentInteraction == InteractionType.Sleep
                    ? EffectKind.Sleeping
                    : EffectKind.Resting,
                effects.ComfortDelta > 0f);
        }
        npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + effects.ThermalDelta * k);
        // Spec 31A.5A: warmth/armor are no longer touched here — they are
        // recomputed from the worn-items list by EquipmentMath.
    }

    private static void RecordInteractionImpact(
        NPCState npc,
        NeedKind need,
        EffectKind kind,
        bool positive)
    {
        npc.EffectImpacts.Record(
            need,
            kind,
            positive ? EffectImpactDirection.Positive : EffectImpactDirection.Negative,
            EffectImpactCadence.Fast);
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

    private static string GetPlannedInteractionId(NPCPlanState plan)
    {
        var start = plan.CurrentStepIndex;
        if (start < 0) start = 0;
        if (start > plan.Steps.Count) start = plan.Steps.Count;
        for (var i = start; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Type == PlanStepType.Interact)
            {
                return step.InteractionId;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Turns an actor smoothly away from a furniture anchor before the seat or
    /// bed animation starts.  The simulation position deliberately remains on
    /// the reserved rim until this method succeeds; only Sleep's existing
    /// <see cref="BedSleep.TryEnter"/> may move the body onto its
    /// authored bed pose afterwards.
    /// </summary>
    private static bool TryTurnBackToFurniture(
        WorldState world, NPCState npc, WorldObjectState worldObject,
        Float2 anchorPosition)
    {
        var away = HexSpatialMath.Normalize(new Float2(
            npc.Position.X - anchorPosition.X,
            npc.Position.Y - anchorPosition.Y));
        if (HexSpatialMath.Distance(away, Float2.Zero) <= 0.0001f)
        {
            // A malformed/legacy object can have its approach point on the
            // anchor.  Fall back to the authored furniture yaw rather than
            // inventing a zero-length direction; this is also the same yaw
            // LyingSpot uses when it commits a bed pose.
            away = new Float2(
                System.MathF.Cos(worldObject.RotationDegrees * System.MathF.PI / 180f),
                System.MathF.Sin(worldObject.RotationDegrees * System.MathF.PI / 180f));
        }

        var desired = HexSpatialMath.AngleDegrees(away);
        npc.Movement.DesiredDirection = away;
        npc.Movement.DesiredRotationDegrees = desired;

        var error = MathUtil.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, desired));
        if (error > 0.5f)
        {
            var turnPerTick = npc.TurnSpeed * SimBalance.BaseTurnSpeedFactor *
                AttributeMath.TurnSpeedMult(npc) * world.TickDeltaTime *
                // §50: доворот к станции у ползущей такой же медленный, как её шаг.
                npc.Body.MobilityTurnFactor();
            npc.RotationDegrees = MathUtil.RotateTowards(
                npc.RotationDegrees, desired, turnPerTick);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "FurnitureApproachTurning",
                    $"Obj={worldObject.DefinitionId} Rot={npc.RotationDegrees:F1} " +
                    $"Desired={desired:F1} Error={error:F1}");
            }

            return false;
        }

        npc.RotationDegrees = desired;
        return true;
    }

    // internal ради теста §54.2 r2: правило «фолбэк не валит деревья» проверяется
    // здесь напрямую — арена с подставленным планом молча проходила и на старом
    // коде, потому что до этой развилки исполнитель не доходил.
    internal static InteractionDefinition? ResolveInteraction(
        WorldState world, NPCState npc, ObjectDefinition definition, InteractionType? type,
        string interactionId = "")
    {
        if (type is null)
        {
            // ⭐ §54.2 r2 (баг #168): «план не назвал глагол» НЕ значит «делай с
            // объектом что угодно». У пальмы первый и единственный глагол —
            // chop.palm, то есть СВАЛИТЬ ЕЁ, а сюда приходит ветка добычи
            // «подойти и потрясти» из ForagePlan, которая шага Interact не
            // ставит вовсе. Замер (соак 120 000 тиков, сид 777): роща 17 -> 0,
            // и КАЖДАЯ пальма с 41 267-го тика упала под целью GetWater — то
            // есть колония вырубала свой источник воды, идя за водой. Ровно то,
            // на что жалуется игрок: «рубят все деревья и лишают себя кокосов».
            //
            // Harvest — глагол разрушительный и необратимый (пальма, валун,
            // юкка), и он обязан быть назван планом явно. Не нашлось другого —
            // взаимодействие не состоится, и вызывающий разберётся как с
            // пустышкой (пометит объект и возьмёт следующий).
            foreach (var fallback in definition.Interactions)
            {
                if (fallback.Type != InteractionType.Harvest)
                {
                    return fallback;
                }
            }

            return null;
        }

        InteractionDefinition first = null;
        InteractionDefinition executable = null;
        var wantsBoards = type == InteractionType.Process &&
            definition.Id == ContentIds.Log &&
            DecisionSystem.WoodenProstheticBoardShortfall(world, npc) > 0;
        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type != type.Value)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(interactionId))
            {
                if (interaction.Id == interactionId)
                {
                    return interaction;
                }

                continue;
            }

            first ??= interaction;
            var canExecute = interaction.RequiredCapabilities.Count == 0 ||
                DecisionSystem.HasAnyCapability(npc, interaction.RequiredCapabilities);
            if (!canExecute)
            {
                continue;
            }

            executable ??= interaction;
            if (wantsBoards && interaction.Yields.Exists(
                    drop => drop.DefinitionId == ContentIds.Board))
            {
                return interaction;
            }
        }

        if (!string.IsNullOrEmpty(interactionId))
        {
            return null;
        }

        // Returning the first matching action when none is executable keeps
        // the existing, specific "missing capability" failure message.
        return executable ?? first;
    }
}

}
