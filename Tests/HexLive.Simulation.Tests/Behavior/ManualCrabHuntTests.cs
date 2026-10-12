using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class ManualCrabHuntTests
{
    [Test]
    public void SelectedCrabIsKilledThroughExistingHuntingSystemButItsNeighborIsNot()
    {
        var world=TestWorld.CreateWorld(45001);
        var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        npc.CurrentJunction ??= world.Tiles.Items[npc.Tile].Junctions[0];
        foreach(var n in world.Entities.Npcs.Values) n.Mind.CurrentGoal=GoalType.None;
        npc.Mind.ManualControl=true;
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Spear));
        world.Rabbits.Clear(); world.MobSpawnSlots.Clear();
        var neighbor=new RabbitState {Id=601,Junction=npc.CurrentJunction.Value,Tile=npc.Tile};
        var target=new RabbitState {Id=602,Junction=npc.CurrentJunction.Value,Tile=npc.Tile};
        world.Rabbits.Add(neighbor);world.Rabbits.Add(target);
        var result=ManualCommandExecutor.Apply(world,new AttackMobCommand(npc.Id,ManualCrabHunt.TargetId(target.Id)));
        Assert.That(result.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        Assert.That(npc.Mind.CombatAssistDogId,Is.Null,"Crab id must never start dog combat.");
        var hunt=new RabbitSystem();
        for(var i=0;i<512 && world.Rabbits.Contains(target);i++)
        {
            target.Junction=npc.CurrentJunction.Value;target.Tile=npc.Tile;
            world.Tick++;hunt.Run(world);
        }
        Assert.That(world.Rabbits.Contains(target),Is.False,"Real hunt must produce a kill.");
        Assert.That(world.Rabbits.Contains(neighbor),Is.True,"Manual intent selects exactly one crab.");
        Assert.That(npc.Mind.CurrentGoal,Is.EqualTo(GoalType.None),"Successful hunt releases manual control for the next order.");
        Assert.That(npc.Mind.ManualAttackMobId,Is.Null);
        Assert.That(world.Entities.Objects.Values.Any(o=>o.DefinitionId=="carcass.animal" && o.Variant==MobIds.Crab),Is.True);
    }

    [Test]
    public void HuntWithoutSpearIsRejectedBeforeReplacingAnOrder()
    {
        var world=TestWorld.CreateWorld(45002);
        var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        npc.CurrentJunction ??= world.Tiles.Items[npc.Tile].Junctions[0];
        npc.Mind.ManualControl=true;npc.Inventory.Items.Remove(ContentIds.Spear);
        world.Rabbits.Add(new RabbitState {Id=603,Junction=npc.CurrentJunction.Value,Tile=npc.Tile});
        var result=ManualCommandExecutor.Apply(world,new AttackMobCommand(npc.Id,ManualCrabHunt.TargetId(603)));
        Assert.That(result.Status,Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(result.Reason,Is.EqualTo("MissingTool"));
        Assert.That(npc.Mind.ManualAttackMobId,Is.Null);
    }

    [Test]
    public void VirtualSlotRemainsTheSameTargetAfterMaterializationAndCooldownEndsHunt()
    {
        var world=TestWorld.CreateWorld(45003);
        var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        npc.CurrentJunction ??= world.Tiles.Items[npc.Tile].Junctions[0];
        npc.Mind.ManualControl=true;npc.Inventory.Items.Add(new ItemInstance(ContentIds.Spear));
        var waypoint=new PatrolWaypoint {Junction=npc.CurrentJunction.Value,Tile=npc.Tile,Position=npc.Position};
        var slot=new MobSpawnSlot {SlotId=987,MobId=MobIds.Crab,ReservedMobId=604,State=MobSlotState.Virtual};
        slot.Ring.Add(waypoint);world.MobSpawnSlots.Add(slot);
        var targetId=ManualCrabHunt.TargetId(604);
        Assert.That(ManualCommandExecutor.Apply(world,new AttackMobCommand(npc.Id,targetId)).Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        var orders=new ManualOrderSystem();orders.Run(world);
        Assert.That(npc.Plan.TargetJunctionId,Is.EqualTo(waypoint.Junction));
        slot.State=MobSlotState.Live;
        var live=new RabbitState {Id=604,Junction=waypoint.Junction,Tile=waypoint.Tile};world.Rabbits.Add(live);
        Assert.That(ManualCrabHunt.TryTarget(world,targetId,out var junction,out _),Is.True);
        Assert.That(junction,Is.EqualTo(waypoint.Junction));
        world.Rabbits.Remove(live);slot.State=MobSlotState.Cooldown;orders.Run(world);
        Assert.That(npc.Mind.ManualAttackMobId,Is.Null);
        Assert.That(npc.Mind.CurrentGoal,Is.EqualTo(GoalType.None));
    }
}
}
