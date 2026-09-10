using System.Net;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentPerceptionConsumptionTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Test]
    public async Task FailedModelRetriesSightingsAndSuccessfulTurnOnlyConsumesItsReadWatermark()
    {
        using var fixture = new Fixture();
        fixture.Providers.Fail = true;
        Assert.That(await fixture.Turn(), Is.False);
        fixture.Providers.Fail = false;
        fixture.Providers.DuringDecision = () => fixture.Transport.Watermark = 11;
        Assert.That(await fixture.Turn(), Is.True);
        fixture.Providers.DuringDecision = null;
        Assert.That(await fixture.Turn(), Is.True);
        Assert.That(fixture.Transport.Cursors, Is.EqualTo(new long[] { 0, 0, 10 }));
        Assert.That(fixture.Providers.Snapshots.Select(s => s.GetProperty("recentPerception").GetProperty("watermark").GetInt64()),
            Is.EqualTo(new long[] { 10, 10, 11 }));
        Assert.That(fixture.Providers.Snapshots[0].GetProperty("recentPerception").GetProperty("observations")[0]
            .GetProperty("definitionId").GetString(), Is.EqualTo("clothing.passed"));
    }

    [Test]
    public async Task CancelledOrActionSuppressedModelTurnDoesNotConsumeSightings()
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        fixture.Providers.DuringDecision = () => { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); };
        Assert.ThrowsAsync<OperationCanceledException>(async () => await fixture.Turn(stop.Token));
        var action = new TaskCompletionSource<bool>();
        typeof(AgentHostRuntime).GetField("_actionTask", Private)!.SetValue(fixture.Runtime, action.Task);
        Assert.That(await fixture.Turn(), Is.True);
        Assert.That(fixture.Transport.Cursors, Is.EqualTo(new long[] { 0 }), "Suppressed heartbeat must not read/ack perception.");
        action.SetResult(true);
        fixture.Providers.DuringDecision = null;
        Assert.That(await fixture.Turn(), Is.True);
        Assert.That(fixture.Transport.Cursors, Is.EqualTo(new long[] { 0, 0 }));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("perception-turn-").FullName;
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
                new object?[] { _mcp, "attachment", 901, "heartbeat", "", cancellation,
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
