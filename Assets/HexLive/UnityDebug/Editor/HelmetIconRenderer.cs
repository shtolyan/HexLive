#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{

/// <summary>
/// Bug #329: иконки шлемов рендерятся ИЗ ПРЕФАБОВ в Unity — только здесь живут
/// настроенные игроком материалы (Blender их .mat не читает). Кадр повторяет
/// ICON_GENERATION_SPEC (стиль cloth): орто-камера спереди-справа-чуть-сверху,
/// четыре солнца, прозрачный фон, 512². Оффсеты headwearFit применяются, так
/// что шлем в иконке повёрнут ровно как на персонаже.
/// </summary>
public static class HelmetIconRenderer
{
    private static readonly string[] Helmets =
    {
        "helmet_space", "helmet_moto", "helmet_carbon", "helmet_knight",
        "helmet_m1", "helmet_racing", "helmet_retro", "helmet_t1",
        "helmet_tactical_headset", "helmet_bull", "helmet_tactical",
        "helmet_vietnam", "helmet_vintage",
    };

    private const int IconLayer = 31;
    private const int Resolution = 512;
    private const float Fit = 1.25f; // как cloth-стиль спеки

    // SUNS из render_item_icon.py, оси Blender(Z-up,-Y-фронт) → Unity(Y-up,+Z).
    private static readonly (Vector3 Dir, float Energy)[] Suns =
    {
        (new Vector3(0.7f, 0.9f, 1.0f), 1.6f),
        (new Vector3(-1.0f, 0.25f, 0.6f), 0.7f),
        (new Vector3(-0.2f, 0.6f, -1.0f), 0.9f),
        (new Vector3(0.0f, 1.0f, 0.1f), 0.5f),
    };

    [MenuItem("HexLive/Render Helmet Icons")]
    public static void RenderAll()
    {
        var done = 0;
        foreach (var hid in Helmets)
        {
            try
            {
                RenderOne(hid);
                done++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"HelmetIconRenderer: {hid}: {e}");
            }
        }

        AssetDatabase.Refresh();
        Debug.Log($"HelmetIconRenderer: rendered {done}/{Helmets.Length}");
    }

    private static void RenderOne(string hid)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            $"Assets/HexLiveContent/Wear/clothing.{hid}/{hid}.prefab");
        if (prefab == null)
        {
            throw new System.Exception("prefab missing");
        }

        var inst = Object.Instantiate(prefab);
        var lights = new System.Collections.Generic.List<GameObject>();
        GameObject? camGo = null;
        try
        {
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            ApplyHeadwearFit(inst, smr.transform);
            foreach (var tr in inst.GetComponentsInChildren<Transform>(true))
            {
                tr.gameObject.layer = IconLayer;
            }

            var bounds = smr.bounds;
            var maxDim = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            var direction = new Vector3(0.55f, 0.30f, 1.0f).normalized;

            camGo = new GameObject("iconcam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = maxDim * Fit * 0.5f;
            cam.transform.position = bounds.center + direction * (maxDim * 4f);
            cam.transform.LookAt(bounds.center);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = maxDim * 10f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << IconLayer;

            foreach (var (dir, energy) in Suns)
            {
                var lightGo = new GameObject("iconlight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = energy;
                light.cullingMask = 1 << IconLayer;
                light.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.LookRotation(-dir.normalized);
                lights.Add(lightGo);
            }

            var rt = new RenderTexture(Resolution, Resolution, 24, RenderTextureFormat.ARGB32);
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest
            {
                destination = rt,
            };
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);

            var tex = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            var path = $"Assets/HexLiveContent/Icons/clothing.{hid}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Debug.Log($"HelmetIconRenderer: {path}");
        }
        finally
        {
            foreach (var lightGo in lights)
            {
                Object.DestroyImmediate(lightGo);
            }

            if (camGo != null)
            {
                Object.DestroyImmediate(camGo);
            }

            Object.DestroyImmediate(inst);
        }
    }

    // headwearFit настраивал игрок (WardrobeFitGizmo, uncommitted-поле Wear) —
    // читаем через SerializedObject, чтобы не зависеть от его API.
    private static void ApplyHeadwearFit(GameObject root, Transform mesh)
    {
        var wear = root.GetComponent<HexLive.UnityPresentation.Wearing.Wear>();
        if (wear == null)
        {
            return;
        }

        var so = new SerializedObject(wear);
        var fit = so.FindProperty("headwearFit");
        if (fit == null)
        {
            return;
        }

        var position = fit.FindPropertyRelative("position");
        var rotation = fit.FindPropertyRelative("rotation");
        var scale = fit.FindPropertyRelative("scale");
        if (position != null)
        {
            mesh.localPosition += position.vector3Value;
        }

        if (rotation != null && rotation.vector3Value != Vector3.zero)
        {
            mesh.localRotation *= Quaternion.Euler(rotation.vector3Value);
        }

        if (scale != null && scale.vector3Value != Vector3.zero)
        {
            mesh.localScale = Vector3.Scale(mesh.localScale, scale.vector3Value);
        }
    }
}

}
