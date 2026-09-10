using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentReferenceReaderTests
{
    [Test]
    public async Task SpecPagesRetainOffsetsAndContentIdentity()
    {
        using var handler = new SpecTransport();
        using var client = new McpClient(Options(), handler);
        var reader = new AgentReferenceReader(client);
        var first = await reader.ReadAsync("spec.read", JsonSerializer.SerializeToElement(new { section = "120", offset = 0 }), default);
        var second = await reader.ReadAsync("spec.read", JsonSerializer.SerializeToElement(new { section = "120", offset = first.NextOffset }), default);
        using var a = JsonDocument.Parse(first.Text); using var b = JsonDocument.Parse(second.Text);
        Assert.That(a.RootElement.GetProperty("text").GetString(), Has.Length.EqualTo(5000));
        Assert.That(b.RootElement.GetProperty("text").GetString(), Has.Length.EqualTo(1000));
        Assert.That(second.NextOffset, Is.Null);
        Assert.That(second.SourceIds, Is.Not.EqualTo(first.SourceIds));
        var again = await reader.ReadAsync("spec.read", JsonSerializer.SerializeToElement(new { section = "120", offset = 0 }), default);
        Assert.That(again.SourceIds, Is.EqualTo(first.SourceIds));
    }

    [Test]
    public async Task SkillAndSpecAreAvailableToFinalDecisionWithOnlyReadSourceIds()
    {
        var root = Directory.CreateTempSubdirectory("reference-recall-").FullName;
        try
        {
            using var handler = new SpecTransport();
            using var client = new McpClient(Options(), handler);
            var reader = new AgentReferenceReader(client); var calls = 0;
            var result = await new AgentMemoryRecall(root).DecideAsync("", new("world", "world", 0, 50), new(), (context, token) =>
            {
                if (++calls == 1) return Task.FromResult(new CompanionDecision { MemoryRequests =
                    [new() { Operation = "skills.read", Arguments = JsonSerializer.SerializeToElement(new { id = "build-bed" }) },
                     new() { Operation = "spec.read", Arguments = JsonSerializer.SerializeToElement(new { section = "120" }) }] });
                Assert.That(context, Does.Contain("skill:build-bed:"));
                Assert.That(context, Does.Contain("spec:120:0:"));
                return Task.FromResult(new CompanionDecision { MemorySources = ["invented"], Speech = "Ready" });
            }, default, reader.ReadAsync);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(result.MemorySources, Is.Empty);
            var trace = File.ReadAllText(Path.Combine(root, ".state/last-memory-search.json"));
            Assert.That(trace, Does.Contain("skills.read").And.Contain("spec.read"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void ReferencesCannotInvokeMutatingTools()
    {
        Assert.That(AgentReferenceReader.Allowed("execute_agent_command"), Is.False);
        Assert.That(AgentReferenceReader.Allowed("manage_inventory"), Is.False);
        Assert.That(AgentReferenceReader.Allowed("recipes.read"), Is.True);
    }

    [Test]
    public void GiftUsesTheAdvertisedTransferContractAndAttachmentActor()
    {
        var catalog = JsonSerializer.SerializeToElement(new { tools = HexLive.Server.Mcp.McpTools.Catalog.Select(t => new { name = t.Name, inputSchema = t.InputSchema }) });
        var bound = new AgentActionContract(catalog).BindAndValidate(new CompanionAction
        {
            Tool = "transfer_inventory", Arguments = JsonSerializer.SerializeToElement(new { npcId = 999,
                otherNpcId = 902, source = "Carried", index = 0, expectedDefinitionId = "food.coconut", direction = "Give" })
        }, 901);
        Assert.That(bound["npcId"].GetInt32(), Is.EqualTo(901));
        Assert.That(bound["direction"].GetString(), Is.EqualTo("Give"));
    }

    private static AgentProviderOptions Options() => new() { McpUri = new("http://fixture/mcp"), McpToken = "fixture", XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture" };

    private sealed class SpecTransport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = doc.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var parameters = root.GetProperty("params");
                Assert.That(parameters.GetProperty("name").GetString(), Is.EqualTo("read_spec"));
                var args = parameters.GetProperty("arguments"); var offset = args.GetProperty("offset").GetInt32();
                var text = JsonSerializer.Serialize(new { section = "120", offset, text = new string('x', Math.Max(0, 6000 - offset)), totalChars = 6000 });
                result = new { content = new[] { new { type = "text", text } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture"); return response;
        }
    }
}
