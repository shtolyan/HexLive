using System.Linq;
using HexLive.Simulation.Runtime;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Behavior
{
public sealed class TravelPartyJournalTests
{
    [Test]
    public void RealJoinAndReleaseInDifferentHoursKeepBothPersonalMemories()
    {
        var f=new ColonyJournalScenario();
        f.Resident.Social.GetOrCreate(f.Visitor.Id).Trust=.8f;
        var faction=f.Resident.Faction;
        Assert.That(f.Engine.ApplyManualCommand(new RequestTravelPartyCommand(f.Visitor.Id,f.Resident.Id)).Accepted,Is.True);
        f.CloseHour(); f.AssertPair("TravelPartyJoined",f.Visitor,f.Resident);
        Assert.That(f.Engine.ApplyManualCommand(new ReleaseTravelPartyCommand(f.Visitor.Id,f.Resident.Id)).Accepted,Is.True);
        f.CloseHour(); f.AssertPair("TravelPartyReleased",f.Visitor,f.Resident);
        Assert.That(f.Resident.Faction,Is.EqualTo(faction));
        Assert.That(f.Visitor.Journal.Entries,Has.Count.EqualTo(2));Assert.That(f.Resident.Journal.Entries,Has.Count.EqualTo(2));
    }
    [Test]
    public void QuickJoinAndReleaseKeepOnlyTheMoreMeaningfulMemoryOfThatHour()
    {
        var f=new ColonyJournalScenario();f.Resident.Social.GetOrCreate(f.Visitor.Id).Trust=.8f;
        Assert.That(f.Engine.ApplyManualCommand(new RequestTravelPartyCommand(f.Visitor.Id,f.Resident.Id)).Accepted,Is.True);
        Assert.That(f.Engine.ApplyManualCommand(new ReleaseTravelPartyCommand(f.Visitor.Id,f.Resident.Id)).Accepted,Is.True);
        f.CloseHour(); f.AssertPair("TravelPartyJoined",f.Visitor,f.Resident);
        Assert.That(f.Resident.Mind.TravelLeaderNpcId,Is.Null);
        Assert.That(f.Visitor.Journal.Entries,Has.Count.EqualTo(1));Assert.That(f.Resident.Journal.Entries,Has.Count.EqualTo(1));
    }
}
}
