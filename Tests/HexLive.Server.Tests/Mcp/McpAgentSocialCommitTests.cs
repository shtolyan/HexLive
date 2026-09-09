using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpAgentSocialCommitTests
{
    [Test]
    public void CompletedNoneCannotBecomeWarmOnRetryOrAfterReconnectAndSaveLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-social-" + Guid.NewGuid().ToString("N") + ".sav");
        try
        {
            int id;
            using (var host = CreateHost(path))
            {
                id = host.Read(w => { var npc = w.Entities.Npcs.Values.First(); npc.Needs.Social = .4f; npc.Companion.PlayerVoiceBond.Trust = .31f; npc.Companion.LastIntentSummary = "private sentinel"; return npc.Id.Value; });
                var (tools, attachment) = Attach(host, id);
                Commit(tools, attachment, "same-turn", "None");
                Commit(tools, attachment, "same-turn", "Warm");
                Assert.That(Social(host, id), Is.EqualTo(.4f).Within(.0001f), "Completed None must be a persisted no-op, not permission to replay with another reaction.");
                host.Save();
            }
            using var restored = CreateHost(path);
            var reconnected = Attach(restored, id);
            Commit(reconnected.Tools, reconnected.Attachment, "same-turn", "Hostile");
            Assert.That(Social(restored, id), Is.EqualTo(.4f).Within(.0001f));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestCase("Warm", .18f)]
    [TestCase("Neutral", .08f)]
    [TestCase("Tense", -.06f)]
    [TestCase("Hostile", -.12f)]
    public void CompletedVoiceChangesOnlyPhysicalSocialOnceAcrossRetryAndSaveReload(string reaction, float delta)
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-social-" + Guid.NewGuid().ToString("N") + ".sav");
        try
        {
            int id;
            using (var host = CreateHost(path))
            {
                id = host.Read(w => { var npc = w.Entities.Npcs.Values.First(); npc.Needs.Social = .4f; npc.Companion.PlayerVoiceBond.Trust = .31f; npc.Companion.LastIntentSummary = "private sentinel"; return npc.Id.Value; });
                var (tools, attachment) = Attach(host, id);
                Commit(tools, attachment, "voice-turn", reaction);
                Commit(tools, attachment, "voice-turn", reaction);
                Assert.That(Social(host, id), Is.EqualTo(.4f + delta).Within(.0001f));
                host.Read(w =>
                {
                    var npc = w.Entities.Npcs[new EntityId(id)];
                    Assert.That(npc.AppliedAgentTurnIds.Count(x => x == "voice-turn"), Is.EqualTo(1));
                    Assert.That(npc.Companion.PlayerVoiceBond.Trust, Is.EqualTo(.31f), "Generic Social must not change legacy bond.");
                    Assert.That(npc.Companion.LastIntentSummary, Is.EqualTo("private sentinel"));
                    return true;
                });
                host.Save();
            }
            using var restored = CreateHost(path);
            var reconnected = Attach(restored, id);
            Commit(reconnected.Tools, reconnected.Attachment, "voice-turn", reaction);
            Assert.That(Social(restored, id), Is.EqualTo(.4f + delta).Within(.0001f));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void AutonomousNoneTurnsDoNotEvictTheBoundedVoiceReplayWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-social-" + Guid.NewGuid().ToString("N") + ".sav");
        using var host = CreateHost(path);
        var id = host.Read(w => { var npc = w.Entities.Npcs.Values.First(); npc.Needs.Social = .4f; return npc.Id.Value; });
        var (tools, attachment) = Attach(host, id);
        Commit(tools, attachment, "voice", "Warm");
        for (var i = 0; i < 80; i++) Commit(tools, attachment, "heartbeat-" + i, "None", voiceTurn: false);
        Commit(tools, attachment, "voice", "Warm");
        Assert.That(Social(host, id), Is.EqualTo(.58f).Within(.0001f));
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(id)].AppliedAgentTurnIds.Count), Is.EqualTo(1));
    }

    [Test]
    public void RelationFrameSupportsItsSeparateBudgetAndKeepsOrdinaryTextBounded()
    {
        var view = new string('я', AgentWire.MaxRelationCharacters);
        var frame = AgentWire.AgentState(new AgentStateFrame { RelationView = view });
        var decoded = AgentWire.ReadAgentState(frame.Skip(1).ToArray());
        Assert.That(decoded.RelationView, Is.EqualTo(view));
        Assert.Throws<InvalidDataException>(() => AgentWire.AgentState(new AgentStateFrame { RelationView = view + "я" }));
        Assert.Throws<InvalidDataException>(() => AgentWire.AgentState(new AgentStateFrame { IntentSummary = new string('я', AgentWire.MaxTextCharacters + 1) }));
    }

    [Test]
    public void LongPublicRelationViewCommitsAndOversizeFailsBeforeSocial()
    {
        using var host = CreateHost(Path.Combine(Path.GetTempPath(), "agent-social-" + Guid.NewGuid().ToString("N") + ".sav"));
        var id = host.Read(w => { var npc = w.Entities.Npcs.Values.First(); npc.Needs.Social = .4f; return npc.Id.Value; });
        var (tools, attachment) = Attach(host, id);
        var relation = "{\"reason\":\"" + new string('я', 240) + "\"}";
        var result = tools.Call("commit_agent_turn", JsonSerializer.SerializeToElement(new { attachmentId = attachment, turnId = "relation", reaction = "None", intentSummary = "", relationView = relation }), "mcp:social", out var error);
        Assert.That(error, Is.False, result);
        result = tools.Call("commit_agent_turn", JsonSerializer.SerializeToElement(new { attachmentId = attachment, turnId = "oversize", reaction = "Warm", intentSummary = "", relationView = new string('я', AgentWire.MaxRelationCharacters + 1) }), "mcp:social", out error);
        Assert.That(error, Is.True, result);
        Assert.That(Social(host, id), Is.EqualTo(.4f));
    }

    private static float Social(WorldHost host, int id) => host.Read(w => w.Entities.Npcs[new EntityId(id)].Needs.Social);

    private static (McpTools Tools, string Attachment) Attach(WorldHost host, int id)
    {
        var registry = new AgentSessionRegistry();
        Assert.That(registry.TryAttach(id, "mcp:social", 1, "Agent", AgentCapabilities.RelationView, 45, out var attachment, out var reason), Is.True, reason);
        return (new McpTools(() => host, () => 1, new ControlLeases(45), registry), attachment.AttachmentId);
    }

    private static void Commit(McpTools tools, string attachment, string turn, string reaction, bool voiceTurn = true)
    {
        var result = tools.Call("commit_agent_turn", JsonSerializer.SerializeToElement(new { attachmentId = attachment, turnId = turn, reaction, voiceTurn, intentSummary = "" }), "mcp:social", out var error);
        Assert.That(error, Is.False, result);
    }

    private static WorldHost CreateHost(string path)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SimData/simdata.json"))) root = root.Parent;
        return new WorldHost(12345, GameMode.Feud, path, Path.Combine(root!.FullName, "SimData/simdata.json"), false);
    }
}
