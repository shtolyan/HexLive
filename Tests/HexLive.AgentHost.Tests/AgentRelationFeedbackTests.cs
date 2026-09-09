using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentRelationFeedbackTests
{
    [Test]
    public async Task ReasonAndActualClampedChangesPersistWithoutReplayingOrLeakingOtherSpeakers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "relation-feedback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MashaMemoryStore(directory);
            var world = await store.BindHexLiveWorldAsync(Json("{\"tick\":1200,\"seed\":7}"), 901, "", CancellationToken.None);
            await store.ImportHexLiveLegacyAsync(world, Json("{\"playerVoiceBond\":{\"familiarity\":0.99,\"trust\":0.99,\"affinity\":0.99}}"), CancellationToken.None);
            const string key = "fixture:player-a";
            await store.BindSpeakerAsync(key, true, CancellationToken.None);
            world = world with { SpeakerKey = key, MessageIds = new[] { "message-a" } };
            var decision = new CompanionDecision { Reaction = "Hostile", RelationshipAssessment =
                new(true, RelationshipDirection.Increase, RelationshipDirection.Increase, false, "Помогла разобраться в новом месте") };
            Assert.That(await store.CommitTurnAsync(world, "turn-a", "voice", decision, CancellationToken.None), Is.True);
            var loaded = new MashaMemoryStore(directory);
            Assert.That(await loaded.CommitTurnAsync(world, "turn-a", "voice", decision, CancellationToken.None), Is.False);
            Assert.That(await loaded.CommitTurnAsync(world, "different-turn-same-message", "voice", decision, CancellationToken.None), Is.False);
            var archive = await loaded.SnapshotAsync(CancellationToken.None);
            using var view = JsonDocument.Parse(AgentRelationView.Serialize(archive, key));
            foreach (var axis in new[] { "familiarity", "trust", "affinity" })
            {
                Assert.That(view.RootElement.GetProperty(axis).GetSingle(), Is.EqualTo(1f));
                Assert.That(view.RootElement.GetProperty(axis + "Delta").GetSingle(), Is.EqualTo(.01f).Within(.0001f), "Show actual clamped change, not the requested step or Hostile Social reaction.");
            }
            Assert.That(view.RootElement.GetProperty("reason").GetString(), Is.EqualTo(decision.RelationshipAssessment.Reason));
            Assert.That(AgentRelationView.Serialize(archive, "fixture:player-b"), Does.Not.Contain(decision.RelationshipAssessment.Reason));
            Assert.That(view.RootElement.TryGetProperty("memories", out _), Is.False);
            await loaded.CommitTurnAsync(world with { MessageIds = new[] { "message-b" } }, "heartbeat", "heartbeat", decision, CancellationToken.None);
            var afterHeartbeat = await loaded.SnapshotAsync(CancellationToken.None);
            Assert.That(AgentRelationView.Serialize(afterHeartbeat, key), Is.EqualTo(view.RootElement.GetRawText()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestCase(RelationshipDirection.Decrease, -.03f, -.02f)]
    [TestCase(RelationshipDirection.Unchanged, 0f, 0f)]
    public async Task NegativeAndUnchangedAssessmentsKeepTheirOwnReasonAndActualChanges(RelationshipDirection direction, float trust, float affinity)
    {
        var directory = Path.Combine(Path.GetTempPath(), "relation-feedback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MashaMemoryStore(directory);
            var world = await store.BindHexLiveWorldAsync(Json("{\"tick\":1200,\"seed\":7}"), 901, "", CancellationToken.None);
            await store.ImportHexLiveLegacyAsync(world, Json("{\"playerVoiceBond\":{\"familiarity\":0.5,\"trust\":0.5,\"affinity\":0.5}}"), CancellationToken.None);
            const string key = "fixture:player-a";
            await store.BindSpeakerAsync(key, true, CancellationToken.None);
            world = world with { SpeakerKey = key, MessageIds = new[] { "new-message" } };
            await store.CommitTurnAsync(world, "assessment", "voice", new CompanionDecision { Reaction = "Warm", RelationshipAssessment =
                new(false, direction, direction, false, "Оценка нового сообщения") }, CancellationToken.None);
            var archive = await new MashaMemoryStore(directory).SnapshotAsync(CancellationToken.None);
            using var view = JsonDocument.Parse(AgentRelationView.Serialize(archive, key));
            Assert.That(view.RootElement.GetProperty("hasChange").GetBoolean(), Is.True);
            Assert.That(view.RootElement.GetProperty("trustDelta").GetSingle(), Is.EqualTo(trust).Within(.0001f));
            Assert.That(view.RootElement.GetProperty("affinityDelta").GetSingle(), Is.EqualTo(affinity).Within(.0001f));
            Assert.That(view.RootElement.GetProperty("familiarityDelta").GetSingle(), Is.Zero);
            Assert.That(view.RootElement.GetProperty("reason").GetString(), Is.EqualTo("Оценка нового сообщения"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestCase("heartbeat")]
    [TestCase("critical")]
    public void NonVoiceProviderDecisionCannotChangePhysicalSocial(string trigger)
    {
        var decision = AgentProviders.ParseDecision("""
            {"speech":"","emotion":"neutral","action":null,"reaction":"Warm",
             "relationshipAssessment":null,"intentSummary":"","memoryUpserts":[],"journalText":""}
            """, trigger);
        Assert.That(decision.Reaction, Is.EqualTo("None"));
    }

    [TestCase('"')]
    [TestCase('\\')]
    [TestCase('\u2028')]
    [TestCase('\u2029')]
    public void LongReasonAndEscapedNamesFitTheSeparateWireBudget(char character)
    {
        Assert.That(AgentRelationView.MaxCharacters, Is.EqualTo(AgentWire.MaxRelationCharacters));
        var archive = new MashaArchive();
        var key = "fixture:" + new string('a', 192);
        archive.Speakers[key] = new SpeakerMemory { VoiceName = new string(character, 48), LastAssessmentReason = new string(character, 240) };
        var view = AgentRelationView.Serialize(archive, key);
        Assert.That(view.Length, Is.GreaterThan(AgentWire.MaxTextCharacters));
        Assert.That(view.Length, Is.LessThanOrEqualTo(AgentWire.MaxRelationCharacters));
        using var parsed = JsonDocument.Parse(view);
        Assert.That(parsed.RootElement.GetProperty("reason").GetString(), Is.Not.Empty);
        Assert.That(archive.Speakers[key].LastAssessmentReason, Has.Length.EqualTo(240), "Only the public explanation can be shortened; full local reason stays intact.");
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
