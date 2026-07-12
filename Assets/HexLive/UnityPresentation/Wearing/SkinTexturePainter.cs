#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Spec 40.8-D: paints wounds STRAIGHT INTO the skin textures (the molly
    /// hit-placement tech). Placement follows molly's MeleeOnHitNonPhysics —
    /// collider-free: bake the current pose, run a Möller–Trumbore raycast
    /// against the skin triangles, take the hit's submesh + barycentric UV.
    /// No MeshCollider means no PhysX mesh cooking per placement (the
    /// expensive part of the temp-collider variant). Cheaper than molly too:
    /// topology/UVs are cached once (they never change across pose bakes),
    /// vertex positions refresh through a reusable buffer, and the RAY is
    /// transformed into local space with one inverse matrix instead of
    /// pushing every vertex through localToWorld. Scale rides the TRS matrix
    /// exactly like molly, so the ~0.35-scaled actors hit correctly.
    /// Everything is lazy: an NPC who never bleeds allocates nothing.
    /// Each wound paints TWO stamps in one pass: the hand-picked blood-splash
    /// underlay first, the detailed gash/splat art on top. Stamp records
    /// (slot, uv, seed, textures) persist, so healing just REPAINTS the
    /// composite with lower alpha until the mark dissolves — and a
    /// save-replay reproduces identical spots (seeded rays). Skin stays on
    /// URP Lit: tan/sunburn tints multiply the repainted map exactly like
    /// the original, clothing occludes it naturally.
    /// </summary>
    public sealed class SkinTexturePainter : MonoBehaviour
    {
        private sealed class Zone
        {
            public string BoneA = string.Empty;
            public string BoneB = string.Empty;
            public float Radius;
        }

        // Same Genesis3 segments the decal system uses.
        private static readonly Dictionary<string, Zone> Zones = new()
        {
            ["Head"] = new Zone { BoneA = "head", BoneB = "", Radius = 0.055f },
            ["Torso"] = new Zone { BoneA = "abdomenUpper", BoneB = "chestUpper", Radius = 0.075f },
            ["Pelvis"] = new Zone { BoneA = "hip", BoneB = "abdomenLower", Radius = 0.075f },
            ["ArmL"] = new Zone { BoneA = "lShldrBend", BoneB = "lForearmBend", Radius = 0.026f },
            ["ArmR"] = new Zone { BoneA = "rShldrBend", BoneB = "rForearmBend", Radius = 0.026f },
            ["LegL"] = new Zone { BoneA = "lThighBend", BoneB = "lShin", Radius = 0.042f },
            ["LegR"] = new Zone { BoneA = "rThighBend", BoneB = "rShin", Radius = 0.042f },
        };

        private sealed class Stamp
        {
            public string Key = string.Empty;
            public int Slot;
            public Vector2 Uv;
            public int Seed;
            public float UvSize;
            public Texture? Under;
            public Texture? Over;
            public bool IsBandage;
        }

        // 1024 visibly softened the 4096 Daz skin (the whole slot swaps to the
        // paint target on the first wound) — 2048 keeps the pores readable.
        private const int MaxRenderTextureSize = 2048;
        // GUI-neutral: Graphics.DrawTexture doubles the colour, so 0.5 gray
        // renders the stamp unmodified; alpha likewise runs on a 0.5 scale.
        private static Color StampTint(float alpha) => new(0.5f, 0.5f, 0.5f, alpha * 0.5f);

        // Stamp art loads once per session, not once per wound.
        private static bool _stampTexturesLoaded;
        private static Texture2D? _texSplash;
        private static Texture2D? _texScratch;
        private static Texture2D? _texSplat;
        private static Texture2D? _texBandage;

        private SkinnedMeshRenderer? _body;
        private BodyBones? _bones;
        private Transform? _bodyRoot;
        private int _npcId;
        private float _height = 1.7f;
        private HashSet<int> _skinSlots = new();

        private Material[]? _materials;      // per-NPC instances
        private Texture?[] _originalAlbedo = System.Array.Empty<Texture?>();
        private RenderTexture?[] _slotRt = System.Array.Empty<RenderTexture?>();

        private readonly Dictionary<string, Stamp> _stamps = new();
        private readonly Dictionary<string, float> _alpha = new(); // key -> current fade
        private readonly HashSet<string> _desired = new();
        private readonly List<string> _stale = new();
        private int _lastStateHash;

        // ---- raycast working set (lazy: built on the FIRST placement) ----
        // Triangle indices + per-triangle slot never change when a pose is
        // baked, so they are cached once; only vertex positions refresh.
        private Mesh? _bakedMesh;
        private readonly List<Vector3> _bakedVerts = new();
        private int[] _skinTriangles = System.Array.Empty<int>();
        private int[] _skinTriangleSlot = System.Array.Empty<int>(); // per tri
        private Vector2[] _uvs = System.Array.Empty<Vector2>();
        private Matrix4x4 _worldToLocal = Matrix4x4.identity;

        public void Construct(SkinnedMeshRenderer body, IEnumerable<int> skinSlots,
            BodyBones bones, Transform bodyRoot, int npcId)
        {
            _body = body;
            _bones = bones;
            _bodyRoot = bodyRoot;
            _npcId = npcId;
            _height = 1.7f * bodyRoot.lossyScale.y;
            _skinSlots = new HashSet<int>(skinSlots);

            _materials = body.materials; // instantiate once, per NPC
            _originalAlbedo = new Texture?[_materials.Length];
            _slotRt = new RenderTexture?[_materials.Length];
            for (var i = 0; i < _materials.Length; i++)
            {
                _originalAlbedo[i] = _materials[i] != null && _materials[i].HasProperty("_BaseMap")
                    ? _materials[i].GetTexture("_BaseMap")
                    : null;
            }
        }

        /// <summary>
        /// wounds: (zone, seed, heal01) records; bandaged: zones under a leaf
        /// wrap. New wounds raycast-place once; heals repaint with lower alpha;
        /// fully healed marks vanish (composite rebuilt from the original).
        /// </summary>
        public void Sync(List<(string zone, int seed, float heal)> wounds, HashSet<string> bandaged)
        {
            if (_body == null || _materials == null)
            {
                return;
            }

            _desired.Clear();
            var stateHash = 17;
            var needsPlacement = false;

            foreach (var (zone, seed, heal) in wounds)
            {
                _ = zone;
                var key = $"w{seed}";
                _desired.Add(key);
                // Fade buckets of 0.1 — repaint only when a step is crossed.
                var fade = Mathf.Clamp01(1f - heal);
                _alpha[key] = fade;
                stateHash = stateHash * 31 + seed;
                stateHash = stateHash * 31 + (int)(fade * 10f);
                if (!_stamps.ContainsKey(key))
                {
                    needsPlacement = true;
                }
            }

            foreach (var zone in bandaged)
            {
                var key = $"b{zone}";
                _desired.Add(key);
                _alpha[key] = 1f;
                stateHash = stateHash * 31 + key.GetHashCode();
                if (!_stamps.ContainsKey(key))
                {
                    needsPlacement = true;
                }
            }

            // Drop records that no longer exist (healed / unbandaged).
            _stale.Clear();
            foreach (var key in _stamps.Keys)
            {
                if (!_desired.Contains(key))
                {
                    _stale.Add(key);
                }
            }

            foreach (var key in _stale)
            {
                _stamps.Remove(key);
                _alpha.Remove(key);
            }

            if (_stale.Count > 0)
            {
                stateHash = stateHash * 31 + 1;
            }

            if (stateHash == _lastStateHash && !needsPlacement)
            {
                return; // nothing changed — no repaint
            }

            _lastStateHash = stateHash;

            if (needsPlacement)
            {
                PlaceNewStamps(wounds, bandaged);
            }

            RepaintAll();
        }

        // ---- placement: molly's bake-and-raycast, collider-free ----

        private void PlaceNewStamps(List<(string zone, int seed, float heal)> wounds, HashSet<string> bandaged)
        {
            if (!BakePoseForRaycasts())
            {
                return;
            }

            foreach (var (zone, seed, _) in wounds)
            {
                var key = $"w{seed}";
                if (!_stamps.ContainsKey(key))
                {
                    TryPlace(key, zone, seed, isBandage: false);
                }
            }

            foreach (var zone in bandaged)
            {
                var key = $"b{zone}";
                if (!_stamps.ContainsKey(key))
                {
                    // The wrap sits where the zone's wounds are: reuse the
                    // first wound seed in that zone if any, else the zone key.
                    TryPlace(key, zone, zone.GetHashCode(), isBandage: true);
                }
            }
        }

        // Bakes the current pose and refreshes the local-space working set.
        // Topology and UVs are read once — from the BAKED mesh, not the
        // shared one: the Daz import ships with Read/Write disabled
        // (isReadable false), while a runtime-baked snapshot is always CPU
        // readable. Topology never changes across bakes, so later sessions
        // only re-read vertex positions.
        private bool BakePoseForRaycasts()
        {
            if (_body == null || _bones == null || _bodyRoot == null || _body.sharedMesh == null)
            {
                return false;
            }

            _bakedMesh ??= new Mesh();
            _body.BakeMesh(_bakedMesh); // default bake: scale lives in the TRS below (molly)
            _bakedMesh.GetVertices(_bakedVerts);

            if (_skinTriangles.Length == 0)
            {
                var mesh = _bakedMesh;
                _uvs = mesh.uv;
                var triangles = new List<int>();
                var slots = new List<int>();
                foreach (var slot in _skinSlots)
                {
                    if (slot < 0 || slot >= mesh.subMeshCount)
                    {
                        continue;
                    }

                    var indices = mesh.GetTriangles(slot);
                    triangles.AddRange(indices);
                    for (var i = 0; i < indices.Length / 3; i++)
                    {
                        slots.Add(slot);
                    }
                }

                _skinTriangles = triangles.ToArray();
                _skinTriangleSlot = slots.ToArray();
                if (_skinTriangles.Length == 0 || _uvs.Length == 0)
                {
                    Debug.LogWarning($"[SkinPaint] npc{_npcId}: no skin triangles/uvs — painting disabled");
                    return false;
                }
            }

            // Whether BakeMesh already applied the transform scale varies by
            // bake path, so detect it from the data: the baked body's longest
            // local dimension is either ~body height (scale baked in) or the
            // full-size ~1.7 m rig (scale NOT baked in — apply it ourselves).
            var bodyTransform = _body.transform;
            var size = _bakedMesh!.bounds.size;
            var meshSpan = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            var lossy = bodyTransform.lossyScale;
            var scaleBakedIn = Mathf.Abs(meshSpan - _height) <
                               Mathf.Abs(meshSpan * Mathf.Max(0.0001f, lossy.y) - _height);
            var localToWorld = Matrix4x4.TRS(
                bodyTransform.position, bodyTransform.rotation,
                scaleBakedIn ? Vector3.one : lossy);
            _worldToLocal = localToWorld.inverse;
            return true;
        }

        private void TryPlace(string key, string zoneName, int seed, bool isBandage)
        {
            if (!Zones.TryGetValue(zoneName, out var zone))
            {
                PlaceTombstone(key, seed, isBandage);
                return;
            }

            var boneA = _bones!.GetBone(zone.BoneA);
            if (boneA == null)
            {
                // Unresolvable zone: record a dead stamp so the placement
                // isn't retried (and logged) on every sync forever.
                Debug.LogWarning($"[SkinPaint] npc{_npcId} {key}: bone '{zone.BoneA}' not found");
                PlaceTombstone(key, seed, isBandage);
                return;
            }

            var boneB = string.IsNullOrEmpty(zone.BoneB) ? null : _bones.GetBone(zone.BoneB);
            var state = (uint)(_npcId * 73856093 ^ seed) | 1u;
            var t = 0.25f + NextRand(ref state) * 0.5f;
            var azimuth = NextRand(ref state) * Mathf.PI * 2f;

            var axis = boneB != null
                ? boneB.position - boneA.position
                : _bodyRoot!.up * (_height * 0.1f);
            var axisDir = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.up;
            var side = Vector3.Cross(axisDir, _bodyRoot!.forward).normalized;
            if (side.sqrMagnitude < 0.01f)
            {
                side = Vector3.Cross(axisDir, Vector3.right).normalized;
            }

            var radial = Quaternion.AngleAxis(azimuth * Mathf.Rad2Deg, axisDir) * side;
            var radius = zone.Radius * _height;
            var anchor = boneA.position + axis * t;

            // The seeded surface point sits on the limb at the seeded azimuth.
            // Instead of raycasting at it (rays can slip past thin limbs), take
            // the CLOSEST skin triangle — placement can never miss.
            var searchPoint = _worldToLocal.MultiplyPoint3x4(anchor + radial * radius);
            var triangle = ClosestSkinTriangle(searchPoint);
            if (triangle < 0)
            {
                PlaceTombstone(key, seed, isBandage);
                return;
            }

            var slot = _skinTriangleSlot[triangle];
            var i0 = _skinTriangles[triangle * 3];
            var i1 = _skinTriangles[triangle * 3 + 1];
            var i2 = _skinTriangles[triangle * 3 + 2];
            // Genesis3 uses UDIM-style UV tiles (torso U∈[1,2], legs U∈[2,3]…):
            // the sampler wraps, so painting must wrap into [0,1] the same way.
            var uv = (_uvs[i0] + _uvs[i1] + _uvs[i2]) / 3f;
            uv.x = Mathf.Repeat(uv.x, 1f);
            uv.y = Mathf.Repeat(uv.y, 1f);

            EnsureStampTextures();
            _stamps[key] = new Stamp
            {
                Key = key,
                Slot = slot,
                Uv = uv,
                Seed = seed,
                UvSize = isBandage ? 0.16f : 0.09f + NextRand(ref state) * 0.05f,
                Under = isBandage ? null : _texSplash,
                Over = isBandage ? _texBandage : (seed & 1) == 0 ? _texScratch : _texSplat,
                IsBandage = isBandage
            };
            Debug.Log($"[SkinPaint] npc{_npcId} {key} zone={zoneName} -> slot={slot} uv=({uv.x:F3},{uv.y:F3})");
        }

        // A dead stamp record: paints nothing (slot -1 never matches) but
        // stops Sync from re-attempting the same placement every frame.
        private void PlaceTombstone(string key, int seed, bool isBandage)
        {
            _stamps[key] = new Stamp { Key = key, Slot = -1, Seed = seed, IsBandage = isBandage };
        }

        // Closest-by-centroid skin triangle to a baked-local point.
        private int ClosestSkinTriangle(Vector3 point)
        {
            var best = -1;
            var bestSqr = float.MaxValue;
            for (var i = 0; i < _skinTriangles.Length; i += 3)
            {
                var centroid = (_bakedVerts[_skinTriangles[i]] +
                                _bakedVerts[_skinTriangles[i + 1]] +
                                _bakedVerts[_skinTriangles[i + 2]]) / 3f;
                var sqr = (centroid - point).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = i / 3;
                }
            }

            return best;
        }

        private static void EnsureStampTextures()
        {
            if (_stampTexturesLoaded)
            {
                return;
            }

            _stampTexturesLoaded = true;
            _texSplash = Resources.Load<Texture2D>("HexLive/Decals/blood_splash");
            _texScratch = Resources.Load<Texture2D>("HexLive/Decals/wound_scratch");
            _texSplat = Resources.Load<Texture2D>("HexLive/Decals/blood_splat");
            _texBandage = Resources.Load<Texture2D>("HexLive/Decals/bandage_wrap");
        }

        // ---- painting ----

        private void RepaintAll()
        {
            if (_materials == null)
            {
                return;
            }

            // Which slots carry stamps now?
            for (var slot = 0; slot < _materials.Length; slot++)
            {
                var hasStamp = false;
                foreach (var stamp in _stamps.Values)
                {
                    if (stamp.Slot == slot)
                    {
                        hasStamp = true;
                        break;
                    }
                }

                if (hasStamp)
                {
                    RepaintSlot(slot);
                }
                else if (_slotRt[slot] != null)
                {
                    // No marks left: restore the untouched original texture.
                    _materials[slot].SetTexture("_BaseMap", _originalAlbedo[slot]);
                }
            }
        }

        private void RepaintSlot(int slot)
        {
            var source = _originalAlbedo[slot];
            if (source == null || _materials == null)
            {
                return;
            }

            var rt = _slotRt[slot];
            if (rt == null)
            {
                var w = Mathf.Min(source.width, MaxRenderTextureSize);
                var h = Mathf.Min(source.height, MaxRenderTextureSize);
                // Explicit sRGB: the albedo is an sRGB texture — a default
                // (linear) target shifts the whole slot's tone (pale skin).
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB)
                {
                    name = $"SkinPaint_{_npcId}_{slot}",
                    // Mips regenerate manually AFTER the stamps land — auto
                    // generation can run off the bare Blit and miss them.
                    useMipMap = true,
                    autoGenerateMips = false,
                    filterMode = source.filterMode,
                    anisoLevel = Mathf.Max(source.anisoLevel, 4),
                    wrapMode = source.wrapMode
                };
                rt.Create();
                _slotRt[slot] = rt;
                Debug.Log($"[SkinPaint] npc{_npcId} slot={slot}: created {w}x{h} paint target");
            }

            // Fresh copy of the authored skin, then every live stamp on top —
            // fading is just repainting with lower alpha. The RT reaches the
            // material ONLY after the base copy landed: if anything below
            // throws, the skin keeps its original texture instead of showing
            // an unfilled (black) target.
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, 1f, 1f, 0f); // (0,0) top-left, UV v flips below

                foreach (var stamp in _stamps.Values)
                {
                    if (stamp.Slot != slot || !_alpha.TryGetValue(stamp.Key, out var alpha) || alpha <= 0.01f)
                    {
                        continue;
                    }

                    // Underlay (the picked blood splash) draws wider; the
                    // detailed art centers on the exact hit UV. V axis flips
                    // (UV bottom-left origin vs pixel-matrix top-left).
                    var cx = stamp.Uv.x;
                    var cy = 1f - stamp.Uv.y;
                    if (stamp.Under != null)
                    {
                        var s = stamp.UvSize * 1.6f;
                        Graphics.DrawTexture(new Rect(cx - s * 0.5f, cy - s * 0.5f, s, s),
                            stamp.Under, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(alpha * 0.8f));
                    }

                    if (stamp.Over != null)
                    {
                        var s = stamp.UvSize;
                        Graphics.DrawTexture(new Rect(cx - s * 0.5f, cy - s * 0.5f, s, s),
                            stamp.Over, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(alpha));
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
                Debug.LogWarning($"[SkinPaint] npc{_npcId} slot={slot}: repaint failed, original restored — {e.Message}");
            }
        }

        private void OnDestroy()
        {
            foreach (var rt in _slotRt)
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

        private static float NextRand(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f;
        }
    }
}
