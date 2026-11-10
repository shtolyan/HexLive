using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class ManualForeignPreyTests
{
    private static (SimulationEngine Engine, NPCState Actor, NPCState Victim) Fixture(Faction faction)
    {
        var engine=TestWorld.CreateEngine();
        foreach(var npc in engine.World.Entities.Npcs.Values)
        {
            npc.Mind.ManualControl=true;
            npc.Needs.Hunger=npc.Needs.Thirst=0;
        }
        engine.Step();
        var pair=engine.World.Entities.Npcs.Values.Where(n=>n.Faction==Faction.Colony).Take(2).ToArray();
        var actor=pair[0];var victim=pair[1];
        actor.Inventory.Items.Add(new ItemInstance("tool.knife"));
        victim.Faction=faction;
        actor.IsFighting=false;actor.Mind.CombatOpponentNpcId=null;
        return(engine,actor,victim);
    }

    [TestCase(Faction.Outsiders)]
    [TestCase(Faction.Colony2)]
    public void NamedSleepingForeignVictimIsAdmittedAndEngaged(Faction faction)
    {
        var(engine,actor,victim)=Fixture(faction);
        // This case directly exercises the adjacency resolver, without
        // stepping movement after the temporary colocated junction.
        victim.CurrentJunction=actor.CurrentJunction;
        victim.Tile=actor.Tile;victim.Position=actor.Position;
        victim.Execution.CurrentInteraction=InteractionType.Sleep;
        var admission=ManualCommandExecutor.Apply(engine.World,new PreyPersonCommand(actor.Id,victim.Id));
        Assert.That(admission.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted),admission.Reason);
        Assert.That(actor.Mind.ManualAttackNpcId,Is.EqualTo(victim.Id));
        var previous=SimBalance.TimedMeleeEverywhere;
        try
        {
            SimBalance.TimedMeleeEverywhere=true;
            new PredationSystem().Run(engine.World);
            Assert.That(actor.IsFighting,Is.True);
            Assert.That(actor.Mind.CombatOpponentNpcId,Is.EqualTo(victim.Id));
        }
        finally{SimBalance.TimedMeleeEverywhere=previous;}
    }

    [Test]
    public void AutonomousPreyStillCannotEngageAnOutsider()
    {
        var(engine,actor,victim)=Fixture(Faction.Outsiders);
        foreach(var other in engine.World.Entities.Npcs.Values.Where(n=>n.Id!=actor.Id&&n.Id!=victim.Id))other.Health=0;
        actor.Mind.ManualControl=false;actor.Mind.CurrentGoal=GoalType.Prey;
        actor.Mind.ManualAttackNpcId=null;
        new PredationSystem().Run(engine.World);
        Assert.That(actor.IsFighting,Is.False);
        Assert.That(actor.Mind.CombatOpponentNpcId,Is.Null);
    }

    [Test]
    public void ForeignPreyStillContinuesToButcheryWhenTheNamedVictimFalls()
    {
        var(engine,actor,victim)=Fixture(Faction.Outsiders);
        Assert.That(ManualCommandExecutor.Apply(engine.World,new PreyPersonCommand(actor.Id,victim.Id)).Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        victim.Mind.FaintedUntilTick=100000;
        for(int i=0;i<2000&&!engine.World.Events.Items.Any(e=>e.Type=="Butchered");i++)engine.Step();
        Assert.That(engine.World.Events.Items.Any(e=>e.Type=="Butchered"),Is.True);
        Assert.That(actor.Mind.ManualAttackNpcId,Is.Null);
    }
}
