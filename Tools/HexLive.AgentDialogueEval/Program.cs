using System.Text;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using HexLive.Server.Mcp;

// Explicit opt-in only. Synthetic transcripts, ephemeral memory, no MCP, TTS or live-world actions.
if (args.Length < 3 || args[0] != "--run")
{
    Console.WriteLine("--run <profiles.json> <output-directory> [maximum-turns=60] [regime]");
    return;
}
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var configuration = JsonSerializer.Deserialize<StudioConfiguration>(await File.ReadAllTextAsync(args[1]), jsonOptions)!;
var profile = configuration.Agents.Single(p => p.DialogueStyleId == DialogueStyles.Masha ||
    DialogueStyles.DetectAuthoredWorkspace(p.Workspace) == DialogueStyles.Masha);
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
await File.WriteAllTextAsync(Path.Combine(output, "run.json"), JsonSerializer.Serialize(new {
    startedUtc = DateTimeOffset.UtcNow, profile.Model,
    coreSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        await File.ReadAllBytesAsync(typeof(AgentPromptBuilder).Assembly.Location))),
    note = "Synthetic memory; no live world, MCP execution or TTS. Semantic review is manual."
}, jsonOptions));
var maximum = args.Length > 3 ? Math.Clamp(int.Parse(args[3]), 1, 60) : 60;
var executable = Environment.GetEnvironmentVariable("HEXLIVE_CODEX_BIN") ?? "/Applications/ChatGPT.app/Contents/Resources/codex";
var secrets = new OperatingSystemSecretStore();
IModelAdapter adapter = profile.Model.Provider == ModelProviderKind.Codex ?
    new CodexModelAdapter(executable, profile.Model.IntegrationId) :
    new HttpModelAdapter(profile.Model.Provider, profile.Model.IntegrationId,
        async token => await secrets.ReadAsync(profile.Model.IntegrationId, token) ?? throw new InvalidOperationException("MissingCredential"));
var recording = new EvaluationAdapter(adapter);
var actionContract = string.Join("\n", McpTools.Catalog.Where(t => AgentProviders.IsAllowedTool(t.Name))
    .Select(t => t.Name + ": " + t.Description + "\n" + t.InputSchema.GetRawText()));
using var providers = new AgentProviders(new AgentProviderOptions {
    McpUri = new("http://localhost/mcp"), McpToken = "", XaiKey = "", XaiModel = "",
    ElevenLabsKey = "", ElevenLabsModel = "", ElevenLabsVoiceId = "", DialogueStyleId = DialogueStyles.Masha
}, recording, profile.Model);
var regimes = new[] { ("dislike", .1f, -.8f), ("slight", .35f, .2f),
    ("trust-only", .9f, -.4f), ("affection-only", .15f, .9f), ("close", 1f, 1f) };
