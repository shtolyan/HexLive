#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using HexLive.UnityPresentation.Wearing.Garments;
using HexLive.UnityPresentation.Rendering;

namespace HexLive.UnityPresentation.Wearing.Editor
{
// §169 / #462: refresh only the two fitted TrailPack prefabs, never all materials.
public static class TrailPackPreparation
{
    const string Root="Assets/HexLiveContent/People";
    public static void Rebuild()
    {
        var audit=JObject.Parse(File.ReadAllText(Root+"/Validation/scale-audit.json"));
        var records=JObject.Parse(File.ReadAllText(Root+"/catalog.json"))["records"];
        var cache=(IDictionary)typeof(ActorWardrobe).GetField("Cache",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        var variants=(IDictionary)typeof(GarmentVariants).GetField("ById",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        foreach(var actor in new[]{"Marta","Kshishtof"})
        {
            string model=Root+"/Source/Models/"+actor+"_PrimalTrailPack.fbx";
            AssetDatabase.ImportAsset(model,ImportAssetOptions.ForceUpdate);
            string path=Root+"/Prefabs/Wear/"+actor+"_PrimalTrailPack.prefab";
            var original=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var materials=original.GetComponentInChildren<SkinnedMeshRenderer>().sharedMaterials;
            var source=AssetDatabase.LoadAssetAtPath<GameObject>(model);
            var go=UnityEngine.Object.Instantiate(source);
            try
            {
                go.name=original.name;
                foreach(var animator in go.GetComponentsInChildren<Animator>(true))UnityEngine.Object.DestroyImmediate(animator);
                var renderer=go.GetComponentInChildren<SkinnedMeshRenderer>();
                if(renderer.sharedMesh.subMeshCount!=materials.Length)throw new InvalidOperationException("Material slot mismatch");
                renderer.sharedMaterials=materials;
                var wear=go.AddComponent<Wear>();EditorUtility.CopySerialized(original.GetComponent<Wear>(),wear);
                if(wear.Layer!=VisualWearLayer.Bags)throw new InvalidOperationException("TrailPack must remain Bags");
                PrefabUtility.SaveAsPrefabAsset(go,path);
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
            string id="gear.backpack_primal_trail"+(actor=="Marta"?"":"_male");
            var row=records.Single(r=>(string)r["id"]==id);
            var oldCache=cache[id];var oldDef=variants[id];
            cache[id]=new List<Wear>{AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Wear>()};
            variants[id]=AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
            try
            {
                foreach(var hanging in new[]{false,true})
                {
                    var drop=hanging?GarmentDropFactory.BuildHanging(id):GarmentDropFactory.Build(id);
                    try
                    {
                        drop.transform.localScale=Vector3.one*HexWorldRenderer.ActorScale;
                        if(!ObjectFit.WorldBounds(drop,out var bounds))throw new InvalidOperationException("Missing ground geometry");
                        drop.transform.position-=Vector3.up*bounds.min.y;ObjectFit.WorldBounds(drop,out bounds);
                        var target=audit["rows"].Single(r=>(string)r["id"]==id&&(bool)r["hanging"]==hanging);
                        target["bounds"]=new JObject{["min"]=new JArray(bounds.min.x,bounds.min.y,bounds.min.z),["max"]=new JArray(bounds.max.x,bounds.max.y,bounds.max.z),["size"]=new JArray(bounds.size.x,bounds.size.y,bounds.size.z)};
                    }
                    finally{if(drop!=null)UnityEngine.Object.DestroyImmediate(drop);}
                }
            }
            finally{if(oldCache==null)cache.Remove(id);else cache[id]=oldCache;if(oldDef==null)variants.Remove(id);else variants[id]=oldDef;}
        }
        File.WriteAllText(Root+"/Validation/scale-audit.json",audit.ToString());
        Debug.Log("TRAIL_PACK: two prefabs and four measured bounds updated; materials and other entries preserved; no bundles");
    }
}
}
#endif
