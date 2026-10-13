using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
// §171.20: one shared luminance atlas per source texture; no change to authored
// mesh/UV/normal/alpha. Prototype and existing authored colour variants skip this.
public static class HairTintApplier
{
    private sealed class Atlas
    {
        public Texture Source;
        public Texture2D Gray;
        public bool Building;
        public int Users;
        public float Seen;
    }
    private static readonly List<Atlas> Atlases = new();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    { foreach (var a in Atlases) if (a.Gray != null) UnityEngine.Object.Destroy(a.Gray); Atlases.Clear(); }
    public static IEnumerator Apply(GameObject hair, HairTintPalette.Tone tone, Func<bool> current, Action<bool> done = null)
    {
        if (hair == null || !current()) yield break;
        var owner = hair.AddComponent<HairTintOwnership>();
        foreach (var renderer in hair.GetComponentsInChildren<Renderer>(true))
        {
            var source = renderer.sharedMaterials; var materials = new Material[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var original = source[i];
                if (original == null) continue;
                var texture = original.mainTexture;
                Atlas atlas = null;
                if (texture != null)
                {
                    atlas = Atlases.FirstOrDefault(a => a.Source == texture);
                    if (atlas == null)
                    {
                        atlas = new Atlas { Source = texture, Building = true }; Atlases.Add(atlas);
                        yield return Build(atlas);
                    }
                    else while (atlas.Building && hair != null && current()) yield return null;
                    if (hair == null || !current()) yield break;
                    if (atlas.Gray == null) yield break;
                    atlas.Users++; atlas.Seen = Time.realtimeSinceStartup;
                    var held = atlas; owner.Releases.Add(() => { held.Users--; Trim(); });
                }
                if (hair == null || !current()) yield break;
                var copy = new Material(original) { name = original.name + " [" + tone.Id + "]" };
                var colour = new Color32(tone.R, tone.G, tone.B, (byte)Mathf.RoundToInt(original.color.a * 255));
                if (copy.HasProperty("_BaseColor")) copy.SetColor("_BaseColor", colour);
                if (copy.HasProperty("_Color")) copy.SetColor("_Color", colour);
                if (atlas != null) copy.mainTexture = atlas.Gray;
                owner.Materials.Add(copy); materials[i] = copy;
            }
            if (hair == null || !current()) yield break;
            renderer.sharedMaterials = materials;
        }
        Trim(); done?.Invoke(true);
    }
    private static IEnumerator Build(Atlas atlas)
    {
        var source = atlas.Source;
        var width = Mathf.Min(source.width, 512); var height = Mathf.Min(source.height, 512);
        var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Texture2D readback = null;
        var wrap = source.wrapMode; var filter = source.filterMode;
        var copied = false;
        try
        {
            Graphics.Blit(source, target); RenderTexture.active = target;
            readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0); readback.Apply(); copied = true;
        }
        catch (UnityException) { }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        if (!copied)
        {
            if (readback != null) UnityEngine.Object.Destroy(readback);
            atlas.Building = false; Atlases.Remove(atlas); yield break;
        }
        try
        {
            var pixels = readback.GetPixels32(); var histogram = new int[256]; var count = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                { var value = Luminance(pixels[y * width + x]); if (value > 1) { histogram[value]++; count++; } }
                if (y % 16 == 15) yield return null;
            }
            var low = Percentile(histogram, count * .05f); var high = Percentile(histogram, count * .95f);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x; var p = pixels[index];
                    var shade = high <= low ? .7f : Mathf.Lerp(.23f, 1f, Mathf.Clamp01((Luminance(p) - low) / (float)(high - low)));
                    var gray = (byte)Mathf.RoundToInt(shade * 255); pixels[index] = new Color32(gray, gray, gray, p.a);
                }
                if (y % 16 == 15) yield return null;
            }
            atlas.Gray = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "HairTintLuminance", wrapMode = wrap, filterMode = filter };
            atlas.Gray.SetPixels32(pixels); atlas.Gray.Apply(false, true);
        }
        finally { if (readback != null) UnityEngine.Object.Destroy(readback); atlas.Building = false; Trim(); }
    }
    private static int Luminance(Color32 p) => (54 * p.r + 183 * p.g + 19 * p.b) >> 8;
    private static int Percentile(int[] values, float threshold)
    { var sum = 0; for (var i = 0; i < values.Length; i++) { sum += values[i]; if (sum >= threshold) return i; } return 255; }
    private static void Trim()
    {
        foreach (var atlas in Atlases.Where(a => !a.Building && a.Users == 0).OrderBy(a => a.Seen).ToArray())
        {
            if (Atlases.Count <= 32 && atlas.Source != null) continue;
            if (atlas.Gray != null) UnityEngine.Object.Destroy(atlas.Gray); Atlases.Remove(atlas);
        }
    }
}
internal sealed class HairTintOwnership : MonoBehaviour
{
    public readonly List<Material> Materials = new();
    public readonly List<Action> Releases = new();
    private void OnDestroy()
    { foreach (var material in Materials) if (material != null) Destroy(material); foreach (var release in Releases) release(); }
}
}
