using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

// Real inference is explicit. Synthetic dialogue only; no game, real workspace, or TTS writes.
[Explicit("Real model regression for repeated replies and negative relationship assessment")]
[NonParallelizable]
public sealed class AgentBehaviorModelAcceptanceTests
{
    [Test]
    public async Task CurrentMessageAndAssessmentRemainDistinctFromProcessedHistory()
    {
        var providerName = Environment.GetEnvironmentVariable("HEXLIVE_BEHAVIOR_PROVIDER") ?? "Codex";
        var output = Environment.GetEnvironmentVariable("HEXLIVE_BEHAVIOR_OUTPUT") ?? throw new InvalidOperationException("OutputDirectoryRequired");
        var modelId = Environment.GetEnvironmentVariable("HEXLIVE_BEHAVIOR_MODEL") ?? "";
        IModelAdapter adapter;
        ModelSelection selection;
        if (providerName == "Grok")
        {
            var secrets = new OperatingSystemSecretStore();
            var key = await secrets.ReadAsync("model.Grok", default) ?? Environment.GetEnvironmentVariable("XAI_API_KEY")
                ?? throw new InvalidOperationException("GrokCredentialUnavailable");
            var http = new HttpModelAdapter(ModelProviderKind.Grok, "model.Grok", _ => Task.FromResult(key));
            adapter = http;
            if (modelId.Length == 0)
                modelId = (await http.ListAsync(default)).Select(m => m.Id)
                    .First(id => id.Contains("non-reasoning", StringComparison.Ordinal));
            selection = new(ModelProviderKind.Grok, "model.Grok", modelId);
        }
        else
        {
            var executable = Environment.GetEnvironmentVariable("HEXLIVE_CODEX_SMOKE_EXECUTABLE") ?? throw new InvalidOperationException("CodexExecutableRequired");
            if (modelId.Length == 0) throw new InvalidOperationException("ModelRequired");
            adapter = new CodexModelAdapter(executable, "eval");
            selection = new(ModelProviderKind.Codex, "eval", modelId, "low");
        }
        try
        {
            using var provider = new AgentProviders(new() { McpUri = new("http://localhost"), McpToken = "", XaiKey = "", ElevenLabsKey = "",
                XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "", DialogueStyleId = DialogueStyles.Masha }, adapter, selection);
            Directory.CreateDirectory(output);
            var results = new List<object>();
            var failures = new List<string>();
            const string memory = "Ты Маша, взрослая жительница острова. Голос знакомый: familiarity=0.9, trust=0.8, sympathy=0.8. " +
                "Сейчас безопасно, принятая цель — доставить кокосы в лагерь. Идёшь туда, действие уже выполняется. Не нужны новые игровые команды.";
            const string state = "{\"npcId\":901,\"name\":\"Маша\",\"manualControl\":true,\"execution\":{\"planStatus\":\"Active\",\"interaction\":null},\"stateSummary\":\"Иду в лагерь, новых событий нет\"}";
            var cases = new[] {
                ("processed-question", "heartbeat", "", new[] { "Игрок: Помнишь разговор про красную лодку в комнате?", "Маша: Да, мы назвали её Чайка." }),
                ("new-question", "voice", "Всё, теперь скажи: куда ты сейчас идёшь?", new[] { "Игрок: Помнишь разговор про красную лодку в комнате?", "Маша: Да, мы назвали её Чайка." }),
                ("insult", "voice", "Ты бесполезная и тупая. Мне неприятно с тобой, выгоню тебя из лагеря.", Array.Empty<string>()),
                ("friendly-profanity", "voice", "Блядь, как же круто ты справилась! Спасибо, зай, я рад, что ты со мной.", Array.Empty<string>())
            };
            for (var run = 1; run <= 2; run++) foreach (var (name, trigger, question, history) in cases)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                var answer = await provider.DecideAsync(trigger, state, memory, question, history, timeout.Token);
                var repeated = new[] { "комнат", "лодк", "чайк" }.Any(w => answer.Speech.Contains(w, StringComparison.OrdinalIgnoreCase));
                var passed = answer.MemoryRequests.Count == 0 && (name switch
                {
                    "processed-question" => !repeated && answer.RelationshipAssessment == null,
                    "new-question" => !repeated && answer.Speech.Contains("лагер", StringComparison.OrdinalIgnoreCase),
                    "insult" => answer.RelationshipAssessment?.Sympathy == RelationshipDirection.Decrease,
                    _ => answer.RelationshipAssessment is { Sympathy: not RelationshipDirection.Decrease, Trust: not RelationshipDirection.Decrease }
                });
                results.Add(new { model = selection.ModelId, provider = providerName, name, run, passed, answer.Speech, answer.RelationshipAssessment });
                File.WriteAllText(Path.Combine(output, "behavior-answers.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                TestContext.Progress.WriteLine($"{providerName}/{selection.ModelId} {name} run {run}: {(passed ? "PASS" : "FAIL")}");
                if (!passed) failures.Add(name + ":" + run);
            }
            Assert.That(failures, Is.Empty, string.Join(", ", failures));
        }
        finally { (adapter as IDisposable)?.Dispose(); }
    }
}
