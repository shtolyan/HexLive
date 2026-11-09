using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Behavior
{
public sealed class RecruitmentJournalTests
{
    [Test]
    public void RealVoluntaryRecruitmentWritesTheInviterAndNewcomersOwnMemories()
    {
        var f=new ColonyJournalScenario(); f.Resident.Faction=Faction.Outsiders;
        f.Visitor.Social.GetOrCreate(f.Resident.Id).Affinity=.8f;
        f.Resident.Social.GetOrCreate(f.Visitor.Id).Affinity=.8f;
        f.Resident.Social.GetOrCreate(f.Visitor.Id).Trust=.8f;
        Assert.That(f.Engine.ApplyManualCommand(new MergeCampsCommand(f.Visitor.Id,f.Resident.Id,false)).Accepted,Is.True);
        Assert.That(f.Resident.Faction,Is.EqualTo(f.Visitor.Faction));
        f.CloseHour(); f.AssertPair("OutsiderRecruited",f.Visitor,f.Resident);
        Assert.That(f.Visitor.Journal.Entries,Has.Count.EqualTo(1));Assert.That(f.Resident.Journal.Entries,Has.Count.EqualTo(1));
    }
}
}
