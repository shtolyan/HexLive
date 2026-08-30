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

public sealed partial class PlanningSystem : ISimulationSystem
{
    public string Name => nameof(PlanningSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // §60/§105/§110: authored stillness owns the body. In particular,
            // a coma must not rebuild Goal=None every medium tick, and a
            // crying breakdown must not restart the plan it just interrupted.
            if (WatchdogExclusions.IsAuthoredStillness(world, npc))
            {
                continue;
            }

            // §121: планами ручной колонистки владеют только приказы —
            // ManualCommandExecutor строит их сразу при получении команды, а
            // ManualOrderSystem пересобирает погоню. Планировщику здесь делать
            // нечего: он бы «вылечил» приказ, подставив ему свою цель.
            // §121.6 — исключение: авто-нужды (еда/питьё) планируются штатно,
            // теми же ветками, что у ИИ.
            if (Spec121.ManualControlEnabled && npc.Mind.ManualControl &&
                !NpcControlPolicy.MayPlanGoal(npc, npc.Mind.CurrentGoal))
            {
                continue;
            }

            // §118.4: RescueSystem installs recovery sleep after the carrier's
            // plan completes. It intentionally has no active plan of the
            // patient's own; treating it as an orphan woke her, released the
            // bed, and immediately auctioned the same rescue again.
            if (KenshiRescueMath.IsRecoveryResting(world, npc))
            {
                continue;
            }

            // §117 r2: once the approach has become a demand/fight, the
            // CampExpulsionSystem owns the scene clock and there is no next
            // movement plan to build.  The completed move-only approach used
            // to fall through here every Medium tick, producing a stream of
            // empty Expel plans until the answer beat ended.  The challenged
            // participant is likewise held by the scene, not by Planning.
            if (npc.Mind.CurrentGoal == GoalType.Expel &&
                (npc.Mind.PendingExpulsionFrom.HasValue ||
                 (npc.Mind.ExpulsionTargetNpcId.HasValue &&
                  npc.Mind.ExpulsionPhase > 0)))
            {
                continue;
            }

            // §24.10 r2: None has no plan, and a completed Idle plan already
            // says exactly what it should say. Rebuilding either every Medium
            // tick generated hundreds of empty PlanStarted records while a
            // character stood normally (and amplified the drowning case into
            // four log records per second) without changing a single action.
            if (npc.Mind.CurrentGoal == GoalType.None ||
                (npc.Mind.CurrentGoal == GoalType.Idle &&
                 npc.Plan.Goal == GoalType.Idle &&
                 npc.Plan.Status == PlanStatus.Completed))
            {
                continue;
            }

            if (npc.Plan.Status == PlanStatus.Active && npc.Plan.Goal == npc.Mind.CurrentGoal)
            {
                // Spec 29F.2: охота преследует ЖИВУЮ цель. Обычный скип держал
                // бы план на узел, где краб стоял при планировании, до самого
                // прихода — а краб прыгает каждый Medium-тик. Краб сдвинулся →
                // план падает насквозь в Hunt-блок и перестраивается (как
                // собачья погоня MobSystem.ChaseStep ре-путит каждый проход).
                // В полёте (HopTimer > 0) не ретаргетим — подождёт тик (§21.21B:
                // прыжок не должен пережить свой путь).
                var huntStale = npc.Mind.CurrentGoal == GoalType.Hunt &&
                    npc.Movement.HopTimer <= 0f &&
                    DecisionSystem.NearestVisibleRabbit(npc, world)?.Junction is { } rj &&
                    !(npc.Plan.TargetJunctionId is { } tj && tj.Equals(rj));

                // ⭐ §108: та же болезнь, что у краба, только заметнее — цель
                // ходит на своих двоих через весь остров. План вёл на клетку,
                // где чужак стоял в МОМЕНТ СГОВОРА (обычно у его лагеря), и
                // держался до самого прихода: тройка добегала до пустого места,
                // разминувшись с ним по дороге, и только там разворачивалась.
                // Со стороны это выглядело так, будто они его не узнали.
                var groupHuntStale = npc.Mind.CurrentGoal == GoalType.GroupHunt &&
                    npc.Movement.HopTimer <= 0f &&
                    npc.Mind.GroupHuntTargetNpcId is { } huntedId &&
                    world.Entities.Npcs.TryGetValue(huntedId, out var hunted) &&
                    hunted.CurrentJunction is { } huntedJunction &&
                    // Достала — хватит бежать: добегать до своей клетки, стоя в
                    // паре шагов от него, и есть то самое «прошла мимо».
                    (InteractionReach.CanStrike(world, npc, hunted) ||
                     !(npc.Plan.TargetJunctionId is { } gj &&
                       (gj.Equals(huntedJunction) ||
                        IsAdjacentJunction(world, gj, huntedJunction))));

                // §117: do not re-path on EVERY single junction crossed by a
                // walking intruder. Seed 632 rebuilt Expel 20 times in 96
                // ticks, repeatedly discarding a viable route. The scene
                // itself detects striking range; an active route only becomes
                // stale after the target has moved materially away from the
                // tile for which it was built.
                var expelStale = npc.Mind.CurrentGoal == GoalType.Expel &&
                    npc.Movement.HopTimer <= 0f &&
                    npc.Mind.ExpulsionTargetNpcId is { } expelledId &&
                    world.Entities.Npcs.TryGetValue(expelledId, out var expelled) &&
                    npc.Plan.TargetTile is { } expelledAtPlan &&
                    HexSpatialMath.HexDistance(expelledAtPlan, expelled.Tile) > 2;

                // §29C.4B: an on-station defender uses an explicit bounded
                // Wait step. Re-open it when its assist lock expires so the
                // live attacker is revalidated; otherwise keep the hold as one
                // plan instead of recreating an empty Completed plan forever.
                var defendHoldExpired = npc.Mind.CurrentGoal == GoalType.Defend &&
                    npc.Plan.Steps.Count > 0 &&
                    npc.Plan.Steps[npc.Plan.Steps.Count - 1].Type == PlanStepType.Wait &&
                    npc.Plan.Steps[npc.Plan.Steps.Count - 1].TimeoutEndTick is { } defendHoldEnd &&
                    world.Tick >= defendHoldEnd;

                if (!huntStale && !groupHuntStale && !expelStale && !defendHoldExpired)
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanSkipped",
                            $"ActivePlan already matches Goal={npc.Mind.CurrentGoal} Step={npc.Plan.CurrentStepIndex}/{npc.Plan.Steps.Count}");
                    }
                    continue;
                }

                // Мягкий сброс хода — образец «housemate repath» из
                // MovementSystem (§21.21B): иначе PathfindingSystem доиграет
                // старый маршрут до конца и только потом посмотрит на новую
                // цель. HopTimer уже проверен нулевым, оставшиеся hop-поля
                // перепишет следующий arm+launch.
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;
                npc.Movement.IsMoving = false;
                npc.Movement.SetStatus(MovementStatus.Waiting);
                npc.Movement.HopArmed = false;
                npc.Movement.HopPathIndex = -1;

                // Брошенный подход отдать сразу. У краба это не жало (кролик
                // один, охотница одна), а тройка вокруг ОДНОГО чужака делит
                // шесть соседних узлов: держать за собой те, что остались у
                // его прежнего места, значит отталкивать подруг в
                // NoFreeApproachJunction — на срок жизни резерва.
                if (groupHuntStale && npc.Plan.TargetJunctionId is { } droppedApproach)
                {
                    SpatialMutations.ReleaseJunctionReservation(world, droppedApproach, npc.Id);
                }
                if (expelStale && npc.Plan.TargetJunctionId is { } droppedExpelApproach)
                {
                    SpatialMutations.ReleaseJunctionReservation(world, droppedExpelApproach, npc.Id);
                }
            }

            var prevStatus = npc.Plan.Status;
            var prevGoal = npc.Plan.Goal;

            // ⭐ Взаимодействие живёт ровно столько, сколько план, который его
            // начал. Сюда мы попадаем ТОЛЬКО когда план перестал быть активным
            // (или разошёлся с целью), а значит начатое им действие осиротело:
            // ExecutionSystem его больше не тронет (первая же строка её цикла —
            // `Plan.Status != Active → continue`), доиграть и закрыться оно не
            // может, а сброса не было — смена цели чистит через Abort только
            // когда цель СМЕНИЛАСЬ (§23.17), и та же самая цель проходит мимо.
            //
            // Так рождался «поехавший по земле питьевой кокос»: план
            // ConsumeInventoryItem начал Drink, через два тика пробитый кокос
            // ушёл из рюкзака (ExecFailed → Plan=Failed), цель осталась Drink,
            // планировщик построил новый план — пеший, на 158 шагов, — и она
            // ехала через весь остров с CurrentInteraction=Drink: вид зеркалит
            // глагол каждый кадр, поэтому клип питья играл поверх ходьбы, а в
            // руке оставался тот же кокос. Заодно осиротевшее взаимодействие
            // держало занятость объекта и резервы — Abort отдаёт и их.
            if (npc.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.Replan,
                    $"Replan over a live {npc.Execution.CurrentInteraction} " +
                    $"(Goal={npc.Mind.CurrentGoal} PrevStatus={prevStatus})");
            }

            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetItemDefinitionId = null;
            npc.Plan.TargetAgentId = null;
            npc.Plan.RunRequested = false;
            npc.Plan.Goal = npc.Mind.CurrentGoal;

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanStarted",
                    $"Goal={npc.Mind.CurrentGoal} PrevGoal={prevGoal} PrevStatus={prevStatus}");
            }

            if (npc.Mind.CurrentGoal == GoalType.Eat)
            {
                // ⭐ §121.6 r2: у ручной обед — только из СВОЕГО рюкзака. Все
                // ветки ниже, которые ведут ногами в мир (вертел, земля,
                // пальма), для неё закрыты: пустой рюкзак значит «ждёт
                // приказа», а не «пошла за едой».
                var inventoryOnly = NpcControlPolicy.RequiresInventoryOnlySelfCare(npc);

                // Eating happens in place from inventory (spec 29B.3):
                // no target object, no junction reservation. §54.17: the MOST
                // NUTRITIOUS item, not the first — cooked meat beats the
                // coconut half that happened to enter the pack earlier.
                var foodDefinitionId = FoodMath.BestFoodInInventory(world, npc);
                // §54.17 r2: a roast on a perceived spit outranks the pack —
                // otherwise "coconut in hand" wins forever and the cooked
                // meat hangs untouched until it burns through colony turnover.
                // Bug #323: и РУЧНАЯ тоже — но только у близкого костра
                // (шаг к огню рядом — не «ушла сама»); дальний вертел для неё
                // по-прежнему закрыт §121.6.
                if (TryBuildSpitTakePlan(world, npc, foodDefinitionId,
                        inventoryOnly ? ManualSpitTakeRadiusTiles : int.MaxValue))
                {
                    continue;
                }

                if (foodDefinitionId is not null)
                {
                    npc.Plan.TargetItemDefinitionId = foodDefinitionId;
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.ConsumeInventoryItem,
                        Interaction = InteractionType.Eat
                    });
                    npc.Plan.CurrentStepIndex = 0;
                    npc.Plan.Status = PlanStatus.Active;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanBuilt",
                            $"Goal=Eat Item={foodDefinitionId} Steps=[ConsumeInventoryItem]");
                    }
                    continue;
                }

                if (BuildCoconutEatPlan(world, npc, inventoryOnly))
                {
                    continue;
                }

                npc.Plan.Status = PlanStatus.Failed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanFailed",
                        "Goal=Eat but no food in inventory");
                }
                continue;
            }

            // §119.2: an item-craft goal means "satisfy this item demand", not
            // necessarily "manufacture a fresh copy". RecipeCatalog supplies
            // the output id for every persistent item recipe, so this one seam
            // automatically covers bandages, rope, tools, prostheses and future
            // item recipes. Finished output wins over both an unfinished project
            // and a new ingredient bill.
            if (Content.RecipeCatalog.UsesPersistentProject(npc.Mind.CurrentGoal) &&
                CraftProjectMath.HasReachableCompletedOutput(
                    world, npc, npc.Mind.CurrentGoal))
            {
                if (TryBuildCompletedCraftOutputTakePlan(world, npc))
                {
                    continue;
                }

                // A visible result with no currently usable pickup (full pack,
                // lost rim, reservation race) must still suppress manufacturing.
                // Back off instead of converting that temporary problem into a
                // second physical item on the ground.
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanFailed",
                        $"Goal={npc.Mind.CurrentGoal} finished output visible but not takeable");
                }
                continue;
            }

            // §gear-craft: the recipe declares NO station — craft right where
            // she stands: a one-step in-place plan, no walk, no target object.
            if (Content.RecipeCatalog.UsesPersistentProject(npc.Mind.CurrentGoal) &&
                string.IsNullOrEmpty(Content.RecipeCatalog.StationOf(npc.Mind.CurrentGoal)))
            {
                var existingProject = CraftProjectMath.FindReachableProject(
                    world, npc, npc.Mind.CurrentGoal);
                if (existingProject != null && existingProject.Junctions.Count > 0)
                {
                    npc.Plan.TargetTile = existingProject.Tile;
                    npc.Plan.TargetJunctionId = existingProject.Junctions[0];
                }

                // §84: a pack SHORT of the bill may still be covered by pieces
                // already lying nearby (the just-cut yucca's fibers) — walk to
                // the pile and craft THERE; the craft-start beat takes the
                // pieces straight off the ground. Only the first short input
                // picks the walk (fiber recipes have a single input anyway).
                if (existingProject == null &&
                    Content.RecipeCatalog.ByGoal.TryGetValue(npc.Mind.CurrentGoal, out var inPlaceRecipe))
                {
                    foreach (var ing in inPlaceRecipe.Inputs)
                    {
                        var missing = ing.Count - DecisionSystem.CountInventory(npc, ing.Id);
                        if (missing <= 0)
                        {
                            continue;
                        }

                        var pile = DecisionSystem.FindGroundInputPile(npc, world, ing.Id, missing);
                        if (pile is not null &&
                            world.Entities.Objects.TryGetValue(pile.Id, out var pileObject) &&
                            pileObject.Junctions.Count > 0)
                        {
                            npc.Plan.TargetTile = pileObject.Tile;
                            npc.Plan.TargetJunctionId = pileObject.Junctions[0];
                        }

                        break;
                    }
                }

                npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.CraftInPlace });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanBuilt",
                        $"Goal={npc.Mind.CurrentGoal} Steps=[CraftInPlace]" +
                        (npc.Plan.TargetJunctionId is { } pileJ
                            ? $" (walk to ground pile Junction={pileJ.Value})"
                            : " (no station)"));
                }
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Drink)
            {
                // ⭐ §121.6 r2: у ручной питьё — только из СВОЕГО рюкзака.
                // Водосборник, кокос в мире и поход за помощью — это добыча,
                // и без приказа она за ней не идёт.
                var inventoryOnly = NpcControlPolicy.RequiresInventoryOnlySelfCare(npc);

                if (DecisionSystem.HasBottleWater(npc))
                {
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.DrinkBottle
                    });
                    npc.Plan.CurrentStepIndex = 0;
                    npc.Plan.Status = PlanStatus.Active;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanBuilt",
                            $"Goal=Drink Item=tool.bottle Steps=[DrinkBottle] Charges={npc.BottleCharges}");
                    }
                    continue;
                }

                var drinkDefinitionId = npc.Inventory.FindFirstDrink(world.Content);
                if (drinkDefinitionId is not null)
                {
                    npc.Plan.TargetItemDefinitionId = drinkDefinitionId;
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.ConsumeInventoryItem,
                        Interaction = InteractionType.Drink
                    });
                    npc.Plan.CurrentStepIndex = 0;
                    npc.Plan.Status = PlanStatus.Active;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanBuilt",
                            $"Goal=Drink Item={drinkDefinitionId} Steps=[ConsumeInventoryItem]");
                    }
                    continue;
                }

                // §54.15 r2: nothing drinkable in hand — the rain already
                // collected under the funnel is the nearest water there is, so
                // it beats walking to a coconut (and a full bottle that nobody
                // ever drew from was the whole station going to waste).
                if (!inventoryOnly && TryBuildCollectorDrawPlan(world, npc))
                {
                    continue;
                }

                if (BuildCoconutDrinkPlan(world, npc, inventoryOnly))
                {
                    continue;
                }

                // Jul 2026: a BLADELESS dehydrated girl far from the colony
                // cannot make water appear where she stands — but the camp can
                // (housemates craft knives and pierce coconuts day 0, and §53
                // Hydrate-aid needs her in sight). Walking home IS her drink
                // plan; the recurring seed-42 day-0.5 death was Marta chasing
                // distant tools at the island's rim while thirst hit 1.0.
                if (!inventoryOnly &&
                    !DecisionSystem.HasCoconutBlade(npc) &&
                    TryBuildSeekWaterHelpPlan(world, npc))
                {
                    continue;
                }

                // Jul 2026: cooldown on failure — without it a Drink the
                // availability scan still believes in re-fires every tick
                // (score ~2.15) and the girl stands in a PlanFailed loop
                // instead of letting GetWater/forage chains run.
                SetGoalCooldown(world, npc, GoalType.Drink);
                npc.Plan.Status = PlanStatus.Failed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanFailed",
                        "Goal=Drink but nothing drinkable in inventory");
                }
                continue;
            }

            // §54.15: rain water waiting in a collector beats foraging for a
            // coconut — draw from it first. Falls through to the coconut walk
            // when no collector holds a drinkable bottle.
            if (npc.Mind.CurrentGoal == GoalType.GetWater &&
                TryBuildCollectorDrawPlan(world, npc))
            {
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Socialize)
            {
                BuildTalkPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Romance)
            {
                BuildRomancePlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Aid)
            {
                BuildAidPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Abuse)
            {
                BuildAbusePlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.LootHelpless)
            {
                BuildLootHelplessPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Defend)
            {
                BuildDefendPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.GroupHunt)
            {
                BuildGroupHuntPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Expel)
            {
                BuildExpelPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Explore)
            {
                BuildExplorePlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.ReachSafeGround)
            {
                BuildReachSafeGroundPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Homeward)
            {
                BuildHomewardPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.CoolOff)
            {
                BuildCoolOffPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Bathe)
            {
                BuildBathePlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.WashClothes)
            {
                BuildWashClothesPlan(world, npc);
                continue;
            }

            // §133: прибраться — отнести забытую одежду к дому.
            if (npc.Mind.CurrentGoal == GoalType.StowClothes)
            {
                BuildStowClothesPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Hunt)
            {
                // Spec 29F.2: a move-only chase to the rabbit's junction;
                // the rabbit flees, re-planning produces a genuine pursuit.
                var rabbit = DecisionSystem.NearestVisibleRabbit(npc, world);
                if (rabbit is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Hunt);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Hunt NoVisibleRabbit");

                    }
                    continue;
                }

                npc.Plan.TargetJunctionId = rabbit.Junction;
                npc.Plan.TargetTile = rabbit.Tile;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = rabbit.Junction
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "HuntPlanned",
                        $"Rabbit={rabbit.Id} Tile={rabbit.Tile.Q},{rabbit.Tile.R}");
                }
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Prey)
            {
                // §56: a move-only stalk to the victim's junction — the strike is
                // resolved by PredationSystem once adjacent. Like the hunt, the
                // completion→rebuild cycle produces a genuine pursuit if the
                // victim moves. The kill drops a corpse the existing §54 Butcher
                // goal then processes into meat to eat.
                var victim = DecisionSystem.NearestPreyVictim(npc, world);
                if (victim?.CurrentJunction is not { } victimJunction)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Prey);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Prey NoReachableVictim");

                    }
                    continue;
                }

                npc.Plan.TargetJunctionId = victimJunction;
                npc.Plan.TargetTile = victim.Tile;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = victimJunction
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PreyPlanned",
                        $"Victim={victim.Id.Value} Tile={victim.Tile.Q},{victim.Tile.R}");
                }
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Raid)
            {
                // §72: a move-only stalk, like §56 Prey — RaidSystem resolves
                // the fight once he is adjacent, and the completion→rebuild
                // cycle IS the pursuit when she runs.
                //
                // Two deliberate differences from Prey. First he COMMITS to one
                // victim (Mind.RaidTargetNpcId): re-picking the weakest target
                // on every rebuild turns a stalk into dithering. Second he walks
                // to a junction BESIDE her, not onto hers — the §29C.4B Defend
                // plan learned that the hard way (Started→Arrived 17 times in
                // 68 ticks fighting the occupancy system for her own square).
                var raidVictim = ResolveRaidVictim(world, npc);
                if (raidVictim?.CurrentJunction is not { } raidVictimJunction)
                {
                    // Nobody worth taking in range — walk toward their camp and
                    // look again from there. This is what makes him a hunter
                    // rather than a hermit with a grudge.
                    if (TryBuildProwlPlan(world, npc))
                    {
                        continue;
                    }

                    npc.Plan.Status = PlanStatus.Failed;
                    AbandonRaid(world, npc, "NoVictim");
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Raid NoReachableVictim");

                    }
                    continue;
                }

                // Already beside her — the fight is on, no plan to build.
                if (npc.CurrentJunction is { } raiderJunction &&
                    IsAdjacentJunction(world, raiderJunction, raidVictimJunction))
                {
                    npc.Plan.Status = PlanStatus.Completed;
                    continue;
                }

                var approach = PickApproachJunction(world, npc, raidVictimJunction);
                if (approach is not { } raidApproach)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    AbandonRaid(world, npc, "NoApproach");
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Raid NoApproach");

                    }
                    continue;
                }

                npc.Plan.TargetJunctionId = raidApproach;
                npc.Plan.TargetTile = raidVictim.Tile;
                npc.Plan.TargetAgentId = raidVictim.Id;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = raidApproach
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "RaidPlanned",
                        $"Victim=NPC{raidVictim.Id.Value} Tile={raidVictim.Tile.Q},{raidVictim.Tile.R} " +
                        $"Opp={RaidMath.Opportunity(world, npc, raidVictim):F2}");
                }
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Undress)
            {
                var removable = DecisionSystem.FindRemovableItem(npc, world);
                if (removable is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Undress);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Undress NothingRemovable");

                    }
                    continue;
                }

                npc.Plan.TargetItemDefinitionId = removable;

                // §133: снятое не бросают там, где застала жара, — идут домой и
                // вешают в гардероб (или на сушилку). На месте раздеваются лишь
                // в крайнем случае: когда печёт уже опасно или дома нет.
                StowMath.UndressSpot? spot = null;
                if (npc.Needs.ThermalDiscomfort < Spec133.UndressAtHomeMaxDiscomfort)
                {
                    spot = StowMath.FindUndressSpot(world, npc);
                }

                if (spot is { } homeSpot &&
                    !(npc.CurrentJunction is { } here && here.Equals(homeSpot.Stand)))
                {
                    npc.Plan.TargetObjectId = homeSpot.StowObject;
                    npc.Plan.TargetJunctionId = homeSpot.Stand;
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.MoveToJunction,
                        TargetJunction = homeSpot.Stand
                    });
                }

                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.UndressItem,
                    Interaction = InteractionType.Undress,
                    TargetJunction = spot?.Stand,
                    TargetObject = spot?.StowObject
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanBuilt",
                        $"Goal=Undress Item={removable} " +
                        $"Stow={(spot?.StowObject is { } so ? so.Value.ToString() : "inPlace")} " +
                        $"Steps=[{(npc.Plan.Steps.Count > 1 ? "MoveToJunction," : string.Empty)}UndressItem]");
                }
                continue;
            }

            // §68 r2: a carried dressing is still used in place. With an empty
            // or full pack, however, a ready dressing in the craft ring can be
            // applied straight from the ground/pocket; a farther perceived one
            // gets a walk leg but never a PickUp leg.
            if (npc.Mind.CurrentGoal == GoalType.TreatWounds)
            {
                if (!TryBuildSelfTreatmentPlan(world, npc))
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.TreatWounds);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanFailed", "Goal=TreatWounds NoBandage");

                    }
                }
                continue;
            }

            // Spec 29G: no chair/bed among candidates -> rest on the land.
            if (npc.Mind.CurrentGoal == GoalType.Sit && !HasFurnitureCandidate(world, npc, InteractionType.Sit))
            {
                BuildGroundSitPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Sleep && !HasFurnitureCandidate(world, npc, InteractionType.Sleep))
            {
                BuildGroundSleepPlan(world, npc);
                continue;
            }

            // Spec 35.5: no free rack in sight -> stand by the lit fire
            // instead (x4 drying covers the whole outfit).
            if (npc.Mind.CurrentGoal == GoalType.DryClothes && !HasFreeRackCandidate(world, npc))
            {
                BuildFireDryPlan(world, npc);
                continue;
            }

            // §137: аукцион выбрал «ничего» — значит, самое время присесть.
            // Стоит ПЕРЕД проверкой на отсутствие взаимодействия ниже: у Idle
            // его нет и не будет (в GoalCatalog он не заведён нарочно, иначе
            // общий путь пошёл бы искать под этот глагол объект в мире).
            if (npc.Mind.CurrentGoal == GoalType.Idle)
            {
                BuildIdleRestPlan(world, npc);
                continue;
            }

            var interactionType = GoalToInteraction(npc.Mind.CurrentGoal);
            if (interactionType is null)
            {
                npc.Plan.Status = PlanStatus.Completed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanNoInteraction",
                        $"Goal={npc.Mind.CurrentGoal} has no mapped interaction (Idle?)");
                }
                continue;
            }

            var wearsBackpack = false;
            foreach (var worn in npc.WornItems)
            {
                if (world.Content.ObjectDefinitions.TryGetValue(worn.DefinitionId, out var definition) &&
                    definition.Layer == WearLayer.Bags)
                {
                    wearsBackpack = true;
                    break;
                }
            }
            var selectedOutfitTarget = interactionType == InteractionType.Dress &&
                npc.Mind.OutfitLocked
                    ? npc.Mind.OutfitMaintenanceTargetObjectId
                    : null;
            var preferBackpack = selectedOutfitTarget is null &&
                interactionType == InteractionType.Dress &&
                !wearsBackpack && DecisionSystem.KnowsReachableBackpack(npc, world);

            // Spec 29C.4A: a threatened underarmored NPC dressing up prefers
            // the best armor over the nearest garment.
            var preferArmor = selectedOutfitTarget is null &&
                interactionType == InteractionType.Dress &&
                npc.Memory.Dangers.Count > 0 && npc.EquippedArmor < 0.3f;
            // §133: при чужаке первым делом закрывают таз и грудь — и выбирают
            // по этому, а не по теплу и не по броне. Бельё здесь полноценный
            // ответ: оно ничего не греет и не защищает, но закрывает.
            var preferCover = selectedOutfitTarget is null &&
                interactionType == InteractionType.Dress &&
                ModestyMath.OutsiderKnown(world, npc) &&
                ModestyMath.MissingCover(world, npc) &&
                DecisionSystem.KnowsReachablePermittedCover(npc, world);
            // §55: boiling is retired — GetWater now just fetches the nearest
            // coconut to crack open (no boiled-vs-raw source preference).
            var preferBoiled = false;
            // §64: personal beds (SOFT). When choosing where to sleep, a colonist
            // heads for HER OWN bed first — but any free bed stays a valid
            // fallback (no hard filter), so a colonist whose bed isn't built yet
            // never ground-sleeps beside an empty bed. Survival is unchanged.
            var preferOwnBed = SpecDream.Enabled && interactionType == InteractionType.Sleep;
            // §54.17: food targets rank by hunger payoff, distance breaks ties —
            // the nearest-wins fallback made cooked meat invisible next to a
            // coconut. Raw meat's prospect depends on whether the colony can
            // actually roast it, so the fire is checked once, before the loop.
            var preferFood = npc.Mind.CurrentGoal == GoalType.GetFood;
            var preferProstheticBoard = npc.Mind.CurrentGoal == GoalType.GatherWood &&
                DecisionSystem.WoodenProstheticBoardShortfall(world, npc) > 0;
            var preferMedicalStick = npc.Mind.CurrentGoal == GoalType.GatherWood &&
                !preferProstheticBoard &&
                (InventoryMath.NeedsEmergencyCoconutBlade(world, npc) ||
                 DecisionSystem.SplintSupplyNeeded(world, npc));
            var preferCoconutBlade = npc.Mind.CurrentGoal == GoalType.GatherTools &&
                !DecisionSystem.HasCoconutBlade(npc) &&
                (npc.Mind.IsStarving || npc.Mind.IsDehydrated) &&
                DecisionSystem.HasCoconutOpportunity(npc, world);
            var fireUsable = false;
            if (preferFood)
            {
                var (_, fuel, fire) = DecisionSystem.FindCampfire(npc, world);
                fireUsable = fire != null && fuel > 0f && FoodMath.SpitHasFreeHook(fire);
            }

            PerceivedObject? selected = null;
            var selectedArmor = 0f;
            var selectedBoiled = false;
            var selectedMine = false;
            var selectedNutrition = 0f;
            var selectedProstheticBoard = false;
            var selectedOwnedBackpack = false;
            var selectedMedicalStick = false;
            var selectedCoconutBlade = false;
            var selectedDressAffinity = -1f;
            var selectedDressQuality = -1f;
            var selectedCover = 0; // §133: сколько стыдных мест закрывает кандидат
            var candidateCount = 0;
            var queuedBuildSiteId = npc.Mind.CurrentGoal == GoalType.BuildFurniture
                ? DecisionSystem.FindBuildSite(npc, world)?.Id
                : null;
            foreach (var perceived in npc.Perception.Objects)
            {
                if (!perceived.IsReachable ||
                    !DecisionSystem.ObjectUsableBy(perceived, npc.Id) ||
                    npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                    !perceived.AvailableInteractions.Contains(interactionType.Value))
                {
                    continue;
                }

                // §133.10: selected-outfit maintenance never substitutes a
                // warmer/closer garment for the exact piece it remembers.
                if (selectedOutfitTarget is { } selectedPiece &&
                    !perceived.Id.Equals(selectedPiece))
                {
                    continue;
                }

                // Spec 29E.4: per-goal target filtering by tags.
                if (!IsValidTargetFor(
                        world, npc, npc.Mind.CurrentGoal, perceived, queuedBuildSiteId))
                {
                    continue;
                }

                // §24.16 r3: the final stand point is part of availability,
                // even for a non-obstacle object whose anchor is entered
                // directly. SplitLog used to select a log under a housemate,
                // wait 41 ticks, abort, and select the same anchor again.
                if (!HasUsableObjectApproach(
                        world, npc, perceived, interactionType.Value))
                {
                    continue;
                }

                // §52: цель GatherTools может указывать на БРОШЕННУЮ ОДЕЖДУ, в
                // кармане которой лежит нужный инструмент, — забирают оттуда
                // только инструмент, саму куртку не поднимают. Спрашивать «влезет
                // ли куртка» здесь значило отбрасывать единственного кандидата,
                // которого ставка уже посчитала доступным: решение говорило
                // «иди возьми», планировщик отвечал «некуда», и цель молотила
                // вхолостую.
                var stashPickup = npc.Mind.CurrentGoal == GoalType.GatherTools &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var stashCandidate) &&
                    stashCandidate.Contents.Count > 0 &&
                    InventoryMath.StashHoldsWantedTool(world, npc, stashCandidate);

                if (interactionType == InteractionType.PickUp && !stashPickup &&
                    !InventoryMath.CanMakeRoomForGoal(
                        world, npc, npc.Mind.CurrentGoal, perceived.DefinitionId))
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanCandidateSkipped",
                            $"Obj={perceived.Id.Value} Def={perceived.DefinitionId} NoRoomForImportance");
                    }
                    continue;
                }

                // Spec 29C.4A food avoidance: don't shop for food where the
                // dogs are. Starvation may override an old danger MEMORY, but
                // never the presence of a live predator at the target: that
                // was not desperation, it was a deterministic feeding loop.
                // §54.16: butchered meat is EXEMPT while no beast is actually
                // there. The kill site is stamped dangerous for 2400 ticks and
                // the meat rots in 1800 — the ban outlived the meal, so every
                // wolf the colony killed rotted where it fell (14 kills / 13
                // butcherings / 0 chunks cooked in the day-34 save). A stale
                // mark must not fence off the catch they just fought for; a
                // LIVE mob on the spot still does.
                var liveMobNearPickup = interactionType == InteractionType.PickUp &&
                    MobSystem.MobNear(world, perceived.Tile, 2);
                if (interactionType == InteractionType.PickUp &&
                    (liveMobNearPickup ||
                     (!npc.Mind.IsStarving && IsNearDanger(npc, perceived.Tile, 2) &&
                      !IsMeatSource(world, perceived))))
                {
                    continue;
                }

                // §84: чужая по полу вещь отсекается ДО всех прочих правил —
                // она для этого тела вообще не одежда. Здесь, в выборе цели
                // плана, стоит последний рубеж: аукцион мог позвать одеваться
                // из-за другой вещи, а по дороге подвернулась эта.
                if (interactionType == InteractionType.Dress &&
                    !Content.GarmentLibrary.FitsSex(npc.Sex, perceived.DefinitionId))
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanCandidateSkipped",
                            $"Obj={perceived.Id.Value} Def={perceived.DefinitionId} WrongSex");
                    }
                    continue;
                }

                // §52.7: a cold-driven Dress only targets a REAL warmth upgrade —
                // never walk to an identical/worse shirt (clamp-aware gain over
                // what she wears now). Armor-driven dressing (preferArmor) keeps
                // its own CandidateArmor gain rule below, untouched.
                var isBackpackCandidate = interactionType == InteractionType.Dress &&
                    world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var wearDefinition) &&
                    wearDefinition.Layer == WearLayer.Bags;
                if (preferBackpack && !isBackpackCandidate)
                {
                    continue;
                }

                if (interactionType == InteractionType.Dress && selectedOutfitTarget is null &&
                    !preferBackpack && !preferArmor && !preferCover &&
                    EquipmentMath.WarmthGainFromWearing(world, npc, perceived.DefinitionId) <
                        SimBalance.DressWarmthGainMin)
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanCandidateSkipped",
                            $"Obj={perceived.Id.Value} Def={perceived.DefinitionId} NoWarmthGain");
                    }
                    continue;
                }

                // §133: чужое надевают только с разрешения. Своё, ничейное и
                // трофейное (вещь чужака) проходят молча; вещь ЖИВОЙ подруги —
                // только если она уже сказала «да», иначе кандидат отпадает и
                // ниже, отдельным планом, к хозяйке идут спрашивать.
                if (interactionType == InteractionType.Dress &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var ownedCheck) &&
                    ClothingOwnership.FellowOwner(world, npc, ownedCheck) != null &&
                    !HasWearGrant(npc, perceived.Id, world.Tick))
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanCandidateSkipped",
                            $"Obj={perceived.Id.Value} Def={perceived.DefinitionId} NotHers");
                    }
                    continue;
                }

                candidateCount++;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanCandidate",
                        $"Obj={perceived.Id.Value} Tile={perceived.Tile.Q},{perceived.Tile.R} " +
                        $"Dist={perceived.Distance:F2} Occupied={perceived.IsOccupied}");
                }

                if (selectedOutfitTarget is not null)
                {
                    selected = perceived;
                }
                else if (preferBackpack)
                {
                    var mine = world.Entities.Objects.TryGetValue(perceived.Id, out var backpack) &&
                        backpack.Owner == npc.Id;
                    if (selected is null ||
                        (mine && !selectedOwnedBackpack) ||
                        (mine == selectedOwnedBackpack && perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedOwnedBackpack = mine;
                    }
                }
                else if (preferCoconutBlade)
                {
                    var isBlade = ToolCandidateProvidesCapability(
                        world, perceived, Content.GearCapability.Cut);
                    if (selected is null ||
                        (isBlade && !selectedCoconutBlade) ||
                        (isBlade == selectedCoconutBlade &&
                         perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedCoconutBlade = isBlade;
                    }
                }
                else if (preferCover)
                {
                    // Сначала — сколько стыдных мест закроет, потом броня,
                    // дальше вкус и расстояние (та же лесенка, что у брони).
                    var cover = ModestyMath.CoverGainFromWearing(world, npc, perceived.DefinitionId);
                    if (cover <= 0)
                    {
                        continue;
                    }

                    var coverArmor = DecisionSystem.CandidateArmor(world, perceived, npc.Sex);
                    var coverAffinity = ItemAffinity.For(npc.Id.Value, perceived.DefinitionId);
                    if (selected is null || cover > selectedCover ||
                        (cover == selectedCover &&
                         (coverArmor > selectedArmor + 0.01f ||
                          (System.Math.Abs(coverArmor - selectedArmor) <= 0.01f &&
                           (coverAffinity > selectedDressAffinity + 0.0001f ||
                            (System.Math.Abs(coverAffinity - selectedDressAffinity) <= 0.0001f &&
                             perceived.Distance < selected.Distance))))))
                    {
                        selected = perceived;
                        selectedCover = cover;
                        selectedArmor = coverArmor;
                        selectedDressAffinity = coverAffinity;
                    }
                }
                else if (preferArmor)
                {
                    var armor = DecisionSystem.CandidateArmor(world, perceived, npc.Sex);
                    var affinity = ItemAffinity.For(npc.Id.Value, perceived.DefinitionId);
                    if (selected is null || armor > selectedArmor + 0.01f ||
                        (System.Math.Abs(armor - selectedArmor) <= 0.01f &&
                         (affinity > selectedDressAffinity + 0.0001f ||
                          (System.Math.Abs(affinity - selectedDressAffinity) <= 0.0001f &&
                           perceived.Distance < selected.Distance))))
                    {
                        selected = perceived;
                        selectedArmor = armor;
                        selectedDressAffinity = affinity;
                    }
                }
                else if (preferBoiled)
                {
                    var isBoiled = world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var d) &&
                        d.HasTag("Campfire");
                    if (selected is null ||
                        (isBoiled && !selectedBoiled) ||
                        (isBoiled == selectedBoiled && perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedBoiled = isBoiled;
                    }
                }
                else if (preferOwnBed)
                {
                    // Her own bed wins outright; otherwise nearest free bed.
                    var mine = world.Entities.Objects.TryGetValue(perceived.Id, out var bedObj) &&
                        bedObj.Owner is { } ow && ow.Equals(npc.Id);
                    if (selected is null ||
                        (mine && !selectedMine) ||
                        (mine == selectedMine && perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedMine = mine;
                    }
                }
                else if (preferFood)
                {
                    var nutrition = FoodMath.ProspectiveNutrition(world, perceived.DefinitionId, fireUsable);
                    if (selected is null || nutrition > selectedNutrition + 0.01f ||
                        (System.Math.Abs(nutrition - selectedNutrition) <= 0.01f &&
                         perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedNutrition = nutrition;
                    }
                }
                else if (preferProstheticBoard)
                {
                    var isBoard = perceived.DefinitionId == ContentIds.Board;
                    if (selected is null ||
                        (isBoard && !selectedProstheticBoard) ||
                        (isBoard == selectedProstheticBoard &&
                         perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedProstheticBoard = isBoard;
                    }
                }
                else if (preferMedicalStick)
                {
                    var isStick = perceived.DefinitionId == ContentIds.Stick;
                    if (selected is null ||
                        (isStick && !selectedMedicalStick) ||
                        (isStick == selectedMedicalStick &&
                         perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedMedicalStick = isStick;
                    }
                }
                else if (interactionType == InteractionType.Dress)
                {
                    // §75A: статы вещи первичны — реальный прирост тепла плюс
                    // броня; симпатия решает только между равноценными,
                    // расстояние последним.
                    var quality =
                        EquipmentMath.WarmthGainFromWearing(world, npc, perceived.DefinitionId) +
                        DecisionSystem.CandidateArmor(world, perceived, npc.Sex);
                    var affinity = ItemAffinity.For(npc.Id.Value, perceived.DefinitionId);
                    if (selected is null || quality > selectedDressQuality + 0.01f ||
                        (System.Math.Abs(quality - selectedDressQuality) <= 0.01f &&
                         (affinity > selectedDressAffinity + 0.0001f ||
                          (System.Math.Abs(affinity - selectedDressAffinity) <= 0.0001f &&
                           perceived.Distance < selected.Distance))))
                    {
                        selected = perceived;
                        selectedDressQuality = quality;
                        selectedDressAffinity = affinity;
                    }
                }
                else if (selected is null || perceived.Distance < selected.Distance)
                {
                    selected = perceived;
                }
            }

            if (selected is null)
            {
                // Jul 2026: GetWater forages too — a coconut IS the island's
                // water; walking to a remembered palm brings its dropped nuts
                // into perception exactly like the food walk does.
                if (npc.Mind.CurrentGoal is GoalType.GetFood or GoalType.GetWater)
                {
                    BuildForagePlan(world, npc);
                    continue;
                }

                // §133: одеться нечем, потому что всё вокруг — чужое. Значит
                // идём спрашивать разрешения у хозяйки, а не признаём провал.
                if (interactionType == InteractionType.Dress &&
                    TryBuildAskWearPermissionPlan(world, npc))
                {
                    continue;
                }

                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanFailed",
                        $"Goal={npc.Mind.CurrentGoal} Interaction={interactionType} " +
                        $"Candidates={candidateCount} NoSuitableObject");
                }
                continue;
            }

            // Resolve the target junction: from the live object when it exists,
            // from memory for remembered-but-unseen targets (the plan will walk
            // there and only then discover whether the belief was stale).
            JunctionId? targetJunction;
            if (world.Entities.Objects.TryGetValue(selected.Id, out var worldObject))
            {
                targetJunction = worldObject.Junctions.Count > 0 ? worldObject.Junctions[0] : null;
            }
            else if (selected.FromMemory &&
                     npc.Memory.KnownObjects.TryGetValue(selected.Id, out var rememberedTarget))
            {
                targetJunction = rememberedTarget.Junction;
            }
            else
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanFailed",
                        $"Goal={npc.Mind.CurrentGoal} Obj={selected.Id.Value} vanished and not remembered");
                }
                continue;
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanTargetSelected",
                    $"Obj={selected.Id.Value} Def={selected.DefinitionId} " +
                    $"Tile={selected.Tile.Q},{selected.Tile.R} Dist={selected.Distance:F2} " +
                    $"FromMemory={selected.FromMemory} FromCandidates={candidateCount}");
            }

            // Spec 31C.1: obstacle anchors are blocked — stand beside the
            // trunk, not inside it. Gathering a ground item (PickUp) also stands
            // BESIDE now: she walks up to the nearest cell next to the item and
            // collects from there instead of stepping onto it.
            if (interactionType == InteractionType.Craft &&
                world.Entities.Objects.TryGetValue(selected.Id, out var craftStation) &&
                craftStation.CraftJunction is { } authoredWorkPoint)
            {
                targetJunction = authoredWorkPoint; // §119: always the same side of the bench
            }

            // Furniture has one approach contract for both autonomous and
            // manual orders: seats and beds are never entered from their
            // anchor.  Pickups/water/obstacles keep the older beside rule.
            // Keeping this predicate shared is important: otherwise a manual
            // sleep could still use a different junction than an autonomous
            // sleep and the turn-back pose would start from the wrong side.
            if (targetJunction is { } anchorId &&
                RequiresBesideApproach(world, anchorId, interactionType.Value))
            {
                var besideReach = SpatialQueries.BesideReach(
                    world.Content.ObjectDefinitions.TryGetValue(selected.DefinitionId, out var besideDef)
                        ? besideDef.ObstacleRadius : 0f);
                if (!TryReserveBesideJunction(world, npc, anchorId, 48, out var beside,
                        besideReach, worldObject))
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "PlanFailed",
                            $"Goal={npc.Mind.CurrentGoal} Obj={selected.Id.Value} no free junction beside obstacle");
                    }
                    continue;
                }

                targetJunction = beside;
            }

            npc.Plan.TargetObjectId = selected.Id;
            npc.Plan.TargetTile = selected.Tile;
            npc.Plan.TargetJunctionId = targetJunction;

            if (targetJunction is { } jId &&
                !SpatialMutations.TryReserveJunction(world, jId, npc.Id, world.Tick, 48))
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ReservationFailed",
                        $"Junction={jId.Value} Already reserved or occupied");
                }
                continue;
            }

            if (targetJunction is { } reservedJId)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "JunctionReserved",
                        $"Junction={reservedJId.Value} Duration=48ticks Until={world.Tick + 48}");
                }
            }

            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = targetJunction,
                TargetObject = selected.Id
            });
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetObject = selected.Id,
                TargetJunction = targetJunction,
                Interaction = interactionType.Value
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanBuilt",
                    $"Goal={npc.Plan.Goal} Target={selected.DefinitionId} " +
                    $"Tile={selected.Tile.Q},{selected.Tile.R} Junction={Trace.FormatJunction(targetJunction)} " +
                    $"FromMemory={selected.FromMemory} Steps=[MoveToJunction,Interact]");
            }
        }
    }

    /// <summary>
    /// Builds the shared "take the product that already exists" plan for every
    /// persistent item recipe. The target may be the output itself or a dropped
    /// garment whose Contents holds it; completion extracts only the requested
    /// output from that container.
    /// </summary>
    private static bool TryBuildCompletedCraftOutputTakePlan(
        WorldState world, NPCState npc)
    {
        var goal = npc.Mind.CurrentGoal;
        var output = Content.RecipeCatalog.OutputOf(goal);
        if (string.IsNullOrEmpty(output) ||
            !CraftProjectMath.TryFindTakeableCompletedOutput(
                world, npc, goal, out var source) ||
            source.Junctions.Count == 0)
        {
            return false;
        }

        var anchor = source.Junctions[0];
        var reach = SpatialQueries.BesideReach(
            world.Content.ObjectDefinitions.TryGetValue(
                source.DefinitionId, out var definition)
                    ? definition.ObstacleRadius
                    : 0f);
        if (!TryReserveBesideJunction(
                world, npc, anchor, 48, out var beside, reach, source) ||
            !SpatialMutations.TryReserveJunction(
                world, beside, npc.Id, world.Tick, 48))
        {
            return false;
        }

        npc.Plan.TargetObjectId = source.Id;
        npc.Plan.TargetTile = source.Tile;
        npc.Plan.TargetJunctionId = beside;
        npc.Plan.TargetItemDefinitionId = output;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = beside,
            TargetObject = source.Id
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetObject = source.Id,
            TargetJunction = beside,
            Interaction = InteractionType.PickUp
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CraftOutputTakePlanned",
                $"Goal={goal} Output={output} Source={source.Id.Value} " +
                $"SourceDef={source.DefinitionId} " +
                $"Container={source.DefinitionId != output} Junction={beside.Value}");
        }
        return true;
    }

    // §121.9: internal — ручной приказ «перевязаться» ставит тот же план.
    internal static bool TryBuildSelfTreatmentPlan(WorldState world, NPCState npc)
    {
        WorldObjectState source = null;
        if (MedicalSupplyMath.BandageCount(npc) <= 0 &&
            !MedicalSupplyMath.TryFindReachableBandageSource(world, npc, out source))
        {
            return false;
        }

        npc.Plan.TargetItemDefinitionId = ContentIds.Bandage;
        if (source != null)
        {
            npc.Plan.TargetObjectId = source.Id;
            npc.Plan.TargetTile = source.Tile;

            // The same ring-1 reach used by ground-input crafting: no cargo
            // transfer and no ceremonial one-step walk around an item already
            // at her knees. A farther perceived source uses the normal reserved
            // object approach before the treatment beat.
            if (HexSpatialMath.HexDistance(npc.Tile, source.Tile) > 1)
            {
                if (source.Junctions.Count == 0)
                {
                    return false;
                }

                var anchor = source.Junctions[0];
                var reach = SpatialQueries.BesideReach(
                    world.Content.ObjectDefinitions.TryGetValue(
                        source.DefinitionId, out var definition)
                            ? definition.ObstacleRadius
                            : 0f);
                if (!TryReserveBesideJunction(
                        world, npc, anchor, 48, out var beside, reach, source) ||
                    !SpatialMutations.TryReserveJunction(
                        world, beside, npc.Id, world.Tick, 48))
                {
                    return false;
                }

                npc.Plan.TargetJunctionId = beside;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = beside,
                    TargetObject = source.Id
                });
            }
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.TreatSelf,
            TargetObject = source?.Id,
            TargetJunction = npc.Plan.TargetJunctionId,
            Interaction = InteractionType.TreatSelf
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=TreatWounds InventoryBandages={MedicalSupplyMath.BandageCount(npc)} " +
                $"Source={source?.Id.Value.ToString() ?? "inventory"} " +
                $"Steps=[{(npc.Plan.Steps.Count > 1 ? "MoveToJunction," : string.Empty)}TreatSelf]");
        }
        return true;
    }

    // One source of truth for object approach geometry.  Sit/Sleep always use
    // the free rim, even when an integrated bed leaves its anchor unblocked;
    // the anchor is a pose marker, not a place where a standing body may wait.
    internal static bool RequiresBesideApproach(
        WorldState world, JunctionId anchor, InteractionType interaction)
    {
        if (interaction is InteractionType.Sit or InteractionType.Sleep or InteractionType.PickUp)
        {
            return true;
        }

        if (SpatialQueries.IsAllWaterJunction(world, anchor))
        {
            return true;
        }

        return world.Junctions.Items.TryGetValue(anchor, out var anchorJunction) &&
            anchorJunction.Blocked;
    }

    // §121: internal, потому что тем же выбором клетки на ободе пользуется
    // приказ игрока (ManualCommandExecutor). Развести их значило бы дать
    // ручной колонистке ДРУГОЙ способ подходить к пальме, чем автоматической.
    internal static bool TryReserveBesideJunction(
        WorldState world,
        NPCState npc,
        JunctionId anchorId,
        int durationTicks,
        out JunctionId beside,
        float maxBesideDist = float.MaxValue,
        // §26.6A r5: the object this rim serves — only ITS footprint may be
        // crossed on the way to the rim. Null means "cross nothing".
        WorldObjectState owner = null)
    {
        var rimScratch = world.Caches.ObjectApproachJunctionsScratch;
        SpatialQueries.CollectStandableAround(world, anchorId, rimScratch, 96, maxBesideDist, owner,
            InteractionReach.RimMode);
        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, npc);
        if (npc.CurrentJunction is { } current && rimScratch.Contains(current) &&
            !occupiedByActor.Contains(current))
        {
            beside = current;
            return true;
        }

        rimScratch.Sort((a, b) =>
        {
            var da = world.Junctions.Items.TryGetValue(a, out var ja)
                ? HexSpatialMath.Distance(ja.WorldPosition, npc.Position) : float.MaxValue;
            var db = world.Junctions.Items.TryGetValue(b, out var jb)
                ? HexSpatialMath.Distance(jb.WorldPosition, npc.Position) : float.MaxValue;
            return da.CompareTo(db);
        });

        foreach (var rim in rimScratch)
        {
            if (!occupiedByActor.Contains(rim) &&
                CanUseApproachJunction(world, npc, rim) &&
                SpatialMutations.TryReserveJunction(world, rim, npc.Id, world.Tick, durationTicks))
            {
                beside = rim;
                return true;
            }
        }

        beside = default;
        return false;
    }

    /// <summary>Non-mutating half of <see cref="TryReserveBesideJunction"/>.
    /// Decision calls this before bidding for an object goal, so a visible
    /// hammer boxed in by bodies/furniture is not selected and failed every
    /// four ticks.  Geometry, route and live reservations deliberately mirror
    /// the real planner.</summary>
    private static bool HasUsableBesideJunction(
        WorldState world,
        NPCState npc,
        JunctionId anchorId,
        float maxBesideDist,
        WorldObjectState owner)
    {
        var rimScratch = world.Caches.ObjectApproachJunctionsScratch;
        SpatialQueries.CollectStandableAround(world, anchorId, rimScratch, 96, maxBesideDist, owner,
            InteractionReach.RimMode);
        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, npc);

        if (npc.CurrentJunction is { } current && rimScratch.Contains(current) &&
            !occupiedByActor.Contains(current))
        {
            return true;
        }

        foreach (var rim in rimScratch)
        {
            if (!occupiedByActor.Contains(rim) &&
                CanUseApproachJunction(world, npc, rim) &&
                (!world.Reservations.Junctions.TryGetValue(rim, out var reservation) ||
                 reservation.Owner == npc.Id || reservation.EndTick < world.Tick))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUsableObjectApproach(
        WorldState world,
        NPCState npc,
        PerceivedObject perceived,
        InteractionType interaction)
    {
        // A remembered object has no live footprint, but its remembered anchor
        // still has to belong to a component this body can reach. Returning
        // unconditional true here made HarvestYucca select the same target
        // behind a lost jump after every short cooldown (12 path failures in
        // seed 31337). Existence stays a belief until arrival; topology does not.
        if (!world.Entities.Objects.TryGetValue(perceived.Id, out var target) ||
            target.Junctions.Count == 0)
        {
            if (!perceived.FromMemory)
            {
                return true;
            }

            if (!npc.Memory.KnownObjects.TryGetValue(perceived.Id, out var remembered) ||
                remembered.Junction is not { } rememberedJunction ||
                npc.CurrentJunction is not { } rememberedFrom)
            {
                return false;
            }

            return Connectivity.Reachable(
                world, rememberedFrom, rememberedJunction,
                CanUseRoutineTraversal(npc));
        }

        var anchor = interaction == InteractionType.Craft &&
            target.CraftJunction is { } authoredWorkPoint
                ? authoredWorkPoint
                : target.Junctions[0];
        if (RequiresBesideApproach(world, anchor, interaction))
        {
            var reach = SpatialQueries.BesideReach(
                world.Content.ObjectDefinitions.TryGetValue(
                    perceived.DefinitionId, out var definition)
                        ? definition.ObstacleRadius
                        : 0f);
            return HasUsableBesideJunction(world, npc, anchor, reach, target);
        }

        if (PathfindingSystem.OtherActorJunctions(world, npc).Contains(anchor))
        {
            return false;
        }

        if (world.Reservations.Junctions.TryGetValue(anchor, out var reservation) &&
            reservation.Owner != npc.Id && reservation.EndTick >= world.Tick)
        {
            return false;
        }

        return npc.CurrentJunction is not { } from ||
            Connectivity.Reachable(world, from, anchor, CanUseRoutineTraversal(npc));
    }

    /// <summary>Routine errands stop taking jump shortcuts once a leg enters
    /// the safe-ground warning band. Flee/critical exploration and the explicit
    /// ReachSafeGround plan retain their emergency traversal permission.</summary>
    internal static bool CanUseRoutineTraversal(NPCState npc) =>
        npc.Body.CanJump &&
        System.MathF.Min(
            npc.Body.LimbFunction(BodyPart.LegL),
            npc.Body.LimbFunction(BodyPart.LegR)) >= AiBalance.SafeGroundLegAlert;

    private static bool CanUseApproachJunction(WorldState world, NPCState npc, JunctionId rim) =>
        SpatialQueries.IsJunctionFree(world, rim) &&
        (npc.CurrentJunction is not { } from ||
         Connectivity.Reachable(world, from, rim, CanUseRoutineTraversal(npc)));

    private static string FormatInteractions(InteractionType[] interactions)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < interactions.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(interactions[i]);
        }

        return text.ToString();
    }

    private static bool JunctionAvailableFor(WorldState world, JunctionId junction, EntityId npc)
    {
        return !world.Occupancy.JunctionOwner.TryGetValue(junction, out var owner) ||
               owner is null || owner.Value == npc;
    }

    // Spec 29G: a junction on a boundary with >=1 level difference.
    internal static bool IsLedge(WorldState world, Junction junction)
    {
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var coord in junction.Tiles)
        {
            if (world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                min = System.Math.Min(min, tile.Elevation);
                max = System.Math.Max(max, tile.Elevation);
            }
        }

        return max - min >= 1;
    }

    internal static bool IsLedgeId(WorldState world, JunctionId id)
    {
        return world.Junctions.Items.TryGetValue(id, out var junction) && IsLedge(world, junction);
    }

    // Picks one stable point per hex edge: the lattice point nearest the
    // midpoint shared by the upper/dry tile and the lower/water tile. Choosing
    // merely the nearest ledge junction made sitters drift toward edge corners.
    // Facing is the tile-centre normal, exactly perpendicular to that edge.
    internal static bool TryGetEdgeSeatGeometry(
        WorldState world, Junction junction, bool waterOnly,
        out TileCoord standTile, out Float2 facing)
        => TryGetEdgeSeatGeometry(
            world, junction, waterOnly, requireCanonical: true, out standTile, out facing);

    // Bug #332: каноничность — правило ПЛАНИРОВЩИКА («куда сесть»), а не позы.
    // Уже сидящая на НЕканоническом джанкшене шва (ручное «присесть», §137
    // Idle-отдых, снос прибытия) без этой оговорки теряла весь подъём на
    // уступ и проваливалась телом в верхний гекс.
    internal static bool TryGetEdgeSeatGeometry(
        WorldState world, Junction junction, bool waterOnly, bool requireCanonical,
        out TileCoord standTile, out Float2 facing)
    {
        standTile = default;
        facing = Float2.Zero;
        if (junction.Blocked || junction.Tiles.Count < 2)
        {
            return false;
        }

        Tile high = null;
        Tile low = null;
        var bestPairDistance = float.MaxValue;
        foreach (var aCoord in junction.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(aCoord, out var a))
            {
                continue;
            }

            foreach (var bCoord in junction.Tiles)
            {
                if (aCoord == bCoord || !world.Tiles.Items.TryGetValue(bCoord, out var b))
                {
                    continue;
                }

                Tile pairHigh;
                Tile pairLow;
                if (waterOnly)
                {
                    if (a.Flags.HasFlag(TileFlags.Water) ||
                        !a.Flags.HasFlag(TileFlags.Walkable) ||
                        !b.Flags.HasFlag(TileFlags.Water))
                    {
                        continue;
                    }

                    pairHigh = a;
                    pairLow = b;
                }
                else
                {
                    // §31C / bug #192 rework: a junction may touch e0/e1/e2,
                    // but the seat is still the clean one-step e2/e1 EDGE at
                    // that point. Never choose the tempting e2/e0 pair; its
                    // two-step height made the first pose depend on which tile
                    // the path happened to leave under the actor. Returning
                    // pairHigh also makes the snapshot lift the pose to e2.
                    if (a.Elevation <= b.Elevation ||
                        !a.Flags.HasFlag(TileFlags.Walkable) ||
                        a.Elevation - b.Elevation != 1)
                    {
                        continue;
                    }

                    pairHigh = a;
                    pairLow = b;
                }

                var highCenter = HexSpatialMath.TileToWorld(pairHigh.Coord);
                var lowCenter = HexSpatialMath.TileToWorld(pairLow.Coord);
                var midpoint = (highCenter + lowCenter) * 0.5f;
                var pairDistance = HexSpatialMath.Distance(junction.WorldPosition, midpoint);
                if (pairDistance < bestPairDistance)
                {
                    bestPairDistance = pairDistance;
                    high = pairHigh;
                    low = pairLow;
                }
            }
        }

        if (high is null || low is null)
        {
            return false;
        }

        var edgeMidpoint = (HexSpatialMath.TileToWorld(high.Coord) +
                            HexSpatialMath.TileToWorld(low.Coord)) * 0.5f;
        var canonical = junction.Id;
        var canonicalDistance = float.MaxValue;
        foreach (var id in high.Junctions)
        {
            if (!low.Junctions.Contains(id) ||
                !world.Junctions.Items.TryGetValue(id, out var shared) || shared.Blocked)
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(shared.WorldPosition, edgeMidpoint);
            if (distance < canonicalDistance - 0.0001f ||
                System.MathF.Abs(distance - canonicalDistance) <= 0.0001f && id.Value < canonical.Value)
            {
                canonicalDistance = distance;
                canonical = id;
            }
        }

        if (requireCanonical && canonical != junction.Id)
        {
            return false;
        }

        standTile = high.Coord;
        facing = HexSpatialMath.Normalize(
            HexSpatialMath.TileToWorld(low.Coord) - HexSpatialMath.TileToWorld(high.Coord));
        return HexSpatialMath.Distance(facing, Float2.Zero) > 0.0001f;
    }

    // Spec 29C.4A: is this tile within `radius` of a fresh danger memory?
    private static bool IsNearDanger(NPCState npc, TileCoord tile, int radius)
    {
        foreach (var danger in npc.Memory.Dangers)
        {
            if (HexSpatialMath.HexDistance(tile, danger.Tile) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    // §54.16: the spoils of a fight — the only food that lands ON a danger
    // mark by construction. The chunk drops where the beast died; the SPIT
    // then holds the roast, and a wolf killed near the hearth (the common
    // case — they come for the colony) puts the fire itself inside the ring,
    // so the take-from-spit half needs the same exemption or the meat roasts
    // and hangs there untouched.
    private static bool IsMeatSource(WorldState world, PerceivedObject perceived)
    {
        if (perceived.DefinitionId is ContentIds.MeatRaw or ContentIds.MeatCooked)
        {
            return true;
        }

        return world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition) &&
            definition.HasTag("Campfire") &&
            world.Entities.Objects.TryGetValue(perceived.Id, out var fire) &&
            BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked) > 0;
    }

    // Spec 23.10: a failed plan puts its goal on cooldown so the NPC does
    // something else instead of hammering the same target.
    // Переехала в AiBalance: тот же срок применяется к завершённому уходу
    // за собой (RestHygiene), и приватной константой планировщика он быть
    // перестал — второе место жило голым числом 40.
    private static int FailureCooldownTicks => AiBalance.FailureCooldownTicks;

    internal static void SetGoalCooldown(WorldState world, NPCState npc, GoalType goal) =>
        SetGoalCooldown(world, npc, goal, FailureCooldownTicks);

    /// <summary>External event producers (help cries, witnessed assaults,
    /// threat alerts) must obey the same hard gate as the decision auction.
    /// Otherwise an event raised every medium pass can immediately resurrect
    /// the goal that pathfinding has just rejected.</summary>
    internal static bool IsGoalOnCooldown(NPCState npc, GoalType goal, int currentTick) =>
        npc.Mind.Cooldowns.Exists(c => c.Goal == goal && c.EndTick > currentTick);

    /// <summary>
    /// Тот же кулдаун, но на заданный срок. Понадобился §122: сорок тиков петля
    /// не замечает — она их и так пережила пять раз подряд, — а лестнице выхода
    /// нужен срок, за который аукцион успеет заняться другим делом. Отдельным
    /// механизмом это делать нельзя: снятие замка ниже — часть контракта
    /// (§23.10), и вторая реализация однажды его забудет.
    /// </summary>
    internal static void SetGoalCooldown(WorldState world, NPCState npc, GoalType goal,
        int ticks)
    {
        if (goal == GoalType.Idle || goal == GoalType.None)
        {
            return;
        }

        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = goal,
            EndTick = world.Tick + ticks
        });

        // A failed goal must not be defended by its own lock — otherwise the
        // hold rule keeps the zero-scored goal and planning hammers the same
        // target until the lock expires.
        if (npc.Mind.GoalLock is { } goalLock && goalLock.Goal == goal)
        {
            npc.Mind.GoalLock = null;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "GoalCooldownSet",
                $"{goal} on cooldown until tick {world.Tick + ticks}");
        }
    }

    // §4-колонка: 41-рукавный switch переехал в GoalCatalog. Знание «какое
    // взаимодействие исполняет эту цель» — свойство ЦЕЛИ, и жить ему положено
    // в одной строке таблицы рядом с остальными её свойствами, а не отдельным
    // списком, про который надо помнить.
    private static InteractionType? GoalToInteraction(GoalType goal) =>
        AI.GoalCatalog.InteractionFor(goal);

    /// <summary>Non-mutating mirror of the generic planner's common object
    /// filters. Decision uses it for gather/build bids so Candidates=0 cannot
    /// be the normal result immediately after the auction selected a goal.</summary>
    internal static bool HasObjectCandidateForGoal(
        WorldState world, NPCState npc, GoalType goal, ObjectId? requiredTarget = null)
    {
        var interactionType = GoalToInteraction(goal);
        if (interactionType is null)
        {
            return false;
        }

        var queuedBuildSiteId = goal == GoalType.BuildFurniture
            ? requiredTarget ?? DecisionSystem.FindBuildSite(npc, world)?.Id
            : null;
        foreach (var perceived in npc.Perception.Objects)
        {
            if (requiredTarget is { } required && !perceived.Id.Equals(required) ||
                !perceived.IsReachable ||
                !DecisionSystem.ObjectUsableBy(perceived, npc.Id) ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !perceived.AvailableInteractions.Contains(interactionType.Value) ||
                !IsValidTargetFor(world, npc, goal, perceived, queuedBuildSiteId))
            {
                continue;
            }

            var stashPickup = goal == GoalType.GatherTools &&
                world.Entities.Objects.TryGetValue(perceived.Id, out var stash) &&
                stash.Contents.Count > 0 &&
                InventoryMath.StashHoldsWantedTool(world, npc, stash);
            if (interactionType == InteractionType.PickUp && !stashPickup &&
                !InventoryMath.CanMakeRoomForGoal(
                    world, npc, goal, perceived.DefinitionId))
            {
                continue;
            }

            if (interactionType == InteractionType.PickUp &&
                (MobSystem.MobNear(world, perceived.Tile, 2) ||
                 (!npc.Mind.IsStarving && IsNearDanger(npc, perceived.Tile, 2) &&
                  !IsMeatSource(world, perceived))))
            {
                continue;
            }

            // Mirror the generic planner all the way through the exact stand
            // point. Perception's IsReachable deliberately answers only the
            // broader terrain question and ignores temporary actors/claims.
            if (!HasUsableObjectApproach(
                    world, npc, perceived, interactionType.Value))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>Exact GatherTools candidate for a required verb. This is used
    /// by survival chains whose missing tool is semantically specific: a
    /// lighter or saw is still a valid ordinary tool upgrade, but cannot be
    /// advertised as the blade that opens food.</summary>
    internal static bool HasToolCandidateWithCapability(
        WorldState world, NPCState npc, Content.GearCapability capability)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable ||
                !DecisionSystem.ObjectUsableBy(perceived, npc.Id) ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !perceived.AvailableInteractions.Contains(InteractionType.PickUp) ||
                !IsValidTargetFor(world, npc, GoalType.GatherTools, perceived) ||
                !ToolCandidateProvidesCapability(world, perceived, capability) ||
                !HasUsableObjectApproach(world, npc, perceived, InteractionType.PickUp))
            {
                continue;
            }

            var stashPickup = world.Entities.Objects.TryGetValue(
                    perceived.Id, out var stash) &&
                stash.Contents.Count > 0;
            if (stashPickup ||
                InventoryMath.CanMakeRoomFor(world, npc, perceived.DefinitionId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ToolCandidateProvidesCapability(
        WorldState world, PerceivedObject perceived,
        Content.GearCapability capability)
    {
        if (Content.GearCatalog.For(perceived.DefinitionId).Has(capability))
        {
            return true;
        }

        if (!world.Entities.Objects.TryGetValue(perceived.Id, out var stash))
        {
            return false;
        }

        foreach (var item in stash.Contents)
        {
            if (Content.GearCatalog.For(item.DefinitionId).Has(capability))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidTargetFor(
        WorldState world, NPCState npc, GoalType goal, PerceivedObject perceived,
        ObjectId? queuedBuildSiteId = null)
    {
        if (world.Entities.Objects.TryGetValue(perceived.Id, out var liveTarget) &&
            liveTarget.IsCraftProject)
        {
            return false; // §119: 0..99% output is visible, never usable/pickable
        }

        if (!world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition))
        {
            return false;
        }

        switch (goal)
        {
            case GoalType.GetFood:
                // §54.14 (r2): cooked meat hanging on the spit is takeable food —
                // the campfire itself becomes a GetFood target while any hangs.
                if (definition.HasTag("Campfire"))
                {
                    return world.Entities.Objects.TryGetValue(perceived.Id, out var spitSource) &&
                        BuildSiteMath.HangingMeat(spitSource, ContentIds.MeatCooked) > 0;
                }

                return definition.HasTag("Food") &&
                    (!definition.HasTag("Coconut") || HasCoconutBlade(npc));
            case GoalType.GatherWood:
                // Spec §54: wood on the ground. A stick is always useful (fuel,
                // slats, crafts). §54.12: a whole LOG only when logs are billed
                // somewhere (a site's log stage, the raft) or she can split it
                // into sticks — the bed's stick stage otherwise sent her home
                // hugging a useless log.
                if (!definition.HasTag("Wood"))
                {
                    return false;
                }

                // §126/§49 r2: здесь стояла ветка «перед сном бери ТОЛЬКО
                // палку» — часть ночного затвора, снятого вместе с ним. Общее
                // правило ниже (целое бревно оставить лежать как цель SplitLog)
                // и было настоящим содержанием этой ветки.
                if (!definition.HasTag("Log"))
                {
                    return true;
                }

                return Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood) ||
                    WholeLogsWanted(world, npc);
            case GoalType.SplitLog:
                // Spec §54: split a log lying on the ground into sticks.
                return definition.HasTag("Log");
            case GoalType.ChopCrown:
                // Spec §54.2: chop a felled palm crown into loose leaves.
                return definition.HasTag("PalmCrown");
            case GoalType.GatherLeaves:
                // Spec §54.2: pick a scattered palm leaf off the ground.
                return definition.HasTag("PalmLeaf");
            case GoalType.GatherTools:
                // §54.15: a bottle parked in the collector's vessel slot is
                // working furniture, not a dropped tool — retrieval is the
                // TakeVessel verb, never a PickUp scoop.
                if (world.Entities.Objects.TryGetValue(perceived.Id, out var maybeParked) &&
                    WaterCollectorMath.IsParked(world, maybeParked))
                {
                    return false;
                }

                // Only a tool that ADDS something: a verb the pack can't do
                // yet or a better weapon — no hoarding capability-duplicates.
                if (definition.HasTag("Tool") &&
                    !npc.Inventory.Items.Contains(perceived.DefinitionId) &&
                    Content.GearCatalog.AddsValueOver(
                        npc.Inventory.Items, perceived.DefinitionId, npc.Body.WeaponHands))
                {
                    return true;
                }

                // Spec §52: a dropped garment whose pockets hold a missing
                // tool is a valid target — she rifles the pockets on arrival.
                return world.Entities.Objects.TryGetValue(perceived.Id, out var stashContainer) &&
                    stashContainer.Contents.Count > 0 &&
                    InventoryMath.StashHoldsWantedTool(world, npc, stashContainer);
            case GoalType.GatherHerb:
                return definition.HasTag("Herb");
            case GoalType.HarvestYucca:
                return definition.HasTag("Yucca");
            case GoalType.GatherFiber:
                return definition.HasTag("Fiber");
            case GoalType.CookMeat:
                // §54.14 (r2): hanging meat needs a finished spit (stage 3)
                // with a free hook — any other lit fire won't do.
                return definition.HasTag("Campfire") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var spitFire) &&
                    spitFire.ResourceAmount > 0f &&
                    BuildSiteMath.CampfireSpitComplete(spitFire) &&
                    BuildSiteMath.HangingMeat(spitFire, ContentIds.MeatRaw) +
                    BuildSiteMath.HangingMeat(spitFire, ContentIds.MeatCooked) <
                    SimBalance.CampfireSpitCapacity;
            case GoalType.CraftRope:
            case GoalType.CraftCloth:
            case GoalType.CraftKnife:
            case GoalType.CraftBandage:
            case GoalType.CraftSplint:
            case GoalType.CraftWoodenArm:
            case GoalType.CraftWoodenLeg:
            case GoalType.TendFire:
            case GoalType.CraftSpear:
            case GoalType.CraftLeather:
            case GoalType.CraftAxe:
            case GoalType.CraftPickaxe:
            case GoalType.CraftRack:
            case GoalType.CraftBed:
            case GoalType.CraftTent:
            case GoalType.CraftBow:
            case GoalType.CraftArrows:
                var stationTag = RecipeCatalog.StationOf(goal);
                return !string.IsNullOrEmpty(stationTag) && definition.HasTag(stationTag);
            case GoalType.DryClothes:
                // §35.5B: only a rack with a free hanger slot (capacity 8).
                return definition.HasTag("Rack") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var rack) &&
                    !ExecutionSystem.RackIsFull(world, rack);
            case GoalType.GatherStone:
                return definition.HasTag("Stone");
            case GoalType.HarvestTree:
                return definition.HasTag("Palm");
            case GoalType.MineBoulder:
                return definition.HasTag("Boulder");
            case GoalType.Butcher:
                // Spec §54: an animal carcass any time; a housemate's body only
                // as a starvation last resort (cannibalism gate).
                if (definition.HasTag("Carcass"))
                {
                    return true;
                }

                return definition.HasTag("Corpse") &&
                    SimBalance.CannibalismEnabled &&
                    npc.Needs.Hunger >= SimBalance.CannibalizeHungerGate;
            case GoalType.Build:
                // Spec §52: the hut anchor only — a furniture site is a separate goal.
                // §72.13: и только СВОЯ — иначе чужак достраивал бы хижину колонии.
                return definition.HasTag("BuildSite") &&
                    !definition.HasTag("FurnitureSite") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var hutAnchor) &&
                    DecisionSystem.IsOurSite(world, npc, hutAnchor);
            case GoalType.BuildFurniture:
                // §54.13 r2: only a site this NPC can ADVANCE right now — she
                // carries a material its CURRENT stage accepts, or it is fully
                // stocked and ready to raise. Decision scores the goal against
                // the colony's ORDERED queue (FindBuildSite), but targeting
                // used to accept ANY FurnitureSite in view: with stones for
                // the fire's ring in hand she walked to the NEARER rack site
                // (which wants sticks), deposited nothing, completed the
                // 36-tick Build and looped — an empty ping-pong that starved
                // every site for whole 10-day soaks (rack 0/4 sticks, seeds
                // 12345/424242; FOCUS trace t5160-5237).
                // §72.13: и только СВОЯ стройка. Аукцион уже фильтрует очередь
                // (FindBuildSite → IsOurSite), но план брал БЛИЖАЙШИЙ валидный
                // сайт из восприятия — цель выиграна на своём, а материалы
                // уходили в чужой лагерь, стоило пройти рядом с ним.
                return queuedBuildSiteId is { } queuedSite &&
                    queuedSite.Equals(perceived.Id) &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var fsite) &&
                    BuildSiteMath.IsSite(fsite) &&
                    DecisionSystem.IsOurSite(world, npc, fsite) &&
                    (DecisionSystem.CarriesSiteMaterial(npc, fsite) ||
                     BuildSiteMath.IsStocked(fsite));
            case GoalType.BuildRaft:
                return definition.HasTag("Raft");
            case GoalType.Mourn:
                return CorpseMath.IsHumanDead(definition);
            case GoalType.LootCorpse:
                // §28.15F: ровно то же условие, что считал аукцион — тело, на
                // котором ещё что-то есть. Пустой труп мимо: иначе цель
                // выигрывала бы снова и снова, а поход каждый раз кончался бы
                // ничем (та же петля, что съела колонию на кокосах).
                return CorpseMath.IsHumanDead(definition) &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var lootAnchor) &&
                    CorpseMath.HasLootableSpoils(world, npc, lootAnchor);
            case GoalType.WarmUp:
                // Spec 42: only a BURNING fire warms — a cold pit is no target.
                return definition.HasTag("Campfire") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var pit) &&
                    pit.ResourceAmount > 0f;
            case GoalType.HaulToFire:
                // Spec §52: the stockpile is the hearth — any campfire, lit or not.
                return definition.HasTag("Campfire");
            case GoalType.GetWater:
                // Fetch a whole coconut; Drink will put it on the ground and
                // open it with a blade before sipping. (The collector draw is
                // a custom plan branch, not this generic path.)
                // A coconut contested/unreachable on arrival is shunned by the
                // executor. Honour that memory here as well as in Drink's
                // custom coconut picker, otherwise GetWater immediately picks
                // the exact same failed object again.
                return HasCoconutBlade(npc) &&
                    perceived.DefinitionId == ContentIds.Coconut &&
                    !npc.Memory.IsShunned(perceived.Id, world.Tick);
            case GoalType.StowBottle:
                // §54.15: a finished collector whose vessel slot is empty.
                return perceived.DefinitionId == WaterCollectorMath.CollectorId &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var collector) &&
                    WaterCollectorMath.FindVessel(world, collector) is null;
            default:
                return true;
        }
    }

    // §54.15: MoveTo + Interact(TakeVessel) at the nearest collector holding a
    // bottle this NPC may draw from. Mirrors the generic obstacle-target path:
    // the collector anchor is blocked, so she stands on the reserved rim cell.
    private static bool TryBuildCollectorDrawPlan(WorldState world, NPCState npc)
    {
        var target = DecisionSystem.FindDrawableCollector(npc, world);
        if (target is null ||
            !world.Entities.Objects.TryGetValue(target.Id, out var collector) ||
            collector.Junctions.Count == 0)
        {
            return false;
        }

        var besideReach = SpatialQueries.BesideReach(
            world.Content.ObjectDefinitions.TryGetValue(collector.DefinitionId, out var def)
                ? def.ObstacleRadius : 0f);
        if (!TryReserveBesideJunction(world, npc, collector.Junctions[0], 48,
                out var beside, besideReach, collector))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    $"Goal={npc.Mind.CurrentGoal} collector {collector.Id.Value}: no free junction beside it");
            }
            return false;
        }

        npc.Plan.TargetObjectId = target.Id;
        npc.Plan.TargetTile = target.Tile;
        npc.Plan.TargetJunctionId = beside;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = beside,
            TargetObject = target.Id
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetObject = target.Id,
            TargetJunction = beside,
            Interaction = InteractionType.TakeVessel
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal={npc.Mind.CurrentGoal} Target={collector.DefinitionId} Collector={collector.Id.Value} " +
                "Steps=[MoveToJunction,Interact(TakeVessel)]");
        }
        return true;
    }
}

}
