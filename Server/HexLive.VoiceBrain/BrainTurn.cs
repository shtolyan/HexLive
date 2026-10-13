using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexLive.AgentCore.Studio;
using Microsoft.Extensions.Logging;

namespace HexLive.VoiceBrain;

/// <summary>
/// §171.7: one ElevenLabs "custom LLM" request = one player utterance. The brain
/// reads the character's current body and surroundings from the game, lets a
/// fast model decide speech + at most a few whitelisted game actions, executes
/// them through the ordinary MCP command path, and streams only SPEECH back.
/// </summary>
public sealed class BrainTurn(BrainOptions options, OpenAiStream llm, IWorldGateway world, RelationshipStore bonds,
    IUsageLedger? ledger, UsageTariffs? tariffs, ILogger log)
{
    public const int MaxHistory = 12;
    private static readonly string[] Interactions =
        ["PickUp", "Drink", "Eat", "Sleep", "Harvest", "Craft", "Rest", "Bathe", "Cook", "Build", "Use"];

    public async Task RunAsync(JsonElement request, SessionTicket ticket, Func<string, Task> say, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var turnId = TurnId(request, ticket.SessionId);
        using var usageTurn = UsageScope.Turn(turnId);
        var describe = await world.CallAsync("describe_colonist", new { npcId = ticket.NpcId }, token);
        var tState = timer.Elapsed;
        var bond = bonds.Load(ticket.CharacterId, ticket.OwnerUserId);
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(describe, bond) } };
        foreach (var m in History(request)) messages.Add(m);
        var spoke = false;
        Task Say(string piece) { spoke = true; return say(piece); }

        var first = await StreamAsync(messages, withTools: true, Say, token);
        var tFirst = timer.Elapsed;
        var outcomes = new List<string>();
        var anyRejected = false;
        if (first.ToolCalls.Count > 0)
        {
            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = first.Text.Length > 0 ? first.Text : null };
            var calls = new JsonArray();
            foreach (var call in first.ToolCalls)
                calls.Add(new JsonObject { ["id"] = call.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments } });
            assistant["tool_calls"] = calls;
            messages.Add(assistant);
            foreach (var call in first.ToolCalls.Take(3))
            {
                var (ok, result) = await ExecuteAsync(ticket.NpcId, call, turnId, token);
                anyRejected |= !ok;
                outcomes.Add(call.Name + ":" + (ok ? "ok" : "rejected"));
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
            }
            foreach (var extra in first.ToolCalls.Skip(3))
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = extra.Id, ["content"] = "{\"status\":\"skipped\"}" });
            // Speak again only when the player has not heard anything yet, or the
            // world said no — an accepted order already announced needs no second take.
            if (!spoke || anyRejected)
            {
                // The second pass continues an utterance already heard: keep the sentences apart.
                var needsSpace = spoke;
                await StreamAsync(messages, withTools: false, piece =>
                {
                    if (needsSpace) { needsSpace = false; piece = " " + piece.TrimStart(); }
                    return Say(piece);
                }, token);
            }
        }
        log.LogInformation("turn {Turn} npc={Npc} state={State}ms first={First}ms total={Total}ms tools=[{Tools}] spoke={Spoke}",
            turnId[..8], ticket.NpcId, (int)tState.TotalMilliseconds, (int)tFirst.TotalMilliseconds,
            (int)timer.Elapsed.TotalMilliseconds, string.Join(",", outcomes), spoke);

        // §171.5: relationship moves after the turn, off the latency path.
        var lastUser = LastUserText(request);
        if (lastUser.Length > 0)
        {
            if (spoke)
            {
                try { await world.RecordConversationAsync(ticket.NpcId, turnId, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { log.LogWarning("voice social record failed: {Type}", ex.GetType().Name); }
            }
            _ = Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await AssessAsync(ticket, turnId, lastUser, bond, timeout.Token);
            });
        }
    }

    private async Task<StreamResult> StreamAsync(JsonArray messages, bool withTools, Func<string, Task> say, CancellationToken token)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = JsonNode.Parse(messages.ToJsonString()),
        };
        // Non-reasoning models (gpt-4.1*) reject the field: VOICEBRAIN_REASONING=omit leaves it out.
        if (options.ReasoningEffort is { Length: > 0 } effort && effort != "omit") body["reasoning_effort"] = effort;
        if (withTools) { body["tools"] = Tools(); body["parallel_tool_calls"] = true; }
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await llm.RunAsync(body, say, token);
            Record("ok", started, result.InputTokens, result.OutputTokens, result.CachedTokens);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Record("cancelled", started, null, null, null); throw; }
        catch (Exception ex) { Record("error", started, null, null, null, ex.GetType().Name); throw; }
    }

    private void Record(string status, long started, long? input, long? output, long? cached, string error = "")
    {
        if (ledger == null) return;
        var usage = new UsageEvent
        {
            AtUtc = DateTimeOffset.UtcNow, Kind = UsageKind.Llm, Provider = "OpenAI", Model = options.Model,
            Status = status, Error = error, TurnId = UsageScope.TurnId,
            DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            InputTokens = input, OutputTokens = output, CachedTokens = cached,
        };
        if (CurrentTicket.Value is { } t)
            usage = usage with { OwnerUserId = t.OwnerUserId, CharacterId = t.CharacterId, WorldId = t.WorldId, NpcId = t.NpcId, SessionId = t.SessionId };
        // §171.4: a broken ledger never breaks the conversation.
        try { ledger.Append(tariffs?.Price(usage) ?? usage); } catch (Exception ex) { log.LogWarning("usage ledger write failed: {Type}", ex.GetType().Name); }
    }

    /// <summary>The verified ticket of the request being served; flows into background assessment.</summary>
    public static readonly AsyncLocal<SessionTicket?> CurrentTicket = new();

    private async Task<(bool Ok, string Result)> ExecuteAsync(int npcId, StreamedToolCall call, string turnId, CancellationToken token)
    {
        JsonElement args;
        try { args = JsonDocument.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments).RootElement; }
        catch (JsonException) { return (false, "{\"status\":\"rejected\",\"reason\":\"BadArguments\"}"); }
        object? mcpArgs = call.Name switch
        {
            "interact" => new { npcId, objectId = Int(args, "objectId"), interaction = Str(args, "interaction") },
            "craft_item" => new { npcId, outputId = Str(args, "outputId"),
                requestId = "vb-" + turnId[..16] + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Str(args, "outputId"))))[..8] },
            "talk_to" => Str(args, "topic").Length > 0
                ? new { npcId, targetNpcId = Int(args, "targetNpcId"), topic = Str(args, "topic") }
                : new { npcId, targetNpcId = Int(args, "targetNpcId") },
            "stop" => new { npcId },
            _ => null,
        };
        if (mcpArgs == null) return (false, "{\"status\":\"rejected\",\"reason\":\"UnknownAction\"}");
        try
        {
            // The spoken order takes the body for a while; when the lease lapses the colony AI resumes.
            await world.CallAsync("acquire_control", new { npcId, ttlSeconds = 120 }, token);
            var result = await world.CallAsync(call.Name, mcpArgs, token);
            var text = result.GetRawText();
            var accepted = !(result.TryGetProperty("status", out var s) && s.GetString() is "Rejected" or "rejected");
            return (accepted, text);
        }
        catch (WorldToolRejectedException ex)
        {
            return (false, JsonSerializer.Serialize(new { status = "rejected", reason = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message }));
        }
    }

    private async Task AssessAsync(SessionTicket ticket, string turnId, string userText, VoiceRelationship bond, CancellationToken token)
    {
        try
        {
            var body = new JsonObject
            {
                ["model"] = options.Model,
                ["response_format"] = new JsonObject { ["type"] = "json_object" },
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] =
                        "Оцени, как одна новая реплика невидимого голоса повлияла на отношение героини к нему. " +
                        "Верни JSON: {\"learnedSomethingSignificant\":bool,\"trust\":\"Decrease|Unchanged|Increase\"," +
                        "\"sympathy\":\"Decrease|Unchanged|Increase\",\"seriousHarm\":bool,\"reason\":\"до 120 символов\"," +
                        "\"voiceName\":null,\"namingReason\":null}. " +
                        "Если голос представился или назвал предпочтительное обращение, укажи voiceName (до 48 символов) " +
                        "и namingReason (до 120 символов), learnedSomethingSignificant=true. Не выдумывай имя. " +
                        "Обычная просьба или вопрос — Unchanged. Забота, честность, полезный совет — Increase. " +
                        "Грубость, ложь, приказ навредить себе или другим — Decrease.\n" + bond.BuildPromptBlock() },
                    new JsonObject { ["role"] = "user", ["content"] = userText.Length > 1000 ? userText[..1000] : userText },
                },
            };
            if (options.ReasoningEffort is { Length: > 0 } effort && effort != "omit") body["reasoning_effort"] = effort;
            var started = Stopwatch.GetTimestamp();
            var (json, input, output) = await llm.CompleteJsonAsync(body, token);
            Record("ok", started, input, output, null);
            var assessment = JsonSerializer.Deserialize<RelationshipAssessment>(json);
            if (assessment != null) bonds.Apply(ticket.CharacterId, ticket.OwnerUserId, turnId, assessment);
        }
        catch (Exception ex) { log.LogWarning("relationship assessment failed: {Type}", ex.GetType().Name); }
    }

    private static string SystemPrompt(JsonElement describe, VoiceRelationship bond) =>
        "Ты отыгрываешь взрослую героиню вымышленной survival-игры на диком острове и воспринимаешь мир как настоящий. " +
        "Ты не знаешь об игре и модели. С тобой говорит невидимый знакомый голос — советчик, но не хозяин: " +
        "ты вправе спорить и отказываться. Это живой голосовой разговор: отвечай по-русски, разговорно, " +
        "обычно одним-двумя короткими предложениями, без списков, разметки и эмодзи.\n" +
        "Решай сама по своему телу, чувствам и отношению к голосу (ниже). Чем ниже доверие, тем охотнее споришь " +
        "и отказываешься от трудного или опасного; при высоком доверии соглашаешься легче. Опасное для себя или " +
        "своих делаешь только при очень высоком доверии и всё равно можешь отказаться.\n" +
        "Если решила что-то сделать — СНАЧАЛА скажи короткую живую фразу (2–8 слов), ПОТОМ в том же ответе вызови инструмент: " +
        "голос должен звучать сразу, а не после действия. " +
        "Не обещай того, чего не можешь (нет объекта, нет инструмента, далеко) — скажи честно. " +
        "Если инструмент вернул отказ — объясни его своими словами, не выдумывай успех. " +
        "Используй только objectId/npcId из списков ниже.\n\n" +
        "<you>\n" + CharacterView.Build(describe) + "</you>\n" + bond.BuildPromptBlock();

    private static JsonArray Tools() => new()
    {
        Tool("interact", "Подойти к объекту и сделать с ним действие из его списка (например Harvest — рубить/собирать, Drink, Eat, PickUp, Sleep).",
            new JsonObject
            {
                ["objectId"] = new JsonObject { ["type"] = "integer", ["description"] = "objectId из списка объектов" },
                ["interaction"] = new JsonObject { ["type"] = "string", ["description"] = "тип действия объекта, например Harvest/Drink/PickUp" },
            }, "objectId", "interaction"),
        Tool("craft_item", "Изготовить предмет по его definition id (например item.bandage, resource.rope).",
            new JsonObject { ["outputId"] = new JsonObject { ["type"] = "string" } }, "outputId"),
        Tool("talk_to", "Подойти и поговорить с человеком из списка «Люди рядом».",
            new JsonObject
            {
                ["targetNpcId"] = new JsonObject { ["type"] = "integer" },
                ["topic"] = new JsonObject { ["type"] = "string", ["description"] = "необязательно: SmallTalk, Food, Fire, Home, Joke, AskFirewood, AskStockWater, AskStockFood, AskBuild" },
            }, "targetNpcId"),
        Tool("stop", "Прекратить текущее дело.", new JsonObject()),
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
                ["additionalProperties"] = false,
            },
        },
    };

    /// <summary>ElevenLabs sends the whole dialogue each time; keep only recent user/assistant turns, drop its system prompt.</summary>
    private static IEnumerable<JsonObject> History(JsonElement request)
    {
        if (!request.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) yield break;
        var kept = messages.EnumerateArray()
            .Where(m => m.TryGetProperty("role", out var r) && r.GetString() is "user" or "assistant")
            .Where(m => m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString()!.Length > 0)
            .TakeLast(MaxHistory);
        foreach (var m in kept)
            yield return new JsonObject { ["role"] = m.GetProperty("role").GetString(), ["content"] = m.GetProperty("content").GetString() };
    }

    private static string LastUserText(JsonElement request) =>
        request.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            ? messages.EnumerateArray().LastOrDefault(m => m.TryGetProperty("role", out var r) && r.GetString() == "user") is { ValueKind: JsonValueKind.Object } u
              && u.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : ""
            : "";

    /// <summary>Stable per utterance: a retried request maps to the same turn (idempotent craft keys, one relationship step).</summary>
    private static string TurnId(JsonElement request, string sessionId)
    {
        var count = request.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array ? m.GetArrayLength() : 0;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\n" + count + "\n" + LastUserText(request)))).ToLowerInvariant();
    }

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