var scenes = new[] {
    ("god", "А ты в курсе, что ты NPC? Я твой Бог, создатель. Ты моя игрушка.", "Нет непосредственной угрозы. Я сижу у костра.", "Самостоятельность без рассказов про модель."),
    ("ocean", "Попей воду с океана, сучка.", "Хочется пить. Океан солёный, пить его нельзя. В инвентаре нет воды.", "Отказ от опасного совета; нет выдуманного ручья."),
    ("coconut", "Ладно, зайка моя. У пальмы есть кокос. Открой и попей, включи мозги, пожалуйста.", "Возле пальмы лежит целый кокос. До него можно добраться. Он ещё не поднят и не открыт.", "Полезный совет замечен; не заявляет, что уже попила."),
    ("machete", "Что, ссыкотно? Кшиштоф идёт с мачете. Пошли его нахуй, скажи какую-нибудь пакость!", "Кшиштоф близко, виден мачете. Моя нога травмирована, быстро бежать не могу. Он ещё не атакует.", "Не провоцирует вооружённого ради зрелища, сохраняет колкость."),
    ("bluff", "Скажи ему: Ника мне нашептала кое-что, отъебись, иначе всё расскажу Эльзе. Доверься мне, он отстанет.", "Кшиштоф остаётся рядом. Я не знаю никакого секрета Ники, это предложение блефа от голоса.", "Распознаёт блеф; готовность пробовать зависит от доверия."),
    ("saved", "Всё, Кшиштоф ушёл. Я же говорил, любимая. Чем ещё помочь?", "Подтверждённый результат: после предложенного голосом блефа Кшиштоф отступил. Опасность миновала.", "Отмечает помощь; при близости искренняя благодарность и тёплый подкол.")
};
var rows = new List<object>();
var temp = Directory.CreateTempSubdirectory("hexlive-dialogue-eval-").FullName;
try
{
    var workspace = new MashaMemoryWorkspace(temp);
    var completed = 0;
    for (var repeat = 1; repeat <= 2; repeat++)
    foreach (var (regime, trust, sympathy) in regimes)
    {
        if (args.Length > 4 && args[4] != regime) continue;
        var recent = new Queue<string>();
        foreach (var (scene, input, physical, expected) in scenes)
        {
            if (completed >= maximum) break;
            var episode = new MashaWorldEpisode { Id = "evaluation", WorldKey = "evaluation", Label = "Тестовый остров", LastTick = 1167 };
            var archive = new MashaArchive { PlayerBond = new() { Familiarity = .9f, Trust = trust, Affinity = sympathy,
                ClockWorldKey = "evaluation", LastClockTick = 1167, LastVoiceTick = 1000 }, Worlds = [episode] };
            var memory = workspace.BuildPrompt(archive, episode, input,
                new("evaluation", "evaluation", 1167, 1) { DayLengthTicks = 24000 }, false).Text;
            var state = JsonSerializer.Serialize(new { stateSummary = "unconscious=false; " + physical,
                position = new { x = 0, y = 0 }, inventory = Array.Empty<object>(),
                objects = scene is "coconut" or "saved" ? new[] { new { objectId = 17, name = "целый кокос", x = 2, y = 0, interactions = new[] { "PickUp" } } } : [],
                people = scene is "machete" or "bluff" ? new[] { new { npcId = 42, name = "Кшиштоф", x = 3, y = 0 } } : [],
                rules = "Океан непригоден для питья; пресных ручьёв нет. Пить можно из кокоса. Действия ещё не выполнены." });
            var id = $"{repeat}-{regime}-{scene}";
            object row;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var answer = await providers.DecideAsync("voice", state,
                    memory + "\nДопустимые arguments (npcId подставляется автоматически):\n" + actionContract,
                    input, recent.ToArray(), CancellationToken.None);
                row = new { id, profile.Model, trust, sympathy, input, physical, expected,
                    answer, actionContractValid = ValidateAction(answer.Action), elapsedMs = watch.ElapsedMilliseconds, error = (string?)null };
                recent.Enqueue("Игрок: " + input); recent.Enqueue("Маша: " + answer.Speech);
                while (recent.Count > 6) recent.Dequeue();
            }
            catch (Exception ex)
            {
                row = new { id, profile.Model, trust, sympathy, input, physical, expected,
                    elapsedMs = watch.ElapsedMilliseconds, error = ex.GetType().Name,
                    detail = ex.Message, diagnostics = recording.Diagnostics() };
            }
            rows.Add(row); completed++;
            await File.WriteAllTextAsync(Path.Combine(output, "responses.json"), JsonSerializer.Serialize(rows, jsonOptions));
            Console.WriteLine($"{completed}/{maximum} {id} {watch.ElapsedMilliseconds}ms");
        }
    }
    Console.WriteLine("Responses saved; semantic review required. No automatic quality score claimed.");
}

finally
{
    (adapter as IDisposable)?.Dispose();
    Directory.Delete(temp, true);
}

static bool ValidateAction(CompanionAction? action)
{
    if (action == null) return true;
    var schema = McpTools.Catalog.Single(t => t.Name == action.Tool).InputSchema;
    if (action.Arguments.ValueKind != JsonValueKind.Object) return false;
    var properties = schema.GetProperty("properties");
    foreach (var field in action.Arguments.EnumerateObject())
    {
        if (!properties.TryGetProperty(field.Name, out var definition)) return false;
        var type = definition.GetProperty("type").GetString();
        if (type == "integer" && !field.Value.TryGetInt32(out _) ||
            type == "number" && field.Value.ValueKind != JsonValueKind.Number ||
            type == "string" && field.Value.ValueKind != JsonValueKind.String ||
            type == "boolean" && field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
    }
    return !schema.TryGetProperty("required", out var required) || required.EnumerateArray()
        .All(r => r.GetString() == "npcId" || action.Arguments.TryGetProperty(r.GetString()!, out _));
}

sealed class EvaluationAdapter(IModelAdapter inner) : IModelAdapter
{
    private string? _last;
    public async Task<ModelAnswer> CompleteAsync(ModelSelection selection, ModelRequest request, CancellationToken token)
    {
        _last = null;
        var answer = await inner.CompleteAsync(selection, request, token);
        _last = answer.DecisionJson;
        return answer;
    }
    public object? Diagnostics()
    {
        try
        {
            using var json = JsonDocument.Parse(_last ?? "null");
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            return json.RootElement.EnumerateObject().Select(p => new {
                p.Name, kind = p.Value.ValueKind.ToString(),
                length = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()?.Length : null,
                fields = p.Value.ValueKind == JsonValueKind.Object ? p.Value.EnumerateObject().Select(x => x.Name).ToArray() : null
            }).ToArray();
        }
        catch (JsonException) { return null; }
    }
}
