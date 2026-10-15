using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

public sealed class VoicePerceptionTests
{
    internal static object Item(int id,string name,string action="PickUp",bool reachable=true,bool hasTool=true) =>
        new {objectId=id,itemId=name,displayName=name,distance=(double)id,reachable,
            interactions=new[]{new{type=action,toolRequirementMet=hasTool}}};
    internal static JsonElement State(params object[] items)=>JsonSerializer.SerializeToElement(new{tick=123,visibleItems=items});

    [TestCase("Подними лифчик","underwear.bra_lace")]
    [TestCase("Собери кокосы","food.coconut")]
    [TestCase("Подбери камни","resource.stone")]
    [TestCase("Собери воду","tool.bottle")]
    [TestCase("Дострой дом","build.site")]
    public void RequestedObjectSurvivesTheBudget(string question,string definition)
    {
        var items=Enumerable.Range(1,60).Select(i=>Item(i,"resource.stick")).Append(Item(999,definition)).ToArray();
        var view=CharacterView.Build(State(items),question);
        Assert.That(view,Does.Contain("999 "+definition));
        var found=JsonSerializer.SerializeToElement(SurroundingsView.Inspect(State(items),question,0));
        Assert.That(found.GetProperty("objects").EnumerateArray().Any(o=>o.GetProperty("objectId").GetInt32()==999),Is.True);
    }

    [Test]
    public void CountsUseAllCurrentObjectsAndMixedMemorySummaryCannotBecomeSight()
    {
        var items=Enumerable.Range(1,24).Select(i=>Item(i,"tree.palm")).Concat(Enumerable.Range(100,6).Select(i=>Item(i,"resource.stone")));
        var d=JsonSerializer.SerializeToElement(new{visibleItems=items,perceptionSummary="tool.bottle id=555 fromMemory=true"});
        var view=CharacterView.Build(d);
        Assert.That(view,Does.Contain("resource.stone×6").And.Contain("список неполный"));
        Assert.That(view,Does.Not.Contain("tool.bottle").And.Not.Contain("555"));
    }

    [Test]
    public void StructuredEnvironmentAndPerceptionAgeSurviveWithoutMixedMemory()
    {
        var state=JsonSerializer.SerializeToElement(new{tick=200,perceptionTick=198,
            environment=new{temperature=24.5,privatePlace=true},perceptionSummary="old remembered bottle"});
        var view=CharacterView.Build(state);
        Assert.That(view,Does.Contain("tick=198").And.Contain("24.5").And.Contain("privatePlace"));
        Assert.That(view,Does.Not.Contain("old remembered bottle"));
    }

    [Test]
    public void InspectionExcludesMemoryAndPaginatesNearestCurrentMatches()
    {
        var rows=Enumerable.Range(1,25).Select(id=>(object)new{objectId=id,itemId="resource.stick",distance=26-id}).ToList();
        rows.Add(new{objectId=99,itemId="resource.stick",fromMemory=true});
        var d=State(rows.ToArray());
        var first=JsonSerializer.SerializeToElement(SurroundingsView.Inspect(d,"палка",0));
        Assert.That(first.GetProperty("source").GetString(),Is.EqualTo("currentSight"));
        Assert.That(first.GetProperty("totalMatches").GetInt32(),Is.EqualTo(25));
        Assert.That(first.GetProperty("counts")[0].GetProperty("count").GetInt32(),Is.EqualTo(25));
        Assert.That(first.GetProperty("objects")[0].GetProperty("objectId").GetInt32(),Is.EqualTo(25));
        Assert.That(first.GetProperty("nextOffset").GetInt32(),Is.EqualTo(16));
        var next=JsonSerializer.SerializeToElement(SurroundingsView.Inspect(d,"stick",16));
        Assert.That(next.GetProperty("objects").GetArrayLength(),Is.EqualTo(9));
        Assert.That(next.GetProperty("truncated").GetBoolean(),Is.False);
    }

    [Test]
    public async Task FreshInspectionCanContinueWithActionToolsAndTheSameOwnerNpc()
    {
        var world=new World(State(),State(Item(99,"underwear.bra_lace")),State(Item(99,"underwear.bra_lace")));
        var model=new Model(body=>Tool("inspect_surroundings","""{"query":"bra"}"""),body=>
        {
            Assert.That(body.GetProperty("tools").EnumerateArray().Any(t=>t.GetProperty("function").GetProperty("name").GetString()=="interact"),Is.True);
            var tool=body.GetProperty("messages").EnumerateArray().Last(m=>m.GetProperty("role").GetString()=="tool");
            Assert.That(tool.GetProperty("content").GetString(),Does.Contain("currentSight").And.Contain("99"));
            return Tool("interact","""{"objectId":99,"interaction":"PickUp"}""","Подниму.");
        });
        await Run(world,model);
        Assert.That(world.Tools,Is.EqualTo(new[]{"describe_colonist","describe_colonist","describe_colonist","acquire_control","interact"}));
        Assert.That(world.NpcIds,Is.All.EqualTo(1));
        Assert.That(model.StreamCalls,Is.EqualTo(2));
    }

