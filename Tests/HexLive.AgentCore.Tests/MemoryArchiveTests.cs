using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class MemoryArchiveTests
{
    private string _root = "";
    private AgentMemoryArchive _archive = null!;
    [SetUp] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "studio-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); _archive = new(_root); }
    [TearDown] public void Cleanup() { Directory.Delete(_root, true); }
    private AgentMemoryRecord Row(string id, string text, string kind = "event", string speaker = "") => new() {
        Id = id, Text = text, Kind = kind, Source = "fixture", Episode = "island-one", Group = "scene-one",
        Speaker = speaker, OccurredUtc = DateTimeOffset.Parse("2026-09-09T12:00:00+07:00"), Tick = 25000, DayLengthTicks = 24000 };

    [Test] public void ArchiveSurvivesRestartAndOldLimitsWithoutDuplicates()
    {
        _archive.AppendMany(Enumerable.Range(0, 200).Select(i => Row("row" + i, "История " + i)));
        new AgentMemoryArchive(_root).Append(Row("row0", "A duplicate must never replace the source"));
        var search = new AgentMemorySearch(_root); Assert.That(search.Refresh(), Is.EqualTo(200));
        Assert.That(search.Search("История 0").Hits.Any(h => h.Record.Id == "row0"), Is.True);
    }
    [Test] public void InterruptedUtf8TailIsPreservedAndRepairedWithoutLosingCommittedRows()
    {
        _archive.Append(Row("first", "Эльса"));
        var file = Directory.GetFiles(Path.Combine(_root, "memory/archive"), "*.jsonl").Single();
        using (var output = new FileStream(file, FileMode.Append)) output.Write(new byte[] { 0x7b, 0xd0 });
        var search = new AgentMemorySearch(_root); Assert.That(search.Refresh(), Is.EqualTo(1));
        new AgentMemoryArchive(_root).Append(Row("second", "Яна"));
        Assert.That(search.Refresh(), Is.EqualTo(2));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.partial"), Has.Length.EqualTo(1));
    }
    [Test] public void CorruptIndexCanBeRebuiltAndDocumentEditsInvalidateOldSources()
    {
        File.WriteAllText(Path.Combine(_root, "MEMORY.md"), "Красная лодка у берега");
        var search = new AgentMemorySearch(_root); search.Refresh();
        var old = search.Search("лодка").Hits.Single().Record.Id;
        foreach (var file in Directory.GetFiles(Path.Combine(_root, ".state/search"), "*.json")) File.WriteAllText(file, "broken");
        search = new(_root); Assert.That(search.Refresh(), Is.EqualTo(1));
        File.WriteAllText(Path.Combine(_root, "MEMORY.md"), "Синий катер в бухте");
        search.Refresh(); Assert.That(search.Read(old).SourceIds, Is.Empty);
        Assert.That(search.Search("катер").Hits, Has.Length.EqualTo(1));
    }
    [Test] public void ReadAndSearchEnforceSpeakerAndAuthorSuppression()
    {
        _archive.AppendMany([Row("a", "секрет первого голоса", "conversation", "a"), Row("b", "секрет второго голоса", "conversation", "b"), Row("old", "старый секрет", "import", "legacy")]);
        var state = new MashaArchive { PrimarySpeakerKey = "a" };
        state.SuppressedMemoryValues.Add("a\nсекрет первого голоса");
        bool Allow(AgentMemoryRecord r) => AgentMemoryRecall.Allowed(r, "a", state);
        var search = new AgentMemorySearch(_root); search.Refresh();
        Assert.That(search.Search("секрет", allowed: Allow).Hits.Select(h => h.Record.Id), Is.EqualTo(new[] { "old" }));
        Assert.That(search.Read("b", allowed: Allow).SourceIds, Is.Empty);
        Assert.That(search.Read("a", allowed: Allow).SourceIds, Is.Empty);
    }
    [Test] public void CivilAndGameCalendarAreSeparateAndUnknownDatesStayUnknown()
    {
        _archive.AppendMany([Row("civil", "вчера") with { Tick = 99000 }, Row("game", "вчера") with { OccurredUtc = DateTimeOffset.Parse("2026-09-01T00:00:00Z") }, Row("unknown", "вчера") with { OccurredUtc = null, Tick = null }]);
        var search = new AgentMemorySearch(_root); search.Refresh();
        var civil = search.Search("", new(From: DateTimeOffset.Parse("2026-09-09T00:00:00+07:00"), Until: DateTimeOffset.Parse("2026-09-10T00:00:00+07:00")));
        Assert.That(civil.Hits.Select(h => h.Record.Id), Is.EqualTo(new[] { "civil" }));
        Assert.That(search.Search("", new(GameDay: 1)).Hits.Select(h => h.Record.Id), Is.EqualTo(new[] { "game" }));
    }
    [Test] public void ImportedConversationsAndRoomAreSearchableWithoutThoughts()
    {
        var dir = Path.Combine(_root, "memory/imports/molly-iphone/diary"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "2026-04-03.md"), "[10:00] Player said: Мы обсуждали в комнате красную лодку\n[10:01] I said: Я хотела отправиться в море\n[thought] SECRET_REASONING");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dir)!, "conversations.md"), "Player: Полная реплика о кокосах\nMasha: Полный ответ о кокосах");
        var search = new AgentMemorySearch(_root); search.Refresh();
        var hit = search.Search("комната лодка").Hits.First();
        Assert.That(search.Read(hit.Record.Id).Text, Does.Contain("красную лодку"));
        Assert.That(search.Search("кокос").Hits, Is.Not.Empty);
        Assert.That(search.Search("SECRET_REASONING").Hits, Is.Empty);
        Assert.That(hit.Record.Incomplete, Is.True);
    }
    [Test] public async Task RecallCanRephraseReadAndOnlyReturnFinalEffects()
    {
        _archive.Append(Row("rescue", "Эльса: оказала помощь, перевязала рану; результат подтверждён"));
        var engine = new AgentMemoryRecall(_root); var calls = 0;
        var final = await engine.DecideAsync("Помнишь прошлое?", new("island-one", "world", 50000, 50), new(), (context, token) => {
            calls++;
            if (calls == 1) return Task.FromResult(new CompanionDecision { MemoryRequests = [new() { Operation = "memory.search", Arguments = JsonSerializer.SerializeToElement(new { query = "Эльса" }) }] });
            if (calls == 2) return Task.FromResult(new CompanionDecision { MemoryRequests = [new() { Operation = "memory.read", Arguments = JsonSerializer.SerializeToElement(new { sourceId = "rescue" }) }] });
            Assert.That(context, Does.Contain("rescue"));
            return Task.FromResult(new CompanionDecision { Speech = "Помню помощь Эльсе.", MemorySources = ["rescue", "invented"] });
        }, default);
        Assert.That(calls, Is.EqualTo(3)); Assert.That(final.MemorySources, Is.EqualTo(new[] { "rescue" }));
        Assert.That(File.ReadAllText(Path.Combine(_root, ".state/last-memory-search.json")), Does.Contain("sentSourceIds"));
    }
    [Test] public void LinkedFilesCannotEscapeTheWorkspace()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix symlink fixture");
        File.CreateSymbolicLink(Path.Combine(_root, "MEMORY.md"), "/etc/hosts");
        Assert.Throws<InvalidDataException>(() => new AgentMemorySearch(_root).Refresh());
    }

    [Test] public void AggregateViewsCannotReintroduceAnotherSpeakersPrivateJournal()
    {
        Directory.CreateDirectory(Path.Combine(_root,"memory/daily"));
        File.WriteAllText(Path.Combine(_root,"memory/daily/2026-09-09.md"),"Чужой секрет");
        File.WriteAllText(Path.Combine(_root,"MEMORY.md"),MemoryDocumentEdits.Start+"\nЧужой секрет\n"+MemoryDocumentEdits.End+"\nМоя авторская заметка");
        var search=new AgentMemorySearch(_root); search.Refresh();
        Assert.That(search.Search("секрет").Hits,Is.Empty);
        Assert.That(search.Search("авторская заметка").Hits,Is.Not.Empty);
    }

    [Test] public void LongEvidenceIsPagedAsValidJsonWithinTheReadBudget()
    {
        var text=string.Concat(Enumerable.Repeat("Эльса сказала: \"привет\"\n",1000));
        _archive.Append(Row("long",text));
        var search=new AgentMemorySearch(_root); search.Refresh();
        var reconstructed=new StringBuilder(); int? offset=0;
        do {
            var page=search.Read("long",offset.Value);
            Assert.That(page.Text.Length,Is.LessThanOrEqualTo(8000));
            using var doc=JsonDocument.Parse(page.Text);
            reconstructed.Append(doc.RootElement.GetProperty("text").GetString());
            offset=page.NextOffset;
        } while(offset!=null);
        Assert.That(reconstructed.ToString(),Is.EqualTo(text));
    }

    [Test] public void IntermediateProviderResponseCannotCarrySideEffects()
    {
        var answer=AgentProviders.ParseDecision("""
        {"speech":"Не отправлять","emotion":"warm","reaction":"Neutral","relationshipAssessment":null,
        "action":{"tool":"move_to","arguments":{}},"intentSummary":"Не сохранять","journalText":"Не сохранять",
        "memoryUpserts":[{"key":"core:bad","value":"Не сохранять","importance":1}],"memorySources":[],
        "memoryRequests":[{"operation":"memory.search","arguments":{"query":"Эльса"}}]}
        ""","voice");
        Assert.Multiple(()=> {
            Assert.That(answer.Speech,Is.Empty); Assert.That(answer.Action,Is.Null);
            Assert.That(answer.RelationshipAssessment,Is.Null); Assert.That(answer.Reaction,Is.EqualTo("None"));
            Assert.That(answer.MemoryUpserts,Is.Empty); Assert.That(answer.JournalText,Is.Empty);
            Assert.That(answer.IntentSummary,Is.Empty);
        });
    }

    [Test, Explicit("Migrates an explicitly supplied offline workspace COPY for manual archive review")]
    public async Task OfflineWorkspaceCopyMigration()
    {
        var copy=Environment.GetEnvironmentVariable("HEXLIVE_MEMORY_COPY")!;
        Assert.That(copy,Is.Not.Null.And.Not.Empty);
        using var lease=new FileStream(Path.Combine(copy,".agent-studio.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var store=new MashaMemoryStore(copy); var state=await store.SnapshotAsync(default);
        var search=new AgentMemorySearch(copy); var count=search.Refresh();
        Assert.That(count,Is.GreaterThan(0));
        Assert.That(File.Exists(Path.Combine(copy,".state/backups/before-archive-v1.json")),Is.True);
        foreach(var query in new[]{"комната","айфон","Эльса","Яна","начало острова"})
            TestContext.WriteLine($"{query}: {search.Search(query).Total} matches");
        var ids=search.Search("",order:"oldest").Hits.Select(h=>h.Record.Id).ToArray();
        _=new MashaMemoryStore(copy);
        Assert.That(search.Refresh(),Is.EqualTo(count));
        Assert.That(search.Search("",order:"oldest").Hits.Select(h=>h.Record.Id),Is.EqualTo(ids));
        TestContext.WriteLine($"Migrated {count} searchable records; {state.Worlds.Count} episodes; backup present; repeat migration idempotent.");
    }

    public static IEnumerable<TestCaseData> Questions()
    {
        var fixtures = new[] {
            ("room", "В комнате на айфоне обсуждали красную лодку и море.", new[] { "Помнишь разговор в комнате?", "О чём говорили возле красной лодки?", "комната на айфоне", "красная лодка море", "помнишь нашу лодку" }),
            ("start", "Начало путешествия: меня выбросило на берег острова, первым делом нашла кокос.", new[] { "Как начиналось путешествие?", "первые дни на острове", "выбросило на берег", "что нашла на берегу", "начало острова кокос" }),
            ("elsa", "Эльса просила помощь. Я перевязала Эльсе рану, помогла и спасла её.", new[] { "Помнишь Эльсу?", "спасала Эльсу", "перевязка раны Эльсы", "кому помогла Эльсе", "Эльса просила помощь" }),
            ("yana", "Яна звала на помощь. Не ответила на зов Яны: физически не могла помочь.", new[] { "Помнишь Яну?", "ты не помогла Яне", "почему не ответила Яне", "зов о помощи Яны", "физически не могла помочь Яне" }),
            ("craft", "Изготовила каменный топор из камня и ветки у костра.", new[] { "каменный топор", "из чего изготовила топор", "что делала у костра", "топор из ветки", "камень и топор" }),
            ("walk", "Отправилась к северной бухте и прибыла туда до дождя.", new[] { "куда ходила к бухте", "северная бухта", "прибыла до дождя", "отправилась к северу в бухту", "бухта дождь" }),
            ("food", "Сварила суп из рыбы в котелке и поделилась едой с Никой.", new[] { "суп из рыбы", "чем поделилась с Никой", "котелок рыба", "готовила суп", "еда для Ники" }),
            ("bed", "Построила кровать из досок возле хижины и легла отдыхать.", new[] { "кровать возле хижины", "из чего построила кровать", "доски для кровати", "где легла отдыхать", "кровать отдых" })
        };
        foreach (var (id, text, queries) in fixtures)
            foreach (var query in queries) yield return new TestCaseData(id, text, query).SetName("Recall: " + query);
    }
    [TestCaseSource(nameof(Questions))] public void FortyReferenceQuestionsRetrieveTheSource(string id, string text, string query)
    {
        _archive.AppendMany(Questions().Select(c => Row((string)c.Arguments[0]!, (string)c.Arguments[1]!)).DistinctBy(r => r.Id));
        _archive.AppendMany(Enumerable.Range(0, 80).Select(i => Row("noise" + i, "Тихий день. Смотрела на огонь и отдыхала.")));
        var search = new AgentMemorySearch(_root); search.Refresh();
        Assert.That(search.Search(query).Hits.Select(h => h.Record.Id), Does.Contain(id));
    }
    [Test, Explicit("100000-record local performance acceptance")]
    public void HundredThousandRecordBenchmark()
    {
        _archive.AppendMany(Enumerable.Range(0, 100000).Select(i => Row("bench" + i, i % 1000 == 0 ? "Эльса спасена у берега" : "Собрала древесину и изготовила доски " + i)));
        var search = new AgentMemorySearch(_root); search.Refresh();
        var queries = new[] { "Эльса берег", "изготовила доски", "древесина", "" };
        var timings = Enumerable.Range(0, 40).Select(i => { var timer = Stopwatch.StartNew(); search.Search(queries[i % queries.Length]); return timer.Elapsed.TotalMilliseconds; }).Order().ToArray();
        TestContext.WriteLine("Search p95 ms: " + timings[37]); Assert.That(timings[37], Is.LessThan(200));
        var read = Stopwatch.StartNew(); search.Read("bench1000"); var readMs = read.Elapsed.TotalMilliseconds;
        TestContext.WriteLine("Read ms: " + readMs); Assert.That(readMs, Is.LessThan(100));
    }
}
