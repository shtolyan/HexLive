#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using EntityId=HexLive.Simulation.Common.EntityId;
namespace HexLive.Tests
{
public sealed class GiftOfferUiRuntimeTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
    static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,Flags).GetValue(target);
    static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    sealed class Source : ISimulationSource
    {
        public readonly WorldSnapshot Snapshot=new WorldSnapshot{Tick=1};
        public readonly List<ISimulationCommand> Commands=new List<ISimulationCommand>();
        public bool IsReady=>true;public SimulationLink Link=>default;public bool IsCompleted=>false;
        public int Seed=>1;public int CurrentTick=>Snapshot.Tick;public float TickAlpha=>0;public bool IsPaused=>false;public float SpeedMultiplier=>1;
        public WorldSnapshot CreateSnapshot()=>Snapshot;
        public void Pause(){}public void Resume(){}public void TogglePause(){}public void SetSpeed(float value){}public void StepSingleTick(){}
        public bool TryGetObjectDefinition(string id,out ObjectDefinition definition){definition=null;return false;}
        public long DrainEvents(long since,List<SimulationEvent> into)=>since;
        public bool SupportsDirectWorldMutation=>false;public bool SupportsClientSave=>false;public bool SupportsNpcCommands=>true;
        public bool CanControlNpc(EntityId npc)=>npc.Value==1;
        public bool TryGetCraftingOptions(EntityId npc,List<CraftRecipeOption> into)=>false;
        public void EnqueueCommand(ISimulationCommand command)=>Commands.Add(command);
        public bool SupportsAgentIntegration=>false;public bool SttAvailable=>false;
        public bool TryGetAgentState(EntityId npc,out AgentStateFrame value){value=default;return false;}
        public void RequestSttToken(int id){}public bool TryTakeSttTokenResult(out SttTokenResultFrame value){value=default;return false;}
        public void SendAgentText(int id,EntityId npc,string message,string language,string text,string attachment){}
        public bool TryTakeAgentTextResult(out AgentTextResultFrame value){value=default;return false;}
        public bool TryTakeAgentSpeech(out AgentSpeechMessage value){value=default;return false;}
    }
    static Source Fixture()
    {
        var source=new Source();
        source.Snapshot.Npcs.Add(new NpcSnapshot{Id=new EntityId(1),DisplayName="Giver",Health=1,InventoryCapacity=4});
        var receiver=new NpcSnapshot{Id=new EntityId(2),DisplayName="Receiver",Health=1,InventoryUsedSlots=99,InventoryCapacity=99};
        receiver.WornItems.Add("fixture.private-clothing");
        var bag=new InventoryContainerSnapshot{Id="private-bag",Capacity=99};
        bag.Slots.Add(new InventorySlotSnapshot{SourceIndex=0,ItemDefinitionId="fixture.private-item",StackCount=45});
        receiver.InventoryContainers.Add(bag);source.Snapshot.Npcs.Add(receiver);return source;
    }
    static void WithPanel(System.Action<LootTransferPanel,Source> check)
    {
        var host=new GameObject("Gift offer UI test");PanelSettings settings=null;
        try{var panel=host.AddComponent<LootTransferPanel>();settings=host.GetComponent<UIDocument>().panelSettings;var source=Fixture();panel.SetRunner(source);check(panel,source);}
        finally{Object.DestroyImmediate(host);if(settings!=null&&settings.name=="LootTransferPanelSettings")Object.DestroyImmediate(settings);}
    }
    [Test]
    public void OpeningGiftHidesRecipientInventoryAndDoesNotInterruptAnyone()
    {
        WithPanel((panel,source)=>
        {
            LootTransferPanel.OpenGift(1,2);
            Assert.That(LootTransferPanel.IsOpen,Is.True);
            Assert.That(source.Commands,Is.Empty,"An invitation starts only after an item was chosen.");
            Assert.That(Get<Label>(panel,"_rightCapacity").text,Is.Empty);
            Assert.That(Get<VisualElement>(panel,"_rightContent").childCount,Is.EqualTo(1),"Only the recipient hint belongs here.");
            var json=(JObject)typeof(LootTransferPanel).GetMethod("GiftRecipientPane",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{source.Snapshot.Npcs[1]});
            Assert.That(json["count"],Is.Null);
            Assert.That(json.ToString(),Does.Not.Contain("fixture.private").And.Not.Contain("99"));
            Assert.That(((JArray)json["groups"]).Count,Is.Zero);Assert.That(((JArray)json["worn"]).Count,Is.Zero);
        });
    }
    [Test]
    public void HtmlChoiceSendsOneItemClosesWindowAndIgnoresAQueuedSecondClick()
    {
        WithPanel((panel,source)=>
        {
            LootTransferPanel.OpenGift(1,2);
            var args=new JObject{["uid"]="1|0|0|25|fixture.gift",["to"]="right",["count"]=25};
            Call(panel,"OnHtmlCommand","move",args);
            Assert.That(LootTransferPanel.IsOpen,Is.False);
            Assert.That(source.Commands.Count,Is.EqualTo(3));
            Assert.That(source.Commands[0],Is.TypeOf<BeginGiftSessionCommand>());
            var give=source.Commands.OfType<TransferInventoryCommand>().Single();
            Assert.That(give.Count,Is.EqualTo(1));Assert.That(give.Direction,Is.EqualTo(InventoryTransferDirection.Give));
            Assert.That(source.Commands[2],Is.TypeOf<EndGiftSessionCommand>(),"Closing defers release until the queued transfer completes.");
            Call(panel,"OnHtmlCommand","move",args);
            Assert.That(source.Commands.Count,Is.EqualTo(3));
        });
    }
    [Test]
    public void GiftInformationCannotReadAnOldForeignItemCursor()
    {
        WithPanel((panel,source)=>
        {
            LootTransferPanel.OpenGift(1,2);Set(panel,"_htmlInfoUid","2|0|0|45|fixture.private-item");
            Assert.That(Call(panel,"InfoModel",source.Snapshot,null),Is.Null);
        });
    }
}
}
#endif
