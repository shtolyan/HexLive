#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// Local visual acceptance for §175: imported art, actual Loc and portable fire.
public static class SurvivalItemVisualAudit
{
    public static void Run()
    {
        var args = System.Environment.GetCommandLineArgs();
        var option = Array.IndexOf(args, "-survival-audit-output");
        var output = option >= 0 ? args[option + 1] : "Build/Diagnostics/survival-item-rework/visual";
        Directory.CreateDirectory(output);
        var rows = new JArray();
        var originalLanguage = Loc.Current;
        try
        {
            foreach (var id in new[] { ContentIds.Flint, ContentIds.CrudeSaw, ContentIds.Firestarter, ContentIds.Torch })
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
                var icon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/" + id + ".png");
                var row = new JObject { ["id"] = id, ["model"] = model != null && ObjectFit.HasRenderableGeometry(model),
                    ["sprite"] = icon != null, ["spriteSize"] = icon == null ? null : new JArray(icon.rect.width, icon.rect.height) };
                foreach (var language in new[] { Language.English, Language.Russian })
                {
                    Loc.Current = language;
                    var key = "item." + ItemInfo.Slug(id);
                    row[language.ToString()] = new JObject { ["name"] = Loc.Get(key + ".name"), ["description"] = Loc.Get(key + ".desc"),
                        ["localized"] = Loc.Has(key + ".name") && Loc.Has(key + ".desc") };
                }
                rows.Add(row);
            }
            var scene = EditorSceneManager.NewPreviewScene();
            var ambient = RenderSettings.ambientLight; var mode = RenderSettings.ambientMode;
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", new Color(.24f, .28f, .23f));
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(.045f, .055f, .075f);
                var plane = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(plane, scene);
                plane.transform.position = new Vector3(0, -.05f, 0);
                plane.transform.localScale = new Vector3(18, .1f, 18);
                plane.GetComponent<Renderer>().sharedMaterial = material;
                var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/tool.torch.fbx");
                var torch = Object.Instantiate(model);
                SceneManager.MoveGameObjectToScene(torch, scene);
                torch.transform.localScale *= ObjectFit.FitScaleFactor(torch, ContentIds.Torch);
                PortableTorchVisual.Attach(torch);
                foreach (var ps in torch.GetComponentsInChildren<ParticleSystem>())
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.useAutoRandomSeed = false; ps.randomSeed = 123;
                    ps.Simulate(.7f, true, true, false); ps.Pause();
                }
                var cameraObject = new GameObject("Survival audit camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f, .035f, .055f);
                camera.nearClipPlane = .01f; camera.farClipPlane = 40; camera.fieldOfView = 40;
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                Capture(camera, new Vector3(2.4f, 2.4f, -4), new Vector3(0, .5f, 0), output + "/torch-ground.png");
                Capture(camera, new Vector3(.55f, 1.2f, -1.8f), new Vector3(0, .65f, 0), output + "/torch-close.png");
                var light = torch.GetComponentInChildren<Light>();
                rows.Add(new JObject { ["fire"] = new JObject { ["lightRange"] = light.range, ["intensity"] = light.intensity,
                    ["lightPosition"] = Vector(light.transform.position), ["firePosition"] = Vector(torch.GetComponentInChildren<CampfireEffect>().transform.position),
                    ["worldScale"] = Vector(torch.GetComponentInChildren<CampfireEffect>().transform.lossyScale) } });
                Object.DestroyImmediate(torch);
                Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(plane);
            }
            finally
            {
                Object.DestroyImmediate(material);
                RenderSettings.ambientLight = ambient; RenderSettings.ambientMode = mode;
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
        finally { Loc.Current = originalLanguage; }
        File.WriteAllText(output + "/audit.json", rows.ToString());
        foreach (var row in rows.Where(r => r["id"] != null))
            if (!(bool)row["model"] || !(bool)row["sprite"] || !(bool)row["English"]["localized"] || !(bool)row["Russian"]["localized"])
                throw new InvalidOperationException("Incomplete survival item: " + row["id"]);
        Debug.Log("SURVIVAL_AUDIT " + output);
    }

    private static JArray Vector(Vector3 value) => new JArray(value.x, value.y, value.z);

    private static void Capture(Camera camera, Vector3 position, Vector3 lookAt, string path)
    {
        var target = new RenderTexture(1000, 700, 24, RenderTextureFormat.ARGB32); target.Create();
        var previous = RenderTexture.active;
        var image = new Texture2D(1000, 700, TextureFormat.RGBA32, false);
        try
        {
            camera.aspect = 1000f / 700; camera.transform.position = position; camera.transform.LookAt(lookAt);
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 1000, 700), 0, 0); image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
        }
        finally { RenderTexture.active = previous; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(image); }
    }
}
#endif
