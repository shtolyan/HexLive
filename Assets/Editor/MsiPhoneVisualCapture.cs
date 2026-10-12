#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using Object=UnityEngine.Object;

// Native visual fixture only; never saves scenes, edits imports or calls providers.
public static class MsiPhoneVisualCapture
{
 const string Output="Build/MSIRelease/20261012-voice-budget/visual";
 static Camera camera;static RenderTexture target;static NpcVoiceLipSync mouth;
 static BodyBones bones;static SkinnedMeshRenderer face;static int frames,step;
 public static void Begin()
 {
  SessionState.SetBool("MsiPhoneVisualCapture",true);
  UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
  Resume();EditorApplication.EnterPlaymode();
 }
 public static void BeginBalance()
 {
  SessionState.SetBool("MsiPhoneVisualCapture",true);
  SessionState.SetBool("MsiPhoneBalanceCapture",true);
  UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
  Resume();EditorApplication.EnterPlaymode();
 }
 [InitializeOnLoadMethod]static void Resume()
 {
  if(!SessionState.GetBool("MsiPhoneVisualCapture",false))return;
  EditorApplication.playModeStateChanged-=Entered;EditorApplication.playModeStateChanged+=Entered;
 }
 static void Entered(PlayModeStateChange state)
 {
  if(state!=PlayModeStateChange.EnteredPlayMode)return;
  SessionState.EraseBool("MsiPhoneVisualCapture");EditorApplication.playModeStateChanged-=Entered;
  try
  {
   Directory.CreateDirectory(Output);ShaderUtil.allowAsyncCompilation=false;
   if(SessionState.GetBool("MsiPhoneBalanceCapture",false))
   {
    SessionState.EraseBool("MsiPhoneBalanceCapture");
    target=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32);target.Create();
    var ui=new GameObject("BalanceFixture").AddComponent<CharacterPanel>();ui.enabled=false;
    var doc=ui.GetComponent<UIDocument>();doc.panelSettings.targetTexture=target;
    var label=doc.rootVisualElement.Q<Label>("accountBalance");
    label.text="$ 4.8765 *\nРасход $ 0.1235";label.style.display=DisplayStyle.Flex;
    frames=0;EditorApplication.update+=CaptureBalance;return;
   }
   var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/Marta.prefab"));
   foreach(var a in actor.GetComponentsInChildren<Animator>())a.enabled=false;
   bones=actor.GetComponent<BodyBones>();bones.Construct(ActorName.Marta,713);
   foreach(var item in new[]{"PrimalBriefs","PrimalTop"})bones.Equip("phone_"+item,AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Wear/Marta_"+item+".prefab").GetComponent<Wear>(),null);
   foreach(var l in actor.GetComponentsInChildren<LODGroup>())l.ForceLOD(0);
   face=actor.GetComponentsInChildren<SkinnedMeshRenderer>().First(s=>s.sharedMesh!=null&&NpcVoiceLipSync.HasVisemes(s.sharedMesh));
   mouth=actor.AddComponent<NpcVoiceLipSync>();mouth.Construct(face);
   RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.5f,.5f,.5f);
   var light=new GameObject("Key").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.5f;light.transform.rotation=Quaternion.Euler(35,155,0);
   camera=new GameObject("Capture camera").AddComponent<Camera>();camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
   target=new RenderTexture(960,720,24,RenderTextureFormat.ARGB32);target.Create();camera.targetTexture=target;
   camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.065f);camera.orthographic=true;
   var head=bones.GetBone("head");var point=head!=null?head.position:face.bounds.center;
   camera.orthographicSize=.22f;camera.transform.position=point+new Vector3(0,.01f,1.5f);camera.transform.LookAt(point);
   frames=step=0;EditorApplication.update+=Tick;
  }catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
 }
 static void CaptureBalance()
 {
  if(!Application.isPlaying||++frames<100)return;
  EditorApplication.update-=CaptureBalance;
  try
  {
   var old=RenderTexture.active;RenderTexture.active=target;
   var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
   image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();
   File.WriteAllBytes(Output+"/balance-fixture.png",image.EncodeToPNG());RenderTexture.active=old;
   Debug.Log("[PhoneVisual] captured real CharacterPanel balance fixture (test value, no account)");EditorApplication.Exit(0);
  }catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
 }
 static void Tick()
 {
  if(!Application.isPlaying||++frames<50)return;frames=0;
  try
  {
   var names=new[]{"mouth-closed","mouth-speaking-aa","mouth-interrupted","red-drips"};
   var previous=RenderTexture.active;RenderTexture.active=target;
   var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();
   File.WriteAllBytes(Output+"/"+names[step]+".png",image.EncodeToPNG());Object.Destroy(image);RenderTexture.active=previous;
   var weights=Enumerable.Range(0,face.sharedMesh.blendShapeCount).Where(i=>face.GetBlendShapeWeight(i)>1).Select(i=>face.sharedMesh.GetBlendShapeName(i)+":"+face.GetBlendShapeWeight(i).ToString("F2",System.Globalization.CultureInfo.InvariantCulture)).ToArray();
   File.WriteAllText(Output+"/"+names[step]+".txt",string.Join("\n",weights));
   if(step==0){mouth.AppendStream(new[]{"а"},new[]{0f},new[]{60000f});mouth.SetStreamSpeaking(true);}
   if(step==1)mouth.ResetStream();
   if(step==2)
   {
    camera.orthographicSize=.65f;camera.transform.position=new Vector3(.8f,.9f,2f);camera.transform.LookAt(new Vector3(.6f,.6f,0));
    var prefab=BloodSplashVfx.Pick(0);var vfx=Object.Instantiate(prefab,new Vector3(.8f,1f,0),Quaternion.LookRotation(Vector3.down));
    BloodPalette.TintParticles(vfx);
    foreach(var ps in vfx.GetComponentsInChildren<ParticleSystem>()){ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);var m=ps.main;m.loop=true;m.startSpeed=.1f;m.gravityModifier=1;var e=ps.emission;e.rateOverTime=30;ps.Play();}
   }
   if(++step>=4){EditorApplication.update-=Tick;Debug.Log("[PhoneVisual] captured4 real D3D11 frames");EditorApplication.Exit(0);}
  }catch(Exception ex){Debug.LogException(ex);EditorApplication.update-=Tick;EditorApplication.Exit(1);}
 }
}
#endif
