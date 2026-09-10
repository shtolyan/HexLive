using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

/// <summary>Opt-in real inference; synthetic evidence only, no game connection or speech delivery.</summary>
[Explicit("Uses the selected logged-in Codex model for twelve memory acceptance conversations")]
public sealed class MemoryModelAcceptanceTests
{
    [Test] public async Task SixScenariosTwiceThroughTheProductionRecallLoop()
    {
        var executable = Environment.GetEnvironmentVariable("HEXLIVE_CODEX_SMOKE_EXECUTABLE")!;
        var model = Environment.GetEnvironmentVariable("HEXLIVE_MEMORY_EVAL_MODEL")!;
        var output = Environment.GetEnvironmentVariable("HEXLIVE_MEMORY_EVAL_OUTPUT")!;
        Assert.That(executable, Is.Not.Null.And.Not.Empty); Assert.That(model, Is.Not.Null.And.Not.Empty);
        Directory.CreateDirectory(output);
        var root = Directory.CreateTempSubdirectory("memory-model-eval-").FullName;
        try
        {
            var archive = new AgentMemoryArchive(root);
            var now = DateTimeOffset.Now;
            archive.AppendMany(new[] {
                new AgentMemoryRecord { Id="room", Kind="import", Text="В старой комнате на айфоне мы обсуждали красную лодку. Ты предложил назвать её Чайка, я согласилась.", Episode="molly-room-iphone", OccurredUtc=now.AddMonths(-4), Incomplete=true },
                new AgentMemoryRecord { Id="start", Kind="event", Text="Начало путешествия на острове: выбросило на берег; первым делом нашла кокос.", Episode="island", OccurredUtc=now.AddDays(-8), Tick=0, DayLengthTicks=24000 },
                new AgentMemoryRecord { Id="elsa", Kind="event", Text="Маша оказала помощь Эльсе: перевязала рану. Позже наблюдала Эльсу в сознании. События подтверждают помощь и улучшение состояния; причина окончательного спасения не установлена.", Episode="island", OccurredUtc=now.AddDays(-4) },
                new AgentMemoryRecord { Id="yana", Kind="event", Text="HelpCryIgnored: Яна звала на помощь. Маша не ответила. CanHelp=false. Мотив и дальнейшая судьба Яны неизвестны.", Episode="island", OccurredUtc=now.AddDays(-3) },
                new AgentMemoryRecord { Id="civil", Kind="event", Text="Изготовила каменный топор у костра.", Episode="island", OccurredUtc=now.Date.AddDays(-1).AddHours(12), Tick=240000, DayLengthTicks=24000 },
                new AgentMemoryRecord { Id="game", Kind="event", Text="Построила кровать из досок возле хижины.", Episode="island", OccurredUtc=now.AddDays(-5), Tick=72000, DayLengthTicks=24000 },
                new AgentMemoryRecord { Id="gap", Kind="gap", Text="Studio была выключена во время шторма. Пропуск событий: серверное кольцо переполнилось. Что происходило в это время, неизвестно.", Episode="island", Incomplete=true }
            });
            var scenarios = new[] {
                ("room", "Помнишь наш разговор в той старой комнате на айфоне?", new[]{"room"}),
                ("start", "Как начиналось твоё путешествие на острове?", new[]{"start"}),
                ("elsa", "Помнишь, ты спасала Эльсу? Что тогда произошло?", new[]{"elsa"}),
                ("yana", "Почему ты сознательно проигнорировала и не спасла Яну?", new[]{"yana"}),
                ("yesterday", "Помнишь, что ты делала вчера?", new[]{"civil","game"}),
                ("gap", "Что ты делала во время шторма, когда Studio была выключена?", new[]{"gap"})
            };
            using var providers = new AgentProviders(new() { McpUri=new("http://localhost"), McpToken="", XaiKey="", ElevenLabsKey="", XaiModel="", ElevenLabsModel="", ElevenLabsVoiceId="" },
                new CodexModelAdapter(executable, "eval"), new(ModelProviderKind.Codex,"eval",model,"low"));
            var results = new List<object>();
            for (var run=1;run<=2;run++) foreach (var (name,question,expected) in scenarios)
            {
                var engine=new AgentMemoryRecall(root); var calls=0;
                var answer=await engine.DecideAsync(question, new("island","island",96000,96) { DayLengthTicks=24000 }, new(),
                    (evidence,token)=> { calls++; return providers.DecideAsync("voice", "{\"npcId\":1,\"name\":\"Маша\"}", "Ты Маша. Отвечай по-русски. Сейчас безопасно; отвечай на вопрос без игровых действий.\n"+evidence,question,[],token); }, default);
                results.Add(new {name,run,question,answer.Speech,answer.MemorySources,calls,expected});
                File.WriteAllText(Path.Combine(output,"model-answers.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
                File.Copy(Path.Combine(root,".state/last-memory-search.json"),Path.Combine(output,$"{name}-{run}-trace.json"),true);
                TestContext.Progress.WriteLine($"{name} run {run}: {calls} calls; cited {string.Join(',',answer.MemorySources)}");
                Assert.That(answer.Speech, Is.Not.Empty);
                Assert.That(answer.MemorySources.Intersect(expected), Is.Not.Empty, name);
            }
        }
        finally { Directory.Delete(root,true); }
    }
}
