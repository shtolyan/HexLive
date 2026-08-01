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
            if (npc.Plan.Status == PlanStatus.Active && npc.Plan.Goal == npc.Mind.CurrentGoal)
            {
                Trace.Emit(world, npc.Id, "PlanSkipped",
                    $"ActivePlan already matches Goal={npc.Mind.CurrentGoal} Step={npc.Plan.CurrentStepIndex}/{npc.Plan.Steps.Count}");
                continue;
            }

            var prevStatus = npc.Plan.Status;
            var prevGoal = npc.Plan.Goal;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetItemDefinitionId = null;
            npc.Plan.TargetAgentId = null;
            npc.Plan.Goal = npc.Mind.CurrentGoal;

            Trace.Emit(world, npc.Id, "PlanStarted",
                $"Goal={npc.Mind.CurrentGoal} PrevGoal={prevGoal} PrevStatus={prevStatus}");

            if (npc.Mind.CurrentGoal == GoalType.Eat)
            {
                // Eating happens in place from inventory (spec 29B.3):
                // no target object, no junction reservation.
                var foodDefinitionId = npc.Inventory.FindFirstFood(world.Content);
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
                    Trace.Emit(world, npc.Id, "PlanBuilt",
                        $"Goal=Eat Item={foodDefinitionId} Steps=[ConsumeInventoryItem]");
                    continue;
                }

                if (BuildCoconutEatPlan(world, npc))
                {
                    continue;
                }

                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "PlanFailed",
                    "Goal=Eat but no food in inventory");
                continue;
            }

            // §gear-craft: the recipe declares NO station — craft right where
            // she stands: a one-step in-place plan, no walk, no target object.
            if (Content.RecipeCatalog.IsItemOutputGoal(npc.Mind.CurrentGoal) &&
                string.IsNullOrEmpty(Content.RecipeCatalog.StationOf(npc.Mind.CurrentGoal)))
            {
                npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.CraftInPlace });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PlanBuilt",
                    $"Goal={npc.Mind.CurrentGoal} Steps=[CraftInPlace] (no station)");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Drink)
            {
                if (DecisionSystem.HasBottleWater(npc))
                {
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.DrinkBottle
                    });
                    npc.Plan.CurrentStepIndex = 0;
                    npc.Plan.Status = PlanStatus.Active;
                    Trace.Emit(world, npc.Id, "PlanBuilt",
                        $"Goal=Drink Item=tool.bottle Steps=[DrinkBottle] Charges={npc.BottleCharges}");
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
                    Trace.Emit(world, npc.Id, "PlanBuilt",
                        $"Goal=Drink Item={drinkDefinitionId} Steps=[ConsumeInventoryItem]");
                    continue;
                }

                // §54.15 r2: nothing drinkable in hand — the rain already
                // collected under the funnel is the nearest water there is, so
                // it beats walking to a coconut (and a full bottle that nobody
                // ever drew from was the whole station going to waste).
                if (TryBuildCollectorDrawPlan(world, npc))
                {
                    continue;
                }

                if (BuildCoconutDrinkPlan(world, npc))
                {
                    continue;
                }

                // Jul 2026: a BLADELESS dehydrated girl far from the colony
                // cannot make water appear where she stands — but the camp can
                // (housemates craft knives and pierce coconuts day 0, and §53
                // Hydrate-aid needs her in sight). Walking home IS her drink
                // plan; the recurring seed-42 day-0.5 death was Marta chasing
                // distant tools at the island's rim while thirst hit 1.0.
                if (!DecisionSystem.HasCoconutBlade(npc) &&
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
                Trace.Emit(world, npc.Id, "PlanFailed",
                    "Goal=Drink but nothing drinkable in inventory");
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

            if (npc.Mind.CurrentGoal == GoalType.Aid)
            {
                BuildAidPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Defend)
            {
                BuildDefendPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Explore)
            {
                BuildExplorePlan(world, npc);
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

            if (npc.Mind.CurrentGoal == GoalType.Hunt)
            {
                // Spec 29F.2: a move-only chase to the rabbit's junction;
                // the rabbit flees, re-planning produces a genuine pursuit.
                var rabbit = DecisionSystem.NearestVisibleRabbit(npc, world);
                if (rabbit is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Hunt);
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Hunt NoVisibleRabbit");
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
                Trace.Emit(world, npc.Id, "HuntPlanned",
                    $"Rabbit={rabbit.Id} Tile={rabbit.Tile.Q},{rabbit.Tile.R}");
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
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Prey NoReachableVictim");
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
                Trace.Emit(world, npc.Id, "PreyPlanned",
                    $"Victim={victim.Id.Value} Tile={victim.Tile.Q},{victim.Tile.R}");
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
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Raid NoReachableVictim");
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
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Raid NoApproach");
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
                Trace.Emit(world, npc.Id, "RaidPlanned",
                    $"Victim=NPC{raidVictim.Id.Value} Tile={raidVictim.Tile.Q},{raidVictim.Tile.R} " +
                    $"Opp={RaidMath.Opportunity(world, npc, raidVictim):F2}");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Undress)
            {
                var removable = DecisionSystem.FindRemovableItem(npc, world);
                if (removable is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Undress);
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Undress NothingRemovable");
                    continue;
                }

                npc.Plan.TargetItemDefinitionId = removable;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.UndressItem,
                    Interaction = InteractionType.Undress
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PlanBuilt",
                    $"Goal=Undress Item={removable} Steps=[UndressItem]");
                continue;
            }

            // §68: first aid on yourself is in-place work — no walk, no target
            // object, no station. She stops where she stands and winds the
            // dressing (the bandage is already in her pack; the fetch/craft of
            // one is the separate GatherHerb→CraftBandage chain).
            if (npc.Mind.CurrentGoal == GoalType.TreatWounds)
            {
                if (npc.Needs.Bandages <= 0)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.TreatWounds);
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=TreatWounds NoBandage");
                    continue;
                }

                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.TreatSelf,
                    Interaction = InteractionType.TreatSelf
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PlanBuilt",
                    $"Goal=TreatWounds Bandages={npc.Needs.Bandages} Steps=[TreatSelf]");
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

            var interactionType = GoalToInteraction(npc.Mind.CurrentGoal);
            if (interactionType is null)
            {
                npc.Plan.Status = PlanStatus.Completed;
                Trace.Emit(world, npc.Id, "PlanNoInteraction",
                    $"Goal={npc.Mind.CurrentGoal} has no mapped interaction (Idle?)");
                continue;
            }

            // Spec 29C.4A: a threatened underarmored NPC dressing up prefers
            // the best armor over the nearest garment.
            var preferArmor = interactionType == InteractionType.Dress &&
                npc.Memory.Dangers.Count > 0 && npc.EquippedArmor < 0.3f;
            // §55: boiling is retired — GetWater now just fetches the nearest
            // coconut to crack open (no boiled-vs-raw source preference).
            var preferBoiled = false;
            // §64: personal beds (SOFT). When choosing where to sleep, a colonist
            // heads for HER OWN bed first — but any free bed stays a valid
            // fallback (no hard filter), so a colonist whose bed isn't built yet
            // never ground-sleeps beside an empty bed. Survival is unchanged.
            var preferOwnBed = SpecDream.Enabled && interactionType == InteractionType.Sleep;

            PerceivedObject? selected = null;
            var selectedArmor = 0f;
            var selectedBoiled = false;
            var selectedMine = false;
            var candidateCount = 0;
            foreach (var perceived in npc.Perception.Objects)
            {
                if (!perceived.IsReachable ||
                    !DecisionSystem.ObjectUsableBy(perceived, npc.Id) ||
                    !perceived.AvailableInteractions.Contains(interactionType.Value))
                {
                    continue;
                }

                // Spec 29E.4: per-goal target filtering by tags.
                if (!IsValidTargetFor(world, npc, npc.Mind.CurrentGoal, perceived))
                {
                    continue;
                }

                if (interactionType == InteractionType.PickUp &&
                    !InventoryMath.CanMakeRoomFor(world, npc, perceived.DefinitionId))
                {
                    Trace.Emit(world, npc.Id, "PlanCandidateSkipped",
                        $"Obj={perceived.Id.Value} Def={perceived.DefinitionId} NoRoomForImportance");
                    continue;
                }

                // Spec 29C.4A food avoidance: don't shop for food where the
                // dogs are — unless starving (desperation overrides caution).
                // §54.16: butchered meat is EXEMPT while no beast is actually
                // there. The kill site is stamped dangerous for 2400 ticks and
                // the meat rots in 1800 — the ban outlived the meal, so every
                // wolf the colony killed rotted where it fell (14 kills / 13
                // butcherings / 0 chunks cooked in the day-34 save). A stale
                // mark must not fence off the catch they just fought for; a
                // LIVE mob on the spot still does.
                if (interactionType == InteractionType.PickUp && !npc.Mind.IsStarving &&
                    IsNearDanger(npc, perceived.Tile, 2) &&
                    !(IsMeatSource(world, perceived) &&
                      !MobSystem.MobNear(world, perceived.Tile, 2)))
                {
                    continue;
                }

                // §52.7: a cold-driven Dress only targets a REAL warmth upgrade —
                // never walk to an identical/worse shirt (clamp-aware gain over
                // what she wears now). Armor-driven dressing (preferArmor) keeps
                // its own CandidateArmor gain rule below, untouched.
                if (interactionType == InteractionType.Dress && !preferArmor &&
                    EquipmentMath.WarmthGainFromWearing(world, npc, perceived.DefinitionId) <
                        SimBalance.DressWarmthGainMin)
                {
                    Trace.Emit(world, npc.Id, "PlanCandidateSkipped",
                        $"Obj={perceived.Id.Value} Def={perceived.DefinitionId} NoWarmthGain");
                    continue;
                }

                candidateCount++;
                Trace.Emit(world, npc.Id, "PlanCandidate",
                    $"Obj={perceived.Id.Value} Tile={perceived.Tile.Q},{perceived.Tile.R} " +
                    $"Dist={perceived.Distance:F2} Occupied={perceived.IsOccupied}");

                if (preferArmor)
                {
                    var armor = DecisionSystem.CandidateArmor(world, perceived);
                    if (selected is null || armor > selectedArmor + 0.01f ||
                        (System.Math.Abs(armor - selectedArmor) <= 0.01f &&
                         perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedArmor = armor;
                    }
                }
                else if (preferBoiled)
                {
                    var isBoiled = world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var d) &&
                        d.Tags.Contains("Campfire");
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

                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                Trace.Emit(world, npc.Id, "PlanFailed",
                    $"Goal={npc.Mind.CurrentGoal} Interaction={interactionType} " +
                    $"Candidates={candidateCount} NoSuitableObject");
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
                Trace.Emit(world, npc.Id, "PlanFailed",
                    $"Goal={npc.Mind.CurrentGoal} Obj={selected.Id.Value} vanished and not remembered");
                continue;
            }

            Trace.Emit(world, npc.Id, "PlanTargetSelected",
                $"Obj={selected.Id.Value} Def={selected.DefinitionId} " +
                $"Tile={selected.Tile.Q},{selected.Tile.R} Dist={selected.Distance:F2} " +
                $"FromMemory={selected.FromMemory} FromCandidates={candidateCount}");

            // Spec 31C.1: obstacle anchors are blocked — stand beside the
            // trunk, not inside it. Gathering a ground item (PickUp) also stands
            // BESIDE now: she walks up to the nearest cell next to the item and
            // collects from there instead of stepping onto it.
            var gatherBeside = interactionType == InteractionType.PickUp;
            var anchorIsWater = targetJunction is { } wetId &&
                SpatialQueries.IsAllWaterJunction(world, wetId);
            if (targetJunction is { } anchorId &&
                (anchorIsWater || gatherBeside ||
                 (world.Junctions.Items.TryGetValue(anchorId, out var anchorJunction) &&
                  anchorJunction.Blocked)))
            {
                // Reserve in-loop: the first free neighbor is the same for
                // every claimant — without reserving here two sleepers fight
                // over one spot forever (ReservationFailed loop). Nearest-to-NPC
                // first, so "beside" is the CLOSEST reachable cell to the item.
                // Capped to BesideReach: a boxed-in item yields no rim (fail +
                // retarget) rather than a stand a whole hex out (user: interact
                // at the smallest hop, never across a wall/cliff).
                JunctionId? beside = null;
                var besideReach = SpatialQueries.BesideReach(
                    world.Content.ObjectDefinitions.TryGetValue(selected.DefinitionId, out var besideDef)
                        ? besideDef.ObstacleRadius : 0f);
                SpatialQueries.CollectStandableAround(world, anchorId, _rimScratch, 96, besideReach);
                _rimScratch.Sort((a, b) =>
                {
                    var da = world.Junctions.Items.TryGetValue(a, out var ja)
                        ? HexSpatialMath.Distance(ja.WorldPosition, npc.Position) : float.MaxValue;
                    var db = world.Junctions.Items.TryGetValue(b, out var jb)
                        ? HexSpatialMath.Distance(jb.WorldPosition, npc.Position) : float.MaxValue;
                    return da.CompareTo(db);
                });
                foreach (var rim in _rimScratch)
                {
                    if (SpatialQueries.IsJunctionFree(world, rim) &&
                        SpatialMutations.TryReserveJunction(world, rim, npc.Id, world.Tick, 48))
                    {
                        beside = rim;
                        break;
                    }
                }

                if (beside is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                    Trace.Emit(world, npc.Id, "PlanFailed",
                        $"Goal={npc.Mind.CurrentGoal} Obj={selected.Id.Value} no free junction beside obstacle");
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
                Trace.Emit(world, npc.Id, "ReservationFailed",
                    $"Junction={jId.Value} Already reserved or occupied");
                continue;
            }

            if (targetJunction is { } reservedJId)
            {
                Trace.Emit(world, npc.Id, "JunctionReserved",
                    $"Junction={reservedJId.Value} Duration=48ticks Until={world.Tick + 48}");
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
            Trace.Emit(world, npc.Id, "PlanBuilt",
                $"Goal={npc.Plan.Goal} Target={selected.DefinitionId} " +
                $"Tile={selected.Tile.Q},{selected.Tile.R} Junction={Trace.FormatJunction(targetJunction)} " +
                $"FromMemory={selected.FromMemory} Steps=[MoveToJunction,Interact]");
        }
    }

    private static bool TryReserveBesideJunction(
        WorldState world,
        NPCState npc,
        JunctionId anchorId,
        int durationTicks,
        out JunctionId beside,
        float maxBesideDist = float.MaxValue)
    {
        SpatialQueries.CollectStandableAround(world, anchorId, _rimScratch, 96, maxBesideDist);
        if (npc.CurrentJunction is { } current && _rimScratch.Contains(current))
        {
            beside = current;
            return true;
        }

        _rimScratch.Sort((a, b) =>
        {
            var da = world.Junctions.Items.TryGetValue(a, out var ja)
                ? HexSpatialMath.Distance(ja.WorldPosition, npc.Position) : float.MaxValue;
            var db = world.Junctions.Items.TryGetValue(b, out var jb)
                ? HexSpatialMath.Distance(jb.WorldPosition, npc.Position) : float.MaxValue;
            return da.CompareTo(db);
        });

        foreach (var rim in _rimScratch)
        {
            if (SpatialQueries.IsJunctionFree(world, rim) &&
                SpatialMutations.TryReserveJunction(world, rim, npc.Id, world.Tick, durationTicks))
            {
                beside = rim;
                return true;
            }
        }

        beside = default;
        return false;
    }

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
                    if (a.Elevation <= b.Elevation ||
                        !a.Flags.HasFlag(TileFlags.Walkable) ||
                        a.Elevation - b.Elevation < 1)
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

        if (canonical != junction.Id)
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
        if (perceived.DefinitionId is "food.meat_raw" or "food.meat_cooked")
        {
            return true;
        }

        return world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition) &&
            definition.Tags.Contains("Campfire") &&
            world.Entities.Objects.TryGetValue(perceived.Id, out var fire) &&
            BuildSiteMath.HangingMeat(fire, "food.meat_cooked") > 0;
    }

    // Spec 23.10: a failed plan puts its goal on cooldown so the NPC does
    // something else instead of hammering the same target.
    private const int FailureCooldownTicks = 40;

    internal static void SetGoalCooldown(WorldState world, NPCState npc, GoalType goal)
    {
        if (goal == GoalType.Idle || goal == GoalType.None)
        {
            return;
        }

        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = goal,
            EndTick = world.Tick + FailureCooldownTicks
        });

        // A failed goal must not be defended by its own lock — otherwise the
        // hold rule keeps the zero-scored goal and planning hammers the same
        // target until the lock expires.
        if (npc.Mind.GoalLock is { } goalLock && goalLock.Goal == goal)
        {
            npc.Mind.GoalLock = null;
        }

        Trace.Emit(world, npc.Id, "GoalCooldownSet",
            $"{goal} on cooldown until tick {world.Tick + FailureCooldownTicks}");
    }

    private static InteractionType? GoalToInteraction(GoalType goal)
    {
        return goal switch
        {
            GoalType.GetFood => InteractionType.PickUp,
            GoalType.GatherWood => InteractionType.PickUp,
            GoalType.GatherTools => InteractionType.PickUp,
            GoalType.GetWater => InteractionType.PickUp, // §55: fetch a coconut to crack open
            GoalType.StowBottle => InteractionType.PlaceVessel, // §54.15: park the bottle in the collector
            GoalType.TendFire => InteractionType.Fuel,
            GoalType.CraftSpear => InteractionType.Craft,
            GoalType.CookMeat => InteractionType.Craft,
            GoalType.CraftLeather => InteractionType.Craft,
            GoalType.CraftAxe => InteractionType.Craft,
            GoalType.CraftPickaxe => InteractionType.Craft,
            GoalType.CraftRack => InteractionType.Craft,
            GoalType.CraftBed => InteractionType.Craft,
            GoalType.CraftTent => InteractionType.Craft,
            GoalType.BuildRaft => InteractionType.BuildRaft,
            GoalType.CraftBow => InteractionType.Craft,
            GoalType.CraftArrows => InteractionType.Craft,
            GoalType.DryClothes => InteractionType.Hang,
            GoalType.GatherStone => InteractionType.PickUp,
            GoalType.HarvestTree => InteractionType.Harvest,
            GoalType.MineBoulder => InteractionType.Harvest,
            GoalType.SplitLog => InteractionType.Process,
            GoalType.ChopCrown => InteractionType.Process,
            GoalType.GatherLeaves => InteractionType.PickUp,
            GoalType.HarvestYucca => InteractionType.Harvest,
            GoalType.Butcher => InteractionType.Butcher,
            GoalType.Build => InteractionType.Build,
            GoalType.BuildFurniture => InteractionType.Build,
            GoalType.Mourn => InteractionType.Observe,
            GoalType.WarmUp => InteractionType.Observe,
            GoalType.HaulToFire => InteractionType.Observe,
            GoalType.GatherHerb => InteractionType.PickUp,
            GoalType.CraftBandage => InteractionType.Craft,
            GoalType.GatherFiber => InteractionType.PickUp,
            GoalType.CraftRope => InteractionType.Craft,
            GoalType.CraftCloth => InteractionType.Craft,
            GoalType.CraftKnife => InteractionType.Craft,
            GoalType.Bury => InteractionType.Bury,
            GoalType.Sleep => InteractionType.Sleep,
            GoalType.Sit => InteractionType.Sit,
            GoalType.Dress => InteractionType.Dress,
            _ => null
        };
    }

    private static bool IsValidTargetFor(WorldState world, NPCState npc, GoalType goal, PerceivedObject perceived)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition))
        {
            return false;
        }

        switch (goal)
        {
            case GoalType.GetFood:
                // §54.14 (r2): cooked meat hanging on the spit is takeable food —
                // the campfire itself becomes a GetFood target while any hangs.
                if (definition.Tags.Contains("Campfire"))
                {
                    return world.Entities.Objects.TryGetValue(perceived.Id, out var spitSource) &&
                        BuildSiteMath.HangingMeat(spitSource, "food.meat_cooked") > 0;
                }

                return definition.Tags.Contains("Food") &&
                    (!definition.Tags.Contains("Coconut") || HasCoconutBlade(npc));
            case GoalType.GatherWood:
                // Spec §54: wood on the ground. A stick is always useful (fuel,
                // slats, crafts). §54.12: a whole LOG only when logs are billed
                // somewhere (a site's log stage, the raft) or she can split it
                // into sticks — the bed's stick stage otherwise sent her home
                // hugging a useless log.
                if (!definition.Tags.Contains("Wood"))
                {
                    return false;
                }

                if (!definition.Tags.Contains("Log"))
                {
                    return true;
                }

                return Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood) ||
                    WholeLogsWanted(world, npc);
            case GoalType.SplitLog:
                // Spec §54: split a log lying on the ground into sticks.
                return definition.Tags.Contains("Log");
            case GoalType.ChopCrown:
                // Spec §54.2: chop a felled palm crown into loose leaves.
                return definition.Tags.Contains("PalmCrown");
            case GoalType.GatherLeaves:
                // Spec §54.2: pick a scattered palm leaf off the ground.
                return definition.Tags.Contains("PalmLeaf");
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
                if (definition.Tags.Contains("Tool") &&
                    !npc.Inventory.Items.Contains(perceived.DefinitionId) &&
                    Content.GearCatalog.AddsValueOver(
                        npc.Inventory.Items, perceived.DefinitionId, npc.Body.IntactHands))
                {
                    return true;
                }

                // Spec §52: a dropped garment whose pockets hold a missing
                // tool is a valid target — she rifles the pockets on arrival.
                return world.Entities.Objects.TryGetValue(perceived.Id, out var stashContainer) &&
                    stashContainer.Contents.Count > 0 &&
                    InventoryMath.StashHoldsWantedTool(world, npc, stashContainer);
            case GoalType.GatherHerb:
                return definition.Tags.Contains("Herb");
            case GoalType.HarvestYucca:
                return definition.Tags.Contains("Yucca");
            case GoalType.GatherFiber:
                return definition.Tags.Contains("Fiber");
            case GoalType.CookMeat:
                // §54.14 (r2): hanging meat needs a finished spit (stage 3)
                // with a free hook — any other lit fire won't do.
                return definition.Tags.Contains("Campfire") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var spitFire) &&
                    BuildSiteMath.CampfireSpitComplete(spitFire) &&
                    BuildSiteMath.HangingMeat(spitFire, "food.meat_raw") +
                    BuildSiteMath.HangingMeat(spitFire, "food.meat_cooked") <
                    SimBalance.CampfireSpitCapacity;
            case GoalType.CraftRope:
            case GoalType.CraftCloth:
            case GoalType.CraftKnife:
            case GoalType.CraftBandage:
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
                return definition.Tags.Contains("Campfire");
            case GoalType.DryClothes:
                // §35.5B: only a rack with a free hanger slot (capacity 8).
                return definition.Tags.Contains("Rack") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var rack) &&
                    !ExecutionSystem.RackIsFull(world, rack);
            case GoalType.GatherStone:
                return definition.Tags.Contains("Stone");
            case GoalType.HarvestTree:
                return definition.Tags.Contains("Palm");
            case GoalType.MineBoulder:
                return definition.Tags.Contains("Boulder");
            case GoalType.Butcher:
                // Spec §54: an animal carcass any time; a housemate's body only
                // as a starvation last resort (cannibalism gate).
                if (definition.Tags.Contains("Carcass"))
                {
                    return true;
                }

                return definition.Tags.Contains("Corpse") &&
                    SimBalance.CannibalismEnabled &&
                    npc.Needs.Hunger >= SimBalance.CannibalizeHungerGate;
            case GoalType.Build:
                // Spec §52: the hut anchor only — a furniture site is a separate goal.
                return definition.Tags.Contains("BuildSite") &&
                    !definition.Tags.Contains("FurnitureSite");
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
                return definition.Tags.Contains("FurnitureSite") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var fsite) &&
                    BuildSiteMath.IsSite(fsite) &&
                    (DecisionSystem.CarriesSiteMaterial(npc, fsite) ||
                     BuildSiteMath.IsStocked(fsite));
            case GoalType.BuildRaft:
                return definition.Tags.Contains("Raft");
            case GoalType.Mourn:
                return definition.Tags.Contains("Corpse") || definition.Tags.Contains("Grave");
            case GoalType.WarmUp:
                // Spec 42: only a BURNING fire warms — a cold pit is no target.
                return definition.Tags.Contains("Campfire") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var pit) &&
                    pit.ResourceAmount > 0f;
            case GoalType.HaulToFire:
                // Spec §52: the stockpile is the hearth — any campfire, lit or not.
                return definition.Tags.Contains("Campfire");
            case GoalType.Bury:
                return definition.Tags.Contains("Corpse");
            case GoalType.GetWater:
                // Fetch a whole coconut; Drink will put it on the ground and
                // open it with a blade before sipping. (The collector draw is
                // a custom plan branch, not this generic path.)
                return HasCoconutBlade(npc) && perceived.DefinitionId == "food.coconut";
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
                out var beside, besideReach))
        {
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal={npc.Mind.CurrentGoal} collector {collector.Id.Value}: no free junction beside it");
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
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal={npc.Mind.CurrentGoal} Target={collector.DefinitionId} Collector={collector.Id.Value} " +
            "Steps=[MoveToJunction,Interact(TakeVessel)]");
        return true;
    }
}

}
