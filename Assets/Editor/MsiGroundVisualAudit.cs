using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Rendering;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static class MsiGroundVisualAudit
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    public static void Run()
    {
        try
        {
            var dir = Path.GetFullPath("Build/MSIAudit/20261013-ground/" +
                (Array.IndexOf(Environment.GetCommandLineArgs(), "-ground-after") >= 0 ? "after" : "before"));
            Directory.CreateDirectory(dir);
            var rows = new JArray();
            foreach (var id in new[] {"resource.board", "resource.stick", "resource.stone", "item.bandage",
                         "resource.cloth", "item.pill", "tool.hammer", "food.coconut_open",
                         "resource.log", "resource.palm_leaf", "resource.rope", "med.splint",
                         "tool.axe_stone", "tool.knife", "tool.pickaxe_stone", "tool.saw", "tool.spear", "tool.bottle",
                         "food.meat_raw", "food.meat_cooked", "item.plaster", "resource.arrow", "resource.fiber",
                         "resource.herb_leaf", "resource.hide", "resource.mechanical_part", "food.coconut",
                         "food.coconut_pierced", "tool.bow", "tool.lighter", "tool.machete"})
                rows.Add(Pile(id, dir));
            Carcass(dir, rows);
            File.WriteAllText(Path.Combine(dir, "measured.json"), rows.ToString());
            Debug.Log("MSI_GROUND_AUDIT PASS " + dir);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static JObject Pile(string id, string dir)
    {
        var path = (string)typeof(AtomicContentBatchBuild).GetMethod("FindObjectMain",
            BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] {id});
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (source == null) throw new Exception("Missing canonical source: " + id);
        var root = new GameObject(id);
        var renderer = root.AddComponent<HexWorldRenderer>(); renderer.enabled = false;
        var fit = typeof(HexWorldRenderer).GetMethod("FitObjectPrefab", Private);
        GroundPileCatalog.TryGet(id, false, out var profile);
        var count = GroundPileCatalog.Capacity(id);
        var slots = new JArray(); var union = new Bounds();
        for (var i=0; i<count; i++)
        {
            var holder = new GameObject("slot " + i); holder.transform.SetParent(root.transform, false);
            var instance = UnityEngine.Object.Instantiate(source, holder.transform);
            fit.Invoke(renderer, new object[] {instance, id, i+1, false, true});
            var pile = holder.AddComponent<GroundPileLayout>();
            pile.Initialize(instance.transform, profile); pile.Apply(i, count, i+1);
            ObjectFit.WorldBounds(instance, out var bounds);
            if (i==0) union=bounds; else union.Encapsulate(bounds);
            slots.Add(new JObject { ["min"]=V(bounds.min), ["max"]=V(bounds.max),
                ["vertexMinY"]=MinimumVertexY(instance) });
            var admitted=profile.Single.At(profile.Slot(i,count,i+1));
            const float epsilon=.00005f;
            if(bounds.min.x<admitted.MinX-epsilon || bounds.max.x>admitted.MaxX+epsilon ||
                bounds.min.y<admitted.MinY-epsilon || bounds.max.y>admitted.MaxY+epsilon ||
                bounds.min.z<admitted.MinZ-epsilon || bounds.max.z>admitted.MaxZ+epsilon)
                throw new Exception("Model escapes admission: " + id + " slot " + i);
        }
        Render(root, union, Path.Combine(dir,id+".png"));
        UnityEngine.Object.DestroyImmediate(root);
        return new JObject { ["id"]=id, ["source"]=path, ["capacity"]=count, ["slots"]=slots };
    }

    static void Carcass(string dir, JArray rows)
    {
        var config = AssetDatabase.LoadAssetAtPath<MobConfig>("Assets/HexLiveContent/RuntimeSource/Mobs/crab.asset");
        MobLibrary.Register(config);
        var handles = (IDictionary)typeof(ContentPrefabCache).GetField("Handles", BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        handles["mob/crab"] = Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>), Private,
            null, new object[] {config.prefab, (Action)(()=>{})}, null);
        var host=new GameObject("crab audit"); var renderer=host.AddComponent<HexWorldRenderer>(); renderer.enabled=false;
        var live=(GameObject)typeof(HexWorldRenderer).GetMethod("CreateMobView",Private).Invoke(renderer,new object[]{"crab",7});
        var views=(Dictionary<int,GameObject>)typeof(HexWorldRenderer).GetField("_crabViews",Private).GetValue(renderer);
        views[7]=live;
        ObjectFit.WorldBounds(live,out var liveBounds);
        var ray=new Ray(liveBounds.center+Vector3.up,Vector3.down);
        if(!renderer.TryPickWildlife(ray,out var selected) || selected != -8)
            throw new Exception("Actual crab cannot be picked");
        HoverHighlight.Active.Apply(renderer.WildlifeRenderers(selected));
        Render(live,liveBounds,Path.Combine(dir,"crab.highlight.png"));
        HoverHighlight.Active.Clear(renderer.WildlifeRenderers(selected));
        live.SetActive(false);
        if(renderer.TryPickWildlife(ray,out _)) throw new Exception("Hidden crab is pickable");
        rows.Add(new JObject{["id"]="crab.pick",["targetId"]=selected,["visiblePick"]=true,["hiddenRejected"]=true});
        UnityEngine.Object.DestroyImmediate(live);
        var dead=(GameObject)typeof(HexWorldRenderer).GetMethod("CreateObjectView",Private).Invoke(renderer,
            new object[] {new ObjectSnapshot {Id=new ObjectId(360),DefinitionId="carcass.animal",Variant="crab"},
                new Dictionary<int,Float2>(),100});
        ObjectFit.WorldBounds(dead,out var bounds);
        var minimum=MinimumVertexY(dead);
        if(Array.IndexOf(Environment.GetCommandLineArgs(),"-ground-after")>=0 &&
            Mathf.Abs(minimum-dead.transform.position.y)>.00005f)
            throw new Exception("Crab has no real ground contact");
        rows.Add(new JObject { ["id"]="carcass.crab",["surfaceY"]=dead.transform.position.y,
            ["boundsMinY"]=bounds.min.y,["vertexMinY"]=minimum,["gap"]=minimum-dead.transform.position.y });
        Render(dead,bounds,Path.Combine(dir,"carcass.crab.png"),dead.transform.position.y,true);
        // Reconstruct only the old AABB seating, retaining identical model,
        // camera and ground so the before/after contact comparison is readable.
        var body=dead.transform.GetChild(0);var seated=body.position;
        body.position+=Vector3.up*(dead.transform.position.y-bounds.min.y);
        ObjectFit.WorldBounds(dead,out var oldBounds);
        Render(dead,oldBounds,Path.Combine(dir,"carcass.crab.old-contact.png"),dead.transform.position.y,true);
        body.position=seated;
        for(var id=1;id<=8;id++)
        {
            var specimen=(GameObject)typeof(HexWorldRenderer).GetMethod("CreateObjectView",Private).Invoke(renderer,
                new object[]{new ObjectSnapshot{Id=new ObjectId(id),DefinitionId="carcass.animal",Variant="crab"},new Dictionary<int,Float2>(),100});
            if(Mathf.Abs(MinimumVertexY(specimen)-specimen.transform.position.y)>.00005f)
                throw new Exception("Crab contact failed for id " + id);
            UnityEngine.Object.DestroyImmediate(specimen);
        }
        rows.Add(new JObject{["id"]="carcass.yaw-contact",["samples"]=8,["passed"]=true});
        UnityEngine.Object.DestroyImmediate(dead); UnityEngine.Object.DestroyImmediate(host);
    }

    static float MinimumVertexY(GameObject root)
    {
        var y=float.PositiveInfinity;
        foreach(var filter in root.GetComponentsInChildren<MeshFilter>())
            foreach(var vertex in filter.sharedMesh.vertices) y=Mathf.Min(y,filter.transform.TransformPoint(vertex).y);
        foreach(var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh=new Mesh(); renderer.BakeMesh(mesh);
            foreach(var vertex in mesh.vertices) y=Mathf.Min(y,renderer.transform.TransformPoint(vertex).y);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
        return y;
    }
    static JArray V(Vector3 v)=>new JArray(v.x,v.y,v.z);
    static void Render(GameObject root, Bounds bounds, string path, float surface=0, bool contact=false)
    {
        var preview=new PreviewRenderUtility();
        var copy=UnityEngine.Object.Instantiate(root); preview.AddSingleGO(copy);
        var originals=root.GetComponentsInChildren<Renderer>();
        var copies=copy.GetComponentsInChildren<Renderer>();
        var block=new MaterialPropertyBlock();
        for(var r=0;r<originals.Length;r++)
            for(var m=0;m<originals[r].sharedMaterials.Length;m++)
            { originals[r].GetPropertyBlock(block,m); copies[r].SetPropertyBlock(block,m); }
        var plane=GameObject.CreatePrimitive(PrimitiveType.Cube); preview.AddSingleGO(plane);
        plane.transform.position=new Vector3(bounds.center.x,surface-.01f,bounds.center.z);
        plane.transform.localScale=new Vector3(Mathf.Max(2,bounds.size.x+1),.02f,Mathf.Max(2,bounds.size.z+1));
        var camera=preview.camera; camera.orthographic=true; camera.aspect=1.5f;
        camera.orthographicSize=Mathf.Max(.16f,Mathf.Max(bounds.size.x/1.5f,bounds.size.y+bounds.size.z)*.62f);
        camera.transform.position=bounds.center+new Vector3(0,2,-3); camera.transform.LookAt(bounds.center);
        if(contact)
        {
            var target=new Vector3(bounds.center.x,surface+.07f,bounds.center.z);
            camera.orthographicSize=.16f;
            camera.transform.position=target+new Vector3(0,.04f,-1);camera.transform.LookAt(target);
        }
        camera.nearClipPlane=.01f;camera.farClipPlane=50;camera.backgroundColor=new Color(.16f,.18f,.20f);
        camera.clearFlags=CameraClearFlags.SolidColor;
        preview.lights[0].intensity=1.1f; preview.lights[0].transform.rotation=Quaternion.Euler(35,140,0);
        preview.lights[1].intensity=.55f;preview.lights[1].transform.rotation=Quaternion.Euler(-15,-60,0);
        preview.BeginStaticPreview(new Rect(0,0,900,600)); camera.Render();
        var image=preview.EndStaticPreview(); File.WriteAllBytes(path,image.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(image); preview.Cleanup();
    }
}
