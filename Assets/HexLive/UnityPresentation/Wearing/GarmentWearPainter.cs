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
    /// bones and holes flickered in and out. Holes come ONLY from the
    /// garment's own durability (natural wear) — body wounds never rip cloth;
    /// their sole cloth feedback is blood painted into the albedo near the
    /// wound. Dirt uses the same dirt_dust sheet as skin. Blood stays on the
    /// garment; dirt opacity follows its washable simulation state.
    /// Placement: bake the garment's skinned pose, pick a seeded triangle on
    /// the mesh → slot + wrapped UV (always on a UV island).
    /// Everything is event-driven on state buckets — no per-frame work.
    /// </summary>
    public sealed class GarmentWearPainter : MonoBehaviour
    {
        private const int MaskSize = 512;
        private const float TearBucket = 0.05f;
        private const int MaxNaturalHoles = 9;
        private const int MaxDirtStamps = 14;
        private const int MaxStoredDirtStains = 64;
        private const float BloodInputBucket = 0.1f;
        private const float BloodPlacementRadius = 0.3f;

        // GUI colour doubling: 0.5 gray = unmodified stamp colour.
        private static Color StampTint(float alpha) => new(0.5f, 0.5f, 0.5f, alpha * 0.5f);

        private sealed class Hole
        {
            public int Slot;
            public Vector2 Uv;
            public float Size;   // in mask UV space
            public float Depth;  // gray level — opens once tear passes it
        }

        private sealed class PaintStain
        {
            public int Slot;
            public Vector2 Uv;
            public float Size;
            public float Alpha;
            public int Variant;
        }

        private Renderer? _renderer;
        private SkinnedMeshRenderer? _skinnedRenderer;
        private Mesh? _staticMesh;
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
        // These belong to the garment, not to the current wound/hygiene
        // snapshot. Healing removes the wound input but never these records.
        private readonly List<PaintStain> _dirtStains = new();
        private readonly List<PaintStain> _bloodStains = new();
        private readonly Dictionary<int, PaintStain> _bloodStainsByCell = new();
        private int _naturalHolesPlaced;
        private int _lastObservedDirtTarget;
        private int _lastBloodInputHash;
        private int _lastStateHash;

        // Ragged hole stamp (dark core, noisy rim) + shared art, generated once.
        private static Texture2D? _holeStamp;
        private static Texture2D? _texDirt;
        private static Texture2D? _texTearMask;
        private static Texture2D?[] _texBloodBrushes = System.Array.Empty<Texture2D?>();
        private static Material? _alphaErase;
        private static bool _artLoaded;

        // Baked-pose working set (small meshes — garments are a few k tris).
        private Mesh? _bakedMesh;
        private int[] _triangles = System.Array.Empty<int>();
        private int[] _triangleSlot = System.Array.Empty<int>();
        private Vector2[] _uvs = System.Array.Empty<Vector2>();

        public void Construct(SkinnedMeshRenderer renderer)
        {
            _renderer = renderer;
            _skinnedRenderer = renderer;
            ConstructRenderer(renderer);
        }

        public void Construct(MeshRenderer renderer, Mesh mesh)
        {
            _renderer = renderer;
            _staticMesh = mesh;
            ConstructRenderer(renderer);
        }

        private void ConstructRenderer(Renderer renderer)
        {
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
        /// Event-driven: tear follows current durability, while dirt/blood
        /// inputs append UV stains owned by this garment. Blood is permanent;
        /// dirt opacity follows the item's wash state.
        /// </summary>
        public void SetState(float tear01, float dirt01, float blood01,
            Vector4[] damageSpheres, int damageSphereCount)
        {
            if (_renderer == null || _materials == null)
            {
                return;
            }

            var tearB = Mathf.RoundToInt(Mathf.Clamp01(tear01) / TearBucket);
            var placedNew = false;

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

            placedNew |= AccumulateDirtStains(dirt01);
            placedNew |= AccumulateBloodStains(blood01, damageSpheres, damageSphereCount);

            var stateHash = 17;
            stateHash = stateHash * 31 + tearB;
            stateHash = stateHash * 31 + _holes.Count;
            stateHash = stateHash * 31 + _dirtStains.Count;
            stateHash = stateHash * 31 + _bloodStains.Count;
            if (stateHash == _lastStateHash && !placedNew)
            {
                return;
            }

            _lastStateHash = stateHash;
            _lastTear = Mathf.Clamp01(tear01);
            RepaintMasks();
            RepaintAlbedo();
        }

        public void SetDroppedState(float tear01, float dirt01, float blood01)
        {
            if (_renderer == null || _materials == null)
            {
                return;
            }

            var tear = Mathf.Clamp01(tear01);
            var changed = AccumulateDirtStains(dirt01) | AccumulateDroppedBlood(blood01);
            var naturalTarget = Mathf.Min(MaxNaturalHoles,
                Mathf.FloorToInt(tear * MaxNaturalHoles + 0.0001f));
            while (_naturalHolesPlaced < naturalTarget)
            {
                changed |= PlaceNaturalHole(_naturalHolesPlaced++);
            }

            var hash = Mathf.RoundToInt(tear / TearBucket) * 397 ^
                       _dirtStains.Count * 31 ^ _bloodStains.Count;
            if (!changed && hash == _lastStateHash)
            {
                return;
            }

            _lastStateHash = hash;
            _lastTear = tear;
            RepaintMasks();
            RepaintAlbedo();
        }

        private bool AccumulateDroppedBlood(float blood01)
        {
            var target = Mathf.Min(8, Mathf.FloorToInt(Mathf.Clamp01(blood01) * 8f + 0.0001f));
            if (_bloodStains.Count >= target || !BakePose())
            {
                return false;
            }

            var changed = false;
            while (_bloodStains.Count < target)
            {
                var index = _bloodStains.Count;
                var state = (uint)(GetInstanceID() * 2246822519u) ^ (uint)(index * 3266489917u + 17u);
                state = state * 1664525u + 1013904223u;
                var triangle = (int)(state % (uint)(_triangles.Length / 3));
                var i0 = _triangles[triangle * 3];
                var i1 = _triangles[triangle * 3 + 1];
                var i2 = _triangles[triangle * 3 + 2];
                var uv = (_uvs[i0] + _uvs[i1] + _uvs[i2]) / 3f;
                _bloodStains.Add(new PaintStain
                {
                    Slot = _triangleSlot[triangle],
                    Uv = new Vector2(Mathf.Repeat(uv.x, 1f), Mathf.Repeat(uv.y, 1f)),
                    Size = 0.07f + (state % 53u) / 53f * 0.05f,
                    Alpha = Mathf.Clamp01(blood01) * 0.5f,
                    Variant = index & 1
                });
                changed = true;
            }

            return changed;
        }

        private bool AccumulateDirtStains(float dirt01)
        {
            var target = Mathf.Min(MaxDirtStamps,
                Mathf.FloorToInt(Mathf.Clamp01(dirt01) * MaxDirtStamps + 0.0001f));
            var previousTarget = _lastObservedDirtTarget;
            var addCount = Mathf.Max(0, target - previousTarget);
            _lastObservedDirtTarget = target;

            if (target < previousTarget)
            {
                if (target == 0)
                {
                    _dirtStains.Clear();
                }
                else
                {
                    var washedAlpha = Mathf.Lerp(0.05f, 0.55f, Mathf.Clamp01(dirt01));
                    foreach (var stain in _dirtStains)
                    {
                        stain.Alpha = Mathf.Min(stain.Alpha, washedAlpha);
                    }
                }

                return true;
            }

            if (addCount == 0 || _dirtStains.Count >= MaxStoredDirtStains || !BakePose())
            {
                return false;
            }

            var added = false;
            for (var i = 0; i < addCount && _dirtStains.Count < MaxStoredDirtStains; i++)
            {
                var index = _dirtStains.Count;
                var state = (uint)(GetInstanceID() * 40503u) ^ (uint)(index * 104729 + 5);
                state = state * 1664525u + 1013904223u;
                var triangle = (int)(state % (uint)(_triangles.Length / 3));
                var i0 = _triangles[triangle * 3];
                var i1 = _triangles[triangle * 3 + 1];
                var i2 = _triangles[triangle * 3 + 2];
                var uv = (_uvs[i0] + _uvs[i1] + _uvs[i2]) / 3f;
                _dirtStains.Add(new PaintStain
                {
                    Slot = _triangleSlot[triangle],
                    Uv = new Vector2(Mathf.Repeat(uv.x, 1f), Mathf.Repeat(uv.y, 1f)),
                    Size = 0.14f + (state % 89u) / 89f * 0.10f,
                    Alpha = Mathf.Lerp(0.25f, 0.55f, Mathf.Clamp01(dirt01)),
                    Variant = 0
                });
                added = true;
            }

            return added;
        }

        private bool AccumulateBloodStains(float blood01, Vector4[] spheres, int sphereCount)
        {
            var blood = Mathf.Clamp01(blood01);
            var inputs = spheres ?? System.Array.Empty<Vector4>();
            var count = Mathf.Clamp(sphereCount, 0, Mathf.Min(8, inputs.Length));
            var inputHash = Mathf.RoundToInt(blood / BloodInputBucket);
            if (_renderer != null)
            {
                for (var i = 0; i < count; i++)
                {
                    var local = _renderer.transform.InverseTransformPoint(inputs[i]);
                    inputHash = inputHash * 31 + Mathf.RoundToInt(local.x * 10f);
                    inputHash = inputHash * 31 + Mathf.RoundToInt(local.y * 10f);
                    inputHash = inputHash * 31 + Mathf.RoundToInt(local.z * 10f);
                    inputHash = inputHash * 31 + Mathf.RoundToInt(inputs[i].w * 10f);
                }
            }

            if (blood <= 0.05f || count == 0 || inputHash == _lastBloodInputHash || !BakePose())
            {
                _lastBloodInputHash = inputHash;
                return false;
            }

            _lastBloodInputHash = inputHash;
            var vertices = _bakedMesh!.vertices;
            var localToWorld = _renderer!.localToWorldMatrix;
            var changed = false;
            for (var i = 0; i < count; i++)
            {
                var strength = Mathf.Clamp01(blood * inputs[i].w);
                if (strength <= 0.05f)
                {
                    continue;
                }

                var bestTriangle = -1;
                var bestDistance = float.MaxValue;
                var bestBarycentric = Vector3.zero;
                for (var triangle = 0; triangle < _triangles.Length / 3; triangle++)
                {
                    var i0 = _triangles[triangle * 3];
                    var i1 = _triangles[triangle * 3 + 1];
                    var i2 = _triangles[triangle * 3 + 2];
                    var a = localToWorld.MultiplyPoint3x4(vertices[i0]);
                    var b = localToWorld.MultiplyPoint3x4(vertices[i1]);
                    var c = localToWorld.MultiplyPoint3x4(vertices[i2]);
                    var closest = ClosestPointOnTriangle(inputs[i], a, b, c, out var barycentric);
                    var distance = (closest - (Vector3)inputs[i]).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestTriangle = triangle;
                        bestBarycentric = barycentric;
                    }
                }

                if (bestTriangle < 0 || bestDistance > BloodPlacementRadius * BloodPlacementRadius)
                {
                    continue;
                }

                var v0 = _triangles[bestTriangle * 3];
                var v1 = _triangles[bestTriangle * 3 + 1];
                var v2 = _triangles[bestTriangle * 3 + 2];
                var uv = _uvs[v0] * bestBarycentric.x +
                         _uvs[v1] * bestBarycentric.y +
                         _uvs[v2] * bestBarycentric.z;
                uv = new Vector2(Mathf.Repeat(uv.x, 1f), Mathf.Repeat(uv.y, 1f));
                var slot = _triangleSlot[bestTriangle];
                var cellKey = slot * 10000 + Mathf.FloorToInt(uv.x * 10f) * 100 +
                              Mathf.FloorToInt(uv.y * 10f);
                if (_bloodStainsByCell.TryGetValue(cellKey, out var existing))
                {
                    var alpha = Mathf.Max(existing.Alpha, strength * 0.55f);
                    if (alpha > existing.Alpha + 0.01f)
                    {
                        existing.Alpha = alpha;
                        changed = true;
                    }

                    continue;
                }

                var stain = new PaintStain
                {
                    Slot = slot,
                    Uv = uv,
                    Size = Mathf.Lerp(0.07f, 0.12f, strength),
                    Alpha = strength * 0.55f,
                    Variant = (cellKey & 1)
                };
                _bloodStains.Add(stain);
                _bloodStainsByCell[cellKey] = stain;
                changed = true;
            }

            return changed;
        }

        // ---- placement (molly nearest-triangle on the garment mesh) ----

        private bool BakePose()
        {
            if (_renderer == null || (_skinnedRenderer == null && _staticMesh == null))
            {
                return false;
            }

            if (_skinnedRenderer != null)
            {
                _bakedMesh ??= new Mesh();
                _skinnedRenderer.BakeMesh(_bakedMesh);
            }
            else if (_bakedMesh == null && _staticMesh != null)
            {
                _bakedMesh = Instantiate(_staticMesh);
            }

            if (_triangles.Length == 0)
            {
                var mesh = _bakedMesh!;
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

        private static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b,
            Vector3 c, out Vector3 barycentric)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = point - a;
            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                barycentric = new Vector3(1f, 0f, 0f);
                return a;
            }

            var bp = point - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                barycentric = new Vector3(0f, 1f, 0f);
                return b;
            }

            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                var v = d1 / (d1 - d3);
                barycentric = new Vector3(1f - v, v, 0f);
                return a + v * ab;
            }

            var cp = point - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                barycentric = new Vector3(0f, 0f, 1f);
                return c;
            }

            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                var w = d2 / (d2 - d6);
                barycentric = new Vector3(1f - w, 0f, w);
                return a + w * ac;
            }

            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                var w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                barycentric = new Vector3(0f, 1f - w, w);
                return b + w * (c - b);
            }

            var denominator = 1f / (va + vb + vc);
            var faceV = vb * denominator;
            var faceW = vc * denominator;
            barycentric = new Vector3(1f - faceV - faceW, faceV, faceW);
            return a + ab * faceV + ac * faceW;
        }

        // ---- painting ----

        // Per-slot tear mask: the shared artistic sheet as the base (keeps the
        // global erosion destruction sequence), this garment's holes darkened
        // on top. Hole gray = its Depth: a spot only clips open once
        // _TearAmount grows past it.
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

        // Dirt and blood are composited into one authored-resolution albedo
        // copy. They remain separate lists so washing can remove dirt without
        // touching old blood stains.
        private void RepaintAlbedo()
        {
            if (_materials == null)
            {
                return;
            }

            EnsureArt();
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
                var hasPaint = HasStainInSlot(_dirtStains, slot) || HasStainInSlot(_bloodStains, slot);
                if (!hasPaint && !punchHoles)
                {
                    if (_albedoRt[slot] != null)
                    {
                        _materials[slot].SetTexture("_BaseMap", source); // washed clean
                    }

                    continue;
                }

                var rt = _albedoRt[slot];
                if (rt == null)
                {
                    // Keep the authored texel density. Many garments ship with
                    // 4K albedo; downsampling the painted copy to 1024 made
                    // clothing go visibly soft as soon as wear/dirt appeared.
                    var max = Mathf.Max(1, SystemInfo.maxTextureSize);
                    var w = Mathf.Min(source.width, max);
                    var h = Mathf.Min(source.height, max);
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
                    foreach (var stain in _dirtStains)
                    {
                        if (_texDirt == null || stain.Slot != slot || stain.Alpha <= 0.001f)
                        {
                            continue;
                        }

                        var cx = stain.Uv.x;
                        var cy = 1f - stain.Uv.y;
                        var s = stain.Size;
                        Graphics.DrawTexture(new Rect(cx - s * 0.5f, cy - s * 0.5f, s, s),
                            _texDirt, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                            StampTint(stain.Alpha));
                    }

                    foreach (var stain in _bloodStains)
                    {
                        var brush = _texBloodBrushes.Length > 0
                            ? _texBloodBrushes[Mathf.Clamp(stain.Variant, 0, _texBloodBrushes.Length - 1)]
                            : null;
                        if (brush == null || stain.Slot != slot || stain.Alpha <= 0.001f)
                        {
                            continue;
                        }

                        var cx = stain.Uv.x;
                        var cy = 1f - stain.Uv.y;
                        var s = stain.Size;
                        Graphics.DrawTexture(new Rect(cx - s * 0.5f, cy - s * 0.5f, s, s),
                            brush, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                            StampTint(stain.Alpha));
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
                    Debug.LogWarning($"[GarmentWear] albedo repaint failed — {e.Message}");
                }
            }
        }

        private static bool HasStainInSlot(List<PaintStain> stains, int slot)
        {
            foreach (var stain in stains)
            {
                if (stain.Slot == slot && stain.Alpha > 0.001f)
                {
                    return true;
                }
            }

            return false;
        }

        // No-domain-reload runs keep statics between plays — never cache a
        // null asset load across sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _artLoaded = false;
            _texDirt = null;
            _texTearMask = null;
            _texBloodBrushes = System.Array.Empty<Texture2D?>();
            _holeStamp = null;
            _alphaErase = null;
        }

        // A hole's erase strength on sheer fabric: it opens as erosion passes
        // its authored depth — mirroring the tear-mask clip semantics.
        private float HoleOpenStrength(Hole hole)
        {
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
            if (_artLoaded && _texDirt != null && _texTearMask != null && _alphaErase != null &&
                _texBloodBrushes.Length == 2)
            {
                return;
            }

            _artLoaded = true;
            _texDirt = Resources.Load<Texture2D>("HexLive/Decals/dirt_dust");
            _texTearMask = Resources.Load<Texture2D>("HexLive/Decals/tear_mask");
            _texBloodBrushes = new Texture2D?[]
            {
                Resources.Load<Texture2D>("HexLive/Decals/wound_scratch"),
                Resources.Load<Texture2D>("HexLive/Decals/blood_splat")
            };
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
