using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class OutfitLockTests
{
    [Test]
    public void LockCommandRejectsWearAndRemovalButLeavesCarriedDropsAvailable_Bug193()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony && candidate.Health > 0f);
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();

        const string garmentId = "underwear.bra_riot";
        var worn = new ItemInstance(garmentId) { OwnerId = npc.Id.Value };
        npc.WornItems.Add(worn);
        npc.Inventory.Items.Add(new ItemInstance(garmentId) { OwnerId = npc.Id.Value });

        var lockResult = engine.ApplyManualCommand(
            new SetOutfitLockCommand(npc.Id, enabled: true));
        var stowResult = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(InventoryItemSource.Worn, 0, garmentId),
            InventoryAction.Stow));
        var wearResult = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, garmentId),
            InventoryAction.Wear));
        var dropCarriedResult = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, garmentId),
            InventoryAction.Drop));

        Assert.Multiple(() =>
        {
            Assert.That(lockResult.Accepted, Is.True);
            Assert.That(npc.Mind.OutfitLocked, Is.True);
            Assert.That(stowResult.Accepted, Is.False);
            Assert.That(stowResult.Reason, Is.EqualTo("OutfitLocked"));
            Assert.That(wearResult.Accepted, Is.False);
            Assert.That(wearResult.Reason, Is.EqualTo("OutfitLocked"));
            Assert.That(dropCarriedResult.Accepted, Is.True,
                "Закрепление одежды не должно запирать обычные carried-предметы.");
            Assert.That(npc.WornItems.Single(), Is.SameAs(worn));
        });
    }

    [Test]
    public void ActiveHeatUndressCannotCrossTheLock_Bug193()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony && candidate.Health > 0f);
        npc.WornItems.Clear();
        var garment = new ItemInstance("clothing.jacket_autumn")
            { OwnerId = npc.Id.Value };
        npc.WornItems.Add(garment);
        npc.Mind.OutfitLocked = true;
        npc.Mind.CurrentGoal = GoalType.Undress;
        npc.Plan.Goal = GoalType.Undress;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.UndressItem,
            Interaction = InteractionType.Undress
        });

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Single(), Is.SameAs(garment));
            Assert.That(npc.Execution.CurrentInteraction, Is.Null);
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Failed));
        });
    }

    [Test]
    public void LockDuringUndressHandoffRestoresTheSameInstance_Bug193()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony && candidate.Health > 0f);
        npc.WornItems.Clear();
        var garment = new ItemInstance("clothing.jacket_autumn")
            { Durability = 0.61f, OwnerId = npc.Id.Value };
        npc.Mind.OutfitLocked = true;
        npc.Mind.CurrentGoal = GoalType.Undress;
        npc.Plan.Goal = GoalType.Undress;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.UndressItem,
            Interaction = InteractionType.Undress
        });
        // State immediately after WardrobeHandoffFraction: no longer worn,
        // but the exact authoritative instance is still in the acting hand.
        npc.Execution.HeldGarment = garment;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Undress;

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Single(), Is.SameAs(garment));
            Assert.That(npc.WornItems[0].Durability, Is.EqualTo(0.61f));
            Assert.That(npc.Execution.HeldGarment, Is.Null);
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Failed));
        });
    }

    [Test]
    public void LockCancelsCleanClothesBathBeforeDoffCompletes_Bug193()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony && candidate.Health > 0f);
        var standState = world.Junctions.Items.Values.First(junction =>
            junction.Tiles.Count > 0);
        var stand = standState.Id;
        npc.CurrentJunction = stand;
        npc.Tile = standState.Tiles[0];
        npc.Position = standState.WorldPosition;
        npc.WornItems.Clear();
        var garment = new ItemInstance("underwear.bra_riot")
            { OwnerId = npc.Id.Value };
        npc.WornItems.Add(garment);
        npc.Needs.Hygiene = 0.2f;
        npc.Mind.OutfitLocked = true;
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.Bathing;
        npc.Mind.RedressShore = stand;
        npc.Mind.PersonalCareBathShore = stand;
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = stand;
        npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = stand,
            TimeoutEndTick = stand.Value
        });
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Undress;
        npc.Execution.EndTick = world.Tick + 5;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Single(), Is.SameAs(garment));
            Assert.That(npc.Needs.Hygiene, Is.EqualTo(0.2f));
            Assert.That(npc.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.None));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Execution.CurrentInteraction, Is.Null);
        });
    }

    [Test]
    public void OutfitLockSurvivesSaveLoad_Bug193()
    {
        const int seed = 193193;
        var world = TestWorld.CreateWorld(seed);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony);
        npc.Mind.OutfitLocked = true;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        Assert.That(loaded.Entities.Npcs[npc.Id].Mind.OutfitLocked, Is.True);
    }
}
