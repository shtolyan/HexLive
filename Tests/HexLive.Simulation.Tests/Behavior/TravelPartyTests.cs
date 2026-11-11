using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class TravelPartyTests
{
    private static (SimulationEngine engine,NPCState leader,NPCState member) Setup(float trust=0.7f)
    {
        var engine=TestWorld.CreateEngine(); var world=engine.World;
        var pair=world.Entities.Npcs.Values.Where(n=>n.Faction==Faction.Colony).OrderBy(n=>n.Id.Value).Take(2).ToArray();
        var leader=pair[0]; var member=pair[1];
        leader.Needs.Hunger=leader.Needs.Thirst=0f;
        Assert.That(engine.ApplyManualCommand(new SetManualControlCommand(leader.Id,true)).Accepted,Is.True);
        engine.Step();
        member.Faction=Faction.Colony2;
        member.Mind.ManualControl=false;
        member.Needs.Energy=1f;
        member.Social.GetOrCreate(leader.Id).Trust=trust;
        var dest=SpatialQueries.GetPassableNeighbors(world,leader.CurrentJunction!.Value).First(j=>SpatialQueries.IsJunctionFree(world,j));
        if(member.CurrentJunction is {} previous) SpatialMutations.FreeJunction(world,previous,member.Id);
        var oldTile=member.Tile; var junction=world.Junctions.Items[dest];
        member.Tile=junction.Tiles[0]; member.Position=junction.WorldPosition; member.Fragment=junction.Fragment; member.CurrentJunction=dest;
        SpatialMutations.MoveEntityToTile(world,member.Id,oldTile,member.Tile); SpatialMutations.OccupyJunction(world,dest,member.Id);
        return(engine,leader,member);
    }

    [TestCase(0.60f,false)]
    [TestCase(0.6001f,true)]
    public void ConsentThresholdIsStrictAndGrantBelongsOnlyToActualAssignedLeader(float trust,bool accept)
    {
        var(engine,leader,member)=Setup(trust); var world=engine.World;
        var permanent=world.PlayerControlledNpcs.ToArray(); var camp=member.Faction;
        var result=engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id,member.Id));
        Assert.That(result.Accepted,Is.EqualTo(accept),result.Reason);
        Assert.That(PlayerAuthority.CanControl(world,member.Id,out _),Is.EqualTo(accept));
        Assert.That(TravelPartyMath.EffectiveAssignments(world,new[]{leader.Id.Value}).Contains(member.Id.Value),Is.EqualTo(accept));
        Assert.That(TravelPartyMath.EffectiveAssignments(world,new[]{99999}).Contains(member.Id.Value),Is.False);
        Assert.That(world.PlayerControlledNpcs,Is.EquivalentTo(permanent));
        Assert.That(member.Faction,Is.EqualTo(camp));
        if(accept)
        {
            Assert.That(FactionRelations.AreAllies(leader,member),Is.True);
            Assert.That(FactionRelations.AreHostile(leader,member),Is.False);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InvitationChecksCompanionTrustBeforeGrantingControl(bool far)
    {
        var (engine, leader, member) = Setup(far ? 1f : 0f);
        leader.Social.GetOrCreate(member.Id).Trust = 1f;
        if (far) member.Position = leader.Position + new Float2(4.5f, 0f);

        var result = engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id, member.Id));

        Assert.That(result.Reason, Is.EqualTo(far ? "TravelTargetTooFar" : "TravelTrustTooLow"));
        Assert.That(member.Mind.TravelLeaderNpcId, Is.Null);
        Assert.That(member.Mind.ManualControl, Is.False);
    }

    [Test]
    public void ATrustingCompanionCanAcceptAnInvitationWhileGardening()
    {
        var (engine, leader, member) = Setup();
        member.Mind.CurrentGoal = member.Plan.Goal = GoalType.TendGarden;
        member.Plan.Status = PlanStatus.Active;
        member.Execution.Status = ExecutionStatus.InProgress;
        member.Execution.CurrentInteraction = InteractionType.PickUp;

        var result = engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id, member.Id));

        Assert.That(result.Accepted, Is.True, result.Reason);
        Assert.That(member.Mind.TravelLeaderNpcId, Is.EqualTo(leader.Id));
        Assert.That(member.Execution.CurrentInteraction, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AiSwitchOfMemberOrLeaderEndsScopedControl(bool switchLeader)
    {
        var(engine,leader,member)=Setup();
        Assert.That(engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id,member.Id)).Accepted,Is.True);
        Assert.That(engine.ApplyManualCommand(new SetManualControlCommand(switchLeader?leader.Id:member.Id,false)).Accepted,Is.True);
        Assert.That(member.Mind.TravelLeaderNpcId,Is.Null);
        Assert.That(member.Mind.ManualControl,Is.False);
        Assert.That(PlayerAuthority.CanControl(engine.World,member.Id,out _),Is.False);
    }

    [Test]
    public void ExplicitReleaseAndLeaderDeathEndControl()
    {
        var(engine,leader,member)=Setup(); var world=engine.World;
        Assert.That(engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id,member.Id)).Accepted,Is.True);
        Assert.That(engine.ApplyManualCommand(new ReleaseTravelPartyCommand(leader.Id,member.Id)).Accepted,Is.True);
        Assert.That(member.Mind.TravelLeaderNpcId,Is.Null);
        Assert.That(engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id,member.Id)).Accepted,Is.True);
        leader.Health=0;
        new ManualOrderSystem().Run(world);
        Assert.That(member.Mind.TravelLeaderNpcId,Is.Null);
        Assert.That(member.Mind.ManualControl,Is.False);
    }

    [Test]
    public void GroupAiSwitchRevokesGrantImmediately()
    {
        var(engine,leader,member)=Setup();
        Assert.That(engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id,member.Id)).Accepted,Is.True);
        Assert.That(engine.ApplyManualCommand(new SetGroupManualControlCommand(new[]{member.Id},false)).Accepted,Is.True);
        Assert.That(member.Mind.TravelLeaderNpcId,Is.Null);
        Assert.That(PlayerAuthority.CanControl(engine.World,member.Id,out _),Is.False);
    }

    [TestCase(89)]
    [TestCase(90)]
    public void SaveVersionReadsCanonicallyAndNewPartySaveNormalizesOfflineGrantToAi(int version)
    {
        var(engine,leader,member)=Setup(); var world=engine.World;
        if(version>=90) Assert.That(engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id,member.Id)).Accepted,Is.True);
        using var bytes=new MemoryStream();
        using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.WriteAtVersion(world,writer,version);
        bytes.Position=0; var loaded=TestWorld.CreateWorld(world.Seed);
        using(var reader=new BinaryReader(bytes,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Read(loaded,reader);
        var person=loaded.Entities.Npcs[member.Id];
        Assert.That(person.Mind.TravelLeaderNpcId,Is.Null);
        Assert.That(person.Mind.ManualControl,Is.False);
        Assert.That(person.Faction,Is.EqualTo(member.Faction));
        Assert.That(loaded.PlayerControlledNpcs.Contains(member.Id.Value),Is.False);
    }

    [Test]
    public void CompanionCanReceiveOrdinaryGroupMovementWithoutTakingForeignConstructionRights()
    {
        var(engine,leader,member)=Setup(); var world=engine.World;
        Assert.That(engine.ApplyManualCommand(new RequestTravelPartyCommand(leader.Id,member.Id)).Accepted,Is.True);
        var dest=world.Junctions.Items.Values.First(j=>!j.Blocked && j.Tiles.Count>0 &&
            HexSpatialMath.Distance(j.WorldPosition,leader.Position)>1f &&
            HexSpatialMath.Distance(j.WorldPosition,leader.Position)<2f && SpatialQueries.IsJunctionFree(world,j.Id));
        var result=engine.ApplyManualCommand(new GroupMoveCommand(new[]{leader.Id,member.Id},dest.WorldPosition));
        Assert.That(result.Accepted,Is.True,result.Reason);
        Assert.That(member.Plan.Status,Is.EqualTo(AI.PlanStatus.Active));
        Assert.That(ConstructionAccess.Factions(world,new[]{leader.Id.Value,member.Id.Value}),Does.Not.Contain(member.Faction));
    }
}
}
