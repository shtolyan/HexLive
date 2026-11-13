using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §153: подарок — тот же приказ §128, но с направлением Give и живой целью.
/// Проверяется ровно то, что расходится легче всего: КОГО берёт предикат, что
/// вещь доезжает, и что отношения двигает только ОСОЗНАННЫЙ приём.
/// </summary>
public sealed class GiftTransferTests
{
    // ---- §153.1: кого берёт предикат -----------------------------------

    [Test]
    public void AwakePersonIsAGiftTargetButNotALootTarget()
    {
        var (world, giver, other) = Pair();

        Assert.Multiple(() =>
        {
            Assert.That(
                PlayerLootTargets.TryResolve(
                    world, giver, other.Id, InventoryTransferDirection.Take,
                    out _, out _),
                Is.False, "здоровую бодрствующую по-прежнему нельзя обыскать (§111)");
            Assert.That(
                PlayerLootTargets.TryResolve(
                    world, giver, other.Id, InventoryTransferDirection.Give,
                    out var receiver, out _),
                Is.True, "но подарить ей можно");
            Assert.That(receiver.Id, Is.EqualTo(other.Id));
        });
    }

    [Test]
    public void ForeignPersonIsAGiftTarget_GiftIsTheOnePeacefulMove()
    {
        var world = TestWorld.CreateWorld(3311);
        var giver = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var stranger = world.Entities.Npcs.Values.FirstOrDefault(n =>
            n.Faction != Faction.Colony);
        if (stranger is null)
        {
            Assert.Ignore("в этом мире нет чужачки");
        }

        PlaceAdjacent(world, giver, stranger!);

        Assert.That(
            PlayerLootTargets.TryResolve(
                world, giver, stranger!.Id, InventoryTransferDirection.Give,
                out _, out _),
            Is.True);
    }

    [Test]
    public void DyingOrUnconsciousPersonIsNotAStandingGiftRecipient()
    {
        var (world, giver, other) = Pair();
        other.Mind.FaintedUntilTick = world.Tick + 100;

        Assert.Multiple(() =>
        {
            // §153.1: она всё ещё цель — но обычной веткой обыска, со станцией
            // у ног. Именно поэтому вопрос «стоит ли она» отдельный.
            Assert.That(
                PlayerLootTargets.TryResolve(
                    world, giver, other.Id, InventoryTransferDirection.Give,
                    out _, out _),
                Is.True);
            Assert.That(PlayerLootTargets.IsStandingRecipient(world, other), Is.False);
            Assert.That(PlayerLootTargets.CanReactToGift(world, other), Is.False);
        });
    }

    [Test]
    public void SelfIsNeverAGiftTarget()
    {
        var (world, giver, _) = Pair();

        Assert.That(
            PlayerLootTargets.TryResolve(
                world, giver, giver.Id, InventoryTransferDirection.Give,
                out _, out _),
            Is.False);
    }

    // ---- §153.1/.3: вещь доезжает и на неё отвечают --------------------

    [Test]
    public void AwakePersonAcceptsTheGiftAndHerAffinityToTheGiverGrows()
    {
        var (engine, giver, receiver) = Scene();
        var world = engine.World;
        // Жаждущей вода — подарок «в самое сердце», знак дельты не зависит от
        // вкуса и не флейкает от сида.
        receiver.Needs.Thirst = 1f;
        var gift = new ItemInstance("food.coconut") { ResourceAmount = 1f };
        giver.Inventory.Items.Add(gift);
        var before = receiver.Social.GetOrCreate(giver.Id).Affinity;
        var giftTick = Give(engine, giver, receiver, gift.DefinitionId);

        var toGiver = receiver.Social.GetOrCreate(giver.Id);
        Assert.Multiple(() =>
        {
            Assert.That(receiver.Inventory.Items.Any(i => ReferenceEquals(i, gift)), Is.True,
                "вещь обязана доехать физически тем же экземпляром");
            Assert.That(giver.Inventory.Items.Any(i => ReferenceEquals(i, gift)), Is.False);
            Assert.That(toGiver.Affinity, Is.GreaterThan(before));
            Assert.That(toGiver.Familiarity, Is.EqualTo(SocialBalance.TalkRelationshipGain)
                .Within(1e-4f));
            Assert.That(giver.Social.GetOrCreate(receiver.Id).Familiarity,
                Is.EqualTo(SocialBalance.TalkRelationshipGain).Within(1e-4f),
                "знакомство растёт у обеих — они постояли рядом");
            // §153.3: симпатия — только у получательницы. Подарок не сделка.
            Assert.That(giver.Social.GetOrCreate(receiver.Id).Affinity, Is.Zero);
            Assert.That(receiver.Execution.LastTalkResultTick, Is.EqualTo(giftTick),
                "«+/−» над головой — единственный способ показать сдвиг (§28.15E)");
            Assert.That(world.Events.Items.Any(e => e.Type == "GiftGiven"), Is.True);
        });
    }

