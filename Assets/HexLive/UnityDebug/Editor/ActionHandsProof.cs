using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
// Offline visual regression fixture using authored payloads and the runtime
// hand factory. No content publication or server mutation is involved.
public static class ActionHandsProof
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, Private).SetValue(target, value);

    [InitializeOnLoadMethod]
    private static void ResumeAfterReload()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("ActionHandsProof.Run", false))
            {
                SessionState.SetBool("ActionHandsProof.Run", false);
                EditorApplication.delayCall += Run;
            }
        };
    }

    public static void Run()
    {
        if (!Application.isPlaying)
        {
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            SessionState.SetBool("ActionHandsProof.Run", true);
            EditorApplication.EnterPlaymode();
            return;
        }
        try
        {
            Directory.CreateDirectory("Build/ActionHandsProof");
            Seed("tool.bottle", "Assets/HexLiveContent/RuntimeSource/Objects/tool_bottle_native.prefab");
            Seed("food.coconut_pierced", "Assets/HexLiveContent/RuntimeSource/Objects/food.coconut_pierced.fbx");
            var config = AssetDatabase.LoadAssetAtPath<GearConfig>("Assets/HexLiveContent/RuntimeSource/Gear/bottle.asset");
            GearLibrary.Register(config);
            var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/RuntimeSource/Actors/Jana.prefab"));
            var bones = actor.GetComponentInChildren<BodyBones>();
            var map = (Dictionary<string, Transform>)typeof(BodyBones).GetField("_bonesMap", Private).GetValue(bones);
            foreach (var bone in actor.GetComponentsInChildren<Transform>(true)) map[bone.name] = bone;
            var view = actor.GetComponent<NpcActorView>() ?? actor.AddComponent<NpcActorView>();
            Set(view, "_bodyBones", bones);
            var animator = actor.GetComponentInChildren<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;
            animator.Rebind();
            Set(view, "_animator", animator);
            foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.updateWhenOffscreen = true;
            view.SetInteraction("HydrateOther", "tool.bottle");
            Check(view, "_handProp", "tool.bottle", "rHand");
            view.SetOffhandItem("food.coconut_pierced");
            Check(view, "_offhandProp", "food.coconut_pierced", "lHand");
            Capture(actor, "two-vessels.png");
            view.SetOffhandItem(null);
            view.SetHandedness(true);
            view.SetInteraction("HydrateOther", "tool.bottle");
            Check(view, "_handProp", "tool.bottle", "lHand");
            // Repeat after the same item is already loaded: changing bones must reseat it.
            view.SetHandedness(false);
            Check(view, "_handProp", "tool.bottle", "rHand");
            view.SetInteraction("FillVessel", "tool.bottle", interactionSeconds: 6);
            view.SetOffhandItem("food.coconut_pierced");
            if (!animator.GetBool("Crafting")) throw new Exception("Pour did not request the work animation");
            animator.Play("CraftWork", 0, 0.4f);
            animator.Update(0f);
            animator.speed = 0;
            view.enabled = false;
            var startFrame = Time.frameCount;
            EditorApplication.CallbackFunction finish = null;
            finish = () =>
            {
                if (Time.frameCount < startFrame + 2) return;
                EditorApplication.update -= finish;
                try
                {
                    Capture(actor, "pour-work-pose.png");
                    Finish(view);
                }
                catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
            };
            EditorApplication.update += finish;
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }

    private static void Finish(NpcActorView view)
    {
            Set(view, "_hasTwoUsableHands", false);
            view.SetOffhandItem("food.coconut_pierced");
            if (typeof(NpcActorView).GetField("_offhandProp", Private).GetValue(view) != null)
                throw new Exception("Unavailable second hand retained its prop");
            File.WriteAllText("Build/ActionHandsProof/result.txt",
                "PASS: bottle aid; opposite-hand coconut; left/right reseat; pouring pose; unavailable second hand.\n");
            Debug.Log("ActionHandsProof PASS");
            EditorApplication.Exit(0);
    }

    private static void Seed(string id, string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new Exception("Missing fixture " + path);
        var type = typeof(ContentAssetHandle<GameObject>);
        var handle = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { prefab, (Action)(() => { }) }, null);
        var handles = (IDictionary)typeof(ContentPrefabCache).GetField("Handles",
            BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        handles["object/" + id] = handle;
    }

    private static void Check(NpcActorView view, string field, string id, string hand)
    {
        var prop = (GameObject)typeof(NpcActorView).GetField(field, Private).GetValue(view);
        if (prop == null || prop.name != "HandProp " + id || prop.transform.parent.name != hand)
            throw new Exception(field + " must show " + id + " in " + hand);
    }

    private static void Capture(GameObject actor, string name)
    {
        var props = actor.GetComponentsInChildren<Transform>().Where(t => t.name.StartsWith("HandProp ")).ToArray();
        var bounds = new Bounds(props[0].position, Vector3.one * 0.15f);
        foreach (var prop in props)
            foreach (var renderer in prop.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(renderer.bounds);
        var cameraObject = new GameObject("Proof camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.backgroundColor = new Color(0.15f, 0.17f, 0.19f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.transform.position = bounds.center + new Vector3(0.7f, 0.35f, 1.5f) * Mathf.Max(0.6f, bounds.size.magnitude);
        camera.transform.LookAt(bounds.center);
        camera.nearClipPlane = 0.01f;
        var lightObject = new GameObject("Proof light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 2;
        light.transform.rotation = Quaternion.Euler(35, -30, 0);
        RenderSettings.ambientLight = Color.gray;
        var target = new RenderTexture(1000, 800, 24);
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        var image = new Texture2D(1000, 800, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 1000, 800), 0, 0);
        image.Apply();
        File.WriteAllBytes("Build/ActionHandsProof/" + name, image.EncodeToPNG());
        RenderTexture.active = null;
        camera.targetTexture = null;
        Object.DestroyImmediate(image);
        Object.DestroyImmediate(target);
        Object.DestroyImmediate(cameraObject);
        Object.DestroyImmediate(lightObject);
    }
}
}
