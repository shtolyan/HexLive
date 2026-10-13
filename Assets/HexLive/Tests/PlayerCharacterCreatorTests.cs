#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Bootstrap;
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
                    ["hair"]=new JArray(new[]{"none"}.Concat(hair)), ["eyes"]=new JArray(""),
                    ["startingClothing"]=new JArray("underwear.briefs_primal","clothing.top_primal") },
                new JObject { ["id"]="Kshishtof", ["sex"]="male", ["skins"]=new JArray("Kshishtof","Tonny"),
                    ["hair"]=new JArray(new[]{"none"}.Concat(hair)), ["eyes"]=new JArray(""),
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
        model.SetSex(true);
        Assert.That(model.Character.Body,Is.EqualTo("Kshishtof"));
        Assert.That(model.Character.Hair,Is.EqualTo("LowPonytail"));
        Assert.That(model.Character.HairColour,Is.EqualTo("tint.silver"));
        Assert.That(model.Character.Clothing,Is.EqualTo(new[]{"underwear.briefs_primal_male"}));
        Assert.That(model.CanSubmit,Is.True);
        var request=model.Request("fixture-request");
        Assert.That(request.Properties().Select(p=>p.Name),Is.EquivalentTo(new[]{"requestId","name","body","skin","hair","hairColour","eyes","voice","camp","traits","attributes","skills"}));
        model.Character.Attributes[0].Value=1;model.Character.Attributes[1].Value=1;
        Assert.That(model.CanSubmit,Is.False);
        model.SetSex(false);
        Assert.That(model.Character.Clothing,Is.EqualTo(new[]{"underwear.briefs_primal","clothing.top_primal"}));
    }
    private static object Get(Type t,string field)=>t.GetField(field,Static).GetValue(null);
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
                var record=new ContentRecord {type=row.Value<string>("type"),id=row.Value<string>("id"),metadata=(JObject)row["metadata"],variant=new ContentVariant()};
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
            if(route=="/me")done(new JObject{["playerId"]=player,["needsNewCharacter"]=!created,["character"]=new JObject{["alive"]=created}},200);
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
            Assert.That(ui.Root.Query<Image>(className:"creator-tile-image").ToList().Any(i=>i.image!=null),Is.True,"Real hair thumbnails must render");
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
            Capture(target,"creator-compact");
        }
        finally {ui.Dispose();Loc.Current=language;settings.targetTexture=null;target.Release();Object.Destroy(target);Object.Destroy(settings);Object.Destroy(host);}
    }
}
}
#endif
