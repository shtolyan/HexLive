#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class NpcBodyPickerRuntimeTests
{
    public static string[] ArmCases => new[]{"Marta|l|upright","Marta|r|upright","Marta|l|prone","Marta|r|prone",
        "Kshishtof|l|upright","Kshishtof|r|upright","Kshishtof|l|prone","Kshishtof|r|prone"};
    public static string[] Actors => new[]{"Marta","Kshishtof"};

    [UnityTest]
    public IEnumerator VisibleOutstretchedForearmAndPalmRemainPickableOutsideTheOldCoreVolumes(
        [ValueSource(nameof(ArmCases))] string value)
    {
        Assert.That(Application.isPlaying,Is.True,"Run the actual imported-body fixture in PlayMode");
        var parts=value.Split('|');var name=parts[0];var side=parts[1];var prone=parts[2]=="prone";
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        try
        {
            var animator=actor.GetComponent<Animator>();animator.enabled=false;
            actor.GetComponent<BodyBones>().Construct((ActorName)Enum.Parse(typeof(ActorName),name),646);
            // This fixture has no camera and deliberately moves far offscreen.
            // Evaluate the real imported rig and let Unity update its skinned
            // bounds before asking the old exact-mesh picker (whose first gate
            // is Renderer.bounds). Bone positions alone do not refresh bounds.
            animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController=null;animator.enabled=true;
            animator.Rebind();animator.Update(0);animator.enabled=false;
            actor.transform.localScale=Vector3.one*.7764706f;actor.transform.position=new Vector3(1000,2,1000);
            actor.transform.rotation=Quaternion.Euler(prone?90:0,32,0);
            var view=actor.GetComponent<NpcActorView>()??actor.AddComponent<NpcActorView>();view.enabled=false;
            var skin=view.PrimaryBodySkin;Assert.That(skin,Is.Not.Null);skin.enabled=true;
            skin.updateWhenOffscreen=true;
            Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&string.Equals(b.name,id,StringComparison.OrdinalIgnoreCase));
            var elbow=Bone(side+"ForearmBend");var hand=Bone(side+"Hand");var palm=Bone(side+"Mid2");
            yield return null;yield return null;
            var samplePoints=new[]{(elbow.position+hand.position)*.5f,(hand.position+palm.position)*.5f};
            WriteGeometryDiagnostic(actor,skin,samplePoints,value);
            var rows=new JArray();
            foreach(var point in samplePoints)
            {
                var ray=new Ray(point-actor.transform.forward*3,actor.transform.forward);
                Assert.That(OldCoreHit(skin,ray,10),Is.False,value+" is a concrete miss of the old torso/legs-only picker");
                var exactMeshHit=HexLive.UnityPresentation.Views.MeshRayPicker.TryIntersect(skin,ray,out var exactMeshDistance);
                Assert.That(view.TryRaycastVisibleGeometry(ray,10,out var realMeshHit),Is.True,
                    value+" must be genuinely visible imported skin, not an arbitrary point outside the body; "+
                    "point="+point+" bounds="+skin.bounds+" directMeshHit="+exactMeshHit+" directMeshDistance="+exactMeshDistance);
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hit),Is.True,value);
                Assert.That(hit,Is.InRange(2.6f,3.1f));
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,hit*.5f,out _),Is.False,"Respect a nearer target's maximum distance");
                skin.enabled=false;
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hiddenHit),Is.True,value+" clothing-hidden body");
                Assert.That(hiddenHit,Is.EqualTo(hit).Within(.00001f));skin.enabled=true;
                rows.Add(new JObject{["point"]=V(point),["realMeshDistance"]=realMeshHit,["bodyDistance"]=hit,["oldCoreMiss"]=true});
            }
            var empty=(hand.position+palm.position)*.5f+actor.transform.right*(side=="l"?-.4f:.4f);
            Assert.That(NpcBodyPicker.TryIntersect(view,new Ray(empty-actor.transform.forward*3,actor.transform.forward),10,out _),Is.False,
                "Do not replace precise limb volumes with one wide body AABB");
            var directory="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(directory);
            File.WriteAllText(directory+"/arms-"+value.Replace('|','-')+".json",new JObject{["sourcePose"]="Actual authored prefab; no mesh/pose edits",
                ["actor"]=name,["side"]=side,["prone"]=prone,["hits"]=rows}.ToString());
        }
        finally{Object.DestroyImmediate(actor);}
    }

    [UnityTest]
    public IEnumerator CachedArmReferencesFollowTheActualImportedPunchClip([ValueSource(nameof(Actors))] string name)
    {
        Assert.That(Application.isPlaying,Is.True);
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        var graph=PlayableGraph.Create("NpcBodyPicker actual arm motion");
        try
        {
            var animator=actor.GetComponent<Animator>();animator.enabled=false;
            var bones=actor.GetComponent<BodyBones>();bones.Construct((ActorName)Enum.Parse(typeof(ActorName),name),646);
            var view=actor.GetComponent<NpcActorView>()??actor.AddComponent<NpcActorView>();view.enabled=false;
            var skin=view.PrimaryBodySkin;Assert.That(skin,Is.Not.Null);
            Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&b.name==id);
            // Warm the real picker in the authored pose, before bones animate.
            var warm=(Bone("rHand").position+Bone("rMid2").position)*.5f;
            Assert.That(NpcBodyPicker.TryIntersect(view,new Ray(warm-Vector3.forward*3,Vector3.forward),10,out _),Is.True);
            var before=actor.transform.InverseTransformPoint(Bone("rHand").position);
            var clip=AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Punch A_once_to65.fbx")
                .OfType<AnimationClip>().First(c=>!c.name.StartsWith("__preview__",StringComparison.Ordinal));
            animator.enabled=true;animator.runtimeAnimatorController=null;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();
            var play=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"real punch",animator).SetSourcePlayable(play);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);graph.Play();float moved=0;var rows=new JArray();
            foreach(var time in new[]{.2f,.5f,.8f})
            {
                play.SetTime(clip.length*time);graph.Evaluate(0);
                yield return null;yield return null;
                moved=Mathf.Max(moved,Vector3.Distance(before,actor.transform.InverseTransformPoint(Bone("rHand").position)));
                foreach(var side in new[]{"l","r"})
                {
                    var point=(Bone(side+"Hand").position+Bone(side+"Mid2").position)*.5f;
                    var ray=new Ray(point-actor.transform.forward*3,actor.transform.forward);
                    Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hit),Is.True,name+"/"+side+" at "+time);
                    rows.Add(new JObject{["time"]=time,["side"]=side,["palm"]=V(point),["distance"]=hit});
                }
            }
            Assert.That(moved,Is.GreaterThan(.05f),"The real imported clip must actually move the arm");
            var directory="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(directory);
            File.WriteAllText(directory+"/arms-punch-"+name+".json",new JObject{["clip"]=AssetDatabase.GetAssetPath(clip),
                ["handMotionMetres"]=moved,["hits"]=rows,["cacheWarmedBeforeAnimation"]=true}.ToString());
        }
        finally{if(graph.IsValid())graph.Destroy();Object.DestroyImmediate(actor);}
    }

    static JArray V(Vector3 point)=>new JArray(point.x,point.y,point.z);
    static JArray M(Matrix4x4 matrix)=>new JArray(Enumerable.Range(0,16).Select(i=>matrix[i]));
    static JObject B(Bounds bounds)=>new JObject{["center"]=V(bounds.center),["size"]=V(bounds.size)};
    static void WriteGeometryDiagnostic(GameObject actor,SkinnedMeshRenderer skin,Vector3[] points,string value)
    {
        var directory="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(directory);
        var prefix=directory+"/geometry-"+value.Replace('|','-');
        var report=new JObject{["actorScale"]=V(actor.transform.lossyScale),["skinScale"]=V(skin.transform.lossyScale),
            ["skinLocalScale"]=V(skin.transform.localScale),["skinPosition"]=V(skin.transform.position),
            ["rendererBounds"]=B(skin.bounds),["rendererLocalBounds"]=B(skin.localBounds),["samples"]=new JArray(points.Select(V))};
        var variants=new JArray();
        var intersect=typeof(HexLive.UnityPresentation.Views.MeshRayPicker).GetMethod("TryIntersectArrays",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.That(intersect,Is.Not.Null,"Diagnostic must use the actual production triangle intersection algorithm");
        foreach(var useScale in new[]{true,false})
        {
            var baked=new Mesh();
            try
            {
                skin.BakeMesh(baked,useScale);var vertices=baked.vertices;var triangles=baked.triangles;
                foreach(var fullMatrix in new[]{false,true})
                {
                    var matrix=fullMatrix?skin.transform.localToWorldMatrix:Matrix4x4.TRS(skin.transform.position,skin.transform.rotation,Vector3.one);
                    var world=vertices.Select(matrix.MultiplyPoint3x4).ToArray();var bounds=new Bounds(world[0],Vector3.zero);
                    foreach(var vertex in world)bounds.Encapsulate(vertex);
                    var samples=new JArray();
                    foreach(var point in points)
                    {
                        var ray=new Ray(point-actor.transform.forward*3,actor.transform.forward);
                        var args=new object[]{vertices,triangles,matrix,ray,0f};var hit=(bool)intersect.Invoke(null,args);
                        var nearest=0;var distance=float.PositiveInfinity;var centroid=Vector3.zero;
                        for(var i=0;i+2<triangles.Length;i+=3)
                        {
                            var center=(world[triangles[i]]+world[triangles[i+1]]+world[triangles[i+2]])/3;
                            var candidate=(center-point).sqrMagnitude;if(candidate>=distance)continue;
                            nearest=i;distance=candidate;centroid=center;
                        }
                        samples.Add(new JObject{["point"]=V(point),["hit"]=hit,["distance"]=(float)args[4],
                            ["closestTriangleCentroidDistance"]=Mathf.Sqrt(distance),["closestTriangleCentroid"]=V(centroid),
                            ["closestTriangleWorld"]=new JArray(V(world[triangles[nearest]]),V(world[triangles[nearest+1]]),V(world[triangles[nearest+2]])),
                            ["closestTriangleRaw"]=new JArray(V(vertices[triangles[nearest]]),V(vertices[triangles[nearest+1]]),V(vertices[triangles[nearest+2]]))});
                    }
                    variants.Add(new JObject{["bakeUseScale"]=useScale,["matrixMode"]=fullMatrix?"renderer.localToWorld":"positionRotationUnitScale",
                        ["matrix"]=M(matrix),["bakedLocalBounds"]=B(baked.bounds),["worldBounds"]=B(bounds),["vertexCount"]=vertices.Length,["samples"]=samples});
                }
            }
            finally{Object.DestroyImmediate(baked);}
        }
        report["variants"]=variants;
        var cameraObject=new GameObject("Actual body picking diagnostic camera");var lightObject=new GameObject("Picking diagnostic light");
        var target=new RenderTexture(768,768,24);var image=new Texture2D(768,768,TextureFormat.RGB24,false);var previous=RenderTexture.active;
        try
        {
            var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;
            camera.orthographicSize=skin.bounds.extents.magnitude*1.1f;camera.nearClipPlane=.01f;camera.farClipPlane=20;
            camera.transform.position=skin.bounds.center-actor.transform.forward*4;camera.transform.LookAt(skin.bounds.center,actor.transform.up);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.18f,.22f);camera.useOcclusionCulling=false;
            var light=lightObject.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=camera.transform.rotation;
            target.Create();RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest{destination=target});
            RenderTexture.active=target;image.ReadPixels(new Rect(0,0,768,768),0,0);image.Apply();File.WriteAllBytes(prefix+".png",image.EncodeToPNG());
            var pixels=image.GetPixels32();var background=pixels[0];var changed=pixels.Count(p=>Math.Abs(p.r-background.r)+Math.Abs(p.g-background.g)+Math.Abs(p.b-background.b)>24);
            report["renderForegroundPixels"]=changed;report["renderPath"]=prefix+".png";
        }
        finally
        {
            RenderTexture.active=previous;target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(image);
            Object.DestroyImmediate(cameraObject);Object.DestroyImmediate(lightObject);
            File.WriteAllText(prefix+".json",report.ToString());
        }
    }
    // Differential control retained for this regression: a ray crossing actual
    // visible forearm/palm skin was completely outside all five old volumes.
    static bool OldCoreHit(SkinnedMeshRenderer skin,Ray ray,float maximum)
    {
        Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&b.name==id);
        var p=Bone("pelvis").position;var h=Bone("head").position;
        var l=Bone("lFoot").position;var r=Bone("rFoot").position;var lk=Bone("lShin").position;var rk=Bone("rShin").position;
        var height=Vector3.Distance(p,h)+(Vector3.Distance(p,lk)+Vector3.Distance(lk,l)+Vector3.Distance(p,rk)+Vector3.Distance(rk,r))*.5f;
        NpcPickMath.Point P(Vector3 v)=>new(v.x,v.y,v.z);
        bool Span(Vector3 a,Vector3 b,float radius)=>NpcPickMath.Capsule(P(ray.origin),P(ray.direction),P(a),P(b),radius,maximum,out _);
        return Span(p,h,height*.16f+.03f)||Span(p,lk,height*.09f+.03f)||Span(lk,l,height*.09f+.03f)||
            Span(p,rk,height*.09f+.03f)||Span(rk,r,height*.09f+.03f);
    }

    [TestCase("Marta",false)] [TestCase("Marta",true)]
    [TestCase("Kshishtof",false)] [TestCase("Kshishtof",true)]
    public void RealBodyCanBePickedAtTorsoAndLegsWhenUprightOrLyingWithBodyLayerHidden(string name,bool prone)
    {
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        try
        {
            actor.GetComponent<Animator>().enabled=false;
            actor.GetComponent<BodyBones>().Construct((ActorName)Enum.Parse(typeof(ActorName),name),646);
            actor.transform.localScale=Vector3.one*.7764706f;
            actor.transform.position=new Vector3(1000,2,1000);
            actor.transform.rotation=Quaternion.Euler(prone?90:0,32,0);
            var view=actor.AddComponent<NpcActorView>();view.enabled=false;
            var skin=view.PrimaryBodySkin;Assert.That(skin,Is.Not.Null);
            Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&string.Equals(b.name,id,StringComparison.OrdinalIgnoreCase));
            var pelvis=Bone("pelvis");var head=Bone("head");
            var points=new[]{(pelvis.position+head.position)*.5f,
                (pelvis.position+Bone("lFoot").position)*.5f,
                (pelvis.position+Bone("rFoot").position)*.5f};
            var results=new JArray();
            foreach(var point in points)
            {
                var direction=new Vector3(.3f,-.6f,1).normalized;
                var ray=new Ray(point-direction*3,direction);
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var distance),Is.True);
                Assert.That(distance,Is.InRange(2f,3f));
                // Clothing visibility may hide skin; device picking is a body
                // volume, not a test of the skin renderer's current draw bit.
                skin.enabled=false;
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hiddenHit),Is.True);
                Assert.That(hiddenHit,Is.EqualTo(distance).Within(.00001f));
                results.Add(new JObject{["point"]=new JArray(point.x,point.y,point.z),["distance"]=distance});
            }
            var output="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(output);
            File.WriteAllText(output+"/"+name+(prone?"-prone":"-upright")+".json",results.ToString());
        }
        finally {Object.DestroyImmediate(actor);}
    }
}
}
#endif
