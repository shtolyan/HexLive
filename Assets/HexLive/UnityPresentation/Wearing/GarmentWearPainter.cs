#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Spec 40.10-D: garment wear painted INTO textures (the skin-painter
    /// tech applied to clothes). Holes are STAMPED into a per-garment copy of
    /// the artistic tear mask in UV space, so they are glued to the fabric —
    /// the previous world-space damage-sphere clip breathed with the animated
    /// bones and holes flickered in and out. Dirt grains stamp into a copy of
    /// the garment albedo with the SAME dirt_dust sheet the skin uses, so
    /// filth reads consistently across skin and cloth (the shader's
    /// procedural dust speckle is muted in this mode).
    /// Placement is the proven molly pipeline: bake the garment's skinned
    /// pose, nearest triangle to the damage point → slot + wrapped UV.
    /// Everything is event-driven on state buckets — no per-frame work.
    /// </summary>
    public sealed class GarmentWearPainter : MonoBehaviour
    {
        private const int MaskSize = 512;
        private const float TearBucket = 0.05f;
        private const float DirtBucket = 0.1f;
        // A damage sphere places/refreshes its hole when its strength crosses
        // another quarter step (fresh wounds re-rip the same spot).
        private const float SphereBucket = 0.25f;
        private const int MaxNaturalHoles = 9;
        private const int MaxDirtStamps = 14;

        // GUI colour doubling: 0.5 gray = unmodified stamp colour.
        private static Color StampTint(float alpha) => new(0.5f, 0.5f, 0.5f, alpha * 0.5f);

        private sealed class Hole
        {
            public int Slot;
            public Vector2 Uv;
            public float Size;   // in mask UV space
            public float Depth;  // 0 = clips at any tear amount
        }

        private SkinnedMeshRenderer? _renderer;
        private Material[]? _materials;
        private Texture?[] _originalAlbedo = System.Array.Empty<Texture?>();
        private RenderTexture?[] _maskRt = System.Array.Empty<RenderTexture?>();
        private RenderTexture?[] _albedoRt = System.Array.Empty<RenderTexture?>();
        // Transparent slots (stockings, sheer sleeves) keep their authored
        // shader — their holes are ERASED from the albedo alpha instead of
        // clipped by the tear mask (the opaque tear shader made them black).
        private bool[] _transparentSlot = System.Array.Empty<bool>();
        private float _lastTear;

        private readonly List<Hole> _holes = new();
        private readonly float[] _sphereBuckets = new float[8];
        private int _naturalHolesPlaced;
        private int _lastStateHash;

        // Ragged hole stamp (dark core, noisy rim) + shared art, generated once.
        private static Texture2D? _holeStamp;
        private static Texture2D? _texDirt;
        private static Texture2D? _texTearMask;
        private static Material? _alphaErase;
        private static bool _artLoaded;

        // Baked-pose working set (small meshes — garments are a few k tris).
        private Mesh? _bakedMesh;
        private readonly List<Vector3> _bakedVerts = new();
        private int[] _triangles = System.Array.Empty<int>();
        private int[] _triangleSlot = System.Array.Empty<int>();
        private Vector2[] _uvs = System.Array.Empty<Vector2>();
        private Matrix4x4 _worldToLocal = Matrix4x4.identity;

        public bool HasDamageHoles { get; private set; }

        public void Construct(SkinnedMeshRenderer renderer)
        {
            _renderer = renderer;
            _materials = renderer.materials; // instances (ApplyTearShader made them)
            _originalAlbedo = new Texture?[_materials.Length];
            _maskRt = new RenderTexture?[_materials.Length];
            _albedoRt = new RenderTexture?[_materials.Length];
            _transparentSlot = new bool[_materials.Length];
            for (var i = 0; i < _materials.Length; i++)
            {
                _originalAlbedo[i] = _materials[i] != null && _materials[i].HasProperty("_BaseMap")
                    ? _materials[i].GetTexture("_BaseMap")
                    : null;
                _transparentSlot[i] = Wear.IsTransparentMaterial(_materials[i]);
            }
        }

        /// <summary>
        /// Event-driven: repaints only when a tear/dirt bucket or a sphere
        /// strength bucket changes. Spheres are world-space wound anchors —
        /// their UVs freeze at placement, so animation can't move the holes.
        /// </summary>
        public void SetState(float tear01, float dirt01, Vector4[] spheres, int sphereCount)
        {
            if (_renderer == null || _materials == null)
            {
                return;
            }

            var tearB = Mathf.RoundToInt(Mathf.Clamp01(tear01) / TearBucket);
            var dirtB = Mathf.RoundToInt(Mathf.Clamp01(dirt01) / DirtBucket);

            var placedNew = false;

            // Damage holes: one per sphere slot, refreshed when the wound
            // there deepens past another quarter.
            for (var i = 0; i < Mathf.Min(sphereCount, _sphereBuckets.Length); i++)
            {
                var bucket = Mathf.Ceil(Mathf.Clamp01(spheres[i].w) / SphereBucket) * SphereBucket;
                if (bucket > _sphereBuckets[i] + 0.001f && spheres[i].w > 0.2f)
                {
                    _sphereBuckets[i] = bucket;
                    if (PlaceDamageHole(spheres[i]))
                    {
                        placedNew = true;
                        HasDamageHoles = true;
                    }
                }
            }

            // Natural wear: seeded holes accumulate as the garment erodes.
            var naturalTarget = Mathf.Min(MaxNaturalHoles, Mathf.FloorToInt(Mathf.Clamp01(tear01) * MaxNaturalHoles + 0.0001f));
            while (_naturalHolesPlaced < naturalTarget)
            {
                if (PlaceNaturalHole(_naturalHolesPlaced))
                {
                    placedNew = true;
                }

                _naturalHolesPlaced++;
            }

            var stateHash = 17;
            stateHash = stateHash * 31 + tearB;
            stateHash = stateHash * 31 + dirtB;
            stateHash = stateHash * 31 + _holes.Count;
            if (stateHash == _lastStateHash && !placedNew)
            {
                return;
            }

            _lastStateHash = stateHash;
            _lastTear = Mathf.Clamp01(tear01);
            RepaintMasks();
            RepaintDirt(dirt01);
        }

        // ---- placement (molly nearest-triangle on the garment mesh) ----

        private bool BakePose()
        {
            if (_renderer == null || _renderer.sharedMesh == null)
            {
                return false;
            }

            _bakedMesh ??= new Mesh();
            _renderer.BakeMesh(_bakedMesh);
            _bakedMesh.GetVertices(_bakedVerts);

            if (_triangles.Length == 0)
            {
                var mesh = _bakedMesh;
                _uvs = mesh.uv;
                var triangles = new List<int>();
                var slots = new List<int>();
                for (var s = 0; s < mesh.subMeshCount; s++)
                {
                    var indices = mesh.GetTriangles(s);
                    triangles.AddRange(indices);
                    for (var i = 0; i < indices.Length / 3; i++)
                    {
                        slots.Add(s);
                    }
                }

                _triangles = triangles.ToArray();
                _triangleSlot = slots.ToArray();
                if (_triangles.Length == 0 || _uvs.Length == 0)
                {
                    return false;
                }
            }

            // Bake-scale detection like the skin painter, but against the
            // renderer's own world bounds (garments have no canonical height).
            var t = _renderer.transform;
            var size = _bakedMesh.bounds.size;
            var meshSpan = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            var world = _renderer.bounds.size;
            var worldSpan = Mathf.Max(world.x, Mathf.Max(world.y, world.z));
            var lossy = Mathf.Max(0.0001f, t.lossyScale.y);
            var scaleBakedIn = Mathf.Abs(meshSpan - worldSpan) <
                               Mathf.Abs(meshSpan * lossy - worldSpan);
            var localToWorld = Matrix4x4.TRS(t.position, t.rotation,
                scaleBakedIn ? Vector3.one : t.lossyScale);
            _worldToLocal = localToWorld.inverse;
            return true;
        }

        private bool PlaceDamageHole(Vector4 sphere)
        {
            if (!BakePose())
            {
                return false;
            }

            var local = _worldToLocal.MultiplyPoint3x4(new Vector3(sphere.x, sphere.y, sphere.z));
            var triangle = ClosestTriangle(local);
            if (triangle < 0)
            {
                return false;
            }

            AddHoleAt(triangle,
                0.04f + Mathf.Clamp01(sphere.w) * 0.04f,
                depth: 0.08f); // bites start as punctures; heavy wear opens them
            return true;
        }

        private bool PlaceNaturalHole(int index)
        {
            if (!BakePose())
            {
                return false;
            }

            // Seeded triangle pick: always ON the garment (random UVs could
            // land between islands), deterministic per garment + index.
            var state = (uint)(GetInstanceID() * 2654435761u) ^ (uint)(index * 9176u + 1);
            state = state * 1664525u + 1013904223u;
            var triangle = (int)(state % (uint)(_triangles.Length / 3));
            AddHoleAt(triangle,
                0.03f + (state % 97u) / 97f * 0.035f,
                depth: 0.30f + index * 0.06f); // later holes need deeper wear
            return true;
        }

        private void AddHoleAt(int triangle, float size, float depth)
        {
            var i0 = _triangles[triangle * 3];
            var i1 = _triangles[triangle * 3 + 1];
            var i2 = _triangles[triangle * 3 + 2];
            var uv = (_uvs[i0] + _uvs[i1] + _uvs[i2]) / 3f;
            uv.x = Mathf.Repeat(uv.x, 1f);
            uv.y = Mathf.Repeat(uv.y, 1f);
            _holes.Add(new Hole
            {
                Slot = _triangleSlot[triangle],
                Uv = uv,
                Size = size,
                Depth = depth
            });
        }

        private int ClosestTriangle(Vector3 point)
        {
            var best = -1;
            var bestSqr = float.MaxValue;
            for (var i = 0; i < _triangles.Length; i += 3)
            {
                var centroid = (_bakedVerts[_triangles[i]] +
                                _bakedVerts[_triangles[i + 1]] +
                                _bakedVerts[_triangles[i + 2]]) / 3f;
                var sqr = (centroid - point).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = i / 3;
                }
            }

            return best;
        }

        // ---- painting ----

        // Per-slot tear mask: the shared artistic sheet as the base (keeps the
        // global erosion destruction sequence), this garment's holes darkened
        // on top. Hole gray = its Depth: dark bites clip open right away,
        // pale natural spots only once _TearAmount grows past them.
        private void RepaintMasks()
        {
            if (_materials == null)
            {
                return;
            }

            EnsureArt();
            for (var slot = 0; slot < _materials.Length; slot++)
            {
                if (_transparentSlot[slot])
                {
                    continue; // holes are erased from the albedo alpha instead
                }

                var hasHole = false;
                foreach (var hole in _holes)
                {
                    if (hole.Slot == slot)
                    {
                        hasHole = true;
                        break;
                    }
                }

                if (!hasHole)
                {
                    continue;
                }

                var rt = _maskRt[slot];
                if (rt == null)
                {
                    // sRGB like the authored mask: hole depth grays must mean
                    // the SAME clip levels as the artistic sheet's grays.
                    rt = new RenderTexture(MaskSize, MaskSize, 0, RenderTextureFormat.ARGB32,
                        RenderTextureReadWrite.sRGB)
                    {
                        name = $"GarmentMask_{GetInstanceID()}_{slot}",
                        useMipMap = false
                    };
                    rt.Create();
                    _maskRt[slot] = rt;
                }

                var previous = RenderTexture.active;
                try
                {
                    if (_texTearMask != null)
                    {
                        Graphics.Blit(_texTearMask, rt);
                    }
                    else
                    {
                        RenderTexture.active = rt;
                        GL.Clear(false, true, Color.white);
                    }

                    RenderTexture.active = rt;
                    GL.PushMatrix();
                    GL.LoadPixelMatrix(0f, 1f, 1f, 0f);
                    foreach (var hole in _holes)
                    {
                        if (hole.Slot != slot || _holeStamp == null)
                        {
                            continue;
                        }

                        var cx = hole.Uv.x;
                        var cy = 1f - hole.Uv.y;
                        var s = hole.Size;
                        // The stamp's core is Depth-gray: DrawTexture blends
                        // the mask down toward it by the stamp's own alpha.
                        var tint = new Color(hole.Depth * 0.5f, hole.Depth * 0.5f, hole.Depth * 0.5f, 0.5f);
                        Graphics.DrawTexture(new Rect(cx - s * 0.5f, cy - s * 0.5f, s, s),
                            _holeStamp, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, tint);
                    }

                    GL.PopMatrix();
                    RenderTexture.active = previous;
                    _materials[slot].SetTexture("_TearMaskTex", rt);
                    _materials[slot].SetFloat("_TearTexOn", 1f);
                }
                catch (System.Exception e)
                {
                    RenderTexture.active = previous;
                    Debug.LogWarning($"[GarmentWear] mask repaint failed — {e.Message}");
                }
            }
        }

        // Dirt grains into the albedo copy — same sheet as the skin dirt, so
        // a filthy girl reads consistently head to toe.
        private void RepaintDirt(float dirt01)
        {
            if (_materials == null)
            {
                return;
            }

            EnsureArt();
            var count = Mathf.Min(MaxDirtStamps, Mathf.FloorToInt(Mathf.Clamp01(dirt01) * MaxDirtStamps + 0.0001f));
            for (var slot = 0; slot < _materials.Length; slot++)
            {
                var source = _originalAlbedo[slot];
                if (source == null)
                {
                    continue;
                }

                // Transparent slots erase their holes from the albedo alpha
                // (their authored shader blends them out) — the albedo copy
                // is needed even with zero dirt once a hole is open.
                var punchHoles = _transparentSlot[slot] && AnyOpenHoleIn(slot);
                if (count == 0 && !punchHoles)
                {
                    if (_albedoRt[slot] != null)
                    {
                        _materials[slot].SetTexture("_BaseMap", source); // washed clean
                    }

                    continue;
                }

                if (!BakePose())
                {
                    return;
                }

                var rt = _albedoRt[slot];
                if (rt == null)
                {
                    var w = Mathf.Min(source.width, 1024);
                    var h = Mathf.Min(source.height, 1024);
                    rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                        RenderTextureReadWrite.sRGB)
                    {
                        name = $"GarmentDirt_{GetInstanceID()}_{slot}",
                        useMipMap = true,
                        autoGenerateMips = false,
                        filterMode = source.filterMode,
                        anisoLevel = Mathf.Max(source.anisoLevel, 4),
                        wrapMode = source.wrapMode
                    };
                    rt.Create();
                    _albedoRt[slot] = rt;
                }

                var previous = RenderTexture.active;
                try
                {
                    Graphics.Blit(source, rt);
                    RenderTexture.active = rt;
                    GL.PushMatrix();
                    GL.LoadPixelMatrix(0f, 1f, 1f, 0f);
                    for (var i = 0; i < count; i++)
                    {
                        if (_texDirt == null)
                        {
                            break;
                        }

                        // Seeded triangle per stamp — deterministic, on-island.
                        var state = (uint)(GetInstanceID() * 40503u) ^ (uint)(slot * 7919 + i * 104729 + 5);
                        state = state * 1664525u + 1013904223u;
                        var triangle = (int)(state % (uint)(_triangles.Length / 3));
                        if (_triangleSlot[triangle] != slot)
                        {
                            continue; // grain belongs to another slot's island
                        }

                        var i0 = _triangles[triangle * 3];
                        var i1 = _triangles[triangle * 3 + 1];
                        var i2 = _triangles[triangle * 3 + 2];
                        var uv = (_uvs[i0] + _uvs[i1] + _uvs[i2]) / 3f;
                        var cx = Mathf.Repeat(uv.x, 1f);
                        var cy = 1f - Mathf.Repeat(uv.y, 1f);
                        var s = 0.14f + (state % 89u) / 89f * 0.10f;
                        Graphics.DrawTexture(new Rect(cx - s * 0.5f, cy - s * 0.5f, s, s),
                            _texDirt, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                            StampTint(Mathf.Clamp01(dirt01)));
                    }

                    // Punch the open holes out of the alpha (sheer garments).
                    if (punchHoles && _alphaErase != null && _holeStamp != null)
                    {
                        foreach (var hole in _holes)
                        {
                            if (hole.Slot != slot)
                            {
                                continue;
                            }

                            var strength = HoleOpenStrength(hole);
                            if (strength <= 0.01f)
                            {
                                continue;
                            }

                            _alphaErase.SetFloat("_Strength", strength);
                            var cx = Mathf.Repeat(hole.Uv.x, 1f);
                            var cy = 1f - Mathf.Repeat(hole.Uv.y, 1f);
                            Graphics.DrawTexture(
                                new Rect(cx - hole.Size * 0.5f, cy - hole.Size * 0.5f, hole.Size, hole.Size),
                                _holeStamp, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                                Color.white, _alphaErase);
                        }
                    }

                    GL.PopMatrix();
                    RenderTexture.active = previous;
                    rt.GenerateMips();
                    _materials[slot].SetTexture("_BaseMap", rt);
                }
                catch (System.Exception e)
                {
                    RenderTexture.active = previous;
                    _materials[slot].SetTexture("_BaseMap", source);
                    Debug.LogWarning($"[GarmentWear] dirt repaint failed — {e.Message}");
                }
            }
        }

        // No-domain-reload runs keep statics between plays — never cache a
        // null asset load across sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _artLoaded = false;
            _texDirt = null;
            _texTearMask = null;
            _holeStamp = null;
            _alphaErase = null;
        }

        // A hole's erase strength on sheer fabric: bite holes (depth ≈ 0) are
        // open immediately; natural-wear holes open as erosion passes their
        // authored depth — mirroring the tear-mask clip semantics.
        private float HoleOpenStrength(Hole hole)
        {
            if (hole.Depth <= 0.05f)
            {
                return 1f;
            }

            return Mathf.Clamp01((_lastTear * 1.08f - hole.Depth) * 5f);
        }

        private bool AnyOpenHoleIn(int slot)
        {
            foreach (var hole in _holes)
            {
                if (hole.Slot == slot && HoleOpenStrength(hole) > 0.01f)
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnsureArt()
        {
            if (_artLoaded && _texDirt != null && _texTearMask != null && _alphaErase != null)
            {
                return;
            }

            _artLoaded = true;
            _texDirt = Resources.Load<Texture2D>("HexLive/Decals/dirt_dust");
            _texTearMask = Resources.Load<Texture2D>("HexLive/Decals/tear_mask");
            var erase = Shader.Find("Hidden/HexLive/AlphaErase");
            _alphaErase = erase != null ? new Material(erase) : null;

            // Ragged hole stamp: white core fading out with an angular-noise
            // rim (the TINT recolors the core to the hole's depth-gray).
            const int size = 96;
            _holeStamp = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size - 0.5f;
                    var v = (y + 0.5f) / size - 0.5f;
                    var angle = Mathf.Atan2(v, u);
                    var ragged = 0.36f + 0.10f * Mathf.Sin(angle * 5f) * Mathf.Sin(angle * 3.3f + 1.7f);
                    var d = Mathf.Sqrt(u * u + v * v) / ragged;
                    var alpha = Mathf.Clamp01(1.35f - d * 1.35f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha * alpha);
                }
            }

            _holeStamp.SetPixels(pixels);
            _holeStamp.Apply();
        }

        private void OnDestroy()
        {
            foreach (var rt in _maskRt)
            {
                if (rt != null)
                {
                    rt.Release();
                    Destroy(rt);
                }
            }

            foreach (var rt in _albedoRt)
            {
                if (rt != null)
                {
                    rt.Release();
                    Destroy(rt);
                }
            }

            if (_bakedMesh != null)
            {
                Destroy(_bakedMesh);
            }
        }
    }
}
