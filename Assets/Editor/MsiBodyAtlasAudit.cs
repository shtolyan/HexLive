#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object=UnityEngine.Object;

// Native Unity evidence only. No scenes/prefabs/materials are saved.
public static class MsiBodyAtlasAudit
{
    const string Root="Assets/HexLiveContent/People";
    const string Output="Build/MSIVisualAudit/SeamAudit/Views";
    public static Texture2D Original(ActorName actor)
    {
        var source=Directory.GetFiles("Build/MSIVisualAudit/AtlasSource",actor+"_BaseColor_*.jpg").Single();
        var path="Assets/Editor/MSIVisualAuditNormals/"+actor+"_original.jpg";
        File.Copy(source,path,true);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
        var importer=(TextureImporter)AssetImporter.GetAtPath(path);
        importer.maxTextureSize=2048;importer.mipmapEnabled=true;importer.sRGBTexture=true;
        importer.textureCompression=TextureImporterCompression.Compressed;importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
    public static void Capture()
    {
        Directory.CreateDirectory(Output);
        var render=typeof(MsiPeopleVisualAudit).GetMethod("Render",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
        int images=0,materials=0;
        foreach(var name in new[]{ActorName.Marta,ActorName.Kshishtof})
        {
            var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Source/Textures/"+name+"_BaseColor_Padded.png");
            if(texture==null)throw new InvalidOperationException("Missing installed atlas "+name);
            var importer=(TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
            if(!importer.mipmapEnabled || !importer.sRGBTexture)throw new InvalidOperationException("Incorrect colour import");
            foreach(var path in Directory.GetFiles(Root+"/Materials","Skin_"+name+"_*.mat").Concat(new[]{Root+"/Materials/"+name+"_Atlas__Base.mat"}))
            {
                var m=AssetDatabase.LoadAssetAtPath<Material>(path.Replace('\\','/'));
                if(m.GetTexture("_BaseMap")!=texture)throw new InvalidOperationException("Stale skin reference "+path);
                materials++;
            }
            var before=Original(name);
            var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Actors/"+name+".prefab"));
            var graph=default(PlayableGraph);Material material=null;
            try
            {
                foreach(var component in actor.GetComponentsInChildren<MonoBehaviour>(true))
                    if(component!=null && component.GetType().Namespace?.StartsWith("RootMotion.FinalIK")==true)component.enabled=false;
                var bones=actor.GetComponent<BodyBones>();bones.Construct(name,432);
                if(bones.HairInstance!=null)
                    foreach(var renderer in bones.HairInstance.GetComponentsInChildren<Renderer>())renderer.enabled=false;
                bones.SetHair(null);
                bones.Equip("seam_PrimalBriefs",AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Wear/"+name+"_PrimalBriefs.prefab").GetComponent<Wear>(),null);
                if(!ActorBodyResolver.TryResolve(actor,out var skin,out var error))throw new InvalidOperationException(error);
                material=new Material(skin.sharedMaterial);var mats=skin.sharedMaterials;mats[0]=material;skin.sharedMaterials=mats;
                var animator=actor.GetComponent<Animator>();animator.runtimeAnimatorController=null;
                animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.applyRootMotion=false;animator.Rebind();
                var restHand=bones.GetBone("lHand").position;
                foreach(var pose in new[]{"Standing Idle.fbx","X Bot@Running.fbx","Jump.fbx"})
                {
                    graph=PlayableGraph.Create("Atlas seam views");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var clip=AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/"+pose).OfType<AnimationClip>().First(c=>!c.name.StartsWith("__preview"));
                    var playable=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"Pose",animator).SetSourcePlayable(playable);graph.Play();
                    playable.SetTime(clip.length*.4);graph.Evaluate(0);
                    if(Vector3.Distance(restHand,bones.GetBone("lHand").position)<.03f)
                        throw new InvalidOperationException("Clip remained in rest pose: "+name+"/"+pose);
                    foreach(var original in new[]{true,false})
                    {
                        material.SetTexture("_BaseMap",original?before:texture);
                        foreach(float angle in new[]{0f,90f,180f,270f})
                        {
                            var bounds=new Bounds(new Vector3(0,1.1f,0),new Vector3(1.2f,1.8f,.7f));
                            var file=Output+"/"+name+"-"+Path.GetFileNameWithoutExtension(pose)+"-"+(original?"before":"after")+"-"+angle+".png";
                            render.Invoke(null,new object[]{actor,bounds,angle,file});images++;
                        }
                    }
                    graph.Destroy();
                }
            }
            finally{if(graph.IsValid())graph.Destroy();Object.DestroyImmediate(actor);if(material!=null)Object.DestroyImmediate(material);}
        }
        File.WriteAllText(Output+"/result.txt",$"Installed PNG atlases; original GUIDs; material references={materials}; native images={images}; 2 bodies x 3 poses x 4 views x before/after. No mesh/normal/paint-map mutation.");
        Debug.Log("[BodyAtlasAudit] materials="+materials+" images="+images);
    }
}
#endif
