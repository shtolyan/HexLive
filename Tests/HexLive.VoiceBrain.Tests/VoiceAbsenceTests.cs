using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

/// <summary>§171.26: «пока тебя не было» and the character's own first line.</summary>
public sealed class VoiceAbsenceTests
{
    private const int Day = 24000;
    private static readonly SessionTicket First = new("session-1", "owner", "world:1", "world", 1, long.MaxValue);

    private string _directory = "";

    [SetUp] public void Create() => _directory = Path.Combine(Path.GetTempPath(), "hexlive-absence-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Remove() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Test]
    public void ThePauseIsFixedOncePerSessionAndMeasuredFromTheLastContact()
    {
        var clock = new ContactClock(_directory);
        var first = clock.Begin(First, 1000, Day);
        Assert.That(first.First, Is.True);
        clock.Touch(First, 2000, Day);
        Assert.That(clock.Begin(First, 2500, Day), Is.EqualTo(first), "same call keeps its view of the pause");

        var second = First with { SessionId = "session-2" };
        var later = clock.Begin(second, 2000 + 3 * Day + 5000, Day);
        Assert.Multiple(() =>
        {
            Assert.That(later.First, Is.False);
            Assert.That(later.PauseTicks, Is.EqualTo(3 * Day + 5000));
            Assert.That(later.SinceTick, Is.EqualTo(2000));
        });
        clock.Touch(second, 2000 + 3 * Day + 6000, Day);
        Assert.That(clock.Begin(second, 2000 + 3 * Day + 7000, Day), Is.EqualTo(later));
    }

    [Test]
    public void RollbackAndAnotherWorldAreUnknownNotGuessed()
    {
        var clock = new ContactClock(_directory);
        clock.Begin(First, 5000, Day); clock.Touch(First, 5000, Day);
        var rolledBack = clock.Begin(First with { SessionId = "s2" }, 100, Day);
        var otherWorld = clock.Begin(First with { SessionId = "s3", WorldId = "other" }, 9000, Day);
        Assert.That((rolledBack.First, rolledBack.PauseTicks), Is.EqualTo((false, (long?)null)));
        Assert.That((otherWorld.First, otherWorld.PauseTicks), Is.EqualTo((false, (long?)null)));
    }

    [Test]
    public void TheBlockNamesThePauseAndOnlyDiaryEntriesAfterTheLastContact()
    {
        var describe = Describe(tick: 10 * Day, journal: new object[]
        {
            Entry(1 * Day, "Это было до прошлого разговора."),
            Entry(8 * Day, "Тихо прошло.", quiet: 3),
            Entry(9 * Day, "Даша дала мне поесть."),
        });
        var view = AbsenceView.Build(new SessionContact(false, 3 * Day + 5 * Day / 24, 7 * Day, Day), describe);
        Assert.Multiple(() =>
        {
            Assert.That(view, Does.Contain("прошло игрового времени: 3 дн 5 ч"));
            Assert.That(view, Does.Contain("Пока голоса не было"));
            Assert.That(view, Does.Contain("Даша дала мне поесть.").And.Contain("Тихо прошло."));
            Assert.That(view, Does.Not.Contain("до прошлого разговора"));
            Assert.That(view.IndexOf("Тихо прошло.", StringComparison.Ordinal),
                Is.LessThan(view.IndexOf("Даша дала", StringComparison.Ordinal)), "chronological order");
        });
    }

    [Test]
    public void EventsWinOverQuietHoursWhenTheStretchIsLong()
    {
        var rows = Enumerable.Range(1, 12).Select(i => Entry(i * 1000, "тихо " + i, quiet: 2))
            .Append(Entry(500, "Волк меня укусил.")).ToArray();
        var view = AbsenceView.Build(new SessionContact(false, 20000, 0, Day), Describe(20000, rows));
        Assert.That(view, Does.Contain("Волк меня укусил."));
        Assert.That(view.Split('\n').Count(l => l.StartsWith("- ")), Is.EqualTo(AbsenceView.MaxEntries));
    }

    [Test]
    public void FirstAndUnknownConversationsSayExactlyThat()
    {
        var describe = Describe(5000, Enumerable.Range(1, 6).Select(i => Entry(i * 100, "запись " + i)).ToArray());
        var first = AbsenceView.Build(new SessionContact(true, null, null, Day), describe);
        var unknown = AbsenceView.Build(new SessionContact(false, null, null, Day), describe);
        Assert.That(first, Does.Contain("первый разговор").And.Contain("Недавно в твоём дневнике").And.Not.Contain("запись 2"));
        Assert.That(unknown, Does.Contain("неизвестно").And.Contain("запись 6"));
        Assert.That(AbsenceView.Pause(Day / 48, Day), Does.Contain("только что"));
    }

    [Test]
    public async Task GreetingUsesThePauseAndIsArchivedAsContact()
    {
        var model = new FakeModel("«Ну наконец-то. Я уж думала, ты меня бросил.»");
        using var http = new HttpClient(model);
        var memory = new VoiceConversationMemory(Path.Combine(_directory, "conversations"));
        var clock = new ContactClock(Path.Combine(_directory, "contacts"));
        clock.Begin(First, 1000, Day); clock.Touch(First, 1000, Day);
        var world = new FakeWorld(Describe(1000 + 2 * Day, new object[] { Entry(1000 + Day, "Даша дала мне поесть.") }));
        var options = new BrainOptions { OpenAiKey = "fixture", ElevenLabsKey = "fixture", BrainSecret = "fixture", TicketKey = new byte[32] };
        var turn = new BrainTurn(options, new OpenAiStream(http, "fixture"), world, new RelationshipStore(Path.Combine(_directory, "bonds")),
            null, null, NullLogger.Instance, memory: memory, contacts: clock);
        var call = First with { SessionId = "session-2" };

        var greeting = await turn.GreetAsync(call, default);

        Assert.Multiple(() =>
        {
            Assert.That(greeting, Is.EqualTo("Ну наконец-то. Я уж думала, ты меня бросил."));
            Assert.That(model.LastPrompt, Does.Contain("прошло игрового времени: 2 дн").And.Contain("Даша дала мне поесть."));
            Assert.That(model.LastBody, Does.Not.Contain("\"tools\""), "a greeting never commands the body");
            Assert.That(memory.For(call).Search("бросил", 0), Does.Contain("Ну наконец-то"));
            Assert.That(clock.Begin(call, 1000 + 2 * Day, Day).PauseTicks, Is.EqualTo(2 * Day), "the call keeps the pause it opened with");
            Assert.That(clock.Begin(call with { SessionId = "session-3" }, 1000 + 2 * Day + 100, Day).PauseTicks, Is.EqualTo(100),
                "the spoken greeting is the latest contact");
        });
    }

    [Test]
    public void LongGreetingsAreCutAtASentence()
    {
        var runOn = BrainTurn.Trim("Привет. " + new string('а', 300));
        Assert.That(runOn, Has.Length.LessThanOrEqualTo(BrainTurn.MaxGreeting + 1).And.EndsWith("…"));
        Assert.That(BrainTurn.Trim("Привет, давно не виделись. А у нас тут " + new string('б', 200)), Is.EqualTo("Привет, давно не виделись."));
    }

    [Test]
    public async Task GreetingIsOffUnlessEnabled()
    {
        var model = new FakeModel("не должно звучать");
        var service = new VoiceBrainService(new BrainOptions { OpenAiKey = "fixture", ElevenLabsKey = "fixture", BrainSecret = "fixture",
            TicketKey = new byte[32], StateDirectory = _directory }, _ => new FakeWorld(Describe(0, [])), NullLogger.Instance, new HttpClient(model));
        Assert.That(await service.GreetingAsync(First, default), Is.Null);
        Assert.That(model.Calls, Is.Zero);
    }

    private static object Entry(long tick, string text, int quiet = 0) =>
        new { tick, hoursAgo = 0, quietHours = quiet, type = "", mood = "plain", text };

    private static JsonElement Describe(long tick, object[] journal) =>
        JsonSerializer.SerializeToElement(new { name = "Надя", tick, dayLengthTicks = Day, journal });

    private sealed class FakeWorld(JsonElement describe) : IWorldGateway
    {
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token) => Task.FromResult(describe);
    }

    private sealed class FakeModel(string reply) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";
        public string LastPrompt { get; private set; } = "";
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            LastBody = await request.Content!.ReadAsStringAsync(token);
            using var body = JsonDocument.Parse(LastBody);
            LastPrompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? "";
            var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = reply }, finish_reason = "stop" } } });
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + chunk + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
