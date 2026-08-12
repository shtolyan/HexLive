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

public sealed partial class DecisionSystem
{
    // Spec 29F helpers.
    // Spec 35.5: one communal rack is enough for v1.
    internal static bool RackExists(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.DryingRack)
            {
                return true;
            }
        }

        return false;
    }

    internal static int CountInventory(NPCState npc, string definitionId)
    {
        var count = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item == definitionId)
            {
                count++;
            }
        }

        return count;
    }

    // Spec 29F.1: rabbits are perceived by direct proximity scan (<= 4 tiles),
    // no rabbit memory in v1. Spooked rabbits don't count.
    internal static Wildlife.RabbitState? NearestVisibleRabbit(NPCState npc, WorldState world)
    {
        Wildlife.RabbitState? best = null;
        var bestDistance = int.MaxValue;
        foreach (var rabbit in world.Rabbits)
        {
            if (world.Tick < rabbit.SpookedUntilTick)
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(npc.Tile, rabbit.Tile);
            // Radius stays 4: 5-6 made hunts more frequent but the longer
            // chases dragged NPCs into dog country (wipes on two seeds).
            if (distance <= 4 && distance < bestDistance)
            {
                bestDistance = distance;
                best = rabbit;
            }
        }

        return best;
    }

    // §56: Prey unlocks only when EVERY softer food source is absent — carried
    // food, ground food, a known fruit producer, a rabbit to hunt, an animal
    // carcass, or an existing corpse to butcher. The corpse clause guarantees a
    // found body is always eaten before anyone is killed (a killed housemate is
    // strictly worse than one who died on their own).
    internal static bool NoOtherFoodReachable(NPCState npc, WorldState world)
    {
        if (npc.Inventory.FindFirstFood(world.Content) is not null)
        {
            return false;
        }

        if (HasReachableFoodForCurrentTools(npc, world) || HasCoconutMeal(npc, world))
        {
            return false;
        }

        if (KnowsReachableProducer(npc, world))
        {
            return false;
        }

        if (NearestVisibleRabbit(npc, world) is not null)
        {
            return false;
        }

        if (HasReachableWithTag(npc, world, "Carcass"))
        {
            return false;
        }

        if (HasReachableWithTag(npc, world, "Corpse"))
        {
            return false;
        }

        return true;
    }

    // ⭐ ОДНО место, где решается «этот кокос вообще возьмут».
    //
    // Фильтры обязаны совпадать с PlanningSystem.TryFindCoconutObject — и это
    // не стилистика. Когда доступность говорит «есть», а план отвечает «нечего
    // взять», цель выигрывает аукцион, план проваливается, и так каждый проход:
    // NPC спамит Drink/PlanFailed, пока не умрёт от той самой нужды, ради
    // которой цель и бралась. Это уже случалось (Jul 2026, смерть от жажды).
    //
    // Фикс тогда внесли в ОДНУ копию из двух: у воды фильтр появился, у еды —
    // нет, хотя план у них общий. Здесь копия ровно одна, поэтому расходиться
    // больше нечему.
    internal static bool HasUsableCoconut(
        NPCState npc, WorldState world, string definitionId, bool requireWater = false)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                obj.DefinitionId != definitionId ||
                !ObjectUsableBy(obj, npc.Id) ||
                // недавно оказался занят по прибытии — не топтаться вокруг него
                npc.Memory.IsShunned(obj.Id, world.Tick) ||
                // виден в восприятии, но в мире его уже нет
                !world.Entities.Objects.TryGetValue(obj.Id, out var worldObject) ||
                (requireWater && worldObject.ResourceAmount <= 0f))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal static bool HasCoconutMeal(NPCState npc, WorldState world)
    {
        if (npc.Inventory.Items.Contains(ContentIds.CoconutOpen))
        {
            return true;
        }

        var hasBlade = HasCoconutBlade(npc);
        if (hasBlade &&
            (npc.Inventory.Items.Contains(ContentIds.Coconut) ||
             npc.Inventory.Items.Contains(ContentIds.CoconutPierced)))
        {
            return true;
        }

        if (HasUsableCoconut(npc, world, ContentIds.CoconutOpen))
        {
            return true;
        }

        return hasBlade &&
            (HasUsableCoconut(npc, world, ContentIds.Coconut) ||
             HasUsableCoconut(npc, world, ContentIds.CoconutPierced));
    }

    internal static bool HasCoconutWater(NPCState npc, WorldState world)
    {
        var hasBlade = HasCoconutBlade(npc);
        if (hasBlade && npc.Inventory.Items.Contains(ContentIds.Coconut))
        {
            return true;
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.CoconutPierced && item.ResourceAmount > 0f)
            {
                return true;
            }
        }

        if (hasBlade && HasUsableCoconut(npc, world, ContentIds.Coconut))
        {
            return true;
        }

        return HasUsableCoconut(npc, world, ContentIds.CoconutPierced, requireWater: true);
    }

    internal static bool HasBottleWater(NPCState npc) =>
        npc.BottleWater != WaterKind.None && npc.BottleCharges > 0;

    internal static bool HasInventoryCoconutMeal(NPCState npc)
    {
        if (npc.Inventory.Items.Contains(ContentIds.CoconutOpen))
        {
            return true;
        }

        return HasCoconutBlade(npc) &&
            (npc.Inventory.Items.Contains(ContentIds.Coconut) ||
             npc.Inventory.Items.Contains(ContentIds.CoconutPierced));
    }

    internal static bool HasInventoryCoconutWater(NPCState npc)
    {
        if (HasBottleWater(npc))
        {
            return true;
        }

        if (HasCoconutBlade(npc) && npc.Inventory.Items.Contains(ContentIds.Coconut))
        {
            return true;
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.CoconutPierced && item.ResourceAmount > 0f)
            {
                return true;
            }
        }

        return false;
    }

    // §50-prone: piercing a coconut is LIGHT hand-work — a one-legged crawler
    // with a knife still opens her dinner (Marta starved to death at day 28
    // sitting NEXT to coconuts because the blanket prone-gate blocked this).
    // Fighting and heavy tool work stay forbidden while lying.
    internal static bool HasCoconutBlade(NPCState npc) =>
        npc.Body.HasUsableHand &&
        Content.GearCatalog.HasCapability(npc.Inventory.Items, Content.GearCapability.Cut);

    internal static bool HasCoconutOpportunity(NPCState npc, WorldState world)
    {
        if (npc.Inventory.Items.Contains(ContentIds.Coconut) ||
            npc.Inventory.Items.Contains(ContentIds.CoconutPierced) ||
            npc.Inventory.Items.Contains(ContentIds.CoconutOpen))
        {
            return true;
        }

        return HasUsableCoconut(npc, world, ContentIds.Coconut) ||
            HasUsableCoconut(npc, world, ContentIds.CoconutPierced) ||
            HasUsableCoconut(npc, world, ContentIds.CoconutOpen) ||
            KnowsReachableCoconutProducer(npc, world);
    }

    internal static bool HasReachableFoodForCurrentTools(NPCState npc, WorldState world)
    {
        var hasBlade = HasCoconutBlade(npc);
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !ObjectUsableBy(obj, npc.Id) ||
                !obj.AvailableInteractions.Contains(InteractionType.PickUp) ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition))
            {
                continue;
            }

            // §54.14 (r2): cooked meat hanging on the spit counts as reachable
            // food — the fire is the source object, the meat is what's taken.
            if (definition.Tags.Contains("Campfire"))
            {
                if (world.Entities.Objects.TryGetValue(obj.Id, out var fire) &&
                    BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked) > 0 &&
                    InventoryMath.CanMakeRoomFor(world, npc, ContentIds.MeatCooked))
                {
                    return true;
                }

                continue;
            }

            if (!InventoryMath.CanMakeRoomFor(world, npc, obj.DefinitionId) ||
                !definition.Tags.Contains("Food"))
            {
                continue;
            }

            if (definition.Tags.Contains("Coconut") && !hasBlade)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal static bool KnowsReachableCoconutProducer(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                !npc.Memory.IsShunned(obj.Id, world.Tick) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Produce?.ProducedDefinitionId == ContentIds.Coconut)
            {
                return true;
            }
        }

        return false;
    }

    // §56: the victim of a predation — the weakest reachable housemate. Lowest
    // health wins; a sleeper is discounted (an easy kill is preferred) and the
    // nearest breaks remaining ties. A starved predator preys on the frail, so
    // when no soft target is in reach the goal simply has no victim (it fails).
    internal static NPCState? NearestPreyVictim(NPCState npc, WorldState world)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return null;
        }

        NPCState? best = null;
        var bestScore = float.MaxValue;
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id == npc.Id || other.Health <= 0f ||
                // §72: §56 predation stays inside the family — the other
                // faction is the Raid goal's business, not the butcher's.
                !FactionRelations.AreAllies(npc, other) ||
                other.CurrentJunction is not { } otherJunction ||
                // §106: a swimmer is no victim — water IS reachable (SwimCost),
                // so without this filter the predator would wade in after her.
                // The Prey plan then fails the normal way (cooldown included).
                (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, other)))
            {
                continue;
            }

            if (!from.Equals(otherJunction) &&
                !Connectivity.Reachable(world, from, otherJunction, npc.Body.CanJump))
            {
                continue;
            }

            var vulnerability = other.Health -
                (other.Mind.CurrentGoal == GoalType.Sleep ? 0.25f : 0f) +
                HexSpatialMath.HexDistance(npc.Tile, other.Tile) * 0.01f;
            if (vulnerability < bestScore)
            {
                bestScore = vulnerability;
                best = other;
            }
        }

        return best;
    }

    // Spec 29E helpers.
    internal static bool HasReachableWithTag(NPCState npc, WorldState world, string tag)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                !npc.Memory.IsShunned(obj.Id, world.Tick) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains(tag))
            {
                return true;
            }
        }

        return false;
    }

    // §80: то же, но в радиусе от точки. HasReachableWithTag НЕ фильтрует по
    // расстоянию — восприятие подмешивает в список ещё и то, что NPC когда-то
    // видел (`IsReachable` считается по связности, а не по близости), поэтому
    // «есть ли такой предмет» истинно для всего острова разом.
    //
    // Для гейтов вида «сначала подбери с земли, потом добывай ещё» это ровно
    // неверная мера: одна забытая палка на другом конце острова запрещала бы
    // работу навсегда. Здесь спрашивают «есть ли под рукой».
    internal static bool HasNearbyWithTag(
        NPCState npc, WorldState world, string tag, TileCoord origin, int radiusTiles)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                !npc.Memory.IsShunned(obj.Id, world.Tick) &&
                HexSpatialMath.HexDistance(obj.Tile, origin) <= radiusTiles &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains(tag))
            {
                return true;
            }
        }

        return false;
    }

    // §84: the nearest lying INPUT PILE for an in-place craft — a perceived
    // ground piece of the given definition whose cluster (its tile + ring-1,
    // the yucca scatter radius) holds at least `need` free pieces. The craft
    // then happens AT the pile: planning walks her to the returned piece and
    // the craft-start beat claims the cluster straight off the ground — no
    // pocket round-trip (pick up → lay back out) through the inventory.
    internal static PerceivedObject? FindGroundInputPile(
        NPCState npc, WorldState world, string definitionId, int need)
    {
        if (need <= 0)
        {
            return null; // the pack already covers the bill — no pile wanted
        }

        PerceivedObject? best = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.DefinitionId != definitionId || !obj.IsReachable ||
                !ObjectUsableBy(obj, npc.Id) ||
                npc.Memory.IsShunned(obj.Id, world.Tick))
            {
                continue;
            }

            if (best is not null && obj.Distance >= best.Distance)
            {
                continue;
            }

            var cluster = 0;
            foreach (var other in npc.Perception.Objects)
            {
                if (other.DefinitionId == definitionId && other.IsReachable &&
                    ObjectUsableBy(other, npc.Id) &&
                    !npc.Memory.IsShunned(other.Id, world.Tick) &&
                    HexSpatialMath.HexDistance(other.Tile, obj.Tile) <= 1)
                {
                    cluster++;
                }
            }

            if (cluster >= need)
            {
                best = obj;
            }
        }

        return best;
    }

    internal static bool HasReachableDefinition(NPCState npc, WorldState world, string definitionId)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                !npc.Memory.IsShunned(obj.Id, world.Tick) &&
                obj.DefinitionId == definitionId)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasReachableDefinitionWorthCarrying(
        NPCState npc, WorldState world, string definitionId)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                !npc.Memory.IsShunned(obj.Id, world.Tick) &&
                obj.DefinitionId == definitionId &&
                InventoryMath.CanMakeRoomFor(world, npc, obj.DefinitionId))
            {
                return true;
            }
        }

        return false;
    }

    // §47 comfort: like HasReachableWithTag but counts — used for the
    // bed-per-girl deficit. Occupancy is ignored on purpose (a bed someone
    // sleeps in right now still exists as furniture).
    internal static int CountReachableWithTag(NPCState npc, WorldState world, string tag)
    {
        var count = 0;
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains(tag))
            {
                count++;
            }
        }

        return count;
    }

    // Spec 40.15: a KNOWN object with this tag that's reachable overland —
    // used for far, out-of-perception goals (the coastal raft) that NPCs
    // remember from the start (SeedHomeKnowledge) even when they can't see it.
    internal static bool KnowsReachableWithTag(NPCState npc, WorldState world, string tag)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return false;
        }

        foreach (var known in npc.Memory.KnownObjects.Values)
        {
            if (known.Junction is { } j &&
                world.Content.ObjectDefinitions.TryGetValue(known.DefinitionId, out var def) &&
                def.Tags.Contains(tag) &&
                (Connectivity.Reachable(world, from, j) ||
                 Connectivity.ReachableBeside(world, from, j, true,
                     world.Entities.Objects.TryGetValue(known.Id, out var live) ? live : null)))
            {
                return true;
            }
        }

        return false;
    }

    // §gear: any-of capability check over the inventory (typed).
    internal static bool HasAnyCapability(
        NPCState npc, System.Collections.Generic.List<Content.GearCapability> capabilities)
    {
        if (!npc.Body.HasUsableHand)
        {
            return false;
        }

        foreach (var capability in capabilities)
        {
            if (Content.GearCatalog.HasCapability(npc.Inventory.Items, capability))
            {
                return true;
            }
        }

        return false;
    }

    // §gear-data: can the npc perform this object's interaction per its
    // DECLARED capabilities? Undeclared (legacy) content falls back to the
    // caller's own check — decision and execution stay in agreement.
    internal static bool CanPerformDeclared(
        WorldState world, NPCState npc, string definitionId, InteractionType type, bool legacyOk)
    {
        if (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def))
        {
            if (type == InteractionType.Process)
            {
                var isLightCoconutWork = def.Tags.Contains("Coconut");
                if (!npc.Body.HasUsableHand ||
                    (!isLightCoconutWork && !npc.Body.CanUseToolsOrWeapons))
                {
                    return false;
                }
            }

            foreach (var interaction in def.Interactions)
            {
                if (interaction.Type == type)
                {
                    return interaction.RequiredCapabilities.Count == 0
                        ? legacyOk
                        : HasAnyCapability(npc, interaction.RequiredCapabilities);
                }
            }
        }

        return legacyOk;
    }

    internal static bool HasMissingToolReachable(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable || !ObjectUsableBy(obj, npc.Id) ||
                npc.Memory.IsShunned(obj.Id, world.Tick) ||
                !obj.AvailableInteractions.Contains(InteractionType.PickUp))
            {
                continue;
            }

            // §54.15: a bottle parked in the collector's slot is not "a
            // missing tool lying around" — GatherTools must ignore it.
            if (world.Entities.Objects.TryGetValue(obj.Id, out var maybeParked) &&
                WaterCollectorMath.IsParked(world, maybeParked))
            {
                continue;
            }

            if (!npc.Inventory.Items.Contains(obj.DefinitionId) &&
                InventoryMath.CanMakeRoomFor(world, npc, obj.DefinitionId) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains("Tool") &&
                Content.GearCatalog.AddsValueOver(
                    npc.Inventory.Items, obj.DefinitionId, npc.Body.WeaponHands))
            {
                return true;
            }

            // Spec §52: a tool stashed in a dropped garment's pockets counts
            // as reachable too — GatherTools rifles the pockets on arrival.
            if (world.Entities.Objects.TryGetValue(obj.Id, out var container) &&
                container.Contents.Count > 0 &&
                InventoryMath.StashHoldsWantedTool(world, npc, container))
            {
                return true;
            }
        }

        return false;
    }

    internal static (bool Seen, float Fuel, WorldObjectState Fire) FindCampfire(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                npc.Memory.IsShunned(obj.Id, world.Tick) ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire"))
            {
                continue;
            }

            // §54.14 (r2): hand the live object back too — spit/ring stage
            // checks and hanging-meat counts read it directly.
            world.Entities.Objects.TryGetValue(obj.Id, out var worldObject);
            return (true, worldObject?.ResourceAmount ?? 0f, worldObject);
        }

        return (false, 0f, null);
    }

    // Bug #93: cooking needs ONE concrete fire which satisfies the whole
    // contract. Reusing FindCampfire mixed properties from whichever perceived
    // fire happened to be first: an incomplete/cold new hearth could suppress
    // CookMeat even when a lit free spit was also visible (or availability
    // could be borrowed from one fire while the planner selected another).
    internal static WorldObjectState FindCookingFire(NPCState npc, WorldState world)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable ||
                !ObjectUsableBy(perceived, npc.Id) ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !world.Content.ObjectDefinitions.TryGetValue(
                    perceived.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire") ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var fire) ||
                fire.ResourceAmount <= 0f ||
                !FoodMath.SpitHasFreeHook(fire))
            {
                continue;
            }

            return fire;
        }

        return null;
    }

    // Does the NPC carry at least one material this site still needs?
    // §54.12: a perceived object she could actually sit ON (stump/chair/bed).
    private static bool HasPerceivedSeat(NPCState npc)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable && !perceived.IsOccupied &&
                perceived.AvailableInteractions.Contains(InteractionType.Sit))
            {
                return true;
            }
        }

        return false;
    }

    // §54.12: any ledge junction within the sit-plan search radius. Ledges only
    // change with terrain, so the junction list is cached per TopologyVersion —
    // the per-decision cost is a distance sweep over the (short) ledge list.
    private static int _ledgeCacheTopology = -1;

    private static readonly System.Collections.Generic.List<Junction> _ledgeCache = new();

    private static bool AnyLedgeNear(WorldState world, NPCState npc, float radius)
    {
        if (_ledgeCacheTopology != world.TopologyVersion)
        {
            _ledgeCacheTopology = world.TopologyVersion;
            _ledgeCache.Clear();
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (PlanningSystem.IsLedge(world, junction))
                {
                    _ledgeCache.Add(junction);
                }
            }
        }

        foreach (var junction in _ledgeCache)
        {
            if (!junction.Blocked &&
                HexSpatialMath.Distance(junction.WorldPosition, npc.Position) < radius)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReachableBathTile(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return false;
        }

        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !HygieneMath.IsShoreTile(world, junction.Tiles[0]))
            {
                continue;
            }

            if (Connectivity.Reachable(world, from, junction.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static float DirtyGarmentWashNeed(WorldState world, NPCState npc)
    {
        var need = 0f;

        // §88: лежащие на берегу вещи считаются ТОЛЬКО те, что она видит.
        // Раньше перебирался весь мир: любая грязная тряпка на другом конце
        // острова поднимала кому угодно нужду стирать, и человек шёл через всю
        // карту к вещи, о существовании которой знать не мог. Чужак при этом
        // стирал ещё и одежду девушек. Восприятие — та же мера, что у соседних
        // гейтов (см. KnowsReachableWarmthUpgrade).
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable ||
                !ObjectUsableBy(perceived, npc.Id) ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var obj))
            {
                continue;
            }

            var contamination = MathUtil.Clamp01(obj.Dirtiness + obj.Bloodiness);
            if (contamination <= need ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Layer is null ||
                !HygieneMath.IsBathingTile(world, obj.Tile))
            {
                continue;
            }

            need = contamination;
        }

        // §40.6 r2 (laundry-in-hand): worn pieces wash directly — she walks to
        // the water edge, takes the dirtiest piece off into her hand and scrubs
        // it there. No more waiting for a bathe-undress to beach the pile.
        foreach (var item in npc.WornItems)
        {
            need = System.MathF.Max(need, MathUtil.Clamp01(item.Dirtiness + item.Bloodiness));
        }

        return need;
    }

    // §68/§118: how badly she needs a dressing, 0..1. Active blood loss is the
    // emergency half; the full unstabilized cut burden remains after clotting
    // so dry open wounds on intact parts still receive quiet aftercare. A
    // clotted stump scars naturally and is excluded by WoundMath.
    internal static float SelfTreatBurden(NPCState npc)
    {
        if (Spec118.Enabled)
        {
            if (!WoundMath.NeedsAftercare(npc))
            {
                return 0f;
            }

            return MathUtil.Clamp01(System.Math.Max(
                WoundMath.UnstabilizedCutBurden(npc),
                1f - npc.Needs.Blood));
        }

        var worstPart = 1f;
        foreach (var pair in npc.Body.Parts)
        {
            if (!npc.Body.IsSevered(pair.Key) && pair.Value < worstPart)
            {
                worstPart = pair.Value;
            }
        }

        var burden = System.MathF.Max(1f - npc.Health, 1f - worstPart);
        return MathUtil.Clamp01(System.MathF.Max(burden, 1f - npc.Needs.Blood));
    }

    private static bool HasInteraction(NPCState npc, InteractionType interactionType)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                ObjectUsableBy(obj, npc.Id) &&
                obj.AvailableInteractions.Contains(interactionType) &&
                // §84: чужую по полу вещь не видно как одежду вовсе.
                (interactionType != InteractionType.Dress ||
                 Content.GarmentLibrary.FitsSex(npc.Sex, obj.DefinitionId)))
            {
                return true;
            }
        }

        return false;
    }

    // Spec 24.3: occupied objects are unavailable — unless occupied by this
    // NPC itself (an NPC mid-interaction must not lose its own target).
    internal static bool ObjectUsableBy(PerceivedObject obj, EntityId self)
    {
        return !obj.IsOccupied ||
            (obj.OccupiedBy.HasValue && obj.OccupiedBy.Value == self);
    }

    // Spec 31A.5A: warmest worn item that is safe to take off — armor stays
    // on while any danger memory is fresh.
    internal static string? FindRemovableItem(NPCState npc, WorldState world)
    {
        string? best = null;
        var bestWarmth = -1f;
        foreach (var itemId in npc.WornItems)
        {
            var (warmth, armor) = EquipmentMath.ItemValues(world, itemId);
            if (armor > 0f && npc.Memory.Dangers.Count > 0)
            {
                continue; // protection beats comfort under threat
            }

            // §133 (отменяет прежнее «жара НИКОГДА не раздевает догола»):
            // раздеться до конца можно — но только когда рядом некому смотреть.
            // Пока про чужака известно, бельё не снимается ни при какой жаре;
            // без чужака это её дело.
            if (world.Content.ObjectDefinitions.TryGetValue(itemId, out var def) &&
                def.Layer == WearLayer.Underwear &&
                ModestyMath.OutsiderKnown(world, npc))
            {
                continue;
            }

            // §52.8: shedding something that does not warm you cools you by
            // NOTHING — a heat-undress must never pick gear or jewelry (the tool
            // holster, a necklace). Mirrors the §52.7 "dressing must pay off"
            // rule on the way out.
            if (warmth <= 0f)
            {
                continue;
            }

            if (warmth > bestWarmth)
            {
                best = itemId;
                bestWarmth = warmth;
            }
        }

        return best;
    }

    // Spec 29C.4A: does the NPC know a reachable Dress item with armor?
    internal static bool KnowsReachableArmor(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                CandidateArmor(world, obj, npc.Sex) > npc.EquippedArmor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// §133: есть ли в досягаемости вещь, которой можно прикрыть таз или грудь
    /// И которую ей МОЖНО надеть прямо сейчас — своя, ничейная или уже
    /// разрешённая. Чужая вещь подруги сюда не входит намеренно: под чужаком
    /// бегать спрашивать разрешения — не план, а способ зависнуть.
    /// </summary>
    internal static bool KnowsReachablePermittedCover(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable || !ObjectUsableBy(obj, npc.Id) ||
                !Content.GarmentLibrary.FitsSex(npc.Sex, obj.DefinitionId) ||
                ModestyMath.CoverGainFromWearing(world, npc, obj.DefinitionId) <= 0)
            {
                continue;
            }

            if (!world.Entities.Objects.TryGetValue(obj.Id, out var live))
            {
                continue;
            }

            if (ClothingOwnership.FellowOwner(world, npc, live) == null ||
                PlanningSystem.HasWearGrant(npc, obj.Id, world.Tick))
            {
                return true;
            }
        }

        return false;
    }

    internal static float CandidateArmor(
        WorldState world, PerceivedObject obj, Content.GarmentSex wearerSex)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition))
        {
            return 0f;
        }

        var best = 0f;
        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == InteractionType.Dress &&
                Content.GarmentLibrary.FitsSex(wearerSex, obj.DefinitionId) &&
                interaction.Effects.ArmorDelta > best)
            {
                best = interaction.Effects.ArmorDelta;
            }
        }

        return best;
    }

    // §52.7: does the NPC know a reachable garment that would ACTUALLY warm her
    // — a clamp-aware marginal gain of ≥ DressWarmthGainMin over what she wears
    // now? Gates the cold-driven Dress bid (DecisionSystem) so she never even
    // sets out for an identical/worse shirt, or reaches for cloth once already
    // bundled to the warmth cap. Perception-scoped, mirroring KnowsReachableArmor.
    internal static bool KnowsReachableWarmthUpgrade(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                EquipmentMath.WarmthGainFromWearing(world, npc, obj.DefinitionId) >=
                    SimBalance.DressWarmthGainMin)
            {
                return true;
            }
        }

        return false;
    }

    // Spec 27.18A foraging: food can also be sought at a known producer
    // (an apple tree), even when no food item itself is known.
    internal static bool KnowsReachableProducer(NPCState npc, WorldState world)
    {
        var hasBlade = HasCoconutBlade(npc);
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                !npc.Memory.IsShunned(obj.Id, world.Tick) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Produce != null &&
                (definition.Produce.ProducedDefinitionId != ContentIds.Coconut || hasBlade))
            {
                return true;
            }
        }

        return false;
    }

    // §54.15: the nearest finished collector whose parked bottle this NPC may
    // draw from RIGHT NOW (≥1 gulp collected, her own bottle empty). Feeds
    // both GetWater availability and the planner's collector-draw branch.
    internal static PerceivedObject? FindDrawableCollector(NPCState npc, WorldState world)
    {
        PerceivedObject? best = null;
        foreach (var seen in npc.Perception.Objects)
        {
            if (seen.DefinitionId != WaterCollectorMath.CollectorId ||
                !seen.IsReachable || !ObjectUsableBy(seen, npc.Id))
            {
                continue;
            }

            if (!world.Entities.Objects.TryGetValue(seen.Id, out var collector))
            {
                continue;
            }

            var vessel = WaterCollectorMath.FindVessel(world, collector);
            if (vessel is null || !WaterCollectorMath.CanTake(world, npc, vessel))
            {
                continue;
            }

            if (best is null || seen.Distance < best.Distance)
            {
                best = seen;
            }
        }

        return best;
    }

    // §54.15: the nearest finished collector with an EMPTY vessel slot — the
    // StowBottle chore's target (park the bottle, let the rain do the rest).
    internal static PerceivedObject? FindStowableCollector(NPCState npc, WorldState world)
    {
        if (npc.BottleWater != WaterKind.None ||
            CountInventory(npc, WaterCollectorMath.VesselId) == 0)
        {
            return null;
        }

        PerceivedObject? best = null;
        foreach (var seen in npc.Perception.Objects)
        {
            if (seen.DefinitionId != WaterCollectorMath.CollectorId ||
                !seen.IsReachable || !ObjectUsableBy(seen, npc.Id))
            {
                continue;
            }

            if (!world.Entities.Objects.TryGetValue(seen.Id, out var collector) ||
                WaterCollectorMath.FindVessel(world, collector) is not null)
            {
                continue;
            }

            if (best is null || seen.Distance < best.Distance)
            {
                best = seen;
            }
        }

        return best;
    }
}

}
