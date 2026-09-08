using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class RelationshipTests
{
    private static VoiceRelationship Create() => new(new(.5f, .5f, 0, "Голос", null));
    [Test]
    public void NeutralCanLeaveAllThreeMetricsUnchanged()
    {
        var relationship = Create();
        var before = relationship.Snapshot;
        Assert.That(relationship.Apply(["one"], new(false, RelationshipDirection.Unchanged,
            RelationshipDirection.Unchanged, false, "Обычный вопрос"), DateTimeOffset.UtcNow), Is.True);
        Assert.That(relationship.Snapshot.Familiarity, Is.EqualTo(before.Familiarity));
        Assert.That(relationship.Snapshot.Trust, Is.EqualTo(before.Trust));
        Assert.That(relationship.Snapshot.Sympathy, Is.EqualTo(before.Sympathy));
        Assert.That(relationship.Snapshot.LastContactUtc, Is.Not.Null);
    }
    [Test]
    public void DislikeCanCrossZeroAndRecoverWithoutForgettingThePerson()
    {
        var relationship = Create();
        relationship.Apply(["one"], new(false, RelationshipDirection.Decrease,
            RelationshipDirection.Decrease, false, "Давление"), DateTimeOffset.UtcNow);
        Assert.That(relationship.Snapshot.Sympathy, Is.EqualTo(-.02f).Within(.00001));
        Assert.That(relationship.Snapshot.Familiarity, Is.EqualTo(.5f));
        relationship.Apply(["two"], new(true, RelationshipDirection.Increase,
            RelationshipDirection.Increase, false, "Извинение и новый факт"), DateTimeOffset.UtcNow);
        Assert.That(relationship.Snapshot.Sympathy, Is.EqualTo(0).Within(.00001));
        Assert.That(relationship.Snapshot.Familiarity, Is.EqualTo(.52f).Within(.00001));
    }
    [Test]
    public void ReplayAfterRestoreDoesNotChangeMetricsOrName()
    {
        var relationship = Create();
        var assessment = new RelationshipAssessment(true, RelationshipDirection.Increase,
            RelationshipDirection.Increase, false, "Помощь", "Советчик", "Помог советом");
        relationship.Apply(["one", "two"], assessment, DateTimeOffset.UtcNow);
        var restored = VoiceRelationship.Restore(relationship.ExportState());
        var before = restored.Snapshot;
        Assert.That(restored.Apply(["two", "three"], assessment, DateTimeOffset.UtcNow), Is.False);
        Assert.That(restored.Snapshot, Is.EqualTo(before));
        Assert.That(restored.BuildPromptBlock(), Does.Contain("Familiarity"));
    }
    [Test]
    public void SevereHarmUsesBoundedNegativeSteps()
    {
        var relationship = Create();
        relationship.Apply(["one"], new(false, RelationshipDirection.Decrease,
            RelationshipDirection.Decrease, true, "Угроза"), DateTimeOffset.UtcNow);
        Assert.That(relationship.Snapshot.Trust, Is.EqualTo(.44f).Within(.00001));
        Assert.That(relationship.Snapshot.Sympathy, Is.EqualTo(-.04f).Within(.00001));
    }
    [Test]
    public void PositiveLegacyValuesAreNotRescaled()
    {
        var relationship = new VoiceRelationship(new(.2f, .7f, .8f, "Старое прозвище", null));
        Assert.That(relationship.Snapshot.Sympathy, Is.EqualTo(.8f));
        Assert.That(relationship.Snapshot.VoiceName, Is.EqualTo("Старое прозвище"));
    }
}
