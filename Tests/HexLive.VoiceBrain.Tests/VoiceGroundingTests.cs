using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

public sealed class VoiceGroundingTests
{
    [Test]
    public async Task EachTurnGetsFreshOutfitDespiteAnOldWrongAnswerWithoutAnotherModelRound()
    {
        var dir = Directory.CreateTempSubdirectory("voice-grounding-");
        try
        {
            var world = new ChangingWorld(); var model = new CapturingModel();
            using var http = new HttpClient(model);
            var options = new BrainOptions { OpenAiKey="fixture", ElevenLabsKey="fixture", BrainSecret="fixture", TicketKey=new byte[32] };
            var memory = new VoiceConversationMemory(dir.FullName);
            var ticket = new SessionTicket("call", "owner", "world:1", "world", 1, long.MaxValue);
            memory.For(ticket).Record("old", "assistant", "На мне походные штаны.");
            var turn = new BrainTurn(options, new OpenAiStream(http,"fixture"), world,
                new RelationshipStore(Path.Combine(dir.FullName,"bonds")), null, null, NullLogger.Instance, memory:memory);
            using var request = JsonDocument.Parse("""
                {"messages":[{"role":"assistant","content":"На мне походные штаны."},
                {"role":"user","content":"Что на тебе надето сейчас?"}]}
                """);
            for (var i=0; i<2; i++)
                await turn.RunAsync(request.RootElement,ticket,_=>Task.CompletedTask,default);
            Assert.That(world.Reads, Is.EqualTo(2), "Exactly the existing read before each utterance; no acquire_control");
            Assert.That(model.Prompts, Has.Count.EqualTo(2), "No extra streaming LLM round for reading wardrobe");
            var first = model.Prompts[0]; var second = model.Prompts[1];
            static string Current(string prompt) => prompt.Split("<you>\n")[1].Split("</you>")[0];
            Assert.That(Current(first), Does.Contain("tick=100").And.Contain("Primal briefs"));
            Assert.That(Current(second), Does.Contain("tick=101").And.Contain("ничего, список надетых вещей пуст"));
            Assert.That(Current(second), Does.Not.Contain("Primal briefs"));
            Assert.That(second, Does.Contain("На мне походные штаны"), "Past conversation is retained, not silently rewritten");
            Assert.That(second, Does.Contain("они важнее старых ответов").And.Contain("вещи в инвентаре и рядом не надеты")
                .And.Contain("accepted/queued не означает построено"));
        }
        finally { dir.Delete(true); }
    }

    private sealed class ChangingWorld : IWorldGateway
    {
        public int Reads;
        public Task<JsonElement> CallAsync(string tool, object args, CancellationToken token)
        {
            Assert.That(tool, Is.EqualTo("describe_colonist"));
            using var doc = JsonDocument.Parse(Reads++ == 0
                ? """{"tick":100,"wornItems":[{"itemId":"underwear.briefs_primal","displayName":"Primal briefs"}]}"""
                : """{"tick":101,"wornItems":[],"execution":{"status":"Moving","interaction":"Build"}}""");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class CapturingModel : HttpMessageHandler
    {
        public List<string> Prompts = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = doc.RootElement;
            if (!body.TryGetProperty("stream",out var stream) || !stream.GetBoolean())
                return new(HttpStatusCode.ServiceUnavailable); // No background relationship writes or paid calls.
            Prompts.Add(body.GetProperty("messages")[0].GetProperty("content").GetString()!);
            return new(HttpStatusCode.OK) { Content=new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Проверила.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                Encoding.UTF8,"text/event-stream") };
        }
    }
}
