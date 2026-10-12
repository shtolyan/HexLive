using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

public sealed class VoiceTurnRelationshipTests
{
    [Test]
    public async Task SpokenIntroductionPersistsNameAndRecordsSocialWithSessionScopedRetryId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hexlive-turn-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RelationshipStore(directory);
            var world = new FakeWorld();
            using var http = new HttpClient(new FakeModel());
            var options = new BrainOptions { OpenAiKey = "fixture", ElevenLabsKey = "fixture", BrainSecret = "fixture", TicketKey = new byte[32] };
            var turn = new BrainTurn(options, new OpenAiStream(http, "fixture"), world, store, null, null, NullLogger.Instance);
            using var input = JsonDocument.Parse("""{"messages":[{"role":"user","content":"Меня зовут Толя"}]}""");
            var ticket = new SessionTicket("session-1", "owner", "world:1", "world", 1, long.MaxValue);
            async Task Run(SessionTicket t)
            {
                var spoken = new StringBuilder();
                await turn.RunAsync(input.RootElement, t, text => { spoken.Append(text); return Task.CompletedTask; }, default);
                Assert.That(spoken.ToString(), Is.EqualTo("Привет, Толя."));
                var until = DateTime.UtcNow.AddSeconds(5);
                while (store.Load("world:1", "owner").Snapshot.Familiarity < (t.SessionId == "session-1" ? .02f : .04f) && DateTime.UtcNow < until)
                    await Task.Delay(20);
            }
            await Run(ticket);
            await Run(ticket);
            Assert.That(world.Turns.Count, Is.EqualTo(1), "request retry must not double Social");
            Assert.That(store.Load("world:1", "owner").Snapshot.Familiarity, Is.EqualTo(.02f));
            await Run(ticket with { SessionId = "session-2" });
            Assert.That(world.Turns.Count, Is.EqualTo(2), "new call must not collide with previous call");
            Assert.That(store.Load("world:1", "owner").Snapshot.VoiceName, Is.EqualTo("Толя"));
            Assert.That(store.Load("world:1", "owner").Snapshot.Familiarity, Is.EqualTo(.04f));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FakeWorld : IWorldGateway
    {
        public HashSet<string> Turns { get; } = new();
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { name = "Надя", health = 1, hygiene = 1 }));
        public Task RecordConversationAsync(int npcId, string turnId, CancellationToken token)
        { Turns.Add(turnId); return Task.CompletedTask; }
    }

    private sealed class FakeModel : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean())
                return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Привет, Толя.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
            var assessment = """{"learnedSomethingSignificant":true,"trust":"Increase","sympathy":"Increase","seriousHarm":false,"reason":"Представился","voiceName":"Толя","namingReason":"Сам назвал имя"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { message = new { content = assessment } } } }), Encoding.UTF8, "application/json") };
        }
    }
}