    [Test]
    public void GiftToASleeperMovesTheItemButBuysNoGratitude()
    {
        var (engine, giver, receiver) = Scene();
        var world = engine.World;
        receiver.Execution.CurrentInteraction = InteractionType.Sleep;
        var gift = new ItemInstance("tool.knife");
        giver.Inventory.Items.Add(gift);

        Give(engine, giver, receiver, gift.DefinitionId);

        Assert.Multiple(() =>
        {
            Assert.That(receiver.Inventory.Items.Any(i => ReferenceEquals(i, gift)), Is.True,
                "§128 разрешал класть вещь спящей и разрешает");
            Assert.That(receiver.Social.GetOrCreate(giver.Id).Affinity, Is.Zero,
                "иначе «подарок» стал бы способом качать симпатию во сне");
            Assert.That(receiver.Social.GetOrCreate(giver.Id).Familiarity, Is.Zero);
            Assert.That(world.Events.Items.Any(e => e.Type == "GiftGiven"), Is.False);
        });
    }

    private static ItemInstance[] FillResourceSlots(NPCState receiver)
    {
        var ids=new[]{ContentIds.Stone,ContentIds.Stick,ContentIds.Rope,ContentIds.PalmLeaf,
            ContentIds.Flint,ContentIds.Fiber,ContentIds.Cloth,ContentIds.Hide,ContentIds.Board};
        Assert.That(receiver.Inventory.Capacity,Is.LessThanOrEqualTo(ids.Length));
        var items=ids.Take(receiver.Inventory.Capacity).Select((id,i)=>new ItemInstance(id)
            {Durability=.231513f+i*.001f,Dirtiness=.41235f,OwnerId=receiver.Id.Value}).ToArray();
        receiver.Inventory.Items.AddRange(items);
        Assert.That(receiver.Inventory.HasSpace,Is.False);
        return items;
    }

