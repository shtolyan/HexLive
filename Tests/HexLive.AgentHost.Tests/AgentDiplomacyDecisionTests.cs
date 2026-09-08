using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentDiplomacyDecisionTests
{
    [Test]
    public void AgentMayOfferOrdinaryMergeButNotForceFactionChange()
    {
        Assert.That(AgentProviders.IsAllowedTool("merge_camps"), Is.True);
        Assert.That(AgentProviders.IsAllowedTool("set_faction"), Is.False);
        var decision = AgentProviders.ParseDecision("""
            {"speech":"","emotion":"warm","reaction":"None","relationshipAssessment":null,
             "intentSummary":"Предложу общий дом.","memoryUpserts":[],"journalText":"",
             "action":{"tool":"merge_camps","arguments":{"targetNpcId":31,"useTargetCamp":false}}}
            """, "heartbeat");
        Assert.That(decision.Action!.Tool, Is.EqualTo("merge_camps"));
        Assert.That(decision.Action.Arguments.GetProperty("useTargetCamp").GetBoolean(), Is.False);
    }
}
