#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Views;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;
using EntityId = HexLive.Simulation.Common.EntityId;
namespace HexLive.Tests
{
/// <summary>Actual menu builders, real local snapshot exporter and both production
/// presentation/dispatch paths. No RemoteBackend, registry, server or world startup.</summary>
public sealed class ColonyMenuAcceptanceRuntimeTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    private static object Call(object target,string method,params object[] args) =>
        target.GetType().GetMethod(method,Private).Invoke(target,args);
    private static void Set(object target,string field,object value) =>
        target.GetType().GetField(field,Private).SetValue(target,value);

    [TestCase(Language.English)] [TestCase(Language.Russian)]
    public void NewSocialDoorAndTrustLabelsResolveThroughImportedLoc(Language language)
    {
        var previous=Loc.Current;
        try
        {
            Loc.Current=language;
            foreach(var key in new[]{"menu.ask_bathe","menu.ask_wash_clothes","menu.travel.invite","menu.travel.release",
                "menu.travel.need_trust","menu.recruit_outsider","menu.recruit_outsider.requirements","menu.knock_door","menu.break_door"})
            {
                Assert.That(Loc.Has(key),Is.True,key);
                Assert.That(Loc.Get(key),Is.Not.Empty.And.Not.EqualTo(key));
                if(language==Language.Russian) Assert.That(Loc.Get(key),Does.Match("[А-Яа-яЁё]"));
            }
        }
        finally{Loc.Current=previous;}
    }

    [TestCase(false)] [TestCase(true)]
    public void HygieneRequestsUseTypedTalkAndOwnDirectiveChildren(bool html)
    {
        using var f=new Fixture();
        f.OpenPerson(); f.Click("menu.ask_bathe",html);
        var bathe=f.One<TalkToCommand>();
        Assert.That(bathe.Npc,Is.EqualTo(f.Helper.Id)); Assert.That(bathe.Target,Is.EqualTo(f.Target.Id));
        Assert.That(bathe.RequestedTopic,Is.EqualTo(TalkTopic.AskBathe));
        f.OpenPerson(); f.Click("menu.ask_wash_clothes",html);
        Assert.That(f.One<TalkToCommand>().RequestedTopic,Is.EqualTo(TalkTopic.AskWashClothes));
        f.Target.Faction=f.Helper.Faction;
        f.OpenPerson(); f.Click("menu.directive",html,"menu.directive.bathe");
        Assert.That(f.One<SetDirectiveCommand>().Kind,Is.EqualTo(DirectiveKind.Bathe));
        f.OpenPerson(); f.Click("menu.directive",html,"menu.directive.wash_clothes");
        Assert.That(f.One<SetDirectiveCommand>().Kind,Is.EqualTo(DirectiveKind.WashClothes));
        f.Target.Mind.ComaCause=ComaCause.Exhaustion;
        f.OpenPerson();
        Assert.That(f.Entries.Any(e=>e.Label==Loc.Get("menu.ask_bathe")),Is.False);
        Assert.That(f.Entries.Any(e=>e.Label==Loc.Get("menu.ask_wash_clothes")),Is.False);
    }

    [TestCase(false)] [TestCase(true)]
    public void TravelInviteUsesStrictTrustAndReleaseTargetsTheActualLeader(bool html)
    {
        using var f=new Fixture();
        f.Target.Social.GetOrCreate(f.Helper.Id).Trust=.60f;
        f.OpenPerson(); Assert.That(f.Entry("menu.travel.invite").Enabled,Is.False);
        f.Click("menu.travel.invite",html); Assert.That(f.Drain(),Is.Empty);
        f.Target.Social.GetOrCreate(f.Helper.Id).Trust=.6001f;
        f.OpenPerson(); Assert.That(f.Entry("menu.travel.invite").Enabled,Is.True);
        f.Click("menu.travel.invite",html);
        var invite=f.One<RequestTravelPartyCommand>();
        Assert.That(invite.Npc,Is.EqualTo(f.Helper.Id)); Assert.That(invite.Target,Is.EqualTo(f.Target.Id));
        Assert.That(f.Engine.ApplyManualCommand(invite).Accepted,Is.True);
        f.OpenPerson();
        Assert.That(f.Entries.Any(e=>e.Label==Loc.Get("menu.travel.invite")),Is.False);
        f.Click("menu.travel.release",html);
        var release=f.One<ReleaseTravelPartyCommand>();
        Assert.That(release.Npc,Is.EqualTo(f.Helper.Id)); Assert.That(release.Target,Is.EqualTo(f.Target.Id));
        Assert.That(f.Engine.ApplyManualCommand(release).Accepted,Is.True);
        Assert.That(f.Target.Mind.TravelLeaderNpcId,Is.Null);
        Assert.That(f.Target.Faction,Is.EqualTo(Faction.Colony2));
    }

