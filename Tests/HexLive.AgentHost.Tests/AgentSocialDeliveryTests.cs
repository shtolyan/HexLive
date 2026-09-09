using System.Net;
using System.Reflection;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentSocialDeliveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task RestoredMemoryRejectsTheSameMessageUnderANewTurnBeforeAnotherSocialCommit()
    {
        var directory = Directory.CreateTempSubdirectory("agent-social-delivery-").FullName;
        try
        {
            var options = Options(directory);
            using var transport = new Transport();
            using var mcp = new McpClient(options.ProviderOptions, transport);
            using var providers = new Providers();
            var first = new AgentHostRuntime(options, providers, transport);
            await Turn(first, mcp, "turn-a");
            var restored = new AgentHostRuntime(options, providers, transport);
            await Turn(restored, mcp, "different-turn-same-message");
            Assert.That(providers.Decisions, Is.EqualTo(1));
            Assert.That(transport.Commits, Has.Count.EqualTo(1));
            var commit = transport.Commits.Single();
            Assert.That(commit.GetProperty("voiceTurn").GetBoolean(), Is.True);
            using var relation = JsonDocument.Parse(commit.GetProperty("relationView").GetString()!);
            Assert.That(relation.RootElement.GetProperty("reason").GetString(), Is.EqualTo("Новое сообщение принято без изменения отношений"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task LostCommitResponseRetriesTheOriginalTurnFromDurableOutboxWithoutAnotherModelDecision()
    {
        var directory = Directory.CreateTempSubdirectory("agent-social-delivery-").FullName;
        try
        {
            var options = Options(directory);
            using var transport = new Transport { LoseFirstCommitResponse = true };
            using var mcp = new McpClient(options.ProviderOptions, transport);
            using var providers = new Providers();
            var first = new AgentHostRuntime(options, providers, transport);
            await Turn(first, mcp, "durable-turn");
            var restored = new AgentHostRuntime(options, providers, transport);
            await (Task)typeof(AgentHostRuntime).GetMethod("FlushOutboxAsync", Private)!.Invoke(restored,
                new object[] { mcp, "new-attachment", 901, CancellationToken.None })!;
            Assert.That(providers.Decisions, Is.EqualTo(1));
            var voiceCommits = transport.Commits.Where(x => x.GetProperty("turnId").GetString() == "durable-turn").ToArray();
            Assert.That(voiceCommits, Has.Length.EqualTo(2));
            Assert.That(voiceCommits.All(x => x.GetProperty("voiceTurn").GetBoolean()), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Turn(AgentHostRuntime runtime, McpClient mcp, string id)
    {
        var task = (Task<bool>)typeof(AgentHostRuntime).GetMethod("ProcessSafelyAsync", Private)!.Invoke(runtime,
            new object?[] { mcp, "attachment", 901, "voice", "fixture", CancellationToken.None,
                CancellationToken.None, id, "0123456789abcdef0123456789abcdef", new[] { "message-a" } })!;
        Assert.That(await task, Is.True);
    }

    private static AgentHostOptions Options(string directory) => new()
    {
        McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture", ProfileId = "masha", DisplayName = "fixture",
        WorldId = "fixture", XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture",
        MemoryDirectory = Path.Combine(directory, "memory"), StateDirectory = Path.Combine(directory, "state"), FakeProviders = true,
    };

    private sealed class Providers : IAgentProviders
    {
        public int Decisions;
        public Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext, string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        {
            Decisions++;
            return Task.FromResult(new CompanionDecision { Reaction = "None", RelationshipAssessment =
                new(false, RelationshipDirection.Unchanged, RelationshipDirection.Unchanged, false, "Новое сообщение принято без изменения отношений") });
        }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken) => throw new AssertionException("No speech requested");
        public void Dispose() { }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public readonly List<JsonElement> Commits = new();
        public bool LoseFirstCommitResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var name = root.GetProperty("params").GetProperty("name").GetString();
                if (name == "commit_agent_turn")
                {
                    Commits.Add(root.GetProperty("params").GetProperty("arguments").Clone());
                    if (LoseFirstCommitResponse) { LoseFirstCommitResponse = false; throw new HttpRequestException("Fixture response lost"); }
                }
                object payload = name switch
                {
                    "world_status" => new { worldId = "fixture", tick = 100, seed = 12345, paused = false },
                    "describe_colonist" => new { stateSummary = "health=1; unconscious=false" },
                    _ => new { accepted = true },
                };
                result = new { isError = false, content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }
}
