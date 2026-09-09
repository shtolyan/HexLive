using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentActionContractTests
{
    private static AgentActionContract Contract => new(JsonSerializer.Deserialize<JsonElement>("""
        {"tools":[{"name":"self_action","inputSchema":{"type":"object","additionalProperties":false,
        "properties":{"npcId":{"type":"integer"},"kind":{"type":"string","enum":["TreatSelf","GroundSit"]},
        "count":{"type":"integer"},"run":{"type":"boolean"}},"required":["npcId","kind"]}}]}
        """));

    [TestCase("{}", "MissingRequiredArgument")]
    [TestCase("{\"kind\":null}", "InvalidArgumentType")]
    [TestCase("{\"kind\":\"private-secret\"}", "InvalidArgumentEnum")]
    [TestCase("{\"kind\":\"TreatSelf\",\"count\":1.5}", "InvalidArgumentType")]
    [TestCase("{\"kind\":\"TreatSelf\",\"run\":\"true\"}", "InvalidArgumentType")]
    [TestCase("{\"kind\":\"TreatSelf\",\"extra\":1}", "UnknownArgument")]
    [TestCase("{\"kind\":\"TreatSelf\",\"kind\":\"GroundSit\"}", "DuplicateArgument")]
    [TestCase("{\"kind\":\"TreatSelf\",\"npcId\":901,\"npcId\":902}", "DuplicateArgument")]
    public void InvalidArgumentsHaveSafeMachineCodes(string json, string reason)
    {
        var error = Assert.Throws<AgentActionValidationException>(() => Contract.BindAndValidate(
            new CompanionAction { Tool = "self_action", Arguments = JsonSerializer.Deserialize<JsonElement>(json) }, 901));
        Assert.That(error!.ReasonCode, Is.EqualTo(reason));
        Assert.That(error.ToString(), Does.Not.Contain("private-secret"));
    }

    [TestCase("{\"kind\":\"TreatSelf\"}")]
    [TestCase("{\"kind\":\"TreatSelf\",\"npcId\":902}")]
    public void ActorComesFromAttachmentAndRequiredFieldsRemainEnforced(string json)
    {
        var arguments = Contract.BindAndValidate(new CompanionAction
            { Tool = "self_action", Arguments = JsonSerializer.Deserialize<JsonElement>(json) }, 901);
        Assert.That(arguments["npcId"].GetInt32(), Is.EqualTo(901));
        Assert.That(arguments["kind"].GetString(), Is.EqualTo("TreatSelf"));
    }

    [Test]
    public void AllAdvertisedAllowedToolsRetainTheServersSchema()
    {
        var catalog = JsonSerializer.SerializeToElement(new { tools = HexLive.Server.Mcp.McpTools.Catalog
            .Select(t => new { name = t.Name, inputSchema = t.InputSchema }) });
        var contract = new AgentActionContract(catalog);
        var talk = contract.BindAndValidate(new CompanionAction { Tool = "talk_to",
            Arguments = JsonSerializer.SerializeToElement(new { targetNpcId = 902 }) }, 901);
        Assert.That(talk["targetNpcId"].GetInt32(), Is.EqualTo(902));
        var self = catalog.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "self_action").GetProperty("inputSchema");
        Assert.That(self.GetProperty("additionalProperties").GetBoolean(), Is.False);
        Assert.That(self.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(x => x.GetString()),
            Is.EquivalentTo(Enum.GetNames<HexLive.Simulation.Runtime.SelfActionKind>()));
        Assert.Throws<AgentActionValidationException>(() => contract.BindAndValidate(new CompanionAction
            { Tool = "self_action", Arguments = JsonSerializer.SerializeToElement(new { kind = "private-secret" }) }, 901));
        var interact = catalog.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "interact").GetProperty("inputSchema");
        var verbs = interact.GetProperty("properties").GetProperty("interaction").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.That(verbs, Does.Contain("PickUp").And.Not.Contain("Bury").And.Not.Contain("MedicalAid"));
        Assert.Throws<AgentActionValidationException>(() => contract.BindAndValidate(new CompanionAction
            { Tool = "self_action", Arguments = JsonSerializer.SerializeToElement(new { }) }, 901));
    }

    [TestCase(-32602, "McpInvalidParams")]
    [TestCase(-32601, "McpMethodNotFound")]
    [TestCase(-32000, "McpRpcError")]
    public async Task RpcErrorsNeverExposeMessagesOrData(int code, string reason)
    {
        using var handler = new RpcFailure(code);
        using var client = new McpClient(new AgentProviderOptions
            { McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture", FakeProviders = true, XaiKey = "", ElevenLabsKey = "",
                XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture" }, handler);
        var error = Assert.ThrowsAsync<McpRequestException>(async () => await client.CallToolAsync("self_action", new { }, default));
        Assert.That(error!.ReasonCode, Is.EqualTo(reason));
        Assert.That(error.ToString(), Does.Not.Contain("private-secret"));
        await Task.CompletedTask;
    }

    [Test]
    public void KnownPlainAdmissionCodesAreSafeAndRemainUseful()
    {
        Assert.That(new McpToolRejectedException("ControlledByAgent").ReasonCode, Is.EqualTo("ControlledByAgent"));
        Assert.That(new McpToolRejectedException("{\"reason\":\"NoBandage\"}").ReasonCode, Is.EqualTo("NoBandage"));
        Assert.That(new McpToolRejectedException("[]").ReasonCode, Is.EqualTo("InvalidToolArgumentsOrRejected"));
    }

    [Test]
    public void AlphabeticPrivateReasonIsNotMistakenForAnAdmissionCode()
    {
        var error = new McpToolRejectedException("{\"reason\":\"PrivateSecretToken\"}");
        Assert.That(error.ReasonCode, Is.EqualTo("InvalidToolArgumentsOrRejected"));
        Assert.That(error.ToString(), Does.Not.Contain("PrivateSecretToken"));
    }

    private sealed class RpcFailure(int code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                new { jsonrpc = "2.0", error = new { code, message = "private-secret", data = "private-secret" } })) });
    }
}
