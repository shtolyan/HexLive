#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class RuntimeWindowTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(object owner, string name) => owner.GetType().GetField(name, Private).GetValue(owner);
        private static object Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Private).Invoke(owner,args);
        private static void Click(Button button) => typeof(Clickable).GetMethod("SimulateSingleClick", Private | BindingFlags.Public)
            .Invoke(button.clickable, new object[] { null, 0 });
        private static void Down(VisualElement element, int button=0)
        {
            using var e = PointerDownEvent.GetPooled(new Event { type=EventType.MouseDown, button=button, mousePosition=element.worldBound.center });
            e.target=element; element.SendEvent(e);
        }
        private static void AssertIcon(VisualElement root, string name)
        {
            var button = root.Q<Button>(name); Assert.That(button, Is.Not.Null);
            var icon = button.Q<VectorIcon>("close-icon"); Assert.That(icon, Is.Not.Null);
            Assert.That(icon.Current, Is.EqualTo(VectorIcon.Kind.Close));
            Assert.That(icon.worldBound.width, Is.GreaterThan(15));
            Assert.That(icon.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(button.panel.Pick(button.worldBound.center), Is.EqualTo(button), "The actual close hit target is unobstructed.");
        }
        private static void Capture(UIDocument document, RenderTexture target, VisualElement window, string name)
        {
            var bounds=window.worldBound;
            var old=RenderTexture.active;
            var rect=new Rect(Mathf.Max(0,bounds.x-4), Mathf.Max(0,target.height-bounds.yMax-4),
                Mathf.Min(target.width, bounds.width+8), Mathf.Min(target.height,bounds.height+8));
            var image=new Texture2D(Mathf.RoundToInt(rect.width),Mathf.RoundToInt(rect.height),TextureFormat.RGBA32,false);
            try
            {
                RenderTexture.active=target; image.ReadPixels(rect,0,0); image.Apply();
                Directory.CreateDirectory("Build/MSIRelease/20261013-window-close/visual");
                File.WriteAllBytes("Build/MSIRelease/20261013-window-close/visual/"+name+".png",image.EncodeToPNG());
            }
            finally { RenderTexture.active=old; Object.DestroyImmediate(image); }
        }

        [UnityTest]
        public IEnumerator CharacterWindowsHaveVisibleCrossAndOutsideDismissWithoutWorldInput()
        {
            var host=new GameObject("Window close verification");
            var doc=host.AddComponent<UIDocument>(); var panel=host.AddComponent<CharacterPanel>(); panel.enabled=false;
            var settings=doc.panelSettings; settings.clearColor=true; settings.colorClearValue=Color.black;
            var target=new RenderTexture(1920,1080,24); target.Create(); settings.targetTexture=target;
            try
            {
                foreach (var kind in new[]{"Inventory","Health","Journal"})
                {
                    var lower=kind.ToLowerInvariant(); Call(panel,"Toggle"+kind);
                    yield return null; yield return null; yield return null;
                    AssertIcon(doc.rootVisualElement,lower+"-close");
                    var window=(VisualElement)Field(panel,"_"+lower+"Window");
                    Down(window); Assert.That(Field(panel,"_"+lower+"Open"),Is.True,"Interior click cannot dismiss");
                    NpcSelection.PointerOverUi=false;
                    Assert.That(RuntimeWindow.BlocksWorldInput,Is.True,"Independent of global hover flag");
                    Capture(doc,target,window,lower);
                    Click(doc.rootVisualElement.Q<Button>(lower+"-close"));
                    Assert.That(Field(panel,"_"+lower+"Open"),Is.False);
                    Assert.That(RuntimeWindow.BlocksWorldInput,Is.True,"Dismiss frame is consumed");
                    yield return null;
                    Assert.That(RuntimeWindow.BlocksWorldInput,Is.False);
                    Call(panel,"Toggle"+kind); yield return null;
                    Down(doc.rootVisualElement.Q(lower+"-backdrop"));
                    Assert.That(Field(panel,"_"+lower+"Open"),Is.False);
                    yield return null;
                    Assert.That(RuntimeWindow.BlocksWorldInput,Is.False);
                }
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(settings); target.Release(); Object.DestroyImmediate(target); }
        }

        [UnityTest]
        public IEnumerator ClosingGestureBlocksMouseTouchAndOtherOpenWindowsUntilRelease()
        {
            var host=new GameObject("Window gesture verification"); var doc=host.AddComponent<UIDocument>();
            var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings")); doc.panelSettings=settings;
            using var input=new InputIsolation();
            var mouse=InputSystem.AddDevice<Mouse>(); var touch=InputSystem.AddDevice<Touchscreen>();
            mouse.MakeCurrent();
            var firstCard=new VisualElement(); var secondCard=new VisualElement();
            doc.rootVisualElement.Add(firstCard); doc.rootVisualElement.Add(secondCard);
            RuntimeWindow first=null, second=null;
            first=new RuntimeWindow(doc.rootVisualElement,firstCard,()=>first.Show(false),"first");
            second=new RuntimeWindow(doc.rootVisualElement,secondCard,()=>second.Show(false),"second");
            try
            {
                first.Show(true); second.Show(true); first.Show(false);
                yield return null; Assert.That(RuntimeWindow.BlocksWorldInput,Is.True,"Another window still owns input");
                InputSystem.QueueStateEvent(mouse,new MouseState { buttons=1 }); InputSystem.Update();
                second.Show(false);
                for(var i=0;i<3;i++)
                {
                    yield return null; InputSystem.Update();
                    Assert.That(mouse.leftButton.isPressed,Is.True,"Fixture must still deliver a held physical press");
                    Assert.That(RuntimeWindow.BlocksWorldInput,Is.True,"Held mouse outlives closing frame");
                }
                InputSystem.QueueStateEvent(mouse,new MouseState()); InputSystem.Update();
                Assert.That(RuntimeWindow.BlocksWorldInput,Is.True,"Release frame remains consumed");
                yield return null; InputSystem.Update(); Assert.That(RuntimeWindow.BlocksWorldInput,Is.False);
                InputSystem.QueueStateEvent(touch,new TouchState { touchId=1,phase=UnityEngine.InputSystem.TouchPhase.Began,position=Vector2.one }); InputSystem.Update();
                RuntimeWindow.SuppressGesture();
                yield return null; InputSystem.Update(); Assert.That(RuntimeWindow.BlocksWorldInput,Is.True,"Touch holds the same latch");
                InputSystem.QueueStateEvent(touch,new TouchState { touchId=1,phase=UnityEngine.InputSystem.TouchPhase.Ended,position=Vector2.one }); InputSystem.Update();
                Assert.That(RuntimeWindow.BlocksWorldInput,Is.True);
                yield return null; InputSystem.Update(); Assert.That(RuntimeWindow.BlocksWorldInput,Is.False);
                second.Show(true); doc.rootVisualElement.Remove(secondCard);
                yield return null; Assert.That(RuntimeWindow.BlocksWorldInput,Is.False,"Detached document/window releases ownership");
            }
            finally { first.Show(false); second.Show(false); InputSystem.RemoveDevice(mouse); InputSystem.RemoveDevice(touch); Object.DestroyImmediate(host); Object.DestroyImmediate(settings); }
        }

        [UnityTest]
        public IEnumerator ActualLootWindowKeepsTransfersButConsumesWorldClicksAndCloseGesture()
        {
            SimDataFile.Require(Path.Combine(Application.dataPath,"..","SimData","simdata.json"));
            var definition=PrototypeWorldDefinitionFactory.Create(271816231);
            var world=new WorldStateFactory().Create(definition);
            var options=new SimulationSettings { TickDeltaTime=definition.Simulation.TickDeltaTime,
                MediumInterval=definition.Simulation.MediumTickInterval,SlowInterval=definition.Simulation.SlowTickInterval };
            var clock=new SimulationClock(); var engine=new SimulationEngine(world,options,clock);
            SimulationSystemRegistry.RegisterDefaults(engine); engine.Step();
            var pair=world.Entities.Npcs.Values.Where(n=>n.Faction==Faction.Colony).Take(2).ToArray();
            var actor=pair[0]; var ward=pair[1];
            world.PlayerControlledNpcs.Add(actor.Id.Value);
            actor.Mind.ManualControl=true; ward.Mind.ComaCause=ComaCause.Exhaustion; ward.Needs.Energy=0;
            actor.Inventory.Items.Clear(); ward.Inventory.Items.Clear();
            for(var i=0;i<3;i++)ward.Inventory.Items.Add(new ItemInstance("resource.stick"));
            var owner=new GameObject("Inactive test world"); owner.SetActive(false);
            var runner=owner.AddComponent<SimulationRunnerBehaviour>();
            typeof(SimulationRunnerBehaviour).GetField("_backend",Private).SetValue(runner,new LocalEngineBackend(engine,clock,options));
            var adapter=owner.AddComponent<SimulationInputAdapter>(); adapter.SetRunner(runner);
            var host=new GameObject("Actual loot window"); var panel=host.AddComponent<LootTransferPanel>(); panel.enabled=false; panel.SetRunner(runner);
            var doc=host.GetComponent<UIDocument>(); var settings=doc.panelSettings;
            var target=new RenderTexture(1920,1080,24); target.Create(); settings.targetTexture=target;
            using var input=new InputIsolation();
            var mouse=InputSystem.AddDevice<Mouse>();
            mouse.MakeCurrent();
            var selection=NpcSelection.SelectedIds.ToArray();
            var otherLoot=Object.FindObjectsByType<LootTransferPanel>(FindObjectsSortMode.None)
                .Where(p=>p!=panel).Select(p=>(Panel:p,Enabled:p.enabled)).ToArray();
            try
            {
                foreach(var p in otherLoot) p.Panel.enabled=false;
                NpcSelection.Activate(actor.Id.Value); Call(adapter,"RefreshControlSelection");
                yield return null; yield return null;
                Assert.That(runner.CanControlNpc(actor.Id),Is.True,"Fixture actor belongs to this player");
                Assert.That(runner.CreateSnapshot().Npcs.First(n=>n.Id==ward.Id).IsUnconscious,Is.True,"Fixture target is actually lootable");
                Assert.That(typeof(LootTransferPanel).GetField("_instance",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null),Is.SameAs(panel),"Fixture owns the UI singleton");
                LootTransferPanel.Open(actor.Id.Value,ward.Id.Value);
                Assert.That(LootTransferPanel.IsOpen,Is.True,"Opening uses real assigned, lootable snapshots");
                yield return null; yield return null;
                Assert.That(LootTransferPanel.IsOpen,Is.True); AssertIcon(doc.rootVisualElement,"loot-close");
                Capture(doc,target,doc.rootVisualElement.Q("loot-transfer-frame"),"loot");
                NpcSelection.PointerOverUi=false;
                Assert.That(adapter.TryHandleManualClick(Vector2.zero),Is.True);
                Assert.That(adapter.TryHandleContextClick(Vector2.zero),Is.True);
                Assert.That(engine.Commands.Count,Is.Zero,"No move, mode switch or context command while window is open");
                Down(doc.rootVisualElement.Q("loot-transfer-frame")); Assert.That(LootTransferPanel.IsOpen,Is.True);
                // Real item drag callback must survive the shared modal event handlers.
                var right=(VisualElement)Field(panel,"_rightContent");
                var name=(string)Call(panel,"ItemName","resource.stick");
                var cell=right.Query<VisualElement>().ToList().First(e=>e.tooltip==name);
                Down(cell); Assert.That(Field(panel,"_dragPrepared"),Is.True);
                var drag=Field(panel,"_drag"); Call(panel,"DropOn",actor.Id.Value,drag);
                Assert.That(Field(panel,"_quantityOpen"),Is.True,"A real stack transfer still opens its quantity picker");
                yield return null; AssertIcon(doc.rootVisualElement,"loot-quantity-close");
                Click(doc.rootVisualElement.Q<Button>("loot-quantity-close"));
                Assert.That(Field(panel,"_quantityOpen"),Is.False); Assert.That(LootTransferPanel.IsOpen,Is.True);
                Assert.That(engine.Commands.Count,Is.Zero,"Cancel cannot transfer any item");
                InputSystem.QueueStateEvent(mouse,new MouseState { buttons=1 }); InputSystem.Update();
                Click(doc.rootVisualElement.Q<Button>("loot-close"));
                Assert.That(LootTransferPanel.IsOpen,Is.False);
                for(var i=0;i<3;i++)
                {
                    yield return null; InputSystem.Update();
                    Assert.That(mouse.leftButton.isPressed,Is.True,"Read the Dynamic input buffer, not the Editor buffer after yield");
                    Assert.That(adapter.TryHandleManualClick(Vector2.zero),Is.True);
                    Assert.That(adapter.TryHandleContextClick(Vector2.zero),Is.True);
                    Assert.That(engine.Commands.Count,Is.Zero);
                }
                Assert.That(actor.Mind.ManualControl,Is.True); Assert.That(NpcSelection.SelectedIds,Does.Contain(actor.Id.Value));
                InputSystem.QueueStateEvent(mouse,new MouseState()); InputSystem.Update();
                Assert.That(RuntimeWindow.BlocksWorldInput,Is.True); yield return null;
                Assert.That(RuntimeWindow.BlocksWorldInput,Is.False);
                LootTransferPanel.Open(actor.Id.Value,ward.Id.Value); yield return null;
                Down(doc.rootVisualElement.Q("loot-backdrop")); Assert.That(LootTransferPanel.IsOpen,Is.False);
            }
            finally
            {
                LootTransferPanel.Close(); InputSystem.RemoveDevice(mouse); NpcSelection.Clear();
                NpcSelection.ActivateMany(selection);
                foreach(var p in otherLoot) if (p.Panel!=null) p.Panel.enabled=p.Enabled;
                Object.DestroyImmediate(host); Object.DestroyImmediate(settings); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(owner);
            }
        }

        private sealed class InputIsolation : IDisposable
        {
            private readonly PanelInputConfiguration _configuration;
            private readonly PanelInputConfiguration.PanelInputRedirection _previous;
            private readonly GameObject _owner;
            private readonly InputSettings.BackgroundBehavior _background;
            private readonly InputSettings.UpdateMode _update;
            public InputIsolation()
            {
                _configuration=Object.FindObjectsByType<PanelInputConfiguration>(FindObjectsSortMode.None)
                    .FirstOrDefault(c=>c.isActiveAndEnabled);
                if (_configuration==null) { _owner=new GameObject("Window test input isolation"); _configuration=_owner.AddComponent<PanelInputConfiguration>(); }
                _previous=_configuration.panelInputRedirection;
                _configuration.panelInputRedirection=PanelInputConfiguration.PanelInputRedirection.Always;
                // Batchmode focus/native events must not reset the synthetic
                // held devices between frames. All test input is delivered explicitly.
                _background=InputSystem.settings.backgroundBehavior;
                _update=InputSystem.settings.updateMode;
                InputSystem.settings.backgroundBehavior=InputSettings.BackgroundBehavior.IgnoreFocus;
                InputSystem.settings.updateMode=InputSettings.UpdateMode.ProcessEventsManually;
            }
            public void Dispose()
            {
                if (_configuration!=null) _configuration.panelInputRedirection=_previous;
                InputSystem.settings.backgroundBehavior=_background;
                InputSystem.settings.updateMode=_update;
                if (_owner!=null) Object.DestroyImmediate(_owner);
            }
        }
    }
}
#endif
