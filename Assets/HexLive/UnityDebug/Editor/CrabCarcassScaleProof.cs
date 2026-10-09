using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Rendering;
using UnityEditor;
using UnityEngine;

public static class CrabCarcassScaleProof
{
    // Exercise the production factories with the real local mob payload.
    public static void Run()
    {
        if (Application.productName != "HexLive") throw new Exception("Wrong project");
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var config = AssetDatabase.LoadAssetAtPath<MobConfig>("Assets/HexLiveContent/RuntimeSource/Mobs/crab.asset");
        var raw = UnityEngine.Object.Instantiate(config.prefab);
        ObjectFit.WorldBounds(raw, out var whole);
        var first = raw.GetComponentInChildren<Renderer>().bounds;
        Debug.Log($"CRAB_PROOF source first={first.size:F6} whole={whole.size:F6} oldOversize={Mathf.Max(whole.size.x, whole.size.z) / Mathf.Max(first.size.x, first.size.z):F6}");
        UnityEngine.Object.DestroyImmediate(raw);
        MobLibrary.Register(config);
        var handles = (IDictionary)typeof(ContentPrefabCache).GetField("Handles", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        handles["mob/crab"] = Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>), flags, null, new object[] { config.prefab, (Action)(() => {}) }, null);
        var host = new GameObject("Crab scale proof");
        var renderer = host.AddComponent<HexWorldRenderer>();
        var live = (GameObject)typeof(HexWorldRenderer).GetMethod("CreateMobView", flags).Invoke(renderer, new object[] { "crab", 1 });
        var dead = (GameObject)typeof(HexWorldRenderer).GetMethod("CreateObjectView", flags).Invoke(renderer, new object[] {
            new ObjectSnapshot { Id = new ObjectId(360), DefinitionId = "carcass.animal", Variant = "rabbit", SpawnTick = 0 }, new Dictionary<int, Float2>(), 100 });
        ObjectFit.WorldBounds(live, out var liveBounds);
        ObjectFit.WorldBounds(dead, out var deadBounds);
        var liveBody = live.transform.GetChild(0);
        var deadBody = dead.transform.GetChild(0);
        Debug.Log($"CRAB_PROOF live={liveBounds.size:F6} dead={deadBounds.size:F6} liveScale={liveBody.localScale:F6} deadScale={deadBody.localScale:F6} groundError={deadBounds.min.y-dead.transform.position.y:F6}");
        var passed = Vector3.Distance(liveBody.localScale, deadBody.localScale) < 0.0001f &&
                     Mathf.Abs(deadBounds.min.y - dead.transform.position.y) < 0.0001f &&
                     Mathf.Abs(Mathf.Max(deadBounds.size.x, deadBounds.size.z) - 0.24f) < 0.0001f;
        // Render the actual factory results together at identical camera scale.
        live.transform.position = new Vector3(-0.22f, 0, 0);
        dead.transform.position = new Vector3(0.22f, -deadBounds.min.y + dead.transform.position.y, 0);
        var cameraGo = new GameObject("Proof camera");
        var camera = cameraGo.AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 1.2f, -1.4f);
        camera.transform.LookAt(Vector3.zero);
        camera.orthographic = true; camera.orthographicSize = 0.6f;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.gray;
        var lightGo = new GameObject("Proof light");
        lightGo.AddComponent<Light>().type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
        var rt = new RenderTexture(1000, 700, 24);
        camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
        var image = new Texture2D(1000, 700, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 1000, 700), 0, 0); image.Apply();
        Directory.CreateDirectory("Build/CrabProof");
        File.WriteAllBytes("Build/CrabProof/result.png", image.EncodeToPNG());
        Debug.Log("CRAB_PROOF " + (passed ? "PASS" : "FAIL"));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
