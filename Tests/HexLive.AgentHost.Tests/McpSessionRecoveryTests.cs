using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class McpSessionRecoveryTests
{
    [Test]
    public async Task ExpiredClientNeverReplaysMutationOrReinitializesAndNewClientUsesFreshId()
    {
        using var transport = new Transport();
        using var client = new McpClient(Options(), transport);
        await client.CallToolAsync("world_status", new { }, default);
        transport.ExpireNext = true;
        Assert.ThrowsAsync<McpSessionExpiredException>(() =>
            client.CallToolAsync("move_to", new { npcId = 901 }, default));
        Assert.That(client.Healthy, Is.False);
        Assert.That(transport.Calls.Count(x => x.Method == "move_to"), Is.EqualTo(1));
        Assert.That(transport.Calls.Count(x => x.Method == "initialize"), Is.EqualTo(1),
            "The rejected mutating RPC must not cause any automatic retry/handshake.");
        Assert.ThrowsAsync<McpSessionExpiredException>(() => client.CallToolAsync("world_status", new { }, default));
        Assert.ThrowsAsync<McpSessionExpiredException>(() => client.ReadToolCatalogAsync(default));
        using var replacement = new McpClient(Options(), transport);
        await replacement.CallToolAsync("world_status", new { }, default);
        Assert.That(transport.Calls.Where(x => x.Method == "initialize").Select(x => x.Session),
            Is.EqualTo(new string?[] { null, null }));
        Assert.That(transport.Calls.Count(x => x.Method == "move_to"), Is.EqualTo(1));
        Assert.That(transport.Calls.Last().Session, Is.EqualTo("session-2"));
    }

    [Test]
    public async Task MutationQueuedBehindExpiredHeartbeatCannotSendOrInitializeOnOldClient()
    {
        using var transport = new Transport();
        using var client = new McpClient(Options(), transport);
        await client.CallToolAsync("world_status", new { }, default);
        transport.ExpireNext = true;
        transport.ExpiryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ReleaseExpiry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = client.CallToolAsync("agent_heartbeat", new { attachmentId = "old" }, default);
        await transport.ExpiryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queuedMutation = client.CallToolAsync("move_to", new { npcId = 901 }, default);
        transport.ReleaseExpiry.SetResult();
        Assert.ThrowsAsync<McpSessionExpiredException>(async () => await heartbeat.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.ThrowsAsync<McpSessionExpiredException>(async () => await queuedMutation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(transport.Calls.Count(x => x.Method == "initialize"), Is.EqualTo(1));
        Assert.That(transport.Calls.Count(x => x.Method == "move_to"), Is.Zero);
        using var replacement = new McpClient(Options(), transport);
        await replacement.CallToolAsync("world_status", new { }, default);
        await replacement.CallToolAsync("attach_agent", new { npcId = 901 }, default);
        await replacement.CallToolAsync("move_to", new { npcId = 901 }, default);
        Assert.That(transport.Calls.TakeLast(3).Select(x => x.Method),
            Is.EqualTo(new[] { "world_status", "attach_agent", "move_to" }));
        Assert.That(transport.Calls.TakeLast(3).Select(x => x.Session), Is.All.EqualTo("session-2"));
        Assert.That(transport.Calls.Count(x => x.Method == "move_to"), Is.EqualTo(1));
    }

    [Test]
    public void FreshInitializeUnauthorizedRemainsTerminalHttpError()
    {
        using var transport = new Transport { RejectFresh = true };
        using var client = new McpClient(Options(), transport);
        var error = Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CallToolAsync("world_status", new { }, default));
        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(transport.Calls.Select(x => x.Method), Is.EqualTo(new[] { "initialize" }));
    }

    [Test]
    public async Task ForbiddenEstablishedSessionIsNotTreatedAsSessionExpiry()
    {
        using var transport = new Transport();
        using var client = new McpClient(Options(), transport);
        await client.CallToolAsync("world_status", new { }, default);
        transport.ForbidNext = true;
        var error = Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CallToolAsync("move_to", new { npcId = 901 }, default));
        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(transport.Calls.Count(x => x.Method == "initialize"), Is.EqualTo(1));
    }

    internal static AgentProviderOptions Options() => new()
    {
        McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture-only",
        XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture",
        ElevenLabsVoiceId = "fixture",
    };

    private sealed class Transport : HttpMessageHandler
    {
        public bool ExpireNext, RejectFresh, ForbidNext;
        public TaskCompletionSource? ExpiryEntered, ReleaseExpiry;
        public List<(string Method, string? Session)> Calls { get; } = [];
        private int _sessions;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()!;
            var name = method == "tools/call" ? root.GetProperty("params").GetProperty("name").GetString()! : method;
            var session = request.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null;
            Calls.Add((name, session));
            if (RejectFresh && method == "initialize") return new(HttpStatusCode.Unauthorized);
            if (ExpireNext)
            {
                ExpireNext = false;
                ExpiryEntered?.TrySetResult();
                if (ReleaseExpiry != null) await ReleaseExpiry.Task.WaitAsync(token);
                return new(HttpStatusCode.Unauthorized);
            }
            if (ForbidNext) { ForbidNext = false; return new(HttpStatusCode.Forbidden); }
            if (method == "initialize") _sessions++;
            object result = method == "tools/call"
                ? new { isError = false, content = new[] { new { type = "text", text = "{}" } } }
                : new { protocolVersion = "2025-06-18" };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "session-" + _sessions);
            return response;
        }
    }
}
