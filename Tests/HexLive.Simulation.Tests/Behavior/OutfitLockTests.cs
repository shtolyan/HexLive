using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
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
            Assert.That(npc.Mind.DesiredOutfit.Select(piece => piece.DefinitionId),
                Is.EqualTo(new[] { garmentId }),
                "Включение закрепления должно выбрать текущий комплект.");
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
    public void SelectedDryingPieceIsRememberedAndRestoredOnlyAfterAuditWhenDry()
    {
        var engine = TestWorld.CreateEngine(133010);
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony && candidate.Health > 0f);
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.Mind.Cooldowns.Clear();
        var rackAnchor = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && junction.Tiles.Count > 0);
        npc.CurrentJunction = rackAnchor.Id;
        npc.Tile = rackAnchor.Tiles[0];
        npc.Position = rackAnchor.WorldPosition;

        const string garmentId = "clothing.jacket_autumn";
        var selected = new ItemInstance(garmentId)
        {
            OwnerId = npc.Id.Value,
            Wetness = 1f,
            Durability = 0.63f
        };
        npc.WornItems.Add(selected);
        Assert.That(engine.ApplyManualCommand(
            new SetOutfitLockCommand(npc.Id, enabled: true)).Accepted, Is.True);

        var rack = WorldObjectMutations.SpawnObject(
            world, ContentIds.DryingRack, npc.Fragment, npc.Tile, rackAnchor.Id);
        npc.WornItems.Remove(selected);
        EquipmentMath.Recalculate(world, npc);
        var hung = ExecutionSystem.StowGarmentWithContents(
            world, npc, selected, rack.Id);

        Assert.That(hung, Is.Not.Null);
        Assert.That(npc.Mind.DesiredOutfit.Single().GroundObjectId,
            Is.EqualTo(hung.Id),
            "Закреплённая вещь должна помнить точный объект на сушилке.");
        Assert.That(npc.Memory.KnownObjects[hung.Id].IsPermanent, Is.True,
            "Собственную повешенную вещь нельзя забыть по обычному TTL.");

        new PerceptionSystem().Run(world);
        world.Tick = npc.Mind.NextOutfitMaintenanceTick;
        Assert.That(OutfitMaintenanceMath.RefreshMaintenanceTarget(world, npc), Is.False,
            "Мокрую вещь задача помнит, но надевать не должна.");

        hung.Wetness = 0f;
        var spare = WorldObjectMutations.SpawnObject(
            world, garmentId, npc.Fragment, npc.Tile, rackAnchor.Id);
        spare.Owner = npc.Id;
        world.Tick++;
        new PerceptionSystem().Run(world);
        Assert.That(OutfitMaintenanceMath.RefreshMaintenanceTarget(world, npc), Is.False,
            "Полная сверка комплекта не должна выполняться каждый тик.");

        world.Tick = npc.Mind.NextOutfitMaintenanceTick;
        new PerceptionSystem().Run(world);
        Assert.That(OutfitMaintenanceMath.RefreshMaintenanceTarget(world, npc), Is.True);
        Assert.That(npc.Mind.OutfitMaintenanceTargetObjectId, Is.EqualTo(hung.Id));

        new DecisionSystem().Run(world);
        var dressScore = npc.Mind.LastScores.Single(score => score.Goal == GoalType.Dress);
        var idleScore = npc.Mind.LastScores.Single(score => score.Goal == GoalType.Idle);
        Assert.That(dressScore.FinalScore, Is.GreaterThan(idleScore.FinalScore),
            "Возврат выбранного комплекта должен иметь высокий бытовой приоритет.");
        npc.Mind.CurrentGoal = GoalType.Dress;
        npc.Plan.Goal = GoalType.Dress;
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.Steps.Clear();
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(hung.Id),
            "Даже одинаковая запасная вещь не должна подменить точный объект комплекта.");

        var definition = world.Content.ObjectDefinitions[garmentId];
        var dress = definition.Interactions.Single(interaction =>
            interaction.Type == InteractionType.Dress);
        Assert.That(ExecutionSystem.CompleteDress(
            world, npc, hung, definition, dress, string.Empty), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Select(item => item.DefinitionId),
                Does.Contain(garmentId));
            Assert.That(npc.WornItems.Single(item => item.DefinitionId == garmentId).Durability,
                Is.EqualTo(0.63f));
            Assert.That(npc.Mind.DesiredOutfit.Single().GroundObjectId, Is.Null);
            Assert.That(npc.Memory.KnownObjects.ContainsKey(hung.Id), Is.False);
            Assert.That(npc.Mind.Cooldowns.Any(cooldown => cooldown.Goal == GoalType.Dress),
                Is.False,
                "Выбранный комплект не должен ждать обычный cooldown между вещами.");
        });
    }

    [Test]
    public void RepeatedEnableDoesNotRecaptureNakedBodyWhileSelectedPieceIsDrying()
    {
        var world = TestWorld.CreateWorld(133011);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony && candidate.Health > 0f);
        var stand = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && junction.Tiles.Count > 0);
        npc.CurrentJunction = stand.Id;
        npc.Tile = stand.Tiles[0];
        npc.Position = stand.WorldPosition;
        npc.WornItems.Clear();
        var selected = new ItemInstance("underwear.bra_riot")
            { OwnerId = npc.Id.Value, Wetness = 1f };
        npc.WornItems.Add(selected);
        OutfitMaintenanceMath.SetLockedOutfit(world, npc, enabled: true);
        npc.WornItems.Clear();
        var ground = ExecutionSystem.DropGarmentWithContents(world, npc, selected);

        OutfitMaintenanceMath.SetLockedOutfit(world, npc, enabled: true);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.DesiredOutfit.Count, Is.EqualTo(1));
            Assert.That(npc.Mind.DesiredOutfit[0].DefinitionId,
                Is.EqualTo(selected.DefinitionId));
            Assert.That(npc.Mind.DesiredOutfit[0].GroundObjectId,
                Is.EqualTo(ground.Id));
        });
    }

    [Test]
    public void ExactBorrowedPieceReturnsWithoutTurningMaintenanceIntoGeneralPermission()
    {
        var world = TestWorld.CreateWorld(133012);
        var colony = world.Entities.Npcs.Values
            .Where(candidate => candidate.Faction == Faction.Colony && candidate.Health > 0f)
            .Take(2).ToArray();
        Assert.That(colony.Length, Is.EqualTo(2));
        var wearer = colony[0];
        var owner = colony[1];
        var stand = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && junction.Tiles.Count > 0);
        wearer.CurrentJunction = stand.Id;
        wearer.Tile = stand.Tiles[0];
        wearer.Position = stand.WorldPosition;
        wearer.WornItems.Clear();

        const string garmentId = "underwear.bra_riot";
        var borrowed = new ItemInstance(garmentId)
        {
            OwnerId = owner.Id.Value,
            Wetness = 0f
        };
        wearer.WornItems.Add(borrowed);
        OutfitMaintenanceMath.SetLockedOutfit(world, wearer, enabled: true);
        wearer.WornItems.Clear();
        var ground = ExecutionSystem.DropGarmentWithContents(world, wearer, borrowed);

        new PerceptionSystem().Run(world);
        world.Tick = wearer.Mind.NextOutfitMaintenanceTick;
        Assert.That(OutfitMaintenanceMath.RefreshMaintenanceTarget(world, wearer), Is.True);

        var definition = world.Content.ObjectDefinitions[garmentId];
        var dress = definition.Interactions.Single(interaction =>
            interaction.Type == InteractionType.Dress);
        Assert.That(ExecutionSystem.CompleteDress(
            world, wearer, ground, definition, dress, string.Empty), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(wearer.WornItems.Single().OwnerId, Is.EqualTo(owner.Id.Value),
                "Транзакционный возврат не должен присваивать одолженную вещь.");
            Assert.That(wearer.Mind.WearGrants, Is.Empty,
                "Постоянный комплект не должен выдавать общее разрешение на чужую одежду.");
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
    public void LockKeepsExactOutfitButDoesNotCancelBath()
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
            Assert.That(npc.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.Bathing));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Bathe));
            Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Undress));
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

    [Test]
    public void SelectedOutfitAndExactGroundObjectSurviveSaveLoad()
    {
        const int seed = 133057;
        var world = TestWorld.CreateWorld(seed);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony);
        var stand = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && junction.Tiles.Count > 0);
        npc.CurrentJunction = stand.Id;
        npc.Tile = stand.Tiles[0];
        npc.Position = stand.WorldPosition;
        npc.WornItems.Clear();
        var selected = new ItemInstance("clothing.jacket_autumn")
        {
            OwnerId = npc.Id.Value,
            Wetness = 0.8f
        };
        npc.WornItems.Add(selected);
        OutfitMaintenanceMath.SetLockedOutfit(world, npc, enabled: true);
        npc.WornItems.Clear();
        var ground = ExecutionSystem.DropGarmentWithContents(world, npc, selected);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loadedWorld = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(
                   stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loadedWorld, reader);
        }

        var loaded = loadedWorld.Entities.Npcs[npc.Id];
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Mind.OutfitLocked, Is.True);
            Assert.That(loaded.Mind.DesiredOutfit.Count, Is.EqualTo(1));
            Assert.That(loaded.Mind.DesiredOutfit[0].DefinitionId,
                Is.EqualTo(selected.DefinitionId));
            Assert.That(loaded.Mind.DesiredOutfit[0].GroundObjectId,
                Is.EqualTo(ground.Id));
            Assert.That(loadedWorld.Entities.Objects.ContainsKey(ground.Id), Is.True);
        });
    }
}
