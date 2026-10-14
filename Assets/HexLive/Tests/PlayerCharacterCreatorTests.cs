#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class PlayerCreatorRunner : MonoBehaviour { }
public sealed class PlayerCharacterCreatorTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static JObject Options()
    {
        var records = JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/catalog.json"))["records"];
        var hair = records.Where(r => r.Value<string>("type") == "hair").Select(r => r.Value<string>("id")).ToArray();
        return new JObject {
            ["bodies"] = new JArray(
                new JObject { ["id"]="Marta", ["sex"]="female", ["skins"]=new JArray("Marta","Molly","Jana","Jolly","Masha","Rita"),
                    ["hair"]=new JArray(new[]{"none"}.Concat(hair)), ["eyes"]=new JArray(new[]{""}.Concat(ColonistAppearance.EyeColors)),
                    ["startingClothing"]=new JArray("underwear.briefs_primal","clothing.top_primal") },
                new JObject { ["id"]="Kshishtof", ["sex"]="male", ["skins"]=new JArray("Kshishtof","Tonny"),
                    ["hair"]=new JArray(new[]{"none"}.Concat(hair)), ["eyes"]=new JArray(new[]{""}.Concat(ColonistAppearance.EyeColors)),
                    ["startingClothing"]=new JArray("underwear.briefs_primal_male") }),
            ["hair"]=new JArray(hair.Select(id=>new JObject { ["id"]=id, ["colours"]=new JArray(new[]{"prototype"}.Concat(HairTintPalette.All.Select(t=>t.Id))) })),
            ["attributes"]=new JArray("Strength","Agility","Endurance","Intelligence","Willpower","Charisma","Perception"),
            ["skills"]=new JArray("Crafting","Building","Medicine"), ["traits"]=new JArray("Neat","Slob","Brave","Coward"),
            ["attributeBudget"]=4.2, ["skillBudget"]=3.0,
            ["camps"]=new JArray(new JObject {["id"]="Colony",["hasSpace"]=true},new JObject{["id"]="Colony2",["hasSpace"]=false})
        };
    }
    [Test]
    public void PlayerPayloadHasNoWardrobeOwnershipOrBudgetAndSexPreservesAllowedHairColour()
    {
        var model = new PlayerCreatorAppearance(Options()); model.Character.Name="Test";
        model.Character.Hair="LowPonytail"; model.Character.HairColour="tint.silver";
        model.Character.Eyes="green";
        model.SetSex(true);
        Assert.That(model.Character.Body,Is.EqualTo("Kshishtof"));
        Assert.That(model.Character.Hair,Is.EqualTo("LowPonytail"));
        Assert.That(model.Character.HairColour,Is.EqualTo("tint.silver"));
        Assert.That(model.Character.Eyes,Is.EqualTo("green"));
        Assert.That(model.Character.Clothing,Is.EqualTo(new[]{"underwear.briefs_primal_male"}));
        Assert.That(model.CanSubmit,Is.True);
        var request=model.Request("fixture-request");
        Assert.That(request.Value<string>("eyes"),Is.EqualTo("green"));
        Assert.That(request.Properties().Select(p=>p.Name),Is.EquivalentTo(new[]{"requestId","name","body","skin","hair","hairColour","eyes","voice","camp","traits","attributes","skills"}));
        model.Character.Attributes[0].Value=1;model.Character.Attributes[1].Value=1;
        Assert.That(model.CanSubmit,Is.False);
        model.SetSex(false);
        Assert.That(model.Character.Clothing,Is.EqualTo(new[]{"underwear.briefs_primal","clothing.top_primal"}));
    }
    private static object Get(Type t,string field)=>t.GetField(field,Static).GetValue(null);
    [UnityTest]
    public IEnumerator TypingCyrillicNameEnablesNextImmediatelyAndSurvivesStepChanges()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("Creator keyboard input");
        var runner=host.AddComponent<PlayerCreatorRunner>();
        var doc=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
        doc.panelSettings=settings;
        var model=new PlayerCreatorAppearance(Options());
        var ui=new PlayerCharacterCreator(doc.rootVisualElement,runner,model,()=>{},()=>{},()=>{},()=>{});
        try
        {
            yield return null;
            var field=ui.Root.Q<TextField>("creatorName");
            var next=ui.Root.Q<Button>("createCharacter");
            Assert.That(next.enabledSelf,Is.False);
            Assert.That(ui.Root.Q<Label>("nameError").text,Is.EqualTo(Loc.Get("creator.error.nameRequired")));
            Assert.That(next.tooltip,Is.EqualTo(ui.Root.Q<Label>("nameError").text));
            field.Focus();yield return null;
            foreach(var character in "Ника")
            {
                using var key=KeyDownEvent.GetPooled(character,KeyCode.None,EventModifiers.None);
                ((VisualElement)doc.rootVisualElement.focusController.focusedElement).SendEvent(key);
                yield return null;
            }
            Assert.That(field.value,Is.EqualTo("Ника"));
            Assert.That(model.Character.Name,Is.EqualTo("Ника"),"Typing must update the draft before blur.");
            Assert.That(next.enabledSelf,Is.True);
            Assert.That(ui.Root.Q<Label>("nameError").ClassListContains("creator-hidden"),Is.True);
            var focused=doc.rootVisualElement.focusController.focusedElement;
            typeof(PlayerCharacterCreator).GetMethod("BuildControls",Instance).Invoke(ui,null);
            Assert.That(ui.Root.Q<TextField>("creatorName"),Is.SameAs(field),"Changing appearance controls must not recreate the input.");
            Assert.That(doc.rootVisualElement.focusController.focusedElement,Is.SameAs(focused));
            typeof(Clickable).GetMethod("SimulateSingleClick",Instance|BindingFlags.Public).Invoke(next.clickable,new object[]{null,0});
            Assert.That(typeof(PlayerCharacterCreator).GetField("_tab",Instance).GetValue(ui),Is.EqualTo("abilities"));
            typeof(PlayerCharacterCreator).GetMethod("SwitchTab",Instance).Invoke(ui,new object[]{"appearance"});
            Assert.That(ui.Root.Q<TextField>("creatorName").value,Is.EqualTo("Ника"));
            ui.SetState(true,false,Loc.Get("creator.creating"));
            Assert.That(field.enabledInHierarchy,Is.False);Assert.That(next.enabledSelf,Is.False);
            Assert.That(next.tooltip,Is.EqualTo(Loc.Get("creator.creating")));
            ui.SetState(false,true,Loc.Get("creator.pending"));
            Assert.That(field.enabledInHierarchy,Is.False);Assert.That(next.enabledSelf,Is.True,"An immutable pending request remains retryable.");
            ui.SetState(false,false,"");Assert.That(field.enabledInHierarchy,Is.True);
            field.value="   ";Assert.That(next.enabledSelf,Is.False);
            Assert.That(ui.Root.Q<Label>("nameError").text,Is.EqualTo(Loc.Get("creator.error.nameRequired")));
        }
        finally {ui.Dispose();Object.DestroyImmediate(host);Object.DestroyImmediate(settings);}
    }
    [TestCase("Ника", "")]
    [TestCase("  Толя  ", "")]
    [TestCase("", "error.nameRequired")]
    [TestCase("   ", "error.nameRequired")]
    [TestCase("Ни\tка", "error.nameControl")]
    [TestCase("Ни\nка", "error.nameControl")]
    public void NameValidationMatchesTrimmedServerRequest(string name,string expected)
    {
        var model=new PlayerCreatorAppearance(Options());model.Character.Name=name;
        Assert.That(model.NameErrorKey,Is.EqualTo(expected));
        Assert.That(model.CanSubmit,Is.EqualTo(expected.Length==0));
        model.Character.Name=new string('A',64);Assert.That(model.NameErrorKey,Is.Empty);
        model.Character.Name=new string('A',65);Assert.That(model.NameErrorKey,Is.EqualTo("error.nameTooLong"));
    }
    [UnityTest]
    public IEnumerator FinalCreateExplainsBudgetCampAndPreviewBlockersInBothLanguages()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("Creator validation feedback");var runner=host.AddComponent<PlayerCreatorRunner>();
        var doc=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));doc.panelSettings=settings;
        var model=new PlayerCreatorAppearance(Options());model.Character.Name="Ника";
        var old=Loc.Current;
        PlayerCharacterCreator ui=null;
        try
        {
            foreach(var language in new[]{Language.Russian,Language.English})
            {
                Loc.Current=language;
                ui=new PlayerCharacterCreator(doc.rootVisualElement,runner,model,()=>{},()=>{},()=>{},()=>{});
                typeof(PlayerCharacterCreator).GetMethod("SwitchTab",Instance).Invoke(ui,new object[]{"camp"});
                var next=ui.Root.Q<Button>("createCharacter");var status=ui.Root.Q<Label>("status");
                if (!ui.PreviewReady)
                { Assert.That(next.enabledSelf,Is.False);Assert.That(status.text,Is.EqualTo(Loc.Get("creator.previewLoading"))); }
                yield return Ready(ui);yield return new WaitForSecondsRealtime(.15f);
                Assert.That(next.enabledSelf,Is.True);
                var value=model.Character.Attributes[0].Value;
                model.Character.Attributes[0].Value=2;
                typeof(PlayerCharacterCreator).GetMethod("UpdateReady",Instance).Invoke(ui,null);
                Assert.That(next.enabledSelf,Is.False);Assert.That(status.text,Is.EqualTo(Loc.Get("creator.error.attributeBudget")));
                model.Character.Attributes[0].Value=value;model.Character.Skills[0].Value=2;
                typeof(PlayerCharacterCreator).GetMethod("UpdateReady",Instance).Invoke(ui,null);
                Assert.That(status.text,Is.EqualTo(Loc.Get("creator.error.skillBudget")));
                model.Character.Skills[0].Value=0;model.Character.Camp=Faction.Colony2;
                typeof(PlayerCharacterCreator).GetMethod("UpdateReady",Instance).Invoke(ui,null);
                Assert.That(next.enabledSelf,Is.False);Assert.That(status.text,Is.EqualTo(Loc.Get("creator.error.chooseCamp")));
                model.Character.Camp=Faction.Colony;
                typeof(PlayerCharacterCreator).GetMethod("UpdateReady",Instance).Invoke(ui,null);
                Assert.That(next.enabledSelf,Is.True);Assert.That(status.text,Is.Empty);
                ui.Dispose();ui=null;
            }
        }
        finally {ui?.Dispose();Loc.Current=old;Object.DestroyImmediate(host);Object.DestroyImmediate(settings);}
    }
    [TestCase("Marta")]
    [TestCase("Kshishtof")]
    public void PrimalEyeGpuChangesOnlyIrisAndDoesNotMutateSharedMaterials(string body)
    {
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/"+body+".prefab");
        var actor=Object.Instantiate(prefab);
        var appearance=actor.GetComponent<PeopleAppearance>();
        var skin=actor.GetComponentsInChildren<SkinnedMeshRenderer>().First(r=>r.sharedMaterials.Any(m=>m.name.EndsWith("_Eyes")));
        var source=skin.sharedMaterials.First(m=>m.name.EndsWith("_Eyes"));var bodyMaterial=skin.sharedMaterials[0];
        var sourceTexture=source.GetTexture("_BaseMap");
        var shader=Resources.Load<Shader>("HexLive/PeopleEyeTintBlit");Assert.That(shader,Is.Not.Null);
        var reference=new RenderTexture(256,256,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);reference.Create();
        var copy=new Material(shader);copy.SetFloat("_EnableTint",0);
        var previous=GL.sRGBWrite;var active=RenderTexture.active;
        try
        {
            GL.sRGBWrite=QualitySettings.activeColorSpace==ColorSpace.Linear;Graphics.Blit(sourceTexture,reference,copy);
            var baseline=Pixels(reference);RenderTexture reused=null;Color32[] blue=null;
            foreach(var id in ColonistAppearance.EyeColors)
            {
                Assert.That(appearance.ApplyEyes(id),Is.True,id);
                var material=skin.sharedMaterials.First(m=>m.name==source.name);
                var texture=(RenderTexture)material.GetTexture("_BaseMap");
                if(reused==null)reused=texture;else Assert.That(texture,Is.SameAs(reused),"Only one small render texture per actor");
                Assert.That(texture.width,Is.EqualTo(256));Assert.That(material.GetTextureScale("_BaseMap"),Is.EqualTo(new Vector2(8,8)));
                var pixels=Pixels(texture);var changes=0;
                for(var y=0;y<256;y++)for(var x=0;x<256;x++)
                {
                    var uv=new Vector2((x+.5f)/256,(y+.5f)/256);
                    var distance=Mathf.Min(Vector2.Distance(uv,new Vector2(506f/2048,1-1525f/2048)),Vector2.Distance(uv,new Vector2(1527f/2048,1-1530f/2048)));
                    var n=y*256+x;var delta=Mathf.Abs(pixels[n].r-baseline[n].r)+Mathf.Abs(pixels[n].g-baseline[n].g)+Mathf.Abs(pixels[n].b-baseline[n].b);
                    if(distance>.221f || distance<.047f)Assert.That(delta,Is.LessThanOrEqualTo(3),"Pupil/sclera changed at "+uv+" colour="+id);
                    else if(delta>9)changes++;
                }
                Assert.That(changes,Is.GreaterThan(300),"A palette must really recolour the iris");
                if(id=="blue")blue=pixels;
                if(id=="green")Assert.That(pixels.Where((p,n)=>p.r!=blue[n].r || p.g!=blue[n].g || p.b!=blue[n].b).Count(),Is.GreaterThan(300));
                Assert.That(skin.sharedMaterials[0],Is.SameAs(bodyMaterial));Assert.That(source.GetTexture("_BaseMap"),Is.SameAs(sourceTexture));
            }
            Assert.That(appearance.ApplyEyes(""),Is.True);
            Assert.That(skin.sharedMaterials.Any(m=>ReferenceEquals(m,source)),Is.True,"Original colour restores the original material");
        }
        finally {GL.sRGBWrite=previous;RenderTexture.active=active;reference.Release();Object.DestroyImmediate(reference);Object.DestroyImmediate(copy);Object.DestroyImmediate(actor);}
    }
    private static Color32[] Pixels(RenderTexture texture)
    {
        var previous=RenderTexture.active;var copy=new Texture2D(texture.width,texture.height,TextureFormat.RGBA32,false);
        try {RenderTexture.active=texture;copy.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0);copy.Apply();return copy.GetPixels32();}
        finally {RenderTexture.active=previous;Object.DestroyImmediate(copy);}
    }
    [UnityTest]
    public IEnumerator EyePaletteUnderPreviewAppliesPersistsAndDisablesDuringSubmission()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("Creator eyes");var runner=host.AddComponent<PlayerCreatorRunner>();var doc=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
        var target=new RenderTexture(1280,900,24);target.Create();settings.targetTexture=target;
        settings.scaleMode=PanelScaleMode.ConstantPixelSize;doc.panelSettings=settings;
        var model=new PlayerCreatorAppearance(Options());model.Character.Name="Толя";
        var old=Loc.Current;Loc.Current=Language.Russian;
        var ui=new PlayerCharacterCreator(doc.rootVisualElement,runner,model,()=>{},()=>{},()=>{},()=>{});
        try
        {
            yield return Ready(ui);for(var i=0;i<12;i++)yield return null;
            var buttons=ui.Root.Query<Button>(className:"creator-eye-button").ToList();Assert.That(buttons.Count,Is.EqualTo(9));
            Assert.That(ui.Root.Q("eyeControls").worldBound.yMin,Is.GreaterThanOrEqualTo(ui.Root.Q("preview").worldBound.yMax-.1f));
            foreach(var eye in new[]{"green","blue"})
            {
                var button=ui.Root.Q<Button>("creator-eye-"+eye);
                typeof(Clickable).GetMethod("SimulateSingleClick",Instance|BindingFlags.Public).Invoke(button.clickable,new object[]{null,0});
                Assert.That(model.Character.Eyes,Is.EqualTo(eye));Assert.That(model.Request("fixture").Value<string>("eyes"),Is.EqualTo(eye));
                yield return Ready(ui);yield return new WaitForSecondsRealtime(.15f);for(var i=0;i<12;i++)yield return null;
                Assert.That(button.ClassListContains("creator-selected"),Is.True);
                var pixels=Pixels(target);var image=new Texture2D(1280,900,TextureFormat.RGBA32,false);image.SetPixels32(pixels);image.Apply();
                Directory.CreateDirectory("Build/MSIRelease/20261014-creator-eyes/visual");
                File.WriteAllBytes("Build/MSIRelease/20261014-creator-eyes/visual/"+eye+".png",image.EncodeToPNG());Object.DestroyImmediate(image);
            }
            model.SetSex(true);Assert.That(model.Character.Eyes,Is.EqualTo("blue"));
            typeof(PlayerCharacterCreator).GetMethod("Changed",Instance).Invoke(ui,new object[]{true});
            typeof(PlayerCharacterCreator).GetMethod("BuildControls",Instance).Invoke(ui,null);yield return Ready(ui);
            ui.SetState(true,false,"");Assert.That(ui.Root.Q<Button>("creator-eye-blue").enabledInHierarchy,Is.False);
            ui.SetState(false,true,"");Assert.That(ui.Root.Q<Button>("creator-eye-blue").enabledInHierarchy,Is.False);
            ui.SetState(false,false,"");Assert.That(ui.Root.Q<Button>("creator-eye-blue").enabledInHierarchy,Is.True);
            model.Character.Eyes="unknown";Assert.That(model.CanSubmit,Is.False);
            model.Character.Eyes="blue";
            settings.targetTexture=null;target.Release();Object.DestroyImmediate(target);target=new RenderTexture(390,720,24);target.Create();settings.targetTexture=target;
            for(var i=0;i<15;i++)yield return null;
            Assert.That(ui.Root.ClassListContains("creator-compact"),Is.True);
            foreach(var button in ui.Root.Query<Button>(className:"creator-eye-button").ToList())
            {
                Assert.That(button.worldBound.xMin,Is.GreaterThanOrEqualTo(0));Assert.That(button.worldBound.xMax,Is.LessThanOrEqualTo(390));
                Assert.That(button.worldBound.yMax,Is.LessThan(ui.Root.Q("stage").worldBound.yMax));
            }
        }
        finally {ui.Dispose();Loc.Current=old;settings.targetTexture=null;target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(settings);Object.DestroyImmediate(host);}
    }
    private static void PutHandle<T>(Type cache,string field,string key,T asset) where T:Object
    {
        var handle=Activator.CreateInstance(typeof(ContentAssetHandle<T>),Instance,null,new object[]{asset,(Action)(()=>{})},null);
        ((IDictionary)Get(cache,field))[key]=handle;
    }
    private sealed class SourceFixture : IDisposable
    {
        private readonly ContentAssetService _service=ContentAssetService.Instance;
        private readonly bool _refresh, _ready;
        private readonly Dictionary<string,ContentRecord> _previous;
        private readonly Dictionary<string,ContentRecord> _records;
        public SourceFixture()
        {
            _records=(Dictionary<string,ContentRecord>)typeof(ContentAssetService).GetField("_pinned",Instance).GetValue(_service);
            _previous=new Dictionary<string,ContentRecord>(_records);
            _refresh=(bool)typeof(ContentAssetService).GetField("_refreshStarted",Instance).GetValue(_service);
            _ready=(bool)typeof(ContentAssetService).GetField("_registryReady",Instance).GetValue(_service);
            typeof(ContentAssetService).GetField("_refreshStarted",Instance).SetValue(_service,true);
            typeof(ContentAssetService).GetField("_registryReady",Instance).SetValue(_service,true);
            _records.Clear();
            var catalog=JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/catalog.json"))["records"];
            foreach(var row in catalog)
            {
                var record=new ContentRecord {type=row.Value<string>("type"),id=row.Value<string>("id"),state="active",metadata=(JObject)row["metadata"],variant=new ContentVariant()};
                _records[record.Key]=record;
                if(record.type=="actor" || record.type=="hair")
                {
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(row.Value<string>("main"));
                    Assert.That(prefab,Is.Not.Null,record.Key);
                    if(record.type=="actor")PutHandle(typeof(ContentPrefabCache),"Handles",record.Key,prefab);
                    else PutHandle(typeof(HairContent),"Hair",record.id,prefab);
                }
            }
            var appearances=ScriptableObject.CreateInstance<ActorAppearanceCatalog>();
            Assert.That(PeopleIdMap.Active,Is.True,"Fixture must represent the published Primal catalog, including active state");
            appearances.hairstyles=_records.Values.Where(r=>r.type=="hair").Select(r=>r.id).ToList();
            typeof(ActorAppearanceCatalog).GetField("_instance",Static).SetValue(null,appearances);
            typeof(ActorAppearanceCatalog).GetField("_tried",Static).SetValue(null,true);
            PutHandle<Object>(typeof(AtomicResources),"Handles","config/npcanimset#main",
                AssetDatabase.LoadAssetAtPath<NpcAnimSet>("Assets/HexLiveContent/RuntimeSource/NpcAnimSet.asset"));
            var wardrobe=(Dictionary<string,List<Wear>>)Get(typeof(ActorWardrobe),"Cache");
            foreach(var pair in new[]{("underwear.briefs_primal","Marta_PrimalBriefs"),("clothing.top_primal","Marta_PrimalTop"),("underwear.briefs_primal_male","Kshishtof_PrimalBriefs")})
                wardrobe[pair.Item1]=new List<Wear>{AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Wear/"+pair.Item2+".prefab").GetComponent<Wear>()};
        }
        public void Dispose()
        {
            _records.Clear();foreach(var pair in _previous)_records[pair.Key]=pair.Value;
            typeof(ContentAssetService).GetField("_refreshStarted",Instance).SetValue(_service,_refresh);
            typeof(ContentAssetService).GetField("_registryReady",Instance).SetValue(_service,_ready);
        }
    }
    private static IEnumerator Ready(PlayerCharacterCreator ui)
    {
        var deadline=Time.realtimeSinceStartup+15;
        while(!ui.PreviewReady && Time.realtimeSinceStartup<deadline)yield return null;
        Assert.That(ui.PreviewReady,Is.True,"Actual source actor/hair/outfit must be ready");
    }
    private static void Capture(RenderTexture target,string name)
    {
        var previous=RenderTexture.active;RenderTexture.active=target;
        var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
        image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();
        Directory.CreateDirectory("Build/MSIRelease/20261013-creator/visual");
        File.WriteAllBytes("Build/MSIRelease/20261013-creator/visual/"+name+".png",image.EncodeToPNG());
        RenderTexture.active=previous;Object.Destroy(image);
    }
    private static IEnumerator Idle(PlayerCharacterCreationFlow flow)
    {
        var deadline=Time.realtimeSinceStartup+20;
        while(typeof(PlayerCharacterCreationFlow).GetField("_work",Instance).GetValue(flow)!=null && Time.realtimeSinceStartup<deadline)yield return null;
        Assert.That(typeof(PlayerCharacterCreationFlow).GetField("_work",Instance).GetValue(flow),Is.Null);
    }
    private static void Submit(PlayerCharacterCreationFlow flow)
    {
        var routine=(IEnumerator)typeof(PlayerCharacterCreationFlow).GetMethod("Submit",Instance).Invoke(flow,null);
        typeof(PlayerCharacterCreationFlow).GetMethod("Run",Instance).Invoke(flow,new object[]{routine});
    }
    [UnityTest]
    public IEnumerator NewAccountWithJsonNullCharacterOpensEditorWithoutCreatingAnNpc()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("CreatorNewAccountNullTest");var runner=host.AddComponent<PlayerCreatorRunner>();
        var document=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));document.panelSettings=settings;
        var routes=new List<string>();var connected=false;
        IEnumerator Transport(string route,JObject body,Action<JObject,long> done)
        {
            routes.Add(route);yield return null;
            // Real AccountEndpoints /me contract before explicit creation.
            if(route=="/me")done(JObject.Parse("{\"playerId\":\"new-null-fixture\",\"character\":null,\"needsNewCharacter\":false,\"messageCode\":null}"),200);
            else if(route=="/character/options")done(Options(),200);
            else Assert.Fail("Loading must not create a character automatically.");
        }
        using var flow=new PlayerCharacterCreationFlow(document.rootVisualElement,runner,"wss://creator.invalid","fixture-not-a-credential",()=>connected=true,()=>{},Transport);
        try
        {
            yield return Idle(flow);
            var ui=(PlayerCharacterCreator)typeof(PlayerCharacterCreationFlow).GetField("_editor",Instance).GetValue(flow);
            Assert.That(ui,Is.Not.Null,"A new account must reach the actual creator, not stay on loading.");
            Assert.That(connected,Is.False);
            Assert.That(routes,Is.EqualTo(new[]{"/me","/character/options"}));
            yield return Ready(ui);
        }
        finally {flow.Dispose();WebPage.CreatorWrite("hexlive.creator."+Hash128.Compute("https://creator.invalid|new-null-fixture"),"");Object.Destroy(host);Object.Destroy(settings);}
    }
    [UnityTest]
    public IEnumerator NestedTransportFailureShowsErrorAndTheRetryButtonRecovers()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("CreatorRetryAfterException");var runner=host.AddComponent<PlayerCreatorRunner>();
        var document=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));document.panelSettings=settings;
        var reads=0;var connected=false;
        IEnumerator FailNested() {yield return null;throw new InvalidOperationException("fixture-only transport failure");}
        IEnumerator Transport(string route,JObject body,Action<JObject,long> done)
        {
            yield return null;
            if(route=="/me" && ++reads==1) {yield return FailNested();yield break;}
            if(route=="/me")done(JObject.Parse("{\"playerId\":\"retry-null-fixture\",\"character\":null,\"needsNewCharacter\":false}"),200);
            else if(route=="/character/options")done(Options(),200);
            else Assert.Fail("Retrying load must not send a create request.");
        }
        LogAssert.Expect(LogType.Error,"Player character creation stopped: InvalidOperationException");
        using var flow=new PlayerCharacterCreationFlow(document.rootVisualElement,runner,"wss://creator.invalid","fixture-not-a-credential",()=>connected=true,()=>{},Transport);
        try
        {
            yield return Idle(flow);
            var loading=(VisualElement)typeof(PlayerCharacterCreationFlow).GetField("_loading",Instance).GetValue(flow);
            Assert.That(loading.Q<Label>("status").text,Is.EqualTo(Loc.Get("creator.error.network")));
            var retry=loading.Q<Button>("retry");
            Assert.That(retry.enabledInHierarchy,Is.True);
            Assert.That(retry.ClassListContains("creator-hidden"),Is.False);
            typeof(Clickable).GetMethod("SimulateSingleClick",Instance|BindingFlags.Public).Invoke(retry.clickable,new object[]{null,0});
            yield return Idle(flow);
            var ui=(PlayerCharacterCreator)typeof(PlayerCharacterCreationFlow).GetField("_editor",Instance).GetValue(flow);
            Assert.That(ui,Is.Not.Null);Assert.That(connected,Is.False);Assert.That(reads,Is.EqualTo(2));
            yield return Ready(ui);
        }
        finally {flow.Dispose();WebPage.CreatorWrite("hexlive.creator."+Hash128.Compute("https://creator.invalid|retry-null-fixture"),"");Object.Destroy(host);Object.Destroy(settings);}
    }
    [UnityTest]
    public IEnumerator JsonNullCharacterAfterSuccessfulPostDoesNotConnectOrLeaveTheCreatorBusy()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("CreatorPostNullTest");var runner=host.AddComponent<PlayerCreatorRunner>();
        var document=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));document.panelSettings=settings;
        var player="post-null-"+Guid.NewGuid().ToString("N");var posts=0;var connected=false;
        IEnumerator Transport(string route,JObject body,Action<JObject,long> done)
        {
            yield return null;
            if(route=="/me")done(new JObject{["playerId"]=player,["character"]=JValue.CreateNull(),["needsNewCharacter"]=posts>0},200);
            else if(route=="/character/options")done(Options(),200);
            else {posts++;done(new JObject(),200);}
        }
        using var flow=new PlayerCharacterCreationFlow(document.rootVisualElement,runner,"wss://creator.invalid","fixture-not-a-credential",()=>connected=true,()=>{},Transport);
        try
        {
            yield return Idle(flow);
            var ui=(PlayerCharacterCreator)typeof(PlayerCharacterCreationFlow).GetField("_editor",Instance).GetValue(flow);
            ui.Appearance.Character.Name="Post-null fixture";yield return Ready(ui);
            Submit(flow);yield return Idle(flow);
            Assert.That(posts,Is.EqualTo(1));Assert.That(connected,Is.False);
            Assert.That(ui.Root.Q<Label>("status").text,Is.EqualTo(Loc.Get("creator.characterDied")));
            Assert.That(WebPage.CreatorRead("hexlive.characterAttempt."+player),Is.Empty);
        }
        finally {flow.Dispose();WebPage.CreatorWrite("hexlive.creator."+Hash128.Compute("https://creator.invalid|"+player),"");Object.Destroy(host);Object.Destroy(settings);}
    }
    [UnityTest]
    public IEnumerator LostCreationReplyRetriesIdenticalAttemptAndConnectsOnlyAfterAuthoritativeRead()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("CreatorTransportTest");var runner=host.AddComponent<PlayerCreatorRunner>();
        var document=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));document.panelSettings=settings;
        var player="fixture-"+Guid.NewGuid().ToString("N"); var calls=new List<JObject>();var connected=false;var created=false;
        IEnumerator Transport(string route,JObject body,Action<JObject,long> done)
        {
            yield return null;
            if(route=="/me")done(new JObject{["playerId"]=player,["needsNewCharacter"]=created?false:calls.Count>0,["character"]=created?new JObject{["alive"]=true}:JValue.CreateNull()},200);
            else if(route=="/character/options")done(Options(),200);
            else {calls.Add((JObject)body.DeepClone());created=calls.Count>1;done(new JObject{["error"]="network"},created?200:503);}
        }
        using var flow=new PlayerCharacterCreationFlow(document.rootVisualElement,runner,"wss://creator.invalid","fixture-not-a-credential",()=>connected=true,()=>{},Transport);
        try
        {
            yield return Idle(flow);
            var ui=(PlayerCharacterCreator)typeof(PlayerCharacterCreationFlow).GetField("_editor",Instance).GetValue(flow);
            ui.Appearance.Character.Name="Retry fixture";yield return Ready(ui);
            Submit(flow);yield return Idle(flow);
            Assert.That(calls.Count,Is.EqualTo(1));Assert.That(connected,Is.False);
            ui.Appearance.Character.Name="Changed after ambiguous reply";
            Submit(flow);yield return Idle(flow);
            Assert.That(calls.Count,Is.EqualTo(2));Assert.That(JToken.DeepEquals(calls[0],calls[1]),Is.True,"Retry cannot change id or payload");
            Assert.That(connected,Is.True);
            Assert.That(WebPage.CreatorRead("hexlive.characterAttempt."+player),Is.Empty);
        }
        finally {flow.Dispose();WebPage.CreatorWrite("hexlive.creator."+Hash128.Compute("https://creator.invalid|"+player),"");Object.Destroy(host);Object.Destroy(settings);}
    }
    [UnityTest]
    public IEnumerator ExistingHeroBypassesEditorAndUnauthorizedReadNeverCreates()
    {
        var host=new GameObject("CreatorAuthTest");var runner=host.AddComponent<PlayerCreatorRunner>();
        var root=new VisualElement();var routes=new List<string>();var connected=false;
        IEnumerator Existing(string route,JObject body,Action<JObject,long> done)
        {routes.Add(route);yield return null;done(new JObject{["playerId"]="existing-fixture",["needsNewCharacter"]=false,["character"]=new JObject{["alive"]=true}},200);}
        using(var flow=new PlayerCharacterCreationFlow(root,runner,"wss://creator.invalid","fixture",()=>connected=true,()=>{},Existing))
        {yield return Idle(flow);Assert.That(connected,Is.True);Assert.That(routes,Is.EqualTo(new[]{"/me"}));}
        routes.Clear();connected=false;
        IEnumerator Denied(string route,JObject body,Action<JObject,long> done)
        {routes.Add(route);yield return null;done(new JObject{["error"]="unauthorized"},401);}
        using(var flow=new PlayerCharacterCreationFlow(root,runner,"wss://creator.invalid","fixture",()=>connected=true,()=>{},Denied))
        {yield return Idle(flow);Assert.That(connected,Is.False);Assert.That(routes,Is.EqualTo(new[]{"/me"}));}
        Object.Destroy(host);
    }
    [UnityTest]
    public IEnumerator EveryPublishedHairstyleRendersOnBothBodies()
    {
        using var sources=new SourceFixture();var host=new GameObject("AllCreatorHair");var runner=host.AddComponent<PlayerCreatorRunner>();
        var parent=new VisualElement();var model=new PlayerCreatorAppearance(Options());
        var sheet=new Texture2D(192*6,192*6,TextureFormat.RGB24,false);var index=0;
        using var preview=new LobbyCharacterPreview(parent,runner,model.Character,thumbnail:true);
        try
        {
            foreach(var male in new[]{false,true})
            {
                model.SetSex(male);
                foreach(var hair in model.Hair.ToArray())
                {
                    model.Character.Hair=hair;model.Character.HairColour=hair=="none"?"prototype":"tint.chestnut";
                    preview.Refresh(model.Character);preview.FrameThumbnail(3);
                    var deadline=Time.realtimeSinceStartup+15;
                    while(!preview.IsReady && !preview.Failed && Time.realtimeSinceStartup<deadline)yield return null;
                    Assert.That(preview.IsReady,Is.True,model.Character.Body+" / "+hair);
                    var image=preview.Capture();var pixels=image.GetPixels();
                    Assert.That(pixels.Count(p=>p.r>.15f || p.g>.15f || p.b>.15f),Is.GreaterThan(100),"Visible head / "+hair);
                    sheet.SetPixels(index%6*192,(5-index/6)*192,192,192,pixels);index++;Object.Destroy(image);
                }
            }
            sheet.Apply();Directory.CreateDirectory("Build/MSIRelease/20261013-creator/visual");
            File.WriteAllBytes("Build/MSIRelease/20261013-creator/visual/all-hairstyles.png",sheet.EncodeToPNG());
            Assert.That(index,Is.EqualTo(34));
        }
        finally {preview.Dispose();Object.Destroy(sheet);Object.Destroy(host);}
    }
    [UnityTest]
    public IEnumerator RealCreatorRendersBothSexesTintedPonytailAndResponsiveControls()
    {
        using var sources=new SourceFixture();
        var host=new GameObject("PlayerCreatorVisual");var runner=host.AddComponent<PlayerCreatorRunner>();
        var document=host.AddComponent<UIDocument>();
        var settings=Object.Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
        var target=new RenderTexture(1280,720,24);target.Create();
        settings.targetTexture=target;settings.scaleMode=PanelScaleMode.ConstantPixelSize;document.panelSettings=settings;
        var model=new PlayerCreatorAppearance(Options());model.Character.Name="Алекс";
        model.Character.Hair="LowPonytail";model.Character.HairColour="tint.copper";
        var language=Loc.Current;Loc.Current=Language.Russian;
        var ui=new PlayerCharacterCreator(document.rootVisualElement,runner,model,()=>{},()=>{},()=>{},()=>{});
        try
        {
            yield return Ready(ui);
            for(var i=0;i<20;i++)yield return null;
            Assert.That(ui.Root.Q<Button>("creatorFemale"),Is.Not.Null);
            Assert.That(ui.Root.Query<Image>().ToList().Any(i=>i.vectorImage!=null),Is.True,"Control SVG icons must actually import");
            var labels=string.Join("|",ui.Root.Query<Label>().ToList().Select(l=>l.text));
            Assert.That(labels,Does.Not.Contain("Marta").And.Not.Contain("Masha").And.Not.Contain("Molly").And.Not.Contain("Kshishtof"));
            Capture(target,"creator-female");
            typeof(PlayerCharacterCreator).GetField("_appearanceTab",Instance).SetValue(ui,"hair");
            typeof(PlayerCharacterCreator).GetMethod("BuildControls",Instance).Invoke(ui,null);
            var thumbDeadline=Time.realtimeSinceStartup+12;
            while(!ui.Root.Query<Image>(className:"creator-tile-image").ToList().Any(i=>i.image!=null) && Time.realtimeSinceStartup<thumbDeadline)yield return null;
            var thumbs=ui.Root.Query<Image>(className:"creator-tile-image").ToList();
            var cache=typeof(PlayerCharacterCreator).GetField("_thumbnails",Instance).GetValue(ui);
            var thumbnailStage=(LobbyCharacterPreview)cache.GetType().GetField("_stage",Instance).GetValue(cache);
            var viewport=ui.Root.Q<ScrollView>("controls").contentViewport.worldBound;
            var thumbnailLook=thumbnailStage==null?null:(CharacterCreationConfig)typeof(LobbyCharacterPreview).GetField("_character",Instance).GetValue(thumbnailStage);
            var thumbnailView=thumbnailStage==null?null:(NpcActorView)typeof(LobbyCharacterPreview).GetField("_view",Instance).GetValue(thumbnailStage);
            Assert.That(thumbs.Any(i=>i.image!=null),Is.True,"Real hair thumbnails must render; images="+thumbs.Count+
                "; visible="+thumbs.Count(i=>viewport.Overlaps(i.worldBound))+"; viewport="+viewport+
                "; stageReady="+thumbnailStage?.IsReady+"; stageFailed="+thumbnailStage?.Failed+
                "; look="+thumbnailLook?.Body+"/"+thumbnailLook?.Skin+"/"+thumbnailLook?.Hair+"/"+thumbnailLook?.Eyes+
                "; waiting="+(thumbnailStage==null?"none":typeof(LobbyCharacterPreview).GetProperty("LoadingDependency",Instance).GetValue(thumbnailStage))+
                "; view="+(thumbnailView!=null)+"; hairReady="+thumbnailView?.CreationHairReady+
                "; clothesReady="+(thumbnailView!=null && thumbnailView.CreationClothesReady(thumbnailLook.Clothing)));
            var visibleDeadline=Time.realtimeSinceStartup+12;
            while(ui.Root.Query<Image>(className:"creator-tile-image").ToList().Count(i=>i.image!=null)<3 && Time.realtimeSinceStartup<visibleDeadline)yield return null;
            _ = ui.Root.Q<VisualElement>("preview");
            ((LobbyCharacterPreview)typeof(PlayerCharacterCreator).GetField("_preview",Instance).GetValue(ui)).FocusFace();
            for(var i=0;i<10;i++)yield return null;
            Capture(target,"creator-hair");
            model.SetSex(true);
            typeof(PlayerCharacterCreator).GetMethod("Changed",Instance).Invoke(ui,new object[]{true});
            typeof(PlayerCharacterCreator).GetMethod("BuildControls",Instance).Invoke(ui,null);
            yield return Ready(ui);
            for(var i=0;i<20;i++)yield return null;
            Capture(target,"creator-male");
            var preview=typeof(PlayerCharacterCreator).GetField("_preview",Instance).GetValue(ui);
            var camera=(Camera)typeof(LobbyCharacterPreview).GetField("_camera",Instance).GetValue(preview);
            var before=camera.transform.position;((LobbyCharacterPreview)preview).Rotate(90);
            Assert.That(Vector3.Distance(before,camera.transform.position),Is.GreaterThan(.1f));
            var orbit=camera.transform.position;
            model.Character.HairColour="tint.silver";
            typeof(PlayerCharacterCreator).GetMethod("Changed",Instance).Invoke(ui,new object[]{true});
            yield return Ready(ui);for(var i=0;i<8;i++)yield return null;
            Assert.That(Vector3.Distance(camera.transform.position,orbit),Is.LessThan(.001f),"Changing colour retains the orbit");
            Capture(target,"creator-male-side");
            settings.targetTexture=null;target.Release();Object.Destroy(target);
            target=new RenderTexture(390,720,24);target.Create();settings.targetTexture=target;
            for(var i=0;i<12;i++)yield return null;
            Assert.That(ui.Root.ClassListContains("creator-compact"),Is.True);
            Assert.That(ui.Root.Q<ScrollView>("controls").contentViewport.worldBound.height,Is.GreaterThan(50),"Name must not consume the whole mobile control viewport.");
            var input=ui.Root.Q<TextField>("creatorName").Q<VisualElement>(className:"unity-text-field__input");
            Assert.That(input.worldBound.height,Is.GreaterThanOrEqualTo(30));
            Assert.That(ui.Root.Q<ScrollView>("controls").Contains(ui.Root.Q<TextField>("creatorName")),Is.True);
            Capture(target,"creator-compact");
        }
        finally {ui.Dispose();Loc.Current=language;settings.targetTexture=null;target.Release();Object.Destroy(target);Object.Destroy(settings);Object.Destroy(host);}
    }
}
}
#endif
