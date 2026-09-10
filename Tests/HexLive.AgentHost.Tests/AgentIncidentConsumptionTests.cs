using System.Net;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentIncidentConsumptionTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static AgentIncidentBuffer Buffer(Fixture fixture) =>
        (AgentIncidentBuffer)typeof(AgentHostRuntime).GetField("_incidents", Private)!.GetValue(fixture.Runtime)!;

    private static JsonElement Incident(long seq) => JsonSerializer.SerializeToElement(new
    {
        events = new[] { new { seq, type = "AgentObservedTheft", message = "Actor=NPC7 Item=food.coconut" } },
    });

    [Test]
    public async Task ActualProviderReceivesIncidentAgainAfterFailureAndOutboxCommitConsumesIt()
    {
        using var fixture = new Fixture();
        var buffer = Buffer(fixture);
        buffer.Observe(Incident(10));
        fixture.Providers.Fail = true;
        Assert.That(await fixture.Turn(), Is.False);
        Assert.That(buffer.HasPending, Is.True);
        fixture.Providers.Fail = false;
        Assert.That(await fixture.Turn(), Is.True);
        Assert.That(buffer.HasPending, Is.False);
        foreach (var snapshot in fixture.Providers.Snapshots)
            Assert.That(snapshot.GetProperty("observedIncidents").GetProperty("events")[0]
                .GetProperty("message").GetString(), Does.Contain("Actor=NPC7"));
    }

    [Test]
    public async Task IncidentObservedDuringProviderRequestSurvivesItsCommit()
    {
        using var fixture = new Fixture();
        var buffer = Buffer(fixture);
        buffer.Observe(Incident(10));
        fixture.Providers.DuringDecision = () => buffer.Observe(Incident(11));
        Assert.That(await fixture.Turn(), Is.True);
        var pending = buffer.Snapshot().GetProperty("events");
        Assert.That(pending.GetArrayLength(), Is.EqualTo(1));
        Assert.That(pending[0].GetProperty("seq").GetInt64(), Is.EqualTo(11));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("incident-turn-").FullName;
        public readonly Transport Transport = new();
        public readonly Providers Providers = new();
        public readonly AgentHostRuntime Runtime;
        private readonly McpClient _mcp;
        public Fixture()
        {
            var options = new AgentHostOptions
            {
                McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture", ProfileId = "masha",
                DisplayName = "fixture", WorldId = "fixture", XaiKey = "", ElevenLabsKey = "",
                XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture",
                MemoryDirectory = Path.Combine(_path, "memory"), StateDirectory = Path.Combine(_path, "state"),
                FakeProviders = true,
            };
            Runtime = new AgentHostRuntime(options, Providers, Transport);
            _mcp = new McpClient(options.ProviderOptions, Transport);
        }
        public Task<bool> Turn(CancellationToken cancellation = default) =>
            (Task<bool>)typeof(AgentHostRuntime).GetMethod("ProcessSafelyAsync", Private)!.Invoke(Runtime,
                new object?[] { _mcp, "attachment", 901, "critical", "", cancellation,
                    CancellationToken.None, null, "", Array.Empty<string>() })!;
        public void Dispose()
        {
            _mcp.Dispose();
            Transport.Dispose();
            Directory.Delete(_path, true);
        }
    }

    private sealed class Providers : IAgentProviders
    {
        public bool Fail;
        public Action? DuringDecision;
        public readonly List<JsonElement> Snapshots = new();
        public Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
            string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        {
            Snapshots.Add(JsonSerializer.Deserialize<JsonElement>(stateJson));
            DuringDecision?.Invoke();
            if (Fail) throw new InvalidDataException("fixture");
            return Task.FromResult(new CompanionDecision());
        }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
            throw new AssertionException("Silent fixture");
        public void Dispose() { }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public long Watermark = 10;
        public readonly List<long> Cursors = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = doc.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString();
                if (name == "describe_colonist") Cursors.Add(parameters.GetProperty("arguments").GetProperty("perceptionSince").GetInt64());
                object payload = name switch
                {
                    "world_status" => new { worldId = "fixture", tick = 100, seed = 12345, paused = false },
                    "describe_colonist" => new { stateSummary = "health=1; unconscious=false",
                        recentPerception = new { epoch = "fixture", watermark = Watermark,
                            observations = new[] { new { definitionId = "clothing.passed", lastSeenTick = 10 } } } },
                    _ => new { accepted = true },
                };
                result = new { isError = false, content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }
}
