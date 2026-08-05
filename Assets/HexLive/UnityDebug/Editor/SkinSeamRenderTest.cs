using System.Collections.Generic;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Spec 40.8-J: renders ONE projected decal with the real shader and the
    /// real baked maps, straight into copies of the actor's skin textures, and
    /// writes the result to PNG. No play mode, no NPC — it isolates the part
    /// that can only be judged by looking: does the wrap actually appear on
    /// BOTH textures at the hip, and is it continuous and right way up?
    ///
    /// The frame is built exactly as SkinTexturePainter.TryPlaceProjected
    /// builds it, so what lands here is what lands on the girl.
    ///
    /// Menu: <b>HexLive ▸ Paint Maps ▸ Dump Seam Test Decal</b>
    /// → Build/seamtest_&lt;actor&gt;_&lt;zone&gt;_g&lt;group&gt;.png
    /// </summary>
    public static class SkinSeamRenderTest
    {
        private const string Actor = "Jana";
        private const int DumpSize = 1024;
        // The interesting placements: the hip (upper thigh — the leg/torso
        // texture boundary the user pointed at) and the shoulder.
        private static readonly (string zone, int tRow)[] Cases =
        {
            ("LegL", 0),   // t = TMin -> up by the hip
            ("ArmL", 0),   // t = TMin -> up by the shoulder
        };

        [MenuItem("HexLive/Paint Maps/Dump Seam Test Decal")]
        public static void Dump()
        {
            var map = Resources.Load<PaintPointMap>($"{PaintPointMap.ResourceFolder}/skin_{Actor}");
            var set = Resources.Load<SkinPositionMapSet>(
                $"{PaintPointMap.ResourceFolder}/skinpos_{Actor}");
            var shader = Shader.Find("Hidden/HexLive/ProjectedStamp");
            var art = Resources.Load<Texture2D>("HexLive/Decals/bandage_wrap");
            // A WOUND also carries the pale blood-splash halo, and since the
            // two layers are composited in one pass now, that pairing is the
            // one that needs looking at.
            var woundArt = Resources.Load<Texture2D>("HexLive/Decals/wound_gash_slash");
            var halo = Resources.Load<Texture2D>("HexLive/Decals/blood_splash");
            if (map == null || set == null || shader == null || art == null)
            {
                Debug.LogError($"[SeamTest] missing: map={map != null} set={set != null} " +
                               $"shader={shader != null} art={art != null}");
                return;
            }

            var sources = SourceTexturesByGroup(set);
            var material = new Material(shader);
            var written = new List<string>();

            foreach (var (zoneName, tRow) in Cases)
            {
                var zone = map.ZoneFor(zoneName);
                if (zone == null || zone.Points.Length == 0)
                {
                    continue;
                }

                // The widest cell of that row — a placement that straddles is
                // what we came to look at; take the first valid one.
                var point = default(PaintPointMap.Point);
                for (var a = 0; a < map.AzimuthSamples; a++)
                {
                    var candidate = zone.Points[tRow * map.AzimuthSamples + a];
                    if (candidate.Valid && set.GroupOf(candidate.Slot) >= 0)
                    {
                        point = candidate;
                        break;
                    }
                }

                if (!point.Valid)
                {
                    continue;
                }

                var size = 0.14f * Mathf.Max(0.0001f, map.MeshHeight / 1.7f);
                BuildFrame(point, zone.BindAxisDir, size,
                    out var objectToDecal, out var normal);

                var underToDecal = Matrix4x4.Scale(
                    new Vector3(1f / (size * 1.6f), 1f / (size * 1.6f), 1f)) *
                    Matrix4x4.TRS(point.BindPos,
                        Quaternion.LookRotation(normal,
                            OrthoTangent(normal, zone.BindAxisDir)), Vector3.one).inverse;

                for (var group = 0; group < set.GroupPositionMaps.Length; group++)
                {
                    var path = RenderGroup(set, group, sources, material, art,
                        objectToDecal, normal, size, zoneName,
                        null, underToDecal);
                    if (path != null)
                    {
                        written.Add(path);
                    }

                    // Same spot, but the wound pairing: halo + art in one draw.
                    var wound = RenderGroup(set, group, sources, material,
                        woundArt != null ? woundArt : art,
                        objectToDecal, normal, size, $"{zoneName}wound",
                        halo, underToDecal);
                    if (wound != null)
                    {
                        written.Add(wound);
                    }
                }
            }

            Object.DestroyImmediate(material);
            Debug.Log($"[SeamTest] wrote {written.Count} images: {string.Join(", ", written)}");
        }


        // The decal's "along the limb" axis: the bone axis flattened onto the
        // surface. Same construction SkinTexturePainter uses.
        private static Vector3 OrthoTangent(Vector3 normal, Vector3 axis)
        {
            var along = axis - normal * Vector3.Dot(normal, axis);
            if (along.sqrMagnitude < 1e-6f)
            {
                along = Vector3.Cross(normal, Vector3.right);
                if (along.sqrMagnitude < 1e-6f)
                {
                    along = Vector3.Cross(normal, Vector3.forward);
                }
            }

            return along.normalized;
        }

        // Same construction as SkinTexturePainter.TryPlaceProjected.
        private static void BuildFrame(in PaintPointMap.Point point, Vector3 axis, float size,
            out Matrix4x4 objectToDecal, out Vector3 normal)
        {
            normal = point.BindNormal.normalized;
            var along = axis - normal * Vector3.Dot(normal, axis);
            if (along.sqrMagnitude < 1e-6f)
            {
                along = Vector3.Cross(normal, Vector3.right);
                if (along.sqrMagnitude < 1e-6f)
                {
                    along = Vector3.Cross(normal, Vector3.forward);
                }
            }

            along.Normalize();
            var frame = Matrix4x4.TRS(point.BindPos,
                Quaternion.LookRotation(normal, along), Vector3.one).inverse;
            objectToDecal = Matrix4x4.Scale(new Vector3(1f / size, 1f / size, 1f)) * frame;
        }

        private static string RenderGroup(SkinPositionMapSet set, int group,
            Dictionary<int, Texture> sources, Material material, Texture2D art,
            Matrix4x4 objectToDecal, Vector3 normal, float size, string zoneName,
            Texture2D halo, Matrix4x4 underToDecal)
        {
            var posMap = set.GroupPositionMaps[group];
            var nrmMap = group < set.GroupNormalMaps.Length ? set.GroupNormalMaps[group] : null;
            if (posMap == null || nrmMap == null)
            {
                return null;
            }

            var depth = size * 0.4f;   // ProjectedDepthFactor
            var footprintScale = halo != null ? 1.6f : 1f;
            var footprint = size * footprintScale;
            var slack = set.SampleSpacing + 0.005f; // ProjectedReachMargin
            var windowToDecal = halo != null ? underToDecal : objectToDecal;
            var half = new Vector3(0.5f + slack / footprint,
                0.5f + slack / footprint, depth * footprintScale + slack);
            if (!set.TryGetUvBounds(group, windowToDecal, half, out var window))
            {
                return null; // this texture is nowhere near the decal
            }

            var rt = new RenderTexture(DumpSize, DumpSize, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            rt.Create();
            var previous = RenderTexture.active;
            try
            {
                if (sources.TryGetValue(group, out var source) && source != null)
                {
                    Graphics.Blit(source, rt);
                }
                else
                {
                    RenderTexture.active = rt;
                    GL.Clear(false, true, new Color(0.55f, 0.45f, 0.40f, 1f));
                }

                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, 1f, 1f, 0f);

                material.SetTexture("_PosMap", posMap);
                material.SetTexture("_NrmMap", nrmMap);
                material.SetVector("_SlotRect",
                    new Vector4(window.x, window.y, window.width, window.height));
                material.SetMatrix("_ObjectToDecal", objectToDecal);
                material.SetVector("_DecalNormal", normal);
                material.SetFloat("_Fade", 1f);
                material.SetFloat("_Depth", depth);
                material.SetFloat("_DepthFeather", depth * 0.35f);
                var underDepth = depth * 1.6f;
                material.SetTexture("_UnderTex",
                    halo != null ? (Texture)halo : Texture2D.blackTexture);
                material.SetMatrix("_UnderToDecal", underToDecal);
                material.SetFloat("_UnderFade", halo != null ? 0.5f : 0f);
                material.SetFloat("_UnderDepth", underDepth);
                material.SetFloat("_UnderDepthFeather", underDepth * 0.35f);

                var target = new Rect(window.x, 1f - window.y - window.height,
                    window.width, window.height);
                Graphics.DrawTexture(target, art, new Rect(0f, 0f, 1f, 1f),
                    0, 0, 0, 0, Color.white, material, 0);

                GL.PopMatrix();

                var readback = new Texture2D(DumpSize, DumpSize, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, DumpSize, DumpSize), 0, 0);
                readback.Apply(false);
                RenderTexture.active = previous;

                var root = System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".";
                var path = System.IO.Path.Combine(root,
                    $"Build/seamtest_{Actor}_{zoneName}_g{group}.png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllBytes(path, readback.EncodeToPNG());
                Object.DestroyImmediate(readback);
                return System.IO.Path.GetFileName(path);
            }
            finally
            {
                RenderTexture.active = previous;
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        // group -> the skin texture its slots paint into (for context under
        // the decal; a flat fill would hide a wrong-way-up result).
        private static Dictionary<int, Texture> SourceTexturesByGroup(SkinPositionMapSet set)
        {
            var result = new Dictionary<int, Texture>();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                $"Assets/Resources/HexLive/Actors/{Actor}.prefab");
            if (prefab == null)
            {
                return result;
            }

            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var materials = smr.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var group = set.GroupOf(slot);
                    if (group < 0 || result.ContainsKey(group) || materials[slot] == null ||
                        !materials[slot].HasProperty("_BaseMap"))
                    {
                        continue;
                    }

                    var tex = materials[slot].GetTexture("_BaseMap");
                    if (tex != null)
                    {
                        result[group] = tex;
                    }
                }
            }

            return result;
        }
    }
}
