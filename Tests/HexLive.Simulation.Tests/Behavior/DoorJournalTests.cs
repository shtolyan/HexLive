using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Journal;
using HexLive.Simulation.Spatial;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Behavior
{
public sealed class DoorJournalTests
{
    [TestCase(false)] [TestCase(true)]
    public void RealDoorAnswerClosesAnHourFromBothPerspectives(bool welcome)
    {
        var f=new ColonyJournalScenario();
        f.Resident.Social.GetOrCreate(f.Visitor.Id).Trust=welcome?.8f:0f;
        DoorInteractionMath.Knock(f.World,f.Visitor,f.Door);
        f.CloseHour();
        var type=welcome?"DoorOpenedForGuest":"DoorRefusedGuest";
        f.AssertPair(type,f.Resident,f.Visitor);
    }
    [Test]
    public void ARealUnansweredKnockStillHasItsOwnPersonalEntry()
    {
        var f=new ColonyJournalScenario();
        f.Resident.Tile=f.World.Tiles.Items.Keys.First(t=>HexSpatialMath.HexDistance(t,f.Door.Tile)>DoorInteractionMath.HearingRadiusTiles);
        DoorInteractionMath.Knock(f.World,f.Visitor,f.Door);
        f.CloseHour();
        Assert.That(f.Visitor.Journal.Entries.Last().Type,Is.EqualTo("DoorKnocked"));
        Assert.That(f.Visitor.Journal.Entries.Last().Perspective,Is.EqualTo(JournalPerspective.Did));
        Assert.That(f.Resident.Journal.Entries.Last().QuietHours,Is.GreaterThan(0));
    }
    [Test]
    public void ActualBreakAttemptOutranksCryingAndNamesTheIntruderInTheResidentsEntry()
    {
        var f=new ColonyJournalScenario();
        DoorInteractionMath.BreakAttempt(f.World,f.Visitor,f.Door);
        Assert.That(f.Resident.Mind.CryingUntilTick,Is.GreaterThan(f.World.Tick));
        f.CloseHour(); f.AssertPair("DoorBreakInStarted",f.Visitor,f.Resident);
    }
    [Test]
    public void ActualCompletedBreakInHasBothAuthorsAndKeepsTheOneEntryPerHourLimit()
    {
        var f=new ColonyJournalScenario();
        Assert.That(f.Engine.ApplyManualCommand(new BreakDoorCommand(f.Visitor.Id,f.Door.Id)).Accepted,Is.True);
        var execution=new ExecutionSystem();
        for(var i=0;i<=DoorInteractionMath.BreakWorkTicks(f.World,f.Visitor);i++) {f.World.Tick++;execution.Run(f.World);}
        Assert.That(f.Door.Durability,Is.Zero);
        f.CloseHour(); f.AssertPair("DoorBroken",f.Visitor,f.Resident);
        Assert.That(f.Visitor.Journal.Entries,Has.Count.EqualTo(1));
        Assert.That(f.Resident.Journal.Entries,Has.Count.EqualTo(1));
    }
}
internal sealed class ColonyJournalScenario
{
    public readonly SimulationEngine Engine=TestWorld.CreateEngine();
    public WorldState World=>Engine.World;
    public readonly NPCState Visitor,Resident;
    public readonly WorldObjectState Door;
    private readonly JournalSystem journal=new JournalSystem();
    public ColonyJournalScenario()
    {
        Engine.Step(); World.Mode=GameMode.HugeIsland;
        var pair=World.Entities.Npcs.Values.Where(n=>n.Faction==Faction.Colony).OrderBy(n=>n.Id.Value).Take(2).ToArray();
        Visitor=pair[0];Resident=pair[1];
        foreach(var npc in World.Entities.Npcs.Values)
        {
            PlanInterruption.TryAbort(World,npc,InterruptionCause.PlayerCommand,"Fresh journal scenario");
            npc.Mind.CurrentGoal=GoalType.None;
            if(npc.CurrentJunction is {} old) SpatialMutations.FreeJunction(World,old,npc.Id);
            npc.CurrentJunction=null;
        }
        Assert.That(Engine.ApplyManualCommand(new SetManualControlCommand(Visitor.Id,true)).Accepted,Is.True);
        Door=World.Entities.Objects.Values.Single(o=>o.DefinitionId==DoorTopology.DoorDefinitionId);
        var hut=TestWorld.StartHut(World);hut.OwnerFaction=Faction.Colony2;
        Resident.Faction=Faction.Colony2;Resident.Mind.ManualControl=false;Resident.Traits.Add(TraitKind.Coward);
        Resident.Tile=Door.Tile;Resident.Position=World.Junctions.Items[Door.Junctions[0]].WorldPosition;
        Visitor.DisplayName="marta";Resident.DisplayName="jolly";
        var outside=World.Junctions.Items[Door.Junctions[0]].Neighbors.First(j=>!World.Junctions.Items[j].Blocked && !World.Junctions.Items[j].Tiles.Contains(hut.Tile));
        var junction=World.Junctions.Items[outside];Visitor.CurrentJunction=outside;Visitor.Position=junction.WorldPosition;Visitor.Tile=junction.Tiles[0];
        Visitor.Inventory.Items.Clear();Visitor.Inventory.Items.Add(new ItemInstance(GearCatalog.Axe));
        World.DoorStateVersion++;Assert.That(BuildingDoorRules.TryClose(World,Door.Id),Is.True);
        World.Tick=Spec136.HourTicks*3;foreach(var npc in World.Entities.Npcs.Values)npc.Journal.ClearPending();journal.Run(World);
    }
    public void CloseHour(){World.Tick+=Spec136.HourTicks;journal.Run(World);}
    public void AssertPair(string type,NPCState actor,NPCState recipient)
    {
        var did=actor.Journal.Entries.Last();var got=recipient.Journal.Entries.Last();
        Assert.That(did.Type,Is.EqualTo(type));Assert.That(got.Type,Is.EqualTo(type));
        Assert.That(did.Perspective,Is.EqualTo(JournalPerspective.Did));Assert.That(got.Perspective,Is.EqualTo(JournalPerspective.Received));
        Assert.That(did.SubjectNameId,Is.EqualTo(recipient.DisplayName));Assert.That(got.SubjectNameId,Is.EqualTo(actor.DisplayName));
        Assert.That(did.QuietHours,Is.Zero);Assert.That(got.QuietHours,Is.Zero);
    }
}
}
