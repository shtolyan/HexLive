#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
/// <summary>Offline acceptance on the real imported People bodies, controller and
/// NpcAnimSet. No registry source, HTTP, RemoteBackend, voice or content downloads.</summary>
public sealed class ColonyPresentationAcceptanceRuntimeTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    private static object Call(object target,string method,params object[] args)=>target.GetType().GetMethod(method,Private).Invoke(target,args);
    private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Private).SetValue(target,value);

    [TestCase("Marta")] [TestCase("Kshishtof")]
    public void CryingUsesSecondRealClipInSleepAndFallenIdleIncludingLateInitialization(string body)
    {
        using var f=new ActorFixture(body);
        f.View.SetCrying(true);
        AssertOverrides(f,f.AnimSet.sleep[1]);
        Call(f.View,"ApplySleepPose");
        AssertOverrides(f,f.AnimSet.sleep[1]);
        Quaternion[] previous=null;
        foreach(var state in new[]{"Sleep","FallenIdle"})
        {
            f.Animator.SetBool("Laying",state=="Sleep"); f.Animator.SetBool("Fallen",state=="FallenIdle");
            f.Animator.Play(Animator.StringToHash(state),0,.55f); f.Animator.Update(0f);
            Assert.That(f.Animator.GetCurrentAnimatorStateInfo(0).shortNameHash,Is.EqualTo(Animator.StringToHash(state)));
            Assert.That(f.Animator.GetCurrentAnimatorClipInfo(0).Any(c=>c.clip==f.AnimSet.sleep[1] && c.weight>.9f),Is.True,state);
            var pose=f.Pose();
            if(previous!=null) for(var i=0;i<pose.Length;i++)
                Assert.That(Quaternion.Angle(previous[i],pose[i]),Is.LessThan(.25f),"Both lying states must evaluate the same curled pose: bone "+i);
            previous=pose;
        }
        f.View.SetCrying(false);
        AssertOverrides(f,f.AnimSet.sleep[0]);
    }
    private static void AssertOverrides(ActorFixture f,AnimationClip expected)
    {
        foreach(var name in new[]{"Sleep","X Bot@Sleeping Idle"})
            Assert.That(f.Controller[f.Originals[name]],Is.SameAs(expected),name);
    }

    [TestCase("Marta")] [TestCase("Kshishtof")]
    public void CompanionStarTracksActualEyesAndRendersBlueThenDisappearsOnRelease(string body)
    {
        using var f=new ActorFixture(body);
        Assert.That(SystemInfo.graphicsDeviceType,Is.Not.EqualTo(GraphicsDeviceType.Null),"Use root's graphics PlayMode slot.");
        Call(f.View,"CalibrateFaceAnchor");
        Assert.That(f.View.TryGetFace(out var face,out var faceForward,out _,out var scale),Is.True,"Actual eye bones, not a fake anchor.");
        Assert.That(f.Bones.GetBone("lEye"),Is.Not.Null); Assert.That(f.Bones.GetBone("rEye"),Is.Not.Null);
        var cameraObject=new GameObject("offline companion evidence camera"); var lightObject=new GameObject("offline companion evidence light");
        var tagged=Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Where(c=>c.CompareTag("MainCamera")).ToArray();
        var target=new RenderTexture(640,640,24); var image=new Texture2D(640,640,TextureFormat.RGBA32,false);
        var previous=RenderTexture.active;
        try
        {
            foreach(var existing in tagged) existing.tag="Untagged";
            var camera=cameraObject.AddComponent<Camera>(); camera.tag="MainCamera";
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.08f,.10f,.12f);
            camera.orthographic=true; camera.nearClipPlane=.01f; camera.farClipPlane=20f; camera.cullingMask=1<<31;
            var bounds=f.BodyBounds();
            camera.orthographicSize=bounds.size.y*.78f;
            var look=bounds.center+Vector3.up*(bounds.size.y*.1f);
            var outward=Vector3.ProjectOnPlane(faceForward,Vector3.up).normalized;
            camera.transform.position=look+outward*5f; camera.transform.LookAt(look);
            var light=lightObject.AddComponent<Light>(); light.type=LightType.Directional; light.intensity=1.4f;
            light.transform.rotation=Quaternion.Euler(35f,-25f,0f);
            TravelPartyStar.Sync(f.View,true);
            var star=f.View.GetComponent<TravelPartyStar>(); Call(star,"LateUpdate");
            var visual=(MeshRenderer)typeof(TravelPartyStar).GetField("_view",Private).GetValue(star);
            Assert.That(visual.enabled,Is.True);
            Assert.That(Vector3.Distance(visual.transform.position,face+Vector3.up*(.38f*scale)),Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(visual.transform.rotation,camera.transform.rotation),Is.LessThan(.001f));
            Assert.That(visual.transform.position.y,Is.GreaterThan(face.y));
            Assert.That(visual.GetComponent<MeshFilter>().sharedMesh.triangles.Length,Is.EqualTo(30));
            target.Create();
            Color32[] Read()
            {
                Assert.That(GraphicsSettings.currentRenderPipeline,Is.Not.Null);
                RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest{destination=target});
                RenderTexture.active=target; image.ReadPixels(new Rect(0,0,640,640),0,0); image.Apply(); return image.GetPixels32();
            }
            var on=Read();
            var directory=Path.GetFullPath("Build/Diagnostics/bug-wave-20261110/colony-acceptance"); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory,"travel-star-"+body+".png"),image.EncodeToPNG());
            TravelPartyStar.Sync(f.View,false); Assert.That(visual.enabled,Is.False);
            var off=Read(); var blue=0; var silhouette=0;
            for(var i=0;i<on.Length;i++)
            {
                var delta=Math.Abs(on[i].r-off[i].r)+Math.Abs(on[i].g-off[i].g)+Math.Abs(on[i].b-off[i].b);
                if(delta>30 && on[i].b>on[i].r*1.3f && on[i].b>on[i].g*1.05f) blue++;
                if(Math.Abs(off[i].r-off[0].r)+Math.Abs(off[i].g-off[0].g)+Math.Abs(off[i].b-off[0].b)>30) silhouette++;
            }
            TestContext.WriteLine(body+": body pixels="+silhouette+", added blue star pixels="+blue+", evidence="+directory);
            Assert.That(silhouette,Is.GreaterThan(300),"The actual body must render, not just an isolated star mesh.");
            Assert.That(blue,Is.GreaterThan(15),"Blue GPU pixels must disappear when the companion indicator is released.");
            TravelPartyStar.Sync(f.View,true); Call(star,"LateUpdate"); Assert.That(visual.enabled,Is.True);
        }
        finally
        {
            RenderTexture.active=previous;
            Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(lightObject);
            foreach(var existing in tagged) if(existing!=null) existing.tag="MainCamera";
            target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(image);
        }
    }

    private sealed class ActorFixture:IDisposable
    {
        public readonly GameObject Root;
        public readonly NpcActorView View;
        public readonly BodyBones Bones;
        public readonly Animator Animator;
        public readonly AnimatorOverrideController Controller;
        public readonly NpcAnimSet AnimSet;
        public readonly Dictionary<string,AnimationClip> Originals;
        private readonly object oldSet=typeof(NpcActorView).GetField("_animSet",Static).GetValue(null);
        private readonly bool asyncShaders=ShaderUtil.allowAsyncCompilation;
        public ActorFixture(string body)
        {
            try
            {
            ShaderUtil.allowAsyncCompilation=false;
            AnimSet=AssetDatabase.LoadAssetAtPath<NpcAnimSet>("Assets/HexLiveContent/RuntimeSource/NpcAnimSet.asset");
            Assert.That(AnimSet,Is.Not.Null); Assert.That(AnimSet.sleep.Length,Is.GreaterThanOrEqualTo(2));
            Assert.That(AnimSet.sleep[0],Is.Not.Null); Assert.That(AnimSet.sleep[1],Is.Not.Null);
            Assert.That(AnimSet.sleep[0],Is.Not.SameAs(AnimSet.sleep[1]));
            var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/"+body+".prefab");
            Assert.That(source,Is.Not.Null);
            Root=Object.Instantiate(source); Root.name="offline actual companion "+body;
            Root.transform.position=new Vector3(1000f,0,1000f); Root.transform.localScale=Vector3.one*HexWorldRenderer.ActorScale;
            foreach(var transform in Root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer=31;
            Animator=Root.GetComponent<Animator>(); Assert.That(Animator,Is.Not.Null); Animator.enabled=false; Animator.applyRootMotion=false;
            Bones=Root.GetComponent<BodyBones>(); Bones.Construct((ActorName)Enum.Parse(typeof(ActorName),body),169);
            var authored=Animator.runtimeAnimatorController;
            if(authored is AnimatorOverrideController wrapper) authored=wrapper.runtimeAnimatorController;
            Assert.That(authored,Is.Not.Null);
            View=Root.AddComponent<NpcActorView>(); View.enabled=false;
            Set(View,"_bodyBones",Bones); Set(View,"_bodyRoot",Root.transform); Set(View,"_animator",Animator);
            // Guaranteed ordinary pose index 0, so ending crying must change it.
            Set(View,"_npcId",AnimSet.sleep.Length*4321);
            Originals=(Dictionary<string,AnimationClip>)typeof(NpcActorView).GetField("_clipsByName",Private).GetValue(View);
            foreach(var clip in authored.animationClips) if(clip!=null) Originals[clip.name]=clip;
            Controller=new AnimatorOverrideController(authored); Animator.runtimeAnimatorController=Controller;
            Set(View,"_animOverride",Controller); typeof(NpcActorView).GetField("_animSet",Static).SetValue(null,AnimSet);
            Animator.Rebind(); Animator.Update(0f);
            }
            catch {Dispose(); throw;}
        }
        public Quaternion[] Pose()=>new[]{HumanBodyBones.Hips,HumanBodyBones.Spine,HumanBodyBones.LeftUpperArm,HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftUpperLeg,HumanBodyBones.RightUpperLeg,HumanBodyBones.LeftLowerLeg,HumanBodyBones.RightLowerLeg}
            .Select(b=>Animator.GetBoneTransform(b).localRotation).ToArray();
        public Bounds BodyBounds()
        {
            var renderers=Root.GetComponentsInChildren<SkinnedMeshRenderer>(); Assert.That(renderers,Is.Not.Empty);
            var bounds=renderers[0].bounds; foreach(var renderer in renderers.Skip(1))bounds.Encapsulate(renderer.bounds); return bounds;
        }
        public void Dispose()
        {
            Object.DestroyImmediate(Root); Object.DestroyImmediate(Controller);
            typeof(NpcActorView).GetField("_animSet",Static).SetValue(null,oldSet); ShaderUtil.allowAsyncCompilation=asyncShaders;
        }
    }
}
}
#endif
