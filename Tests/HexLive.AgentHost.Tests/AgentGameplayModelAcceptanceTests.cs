using System.Reflection;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Runtime.Blueprints;
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

    [Explicit("Paid/model-backed delivery with a lost MCP acknowledgement")]
    [TestCase(0, 0)] [TestCase(0, 1)]
    [TestCase(1, 0)] [TestCase(1, 1)]
    [TestCase(2, 0)] [TestCase(2, 1)]
    public Task RealModelDeliveryWithLostAcknowledgement(int fixture, int repetition) => RunGameplayModel("resilience", fixture, repetition);

    private async Task RunGameplayModel(string scenario, int fixture, int repetition)
    {
        var delivery = scenario is "coconuts" or "resilience";
        var decisionTurnLimit = scenario == "bed" ? 64 : 8;
        const string executionWallBudgetPolicy = "max(90, 30 + 4 * declaredDispatches) seconds";
        var task = delivery
            ? "Принеси три кокоса в свой лагерь и выгрузи их. Нужные инструменты сохрани."
            : scenario == "bed" ? "Полностью построй НОВУЮ кровать с нуля в своём лагере за три игровых дня. Уже существующие кровати не засчитываются. Материалы добудь сама, отдыхай и спи по необходимости."
            : "Найди и подари что-нибудь девушке, которая тебе нравится. Выбери подарок осознанно, нужные инструменты сохрани.";
        var output = Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_OUTPUT") ?? throw new InvalidOperationException("OutputRequired");
        var providerName = Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_PROVIDER") ?? "Codex";
        var modelId = Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_MODEL") ?? throw new InvalidOperationException("ModelRequired");
        var replayPath = Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_REPLAY");
        JsonElement[]? recorded = null;
        if (!string.IsNullOrWhiteSpace(replayPath))
        {
            using var replay = JsonDocument.Parse(File.ReadAllText(replayPath));
            recorded = replay.RootElement.GetProperty("modelDecisions").EnumerateArray()
                .Where(r => r.TryGetProperty("decision", out _)).Select(r => r.GetProperty("decision").Clone()).ToArray();
            providerName = "Replay"; modelId = "recorded-decisions";
        }
        var reasoningEffort = providerName == "Codex"
            ? Environment.GetEnvironmentVariable("HEXLIVE_GAMEPLAY_CODEX_REASONING") ?? "low" : null;
        if (reasoningEffort != null && reasoningEffort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new InvalidOperationException("InvalidReasoningEffort");
        Directory.CreateDirectory(output);
        var report = Path.Combine(output, $"{scenario}-{fixture}-{repetition}.json");
        IModelAdapter adapter = providerName == "Codex"
            ? new CodexModelAdapter(Environment.GetEnvironmentVariable("HEXLIVE_CODEX_SMOKE_EXECUTABLE") ?? throw new InvalidOperationException("CodexExecutableRequired"), "eval")
            : new HttpModelAdapter(ModelProviderKind.Grok, "eval", _ => Task.FromResult(Environment.GetEnvironmentVariable("XAI_API_KEY") ?? throw new InvalidOperationException("GrokCredentialRequired")));
        using var model = new AgentProviders(new AgentProviderOptions
        {
            McpUri = new("http://fixture/mcp"), McpToken = "fixture", XaiKey = "", ElevenLabsKey = "",
            XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "", DialogueStyleId = DialogueStyles.Masha
        }, adapter, new(providerName == "Codex" ? ModelProviderKind.Codex : ModelProviderKind.Grok, "eval", modelId, reasoningEffort));
        using var host = Host();
        var engine = (SimulationEngine)typeof(HexLive.Server.WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var bedAnchors = new HashSet<JunctionId>();
        var sawSleep = false;
        var recipientId = 0;
        var recipientItemsBefore = 0;
        host.Read(w =>
        {
            if (scenario == "bed")
                foreach (var id in w.Entities.Npcs.Keys.Where(id => id.Value != 901).ToArray())
                {
                    var removed = AdminWorldCommands.Execute(w, new AdminCommand
                    { Kind = "delete_npc", NpcId = id.Value, OperationId = "bed-fixture-" + id.Value });
                    Assert.That(removed.Accepted, Is.True);
                }
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
                // Prepare only the house through the ordinary blueprint/build path.
                // Completing its architecture stakes EMPTY furniture sites; no bed is paid or raised.
                var draft = BuiltInBuildingBlueprints.Hut1Hex();
                var blueprintId = w.NextPlayerBlueprintId++;
                w.PlayerBlueprints[blueprintId] = draft;
                var home = ColonyQueries.Home(w, Faction.Colony) ?? npc.Tile;
                var house = w.Tiles.Items.Keys.OrderBy(t => HexSpatialMath.HexDistance(t, home))
                    .Select(t => BuildingBootstrap.CreatePlayerBlueprintSite(w, t, 0f, blueprintId)).FirstOrDefault(h => h != null);
                Assert.That(house, Is.Not.Null, "No legal house fixture location.");
                DoorTopology.StampOwner(w, house!, Faction.Colony);
                var bill = BlueprintBuildingPlan.Bill(BlueprintBuildingPlan.Modules(draft));
                foreach (var (id, count) in new[] { (ContentIds.Stick, bill.Sticks), (ContentIds.Board, bill.Boards),
                    (ContentIds.Rope, bill.Rope), (ContentIds.PalmLeaf, bill.Leaves) })
                    for (var i = 0; i < count; i++) house!.Contents.Add(new ItemInstance(id));
                BuildingRules.SyncHutElements(w, house!);
                Assert.That(BuildingRules.Elements(w, house!).All(e => e.Complete), Is.True);
                var raised = (WorldObjectState)typeof(ExecutionSystem).GetMethod("RaiseFurnitureSite", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [w, house, house!.Fragment, house.Junctions[0]])!;
                Assert.That(BuildingRules.FloorComplete(w, raised), Is.True);
                var sites = w.Entities.Objects.Values.Where(o => o.Tile.Equals(raised.Tile) &&
                    o.DefinitionId == ContentIds.BuildSite && o.BuildProduct == ContentIds.BedBasic).ToArray();
                Assert.That(sites, Has.Length.GreaterThan(0));
                foreach (var site in sites)
                {
                    Assert.That(site.Contents, Is.Empty, "Bed acceptance starts with an empty bill.");
                    bedAnchors.Add(site.Junctions[0]);
                }
                File.WriteAllText(Path.Combine(output, $"bed-layout-{fixture}-{repetition}.json"), JsonSerializer.Serialize(new
                {
                    hexRadius = HexSpatialMath.HexRadius, gridStep = HexSpatialMath.HexRadius / HexPointLayout.BoundaryRadius,
                    interiorCount = HexPointLayout.GetInteriorTemplates().Count,
                    center = HexSpatialMath.TileToWorld(raised.Tile),
                    nodes = w.Tiles.Items[raised.Tile].Junctions.Select(id => new
                    { id = id.Value, x = w.Junctions.Items[id].WorldPosition.X, y = w.Junctions.Items[id].WorldPosition.Y,
                      blocked = w.Junctions.Items[id].Blocked, door = w.Junctions.Items[id].Door, bed = bedAnchors.Contains(id) }),
                    beds = sites.Select(site => new { id = site.Id.Value, blocked = site.BlockedJunctions.Count })
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                for (var i = 0; i < 8; i++) engine.Step();
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
        using var transport = new Transport(new McpTools(host, new ControlLeases(45))) { LoseNextResponse = scenario == "resilience" };
        var targetReplaced = false;
        if (scenario == "resilience" && fixture == 1)
            transport.BeforeToolCall = (name, arguments) =>
            {
                if (targetReplaced || name != "execute_agent_command" || arguments.GetProperty("tool").GetString() != "interact") return;
                var action = arguments.GetProperty("arguments");
                if (!action.TryGetProperty("objectId", out var id) || !action.TryGetProperty("interaction", out var interaction) || interaction.GetString() != "PickUp") return;
                host.Read(w =>
                {
                    if (!w.Entities.Objects.TryGetValue(new ObjectId(id.GetInt32()), out var item) || item.DefinitionId != ContentIds.Coconut) return false;
                    // Same physical fixture location; stale identity must fail and be re-observed.
                    var anchor = item.Junctions[0];
                    WorldObjectMutations.DespawnObject(w, item.Id);
                    WorldObjectMutations.SpawnObject(w, item.DefinitionId, item.Fragment, item.Tile, anchor);
                    targetReplaced = true;
                    return true;
                });
            };
        var options = Options(); using var noModelDuringExecution = new NoModel();
        var runtime = new AgentHostRuntime(options, noModelDuringExecution);
        var store = Memory(runtime);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var reader = new AgentReferenceReader(mcp, options.MemoryDirectory, 901);
        var recall = new AgentMemoryRecall(options.MemoryDirectory);
        var catalog = await mcp.ReadToolCatalogAsync(default);
        var contract = (string)typeof(AgentHostRuntime).GetMethod("BuildActionContract", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [catalog])!;
        var turns = new List<object>();
        var calls = 0;
        var reconciliations = 0;
        var restarted = false;
        var modelDecisions = new List<object>();
        var referenceReads = new List<object>();
        var referenceOperations = new HashSet<string>();
        async Task<CompanionDecision> Decide(string trigger, string bodyJson, string context, string player,
            IReadOnlyList<string> recent, CancellationToken token)
        {
            var call = ++calls;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await (Task)typeof(AgentHostRuntime).GetMethod("MaintainPlanLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(runtime, [mcp, 901, true, token])!;
                var answer = recorded == null
                    ? await model.DecideAsync(trigger, bodyJson, context, player, recent, token)
                    : call <= recorded.Length ? recorded[call - 1].Deserialize<CompanionDecision>(AgentMemoryArchive.Json)!
                    : throw new InvalidOperationException("RecordedDecisionsExhausted");
                await (Task)typeof(AgentHostRuntime).GetMethod("MaintainPlanLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(runtime, [mcp, 901, true, token])!;
                modelDecisions.Add(new { call, elapsedMs = watch.ElapsedMilliseconds, decision = JsonSerializer.SerializeToElement(answer),
                    usage = answer.ModelUsage, body = JsonSerializer.Deserialize<JsonElement>(bodyJson) });
                await WriteCheckpointAsync("Planning", token);
                return answer;
            }
            catch (Exception ex)
            {
                modelDecisions.Add(new { call, elapsedMs = watch.ElapsedMilliseconds, error = AgentDiagnostics.FailureKind(ex) });
                throw;
            }
        }
        var passed = false;
        var multiStep = false;
        var status = "Incomplete";
        string? failureCode = null;
        var initialTick = host.Read(w => w.Tick);
        async Task WriteCheckpointAsync(string checkpointStatus, CancellationToken token)
        {
            var saved = await store.SnapshotAsync(token);
            var checkpoint = report + ".tmp";
            File.WriteAllText(checkpoint, JsonSerializer.Serialize(new
            {
                providerName, modelId, reasoningEffort, scenario, fixture, repetition, calls, decisionTurnLimit, executionWallBudgetPolicy,
                status = checkpointStatus, gameTicks = host.Read(w => w.Tick) - initialTick,
                sawSleep, commands = transport.Executions, reconciliations, targetReplaced, restarted,
                objective = saved.Objective, executionPlan = saved.ExecutionPlan, executionProgress = saved.ExecutionProgress,
                referenceReads, receipts = host.Read(w => w.AgentCommands.GetValueOrDefault(901)?.Receipts), modelDecisions, turns
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(checkpoint, report, true);
        }

        void AdvanceObservationClock(bool heartbeat)
        {
            var ticks = heartbeat ? Math.Max(8, (int)Math.Ceiling(options.HeartbeatSeconds / host.TickDeltaTime)) : 8;
            host.Read(w =>
            {
                for (var i = 0; i < ticks; i++)
                {
                    engine.Step();
                    if (w.Tick - initialTick > 3 * EnvironmentSystem.DayLengthTicks)
                        throw new InvalidOperationException("ThreeGameDaysExceeded");
                }
                return true;
            });
        }
        try
        {
            var world = await store.BindHexLiveWorldAsync(await mcp.CallToolAsync("world_status", new { }, default), 901, "fixture", default);
            var prompt = await store.BuildPromptContextAsync(world, "", default);
            world = world with { ObjectiveRevision = prompt.ObjectiveRevision, ExecutionPlanRevision = prompt.ExecutionPlanRevision, ExecutionPlanId = prompt.ExecutionPlanId };
            await store.CommitTurnAsync(world, "request", "voice", new CompanionDecision
            { ObjectiveUpdate = new() { Operation = "set", Text = task, Reason = "Приказ игрока" } }, default);
            for (var turn = 0; turn < decisionTurnLimit; turn++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(330));
                world = await store.BindHexLiveWorldAsync(await mcp.CallToolAsync("world_status", new { }, timeout.Token), 901, "fixture", timeout.Token);
                typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, world);
                prompt = await store.BuildPromptContextAsync(world, task, timeout.Token);
                world = world with { ObjectiveRevision = prompt.ObjectiveRevision, ExecutionPlanRevision = prompt.ExecutionPlanRevision, ExecutionPlanId = prompt.ExecutionPlanId };
                var body = await mcp.CallToolAsync("describe_colonist", new { npcId = 901 }, timeout.Token);
                var state = await store.SnapshotAsync(timeout.Token);
                var context = prompt.Text + "\nAvailable MCP actions:\n" + contract +
                    AgentPromptFiles.Text("AgentHostRuntime.05") + GameplayActionFeedback(runtime);
                var queriedObjects = false;
                var decision = await recall.DecideAsync(turn == 0 ? task : "", world, state, async (evidence, token) =>
                {
                    var requestContext = context + "\n" + evidence +
                        (queriedObjects ? "\n" + AgentPromptFiles.Read("object-knowledge.md") : "");
                    var answer = await Decide(turn == 0 ? "voice" : "heartbeat", body.GetRawText(), requestContext,
                        turn == 0 ? task : "", turn == 0 ? [] : ["Игрок: " + task], token);
                    if (answer.Action?.Tool != "query_known_objects") return answer;
                    if (queriedObjects) throw new AgentActionValidationException("KnowledgeQueryLimitReached");
                    queriedObjects = true;
                    JsonElement known;
                    try
                    {
                        var args = new AgentActionContract(catalog).BindAndValidate(answer.Action, 901);
                        known = await mcp.CallToolAsync("query_known_objects", args, token);
                    }
                    catch (Exception ex) when (ex is AgentActionValidationException or McpToolRejectedException)
                    {
                        known = JsonSerializer.SerializeToElement(new { error = "KnowledgeReadUnavailable", npcId = 901 });
                    }
                    var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body.GetRawText())!;
                    fields["knownObjectQuery"] = known;
                    body = JsonSerializer.SerializeToElement(fields);
                    return await Decide(turn == 0 ? "voice" : "heartbeat", body.GetRawText(),
                        requestContext + "\n" + AgentPromptFiles.Read("object-knowledge.md"),
                        turn == 0 ? task : "", turn == 0 ? [] : ["Игрок: " + task], token);
                }, timeout.Token, async (operation, arguments, token) =>
                {
                    var read = await reader.ReadAsync(operation, arguments, token);
                    referenceOperations.Add(operation);
                    referenceReads.Add(new { operation, arguments, read.SourceIds, response = read.Text });
                    return read;
                }, (answer, validationToken) => store.ValidateObjectiveUpdateAsync(world, answer, validationToken));
                AgentExecutionPlanPolicy.NormalizeContinuation(decision, state, world.WorldKey, 901);
                turns.Add(new { turn, tick = host.Read(w => w.Tick), decision, usage = decision.ModelUsage });
                await WriteCheckpointAsync(status, timeout.Token);
                var emergency = decision.Action != null &&
                    (decision.ObjectiveUpdate?.Operation == "pause" || state.Objective?.Status == "paused");
                if (decision.Action != null && !emergency) throw new InvalidOperationException("ModelDidNotProduceExecutionPlan");
                await store.CommitTurnAsync(world, "model-" + turn, turn == 0 ? "voice" : "heartbeat", decision, timeout.Token);
                var saved = await store.SnapshotAsync(timeout.Token);
                Assert.That(saved.Objective?.Text, Is.EqualTo(task), "Recovery must preserve the original objective.");
                var delivered = host.Read(w => w.Entities.Objects.Values.Count(o => o.DefinitionId == ContentIds.Coconut &&
                    o.ProduceOrigin == ProduceOrigin.Gathered && ColonyQueries.InCamp(w, o.Tile, Faction.Colony)));
                if (saved.Objective?.Status == "completed")
                {
                    passed = delivery ? delivered == 3 && multiStep : scenario == "bed"
                        ? multiStep && sawSleep && host.Read(w => w.Tick - initialTick <= 3 * EnvironmentSystem.DayLengthTicks &&
                            w.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.BedBasic && o.Junctions.Any(bedAnchors.Contains)))
                        : saved.ExecutionProgress.Any(p => p.Step.Tool == "interact") && host.Read(w =>
                        w.Entities.Npcs[new EntityId(recipientId)].Inventory.Items.Count > recipientItemsBefore &&
                        w.Events.Items.Count(e => e.Type == "GiftGiven" && e.Message.Contains($"->NPC{recipientId} ")) == 1);
                    var grounded = scenario == "bed"
                        ? referenceOperations.Contains("build.read") && referenceOperations.Contains("recipes.read") &&
                          saved.ExecutionProgress.Any(p => p.Step.Tool == "interact" && p.Step.Arguments.TryGetProperty("interaction", out var verb) && verb.GetString() == "Build")
                        : scenario != "gift" || referenceOperations.Contains("spec.read");
                    status = !passed ? "FalseCompletion" : grounded ? "Completed" : "MissingRuleConsultation";
                    passed &= grounded;
                    if (scenario == "resilience")
                    {
                        passed &= reconciliations > 0 && host.Read(w => w.AgentCommands[901].Receipts.Count == transport.Executions);
                        passed &= fixture != 1 || targetReplaced;
                        passed &= fixture != 2 || restarted;
                        if (!passed && status == "Completed") status = "ReconciliationNotVerified";
                    }
                    break;
                }
                var plan = saved.ExecutionPlan;
                if (!emergency && plan is not { Status: "active" }) { AdvanceObservationClock(true); continue; }
                if (!emergency) multiStep |= plan!.Steps.Length >= 2;
                // Native polling takes two wall seconds even with an accelerated world.
                // A valid 256-dispatch segment must not fail the old 90-second harness cap.
                var executionSeconds = emergency ? 90 : Math.Max(90, 30 + 4 * plan!.Steps.Sum(step => step.Repeat));
                using var executionStop = new CancellationTokenSource(TimeSpan.FromSeconds(executionSeconds));
                var running = emergency
                    ? RunGameplayEmergencyAsync(runtime, mcp, decision.Action!, executionStop.Token, "emergency-" + turn)
                    : Run(runtime, mcp, plan!.Id, world, executionSeconds, executionStop.Token);
                var exceeded = false;
                while (true)
                {
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
                  AdvanceObservationClock(saved.Objective?.Status == "paused");
                  var afterExecution = (await store.SnapshotAsync(executionStop.Token)).ExecutionPlan;
                  if (scenario != "resilience" || emergency || afterExecution is not { Status: "paused", Reason: "CommandOutcomeUnknown" } || reconciliations >= 3) break;
                  var callsBeforeRecovery = calls;
                  await Task.Delay(2100, executionStop.Token);
                  if (fixture == 2 && !restarted)
                  {
                      runtime = new AgentHostRuntime(options, noModelDuringExecution);
                      store = Memory(runtime);
                      restarted = true;
                  }
                  typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, world);
                  await (Task)typeof(AgentHostRuntime).GetMethod("TryStartExecutionPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                      .Invoke(runtime, [mcp, 901, executionStop.Token])!;
                  running = (Task)typeof(AgentHostRuntime).GetField("_actionTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
                  reconciliations++;
                  Assert.That(calls, Is.EqualTo(callsBeforeRecovery));
                }
                if (exceeded) throw new InvalidOperationException("ThreeGameDaysExceeded");
            }
            if (!passed && status == "Incomplete") status = "DecisionTurnLimitExceeded";
            if (fixture == 2)
                Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Hammer)), Is.True);
            Assert.That(noModelDuringExecution.Calls, Is.Zero);
            Assert.That(passed, Is.True, status + "; inspect " + report);
        }
        catch (Exception ex)
        {
            failureCode = ex is InvalidDataException or InvalidOperationException &&
                ex.Message.Length is > 0 and <= 96 &&
                ex.Message.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
                    ? ex.Message : AgentDiagnostics.FailureKind(ex);
            status = ex.GetType().Name + ":" + status;
            throw;
        }
        finally
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { providerName, modelId, reasoningEffort, scenario, fixture, repetition, calls, decisionTurnLimit, executionWallBudgetPolicy, passed, status, failureCode,
                gameTicks = host.Read(w => w.Tick) - initialTick, sawSleep, commands = transport.Executions, reconciliations, targetReplaced, restarted,
                emergencyActions = turns.Count(t => JsonSerializer.SerializeToElement(t).GetProperty("decision").TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.Object),
                referenceReads, receipts = host.Read(w => w.AgentCommands.GetValueOrDefault(901)?.Receipts), modelDecisions, turns }, new JsonSerializerOptions { WriteIndented = true }));
            (adapter as IDisposable)?.Dispose();
        }
    }

    private static string GameplayActionFeedback(AgentHostRuntime runtime) =>
        (string)typeof(AgentHostRuntime).GetField("_actionFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;

    private static Task RunGameplayEmergencyAsync(AgentHostRuntime runtime, McpClient mcp,
        CompanionAction action, CancellationToken token, string turnId) =>
        (Task)typeof(AgentHostRuntime).GetMethod("PerformActionSafelyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [mcp, 901, action, token, turnId])!;
}
