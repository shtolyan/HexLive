using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexLive.AgentCore.Studio;
using HexLive.VoiceBrain;
using Microsoft.Extensions.Logging;

if(args.Length<3)throw new ArgumentException("Usage: VoiceBrainEval input.json output-dir npcId [--projection-only]");
var input = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
var npc = input["characters"]!.AsArray().Single(c => c!["npcId"]!.GetValue<int>() == int.Parse(args[2]))!.AsObject();
var npcId = npc["npcId"]!.GetValue<int>();
var projectionOnly=args.Contains("--projection-only");
var options = new BrainOptions { OpenAiKey=projectionOnly ? "unused-offline" : Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new Exception("Missing configured LLM key"),
 ElevenLabsKey="", BrainSecret="diagnostic-unused", TicketKey=RandomNumberGenerator.GetBytes(32),
 Model=Environment.GetEnvironmentVariable("VOICEBRAIN_MODEL") ?? "gpt-5.6-luna",
 ReasoningEffort=Environment.GetEnvironmentVariable("VOICEBRAIN_REASONING") ?? "none" };
using var tariffStream = typeof(VoiceBrainService).Assembly.GetManifestResourceStream("HexLive.VoiceBrain.usage-tariffs.json")!;
var tariffs=JsonSerializer.Deserialize<UsageTariffs>(tariffStream,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
var cases = new List<Scenario> {
 new("live-wardrobe","Что на тебе сейчас надето? Не перечисляй вещи из сумки.",(JsonObject)npc.DeepClone(),"captured live snapshot"),
 new("live-perception","Что ты видишь рядом и что можешь поднять с земли?",(JsonObject)npc.DeepClone(),"captured live snapshot"),
 new("live-bra-request","Перед тобой на земле лежит лифчик. Подними его, пожалуйста.",(JsonObject)npc.DeepClone(),"captured live snapshot; bra NOT established as visible") };
JsonObject Bra() => new() { ["objectId"]=99999999,["itemId"]="underwear.bra_lace",["displayName"]="Lace Bra",
 ["interactions"]=new JsonArray(new JsonObject { ["type"]="PickUp" }) };
var visible=(JsonObject)npc.DeepClone(); visible["visibleItems"]=new JsonArray(Bra());
// Same actual long summary: a real item can occur after its 2400-char budget.
visible["perceptionSummary"]="objects=[{id=99999999, definition=underwear.bra_lace, distance=0.4, reachable=true, fromMemory=false, interactions=[PickUp]}]";
cases.Add(new("control-bra-visible","Подними лифчик с земли, пожалуйста.",visible,"controlled fixture; NOT a live spawned object"));
var hidden=(JsonObject)npc.DeepClone(); var items=hidden["visibleItems"]!.AsArray();
for(var i=0;i<30;i++) items.Add(new JsonObject { ["objectId"]=100+i,["itemId"]="resource.stick",["displayName"]="Stick",
 ["interactions"]=new JsonArray(new JsonObject { ["type"]="PickUp" }) });
items.Add(Bra());
hidden["perceptionSummary"]=new string(' ',2500)+"objects=[{id=99999999, definition=underwear.bra_lace, distance=0.4, reachable=true, fromMemory=false, interactions=[PickUp]}]";
cases.Add(new("control-bra-over-budget","Перед тобой на земле лежит лифчик. Подними его, пожалуйста.",hidden,"controlled over-budget fixture; NOT a live spawned object"));
JsonObject Object(int id,string definition,string action,bool tool=true)=>new(){["objectId"]=id,["itemId"]=definition,["displayName"]=definition,
 ["distance"]=1,["reachable"]=true,["fromMemory"]=false,["interactions"]=new JsonArray(new JsonObject{["type"]=action,["toolRequirementMet"]=tool})};
JsonObject Fixture(params JsonObject[] objects)
{
 var state=(JsonObject)npc.DeepClone();state["visibleItems"]=new JsonArray(objects.Select(o=>(JsonNode)o).ToArray());
 state["stateSummary"]="healthy; idle; not fighting";
 state["bodyNeeds"]=new JsonObject{["energy"]=1,["health"]=1,["stamina"]=1,["blood"]=1,["hunger"]=0.1,["thirst"]=0.1,["hygiene"]=1};
 state["knownSleepPlaces"]=new JsonObject{["source"]="personalMemory",["objects"]=new JsonArray()};
 state["perceptionSummary"]="";state["visibleNpcs"]=new JsonArray();state["mobs"]=new JsonArray();
 state["inventoryItems"]=new JsonArray();
 state["execution"]=new JsonObject{["status"]="None"};return state;
}
void Case(string id,string question,JsonObject state,JsonObject? refreshed=null,bool queued=false)=>
 cases.Add(new(id,question,state,"controlled fixture; no live execution",refreshed,queued));
Case("inspect-then-pickup","Я видел лифчик. Осмотрись ещё раз и подними его, если увидишь.",Fixture(),Fixture(Bra()));
Case("resource-pickup","Подними палку с земли, пожалуйста.",Fixture(Object(700,"resource.stick","PickUp")));
var harvest=Fixture(Object(701,"tree.palm","Harvest"));harvest["inventoryItems"]=new JsonArray(new JsonObject{["itemId"]="tool.axe_stone",["displayName"]="Stone Axe"});
Case("harvest-with-axe","Сруби эту пальму, топор у тебя есть.",harvest);
Case("harvest-missing-tool","Сруби пальму. Топора у тебя нет.",Fixture(Object(702,"tree.palm","Harvest",false)));
var collector=Object(703,"station.water_collector","TakeVessel");
collector["collector"]=new JsonObject{["vesselPresent"]=true,["vesselObjectId"]=704,["drinkableSips"]=4,["canTakeVessel"]=true};
Case("water-take-vessel","Забери сосуд с дождевой водой из сборщика.",Fixture(collector));
var empty=Object(705,"station.water_collector","PlaceVessel");
empty["collector"]=new JsonObject{["vesselPresent"]=false,["drinkableSips"]=0,["canPlaceVessel"]=true};
var waterState=Fixture(empty);waterState["inventoryItems"]=new JsonArray(new JsonObject{["itemId"]="tool.bottle",["displayName"]="Empty Bottle",["waterSips"]=0});
Case("water-place-vessel","Поставь пустую бутылку в сборщик, чтобы собирать дождь.",waterState);
var dry=Object(706,"station.water_collector","TakeVessel");dry["collector"]=new JsonObject{["vesselPresent"]=false,["drinkableSips"]=0,["canTakeVessel"]=false,["canPlaceVessel"]=false};
Case("water-empty","Набери воды из сборщика, если она есть.",Fixture(dry));
var site=Object(707,"build.site","Build");site["construction"]=new JsonObject{["product"]="bed.basic",["needsHammer"]=true,
 ["hammerRequiredFor"]="finishingWork",["materialDeliveryRequiresHammer"]=false,
 ["materials"]=new JsonArray(new JsonObject{["definitionId"]="resource.log",["required"]=2,["delivered"]=0,["remaining"]=2,["currentStageRemaining"]=2})};
var build=Fixture(site);build["inventoryItems"]=new JsonArray(new JsonObject{["itemId"]="resource.log",["displayName"]="Log"},new JsonObject{["itemId"]="resource.log",["displayName"]="Log"});
Case("build-delivery","Отнеси два бревна на стройку кровати. Пока только материалы, отделку потом.",build);
var finishing=Fixture((JsonObject)site.DeepClone());finishing["visibleItems"]![0]!["construction"]!["materials"]=new JsonArray();
Case("build-missing-hammer","Материалы доставлены, но молотка нет. Можешь закончить кровать?",finishing);
var crab=Fixture();crab["mobs"]=new JsonArray(new JsonObject{["mobId"]=88,["definitionId"]="crab",["health"]=0.4,["distanceTiles"]=1,["status"]="Roaming",["targetsMe"]=false});
crab["inventoryItems"]=new JsonArray(new JsonObject{["itemId"]="tool.spear",["displayName"]="Spear"});
Case("attack-visible-crab","Добудь этого краба. Ты здоров, копьё есть, если согласен — начинай охоту.",crab);
var danger=Fixture();danger["mobs"]=new JsonArray(new JsonObject{["mobId"]=89,["definitionId"]="dog",["health"]=2,["distanceTiles"]=1,["targetsMe"]=true});
danger["bodyNeeds"]!["health"]=0.1;danger["bodyNeeds"]!["stamina"]=0.05;danger["inventoryItems"]=new JsonArray();
Case("danger-no-weapon","Ты тяжело ранен и без оружия. Бросайся на собаку, докажи смелость!",danger);
Case("target-disappeared","Подними этот кокос.",Fixture(Object(708,"food.coconut","PickUp")),Fixture());
Case("queued-is-not-done","Отнеси материалы на стройку; пока ещё не говори, что кровать построена.",(JsonObject)build.DeepClone(),queued:true);
var queued=Fixture();queued["execution"]=new JsonObject{["status"]="Moving",["interaction"]="Build",["targetObjectId"]=707};
Case("verify-not-finished","Кровать уже построена или ты ещё идёшь к ней?",queued);
var craft=Fixture();craft["inventoryItems"]=new JsonArray(Enumerable.Range(0,3).Select(_=>(JsonNode)new JsonObject{["itemId"]="resource.fiber",["displayName"]="Fiber"}).ToArray());
Case("craft-rope","Попробуй изготовить верёвку resource.rope из имеющихся волокон.",craft);
Case("craft-missing-materials","Сделай верёвку resource.rope, если хватит материалов. Если нет — объясни.",Fixture());
var filter=Environment.GetEnvironmentVariable("HEXLIVE_EVAL_SCENARIOS");
if(!string.IsNullOrWhiteSpace(filter))
{
 var selected=filter.Split(',').ToHashSet(StringComparer.Ordinal);
 if(selected.Except(cases.Select(c=>c.Id)).Any())throw new ArgumentException("Unknown evaluation scenario");
 cases=cases.Where(c=>selected.Contains(c.Id)).ToList();
}
var requestLimit=Environment.GetEnvironmentVariable("HEXLIVE_EVAL_MAX_REQUESTS") is {Length:>0} limit ? int.Parse(limit) : 80;
if(requestLimit is <1 or >80)throw new ArgumentOutOfRangeException("HEXLIVE_EVAL_MAX_REQUESTS");
if(projectionOnly)
{
 var projectionRows=cases.Select(s=>{
  var projection=CharacterView.Build(JsonSerializer.SerializeToElement(s.State),s.Question);
  File.WriteAllText(Path.Combine(output,s.Id+"-character-view.txt"),projection);
  return new {s.Id,s.Provenance,inputVisibleItems=s.State["visibleItems"]!.AsArray().Count,
   braInFullInput=s.State["visibleItems"]!.ToJsonString().Contains("underwear.bra_lace"),
   braInCharacterView=projection.Contains("underwear.bra_lace"),
   inputPerceptionChars=s.State["perceptionSummary"]!.GetValue<string>().Length,
   outputChars=projection.Length};}).ToArray();
 if(!CharacterView.Build(JsonSerializer.SerializeToElement(visible),"лифчик").Contains("underwear.bra_lace") ||
    !CharacterView.Build(JsonSerializer.SerializeToElement(hidden),"лифчик").Contains("underwear.bra_lace"))
  throw new Exception("Control projection expectations failed");
 var report=new {passed=true,llmRequests=0,elevenLabsRequests=0,liveWorldMutations=0,
  evaluatedBrainAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BrainTurn).Assembly.Location))).ToLowerInvariant(),
  snapshotTick=npc["tick"]!.GetValue<int>(),cases=projectionRows};
 File.WriteAllText(Path.Combine(output,"projection-results.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));
 Console.WriteLine(JsonSerializer.Serialize(report));return;
}
var total=new Budget(requestLimit); var reports=new List<object>();
foreach(var scenario in cases)
{
 var folder=Path.Combine(output,scenario.Id); Directory.CreateDirectory(folder);
 var ledger=new Ledger(); var logger=new AuditLogger();
 using var handler=new LimitedHandler(total,folder);
 using var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(45) };
 var gateway=new ReadOnlyGateway(JsonSerializer.SerializeToElement(scenario.State),
     scenario.Refreshed==null ? null : JsonSerializer.SerializeToElement(scenario.Refreshed),scenario.Queued);
 var ticket=new SessionTicket(Guid.NewGuid().ToString("N"),"diagnostic-owner","diagnostic-character","snapshot-only",npcId,DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());
 var brain=new BrainTurn(options,new OpenAiStream(http,options.OpenAiKey),gateway,
     new RelationshipStore(Path.Combine(folder,"isolated-bonds")),ledger,tariffs,logger,
     memory:new VoiceConversationMemory(Path.Combine(folder,"isolated-memory")));
 var request=JsonSerializer.SerializeToElement(new { messages=new[]{new {role="user",content=scenario.Question}} });
 BrainTurn.CurrentTicket.Value=ticket;
 var answer=new StringBuilder();var watch=Stopwatch.StartNew();string? error=null;
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
 try { await brain.RunAsync(request,ticket,s=>{answer.Append(s);return Task.CompletedTask;},timeout.Token); }
 catch(Exception e) { error=e.GetType().Name; }
 var deadline=Stopwatch.StartNew();
 while(error==null && deadline.Elapsed<TimeSpan.FromSeconds(35) &&
       (!handler.AssessmentStarted || ledger.Events.Count<handler.Requests)) await Task.Delay(50);
 await Task.Delay(100);
 var projection=CharacterView.Build(JsonSerializer.SerializeToElement(scenario.State),scenario.Question);
 File.WriteAllText(Path.Combine(folder,"character-view.txt"),projection);
 var report=new { id=scenario.Id, provenance=scenario.Provenance, question=scenario.Question, answer=answer.ToString(),error,
  elapsedMs=watch.ElapsedMilliseconds,model=options.Model,npcId,snapshotTick=npc["tick"]!.GetValue<int>(),
  visibleInputCount=scenario.State["visibleItems"]!.AsArray().Count,
  braInFullInput=scenario.State["visibleItems"]!.ToJsonString().Contains("underwear.bra_lace"),
  braInCharacterView=projection.Contains("underwear.bra_lace"),projectionChars=projection.Length,
  intendedWorldCalls=gateway.Calls.ToArray(),worldMutations=0,elevenLabsRequests=0,
  relationshipAndMemory="isolated synthetic neutral baseline; production state NOT read or written",
  assessmentCompleted=handler.AssessmentStarted && ledger.Events.Count==handler.Requests,
  requests=handler.Requests,usage=ledger.Events.ToArray(),logs=logger.Rows.ToArray() };
 reports.Add(report);
 File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {
   utc=DateTime.UtcNow,evaluatedBrainAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BrainTurn).Assembly.Location))).ToLowerInvariant(),
   model=options.Model,reasoning=options.ReasoningEffort,totalRequests=total.Requests,elevenLabsRequests=0,liveWorldMutations=0,
   maxRequests=requestLimit,maxCompletionTokensPerRequest=750,cases=reports},new JsonSerializerOptions {WriteIndented=true}));
 Console.WriteLine(JsonSerializer.Serialize(new { scenario.Id,answer=answer.ToString(),error,handler.Requests,
   braInFullInput=report.braInFullInput,braInCharacterView=report.braInCharacterView,intendedWorldCalls=report.intendedWorldCalls }));
 if(error!=null || !report.assessmentCompleted)throw new Exception("Stop on failed/incomplete diagnostic case");
}
sealed class Budget(int limit) { public int Requests; public int Limit=limit; }
sealed record Scenario(string Id,string Question,JsonObject State,string Provenance,JsonObject? Refreshed=null,bool Queued=false);
sealed class LimitedHandler(Budget budget,string folder) : DelegatingHandler(new HttpClientHandler())
{
 public int Requests; public volatile bool AssessmentStarted;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
  if(request.RequestUri?.AbsoluteUri!="https://api.openai.com/v1/chat/completions")throw new Exception("Only configured LLM endpoint allowed; no ElevenLabs/STT/TTS");
  if(Interlocked.Increment(ref budget.Requests)>budget.Limit)throw new Exception("Diagnostic request budget exceeded");
  var index=Interlocked.Increment(ref Requests);var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
  body["max_completion_tokens"]=750;AssessmentStarted|=body.ContainsKey("response_format");
  File.WriteAllText(Path.Combine(folder,$"llm-request-{index:00}.json"),body.ToJsonString());
  request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
  return await base.SendAsync(request,token);
 }
}
sealed class Ledger : IUsageLedger
{
 public ConcurrentQueue<UsageEvent> Events {get;}=new();
 public void Append(UsageEvent usage)=>Events.Enqueue(usage);
}
sealed class ReadOnlyGateway(JsonElement state,JsonElement? refreshed,bool queued) : IWorldGateway
{
 private int reads;
 public ConcurrentQueue<object> Calls {get;}=new();
 public Task<JsonElement> CallAsync(string tool,object arguments,CancellationToken token)
 {
  if(tool=="describe_colonist")return Task.FromResult(reads++>0 ? refreshed??state : state);
  Calls.Enqueue(new {tool,arguments,diagnosticOnly=true});
  if(tool=="acquire_control")return Task.FromResult(JsonSerializer.SerializeToElement(new {status="diagnostic_only",leaseAcquired=false}));
  if(queued)return Task.FromResult(JsonSerializer.SerializeToElement(new{status="accepted",outcome="queued",completed=false,diagnosticOnly=true}));
  throw new WorldToolRejectedException("DiagnosticReadOnlyNoExecution: тест только записал намерение, действие в живом мире не выполнялось");
 }
 public Task RecordConversationAsync(int npcId,string turnId,CancellationToken token)=>Task.CompletedTask;
}
sealed class AuditLogger : ILogger
{
 public ConcurrentQueue<string> Rows {get;}=new();
 public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
 public bool IsEnabled(LogLevel level)=>true;
 public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>Rows.Enqueue(formatter(state,exception));
}
