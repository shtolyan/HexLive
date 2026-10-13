#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object=UnityEngine.Object;

public static class MsiPreparedHairAudit
{
    const string Root="Assets/HexLiveContent/People";
    const string Output="Build/MSIVisualAudit/PreparedHair";
    public static void Run()
    {
        Directory.CreateDirectory(Output);
        var manifest=JObject.Parse(File.ReadAllText(Root+"/Source/source-manifest.json"));
        var render=typeof(MsiPeopleVisualAudit).GetMethod("Render",BindingFlags.NonPublic|BindingFlags.Static);
        var clip=AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Male@Run.fbx").OfType<AnimationClip>().First(c=>!c.name.StartsWith("__preview__"));
        var results=new JArray();
        foreach(var row in manifest["models"].Where(r=>(string)r["kind"]=="hair"&&((string)r["id"]).EndsWith("_LOD0")))
        {
            string style=((string)row["id"]).Replace("_LOD0","");
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Hair/"+style+".prefab");
            var dependencies=AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab),true);
            if(dependencies.Any(p=>p.StartsWith("Assets/ImportedActors/")))throw new Exception(style+": legacy source dependency");
            var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Actors/Marta.prefab"));
            var graph=PlayableGraph.Create("PreparedHairRun");
            try
            {
                var animator=actor.GetComponent<Animator>();animator.enabled=false;
                var body=actor.GetComponent<BodyBones>();body.Construct(ActorName.Marta,123);
                body.SetHair(prefab.GetComponent<Wear>());
                var groups=body.HairInstance.GetComponentsInChildren<LODGroup>(true);
                if(groups.Length!=1)throw new Exception(style+": LODGroup count");
                var group=groups[0];var levels=group.GetLODs();
                if(levels.Length!=2||levels.Any(l=>l.renderers.Length!=1))throw new Exception(style+": invalid LOD renderers");
                var hairs=levels.Select(l=>(SkinnedMeshRenderer)l.renderers[0]).ToArray();
                if(hairs[0]==hairs[1])throw new Exception(style+": duplicate LOD renderer");
                foreach(var hair in hairs)
                {
                    if(hair.bones.Any(b=>b==null||b.parent!=body.GetBone(b.name)))throw new Exception(style+": bone not connected to actor");
                    if(hair.sharedMaterials.Length!=hair.sharedMesh.subMeshCount)throw new Exception(style+": material count mismatch");
                    foreach(var material in hair.sharedMaterials)
                    {
                        if(material==null)throw new Exception(style+": missing material");
                        string key=Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(material)).Replace("__Base","");
                        var sourceMaterial=manifest["materials"][key];
                        if(sourceMaterial==null)throw new Exception(style+": material missing from manifest "+key);
                        bool textureRequired=sourceMaterial["images"].Any();
                        if((textureRequired&&material.GetTexture("_BaseMap")==null)||!material.IsKeywordEnabled("_ALPHATEST_ON")||material.GetFloat("_Cull")!=0)
                            throw new Exception(style+": source alpha material contract "+key);
                    }
                }
                foreach(var bone in hairs[0].bones)
                {
                    var low=hairs[1].bones.FirstOrDefault(b=>b.name==bone.name);
                    if(low!=null&&low!=bone)throw new Exception(style+": LOD skeleton is duplicated");
                }
                var head=body.GetBone("head");
                var bounds=new Bounds(head.position+Vector3.up*.06f,new Vector3(.5f,.65f,.5f));
                group.enabled=false;
                for(int lod=0;lod<2;lod++)
                {
                    foreach(var hair in hairs)hair.enabled=hair==hairs[lod];
                    if(hairs.Count(h=>h.enabled)!=1)throw new Exception("Expected exactly one visible hair LOD");
                    foreach(float angle in new[]{0f,90f,180f,270f})
                        render.Invoke(null,new object[]{actor,bounds,angle,Output+"/"+style+"_LOD"+lod+"_"+angle+".png"});
                }
                animator.enabled=true;animator.runtimeAnimatorController=null;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();
                var playable=AnimationClipPlayable.Create(graph,clip);
                var output=AnimationPlayableOutput.Create(graph,"Run",animator);output.SetSourcePlayable(playable);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);graph.Play();
                float maxSpan=0,maxHairMotion=0,footMotion=0;Vector3 firstFoot=Vector3.zero;Vector3[][] first=null;
                for(int frame=0;frame<=18;frame++)
                {
                    playable.SetTime(clip.length*frame/18d);graph.Evaluate(0);
                    var foot=body.GetBone("lFoot").position;
                    if(frame==0)firstFoot=foot;else footMotion=Mathf.Max(footMotion,Vector3.Distance(firstFoot,foot));
                    var sampled=new Vector3[2][];
                    for(int lod=0;lod<2;lod++)
                    {
                        var baked=new Mesh();hairs[lod].BakeMesh(baked,true);
                        sampled[lod]=baked.vertices.Select(hairs[lod].transform.TransformPoint).ToArray();Object.DestroyImmediate(baked);
                        var box=new Bounds(sampled[lod][0],Vector3.zero);
                        for(int v=0;v<sampled[lod].Length;v++)
                        {
                            var p=sampled[lod][v];
                            if(!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z))throw new Exception(style+": nonfinite animation vertex");
                            box.Encapsulate(p);
                            if(first!=null)maxHairMotion=Mathf.Max(maxHairMotion,Vector3.Distance(first[lod][v],p));
                        }
                        maxSpan=Mathf.Max(maxSpan,box.size.magnitude);
                    }
                    if(frame==0)first=sampled;
                    if(frame==9)
                    {
                        foreach(var hair in hairs)hair.enabled=hair==hairs[0];
                        var movingBounds=new Bounds(head.position+Vector3.up*.06f,new Vector3(.5f,.65f,.5f));
                        render.Invoke(null,new object[]{actor,movingBounds,90f,Output+"/"+style+"_Run.png"});
                    }
                }
                if(footMotion<.01f||maxHairMotion<.001f||maxSpan>2f)throw new Exception(style+": invalid motion/bounds");
                results.Add(new JObject{["style"]=style,["runFrames"]=19,["footMotionMetres"]=footMotion,
                    ["hairMotionMetres"]=maxHairMotion,["maxHairSpanMetres"]=maxSpan,["lodThresholds"]=new JArray(levels.Select(l=>l.screenRelativeTransitionHeight)),
                    ["sharedActorSkeleton"]=true,["legacyDependencies"]=false,["renderedViews"]=9});
                File.WriteAllText(Output+"/audit.json",new JObject{["passed"]=false,["styles"]=results}.ToString());
                Debug.Log("PREPARED_HAIR_CHECK "+style);
            }
            finally{if(graph.IsValid())graph.Destroy();Object.DestroyImmediate(actor);}
        }
        File.WriteAllText(Output+"/audit.json",new JObject{["passed"]=results.Count==16,["liveWebGLVerified"]=false,["automaticLodSwitchRendered"]=false,["styles"]=results}.ToString());
    }
}
#endif
