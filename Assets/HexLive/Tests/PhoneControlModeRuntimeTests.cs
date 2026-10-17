#if UNITY_EDITOR
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
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class PhoneControlModeRuntimeTests
    {
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static object Field(object owner,string name)=>owner.GetType().GetField(name,Private).GetValue(owner);
        private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Private).SetValue(owner,value);
        private static object Call(object owner,string name,params object[] args)=>owner.GetType().GetMethod(name,Private).Invoke(owner,args);
        private static void Phone(int npc)=>typeof(CharacterPanel).GetField("_phoneControlNpc",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,npc);
        private static void Down(VisualElement element)
        {
            using var e=PointerDownEvent.GetPooled(new Event {type=EventType.MouseDown,button=0,mousePosition=element.worldBound.center});
            e.target=element; element.SendEvent(e);
        }

        [Test]
        public void StoppedVoiceCannotResurrectFromLateStateTextOrAlignment()
        {
            var voice=new WebVoiceAgent(); var lines=0; var timings=0;
            voice.Line+=(_,__)=>lines++; voice.Alignment+=_=>timings++;
            voice.Apply("[{\"type\":\"state\",\"state\":\"connecting\"}]");
            voice.Stop();
            voice.Apply("[{\"type\":\"state\",\"state\":\"listening\"},{\"type\":\"agent\",\"text\":\"late\"},{\"type\":\"align\",\"chars\":[\"a\"]}]");
            Assert.That(voice.Current,Is.EqualTo(WebVoiceAgent.Phase.Off));
            Assert.That(lines,Is.Zero); Assert.That(timings,Is.Zero);
        }

        [UnityTest]
        public IEnumerator ThreeSegmentsSelectExactModeAndCancelEveryCallPhaseWithoutManualInput()
        {
            SimDataFile.Require(Path.Combine(Application.dataPath,"..","SimData","simdata.json"));
            var definition=PrototypeWorldDefinitionFactory.Create(271816231);
            var world=new WorldStateFactory().Create(definition);
            var options=new SimulationSettings {TickDeltaTime=definition.Simulation.TickDeltaTime,
                MediumInterval=definition.Simulation.MediumTickInterval,SlowInterval=definition.Simulation.SlowTickInterval};
            var clock=new SimulationClock(); var engine=new SimulationEngine(world,options,clock);
            SimulationSystemRegistry.RegisterDefaults(engine); engine.Step();
            var actor=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
            world.PlayerControlledNpcs.Add(actor.Id.Value);
            var owner=new GameObject("Inactive phone test world"); owner.SetActive(false);
            var runner=owner.AddComponent<SimulationRunnerBehaviour>(); Set(runner,"_backend",new LocalEngineBackend(engine,clock,options));
            var input=owner.AddComponent<SimulationInputAdapter>(); input.SetRunner(runner);
            var host=new GameObject("Phone mode layout"); var doc=host.AddComponent<UIDocument>();
            var panel=host.AddComponent<CharacterPanel>(); panel.enabled=false; panel.SetRunner(runner);
            var settings=doc.panelSettings; settings.clearColor=true; settings.colorClearValue=Color.black;
            var target=new RenderTexture(1920,1080,24); target.Create(); settings.targetTexture=target;
            var selection=NpcSelection.SelectedIds.ToArray();
            try
            {
                NpcSelection.Activate(actor.Id.Value);
                var stage=(VisualElement)Field(panel,"_stage"); stage.style.display=DisplayStyle.Flex; stage.style.opacity=1;
                var ai=doc.rootVisualElement.Q("control-ai"); var manual=doc.rootVisualElement.Q("control-manual");
                var phone=doc.rootVisualElement.Q("control-call");
                Assert.That(ai,Is.Not.Null); Assert.That(manual,Is.Not.Null); Assert.That(phone,Is.Not.Null);
                foreach(var phase in new[]{"connecting","listening","speaking"})
                foreach(var desired in new[]{"ai","manual"})
                {
                    // A stale manual snapshot must not grant mouse commands while the phone owns this body.
                    actor.Mind.ManualControl=true;
                    var snapshot=runner.CreateSnapshot().Npcs.First(n=>n.Id==actor.Id);
                    Set(panel,"_agentVoiceNpc",snapshot);
                    var voice=new WebVoiceAgent();
                    typeof(WebVoiceAgent).GetProperty("NpcId").SetValue(voice,actor.Id.Value);
                    voice.Apply("[{\"type\":\"state\",\"state\":\""+phase+"\"}]");
                    Set(panel,"_serverVoice",voice); Phone(actor.Id.Value);
                    Call(panel,"RefreshControlToggle",snapshot); Call(input,"RefreshControlSelection");
                    yield return null; yield return null;
                    Assert.That(phone.worldBound.xMin,Is.GreaterThan(manual.worldBound.xMin));
                    Assert.That(manual.worldBound.xMin,Is.GreaterThan(ai.worldBound.xMin));
                    Assert.That(phone.worldBound.width,Is.InRange(42,45));
                    Assert.That(phone.Q<VectorIcon>().Current,Is.EqualTo(phase=="connecting" ? VectorIcon.Kind.Phone : VectorIcon.Kind.PhoneHangUp));
                    Assert.That(ai.resolvedStyle.backgroundColor.a,Is.Zero);
                    Assert.That(manual.resolvedStyle.backgroundColor.a,Is.Zero);
                    Assert.That(phone.resolvedStyle.backgroundColor.a,Is.GreaterThan(.1f));
                    Assert.That(input.TryHandleManualClick(Vector2.zero),Is.True);
                    Assert.That(input.TryHandleContextClick(Vector2.zero),Is.True);
                    Assert.That(engine.Commands.Count,Is.Zero,"Phone mode consumes direct manual orders even with a stale snapshot");
                    if(desired=="ai") Capture(target,(VisualElement)Field(panel,"_controlButton"),phase);
                    Down(desired=="ai"?ai:manual);
                    Assert.That(voice.Current,Is.EqualTo(WebVoiceAgent.Phase.Off));
                    Assert.That(CharacterPanel.PhoneControls(actor.Id.Value),Is.False);
                    Assert.That(CharacterPanel.BlocksManualInput(actor.Id.Value),Is.True,"Await the authoritative mode snapshot before accepting manual input");
                    Call(input,"RefreshControlSelection");
                    Assert.That(input.ManualNpcId,Is.LessThan(0));
                    voice.Apply("[{\"type\":\"state\",\"state\":\"speaking\"}]");
                    Assert.That(voice.Current,Is.EqualTo(WebVoiceAgent.Phase.Off));
                    Assert.That(engine.Commands.Count,Is.EqualTo(1),"Select the chosen mode exactly once");
                    engine.Step(); Assert.That(actor.Mind.ManualControl,Is.EqualTo(desired=="manual"));
                    Call(panel,"RefreshControlToggle",runner.CreateSnapshot().Npcs.First(n=>n.Id==actor.Id));
                    Assert.That(CharacterPanel.BlocksManualInput(actor.Id.Value),Is.False);
                }
            }
            finally
            {
                Phone(-1); typeof(CharacterPanel).GetField("_controlInputNpc",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,-1);
                NpcSelection.Clear(); NpcSelection.ActivateMany(selection);
                Object.DestroyImmediate(host); Object.DestroyImmediate(settings); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(owner);
            }
        }
        private static void Capture(RenderTexture target,VisualElement window,string phase)
        {
            var old=RenderTexture.active; var bounds=window.worldBound;
            var rect=new Rect(Mathf.Max(0,bounds.x-5),Mathf.Max(0,target.height-bounds.yMax-34),bounds.width+12,bounds.height+42);
            var image=new Texture2D(Mathf.RoundToInt(rect.width),Mathf.RoundToInt(rect.height),TextureFormat.RGBA32,false);
            try
            {
                RenderTexture.active=target; image.ReadPixels(rect,0,0); image.Apply();
                Directory.CreateDirectory("Build/MSIRelease/20261014-control-modes/visual");
                File.WriteAllBytes("Build/MSIRelease/20261014-control-modes/visual/"+phase+".png",image.EncodeToPNG());
            }
            finally {RenderTexture.active=old; Object.DestroyImmediate(image);}
        }
    }
}
#endif