    [TestCase(false)] [TestCase(true)]
    public void HostileOutsiderHasCareActionsAndRecruitmentRequiresEarnedRelations(bool html)
    {
        using var f=new Fixture(); f.Target.Faction=Faction.Outsiders;
        f.Target.Social.GetOrCreate(f.Helper.Id).Affinity=-.5f;
        f.OpenPerson(); Assert.That(f.Entry("menu.recruit_outsider").Enabled,Is.False);
        f.Click("menu.recruit_outsider",html); Assert.That(f.Drain(),Is.Empty);
        foreach(var kind in new[]{AidKind.Feed,AidKind.Hydrate})
        {
            f.OpenPerson(); f.Click("menu.aid",html); Assert.That(f.Drain(),Is.Empty);
            f.Click(kind==AidKind.Feed?"menu.aid.feed":"menu.aid.hydrate",html);
            var aid=f.One<AidPersonCommand>();
            Assert.That(aid.Npc,Is.EqualTo(f.Helper.Id)); Assert.That(aid.Target,Is.EqualTo(f.Target.Id));
            Assert.That(aid.Kind,Is.EqualTo(kind));
        }
        f.OpenPerson(); f.Click("menu.medical_aid",html);
        var medical=f.One<MedicalAidCommand>(); Assert.That(medical.Target,Is.EqualTo(f.Target.Id));
        f.Helper.Social.GetOrCreate(f.Target.Id).Affinity=.8f;
        f.Target.Social.GetOrCreate(f.Helper.Id).Affinity=.8f;
        f.Target.Social.GetOrCreate(f.Helper.Id).Trust=.5f;
        f.OpenPerson(); Assert.That(f.Entry("menu.recruit_outsider").Enabled,Is.False);
        f.Target.Social.GetOrCreate(f.Helper.Id).Trust=.5001f;
        f.OpenPerson(); f.Click("menu.recruit_outsider",html);
        var recruit=f.One<MergeCampsCommand>();
        Assert.That(recruit.Npc,Is.EqualTo(f.Helper.Id)); Assert.That(recruit.Target,Is.EqualTo(f.Target.Id));
        Assert.That(recruit.UseTargetCamp,Is.False,"Individual outsider recruitment must not occupy a whole foreign camp.");
    }

    [TestCase(false)] [TestCase(true)]
    public void DoorOwnerProxyStillSendsTheRealLeafAndToolGateCannotDispatch(bool html)
    {
        using var f=new Fixture();
        var door=f.World.Entities.Objects.Values.Single(o=>o.DefinitionId==DoorTopology.DoorDefinitionId);
        var hut=f.World.Entities.Objects[door.ArchitectureOwnerId.Value];
        hut.OwnerFaction=Faction.Colony2; f.World.DoorStateVersion++;
        Assert.That(BuildingDoorRules.TryClose(f.World,door.Id),Is.True);
        var host=new GameObject("actual door view with hut context proxy");
        try
        {
            var view=host.AddComponent<WorldObjectView>(); view.Init(door.Id.Value,door.DefinitionId);
            view.SetContextProxy(hut.Id.Value,hut.DefinitionId);
            f.Helper.Inventory.Items.Clear(); f.OpenObject(view);
            Assert.That(f.Entry("menu.break_door").Enabled,Is.False);
            f.Click("menu.break_door",html); Assert.That(f.Drain(),Is.Empty);
            f.OpenObject(view); f.Click("menu.knock_door",html);
            var knock=f.One<KnockDoorCommand>();
            Assert.That(knock.Npc,Is.EqualTo(f.Helper.Id)); Assert.That(knock.Door,Is.EqualTo(door.Id));
            Assert.That(knock.Door,Is.Not.EqualTo(hut.Id));
            f.Helper.Inventory.Items.Add(new ItemInstance(GearCatalog.Axe));
            f.OpenObject(view); Assert.That(f.Entry("menu.break_door").Danger,Is.True);
            f.Click("menu.break_door",html);
            Assert.That(f.One<BreakDoorCommand>().Door,Is.EqualTo(door.Id));
            BuildingDoorRules.TryOpen(f.World,door.Id); f.OpenObject(view);
            Assert.That(f.Entries.Any(e=>e.Label==Loc.Get("menu.knock_door")||e.Label==Loc.Get("menu.break_door")),Is.False);
        }
        finally {Object.DestroyImmediate(host);}
    }

