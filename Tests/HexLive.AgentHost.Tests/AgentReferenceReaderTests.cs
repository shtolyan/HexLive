using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentReferenceReaderTests
{
    [TestCase("conflict")]
    [TestCase("io")]
    public async Task PreflightConcurrencyAndStorageFailuresDoNotTriggerModelRepair(string kind)
    {
        var root = Directory.CreateTempSubdirectory("preflight-failure-").FullName;
        try
        {
            var calls = 0;
            Exception failure = kind == "conflict" ? new AgentObjectiveConflictException() : new IOException("fixture");
            try
            {
                await new AgentMemoryRecall(root).DecideAsync("", new("world", "world", 0, 50), new(), (_, _) =>
                { calls++; return Task.FromResult(new CompanionDecision()); }, default,
                    validateDecision: (_, _) => Task.FromException(failure));
                Assert.Fail("Expected the original preflight failure.");
            }
            catch (Exception actual) { Assert.That(actual, Is.SameAs(failure)); }
            Assert.That(calls, Is.EqualTo(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task GiftDecisionMustReadActualRulesBeforeItLeavesRecall(bool queue, bool readsRules)
    {
        var root = Directory.CreateTempSubdirectory("gift-rules-").FullName;
        try
        {
            var calls = 0; var reads = 0;
            var run = new AgentMemoryRecall(root).DecideAsync("", new("world", "world", 0, 50), new(), (context, _) =>
            {
                calls++;
                if (calls > 1) Assert.That(context, Does.Contain("GiftRulesNotRead"));
                if (calls == 2 && readsRules)
                    return Task.FromResult(new CompanionDecision { MemoryRequests =
                        [new() { Operation = "spec.read", Arguments = JsonSerializer.SerializeToElement(new { section = "153" }) }] });
                var arguments = JsonSerializer.SerializeToElement(new { otherNpcId = 902, direction = "Give" });
                return Task.FromResult(new CompanionDecision
                {
                    MemorySources = ["spec:153:0:fixture"],
                    Action = queue ? null : new() { Tool = "transfer_inventory", Arguments = arguments },
                    ExecutionPlanUpdate = queue ? new() { Operation = "replace", Reason = "Gift",
                        Steps = [new("give", "transfer_inventory", arguments)] } : null
                });
            }, default, (operation, arguments, _) =>
            {
                reads++;
                Assert.That(operation, Is.EqualTo("spec.read"));
                Assert.That(arguments.GetProperty("section").GetString(), Is.EqualTo("153"));
                return Task.FromResult(new MemoryReadResult("actual-gift-rules", ["spec:153:0:fixture"], null));
            });
            if (readsRules)
            {
                var decision = await run;
                Assert.That(decision.MemorySources, Does.Contain("spec:153:0:fixture"));
                Assert.That(calls, Is.EqualTo(3));
                Assert.That(reads, Is.EqualTo(1));
            }
            else
            {
                var error = Assert.ThrowsAsync<InvalidDataException>(async () => await run);
                Assert.That(error!.Message, Is.EqualTo("GiftRulesNotRead"));
                Assert.That(calls, Is.EqualTo(4));
                Assert.That(reads, Is.Zero, "A made-up source citation is not a read.");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task DropReferenceUsesAttachedActorInsteadOfModelSuppliedNpc()
    {
        using var handler = new DropTransport();
        using var client = new McpClient(Options(), handler);
        var reader = new AgentReferenceReader(client, npcId: 901);
        var read = await reader.ReadAsync("inventory.drop.read", JsonSerializer.SerializeToElement(new
            { npcId = 999, index = 2, expectedDefinitionId = "resource.palm_crown" }), default);
        Assert.That(handler.Arguments.GetProperty("npcId").GetInt32(), Is.EqualTo(901));
        Assert.That(handler.Arguments.GetProperty("index").GetInt32(), Is.EqualTo(2));
        Assert.That(read.Text, Does.Contain("inventory-drop:901:2"));
        Assert.ThrowsAsync<InvalidDataException>(() => new AgentReferenceReader(client).ReadAsync("inventory.drop.read",
            JsonSerializer.SerializeToElement(new { index = 2, expectedDefinitionId = "resource.palm_crown" }), default));
    }

    private sealed class DropTransport : HttpMessageHandler
    {
        public JsonElement Arguments;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            object result = new { protocolVersion = "2025-06-18" };
            if (doc.RootElement.GetProperty("method").GetString() == "tools/call")
            {
                var parameters = doc.RootElement.GetProperty("params");
                Assert.That(parameters.GetProperty("name").GetString(), Is.EqualTo("read_inventory_drop"));
                Arguments = parameters.GetProperty("arguments").Clone();
                result = new { content = new[] { new { type = "text", text = "{\"canDropHere\":true}" } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CompletionWithNewWorkIsRepairedBeforeCommit(bool physicalAction, bool alwaysConflicting)
    {
        var root = Directory.CreateTempSubdirectory("completion-repair-").FullName;
        try
        {
            var calls = 0;
            var run = new AgentMemoryRecall(root).DecideAsync("", new("world", "world", 0, 50), new(), (context, _) =>
            {
                calls++;
                if (calls > 1) Assert.That(context, Does.Contain("ExecutionPlanNotCompleted"));
                return Task.FromResult(calls == 1 || alwaysConflicting
                    ? new CompanionDecision { Speech = "must-not-publish", ObjectiveUpdate = new() { Operation = "complete" },
                        Action = physicalAction ? new() { Tool = "stop", Arguments = JsonSerializer.SerializeToElement(new { }) } : null,
                        ExecutionPlanUpdate = physicalAction ? null : new() { Operation = "replace", Reason = "Unload",
                            Steps = [new("drop", "stop", JsonSerializer.SerializeToElement(new { }))] } }
                    : new CompanionDecision { IntentSummary = "continue-unloading" });
            }, default);
            if (alwaysConflicting)
            {
                var error = Assert.ThrowsAsync<InvalidDataException>(async () => await run);
                Assert.That(error!.Message, Is.EqualTo("ExecutionPlanNotCompleted"));
                Assert.That(calls, Is.EqualTo(4));
            }
            else
            {
                var result = await run;
                Assert.That(result.IntentSummary, Is.EqualTo("continue-unloading"));
                Assert.That(result.Speech, Is.Empty);
                Assert.That(result.ObjectiveUpdate, Is.Null);
                Assert.That(calls, Is.EqualTo(2));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void InvalidDecisionCarriesItsRejectedFinalJsonForCorrection()
    {
        const string json = """
            {"speech":"","emotion":"neutral","action":null,"reaction":"None","intentSummary":"","relationshipAssessment":null,
             "memoryUpserts":[],"journalText":"","executionPlanUpdate":{"operation":"replace","reason":"Fixture",
             "steps":[{"id":"first","tool":"stop","arguments":{},"condition":{"path":"inventorySummary.freeSlots",
             "operator":"gte","value":1,"onFalseStepId":"first"}}]}}
            """;
        var error = Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(json, "heartbeat"));
        Assert.That(error!.Message, Is.EqualTo("InvalidExecutionCondition"));
        Assert.That(error.Data["decisionJson"], Is.EqualTo(json));
        Assert.That(error.Data["conditionStep"], Is.EqualTo("first"));
        Assert.That(error.Data["conditionTarget"], Is.EqualTo("first"));
        Assert.That(error.Data["laterStepIds"], Is.Empty);
    }

    [TestCase("InvalidExecutionCondition")]
    [TestCase("InvalidExecutionPlanUpdate")]
    public async Task InvalidPlanCanBeCorrectedBeforeReturningAnExecutableDecision(string code)
    {
        var root = Directory.CreateTempSubdirectory("invalid-plan-repair-").FullName;
        try
        {
            var calls = 0;
            var result = await new AgentMemoryRecall(root).DecideAsync("", new("world", "world", 0, 50), new(), (context, _) =>
            {
                if (++calls == 1)
                {
                    var error = new InvalidDataException(code);
                    error.Data["decisionJson"] = "rejected-plan-sentinel";
                    throw error;
                }
                Assert.That(context, Does.Contain(code));
                Assert.That(context, Does.Contain("rejected-plan-sentinel"));
                return Task.FromResult(new CompanionDecision { IntentSummary = "repaired" });
            }, default);
            Assert.That(result.IntentSummary, Is.EqualTo("repaired"));
            Assert.That(calls, Is.EqualTo(2));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConflictingControlFormsAreRepairedWithinTheExistingRoundBudget(bool alwaysConflicting)
    {
        var root = Directory.CreateTempSubdirectory("decision-repair-").FullName;
        try
        {
            var calls = 0;
            var run = new AgentMemoryRecall(root).DecideAsync("", new("world", "world", 0, 50), new(), (context, _) =>
            {
                calls++;
                if (calls > 1) Assert.That(context, Does.Contain("ConflictingActionAndExecutionPlan"));
                return Task.FromResult(calls == 1 || alwaysConflicting
                    ? new CompanionDecision { Speech = "must-not-publish", Action = new() { Tool = "stop", Arguments = JsonSerializer.SerializeToElement(new { }) },
                        ExecutionPlanUpdate = new() { Operation = "replace", Reason = "Fixture",
                            Steps = [new("stop", "stop", JsonSerializer.SerializeToElement(new { }))] } }
                    : new CompanionDecision { IntentSummary = "consistent-final" });
            }, default);
            if (alwaysConflicting)
            {
                Assert.ThrowsAsync<InvalidDataException>(async () => await run);
                Assert.That(calls, Is.EqualTo(4));
            }
            else
            {
                var result = await run;
                Assert.That(result.IntentSummary, Is.EqualTo("consistent-final"));
                Assert.That(result.Speech, Is.Empty);
                Assert.That(calls, Is.EqualTo(2));
            }
        }
        finally { Directory.Delete(root, true); }
    }

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
    public async Task ActiveGoalRefreshesSelectedReferencesBeforeTheNextModelCallAndDropsThemOnNewGoal()
    {
        var root = Directory.CreateTempSubdirectory("goal-refs-").FullName;
        try
        {
            var recall = new AgentMemoryRecall(root);
            var world = new MashaWorldHandle("episode", "world", 0, 0);
            var state = new MashaArchive { Objective = new() { Status = "active", WorldKey = "world", AvatarNpcId = 901, Revision = 1 },
                Worlds = [new() { Id = "episode", WorldKey = "world", AvatarNpcId = 901 }] };
            var reads = 0;
            var version = "version-one";
            Task<MemoryReadResult> Read(string operation, JsonElement args, CancellationToken token)
            { reads++; return Task.FromResult(new MemoryReadResult(version, [version], null)); }
            var calls = 0;
            await recall.DecideAsync("", world, state, (_, _) => Task.FromResult(++calls == 1
                ? new CompanionDecision { MemoryRequests = [new() { Operation = "skills.read", Arguments = JsonSerializer.SerializeToElement(new { id = "give-gift" }) }] }
                : new CompanionDecision()), default, Read);
            version = "version-two";
            calls = 0;
            await recall.DecideAsync("", world, state, (context, _) =>
            {
                calls++;
                Assert.That(context, Does.Contain("version-two").And.Not.Contain("version-one"));
                return Task.FromResult(new CompanionDecision());
            }, default, Read);
            Assert.That(calls, Is.EqualTo(1), "No model round should be needed to reread the same skill.");
            Assert.That(reads, Is.EqualTo(2), "References are refreshed, not served stale.");
            state.Objective.Revision++;
            await recall.DecideAsync("", world, state, (context, _) =>
            {
                Assert.That(context, Does.Not.Contain("version-two"));
                return Task.FromResult(new CompanionDecision());
            }, default, Read);
            Assert.That(reads, Is.EqualTo(2));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task PausingAndResumingRetainsReferencesButSettingTheSameTaskAgainDoesNot()
    {
        var root = Directory.CreateTempSubdirectory("paused-goal-refs-").FullName;
        try
        {
            var recall = new AgentMemoryRecall(root);
            var world = new MashaWorldHandle("episode", "world", 0, 0);
            var state = new MashaArchive { Worlds = [new() { Id = "episode", WorldKey = "world", AvatarNpcId = 901 }] };
            void Change(string operation) => state.Objective = AgentObjectivePolicy.Apply(state.Objective,
                new() { Operation = operation, Text = operation == "set" ? "Build a new bed" : "", Reason = "Fixture" },
                state.Objective?.Revision ?? 0, "world", 901, DateTimeOffset.UtcNow);
            Change("set");
            Task<MemoryReadResult> Read(string operation, JsonElement args, CancellationToken token) =>
                Task.FromResult(new MemoryReadResult("fresh-bed-recipe", ["recipe-source"], null));
            var calls = 0;
            await recall.DecideAsync("", world, state, (_, _) => Task.FromResult(++calls == 1
                ? new CompanionDecision { MemoryRequests = [new() { Operation = "build.read", Arguments = JsonSerializer.SerializeToElement(new { definitionId = "bed.basic" }) }] }
                : new CompanionDecision()), default, Read);
            foreach (var operation in new[] { "pause", "resume", "set" })
            {
                Change(operation);
                await recall.DecideAsync("", world, state, (context, _) =>
                {
                    Assert.That(context.Contains("fresh-bed-recipe"), Is.EqualTo(operation != "set"));
                    return Task.FromResult(new CompanionDecision());
                }, default, Read);
            }
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