    [TestCase(false,true)]
    [TestCase(true,false)]
    public async Task MissingToolOrUnreachableObjectIsRefusedBeforeTakingTheBody(bool reachable,bool hasTool)
    {
        var state=State(Item(10,"tree.palm","Harvest",reachable,hasTool));
        var world=new World(state);var model=new Model(_=>Tool("interact","""{"objectId":10,"interaction":"Harvest"}"""),_=>Speech("Не могу."));
        await Run(world,model);
        Assert.That(world.Tools,Does.Not.Contain("acquire_control").And.Not.Contain("interact"));
    }

    [Test]
    public async Task ObjectRemovedAfterTheDecisionIsNotActedOn()
    {
        var world=new World(State(Item(10,"food.coconut")),State());
        var model=new Model(_=>Tool("interact","""{"objectId":10,"interaction":"PickUp"}"""),body=>
        {
            Assert.That(body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString(),Does.Contain("TargetNotCurrentlyAvailable"));
            return Speech("Уже не вижу его.");
        });
        await Run(world,model);Assert.That(world.Tools,Does.Not.Contain("acquire_control"));
    }

    [Test]
    public async Task RepeatedInspectionCannotCauseAnUnboundedModelOrWorldLoop()
    {
        var world=new World(State());
        var model=new Model(Enumerable.Repeat<Func<JsonElement,HttpResponseMessage>>(_=>Tool("inspect_surroundings","""{"query":"bra"}"""),4)
            .Append(_=>Speech("Сейчас не вижу.")).ToArray());
        await Run(world,model);
        Assert.That(world.Tools.Count(t=>t=="describe_colonist"),Is.EqualTo(1+BrainTurn.MaxInspections));
        Assert.That(world.Tools,Does.Not.Contain("acquire_control"));
        Assert.That(model.StreamCalls,Is.EqualTo(5));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AttackRequiresTheSameCurrentlyVisibleMob(bool visible)
    {
        var state=JsonSerializer.SerializeToElement(new{visibleItems=Array.Empty<object>(),mobs=visible ? new[]{new{mobId=7,definitionId="crab"}}:[]});
        var world=new World(state);var model=new Model(_=>Tool("attack_mob","""{"mobId":7}""","Попробую."),_=>Speech("Не вижу цель."));
        await Run(world,model);
        Assert.That(world.Tools.Contains("attack_mob"),Is.EqualTo(visible));
        Assert.That(world.Tools.Contains("acquire_control"),Is.EqualTo(visible));
    }

    [Test]
    public async Task InspectionOwnershipRejectionDoesNotTakeControl()
    {
        var world=new World(State()){RejectReadAfter=1};
        var model=new Model(_=>Tool("inspect_surroundings","""{"query":"water"}"""),_=>Speech("Сейчас не могу проверить."));
        await Run(world,model);
        Assert.That(world.Tools,Does.Not.Contain("acquire_control"));
    }

    private static async Task Run(World world,Model model)
    {
        var dir=Directory.CreateTempSubdirectory("voice-perception-");
        try
        {
            using var http=new HttpClient(model);
            var options=new BrainOptions{OpenAiKey="fixture",ElevenLabsKey="",BrainSecret="fixture",TicketKey=new byte[32]};
            var turn=new BrainTurn(options,new OpenAiStream(http,"fixture"),world,new RelationshipStore(dir.FullName),null,null,NullLogger.Instance);
            await turn.RunAsync(JsonSerializer.SerializeToElement(new{messages=new[]{new{role="user",content="Проверь и подними лифчик"}}}),
                new SessionTicket("test","owner","w:1","w",1,long.MaxValue),_=>Task.CompletedTask,default);
        }
        finally{dir.Delete(true);}
    }
    private sealed class World(params JsonElement[] states):IWorldGateway
    {
        public List<string> Tools=new();public List<int> NpcIds=new();public int RejectReadAfter=int.MaxValue;private int reads;
        public Task<JsonElement> CallAsync(string tool,object arguments,CancellationToken token)
        {
            Tools.Add(tool);NpcIds.Add(JsonSerializer.SerializeToElement(arguments).GetProperty("npcId").GetInt32());
            if(tool=="describe_colonist")
            {
                if(reads>=RejectReadAfter)throw new WorldToolRejectedException("NpcAccessDenied");
                return Task.FromResult(states[Math.Min(reads++,states.Length-1)]);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new{status="accepted"}));
        }
    }
    private sealed class Model(params Func<JsonElement,HttpResponseMessage>[] replies):HttpMessageHandler
    {
        public int StreamCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
            if(!body.TryGetProperty("stream",out var stream)||!stream.GetBoolean())return new(HttpStatusCode.ServiceUnavailable);
            Assert.That(request.RequestUri!.Host,Is.EqualTo("api.openai.com"));
            return replies[StreamCalls++](body);
        }
    }
    private static HttpResponseMessage Tool(string name,string arguments,string content="")=>Sse(JsonSerializer.Serialize(new{
        choices=new[]{new{delta=new{content,tool_calls=new[]{new{index=0,id="fixture-"+name,type="function",function=new{name,arguments}}}},finish_reason="tool_calls"}}}));
    private static HttpResponseMessage Speech(string content)=>Sse(JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content},finish_reason="stop"}}}));
    private static HttpResponseMessage Sse(string json)=>new(HttpStatusCode.OK){Content=new StringContent("data: "+json+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
}
