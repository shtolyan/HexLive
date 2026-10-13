#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class TrailPackFitTests
{
    const string Root = "Assets/HexLiveContent/People";
    [OneTimeSetUp]
    public void PrepareFittedSources()
    {
        if(Environment.GetEnvironmentVariable("HEXLIVE_TRAILPACK_PHASE")=="before")return;
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityPresentation.Wearing.Editor.TrailPackPreparation")).First(t=>t!=null);
        type.GetMethod("Rebuild").Invoke(null,null);
    }
    static Vector3[] Vertices(SkinnedMeshRenderer skin)
    {
        var mesh = new Mesh();
        try { skin.BakeMesh(mesh, true); return mesh.vertices.Select(skin.transform.TransformPoint).ToArray(); }
        finally { Object.DestroyImmediate(mesh); }
    }
    [TestCase("Marta","PrimalTrailPack")]
    [TestCase("Kshishtof","PrimalTrailPack")]
    [TestCase("Marta","PrimalGathererPack")]
    [TestCase("Kshishtof","PrimalGathererPack")]
    [TestCase("Marta","PrimalHunterPack")]
    [TestCase("Kshishtof","PrimalHunterPack")]
    public void TrailStrapsClearBothBodiesThroughProductionEquipAndAnimation(string actor,string packName)
    {
        var phase = Environment.GetEnvironmentVariable("HEXLIVE_TRAILPACK_PHASE") == "before" ? "before" : "after";
        var output = "Build/MSIRelease/20261014-trailpack/" + phase;
        Directory.CreateDirectory(output);
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
        var colliderGo = new GameObject("TrailFit body surface");
        Mesh colliderMesh = null;
        var graph = PlayableGraph.Create("TrailFit actual locomotion");
        var report = new JArray();
        try
        {
            var animator = go.GetComponent<Animator>(); animator.enabled = false;
            var bones = go.GetComponent<BodyBones>(); bones.Construct((ActorName)Enum.Parse(typeof(ActorName), actor), 462);
            var restPose=bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Select(t=>(bone:t,position:t.localPosition,rotation:t.localRotation,scale:t.localScale)).ToArray();
            foreach (var item in actor == "Marta" ? new[] { "PrimalBriefs", "PrimalTop", packName } : new[] { "PrimalBriefs", packName })
                bones.Equip(item, AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + actor + "_" + item + ".prefab").GetComponent<Wear>(), null);
            var body = go.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.sharedMesh.blendShapeCount == 109);
            var pack = go.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.name.Contains(packName));
            var bindVertices = Vertices(pack);
            var initial = Vertices(body);
            var box = new Bounds(initial[0],Vector3.zero);foreach(var v in initial)box.Encapsulate(v);
            File.WriteAllText(output+"/"+actor+"-bounds.json",new JObject { ["bakedCenter"]=new JArray(box.center.x,box.center.y,box.center.z),["bakedSize"]=new JArray(box.size.x,box.size.y,box.size.z),["renderCenter"]=new JArray(body.bounds.center.x,body.bounds.center.y,body.bounds.center.z),["renderSize"]=new JArray(body.bounds.size.x,body.bounds.size.y,body.bounds.size.z) }.ToString());
            var collider = colliderGo.AddComponent<MeshCollider>();
            var connection = AnimationPlayableOutput.Create(graph, "pose", animator);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
            float worst = float.PositiveInfinity;
            foreach (var pose in new[] { "stand", "run", "jump", "breast-left", "breast-right" })
            {
                animator.Rebind();
                foreach(var poseBone in restPose){poseBone.bone.localPosition=poseBone.position;poseBone.bone.localRotation=poseBone.rotation;poseBone.bone.localScale=poseBone.scale;}
                if (pose == "run" || pose == "jump")
                {
                    var path = "Assets/ImportedActors/AnimLibrary/" + (pose == "run" ? "Male@Run" : "Jump") + ".fbx";
                    var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
                    animator.enabled = true; animator.runtimeAnimatorController = null; animator.Rebind();
                    var playable = AnimationClipPlayable.Create(graph, clip); connection.SetSourcePlayable(playable);
                    playable.SetTime(clip.length * .25); graph.Evaluate(0);
                }
                if (pose.StartsWith("breast"))
                {
                    animator.enabled = false;
                    foreach (var name in new[] { "lPectoral", "rPectoral" })
                    {
                        var bone = bones.GetBone(name);
                        if (bone != null) bone.localRotation *= Quaternion.Euler(0, pose == "breast-left" ? 5 : -5, 0);
                    }
                }
                if (colliderMesh != null) Object.DestroyImmediate(colliderMesh);
                colliderMesh = new Mesh(); body.BakeMesh(colliderMesh, true);
                collider.transform.SetPositionAndRotation(body.transform.position, body.transform.rotation);
                collider.transform.localScale = body.transform.lossyScale;
                collider.sharedMesh = colliderMesh;
                Physics.SyncTransforms();
                var bodyVertices = Vertices(body);
                float motion=bodyVertices.Select((p,i)=>Vector3.Distance(p,initial[i])).Max();
                var allTriangles=body.sharedMesh.triangles;var bodyWeights=body.sharedMesh.boneWeights;
                float HandWeight(BoneWeight w)
                {
                    bool IsHand(int bone)=>new[]{"forearm","hand","carpal","index","thumb","pinky","mid","ring"}.Any(s=>body.bones[bone].name.ToLowerInvariant().Contains(s));
                    return (IsHand(w.boneIndex0)?w.weight0:0)+(IsHand(w.boneIndex1)?w.weight1:0)+(IsHand(w.boneIndex2)?w.weight2:0)+(IsHand(w.boneIndex3)?w.weight3:0);
                }
                // The requested fit is torso/breast/shoulder contact. Forearm/hand
                // crossings are recorded separately, including reference backpacks.
                var torsoTriangles=Enumerable.Range(0,allTriangles.Length/3).Where(f=>Enumerable.Range(0,3).Sum(k=>HandWeight(bodyWeights[allTriangles[f*3+k]]))<1.5f).SelectMany(f=>new[]{allTriangles[f*3],allTriangles[f*3+1],allTriangles[f*3+2]}).ToArray();
                var surface = new Surface(bodyVertices, torsoTriangles);
                var wholeBody=new Surface(bodyVertices,allTriangles);
                var vertices = Vertices(pack); var triangles = pack.sharedMesh.triangles;
                int samples = 0, inside = 0,handCrossings=0; float minimum = float.PositiveInfinity;Vector3 minimumPoint=Vector3.zero;int minimumFace=0;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var a = vertices[triangles[i]]; var b = vertices[triangles[i + 1]]; var c = vertices[triangles[i + 2]];
                    foreach (var uv in new[] { new Vector2(1f/3,1f/3),new Vector2(.2f,.2f),new Vector2(.2f,.6f),new Vector2(.6f,.2f) })
                    {
                        var original = bindVertices[triangles[i]] * uv.x + bindVertices[triangles[i+1]] * uv.y + bindVertices[triangles[i+2]] * (1-uv.x-uv.y);
                        if (original.z < .005f || original.y < 1.18f || original.y > 1.62f) continue;
                        var p = a * uv.x + b * uv.y + c * (1 - uv.x - uv.y);
                        // Closest distance plus closed-body ray parity: a face normal at a crease is ambiguous.
                        var gap = surface.Distance(p);
                        if(wholeBody.Distance(p)<-.002f&&gap>=-.002f)handCrossings++;
                        if(gap<minimum){minimum=gap;minimumPoint=p;minimumFace=surface.LastFace;} samples++; if (gap < -.002f) inside++;
                    }
                }
                worst = Mathf.Min(worst, minimum);
                var weights=body.sharedMesh.boneWeights;var contactBones=new HashSet<string>();
                for(int k=0;k<3;k++){var w=weights[torsoTriangles[minimumFace*3+k]];if(w.weight0>.1f)contactBones.Add(body.bones[w.boneIndex0].name);if(w.weight1>.1f)contactBones.Add(body.bones[w.boneIndex1].name);if(w.weight2>.1f)contactBones.Add(body.bones[w.boneIndex2].name);if(w.weight3>.1f)contactBones.Add(body.bones[w.boneIndex3].name);}
                report.Add(new JObject { ["pose"] = pose, ["samples"] = samples, ["insideTorso"] = inside,["incidentalHandCrossings"]=handCrossings,["minimumTorsoGapMetres"] = minimum,["minimumPoint"]=new JArray(minimumPoint.x,minimumPoint.y,minimumPoint.z),["minimumBodyBones"]=new JArray(contactBones.OrderBy(s=>s)),["maximumBodyVertexMotionMetres"]=motion });
                if (pose == "stand") { Capture(go, output + "/" + actor + "-"+packName+"-front.png", new Vector3(.28f,.04f,1)); Capture(go, output + "/" + actor + "-"+packName+"-side.png", new Vector3(1,.04f,.2f)); }
            }
            File.WriteAllText(output + "/" + actor + "-"+packName+".json", report.ToString());
            Assert.That(report.All(r => (int)r["samples"] > 0), Is.True);
            Assert.That(report.Where(r=>(string)r["pose"] is "run" or "jump").All(r=>(float)r["maximumBodyVertexMotionMetres"]>.05f),Is.True,"actual clips must move the body");
            if(packName=="PrimalTrailPack")Assert.That(worst, Is.GreaterThanOrEqualTo(-.002f), actor + " strap surface clips torso/breast/shoulder; see " + output);
            Assert.That(pack.sharedMaterials, Has.Length.GreaterThan(0));
        }
        finally { if (graph.IsValid()) graph.Destroy(); if(colliderMesh!=null)Object.DestroyImmediate(colliderMesh); Object.DestroyImmediate(colliderGo);Object.DestroyImmediate(go); }
    }
    sealed class Surface
    {
        sealed class Node {public Bounds box; public int[] faces;public Node left,right;}
        readonly Vector3[] vertices;readonly int[] triangles;readonly Node root;
        public int LastFace {get;private set;}
        public Surface(Vector3[] v,int[] t){vertices=v;triangles=t;root=Build(Enumerable.Range(0,t.Length/3).ToArray());}
        Node Build(int[] faces)
        {
            var node=new Node{box=new Bounds(vertices[triangles[faces[0]*3]],Vector3.zero)};
            foreach(var f in faces)for(int k=0;k<3;k++)node.box.Encapsulate(vertices[triangles[f*3+k]]);
            if(faces.Length<=24){node.faces=faces;return node;}
            var size=node.box.size;int axis=size.x>size.y?(size.x>size.z?0:2):(size.y>size.z?1:2);
            Array.Sort(faces,(a,b)=>Center(a,axis).CompareTo(Center(b,axis)));
            node.left=Build(faces.Take(faces.Length/2).ToArray());node.right=Build(faces.Skip(faces.Length/2).ToArray());return node;
        }
        float Center(int f,int axis)=>(vertices[triangles[f*3]][axis]+vertices[triangles[f*3+1]][axis]+vertices[triangles[f*3+2]][axis])/3;
        public float Distance(Vector3 p)
        {
            float best=float.PositiveInfinity,sign=1;Search(root,p,ref best,ref sign);
            var ray=new Ray(p,new Vector3(.177f,.033f,1).normalized);var hits=new List<float>();Intersections(root,ray,hits);
            hits.Sort();int unique=0;float previous=float.NegativeInfinity;
            foreach(var hit in hits)if(hit-previous>.00001f){unique++;previous=hit;}
            return Mathf.Sqrt(best)*(unique%2==0?1:-1);
        }
        void Intersections(Node node,Ray ray,List<float> hits)
        {
            if(!node.box.IntersectRay(ray,out _))return;
            if(node.faces==null){Intersections(node.left,ray,hits);Intersections(node.right,ray,hits);return;}
            foreach(var f in node.faces)
            {
                var a=vertices[triangles[f*3]];var ab=vertices[triangles[f*3+1]]-a;var ac=vertices[triangles[f*3+2]]-a;
                var h=Vector3.Cross(ray.direction,ac);float det=Vector3.Dot(ab,h);if(Mathf.Abs(det)<1e-12f)continue;
                float inv=1/det;var s=ray.origin-a;float u=inv*Vector3.Dot(s,h);if(u<-.000001f||u>1.000001f)continue;
                var q=Vector3.Cross(s,ab);float v=inv*Vector3.Dot(ray.direction,q);if(v<-.000001f||u+v>1.000001f)continue;
                float t=inv*Vector3.Dot(ac,q);if(t>.000001f)hits.Add(t);
            }
        }
        void Search(Node node,Vector3 p,ref float best,ref float sign)
        {
            if(node.box.SqrDistance(p)>best)return;
            if(node.faces!=null)
            {
                foreach(var f in node.faces)
                {
                    var a=vertices[triangles[f*3]];var b=vertices[triangles[f*3+1]];var c=vertices[triangles[f*3+2]];
                    var nearest=Closest(p,a,b,c);float d=(p-nearest).sqrMagnitude;
                    if(d<best){best=d;LastFace=f;sign=Vector3.Dot(p-nearest,Vector3.Cross(b-a,c-a))<0?-1:1;}
                }
                return;
            }
            var first=node.left.box.SqrDistance(p)<node.right.box.SqrDistance(p)?node.left:node.right;
            Search(first,p,ref best,ref sign);Search(first==node.left?node.right:node.left,p,ref best,ref sign);
        }
        static Vector3 Closest(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
        {
            var ab=b-a;var ac=c-a;var ap=p-a;float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);
            if(d1<=0&&d2<=0)return a;var bp=p-b;float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp);
            if(d3>=0&&d4<=d3)return b;float vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            var cp=p-c;float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6)return c;
            float vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
            float va=d3*d6-d5*d4;if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0)return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
            float den=va+vb+vc;if(Mathf.Abs(den)<1e-16f)return a;return a+ab*(vb/den)+ac*(vc/den);
        }
    }
    static void Capture(GameObject actor, string path, Vector3 direction)
    {
        var cameraGo = new GameObject("TrailFit review camera");
        var keyGo = new GameObject("TrailFit review key");
        var fillGo = new GameObject("TrailFit review fill");
        var target = new RenderTexture(900,1000,24);target.Create();
        var active=RenderTexture.active;
        var image=new Texture2D(900,1000,TextureFormat.RGB24,false);
        try
        {
            foreach(var t in actor.GetComponentsInChildren<Transform>(true))t.gameObject.layer=31;
            var points=Vertices(actor.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r=>r.sharedMesh.blendShapeCount==109));
            var bounds=new Bounds(points[0],Vector3.zero);foreach(var p in points)bounds.Encapsulate(p);
            var center=new Vector3(bounds.center.x,bounds.min.y+bounds.size.y*.74f,bounds.center.z);
            var camera=cameraGo.AddComponent<Camera>();camera.cullingMask=1<<31;
            camera.fieldOfView=30;camera.nearClipPlane=.001f;camera.farClipPlane=100;
            camera.transform.position=center+direction.normalized*bounds.size.y*.85f;camera.transform.LookAt(center);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.13f,.15f,.17f);camera.targetTexture=target;
            var key=keyGo.AddComponent<Light>();key.type=LightType.Directional;key.cullingMask=1<<31;key.intensity=1.3f;key.transform.rotation=Quaternion.Euler(30,180,0);
            var fill=fillGo.AddComponent<Light>();fill.type=LightType.Directional;fill.cullingMask=1<<31;fill.intensity=.7f;fill.transform.rotation=Quaternion.Euler(-20,-40,0);
            camera.Render();camera.Render();RenderTexture.active=target;
            image.ReadPixels(new Rect(0,0,900,1000),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());
        }
        finally {RenderTexture.active=active;cameraGo.GetComponent<Camera>().targetTexture=null;Object.DestroyImmediate(image);target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(cameraGo);Object.DestroyImmediate(keyGo);Object.DestroyImmediate(fillGo);}
    }
}
}
#endif
