using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class GiftSessionTests
{
    private static (SimulationEngine engine, NPCState giver, NPCState receiver) Scene()
    {
        var engine = TestWorld.CreateEngine(); var world = engine.World;
        var pair = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        world.Mobs.Clear();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "gift test setup");
            npc.Mind.ManualControl = true; npc.Mind.OutfitLocked = false; npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            npc.Mind.WakeGraceUntilTick = 0; npc.Memory.Dangers.Clear(); npc.IsFighting = false;
        }
        var giver = pair[0]; var receiver = pair[1]; receiver.Mind.ManualControl = false;
        var from = world.Junctions.Items.Values.First(j => !j.Blocked && SpatialQueries.IsJunctionFree(world, j.Id));
        var to = world.Junctions.Items.Values.First(j => !j.Blocked && SpatialQueries.IsJunctionFree(world, j.Id) &&
            HexSpatialMath.Distance(j.WorldPosition, from.WorldPosition) > InteractionReach.Aid * 2f &&
            HexSpatialMath.Distance(j.WorldPosition, from.WorldPosition) < InteractionReach.Aid * 3f &&
            Connectivity.Reachable(world, from.Id, j.Id, giver.Body.CanJump));
        Place(world, giver, from); Place(world, receiver, to);
        return (engine, giver, receiver);
    }

    [Test]
    public void ExplicitGiftInvitationStopsMovingRecipientAndStartsTheApproach()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        receiver.Movement.IsMoving = true;
        receiver.Movement.JunctionPath.Add(receiver.CurrentJunction.Value);
        receiver.Plan.Status = PlanStatus.Active; receiver.Plan.Goal = GoalType.GatherTools;
        var result = engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id));
        Assert.That(result.Accepted, Is.True, result.Reason);
        Assert.That(receiver.Movement.IsMoving, Is.False); Assert.That(receiver.Movement.JunctionPath, Is.Empty);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.EqualTo(giver.Id));
        Assert.That(giver.Mind.GiftRecipientId, Is.EqualTo(receiver.Id));
        Assert.That(giver.Plan.Steps.Single().Type, Is.EqualTo(PlanStepType.MoveToJunction));
        Assert.That(giver.Plan.TargetAgentId, Is.EqualTo(receiver.Id));
        Assert.That(giver.Execution.TargetInventoryItem, Is.Null, "Approach begins without an item cursor.");
        var targetPosition = receiver.Position;
        for (var i = 0; i < 5; i++) engine.Step();
        Assert.That(receiver.Position, Is.EqualTo(targetPosition));
    }

    [Test]
    public void SelectingItemDuringApproachCompletesTheSamePhysicalGiftAndClosingReleasesBoth()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        giver.Inventory.Items.Clear(); receiver.Inventory.Items.Clear();
        var gift = new ItemInstance(ContentIds.Bottle) { ResourceAmount = 4.05f, WaterKind = WaterKind.Boiled, Durability = .74f, OwnerId = giver.Id.Value };
        giver.Inventory.Items.Add(gift);
        Assert.That(engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id)).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id, receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, gift.DefinitionId), 1, InventoryTransferDirection.Give)).Accepted, Is.True);
        engine.ApplyManualCommand(new EndGiftSessionCommand(giver.Id, receiver.Id));
        Assert.That(receiver.Mind.PendingGiftFrom, Is.EqualTo(giver.Id), "Queued transfer keeps its recipient until commit.");
        for (var i = 0; i < 100 && !receiver.Inventory.Items.Any(x => ReferenceEquals(x, gift)); i++) engine.Step();
        Assert.That(receiver.Inventory.Items.Any(x => ReferenceEquals(x, gift)), Is.True);
        Assert.That(gift.ResourceAmount, Is.EqualTo(4.05f)); Assert.That(gift.OwnerId, Is.EqualTo(receiver.Id.Value));
        Assert.That(receiver.Mind.PendingGiftFrom, Is.Null); Assert.That(giver.Mind.GiftRecipientId, Is.Null);
        Assert.That(world.Events.Items.Any(e => e.Type == "ItemStolen"), Is.False);
    }

    [TestCase("timeout")]
    [TestCase("danger")]
    [TestCase("control")]
    [TestCase("death")]
    public void InvitationAlwaysReleasesOnSafetyBoundary(string boundary)
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        Assert.That(engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id)).Accepted, Is.True);
        // #742: the wait deadline only runs while the giver stands and waits
        // for the player to pick an item — not while she is still walking over.
        if (boundary == "timeout")
        {
            PlanInterruption.TryAbort(world, giver, InterruptionCause.PlayerCommand, "arrived, waiting for a choice");
            world.Tick = giver.Mind.GiftUntilTick + 1;
        }
        if (boundary == "danger") receiver.IsFighting = true;
        if (boundary == "control") receiver.Mind.ManualControl = true;
        if (boundary == "death") giver.Health = 0f;
        GiftSessionMath.Update(world);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.Null); Assert.That(giver.Mind.GiftRecipientId, Is.Null);
    }

    [Test]
    public void RetriedInvitationDoesNotRestartChosenTransferOrExtendItsWaitLease()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        giver.Inventory.Items.Clear(); var gift = new ItemInstance(ContentIds.Stick); giver.Inventory.Items.Add(gift);
        engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id));
        var deadline = giver.Mind.GiftUntilTick;
        engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id, receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, gift.DefinitionId), 1, InventoryTransferDirection.Give));
        world.Tick++;
        Assert.That(engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id)).Accepted, Is.True);
        Assert.That(giver.Mind.GiftUntilTick, Is.EqualTo(deadline));
        Assert.That(giver.Plan.Goal, Is.EqualTo(GoalType.PlayerInventory));
        Assert.That(giver.Execution.TargetInventoryItem, Is.SameAs(gift));
    }

    [Test]
    public void WrongAssignedActorCannotStartOrEndSomeoneElsesInvitation()
    {
        var (engine, giver, receiver) = Scene();
        var bad = engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id), "other-player", new[] { receiver.Id.Value });
        Assert.That(bad.Accepted, Is.False); Assert.That(receiver.Mind.PendingGiftFrom, Is.Null);
        Assert.That(engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id), "owner", new[] { giver.Id.Value }).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new EndGiftSessionCommand(giver.Id, receiver.Id), "other-player", new[] { receiver.Id.Value }).Accepted, Is.False);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.EqualTo(giver.Id));
    }

    [Test]
    public void ClosingBeforeSelectionCancelsApproachAndDoesNotFreezeRecipient()
    {
        var (engine, giver, receiver) = Scene();
        engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id));
        engine.ApplyManualCommand(new EndGiftSessionCommand(giver.Id, receiver.Id));
        Assert.That(receiver.Mind.PendingGiftFrom, Is.Null); Assert.That(giver.Mind.GiftRecipientId, Is.Null);
        Assert.That(giver.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
    }

    [Test]
    public void ManualRecipientIsNotFrozenAndNewCommandsHaveReservedOpcodesWithUnchangedOldBytes()
    {
        var (engine, giver, receiver) = Scene(); receiver.Mind.ManualControl = true;
        // #742: a manual recipient is never frozen, but she is no longer an
        // "unavailable target" either — the giver simply walks up to her.
        Assert.That(engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id)).Accepted, Is.True);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.Null);
        Assert.That(giver.Mind.GiftRecipientId, Is.EqualTo(receiver.Id));
        Assert.That(Encode(new StopCommand(new EntityId(7))), Is.EqualTo(new byte[] { 9, 0, 7, 0, 0, 0 }));
        Assert.That(Encode(new SetManualControlCommand(new EntityId(7), true)), Is.EqualTo(new byte[] { 1, 0, 7, 0, 0, 0, 1 }));
        foreach (var command in new ISimulationCommand[] { new BeginGiftSessionCommand(giver.Id, receiver.Id), new EndGiftSessionCommand(giver.Id, receiver.Id) })
        {
            var bytes = Encode(command); Assert.That(bytes[0], Is.EqualTo(command is BeginGiftSessionCommand ? 54 : 55));
            using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
            var decoded = SimulationCommandCodec.Read(reader);
            Assert.That(decoded.GetType(), Is.EqualTo(command.GetType())); Assert.That(decoded.TargetEntity, Is.EqualTo(giver.Id));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }
    }

    [Test]
    public void ANewStopOrderAndSaveReloadCannotLeaveAnInvitationStuck()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        stream.Position = 0; var loaded = TestWorld.CreateWorld(world.Seed);
        loaded.Entities.Npcs[giver.Id].Mind.GiftRecipientId = receiver.Id;
        loaded.Entities.Npcs[receiver.Id].Mind.PendingGiftFrom = giver.Id;
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(loaded, reader);
        Assert.That(loaded.Entities.Npcs[giver.Id].Mind.GiftRecipientId, Is.Null);
        Assert.That(loaded.Entities.Npcs[receiver.Id].Mind.PendingGiftFrom, Is.Null);
        engine.ApplyManualCommand(new StopCommand(giver.Id));
        Assert.That(giver.Mind.GiftRecipientId, Is.Null); Assert.That(receiver.Mind.PendingGiftFrom, Is.Null);
    }

    // ── #737 / #741 / #742: подарок на живой колонии ─────────────────────

    [Test]
    public void GiftOrderedFromAiModeTakesControlAndIsDelivered()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        giver.Mind.ManualControl = false;
        giver.Inventory.Items.Clear(); receiver.Inventory.Items.Clear();
        var gift = new ItemInstance(ContentIds.Stick); giver.Inventory.Items.Add(gift);
        var owner = new[] { giver.Id.Value };
        Assert.That(engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id), "player", owner).Accepted, Is.True);
        Assert.That(giver.Mind.ManualControl, Is.True, "A gift is an order: it takes control like any other order.");
        Assert.That(engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id, receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, gift.DefinitionId), 1, InventoryTransferDirection.Give), "player", owner).Accepted, Is.True);
        engine.ApplyManualCommand(new EndGiftSessionCommand(giver.Id, receiver.Id), "player", owner);
        for (var i = 0; i < 200 && !receiver.Inventory.Items.Any(x => ReferenceEquals(x, gift)); i++) engine.Step();
        Assert.That(receiver.Inventory.Items.Any(x => ReferenceEquals(x, gift)), Is.True);
        Assert.That(world.Events.Items.Any(e => e.Type == "ManualOrderRejected" && e.EntityId == giver.Id.Value), Is.False,
            "A delivered gift must not leave a refusal toast behind.");
    }

    [Test]
    public void RememberedDangerAndOrdinaryNeedsDoNotMakeTheRecipientUnavailable()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        // A wolf seen ten minutes ago is a memory, not an emergency.
        receiver.Memory.Dangers.Add(new HexLive.Simulation.Memory.DangerMemory { Tile = receiver.Tile, Tick = world.Tick });
        giver.Memory.Dangers.Add(new HexLive.Simulation.Memory.DangerMemory { Tile = giver.Tile, Tick = world.Tick });
        giver.Needs.Thirst = 1f;
        var result = engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id));
        Assert.That(result.Accepted, Is.True, result.Reason);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.EqualTo(giver.Id));
    }

    [Test]
    public void DyingOfThirstRecipientCanStillBeGiftedButIsNotAskedToWait()
    {
        var (engine, giver, receiver) = Scene();
        receiver.Needs.Thirst = 1f;
        var result = engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id));
        Assert.That(result.Accepted, Is.True, result.Reason);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.Null, "Water is exactly what she is given; she is not held away from it.");
    }

    [Test]
    public void RecipientKeepsWaitingForAsLongAsTheGiverIsStillWalkingOver()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        Assert.That(engine.ApplyManualCommand(new BeginGiftSessionCommand(giver.Id, receiver.Id)).Accepted, Is.True);
        Assert.That(giver.Plan.Status, Is.EqualTo(PlanStatus.Active));
        // A walk across the island outlasts the 30-second invitation window.
        world.Tick = giver.Mind.GiftUntilTick + 500;
        giver.Mind.GiftUntilTick = world.Tick - 1;
        GiftSessionMath.Update(world);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.EqualTo(giver.Id));
        Assert.That(giver.Mind.GiftRecipientId, Is.EqualTo(receiver.Id));
    }

    [Test]
    public void GiveTransferAloneHoldsTheRecipientEvenWhenNoInvitationWasSent()
    {
        var (engine, giver, receiver) = Scene();
        giver.Inventory.Items.Clear(); var gift = new ItemInstance(ContentIds.Stick); giver.Inventory.Items.Add(gift);
        receiver.Movement.IsMoving = true;
        receiver.Plan.Status = PlanStatus.Active; receiver.Plan.Goal = GoalType.GatherTools;
        var result = engine.ApplyManualCommand(new TransferInventoryCommand(giver.Id, receiver.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, gift.DefinitionId), 1, InventoryTransferDirection.Give));
        Assert.That(result.Accepted, Is.True, result.Reason);
        Assert.That(receiver.Mind.PendingGiftFrom, Is.EqualTo(giver.Id));
        Assert.That(receiver.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
        for (var i = 0; i < 200 && !receiver.Inventory.Items.Any(x => ReferenceEquals(x, gift)); i++) engine.Step();
        Assert.That(receiver.Inventory.Items.Any(x => ReferenceEquals(x, gift)), Is.True);
    }

    [Test]
    public void TalkOrderStopsABusyTargetAtOnceAndSheWaitsPastTheInvitationTimeout()
    {
        var (engine, giver, receiver) = Scene(); var world = engine.World;
        receiver.Movement.IsMoving = true;
        receiver.Movement.JunctionPath.Add(receiver.CurrentJunction.Value);
        receiver.Plan.Status = PlanStatus.Active; receiver.Plan.Goal = GoalType.GatherTools;
        var result = engine.ApplyManualCommand(new TalkToCommand(giver.Id, receiver.Id));
        Assert.That(result.Accepted, Is.True, result.Reason);
        Assert.That(receiver.Plan.Status, Is.Not.EqualTo(PlanStatus.Active), "She drops her errand the moment the order is given.");
        Assert.That(receiver.Mind.PendingTalkFrom, Is.EqualTo(giver.Id));
        // The initiator is still on her way long after the 120-tick window.
        // The window is about to run out: the next medium pass of the manual
        // order system refreshes it for as long as the initiator is on her way.
        receiver.Mind.PendingTalkSinceTick =
            world.Tick - AiBalance.TalkWaitTimeoutTicks + engine.Settings.MediumInterval * 2 + 1;
        var spot = receiver.Position;
        for (var i = 0; i < engine.Settings.MediumInterval * 4 &&
             giver.Execution.CurrentInteraction != InteractionType.Talk; i++) engine.Step();
        Assert.That(receiver.Position, Is.EqualTo(spot));
        Assert.That(giver.Execution.CurrentInteraction == InteractionType.Talk ||
            world.Tick - receiver.Mind.PendingTalkSinceTick <= engine.Settings.MediumInterval, Is.True);
        Assert.That(world.Events.Items.Any(e => e.Type == "TalkWaitTimeout"), Is.False);
        for (var i = 0; i < 200 && !world.Events.Items.Any(e => e.Type == "TalkStarted"); i++) engine.Step();
        Assert.That(world.Events.Items.Any(e => e.Type == "TalkStarted" && e.EntityId == giver.Id.Value), Is.True);
    }

    [Test]
    public void ExactRepeatOfAJustAppliedInventoryDropIsSilentNotAnError()
    {
        var (engine, giver, _) = Scene(); var world = engine.World;
        giver.Inventory.Items.Clear();
        giver.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        giver.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        var drop = new ManageInventoryCommand(giver.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, ContentIds.Stick), InventoryAction.Drop);
        Assert.That(engine.ApplyManualCommand(drop).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(drop).Accepted, Is.True, "A double click is not a failure.");
        Assert.That(giver.Inventory.Items.Select(i => i.DefinitionId), Is.EqualTo(new[] { ContentIds.Bottle }),
            "The repeat must not act on whatever slid into the slot.");
        Assert.That(world.Events.Items.Any(e => e.Type == "ManualOrderRejected"), Is.False);
        // A genuinely stale reference is still reported.
        var stale = engine.ApplyManualCommand(new ManageInventoryCommand(giver.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, ContentIds.Knife), InventoryAction.Drop));
        Assert.That(stale.Accepted, Is.False); Assert.That(stale.Reason, Is.EqualTo("StaleItem"));
    }

    private static byte[] Encode(ISimulationCommand command)
    { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); SimulationCommandCodec.Write(writer, command); return stream.ToArray(); }

    private static void Place(WorldState world, NPCState npc, Junction target)
    {
        if (npc.CurrentJunction is { } previous) SpatialMutations.FreeJunction(world, previous, npc.Id);
        var oldTile = npc.Tile;
        npc.CurrentJunction = target.Id; npc.Position = target.WorldPosition; npc.Tile = target.Tiles[0]; npc.Fragment = target.Fragment;
        SpatialMutations.MoveEntityToTile(world, npc.Id, oldTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, target.Id, npc.Id);
    }
}
}
