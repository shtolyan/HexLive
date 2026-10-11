#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// Disposable native-Unity A/B fixture. Never saves a scene or changes a prefab.
public static class MsiSecondaryMotionDemo
{
    const string Root="Assets/HexLiveContent/People";
    const string Output="Build/MSIVisualAudit/SecondaryMotion";
    const int Count=420, Fps=30;
    sealed class Actor
    {
        public GameObject root;
        public PlayableGraph graph;
        public AnimationMixerPlayable mixer;
        public AnimationClipPlayable[] clips;
        public SecondaryBoneMotion hair, breast;
        public Transform foot;
        public Vector3 expectedFoot;
    }
    static Actor[] actors;
    static Camera camera;
    static RenderTexture target;
    static Texture2D pixels;
    static StreamWriter measurements;
    static int frame, warmup;
    static bool pending;
    static float maxHair, maxBreast, maxError;
    static float minRunFoot=100, maxRunFoot=-100, maxPoseOverwrite;
    public static void Begin()
    {
        SessionState.SetBool("MsiSecondaryMotionDemo",true);
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
        Resume(); EditorApplication.EnterPlaymode();
    }
    [InitializeOnLoadMethod] static void Resume()
    {
        if(!SessionState.GetBool("MsiSecondaryMotionDemo",false))return;
        EditorApplication.playModeStateChanged-=Enter;
        EditorApplication.playModeStateChanged+=Enter;
    }
    static void Enter(PlayModeStateChange state)
    {
        if(state!=PlayModeStateChange.EnteredPlayMode)return;
        SessionState.EraseBool("MsiSecondaryMotionDemo");
        EditorApplication.playModeStateChanged-=Enter;
        try
        {
            ShaderUtil.allowAsyncCompilation=false;
            Directory.CreateDirectory(Output+"/frames");
            actors=new[]{CreateActor(false),CreateActor(true)};
            RenderSettings.ambientMode=AmbientMode.Flat;
            RenderSettings.ambientLight=new Color(.5f,.5f,.5f);
            var key=new GameObject("Motion key").AddComponent<Light>();
            key.type=LightType.Directional;key.intensity=1.4f;key.transform.rotation=Quaternion.Euler(35,155,0);
            var fill=new GameObject("Motion fill").AddComponent<Light>();
            fill.type=LightType.Directional;fill.intensity=.7f;fill.transform.rotation=Quaternion.Euler(20,-35,0);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name="Motion floor";floor.layer=31;floor.transform.localScale=Vector3.one*2;
            var floorMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            floorMaterial.color=new Color(.16f,.19f,.22f);floor.GetComponent<Renderer>().material=floorMaterial;
            camera=new GameObject("Motion camera").AddComponent<Camera>();
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camera.cullingMask=1<<31;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(.10f,.13f,.16f);camera.fieldOfView=33;
            camera.nearClipPlane=.03f;camera.farClipPlane=30;
            target=new RenderTexture(1280,900,24){antiAliasing=4};target.Create();camera.targetTexture=target;
            pixels=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            measurements=new StreamWriter(Output+"/measurements.csv");
            measurements.WriteLine("frame,time,stage,hair_max_degrees,breast_max_degrees,length_error_metres");
            frame=0;warmup=0;pending=false;
            RenderPipelineManager.endCameraRendering+=Captured;
            EditorApplication.update+=Tick;
            Debug.Log("[SecondaryMotion] Authored Leony chain + prepared Marta; left OFF/right ON; manual run/jump clips; no Magica.");
        }
        catch(Exception e){Fail(e);}
    }
    static Actor CreateActor(bool physics)
    {
        var a=new Actor();
        a.root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Actors/Marta.prefab"));
        // FinalIK's fixTransforms runs in Update and would restore its cached
        // startup pose AFTER this fixture's manual graph evaluation. Production
        // animation drives IK in its normal player loop; this clip-only fixture
        // disables IK only on its disposable actors.
        foreach(var component in a.root.GetComponentsInChildren<MonoBehaviour>(true))
            if(component!=null && component.GetType().Namespace?.StartsWith("RootMotion.FinalIK")==true)
                component.enabled=false;
        var bones=a.root.GetComponent<BodyBones>();bones.Construct(ActorName.Marta,physics?912:911);
        a.foot=bones.GetBone("lFoot");
        foreach(var item in new[]{"PrimalBriefs","PrimalTop"})
            bones.Equip("motion_"+item,AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Wear/Marta_"+item+".prefab").GetComponent<Wear>(),null);
        // Strip cloth only from a temporary inactive copy, never the source prefab.
        var holder=new GameObject("Inactive hair template");holder.SetActive(false);
        var template=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ImportedActors/Hair/LeonyPonytail/LeonyPonytail.prefab"),holder.transform);
        foreach(var component in template.GetComponentsInChildren<MonoBehaviour>(true))
            if(component!=null && (component is GarmentCloth || component.GetType().Namespace?.StartsWith("MagicaCloth")==true))
                Object.DestroyImmediate(component);
        // Reuse the current source-UV cutout materials on the original UVs/rig.
        // Only this disposable instance changes; authored prefabs stay untouched.
        var materials=Directory.GetFiles(Root+"/Materials","LeonyPonytail_SourceUV_*__Base.mat")
            .Select(path=>AssetDatabase.LoadAssetAtPath<Material>(path.Replace('\\','/')))
            .ToDictionary(m=>m.name,m=>m);
        foreach(var renderer in template.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            renderer.sharedMaterials=renderer.sharedMaterials.Select(m=>materials.TryGetValue(m.name,out var prepared)?prepared:m).ToArray();
        bones.SetHair(template.GetComponent<Wear>());Object.DestroyImmediate(holder);
        foreach(var component in a.root.GetComponentsInChildren<MonoBehaviour>(true))
            if(component!=null && (component is GarmentCloth || component.GetType().Namespace?.StartsWith("MagicaCloth")==true))
                Object.DestroyImmediate(component);
        foreach(var t in a.root.GetComponentsInChildren<Transform>(true))t.gameObject.layer=31;
        foreach(var lod in a.root.GetComponentsInChildren<LODGroup>())lod.ForceLOD(0);
        var animator=a.root.GetComponent<Animator>();
        animator.runtimeAnimatorController=null;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        animator.applyRootMotion=false;animator.Rebind();
        a.graph=PlayableGraph.Create("Secondary motion A/B");a.graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        a.mixer=AnimationMixerPlayable.Create(a.graph,3);
        var paths=new[]{"X Bot@Running.fbx","Jump.fbx","Standing Idle.fbx"};
        a.clips=new AnimationClipPlayable[3];
        for(int i=0;i<3;i++)
        {
            var clip=AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/"+paths[i]).OfType<AnimationClip>().First(c=>!c.name.StartsWith("__preview"));
            a.clips[i]=AnimationClipPlayable.Create(a.graph,clip);
            a.graph.Connect(a.clips[i],0,a.mixer,i);
            Debug.Log("[SecondaryMotion clip] "+clip.name+" length="+clip.length+" human="+clip.humanMotion);
        }
        AnimationPlayableOutput.Create(a.graph,"Animation",animator).SetSourcePlayable(a.mixer);
        a.graph.Play();a.mixer.SetInputWeight(0,1);a.graph.Evaluate(0);
        if(physics)
        {
            var strandNames=new[]{"PonytailBase","PonytailUpper","PonytailMid","PonytailBottom"};
            var all=a.root.GetComponentsInChildren<Transform>(true);
            var chain=strandNames.Select(n=>all.Single(t=>t.name==n)).ToArray();
            var tips=new Vector3[chain.Length];
            for(int i=0;i<chain.Length;i++)
                tips[i]=chain[i].InverseTransformPoint(i+1<chain.Length?chain[i+1].position:chain[i].position+(chain[i].position-chain[i-1].position)*.7f);
            a.hair=NewSpring(a.root,"Hair spring");
            // Leony has short joints with long weighted cards: the generic
            // 14-degree cap curls its silhouette too far during a running gait.
            var hairSettings=SecondaryBoneMotion.Settings.Hair;
            hairSettings.maxAngle=7;hairSettings.stiffness=360;
            hairSettings.damping=30;hairSettings.motionInheritance=.9f;
            a.hair.Configure(a.root.transform,chain,tips,hairSettings,
                new[]{new SecondaryBoneMotion.Capsule{a=bones.GetBone("chestLower"),b=bones.GetBone("neckLower"),radius=.105f}});
            var breasts=new[]{bones.GetBone("lPectoral"),bones.GetBone("rPectoral")};
            a.breast=NewSpring(a.root,"Breast spring");
            a.breast.Configure(a.root.transform,breasts,breasts.Select(t=>t.InverseTransformVector(a.root.transform.forward*.09f)).ToArray(),SecondaryBoneMotion.Settings.Breast);
            Debug.Log("[SecondaryMotion rig] hair joints="+a.hair.JointCount+" breast joints="+a.breast.JointCount+" chain lengths="+string.Join(",",tips.Select((p,i)=>chain[i].TransformVector(p).magnitude)));
        }
        return a;
    }
    static SecondaryBoneMotion NewSpring(GameObject actor,string name)
    {
        var host=new GameObject(name);host.transform.SetParent(actor.transform,false);
        var result=host.AddComponent<SecondaryBoneMotion>();result.enabled=false;return result;
    }
    static void Tick()
    {
        if(pending)return;
        try
        {
            if(++warmup<30)return;
            float t=frame/(float)Fps;
            bool jump=t>=4&&t<6;
            float runWeight=t<4?1:t<6?0:t<11?1:Mathf.Clamp01((11.3f-t)/.3f);
            float jumpWeight=jump?Mathf.Clamp01(Mathf.Min((t-4)/.15f,(6-t)/.15f)):0;
            if(jump)runWeight=1-jumpWeight;
            for(int i=0;i<actors.Length;i++)
            {
                var a=actors[i];a.hair?.RestorePose();a.breast?.RestorePose();
                a.root.transform.position=new Vector3(i==0?-.63f:.63f,jump?.32f*Mathf.Sin((t-4)/2*Mathf.PI):0,.07f*Mathf.Sin(Mathf.Min(t,11)*2));
                a.root.transform.rotation=Quaternion.Euler(0,t<7?18:Mathf.Lerp(18,195,Mathf.SmoothStep(0,1,(t-7)/2)),0);
                a.mixer.SetInputWeight(0,runWeight);a.mixer.SetInputWeight(1,jumpWeight);a.mixer.SetInputWeight(2,1-runWeight-jumpWeight);
                a.clips[0].SetTime(t%a.clips[0].GetAnimationClip().length);
                a.clips[1].SetTime(Mathf.Clamp01((t-4)/2)*a.clips[1].GetAnimationClip().length);
                a.clips[2].SetTime(Mathf.Min(t,12)%a.clips[2].GetAnimationClip().length);
                a.graph.Evaluate(0);a.hair?.Step(1f/Fps);a.breast?.Step(1f/Fps);
                a.expectedFoot=a.foot.position;
                if(i==0&&t<4)
                {
                    float height=a.root.transform.InverseTransformPoint(a.foot.position).y;
                    minRunFoot=Mathf.Min(minRunFoot,height);maxRunFoot=Mathf.Max(maxRunFoot,height);
                }
            }
            // Follow the jump vertically so the real airborne pose stays in frame.
            var center=new Vector3(0,1.05f+(jump?.45f*Mathf.Sin((t-4)/2*Mathf.PI):0),0);
            camera.transform.position=center+new Vector3(.1f,.12f,4.3f);camera.transform.LookAt(center);
            var on=actors[1];maxHair=Mathf.Max(maxHair,on.hair.LastMaxAngle);maxBreast=Mathf.Max(maxBreast,on.breast.LastMaxAngle);
            maxError=Mathf.Max(maxError,Mathf.Max(on.hair.LastMaxLengthError,on.breast.LastMaxLengthError));
            measurements.WriteLine(string.Format(CultureInfo.InvariantCulture,"{0},{1:F4},{2},{3:F4},{4:F4},{5:G9}",frame,t,jump?"jump":t<11?"run":"settle",on.hair.LastMaxAngle,on.breast.LastMaxAngle,on.hair.LastMaxLengthError));
            pending=true;
        }
        catch(Exception e){Fail(e);}
    }
    static void Captured(ScriptableRenderContext context,Camera rendered)
    {
        if(rendered!=camera||!pending)return;
        try
        {
            foreach(var a in actors)
            {
                float error=Vector3.Distance(a.foot.position,a.expectedFoot);
                maxPoseOverwrite=Mathf.Max(maxPoseOverwrite,error);
                if(error>.001f)throw new InvalidOperationException("Animation pose overwritten before rendering: foot error="+error);
            }
            var previous=RenderTexture.active;RenderTexture.active=target;
            pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();RenderTexture.active=previous;
            File.WriteAllBytes(Output+"/frames/frame-"+frame.ToString("D4")+".png",pixels.EncodeToPNG());
            pending=false;
            if(++frame%60==0)Debug.Log("[SecondaryMotion] captured "+frame+"/"+Count);
            if(frame<Count)return;
            measurements.Dispose();measurements=null;
            var summary=string.Format(CultureInfo.InvariantCulture,"Frames={0}; FPS={1}; hair max={2:F3} deg; breast max={3:F3} deg; length error={4:G9} m; run foot height range={5:F4} m; post-animation pose overwrite={6:G9} m. Native Unity authored Leony/Marta prototype; NOT live game or prepared low-poly hair. Left off, right on.",Count,Fps,maxHair,maxBreast,maxError,maxRunFoot-minRunFoot,maxPoseOverwrite);
            File.WriteAllText(Output+"/result.txt",summary);Debug.Log("[SecondaryMotion] "+summary);
            Finish(maxHair>.1f&&maxBreast>.01f&&maxError<.001f&&maxRunFoot-minRunFoot>.1f?0:2);
        }
        catch(Exception e){Fail(e);}
    }
    static void Fail(Exception e){Debug.LogException(e);Finish(1);}
    static void Finish(int code)
    {
        EditorApplication.update-=Tick;RenderPipelineManager.endCameraRendering-=Captured;
        measurements?.Dispose();
        if(actors!=null)foreach(var a in actors)if(a?.graph.IsValid()==true)a.graph.Destroy();
        EditorApplication.Exit(code);
    }
}
#endif
