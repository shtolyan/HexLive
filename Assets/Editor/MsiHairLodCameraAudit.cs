#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

public static class MsiHairLodCameraAudit
{
    public static void Run()
    {
        const string root="Assets/HexLiveContent/People";
        const string output="Build/MSIVisualAudit/HairLodCamera";
        Directory.CreateDirectory(output);
        var manifest=JObject.Parse(File.ReadAllText(root+"/Source/source-manifest.json"));
        var results=new JArray();float oldBias=QualitySettings.lodBias;int oldMaximum=QualitySettings.maximumLODLevel;
        QualitySettings.lodBias=1;QualitySettings.maximumLODLevel=0;
        try
        {
            foreach(var row in manifest["models"].Where(m=>(string)m["kind"]=="hair"&&((string)m["id"]).EndsWith("_LOD0")))
            {
                string style=((string)row["id"]).Replace("_LOD0","");
                var scene=EditorSceneManager.NewPreviewScene();
                var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(root+"/Prefabs/Actors/Marta.prefab"));
                SceneManager.MoveGameObjectToScene(actor,scene);
                var cameraObject=new GameObject("HairLODProbeCamera");SceneManager.MoveGameObjectToScene(cameraObject,scene);
                var camera=cameraObject.AddComponent<Camera>();camera.scene=scene;camera.enabled=false;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                camera.fieldOfView=30;camera.aspect=1;camera.nearClipPlane=.001f;camera.farClipPlane=1000;
                camera.GetUniversalAdditionalCameraData().renderPostProcessing=false;
                var target=new RenderTexture(512,512,24,RenderTextureFormat.ARGB32);target.Create();camera.targetTexture=target;
                var green=new Material(Shader.Find("Universal Render Pipeline/Unlit"));green.SetColor("_BaseColor",Color.green);
                var red=new Material(green);red.SetColor("_BaseColor",Color.red);
                try
                {
                    actor.GetComponent<Animator>().enabled=false;
                    var body=actor.GetComponent<BodyBones>();body.Construct(ActorName.Marta,123);
                    body.SetHair(AssetDatabase.LoadAssetAtPath<GameObject>(root+"/Prefabs/Hair/"+style+".prefab").GetComponent<Wear>());
                    var group=body.HairInstance.GetComponentInChildren<LODGroup>();var lods=group.GetLODs();
                    var hair=lods.Select(l=>l.renderers.Single()).ToArray();
                    foreach(var renderer in actor.GetComponentsInChildren<Renderer>(true))renderer.enabled=hair.Contains(renderer);
                    for(int i=0;i<2;i++)
                    {
                        hair[i].sharedMaterials=Enumerable.Repeat(i==0?green:red,hair[i].sharedMaterials.Length).ToArray();
                        hair[i].SetPropertyBlock(null);
                        for(int slot=0;slot<hair[i].sharedMaterials.Length;slot++)hair[i].SetPropertyBlock(null,slot);
                        hair[i].shadowCastingMode=ShadowCastingMode.Off;
                    }
                    group.enabled=true;group.ForceLOD(-1);group.RecalculateBounds();
                    var scale=group.transform.lossyScale;
                    float worldSize=group.size*Mathf.Max(Mathf.Abs(scale.x),Mathf.Abs(scale.y),Mathf.Abs(scale.z));
                    var center=group.transform.TransformPoint(group.localReferencePoint);
                    var samples=new JArray();
                    float[] heights={.4f,(lods[0].screenRelativeTransitionHeight+lods[1].screenRelativeTransitionHeight)*.5f,lods[1].screenRelativeTransitionHeight*.25f};
                    for(int test=0;test<heights.Length;test++)
                    {
                        float distance=worldSize/(2*Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f)*heights[test]);
                        camera.transform.position=center+Vector3.forward*distance;camera.transform.LookAt(center);
                        RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
                        var previous=RenderTexture.active;RenderTexture.active=target;
                        var image=new Texture2D(512,512,TextureFormat.RGB24,false);
                        image.ReadPixels(new Rect(0,0,512,512),0,0);image.Apply();RenderTexture.active=previous;
                        var pixels=image.GetPixels32();int greenPixels=pixels.Count(p=>p.g>80&&p.r<40),redPixels=pixels.Count(p=>p.r>80&&p.g<40);
                        File.WriteAllBytes(output+"/"+style+"_"+test+".png",image.EncodeToPNG());Object.DestroyImmediate(image);
                        bool passed=test==0?greenPixels>0&&redPixels==0:test==1?redPixels>0&&greenPixels==0:greenPixels==0&&redPixels==0;
                        samples.Add(new JObject{["expectedLod"]=test==2?-1:test,["distanceMetres"]=distance,["greenPixels"]=greenPixels,["redPixels"]=redPixels,["passed"]=passed});
                        if(!passed){File.WriteAllText(output+"/failure.json",new JObject{["style"]=style,["samples"]=samples}.ToString());throw new Exception(style+": automatic LOD selection failed at sample "+test);}
                    }
                    results.Add(new JObject{["style"]=style,["samples"]=samples});
                    Debug.Log("HAIR_CAMERA_LOD "+style);
                }
                finally
                {
                    Object.DestroyImmediate(green);Object.DestroyImmediate(red);target.Release();Object.DestroyImmediate(target);
                    Object.DestroyImmediate(actor);Object.DestroyImmediate(cameraObject);EditorSceneManager.ClosePreviewScene(scene);
                }
            }
        }
        finally{QualitySettings.lodBias=oldBias;QualitySettings.maximumLODLevel=oldMaximum;}
        File.WriteAllText(output+"/audit.json",new JObject{["passed"]=results.Count==16,["diagnosticColours"]=true,["styles"]=results}.ToString());
    }
}
#endif