    private sealed class Fixture:IDisposable
    {
        public readonly SimulationEngine Engine;
        public WorldState World=>Engine.World;
        public readonly NPCState Helper,Target;
        private readonly GameObject host,panelHost,uiHost;
        private readonly PanelSettings panelSettings;
        private readonly SimulationInputAdapter adapter;
        private readonly ContextMenuPanel panel;
        private readonly int[] oldSelection=NpcSelection.SelectedIds.ToArray();
        private readonly Language language=Loc.Current;
        private readonly Dictionary<FieldInfo,object> statics=new();
        public List<ContextMenuEntry> Entries=>(List<ContextMenuEntry>)typeof(SimulationInputAdapter).GetField("_entries",Private).GetValue(adapter);
        public Fixture()
        {
            Assert.That(ContextMenuPanel.IsOpen,Is.False,"Run in the isolated acceptance scene, never over a player menu.");
            // Prevent a menu-only test from touching an existing title-screen UI.
            foreach(var name in new[]{"_instance","<IsOpen>k__BackingField","<PointerOverPanel>k__BackingField","_worldPointerSuppressed","_worldPointerReleaseFrame"})
            { var field=typeof(ContextMenuPanel).GetField(name,Static); statics[field]=field.GetValue(null); }
            typeof(ContextMenuPanel).GetField("_instance",Static).SetValue(null,null);
            Loc.Current=Language.English;
            SimDataFile.Require(Path.GetFullPath(Path.Combine(Application.dataPath,"..","SimData","simdata.json")));
            var definition=PrototypeWorldDefinitionFactory.Create(12345);
            var world=new WorldStateFactory().Create(definition); world.Mode=GameMode.HugeIsland;
            var settings=new SimulationSettings {TickDeltaTime=definition.Simulation.TickDeltaTime,
                MediumInterval=definition.Simulation.MediumTickInterval,SlowInterval=definition.Simulation.SlowTickInterval};
            var clock=new SimulationClock(); Engine=new SimulationEngine(world,settings,clock);
            SimulationSystemRegistry.RegisterDefaults(Engine); DefinitionIdTable.Build(world.Content); Engine.Step();
            var pair=world.Entities.Npcs.Values.Where(n=>n.Faction==Faction.Colony).OrderBy(n=>n.Id.Value).Take(2).ToArray();
            Helper=pair[0]; Target=pair[1]; Helper.Needs.Hunger=Helper.Needs.Thirst=0f;
            Assert.That(Engine.ApplyManualCommand(new SetManualControlCommand(Helper.Id,true)).Accepted,Is.True);
            PlanInterruption.TryAbort(world,Target,InterruptionCause.PlayerCommand,"Isolated menu fixture");
            Target.Mind.CurrentGoal=GoalType.None; Target.Mind.ManualControl=false; Target.Faction=Faction.Colony2;
            var neighbor=SpatialQueries.GetPassableNeighbors(world,Helper.CurrentJunction.Value).First(j=>SpatialQueries.IsJunctionFree(world,j));
            if(Target.CurrentJunction is {} old) SpatialMutations.FreeJunction(world,old,Target.Id);
            var destination=world.Junctions.Items[neighbor]; var oldTile=Target.Tile;
            Target.Tile=destination.Tiles[0]; Target.Fragment=destination.Fragment; Target.Position=destination.WorldPosition; Target.CurrentJunction=neighbor;
            SpatialMutations.MoveEntityToTile(world,Target.Id,oldTile,Target.Tile); SpatialMutations.OccupyJunction(world,neighbor,Target.Id);
            host=new GameObject("isolated local menu backend"); host.SetActive(false);
            var runner=host.AddComponent<SimulationRunnerBehaviour>(); runner.AutosaveEnabled=false; runner.AutosaveSuppressed=true; Set(runner,"_backend",new LocalEngineBackend(Engine,clock,settings));
            adapter=host.AddComponent<SimulationInputAdapter>(); adapter.SetRunner(runner);
            panelHost=new GameObject("detached production menu presenter"); panelHost.SetActive(false); panel=panelHost.AddComponent<ContextMenuPanel>();
            // A detached VisualElement has no runtime dispatcher. Keep the
            // presenter isolated but attach its real MakeItem rows to a real
            // UIDocument panel, exactly as production delivers UITK events.
            uiHost=new GameObject("isolated UITK event document");
            var document=uiHost.AddComponent<UIDocument>();
            var source=Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            Assert.That(source,Is.Not.Null);
            panelSettings=Object.Instantiate(source); document.panelSettings=panelSettings;
            var root=document.rootVisualElement;
            Set(panel,"_root",root);
            foreach(var name in new[]{"_items","_subCard","_subItems"})
            {var element=new VisualElement();root.Add(element);Set(panel,name,element);}
            NpcSelection.Activate(Helper.Id.Value);
        }
        private void Fresh() { World.Tick++; Call(adapter,"RefreshControlSelection"); }
        public void OpenPerson() {Fresh(); Call(adapter,"OpenNpcMenu",Vector2.zero,Target.Id.Value);}
        public void OpenObject(WorldObjectView view) {Fresh(); Call(adapter,"OpenObjectMenu",Vector2.zero,view);}
        public ContextMenuEntry Entry(string key)=>Entries.Single(e=>e.Label==Loc.Get(key));
        public void Click(string key,bool html,string child=null)
        {
            var index=Entries.FindIndex(e=>e.Label==Loc.Get(key)); Assert.That(index,Is.GreaterThanOrEqualTo(0),key);
            var entry=Entries[index]; var path=new JArray(index);
            if(child!=null)
            {var childIndex=entry.Children.ToList().FindIndex(e=>e.Label==Loc.Get(child)); Assert.That(childIndex,Is.GreaterThanOrEqualTo(0),child);path.Add(childIndex);entry=entry.Children[childIndex];}
            var model=(JArray)typeof(ContextMenuPanel).GetMethod("HtmlEntries",Static).Invoke(null,new object[]{Entries});
            var rowModel=child==null?model[index]:model[index]["children"][(int)path[1]];
            Assert.That((bool)rowModel["enabled"],Is.EqualTo(entry.Enabled));
            Assert.That((bool)rowModel["danger"],Is.EqualTo(entry.Danger));
            if(html)
            {
                Set(panel,"_htmlEntries",Entries.ToList()); typeof(ContextMenuPanel).GetField("<IsOpen>k__BackingField",Static).SetValue(null,true);
                Call(panel,"OnHtmlCommand","pick",new JObject{["path"]=path});
            }
            else
            {
                var root=(VisualElement)typeof(ContextMenuPanel).GetField("_root",Private).GetValue(panel);
                var items=(VisualElement)typeof(ContextMenuPanel).GetField("_items",Private).GetValue(panel);
                root.style.display=DisplayStyle.Flex; items.Clear();
                var row=(VisualElement)Call(panel,"MakeItem",entry); items.Add(row);
                Assert.That(row.panel,Is.Not.Null,"The actual runtime UITK dispatcher must own the row.");
                Assert.That(row.Q<Label>().text,Is.EqualTo(entry.Label));
                using(var press=MouseDownEvent.GetPooled(new Event{type=EventType.MouseDown,button=0}))
                {press.target=row;row.SendEvent(press);}
            }
        }
        public List<ISimulationCommand> Drain()
        {var result=new List<ISimulationCommand>();while(Engine.Commands.TryDequeue(out var command))result.Add(command);return result;}
        public T One<T>() where T:ISimulationCommand
        {var commands=Drain(); var selected=commands.OfType<T>().ToArray();Assert.That(selected,Has.Length.EqualTo(1));Assert.That(commands.All(c=>c is T || c is SetManualControlCommand || c is SetGroupManualControlCommand),Is.True);return selected[0];}
        public void Dispose()
        {
            Object.DestroyImmediate(panelHost); Object.DestroyImmediate(host);
            Object.DestroyImmediate(uiHost); Object.DestroyImmediate(panelSettings);
            foreach(var pair in statics)pair.Key.SetValue(null,pair.Value);
            NpcSelection.ReplaceMany(oldSelection,requestFrame:false); Loc.Current=language;
        }
    }
}
}
#endif
