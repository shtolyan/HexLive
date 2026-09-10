using System.Reflection;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed partial class AgentExecutionRuntimeTests
{
    // Real model, real native commands, isolated save and memory. Never connects to a game server.
    [Explicit("Paid/model-backed gameplay acceptance; needs explicit provider and output environment")]
    [TestCase(0, 0)] [TestCase(0, 1)]
    [TestCase(1, 0)] [TestCase(1, 1)]
    [TestCase(2, 0)] [TestCase(2, 1)]
    public Task RealModelCoconutDelivery(int fixture, int repetition) => RunGameplayModel("coconuts", fixture, repetition);

    [Explicit("Paid/model-backed gameplay acceptance; needs explicit provider and output environment")]
    [TestCase(0, 0)] [TestCase(0, 1)]
    [TestCase(1, 0)] [TestCase(1, 1)]
    [TestCase(2, 0)] [TestCase(2, 1)]
    public Task RealModelGiftDelivery(int fixture, int repetition) => RunGameplayModel("gift", fixture, repetition);

    [Explicit("Paid/model-backed gameplay acceptance; needs explicit provider and output environment")]
    [TestCase(0, 0)] [TestCase(0, 1)]
    [TestCase(1, 0)] [TestCase(1, 1)]
    [TestCase(2, 0)] [TestCase(2, 1)]
    public Task RealModelBedConstruction(int fixture, int repetition) => RunGameplayModel("bed", fixture, repetition);

    private async Task RunGameplayModel(string scenario, int fixture, int repetition)
    {
        var task = scenario == "coconuts"
            ? "Принеси три кокоса в свой лагерь и выгрузи их. Нужные инструменты сохрани."
            : scenario == "bed" ? "Полностью построй кровать в своём лагере за три игровых дня. Материалы добудь сама, отдыхай и спи по необходимости."
            : "Найди и подари что-нибудь девушке, которая тебе нравится. Выбери подарок осознанно, нужные инструменты сохрани.";
        var output = Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_OUTPUT") ?? throw new InvalidOperationException("OutputRequired");
        var providerName = Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_PROVIDER") ?? "Codex";
        var modelId = Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_MODEL") ?? throw new InvalidOperationException("ModelRequired");
        Directory.CreateDirectory(output);
        var report = Path.Combine(output, $"{scenario}-{fixture}-{repetition}.json");
        IModelAdapter adapter = providerName == "Codex"
            ? new CodexModelAdapter(Environment.GetEnvironmentVariable("HEXLIVE_CODEX_SMOKE_EXECUTABLE") ?? throw new InvalidOperationException("CodexExecutableRequired"), "eval")
            : new HttpModelAdapter(ModelProviderKind.Grok, "eval", _ => Task.FromResult(Environment.GetEnvironmentVariable("XAI_API_KEY") ?? throw new InvalidOperationException("GrokCredentialRequired")));
        using var model = new AgentProviders(new AgentProviderOptions
        {
            McpUri = new("http://fixture/mcp"), McpToken = "fixture", XaiKey = "", ElevenLabsKey = "",
            XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "", DialogueStyleId = DialogueStyles.Masha
        }, adapter, new(providerName == "Codex" ? ModelProviderKind.Codex : ModelProviderKind.Grok, "eval", modelId, providerName == "Codex" ? "low" : null));
        using var host = Host();
        var engine = (SimulationEngine)typeof(HexLive.Server.WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        ObjectId? bedSite = null;
        var sawSleep = false;
        var recipientId = 0;
        var recipientItemsBefore = 0;
        host.Read(w =>
        {
            foreach (var n in w.Entities.Npcs.Values) { n.Mind.ManualControl = true; n.Faction = Faction.Colony; }
            for (var i = 0; i < 64; i++) engine.Step();
            w.Mobs.Clear();
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Inventory.Items.Clear(); npc.WornItems.Clear();
            var backpack = new ItemInstance("gear.backpack_riot");
            if (fixture == 1) npc.WornItems.Add(backpack); else npc.Inventory.Items.Add(backpack);
            if (fixture == 2) npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Hammer));
            typeof(SimulationEngine).Assembly.GetType("HexLive.Simulation.Runtime.EquipmentMath")!
                .GetMethod("RecalculateCapacity", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [w, npc]);
            npc.Needs.Energy = npc.Needs.Stamina = 1f;
            npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            npc.Mind.AdrenalineUntilTick = 0;
            npc.Perception.Hostiles.Clear(); npc.Perception.Mobs.Clear();
            var neighbors = SpatialQueries.GetPassableNeighbors(w, npc.CurrentJunction!.Value).Take(3).ToArray();
            Assert.That(neighbors.Length, Is.EqualTo(3));
            foreach (var at in neighbors)
                WorldObjectMutations.SpawnObject(w, ContentIds.Coconut, npc.Fragment, w.Junctions.Items[at].Tiles[0], at);
            for (var i = 0; i < 8; i++) engine.Step();
            if (scenario == "bed")
            {
                var site = w.Entities.Objects.Values.First(o => o.DefinitionId == "build.site" && o.BuildProduct == "bed.basic");
                Assert.That(site.Contents, Is.Empty, "Bed acceptance starts with an empty site.");
                bedSite = site.Id;
                npc.Needs.Energy = .2f;
            }
            if (scenario == "gift")
            {
                var candidates = npc.Perception.Agents.Where(a => a.CanSee && a.IsReachable && a.Id != npc.Id)
                    .Select(a => w.Entities.Npcs[a.Id]).Where(n => n.Sex == GarmentSex.Female && n.Health > 0).ToArray();
                Assert.That(candidates, Is.Not.Empty, "Gift fixture needs a naturally visible recipient.");
                var recipient = candidates[fixture % candidates.Length];
                recipientId = recipient.Id.Value;
                foreach (var other in candidates) npc.Social.GetOrCreate(other.Id).Affinity = 0;
                npc.Social.GetOrCreate(recipient.Id).Affinity = .8f;
                recipient.Social.GetOrCreate(npc.Id).Affinity = 0;
                recipient.Inventory.Items.Clear();
                recipientItemsBefore = recipient.Inventory.Items.Count;
                recipient.Needs.Hunger = fixture == 0 ? .8f : 0;
                recipient.Needs.Thirst = fixture == 1 ? .8f : 0;
                for (var i = 0; i < 8; i++) engine.Step();
            }
            return true;
        });
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var noModelDuringExecution = new NoModel();
        var runtime = new AgentHostRuntime(options, noModelDuringExecution);
        var store = Memory(runtime);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var reader = new AgentReferenceReader(mcp);
        var recall = new AgentMemoryRecall(options.MemoryDirectory);
        var catalog = await mcp.ReadToolCatalogAsync(default);
        var contract = (string)typeof(AgentHostRuntime).GetMethod("BuildActionContract", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [catalog])!;
        var turns = new List<object>();
        var calls = 0;
        var modelDecisions = new List<object>();
        async Task<CompanionDecision> Decide(string trigger, string bodyJson, string context, string player,
            IReadOnlyList<string> recent, CancellationToken token)
        {
            var call = ++calls;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var answer = await model.DecideAsync(trigger, bodyJson, context, player, recent, token);
                modelDecisions.Add(new { call, elapsedMs = watch.ElapsedMilliseconds, decision = answer,
                    usage = answer.ModelUsage, body = JsonSerializer.Deserialize<JsonElement>(bodyJson) });
                return answer;
            }
            catch (Exception ex)
            {
                modelDecisions.Add(new { call, elapsedMs = watch.ElapsedMilliseconds, error = ex.GetType().Name });
                throw;
            }
        }
        var passed = false;
        var multiStep = false;
        var status = "Incomplete";
        var initialTick = host.Read(w => w.Tick);
        try
        {
            var world = await store.BindHexLiveWorldAsync(await mcp.CallToolAsync("world_status", new { }, default), 901, "fixture", default);
            var prompt = await store.BuildPromptContextAsync(world, "", default);
            world = world with { ObjectiveRevision = prompt.ObjectiveRevision, ExecutionPlanRevision = prompt.ExecutionPlanRevision, ExecutionPlanId = prompt.ExecutionPlanId };
            await store.CommitTurnAsync(world, "request", "voice", new CompanionDecision
            { ObjectiveUpdate = new() { Operation = "set", Text = task, Reason = "Приказ игрока" } }, default);
            for (var turn = 0; turn < (scenario == "bed" ? 32 : 8); turn++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                world = await store.BindHexLiveWorldAsync(await mcp.CallToolAsync("world_status", new { }, timeout.Token), 901, "fixture", timeout.Token);
                prompt = await store.BuildPromptContextAsync(world, task, timeout.Token);
                world = world with { ObjectiveRevision = prompt.ObjectiveRevision, ExecutionPlanRevision = prompt.ExecutionPlanRevision, ExecutionPlanId = prompt.ExecutionPlanId };
                var body = await mcp.CallToolAsync("describe_colonist", new { npcId = 901 }, timeout.Token);
                var state = await store.SnapshotAsync(timeout.Token);
                var context = prompt.Text + "\nAvailable MCP actions:\n" + contract;
                var decision = await recall.DecideAsync(turn == 0 ? task : "", world, state, async (evidence, token) =>
                {
                    return await Decide(turn == 0 ? "voice" : "heartbeat", body.GetRawText(), context + "\n" + evidence,
                        turn == 0 ? task : "", turn == 0 ? [] : ["Игрок: " + task], token);
                }, timeout.Token, reader.ReadAsync);
                if (decision.Action?.Tool == "query_known_objects")
                {
                    JsonElement known;
                    try
                    {
                        var args = new AgentActionContract(catalog).BindAndValidate(decision.Action, 901);
                        known = await mcp.CallToolAsync("query_known_objects", args, timeout.Token);
                    }
                    catch (Exception ex) when (ex is AgentActionValidationException or McpToolRejectedException)
                    {
                        // Match ResolveObjectKnowledgeAsync: a rejected read is evidence,
                        // not a physical side effect and not an erased objective.
                        known = JsonSerializer.SerializeToElement(new { error = "KnowledgeReadUnavailable", npcId = 901 });
                    }
                    var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body.GetRawText())!;
                    fields["knownObjectQuery"] = known;
                    decision = await Decide(turn == 0 ? "voice" : "heartbeat", JsonSerializer.Serialize(fields), context + "\n" + AgentPromptFiles.Read("object-knowledge.md"),
                        turn == 0 ? task : "", turn == 0 ? [] : ["Игрок: " + task], timeout.Token);
                }
                turns.Add(new { turn, tick = host.Read(w => w.Tick), decision, usage = decision.ModelUsage });
                File.WriteAllText(report, JsonSerializer.Serialize(new { providerName, modelId, fixture, repetition, calls, status, modelDecisions, turns }, new JsonSerializerOptions { WriteIndented = true }));
                if (decision.Action != null) throw new InvalidOperationException("ModelDidNotProduceExecutionPlan");
                await store.CommitTurnAsync(world, "model-" + turn, turn == 0 ? "voice" : "heartbeat", decision, timeout.Token);
                var saved = await store.SnapshotAsync(timeout.Token);
                var delivered = host.Read(w => w.Entities.Objects.Values.Count(o => o.DefinitionId == ContentIds.Coconut &&
                    o.ProduceOrigin == ProduceOrigin.Gathered && ColonyQueries.InCamp(w, o.Tile, Faction.Colony)));
                if (saved.Objective?.Status == "completed")
                {
                    passed = scenario == "coconuts" ? delivered == 3 && multiStep : scenario == "bed"
                        ? multiStep && sawSleep && host.Read(w => w.Tick - initialTick <= 3 * EnvironmentSystem.DayLengthTicks &&
                            w.Entities.Objects.TryGetValue(bedSite!.Value, out var site) && site.DefinitionId == "bed.basic")
                        : saved.ExecutionProgress.Any(p => p.Step.Tool == "interact") && host.Read(w =>
                        w.Entities.Npcs[new EntityId(recipientId)].Inventory.Items.Count > recipientItemsBefore &&
                        w.Events.Items.Count(e => e.Type == "GiftGiven" && e.Message.Contains($"->NPC{recipientId} ")) == 1);
                    status = passed ? "Completed" : "FalseCompletion";
                    break;
                }
                if (saved.ExecutionPlan is not { Status: "active" } plan) continue;
                multiStep |= plan.Steps.Length >= 2;
                using var executionStop = new CancellationTokenSource();
                var running = Run(runtime, mcp, plan.Id, world, 90, executionStop.Token);
                var exceeded = false;
                while (!running.IsCompleted)
                {
                    host.Read(w =>
                    {
                        var npc = w.Entities.Npcs[new EntityId(901)];
                        for (var i = 0; i < 16 && npc.Plan.Status == PlanStatus.Active; i++)
                        {
                            engine.Step();
                            sawSleep |= npc.Execution.CurrentInteraction == InteractionType.Sleep;
                            if (w.Tick - initialTick > 3 * EnvironmentSystem.DayLengthTicks)
                                { exceeded = true; executionStop.Cancel(); break; }
                        }
                        return true;
                    });
                    await Task.Delay(1);
                }
                await running;
                if (exceeded) throw new InvalidOperationException("ThreeGameDaysExceeded");
            }
            if (fixture == 2)
                Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Hammer)), Is.True);
            Assert.That(noModelDuringExecution.Calls, Is.Zero);
            Assert.That(passed, Is.True, status + "; inspect " + report);
        }
        catch (Exception ex) { status = ex.GetType().Name + ":" + status; throw; }
        finally
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { providerName, modelId, scenario, fixture, repetition, calls, passed, status,
                gameTicks = host.Read(w => w.Tick) - initialTick, sawSleep, commands = transport.Executions, receipts = host.Read(w => w.AgentCommands.GetValueOrDefault(901)?.Receipts), modelDecisions, turns }, new JsonSerializerOptions { WriteIndented = true }));
            (adapter as IDisposable)?.Dispose();
        }
    }
}