    [Test]
    public void AnOfferToAFullPackDoesNotDropTheRecipientsBelongings()
    {
        var(engine,giver,receiver)=Scene();var world=engine.World;
        var before=FillResourceSlots(receiver);
        var gift=new ItemInstance(ContentIds.Hammer){Durability=.371f,Wetness=.219f,Dirtiness=.417f};
        giver.Inventory.Items.Add(gift);
        var belongings = before.Select(item => item.DefinitionId).ToHashSet();
        int DroppedBelongings() => world.Entities.Objects.Values.Count(obj =>
            obj.Owner == receiver.Id && belongings.Contains(obj.DefinitionId));
        var objects = DroppedBelongings();
        Assert.That(InventoryMath.CanMakeRoomFor(world,receiver,gift.DefinitionId),Is.False);
        var result=engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id,receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried,0,gift.DefinitionId),1,InventoryTransferDirection.Give));
        Assert.That(result.Accepted,Is.True,result.Reason);
        engine.Step();
        Assert.That(giver.Inventory.Items.Any(i=>ReferenceEquals(i,gift)),Is.True);
        Assert.That(receiver.Inventory.Items.Zip(before, ReferenceEquals).All(same => same), Is.True);
        Assert.That(receiver.Inventory.Items.Count, Is.EqualTo(before.Length));
        Assert.That(DroppedBelongings(), Is.EqualTo(objects));
    }

    [Test]
    public void ReceiverWithoutExpendableBelongingsKeepsGiftWithTheGiver()
    {
        var(engine,giver,receiver)=Scene();var world=engine.World;
        // A resource gift has no right to evict more important weapons/tools.
        for(int i=0;i<receiver.Inventory.Capacity;i++)receiver.Inventory.Items.Add(new ItemInstance(ContentIds.Hammer));
        var before=receiver.Inventory.Items.ToArray();var gift=new ItemInstance(ContentIds.Stone){Durability=.713f};
        giver.Inventory.Items.Add(gift);
        var result=engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id,receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried,0,gift.DefinitionId),1,InventoryTransferDirection.Give));
        Assert.That(result.Accepted,Is.True,"A full pack must not suppress the offer.");
        engine.Step();
        Assert.That(giver.Inventory.Items.Any(i=>ReferenceEquals(i,gift)),Is.True);
        Assert.That(receiver.Inventory.Items.SequenceEqual(before),Is.True);
        Assert.That(world.Events.Items.Any(e=>e.Type=="GiftGiven"),Is.False);
    }

    [Test]
    public void SleepingFullRecipientDoesNotDiscardBelongingsForAnOffer()
    {
        var(engine,giver,receiver)=Scene();var before=FillResourceSlots(receiver);
        receiver.Execution.CurrentInteraction=InteractionType.Sleep;
        var gift=new ItemInstance(ContentIds.Hammer);giver.Inventory.Items.Add(gift);
        var result=engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id,receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried,0,gift.DefinitionId),1,InventoryTransferDirection.Give));
        Assert.That(result.Accepted,Is.False);
        Assert.That(receiver.Inventory.Items.SequenceEqual(before),Is.True);
        Assert.That(giver.Inventory.Items.Any(i=>ReferenceEquals(i,gift)),Is.True);
    }

    [Test]
    public void AwakeGiftTransfersExactlyOnePhysicalItemEvenWhenCommandRequestsTheStack()
    {
        var(engine,giver,receiver)=Scene();
        var stack=Enumerable.Range(0,3).Select(i=>new ItemInstance(ContentIds.Stick){Durability=.7f+i*.01f}).ToArray();
        giver.Inventory.Items.AddRange(stack);
        var result=engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id,receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried,0,ContentIds.Stick),3,InventoryTransferDirection.Give));
        Assert.That(result.Accepted,Is.True);
        engine.Step();
        Assert.That(giver.Execution.CurrentInteraction, Is.EqualTo(InteractionType.GiveGift));
        Assert.That(receiver.Inventory.Items, Is.Empty);
        Assert.That(giver.Inventory.Items, Is.EqualTo(stack));
        FinishGift(engine, giver);
        Assert.That(receiver.Inventory.Items.Count,Is.EqualTo(1));
        Assert.That(receiver.Inventory.Items[0],Is.SameAs(stack[0]));
        Assert.That(giver.Inventory.Items.Count,Is.EqualTo(2));
        Assert.That(giver.Inventory.Items.Any(i=>ReferenceEquals(i,stack[1])),Is.True);
    }

    [Test]
    public void TakingFromAnAwakePersonIsRejected_GiftIsOneWay()
    {
        var (engine, giver, other) = Scene();
        var hers = new ItemInstance("tool.knife");
        other.Inventory.Items.Add(hers);

        engine.Commands.Enqueue(new TransferInventoryCommand(
            giver.Id, other.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, hers.DefinitionId),
            1, InventoryTransferDirection.Take));
        engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(other.Inventory.Items.Any(i => ReferenceEquals(i, hers)), Is.True);
            Assert.That(giver.Inventory.Items, Is.Empty);
        });
    }

    // ---- §153.2: оценка ------------------------------------------------

    [Test]
    public void ThirstIsWhatTheSameCoconutIsWorth()
    {
        var (_, _, receiver) = Pair();
        receiver.Needs.Thirst = 0f;
        var sated = GiftAppraisal.Evaluate(receiver, "food.coconut", 1, 1f);
        receiver.Needs.Thirst = 1f;
        var thirsty = GiftAppraisal.Evaluate(receiver, "food.coconut", 1, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(thirsty.Score, Is.GreaterThan(sated.Score),
                "нужда — самое тяжёлое слагаемое, и она обязана двигать счёт");
            Assert.That(thirsty.Driver, Is.EqualTo("Need"));
            Assert.That(thirsty.Reaction, Is.EqualTo(GiftReaction.Loved));
            Assert.That(thirsty.AffinityDelta,
                Is.GreaterThanOrEqualTo(sated.AffinityDelta));
        });
    }

    /// <summary>
    /// §52/§153.2: пустая пробитая скорлупа — мусор, а не вода. Ровно та
    /// ловушка, из-за которой рюкзаки заклинивало насмерть; подарок обязан
    /// видеть её так же, иначе «дала воды» превратится в «дала помойку».
    /// </summary>
    [Test]
    public void DrainedShellIsJunk_NotWater()
    {
        var (_, _, receiver) = Pair();
        receiver.Needs.Thirst = 1f;

        var full = GiftAppraisal.Evaluate(receiver, "food.coconut_pierced", 1, 1f);
        var drained = GiftAppraisal.Evaluate(receiver, "food.coconut_pierced", 1, 0f);

        Assert.Multiple(() =>
        {
            Assert.That(drained.Score, Is.LessThan(full.Score));
            Assert.That(full.Reaction, Is.EqualTo(GiftReaction.Loved));
            Assert.That(drained.Reaction,
                Is.Not.EqualTo(GiftReaction.Loved).And.Not.EqualTo(GiftReaction.Liked));
        });
    }

    [Test]
    public void AffinityStepsAreOrderedAndOnlyDislikeIsNegative()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GiftAppraisal.AffinityDelta(GiftReaction.Disliked),
                Is.LessThan(0f), "мусор отнимает — иначе дарить наугад ничего не стоит");
            Assert.That(GiftAppraisal.AffinityDelta(GiftReaction.Neutral), Is.GreaterThan(0f));
            Assert.That(GiftAppraisal.AffinityDelta(GiftReaction.Liked),
                Is.GreaterThan(GiftAppraisal.AffinityDelta(GiftReaction.Neutral)));
            Assert.That(GiftAppraisal.AffinityDelta(GiftReaction.Loved),
                Is.GreaterThan(GiftAppraisal.AffinityDelta(GiftReaction.Liked)));
        });
    }

    [Test]
    public void AppraisalIsDeterministic_TheSameGiftAlwaysCostsTheSame()
    {
        var (_, _, receiver) = Pair();
        receiver.Needs.Hunger = 0.4f;
        var first = GiftAppraisal.Evaluate(receiver, "food.meat_raw", 2, 1f);
        var second = GiftAppraisal.Evaluate(receiver, "food.meat_raw", 2, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(second.Score, Is.EqualTo(first.Score));
            Assert.That(second.Reaction, Is.EqualTo(first.Reaction));
            Assert.That(second.Driver, Is.EqualTo(first.Driver));
        });
    }

    [Test]
    public void UnknownItemDoesNotThrowAndReadsAsNeutralOrWorse()
    {
        var (_, _, receiver) = Pair();

        var verdict = GiftAppraisal.Evaluate(receiver, "nonexistent.thing", 1, 0f);

        Assert.That(verdict.Reaction,
            Is.EqualTo(GiftReaction.Neutral).Or.EqualTo(GiftReaction.Disliked));
    }

    // ---- сцена ----------------------------------------------------------

    /// <summary>
    /// Две девушки в только что созданном мире. Джанкшен новорождённой NPC
    /// проставляет PerceptionSystem на первом тике, а предикат §153.1 требует,
    /// чтобы получательница СТОЯЛА на узле, — поэтому пара ставится руками.
    /// Иначе тест мерит непроинициализированный мир, а не правило.
    /// </summary>
    private static (WorldState world, NPCState giver, NPCState other) Pair(int seed = 3311)
    {
        var world = TestWorld.CreateWorld(seed);
        var people = world.Entities.Npcs.Values.OrderBy(npc => npc.Id.Value).ToArray();
        PlaceAdjacent(world, people[0], people[1]);
        return (world, people[0], people[1]);
    }

    /// <summary>Две соседние девушки, обе в сознании, карманы пусты.</summary>
    private static (SimulationEngine engine, NPCState giver, NPCState receiver) Scene()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var giver = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var receiver = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && !n.Id.Equals(giver.Id));

        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Mind.ManualControl = true;
            npc.Needs.Hunger = 0f;
            npc.Needs.Thirst = 0f;
        }

        giver.Inventory.Items.Clear();
        giver.WornItems.Clear();
        receiver.Inventory.Items.Clear();
        receiver.WornItems.Clear();
        EquipmentMath.RecalculateCapacity(world, giver);
        EquipmentMath.RecalculateCapacity(world, receiver);
        PlaceAdjacent(world, giver, receiver);
        Assert.That(receiver.IsUnconscious(world.Tick), Is.False);
        return (engine, giver, receiver);
    }

    private static long Give(
        SimulationEngine engine, NPCState giver, NPCState receiver, string definitionId)
    {
        var index = giver.Inventory.Items.FindIndex(i => i.DefinitionId == definitionId);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        engine.Commands.Enqueue(new TransferInventoryCommand(
            giver.Id, receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried, index, definitionId),
            1, InventoryTransferDirection.Give));
        engine.Step();
        return FinishGift(engine, giver);
    }

    private static long FinishGift(SimulationEngine engine, NPCState giver)
    {
        var lastTick = engine.World.Tick - 1;
        if (giver.Execution.CurrentInteraction != InteractionType.GiveGift) return lastTick;
        var handoffTick = giver.Execution.EndTick;
        while (engine.World.Tick <= handoffTick)
        {
            lastTick = engine.World.Tick;
            engine.Step();
        }
        return lastTick;
    }

    private static void PlaceAdjacent(WorldState world, NPCState giver, NPCState other)
    {
        var from = world.Junctions.Items.Values.First(j =>
            !j.Blocked && SpatialQueries.IsJunctionFree(world, j.Id) &&
            j.Neighbors.Any(id => SpatialQueries.IsJunctionFree(world, id)));
        var to = world.Junctions.Items[from.Neighbors.First(id =>
            SpatialQueries.IsJunctionFree(world, id))];
        PlaceAt(world, giver, from);
        PlaceAt(world, other, to);
    }

    private static void PlaceAt(WorldState world, NPCState person, Junction destination)
    {
        if (person.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, person.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, person.Id);
        }

        var oldTile = person.Tile;
        person.CurrentJunction = destination.Id;
        person.Position = destination.WorldPosition;
        person.Tile = destination.Tiles[0];
        person.Fragment = destination.Fragment;
        SpatialMutations.MoveEntityToTile(world, person.Id, oldTile, person.Tile);
        SpatialMutations.OccupyJunction(world, destination.Id, person.Id);
    }
}

}
