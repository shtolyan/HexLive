using System.Net;
using System.Text;
using System.Text.Json;
using HexLive.AgentHost;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

public sealed class VoiceConversationMemoryTests
{
    private static SessionTicket Ticket(string session="call1",string owner="owner",string character="world:1") => new(session,owner,character,"world",1,long.MaxValue);
    [Test]
    public void OldConversationSurvivesMoreThanTwelveMessagesRestartAndOtherSessions()
    {
        var directory=Directory.CreateTempSubdirectory("voice-memory-");
        try
        {
            var memory=new VoiceConversationMemory(directory.FullName);var scope=memory.For(Ticket());
            scope.Record("first","user","У меня есть собака Барсик, он боится грома.");
            scope.Record("first","assistant","Помню Барсика, береги его.");
            for(var i=0;i<40;i++)scope.Record("later"+i,"user","Обсудим строительство номер "+i);
            var restored=new VoiceConversationMemory(directory.FullName).For(Ticket("call2"));
            Assert.That(restored.Context("Барсик","new"),Does.Contain("Барсик").And.Contain("боится грома"));
            using var page=JsonDocument.Parse(restored.Search("Барсик",0));
            var id=page.RootElement.GetProperty("sources")[0].GetProperty("sourceId").GetString()!;
            Assert.That(restored.Read(id,0),Does.Contain("Барсик"));
            foreach(var other in new[]{Ticket(owner:"other"),Ticket(character:"world:2")})
            {
                var isolated=memory.For(other);
                Assert.That(isolated.Context("Барсик","new"),Is.EqualTo("[]"));
                Assert.That(isolated.Read(id,0),Does.Contain("SourceUnavailable"));
            }
            Assert.That(Directory.GetFiles(directory.FullName,"*.jsonl",SearchOption.AllDirectories),Has.Length.EqualTo(1));
        }
        finally {directory.Delete(true);}
    }
    [Test]
    public void InterruptedReplyAndProviderGreetingAreKeptAndExactRetriesDoNotDuplicate()
    {
        var directory=Directory.CreateTempSubdirectory("voice-partial-");
        try
        {
            var scope=new VoiceConversationMemory(directory.FullName).For(Ticket());
            Assert.That(scope.Record("turn","assistant","Я хотела сказать",true),Is.True);
            Assert.That(scope.Record("turn","assistant","Я хотела сказать",true),Is.False);
            Assert.That(scope.Record("turn","assistant","Я хотела сказать: привет."),Is.True);
            var rows=new[]{new ConversationUtterance("assistant","Привет, давай поговорим."),new ConversationUtterance("user","Привет")};
            scope.Import("call1",rows,false);scope.Import("call1",rows,false);
            var archived=Directory.GetFiles(directory.FullName,"*.jsonl",SearchOption.AllDirectories).SelectMany(File.ReadAllLines).ToArray();
            Assert.That(archived,Has.Length.EqualTo(5));
            Assert.That(archived.Count(line=>line.Contains("\"incomplete\":true")),Is.EqualTo(1));
            Assert.That(scope.HasCompletedImport("call1"),Is.True);
            Assert.That(scope.Context("Привет","new"),Does.Contain("давай поговорим"));
        }
        finally {directory.Delete(true);}
    }
    [Test]
    public async Task NewCallModelReceivesOldMemoryAndActualStreamedReplyIsDurable()
    {
        var directory=Directory.CreateTempSubdirectory("voice-context-");
        try
        {
            var memory=new VoiceConversationMemory(directory.FullName);
            memory.For(Ticket()).Record("old","user","Мою собаку зовут Барсик.");
            var handler=new Model();using var http=new HttpClient(handler);
            var options=new BrainOptions{OpenAiKey="fixture",ElevenLabsKey="fixture",BrainSecret="fixture",TicketKey=new byte[32]};
            var turn=new BrainTurn(options,new OpenAiStream(http,"fixture"),new World(),new RelationshipStore(Path.Combine(directory.FullName,"bonds")),null,null,NullLogger.Instance,memory:memory);
            using var request=JsonDocument.Parse("""{"messages":[{"role":"user","content":"Помнишь Барсика?"}]}""");
            var said=new StringBuilder();await turn.RunAsync(request.RootElement,Ticket("call2"),piece=>{said.Append(piece);return Task.CompletedTask;},default);
            Assert.That(handler.Prompt,Does.Contain("Мою собаку зовут Барсик"));
            Assert.That(handler.HasMemoryTools,Is.True);Assert.That(said.ToString(),Is.EqualTo("Да, помню Барсика."));
            Assert.That(new VoiceConversationMemory(directory.FullName).For(Ticket("call3")).Context("Барсик","new"),Does.Contain("Да, помню Барсика."));
            await handler.Assessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var until=DateTime.UtcNow.AddSeconds(5);
            while(new RelationshipStore(Path.Combine(directory.FullName,"bonds")).Load("world:1","owner").Snapshot.LastContactUtc==null && DateTime.UtcNow<until)await Task.Delay(20);
            Assert.That(new RelationshipStore(Path.Combine(directory.FullName,"bonds")).Load("world:1","owner").Snapshot.LastContactUtc,Is.Not.Null);
        }
        finally {directory.Delete(true);}
    }
    [Test]
    public async Task CancellationArchivesOnlyDeliveredTextAsIncomplete()
    {
        var directory=Directory.CreateTempSubdirectory("voice-cancel-");
        try
        {
            var memory=new VoiceConversationMemory(directory.FullName);using var cancel=new CancellationTokenSource();
            using var http=new HttpClient(new InterruptedModel());
            var options=new BrainOptions{OpenAiKey="fixture",ElevenLabsKey="fixture",BrainSecret="fixture",TicketKey=new byte[32]};
            var turn=new BrainTurn(options,new OpenAiStream(http,"fixture"),new World(),new RelationshipStore(Path.Combine(directory.FullName,"bonds")),null,null,NullLogger.Instance,memory:memory);
            using var input=JsonDocument.Parse("""{"messages":[{"role":"user","content":"Расскажи о себе"}]}""");
            Assert.ThrowsAsync<OperationCanceledException>(async()=>await turn.RunAsync(input.RootElement,Ticket(),piece=>{cancel.Cancel();return Task.CompletedTask;},cancel.Token));
            var context=new VoiceConversationMemory(directory.FullName).For(Ticket("new-call")).Context("Первое","new");
            Assert.That(context,Does.Contain("Первое предложение").And.Contain("\"incomplete\":true").And.Not.Contain("Невыданный остаток"));
        }
        finally {directory.Delete(true);}
        await Task.CompletedTask;
    }
    private sealed class InterruptedModel:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Первое предложение\"}}]}\n\n"+
                "data: {\"choices\":[{\"delta\":{\"content\":\"Невыданный остаток\"}}]}\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")});
    }
    private sealed class World:IWorldGateway
    {public Task<JsonElement> CallAsync(string tool,object args,CancellationToken token){Assert.That(tool,Is.EqualTo("describe_colonist"),"Memory tools cannot acquire control or mutate the world");return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());}}
    [Test]
    public async Task SearchThenReadCanReachDetailsPastTheExcerptWithoutWorldCommands()
    {
        var directory=Directory.CreateTempSubdirectory("voice-lookup-");
        try
        {
            var memory=new VoiceConversationMemory(directory.FullName);
            memory.For(Ticket()).Record("old","user",new string('x',750)+" Барсик боится грома.");
            var handler=new Model(lookup:true);using var http=new HttpClient(handler);
            var bonds=new RelationshipStore(Path.Combine(directory.FullName,"bonds"));
            var options=new BrainOptions{OpenAiKey="fixture",ElevenLabsKey="fixture",BrainSecret="fixture",TicketKey=new byte[32]};
            var turn=new BrainTurn(options,new OpenAiStream(http,"fixture"),new World(),bonds,null,null,NullLogger.Instance,memory:memory);
            using var request=JsonDocument.Parse("""{"messages":[{"role":"user","content":"Что я рассказывал про Барсика?"}]}""");
            var spoken=new StringBuilder();await turn.RunAsync(request.RootElement,Ticket("call2"),p=>{spoken.Append(p);return Task.CompletedTask;},default);
            Assert.That(handler.StreamCalls,Is.EqualTo(3));Assert.That(spoken.ToString(),Is.EqualTo("Да, помню Барсика."));
            var until=DateTime.UtcNow.AddSeconds(5);
            while(bonds.Load("world:1","owner").Snapshot.LastContactUtc==null && DateTime.UtcNow<until)await Task.Delay(20);
            Assert.That(bonds.Load("world:1","owner").Snapshot.LastContactUtc,Is.Not.Null);
        }
        finally {directory.Delete(true);}
    }
    private sealed class Model(bool lookup=false):HttpMessageHandler
    {
        public string Prompt="";public bool HasMemoryTools;public int StreamCalls;public readonly TaskCompletionSource Assessed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));var body=doc.RootElement;
            if(body.TryGetProperty("stream",out var stream)&&stream.GetBoolean())
            {
                StreamCalls++;
                Prompt=body.GetProperty("messages")[0].GetProperty("content").GetString()!;
                HasMemoryTools=body.GetProperty("tools").EnumerateArray().Any(t=>t.GetProperty("function").GetProperty("name").GetString()=="memory_read");
                if(lookup)
                {
                    if(StreamCalls==1)return Tool("memory_search","{\"query\":\"Барсик\"}");
                    Assert.That(body.GetProperty("tools").EnumerateArray().Select(t=>t.GetProperty("function").GetProperty("name").GetString()),Is.EquivalentTo(new[]{"memory_search","memory_read"}));
                    var content=body.GetProperty("messages").EnumerateArray().Last(m=>m.GetProperty("role").GetString()=="tool").GetProperty("content").GetString()!;
                    using var evidence=JsonDocument.Parse(content);
                    if(StreamCalls==2)return Tool("memory_read",JsonSerializer.Serialize(new{sourceId=evidence.RootElement.GetProperty("sources")[0].GetProperty("sourceId").GetString()}));
                    Assert.That(evidence.RootElement.GetProperty("text").GetString(),Does.Contain("боится грома"));
                }
                return new(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Да, помню Барсика.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
            }
            Assessed.TrySetResult();
            return new(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"message":{"content":"{\"learnedSomethingSignificant\":false,\"trust\":\"Unchanged\",\"sympathy\":\"Unchanged\",\"seriousHarm\":false,\"reason\":\"Обычный вопрос\"}"}}]}""",Encoding.UTF8,"application/json")};
        }
        private static HttpResponseMessage Tool(string name,string args)
        {
            var chunk=JsonSerializer.Serialize(new{choices=new[]{new{delta=new{tool_calls=new[]{new{index=0,id="lookup-"+name,type="function",function=new{name,arguments=args}}}},finish_reason="tool_calls"}}});
            return new(HttpStatusCode.OK){Content=new StringContent("data: "+chunk+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
    }
}
