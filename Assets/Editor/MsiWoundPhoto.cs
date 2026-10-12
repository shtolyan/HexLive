#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// Local visual fixture only. Uses production painters, never saves scene/assets.
public static class MsiWoundPhoto
{
    const string Root = "Assets/HexLiveContent/People";
    const string Output = "Build/MSIVisualAudit/WoundPhoto";
    static Camera camera;
    static RenderTexture target;
    static SkinTexturePainter painter;
    static int frames, shot;
    public static void Begin()
    {
        foreach(var name in new[]{"Marta","Kshishtof"})
        {
            var path=Root+"/PaintMaps/skinpos_"+name+"_g0.exr";
            var importer=(TextureImporter)AssetImporter.GetAtPath(path);
            HexLive.UnityDebug.Editor.SkinPositionMapImportPolicy.Configure(importer);
            importer.SaveAndReimport();
        }
        SessionState.SetBool("MsiWoundPhoto", true);
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
        Resume();
        EditorApplication.EnterPlaymode();
    }
    [InitializeOnLoadMethod] static void Resume()
    {
        if (!SessionState.GetBool("MsiWoundPhoto", false)) return;
        EditorApplication.playModeStateChanged -= Enter;
        EditorApplication.playModeStateChanged += Enter;
    }
    static void Enter(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        SessionState.EraseBool("MsiWoundPhoto");
        EditorApplication.playModeStateChanged -= Enter;
        try
        {
            ShaderUtil.allowAsyncCompilation = false;
            Directory.CreateDirectory(Output);
            foreach (var type in new[] { typeof(PaintPointMap), typeof(SkinPositionMapSet) })
            {
                var cache = (IDictionary)type.GetField("Cache", BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
                cache.Clear();
                foreach (var guid in AssetDatabase.FindAssets("t:"+type.Name, new[]{Root+"/PaintMaps"}))
                {
                    var asset = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid), type);
                    cache[asset.name] = asset;
                }
            }
            var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Actors/Marta.prefab"));
            actor.GetComponent<Animator>().enabled = false;
            var bones = actor.GetComponent<BodyBones>(); bones.Construct(ActorName.Marta, 713);
            bones.SetHair(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Hair/LoonaHair.prefab").GetComponent<Wear>());
            foreach(var item in new[]{"PrimalBriefs","PrimalTop"})
                bones.Equip("photo_"+item, AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Wear/Marta_"+item+".prefab").GetComponent<Wear>(), null);
            foreach(var lod in actor.GetComponentsInChildren<LODGroup>()) lod.ForceLOD(0);
            if(!ActorBodyResolver.TryResolve(actor,out var body,out var error)) throw new Exception(error);
            painter = actor.AddComponent<SkinTexturePainter>();
            painter.Construct(body,new[]{0},bones,actor.transform,713,"Marta");
            var zones = new HashSet<string>{"Head","Torso","ArmL","ArmR","LegL","LegR"};
            var wounds = new List<(string zone,int seed,float heal)>();
            var damage = new List<(string zone,float damage01)>();
            var bruises = new List<(string zone,float bruise01)>();
            int seed=31;
            foreach(var zone in zones)
            {
                for(int i=0;i<3;i++) wounds.Add((zone,seed+=17,.15f));
                damage.Add((zone,.28f)); bruises.Add((zone,.18f));
            }
            painter.Sync(wounds,new HashSet<string>(),uncovered:zones,zoneDamage:damage,zoneBruise:bruises);
            var decals=actor.AddComponent<SkinDecals>(); decals.Construct(bones,actor.transform,713,new[]{body});
            // Wounds-only evidence: unresolved dirt is deliberately excluded.
            decals.Sync(null,zones,1f,0f);
            RenderSettings.ambientMode=AmbientMode.Flat; RenderSettings.ambientLight=new Color(.45f,.45f,.45f);
            var light=new GameObject("Photo key").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.4f;light.transform.rotation=Quaternion.Euler(35,155,0);
            var fill=new GameObject("Photo fill").AddComponent<Light>();fill.type=LightType.Directional;fill.intensity=.5f;fill.transform.rotation=Quaternion.Euler(20,-35,0);
            camera=new GameObject("Photo camera").AddComponent<Camera>();
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.15f,.18f);
            camera.fieldOfView=30;camera.nearClipPlane=.01f;camera.farClipPlane=30;
            target=new RenderTexture(1200,1600,24);target.Create();camera.targetTexture=target;
            SetView(); EditorApplication.update+=Tick;
        }
        catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}
    }
    static void SetView()
    {
        var full=shot%2==0;
        var rear=shot>=2;
        var center=full?new Vector3(0,.95f,0):new Vector3(0,1.18f,0);
        var offset=full?new Vector3(.65f,.08f,4.1f):new Vector3(.1f,.02f,2.05f);
        if(rear)offset.z=-offset.z;
        camera.transform.position=center+offset;
        camera.transform.LookAt(center);
        GameObject.Find("Photo key").transform.rotation=Quaternion.Euler(35,rear?-25:155,0);
        GameObject.Find("Photo fill").transform.rotation=Quaternion.Euler(20,rear?145:-35,0);
    }
    static void Tick()
    {
        try
        {
            if(!painter.TryPaintPresentation()) return;
            if(++frames<180)return;
            if(shot==0)
            {
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var stamps=(IDictionary)typeof(SkinTexturePainter).GetField("_stamps",flags).GetValue(painter);
                Debug.Log("[WoundPhoto diagnostic] stamps="+stamps.Count+" paint="+painter.SlotHasAlbedoPaint(0)+" ready="+painter.PresentationReady);
                var original=(Texture[])typeof(SkinTexturePainter).GetField("_originalAlbedo",flags).GetValue(painter);
                var materials=(Material[])typeof(SkinTexturePainter).GetField("_materials",flags).GetValue(painter);
                Debug.Log("[WoundPhoto material] renderer="+painter.Body.name+" same="+(materials[0]==painter.Body.sharedMaterials[0])+" map="+materials[0].GetTexture("_BaseMap")+" block="+painter.Body.HasPropertyBlock());
                SaveTexture(original[0], "original");
                SaveTexture(materials[0].GetTexture("_BaseMap"),"painted");
                var map=(PaintPointMap)typeof(SkinTexturePainter).GetField("_map",flags).GetValue(painter);
                var pos=(SkinPositionMapSet)typeof(SkinTexturePainter).GetField("_posMaps",flags).GetValue(painter);
                var data=pos.PositionMapOf(0);
                var read=RenderTexture.GetTemporary(data.width,data.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                Graphics.Blit(data,read);var old=RenderTexture.active;RenderTexture.active=read;
                var pixels=new Texture2D(data.width,data.height,TextureFormat.RGBAFloat,false,true);
                pixels.ReadPixels(new Rect(0,0,data.width,data.height),0,0);pixels.Apply();
                foreach(var zone in map.Zones)
                {
                    var p=zone.Points.First(q=>q.Valid);
                    Debug.Log("[WoundPhoto position] "+zone.Zone+" uv="+p.Uv+" expected="+p.BindPos+" GPU="+pixels.GetPixelBilinear(p.Uv.x,p.Uv.y)+" flip="+pixels.GetPixelBilinear(p.Uv.x,1-p.Uv.y)+" format="+data.format);
                }
                Object.DestroyImmediate(pixels);RenderTexture.active=old;RenderTexture.ReleaseTemporary(read);
                foreach(DictionaryEntry entry in stamps)
                {
                    var stamp=entry.Value;var t=stamp.GetType();
                    if(entry.Key.ToString().StartsWith("w")) Debug.Log("[WoundPhoto stamp] "+entry.Key+" pos="+t.GetField("BindPos").GetValue(stamp)+" windows="+string.Join(";",((Rect[])t.GetField("Windows").GetValue(stamp)).Select(r=>r.ToString())));
                }
            }
            var previous=RenderTexture.active;RenderTexture.active=target;
            var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();
            var names=new[]{"front","front-close","rear","rear-close"};
            File.WriteAllBytes(Output+"/marta-wounds-only-"+names[shot]+".png",image.EncodeToPNG());
            Object.DestroyImmediate(image);RenderTexture.active=previous;
            if(++shot<4){frames=0;SetView();return;}
            Debug.Log("[MsiWoundPhoto] Captured real SkinTexturePainter wounds; dirt disabled; 18 wounds; native Unity, not live WebGL.");
            EditorApplication.update-=Tick;EditorApplication.Exit(0);
        }
        catch(Exception e){Debug.LogException(e);EditorApplication.update-=Tick;EditorApplication.Exit(1);}
    }
    static void SaveTexture(Texture texture,string name)
    {
        var rt=RenderTexture.GetTemporary(texture.width,texture.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
        Graphics.Blit(texture,rt);var prev=RenderTexture.active;RenderTexture.active=rt;
        var tex=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
        tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);tex.Apply();
        File.WriteAllBytes(Output+"/"+name+".png",tex.EncodeToPNG());
        Object.DestroyImmediate(tex);RenderTexture.active=prev;RenderTexture.ReleaseTemporary(rt);
    }
}
#endif
