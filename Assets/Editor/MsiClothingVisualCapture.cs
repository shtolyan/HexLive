#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// Flag-guarded native fixture: no import changes, scene saves or external requests.
public static class MsiClothingVisualCapture
{
    const string Output = "Build/MSIRelease/20261012-wardrobe/visual";
    static Camera camera;
    static RenderTexture target;
    static GameObject actor;
    static BodyBones bones;
    static int step, frames;
    public static void Begin()
    {
        SessionState.SetBool("MsiClothingVisualCapture", true);
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
        Resume();
        EditorApplication.EnterPlaymode();
    }
    [InitializeOnLoadMethod] static void Resume()
    {
        if (!SessionState.GetBool("MsiClothingVisualCapture", false)) return;
        EditorApplication.playModeStateChanged -= Entered;
        EditorApplication.playModeStateChanged += Entered;
    }
    static void Entered(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        SessionState.EraseBool("MsiClothingVisualCapture");
        EditorApplication.playModeStateChanged -= Entered;
        try
        {
            Directory.CreateDirectory(Output);
            ShaderUtil.allowAsyncCompilation = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.5f, .5f, .5f);
            var light = new GameObject("Key").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.transform.rotation = Quaternion.Euler(35, 155, 0);
            camera = new GameObject("Capture").AddComponent<Camera>();
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            target = new RenderTexture(960, 960, 24, RenderTextureFormat.ARGB32);
            target.Create(); camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.035f, .045f, .065f);
            camera.orthographic = true;
            step = frames = 0; Outfit();
            EditorApplication.update += Tick;
        }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
    static string Actor => step < 3 ? "Marta" : "Kshishtof";
    static bool Dress => step % 3 == 2;
    static string[] Items => Actor == "Marta"
        ? new[] { "PrimalBriefs", "PrimalTop", Dress ? "PrimalDress" : "PrimalSkirt", "PrimalCollar" }
        : new[] { "PrimalBriefs", Dress ? "PrimalDress" : "PrimalSkirt", "PrimalCollar" };
    static void Outfit()
    {
        if (actor != null) Object.Destroy(actor);
        actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/" + Actor + ".prefab"));
        foreach (var a in actor.GetComponentsInChildren<Animator>()) a.enabled = false;
        bones = actor.GetComponent<BodyBones>();
        bones.Construct((ActorName)Enum.Parse(typeof(ActorName), Actor), 713);
        foreach (var item in Items)
            bones.Equip(item, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Wear/" + Actor + "_" + item + ".prefab").GetComponent<Wear>(), null);
        foreach (var lod in actor.GetComponentsInChildren<LODGroup>()) lod.ForceLOD(0);
        if (!ActorBodyResolver.TryResolve(actor, out var body, out var error)) throw new InvalidOperationException(error);
        var center = body.bounds.center;
        camera.orthographicSize = body.bounds.extents.y * 1.2f;
        camera.transform.position = center + new Vector3(0, 0, 3);
        camera.transform.LookAt(center);
        if (step % 3 == 1)
        {
            var pelvis = bones.GetBone("pelvis");
            var point = pelvis == null ? center : pelvis.position;
            camera.orthographicSize = .38f;
            camera.transform.position = point + new Vector3(0, -.42f, 1.5f);
            camera.transform.LookAt(point);
        }
    }
    static void Tick()
    {
        if (!Application.isPlaying || ++frames < 80) return;
        frames = 0;
        try
        {
            var name = Actor + (Dress ? "-fur-dress" : step % 3 == 1 ? "-skirt-hem" : "-skirt-collar");
            var items = Items;
            foreach (var item in items)
                if (bones.GetWorn(item) == null || bones.GetWorn(item).GetComponentsInChildren<SkinnedMeshRenderer>().Any(r => !r.enabled))
                    throw new InvalidOperationException(name + " hidden/evicted " + item);
            camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            File.WriteAllBytes(Output + "/" + name + ".png", image.EncodeToPNG());
            Object.Destroy(image); RenderTexture.active = previous;
            File.WriteAllText(Output + "/" + name + ".txt", "Native D3D11, actual prepared actor and clothing prefabs. All worn renderers enabled.\n" + string.Join("\n", items));
            if (++step == 6) { EditorApplication.update -= Tick; Debug.Log("[ClothingVisual] captured6 real frames; all layers visible"); EditorApplication.Exit(0); }
            else Outfit();
        }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.update -= Tick; EditorApplication.Exit(1); }
    }
}
#endif
