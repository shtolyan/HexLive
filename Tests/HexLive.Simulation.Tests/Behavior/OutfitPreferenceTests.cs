using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class OutfitPreferenceTests
{
    private static (WorldState World, NPCState Npc) Scene()
    {
        var engine = TestWorld.CreateEngine(70001);
        for (var i = 0; i < 4; i++) engine.Step();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        world.Mobs.Clear();
        world.Environment.GlobalTemperature = 20f;
        world.Environment.TimeOfDayNormalized = 0f;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Needs.ThermalComfort = 0f;
        npc.Needs.ThermalDiscomfort = 0f;
        npc.Mind.Cooldowns.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Memory.Dangers.Clear();
        npc.Perception.Agents.Clear();
        npc.Perception.Objects.Clear();
        npc.Inventory.Items.Clear();
        npc.WornItems.RemoveAll(item => world.Content.ObjectDefinitions[item.DefinitionId].Layer != WearLayer.Bags);
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.Completed;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.IsMoving = false;
        return (world, npc);
    }

    private static string[] RankedIds(NPCState npc) =>
        new[] { "test.outfit_a", "test.outfit_b", "test.outfit_c" }
            .OrderBy(id => ItemAffinity.For(npc.Id.Value, id)).ToArray();

    private static void Register(WorldState world, string id, float warmth = 0f, float armor = 0f)
    {
        var definition = new ObjectDefinition { Id = id, Layer = WearLayer.Wear, Covers = { BodyPart.Torso } };
        definition.Interactions.Add(new InteractionDefinition
        {
            Id = "dress." + id,
            Type = InteractionType.Dress,
            Effects = { WarmthDelta = warmth, ArmorDelta = armor }
        });
        world.Content.ObjectDefinitions[id] = definition;
    }

    private static WorldObjectState Offer(WorldState world, NPCState npc, string id, float distance)
    {
        var garment = WorldObjectMutations.SpawnObject(world, id, npc.Fragment, npc.Tile, npc.CurrentJunction!.Value);
        garment.Owner = npc.Id;
        var seen = new PerceivedObject
        {
            Id = garment.Id, DefinitionId = id, Tile = garment.Tile,
            Distance = distance, IsReachable = true
        };
        seen.AvailableInteractions.Add(InteractionType.Dress);
        npc.Perception.Objects.Add(seen);
        return garment;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PeacefulDressingChoosesTasteAndColdDressingChoosesWarmth(bool cold)
    {
        var (world, npc) = Scene();
        var ids = RankedIds(npc);
        Register(world, ids[0]);
        Register(world, ids[1], warmth: 0.35f);
        Register(world, ids[2]);
        npc.WornItems.Add(new ItemInstance(ids[0]) { OwnerId = npc.Id.Value });
        EquipmentMath.Recalculate(world, npc);
        var warmer = Offer(world, npc, ids[1], 0.1f);
        var favorite = Offer(world, npc, ids[2], 1f);
        if (cold) world.Environment.GlobalTemperature = 10f;

        new DecisionSystem().Run(world);
        var dress = npc.Mind.LastScores.Single(score => score.Goal == GoalType.Dress);
        Assert.That(dress.FinalScore, Is.Positive);
        npc.Mind.CurrentGoal = GoalType.Dress;
        npc.Plan.Status = PlanStatus.Completed;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(cold ? warmer.Id : favorite.Id));
    }

    [Test]
    public void HuntSelectsAProtectiveGarmentAheadOfTheFavorite()
    {
        var (world, npc) = Scene();
        var ids = RankedIds(npc);
        Register(world, ids[0], armor: 0.25f);
        Register(world, ids[2]);
        var protection = Offer(world, npc, ids[0], 1f);
        Offer(world, npc, ids[2], 0.1f);
        world.Rabbits.Clear();
        world.Rabbits.Add(new RabbitState { Id = 700, Tile = npc.Tile, Junction = npc.CurrentJunction!.Value });
        npc.Inventory.Items.Add(ContentIds.Bow);
        npc.Inventory.Items.Add(ContentIds.Arrow);
        npc.Mind.CurrentGoal = GoalType.Hunt;
        var planning = new PlanningSystem();

        planning.Run(world);
        planning.Run(world);

        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(protection.Id));
        Assert.That(npc.Plan.Steps[^1].Interaction, Is.EqualTo(InteractionType.Dress));
        new RabbitSystem().Run(world);
        Assert.That(npc.Inventory.Items.Contains(ContentIds.Arrow), Is.True,
            "The bow stays ready while the hunter is putting on protection.");

        var armor = world.Content.ObjectDefinitions[protection.DefinitionId];
        Assert.That(ExecutionSystem.CompleteDress(
            world, npc, protection, armor, armor.Interactions.Single(), string.Empty), Is.True);
        Assert.That(npc.EquippedArmor, Is.EqualTo(0.25f));
    }

    [Test]
    public void LegacySaveAndCommandAllowOrdinaryWearAndKeepThePhysicalGarment()
    {
        var (world, npc) = Scene();
        var oldPin = new ItemInstance("clothing.jacket_autumn")
        {
            OwnerId = npc.Id.Value,
            Durability = 0.73f
        };
        npc.WornItems.Add(oldPin);
        npc.Mind.OutfitLocked = true;
        npc.Mind.DesiredOutfit.Add(new DesiredOutfitPiece { DefinitionId = oldPin.DefinitionId });
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var loaded = TestWorld.CreateWorld(70001);
        using (var reader = new BinaryReader(bytes))
            WorldSaveSerializer.Read(loaded, reader);
        var actor = loaded.Entities.Npcs[npc.Id];
        var snapshot = WorldSnapshotExporter.Export(loaded);
        Assert.That(snapshot.Npcs.Single(n => n.Id == actor.Id).OutfitLocked, Is.False);
        var index = actor.WornItems.FindIndex(item => item.DefinitionId == oldPin.DefinitionId);
        var item = actor.WornItems[index];
        Assert.That(item.Durability, Is.EqualTo(0.73f));
        var legacyCommand = ManualCommandExecutor.Apply(loaded, new SetOutfitLockCommand(actor.Id, true));
        var stowResult = ManualCommandExecutor.Apply(loaded, new ManageInventoryCommand(actor.Id,
            new InventoryItemRef(InventoryItemSource.Worn, index, item.DefinitionId), InventoryAction.Stow));

        Assert.That(legacyCommand.Accepted, Is.True);
        Assert.That(stowResult.Accepted, Is.True, stowResult.Reason);
        Assert.That(actor.Inventory.Items.Any(carried => ReferenceEquals(carried, item)), Is.True);
    }
}

}
