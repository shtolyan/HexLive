#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Spec 40.8 v4.2: procedural sweat-droplet PATCH sheet. Each cell packs
    /// 8-16 small ROUND water beads — the user verdict on v4.0 single-drop
    /// stamps was "one drop is nothing, cover the whole body", and v4.1's
    /// hanging teardrops were cut: texture-space "down" is meaningless on an
    /// animated body (lying/standing/crawling), so a run-trail pointing up
    /// reads broken. Round beads are pose-agnostic. Density itself is not
    /// the old pox problem: v3's beads were NAKED normal relief; these
    /// drops carry the full water shading (refraction, wet darkening,
    /// specular dot, caustic rim) via DropletStamp.shader. Every drop is an
    /// analytic sphere-cap height field; cells are seeded-scattered in code
    /// — no imported assets. Two outputs:
    ///   Normal — RGB = encoded tangent normal from the height gradient,
    ///            A = drop coverage (drop-in for RepaintSlotNormal).
    ///   Effect — R,G = normal XY (0.5-centered), B = highlight mask
    ///            (specular dot per drop + caustic lower rim), A = coverage
    ///            split: 0.5..1 inside a drop, 0..0.5 damp halo.
    /// </summary>
    public static class SweatDropletSheet
    {
        public const int Cells = 8;
        private const int Cols = 4;
        private const int Rows = 2;
        private const int CellPx = 128;
        // Cap height as a fraction of the drop radius: water sits low.
        private const float Bulge = 0.4f;
        // Damp-halo reach beyond each drop edge, relative to its radius.
        private const float HaloReach = 0.45f;
        private const float HaloAlpha = 0.85f; // encoded scale inside 0..0.5

        private static Texture2D? _normal;
        private static Texture2D? _effect;

        private struct Drop
        {
            public Vector2 Center;
            public float Radius;
        }

        private static Drop[][]? _cells;

        public static Texture2D Normal
        {
            get { EnsureGenerated(); return _normal!; }
        }

        public static Texture2D Effect
        {
            get { EnsureGenerated(); return _effect!; }
        }

        /// <summary>Atlas source rect for Graphics.DrawTexture (UV space).</summary>
        public static Rect CellRect(int index)
        {
            index = Mathf.Clamp(index, 0, Cells - 1);
            return new Rect(index % Cols / (float)Cols, index / Cols / (float)Rows,
                1f / Cols, 1f / Rows);
        }

        /// <summary>v4.2: all cells are round beads (run-trails were cut —
        /// texture "down" is wrong half the time on an animated body).</summary>
        public static bool IsElongated(int index) => false;

        // No-domain-reload runs keep statics between plays; destroyed textures
        // must not stick around as fake-null references.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _normal = null;
            _effect = null;
            _cells = null;
        }

        // Seeded scatter: 8-16 non-overlapping round beads per cell (v4.2:
        // beads halved vs v4.1, roughly twice as many — "просто пузырьков
        // достаточно, не стекающих").
        private static Drop[][] BuildCells()
        {
            var cells = new Drop[Cells][];
            for (var ci = 0; ci < Cells; ci++)
            {
                var state = (uint)(ci * 7919 + 131) | 1u;
                var sparse = ci < 2; // two hero cells: fewer, larger beads
                var count = sparse ? 8 : 12 + ci % 5;
                var drops = new System.Collections.Generic.List<Drop>();
                for (var attempt = 0; attempt < 160 && drops.Count < count; attempt++)
                {
                    var r = sparse
                        ? 0.06f + NextRand(ref state) * 0.04f
                        : 0.04f + NextRand(ref state) * 0.035f;
                    var margin = r * 1.3f + 0.015f;
                    var c = new Vector2(
                        margin + NextRand(ref state) * (1f - 2f * margin),
                        margin + NextRand(ref state) * (1f - 2f * margin));
                    var overlaps = false;
                    foreach (var d in drops)
                    {
                        if (Vector2.Distance(c, d.Center) < (r + d.Radius) * 1.1f + 0.015f)
                        {
                            overlaps = true;
                            break;
                        }
                    }

                    if (!overlaps)
                    {
                        drops.Add(new Drop { Center = c, Radius = r });
                    }
                }

                cells[ci] = drops.ToArray();
            }

            return cells;
        }

        private static void EnsureGenerated()
        {
            if (_normal != null && _effect != null)
            {
                return;
            }

            _cells = BuildCells();
            var width = Cols * CellPx;
            var height = Rows * CellPx;
            // Linear: both textures carry vector/mask DATA, not colour.
            _normal = new Texture2D(width, height, TextureFormat.RGBA32, false, linear: true)
            {
                name = "SweatDropletSheetN",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _effect = new Texture2D(width, height, TextureFormat.RGBA32, false, linear: true)
            {
                name = "SweatDropletSheetFx",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var normalPixels = new Color32[width * height];
            var effectPixels = new Color32[width * height];
            var heights = new float[CellPx * CellPx];

            for (var cell = 0; cell < Cells; cell++)
            {
                var drops = _cells[cell];
                var originX = cell % Cols * CellPx;
                var originY = cell / Cols * CellPx;

                for (var py = 0; py < CellPx; py++)
                {
                    for (var px = 0; px < CellPx; px++)
                    {
                        var p = new Vector2((px + 0.5f) / CellPx, (py + 0.5f) / CellPx);
                        heights[py * CellPx + px] = HeightAt(drops, p);
                    }
                }

                for (var py = 0; py < CellPx; py++)
                {
                    for (var px = 0; px < CellPx; px++)
                    {
                        var p = new Vector2((px + 0.5f) / CellPx, (py + 0.5f) / CellPx);
                        // Signed inside-distance + the drop that owns p.
                        var edge = EdgeAt(drops, p, out var owner);

                        // ~1.5 px anti-aliased coverage.
                        var coverage = Mathf.Clamp01(edge / (1.5f / CellPx));

                        // Tangent normal from the height gradient.
                        var x0 = heights[py * CellPx + Mathf.Max(0, px - 1)];
                        var x1 = heights[py * CellPx + Mathf.Min(CellPx - 1, px + 1)];
                        var y0 = heights[Mathf.Max(0, py - 1) * CellPx + px];
                        var y1 = heights[Mathf.Min(CellPx - 1, py + 1) * CellPx + px];
                        var ddx = (x1 - x0) * (0.5f * CellPx);
                        var ddy = (y1 - y0) * (0.5f * CellPx);
                        var n = new Vector3(-ddx, -ddy, 1f).normalized;
                        n = Vector3.Lerp(new Vector3(0f, 0f, 1f), n, coverage).normalized;

                        // Highlight mask: caustic lower rim + a specular dot
                        // per drop (offset up-left like a drawn highlight).
                        var rim = 0f;
                        if (owner >= 0)
                        {
                            var d = drops[owner];
                            var rimBand = Mathf.SmoothStep(0f, 1f,
                                              Mathf.Clamp01(edge / (0.08f * d.Radius))) *
                                          (1f - Mathf.SmoothStep(0.14f * d.Radius,
                                              0.30f * d.Radius, edge));
                            var down = Mathf.Clamp01(0.35f + 0.65f * Mathf.Clamp(ddy, -1f, 1f));
                            rim = Mathf.Clamp01(rimBand * down) * 0.45f;
                            var hotCenter = d.Center + new Vector2(-0.30f, 0.30f) * d.Radius;
                            var hotD = Vector2.Distance(p, hotCenter);
                            var hot = Mathf.SmoothStep(1f, 0f,
                                Mathf.Clamp01(hotD / (0.24f * d.Radius))) * coverage;
                            rim = Mathf.Max(rim, hot);
                        }

                        // Effect alpha: inside = 0.5..1, damp halo = 0..0.5.
                        float alpha;
                        if (coverage > 0f)
                        {
                            alpha = 0.5f + 0.5f * coverage;
                        }
                        else
                        {
                            var haloR = owner >= 0 ? drops[owner].Radius : 0.1f;
                            var halo = Mathf.Clamp01(1f + edge / (HaloReach * haloR));
                            alpha = 0.5f * HaloAlpha * halo * halo;
                        }

                        var idx = (originY + py) * width + originX + px;
                        var enc = n * 0.5f + new Vector3(0.5f, 0.5f, 0.5f);
                        normalPixels[idx] = new Color32(
                            (byte)(enc.x * 255f), (byte)(enc.y * 255f), (byte)(enc.z * 255f),
                            (byte)(coverage * 255f));
                        effectPixels[idx] = new Color32(
                            (byte)(enc.x * 255f), (byte)(enc.y * 255f), (byte)(rim * 255f),
                            (byte)(alpha * 255f));
                    }
                }
            }

            _normal.SetPixels32(normalPixels);
            _normal.Apply(false, true);
            _effect.SetPixels32(effectPixels);
            _effect.Apply(false, true);
        }

        // Sphere-cap height (cell units) unioned over drops via smooth-max.
        private static float HeightAt(Drop[] drops, Vector2 p)
        {
            var h = 0f;
            foreach (var d in drops)
            {
                h = SmoothMax(h, CapHeight(Vector2.Distance(p, d.Center), d.Radius));
            }

            return h;
        }

        private static float CapHeight(float d, float r)
        {
            var t = 1f - d / r * (d / r);
            return t <= 0f ? 0f : Bulge * r * Mathf.Sqrt(t);
        }

        // Signed inside-distance to the union silhouette + owning drop index.
        private static float EdgeAt(Drop[] drops, Vector2 p, out int owner)
        {
            var edge = float.MinValue;
            owner = -1;
            for (var i = 0; i < drops.Length; i++)
            {
                var d = drops[i];
                var e = d.Radius - Vector2.Distance(p, d.Center);
                if (e > edge)
                {
                    edge = e;
                    owner = i;
                }
            }

            return edge;
        }

        private static float SmoothMax(float a, float b)
        {
            const float k = 0.012f;
            return 0.5f * (a + b + Mathf.Sqrt((a - b) * (a - b) + k * k));
        }

        private static float NextRand(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f;
        }
    }
}
