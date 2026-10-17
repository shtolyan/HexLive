using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

public sealed class VoiceIntroductionTests
{
    [TestCase("Меня зовут Толя", "Толя")]
    [TestCase("я Толя", "Толя")]
    [TestCase("Привет, я Толя, рад познакомиться", "Толя")]
    [TestCase("Зови меня Толей", "Толей")]
    [TestCase("Толя меня зовут", "Толя")]
    [TestCase("my name is Tolya", "Tolya")]
    [TestCase("Меня зовут ТОЛЯ.", "Толя")]
    public void ExplicitNameIsRecognized(string text, string name)
    { Assert.That(VoiceIntroduction.TryRead(text, out var found), Is.True); Assert.That(found, Is.EqualTo(name)); }
    [TestCase("Я думаю, что пора спать")]
    [TestCase("Толя — мой друг")]
    [TestCase("Не называй меня Толя")]
    [TestCase("Она сказала: меня зовут Лена")]
    [TestCase("Меня зовут не Толя")]
    public void OtherPeopleNegationAndOrdinarySpeechDoNotRename(string text) =>
        Assert.That(VoiceIntroduction.TryRead(text, out _), Is.False);

    [Test]
    public async Task NameIsDurableBeforeReplyAndSurvivesUnavailableModelWithoutChangingTrust()
    {
        var directory = Directory.CreateTempSubdirectory("voice-intro-");
        try
        {
            var bonds = new RelationshipStore(directory.FullName);
            using var http = new HttpClient(new FailedModel());
            var options = new BrainOptions { OpenAiKey="fixture", ElevenLabsKey="fixture", BrainSecret="fixture", TicketKey=new byte[32] };
            var turn = new BrainTurn(options,new OpenAiStream(http,"fixture"),new World(),bonds,null,null,NullLogger.Instance);
            using var input=JsonDocument.Parse("""{"messages":[{"role":"user","content":"Меня зовут Толя"}]}""");
            var ticket=new SessionTicket("call1","owner","world:1","world",1,long.MaxValue);
            Assert.ThrowsAsync<HttpRequestException>(async()=>await turn.RunAsync(input.RootElement,ticket,_=>Task.CompletedTask,default));
            var saved=new RelationshipStore(directory.FullName).Load("world:1","owner").Snapshot;
            Assert.That(saved.VoiceName,Is.EqualTo("Толя"));Assert.That(saved.Trust,Is.Zero);Assert.That(saved.Familiarity,Is.EqualTo(.02f));
            Assert.That(bonds.Load("world:1","other").Snapshot.VoiceName,Is.Empty);
            Assert.That(bonds.Load("world:2","owner").Snapshot.VoiceName,Is.Empty);
            Assert.ThrowsAsync<HttpRequestException>(async()=>await turn.RunAsync(input.RootElement,ticket,_=>Task.CompletedTask,default));
            Assert.That(bonds.Load("world:1","owner").Snapshot.Familiarity,Is.EqualTo(.02f),"Replay cannot learn the same introduction twice");
        }
        finally { directory.Delete(true); }
        await Task.CompletedTask;
    }
    private sealed class FailedModel : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    private sealed class World : IWorldGateway
    { public Task<JsonElement> CallAsync(string tool,object args,CancellationToken token)=>Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone()); }
}
