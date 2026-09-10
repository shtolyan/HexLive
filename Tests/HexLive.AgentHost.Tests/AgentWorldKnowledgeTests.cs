using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentWorldKnowledgeTests
{
    [Test]
    public async Task BlockedAutonomyKnowledgeDoesNotSerializeReplyKnowledge()
    {
        using var transport = new ParallelKnowledgeTransport();
        using var stop = new CancellationTokenSource();
        var inner = new KnowledgeProviders();
        using var providers = new KnowledgeAwareAgentProviders(inner,
            new McpClient(McpSessionRecoveryTests.Options(), transport));
        var autonomy = providers.DecideAsync("heartbeat", "{}", "", "§121", [], stop.Token);
        await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await providers.DecideAsync("voice", "{}", "", "§121", [], stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(autonomy.IsCompleted, Is.False);
        Assert.That(inner.Context, Does.Contain("REPLY-KNOWLEDGE"));
        Assert.That(transport.Initializations, Is.EqualTo(2));
        stop.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await autonomy);
    }

    private sealed class ParallelKnowledgeTransport : HttpMessageHandler
    {
        public int Initializations;
        public readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = doc.RootElement;
            var session = request.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.Single() : "";
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "initialize")
                session = "parallel-" + Interlocked.Increment(ref Initializations);
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                if (session == "parallel-1") { Blocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                var args = root.GetProperty("params").GetProperty("arguments");
                var page = args.GetProperty("section").GetString() == "" ? Index() : Page("REPLY-KNOWLEDGE");
                result = new { isError = false, content = new[] { new { type = "text", text = page.GetRawText() } } };
            }
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", session); return response;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExpiredKnowledgeSessionGetsOneFreshReadOnlyAttempt(bool rejectFresh)
    {
        using var transport = new KnowledgeTransport { RejectFresh = rejectFresh };
        var original = new McpClient(McpSessionRecoveryTests.Options(), transport);
        await original.CallToolAsync("world_status", new { }, default);
        var inner = new KnowledgeProviders();
        using var providers = new KnowledgeAwareAgentProviders(inner, original);
        await providers.DecideAsync("voice", "{}", "", "§121", Array.Empty<string>(), default);
        Assert.That(transport.Initializations, Is.EqualTo(2));
        Assert.That(transport.Tools, Is.All.Matches<string>(name => name is "world_status" or "read_spec"));
        Assert.That(inner.Context, rejectFresh ? Does.Contain("не подтверждены") : Does.Contain("KNOWLEDGE-RESTORED"));
        Assert.ThrowsAsync<ObjectDisposedException>(() => original.CallToolAsync("world_status", new { }, default));
        var calls = transport.Tools.Count;
        await providers.DecideAsync("voice", "{}", "", "§121", Array.Empty<string>(), default);
        Assert.That(transport.Tools, Has.Count.EqualTo(calls), "Success caches; failure backs off instead of another immediate handshake.");
        Assert.That(transport.Initializations, Is.EqualTo(2));
    }

    [Test]
    public async Task StoppingDuringExpiredKnowledgeReadDoesNotReconnectOrCallModel()
    {
        using var stop = new CancellationTokenSource();
        using var transport = new KnowledgeTransport { StopOnExpiry = stop };
        var original = new McpClient(McpSessionRecoveryTests.Options(), transport);
        await original.CallToolAsync("world_status", new { }, default);
        var inner = new KnowledgeProviders();
        using var providers = new KnowledgeAwareAgentProviders(inner, original);
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await providers.DecideAsync("voice", "{}", "", "§121", Array.Empty<string>(), stop.Token));
        Assert.That(transport.Initializations, Is.EqualTo(1));
        Assert.That(inner.Decisions, Is.Zero);
    }

    private sealed class KnowledgeProviders : IAgentProviders
    {
        public string Context = "";
        public int Decisions;
        public Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
            string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        { Context = memoryContext; Decisions++; return Task.FromResult(new CompanionDecision()); }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken) => throw new AssertionException("Not requested");
        public void Dispose() { }
    }

    private sealed class KnowledgeTransport : HttpMessageHandler
    {
        public bool RejectFresh;
        public CancellationTokenSource? StopOnExpiry;
        public int Initializations;
        public readonly List<string> Tools = [];
        private bool _expired;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            object result = new { protocolVersion = "2025-06-18" };
            if (method == "initialize")
            {
                Initializations++;
                Assert.That(request.Headers.Contains("Mcp-Session-Id"), Is.False);
                if (RejectFresh && Initializations > 1) return new(System.Net.HttpStatusCode.Unauthorized);
            }
            if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString()!;
                Tools.Add(name);
                if (name == "read_spec" && !_expired)
                {
                    _expired = true;
                    StopOnExpiry?.Cancel();
                    return new(System.Net.HttpStatusCode.Unauthorized);
                }
                object payload = name == "read_spec"
                    ? parameters.GetProperty("arguments").GetProperty("section").GetString() == "" ? Index() : Page("KNOWLEDGE-RESTORED")
                    : new { };
                result = new { isError = false, content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } };
            }
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "knowledge-" + Initializations);
            return response;
        }
    }

    [Test]
    public async Task WoundedHeartbeatSelectsTreatmentNotAlwaysPresentEnergyFields()
    {
        var calls = new List<string>();
        var knowledge = new AgentWorldKnowledge((section, _, _) =>
        {
            calls.Add(section);
            return Task.FromResult(section == "" ? Json(new { index =
                "| [§54](Spec/54.md) | Sleep |\n| [§137](Spec/137.md) | Rest |\n| [§68](Spec/68.md) | Лечение |\n| [§44](Spec/44.md) | Bandages |" }) : Page("Treatment"));
        });
        await knowledge.BuildAsync("", "health=0.831; needs[energy=0.451, stamina=0.623, blood=1]", default);
        Assert.That(calls, Is.EqualTo(new[] { "", "68", "44" }));
    }

    [Test]
    public void ToolFailureKeepsOnlyMachineReasonNotRawResponse()
    {
        var failure = new McpToolRejectedException("{\"reason\":\"NoSupplies\",\"detail\":\"private text\"}");
        Assert.That(failure.ReasonCode, Is.EqualTo("NoSupplies"));
        Assert.That(failure.ToString(), Does.Not.Contain("private text"));
        Assert.That(new McpToolRejectedException("private text").ReasonCode, Is.EqualTo("InvalidToolArgumentsOrRejected"));
    }

    [Test]
    public void ActionContractPreservesTreatmentDescriptionsAndRequiredArguments()
    {
        var method = typeof(AgentHostRuntime).GetMethod("BuildActionContract",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var catalog = Json(new { tools = new[] { new { name = "self_action", description = "TreatSelf means bandage",
            inputSchema = new { type = "object", properties = new { kind = new { type = "string", description = "TreatSelf/GroundSleep" } }, required = new[] { "kind" } } } } });
        var result = (string)method.Invoke(null, new object[] { catalog })!;
        Assert.That(result, Does.Contain("TreatSelf means bandage").And.Contain("TreatSelf/GroundSleep").And.Contain("required"));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Index() => Json(new { index = "| [§54](Spec/54.md) | Sleep |\n| [§137](Spec/137.md) | Rest |\n| [§121](Spec/121.md) | Manual |" });
    private static JsonElement Page(string text, int offset = 0, bool truncated = false)
        => Json(new { text, offset, truncated, nextOffset = truncated ? offset + text.Length : (int?)null });

    [Test]
    public async Task RestQueryReadsActualSectionsOnceAndSeparatesEnergyFromStamina()
    {
        var calls = new List<string>();
        var knowledge = new AgentWorldKnowledge((section, _, _) =>
        {
            calls.Add(section);
            return Task.FromResult(section == "" ? Index() : Page(section == "54"
                ? "### Sleep\n\n#### r1 historical\n\nOLD EnergyDelta obsolete rule.\n\n#### r2 current\n\nEnergy sleep recovery ONLY-SLEEP."
                : "### Rest\n\nRest stamina STAMINA-REST."));
        });
        var context = await knowledge.BuildAsync("Сяду на пенёк восстановить энергию", "{}", default);
        Assert.That(calls, Is.EqualTo(new[] { "", "54", "137" }));
        Assert.That(context, Does.Contain("ONLY-SLEEP").And.Contain("STAMINA-REST").And.Not.Contain("OLD EnergyDelta"));
        Assert.That(context.Length, Is.LessThanOrEqualTo(AgentWorldKnowledge.ContextLimit));
        await knowledge.BuildAsync("Сяду на пенёк восстановить энергию", "{}", default);
        Assert.That(calls.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task ExplicitSectionWinsAndPaginationIsFollowed()
    {
        var offsets = new List<int>();
        var knowledge = new AgentWorldKnowledge((section, offset, _) =>
        {
            if (section == "") return Task.FromResult(Index());
            Assert.That(section, Is.EqualTo("121"));
            offsets.Add(offset);
            return Task.FromResult(offset == 0 ? Page("First\n\n", 0, true) : Page("Manual NEXT-PAGE", 7));
        });
        var context = await knowledge.BuildAsync("Объясни §121", "{}", default);
        Assert.That(context, Does.Contain("NEXT-PAGE"));
        Assert.That(offsets, Is.EqualTo(new[] { 0, 7 }));
    }

    [Test]
    public async Task FailureIsExplicitAndBackedOffNotInvented()
    {
        var calls = 0;
        var knowledge = new AgentWorldKnowledge((_, _, _) =>
        {
            calls++;
            throw new HttpRequestException("fixture");
        });
        Assert.That(await knowledge.BuildAsync("", "{}", default), Does.Contain("не подтверждены"));
        await knowledge.BuildAsync("", "{}", default);
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void PlayerDepartureCancellationPropagates()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var knowledge = new AgentWorldKnowledge((_, _, token) => Task.FromCanceled<JsonElement>(token));
        Assert.ThrowsAsync<TaskCanceledException>(async () => await knowledge.BuildAsync("", "{}", stop.Token));
    }

    [Test]
    public async Task OversizedParagraphsRemainBoundedAndLabelled()
    {
        var knowledge = new AgentWorldKnowledge((section, _, _) => Task.FromResult(section == ""
            ? Index() : Page(string.Join("\n\n", Enumerable.Repeat(new string('я', 2000), 8)))));
        var context = await knowledge.BuildAsync("пенек", "{}", default);
        Assert.That(context.Length, Is.LessThanOrEqualTo(AgentWorldKnowledge.ContextLimit));
        Assert.That(context, Does.Contain("обрезана"));
    }

    [Test]
    public async Task RealSpecificationRetrievesCurrentSleepClockAndRestRule()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Spec"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        var knowledge = new AgentWorldKnowledge((section, offset, _) =>
        {
            if (section == "") return Task.FromResult(Index());
            var text = File.ReadAllText(Path.Combine(root!.FullName, "Spec", section + ".md"));
            var part = text.Substring(offset, Math.Min(24000, text.Length - offset));
            return Task.FromResult(Page(part, offset, offset + part.Length < text.Length));
        });
        var context = await knowledge.BuildAsync("Сяду на пенёк восстановить энергию", "{}", default);
        Assert.That(context, Does.Contain("only owner of sleep energy").And.Contain("StaminaRestGain"));
        Assert.That(context, Does.Not.Contain("TWO independent"));
        Assert.That(context.Length, Is.LessThanOrEqualTo(AgentWorldKnowledge.ContextLimit));
    }

    [Test]
    public async Task InvalidContinuationDoesNotPresentPartialChapterAsKnown()
    {
        var knowledge = new AgentWorldKnowledge((section, _, _) => Task.FromResult(section == ""
            ? Index() : Json(new { text = "Incomplete", offset = 0, truncated = true, nextOffset = 0 })));
        var context = await knowledge.BuildAsync("§121", "{}", default);
        Assert.That(context, Does.Contain("не подтверждены").And.Not.Contain("Incomplete"));
    }
}
