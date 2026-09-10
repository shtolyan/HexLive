using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentRuntimeReconnectTests
{
    [TestCase(Failure.Timeout)]
    [TestCase(Failure.ExpiredSession)]
    public async Task LostHeartbeatReattachesSameNpcAndWorldWithoutPaidWork(Failure failure)
    {
        await using var fixture = new Fixture(failure);
        await Until(() => fixture.Transport.Attachments.Count >= 2);
        Assert.That(fixture.Transport.Attachments, Is.All.EqualTo(901));
        Assert.That(fixture.Transport.Initializations, Is.EqualTo(2));
        Assert.That(fixture.Runtime.TerminalErrorCode, Is.Null);
        Assert.That(fixture.Providers.Decisions, Is.Zero);
        Assert.That(fixture.Transport.Actions, Is.Zero);
    }

    [Test]
    public async Task RevokedCredentialFailsFreshHandshakeAndStopsInsteadOfRetryingForever()
    {
        await using var fixture = new Fixture(Failure.RevokedCredential);
        await fixture.Running.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.That(fixture.Runtime.TerminalErrorCode, Is.EqualTo(nameof(HttpRequestException)));
        Assert.That(fixture.Transport.Initializations, Is.EqualTo(2));
        Assert.That(fixture.Transport.Attachments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task StopDuringRetryDelayCancelsPromptlyWithoutNewAttachment()
    {
        await using var fixture = new Fixture(Failure.Timeout);
        await Until(() => fixture.Runtime.CurrentPhase == "Reconnecting", 5);
        var elapsed = Stopwatch.StartNew();
        fixture.Stop.Cancel();
        await fixture.Running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
        Assert.That(fixture.Transport.Initializations, Is.EqualTo(1));
        Assert.That(fixture.Transport.Attachments, Has.Count.EqualTo(1));
        Assert.That(fixture.Runtime.TerminalErrorCode, Is.Null);
    }

    [Test]
    public async Task StopCancelsInflightHeartbeatAndDoesNotTreatItAsRecoverableTimeout()
    {
        await using var fixture = new Fixture(Failure.WaitForStop);
        await fixture.Transport.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Stop.Cancel();
        await fixture.Running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(fixture.Transport.Initializations, Is.EqualTo(1));
        Assert.That(fixture.Transport.Attachments, Has.Count.EqualTo(1));
        Assert.That(fixture.Runtime.TerminalErrorCode, Is.Null);
    }

    [Test]
    public async Task ExpiryCannotReattachIntoChangedWorld()
    {
        await using var fixture = new Fixture(Failure.ChangedWorld);
        await fixture.Running.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.That(fixture.Runtime.TerminalErrorCode, Is.EqualTo(nameof(AgentTargetChangedException)));
        Assert.That(fixture.Transport.Attachments, Has.Count.EqualTo(1));
        Assert.That(fixture.Providers.Decisions, Is.Zero);
    }

    private static async Task Until(Func<bool> condition, int seconds = 12)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(25, deadline.Token);
    }

    public enum Failure { Timeout, ExpiredSession, RevokedCredential, WaitForStop, ChangedWorld }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("bug391-runtime-").FullName;
        public readonly CancellationTokenSource Stop = new();
        public readonly Transport Transport;
        public readonly Providers Providers = new();
        public readonly AgentHostRuntime Runtime;
        public readonly Task Running;
        public Fixture(Failure failure)
        {
            Transport = new(failure);
            var options = new AgentHostOptions
            {
                McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture-only", ProfileId = "fixture",
                DisplayName = "fixture", WorldId = "fixture", ExpectedWorldId = "fixture", NpcId = 901,
                XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture",
                ElevenLabsVoiceId = "fixture", FakeProviders = true,
                MemoryDirectory = Path.Combine(_path, "memory"), StateDirectory = Path.Combine(_path, "state"),
            };
            Runtime = new AgentHostRuntime(options, Providers, Transport);
            Running = Runtime.RunAsync(Stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            await Running.WaitAsync(TimeSpan.FromSeconds(3));
            Transport.Dispose(); Stop.Dispose();
            Directory.Delete(_path, true);
        }
    }

    private sealed class Providers : IAgentProviders
    {
        public int Decisions;
        public Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
            string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Decisions); return Task.FromResult(new CompanionDecision()); }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
            throw new AssertionException("Paused fixture must never synthesize");
        public void Dispose() { }
    }

    private sealed class Transport(Failure failure) : HttpMessageHandler
    {
        private bool _published, _failed;
        public int Initializations, Actions;
        public ConcurrentQueue<int> Attachments { get; } = new();
        public TaskCompletionSource HeartbeatEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "initialize")
            {
                Interlocked.Increment(ref Initializations);
                if (_failed && failure == Failure.RevokedCredential) return new(HttpStatusCode.Unauthorized);
            }
            object result = new { protocolVersion = "2025-06-18" };
            if (method == "tools/list") result = new { tools = Array.Empty<object>() };
            if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString();
                if (name == "world_status" && _published && !_failed)
                {
                    _failed = true;
                    HeartbeatEntered.TrySetResult();
                    if (failure == Failure.WaitForStop) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    if (failure == Failure.Timeout) throw new TaskCanceledException("Synthetic HttpClient timeout; session token remains live");
                    return new(HttpStatusCode.Unauthorized);
                }
                if (name == "attach_agent") Attachments.Enqueue(parameters.GetProperty("arguments").GetProperty("npcId").GetInt32());
                if (name == "publish_agent_phase") _published = true;
                if (name is "move_to" or "interact" or "acquire_control") Interlocked.Increment(ref Actions);
                object payload = name switch
                {
                    "world_status" => new { worldId = _failed && failure == Failure.ChangedWorld ? "different" : "fixture", tick = 100, seed = 12345, paused = true },
                    "list_colonists" => new { colonists = new[] { new { npcId = 901, health = 1f } } },
                    "attach_agent" => new { attachmentId = "attach-" + Attachments.Count, playerPresent = false, eventWatermark = 0L },
                    "describe_colonist" => new { stateSummary = "health=1; unconscious=false" },
                    _ => new { accepted = true },
                };
                result = new { isError = false, content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "session-" + Initializations);
            return response;
        }
    }
}
