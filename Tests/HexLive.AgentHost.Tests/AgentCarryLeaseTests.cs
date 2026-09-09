using System.Net;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentCarryLeaseTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(false)]
    [TestCase(true)]
    public async Task PickupKeepsLeaseUntilExplicitPutDownOrCancellation(bool cancel)
    {
        var dir = Directory.CreateTempSubdirectory("carry-lease-test-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var handler = new CarryTransport();
        var options = new AgentHostOptions
        {
            McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture",
            ProfileId = "masha", DisplayName = "fixture", WorldId = "fixture",
            XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture",
            ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture",
            MemoryDirectory = Path.Combine(dir, "memory"), StateDirectory = Path.Combine(dir, "state"),
            FakeProviders = true,
        };
        var runtime = new AgentHostRuntime(options);
        using var mcp = new McpClient(options.ProviderOptions, handler);
        Task Start(string tool)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var args = JsonDocument.Parse(tool switch { "carry_person" => "{\"targetNpcId\":902}", "move_to" => "{\"x\":10,\"y\":10}", _ => "{}" });
            var action = new CompanionAction { Tool = tool, Arguments = args.RootElement.Clone() };
            var task = (Task)typeof(AgentHostRuntime).GetMethod("PerformActionSafelyAsync", Private)!
                .Invoke(runtime, new object[] { mcp, 901, action, cts.Token, "carry-turn" })!;
            typeof(AgentHostRuntime).GetField("_actionStop", Private)!.SetValue(runtime, cts);
            typeof(AgentHostRuntime).GetField("_actionTask", Private)!.SetValue(runtime, task);
            return task;
        }
        Task Stop(bool handoff) => (Task)typeof(AgentHostRuntime).GetMethod("StopActionAsync", Private)!
            .Invoke(runtime, new object[] { handoff })!;
        try
        {
            var pickup = Start("carry_person");
            await Task.Delay(2400, timeout.Token);
            Assert.Multiple(() =>
            {
                Assert.That(handler.Carried, Is.True);
                Assert.That(handler.Releases, Is.Zero, "Completed pickup must not return to AI and drop the patient.");
                Assert.That(pickup.IsCompleted, Is.False);
            });
            if (cancel)
            {
                await Stop(false);
                Assert.That(handler.Carried, Is.False);
                Assert.That(handler.Releases, Is.EqualTo(1));
                return;
            }
            await Stop(true);
            _ = Start("move_to");
            await Task.Delay(2400, timeout.Token);
            Assert.That(handler.Carried, Is.True, "Moving to the chosen home must preserve the patient.");
            Assert.That(handler.Releases, Is.Zero);
            await Stop(true);
            await Start("put_down_person");
            Assert.That(handler.Carried, Is.False);
            Assert.That(handler.Releases, Is.EqualTo(1), "Only completed put-down releases the transport lease.");
        }
        finally
        {
            await Stop(false);
            Directory.Delete(dir, true);
        }
    }

    private sealed class CarryTransport : HttpMessageHandler
    {
        public bool Carried;
        public int Releases;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/list")
                result = new { tools = HexLive.Server.Mcp.McpTools.Catalog.Select(t => new
                    { name = t.Name, inputSchema = t.InputSchema }) };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var name = root.GetProperty("params").GetProperty("name").GetString();
                if (name == "carry_person") Carried = true;
                if (name == "put_down_person") Carried = false;
                if (name == "release_control") { Releases++; Carried = false; }
                var text = name == "list_colonists"
                    ? JsonSerializer.Serialize(new { colonists = new[] { new { npcId = 901,
                        planStatus = "Completed", carriedNpcId = Carried ? (int?)31 : null } } })
                    : "{\"accepted\":true}";
                result = new { isError = false, content = new[] { new { type = "text", text } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }
}
