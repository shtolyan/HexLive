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

        /// <summary>Spec 40.8-G: the zone table for the editor-time
        /// PaintPointMap generator — bakes points off the SAME bone axes the
        /// legacy runtime placement aimed at, so the two paths agree.</summary>
        public static IEnumerable<(string zone, string boneA, string boneB, float radius)>
            ZoneDefinitions()
        {
            foreach (var pair in Zones)
            {
                yield return (pair.Key, pair.Value.BoneA, pair.Value.BoneB, pair.Value.Radius);
            }
        }

        // Sweat droplets sample t∈[0.15,0.85], wounds t∈[0.25,0.75] — the
        // baked grid spans the superset so one map serves both.
        public const float MapTMin = 0.15f;
        public const float MapTMax = 0.85f;

        private sealed class Stamp
        {
            public string Key = string.Empty;
            public int Slot;
            public Vector2 Uv;
            public int Seed;
            // PER-AXIS size: UV density is anisotropic (a leg tile packs the
            // circumference tight and the length loose — square-UV stamps
            // stretched into long ribs down the thigh). Sized so the stamp is
            // square and consistent in WORLD units.
            public float UvSizeX;
            public float UvSizeY;
            public Texture? Under;
            public Texture? Over;
            // Relief for the skin NORMAL map: gashes cut in, blood beads up.
            public Texture? UnderNormal;
            public Texture? OverNormal;
            // Wet-gloss shape for the painted _MetallicGlossMap (alpha =
            // smoothness 0..1 before the WoundWetGloss scale).
            public Texture? OverGloss;
            public bool IsBandage;
            // Spec 44: this wrap is a plain MEDKIT gauze dressing (not a herbal
            // leaf wrap) — paints the gauze art and backfills from _texGauze.
            public bool IsGauze;
            // Spec 40.8 v4 water droplet: the effect stamp (refraction normal
            // + rim + coverage/halo) and the atlas cell both textures use.
            public Texture? Effect;
            public Rect CellRect = new(0f, 0f, 1f, 1f);
            public bool IsDroplet;
        }

        // 1024 visibly softened the 4096 Daz skin (the whole slot swaps to the
        // paint target on the first wound) — 2048 keeps the pores readable.
        private const int MaxRenderTextureSize = 2048;
        // GUI-neutral: Graphics.DrawTexture doubles the colour, so 0.5 gray
        // renders the stamp unmodified; alpha likewise runs on a 0.5 scale.
        private static Color StampTint(float alpha) => new(0.5f, 0.5f, 0.5f, alpha * 0.5f);

        // ---- Spec 40.8 v4 water-droplet knobs ----
        // Drops appear once the wetness pool clears this (below it wetness is
        // gloss-only, as before).
        private const float DropletWetnessThreshold = 0.25f;
        // 40.8 v4.1: head PATCHES are allowed again — the pox read came from
        // naked normal-relief beads, these are fully shaded water drops. The
        // face stamps stay smaller (HeadPatchScale) as extra insurance.
        private const int FaceDroplets = 2;
        private const float HeadPatchScale = 0.6f;
        // v4.1: a stamp is a PATCH of 4-9 small drops (user verdict on
        // single-drop stamps: "one drop is nothing — cover the whole body").
        // Patch spans 5-7.5 cm; the drops inside come out Ø ~10-18 mm.
        private const float DropWorldSizeMin = 0.05f;
        private const float DropWorldSizeMax = 0.075f;
        // Belt-and-braces for the dense face UV tile ("beads blew up huge"):
        // no droplet stamp may span more UV than this on either axis.
        private const float MaxDropletUvSize = 0.12f;
        // Water shading, applied by DropletStamp.shader at stamp time.
        private const float DropGloss = 0.95f;   // smoothness inside the drop
        private const float DropDarken = 0.7f;   // wet albedo under the drop
        private const float HaloDarken = 0.9f;   // damp ring around it
        // Albedo shift as a fraction of the STAMP size. v4.1 stamps are
        // multi-drop patches, so this is ~0.3x of a single drop's span.
        private const float RefractStrength = 0.05f;
        private const float RimBoost = 0.8f;     // additive meniscus highlight
        // The gloss mask is soft — 1024 is plenty (2048 with mips costs ~21 MB
        // per slot and buys nothing for a smoothness ramp).
        private const int GlossRtSize = 1024;

        // ---- wound volume knobs (spec 40.8-D v5) ----
        // Fresh cuts glisten: absolute smoothness stamped into the wet core
        // (base skin stays at the caller's dry/wet value, 0.32 dry).
        // Full 1.0 — the wound IS the volume cue now (relief was cut for
        // UV-seam artifacts), so it must visibly out-shine everything,
        // including the 0.72 sweat sheen and the 0.95 droplets.
        private const float WoundWetGloss = 1f;

        private static readonly int UnderTexId = Shader.PropertyToID("_UnderTex");
        private static readonly int SlotRectId = Shader.PropertyToID("_SlotRect");
        private static readonly int CellRectId = Shader.PropertyToID("_CellRect");
        private static readonly int FadeId = Shader.PropertyToID("_Fade");
        private static readonly int RefractStrengthId = Shader.PropertyToID("_RefractStrength");
        private static readonly int DarkenId = Shader.PropertyToID("_Darken");
        private static readonly int HaloDarkenId = Shader.PropertyToID("_HaloDarken");
        private static readonly int RimBoostId = Shader.PropertyToID("_RimBoost");
        private static readonly int BaseGlossId = Shader.PropertyToID("_BaseGloss");
        private static readonly int DropGlossId = Shader.PropertyToID("_DropGloss");
        private static readonly int GlossMaxId = Shader.PropertyToID("_GlossMax");

        // Stamp art loads once per session, not once per wound.
        // NOTE: the RVFX pack splatters were tried as underlay variants and
        // reverted — on the BODY the original pair (the user's blood_splash
        // picture + the generated art) reads better; the pack textures serve
        // the ground stains and the splash VFX instead.
        private static bool _stampTexturesLoaded;
        private static Texture2D? _texSplash;
        private static Texture2D? _texScratch;
        private static Texture2D? _texSplat;
        private static Texture2D? _texBandage;
        private static Texture2D? _texGauze;
        // Matching relief maps (RGB = encoded tangent normal, A = stamp
        // alpha): scratches groove IN, blood pools bead UP.
        private static Texture2D? _texSplashN;
        private static Texture2D? _texScratchN;
        private static Texture2D? _texSplatN;
        // LEGACY v3 bead-spray sheet — kept for future pox/insect-bite
        // visuals. Live sweat now uses the procedural SweatDropletSheet
        // (spec 40.8 v4: few large drops, three painted channels).
        private static Texture2D? _texSweatN;
        // Wet-core gloss shapes for the wound art (alpha = smoothness 0..1).
        private static Texture2D? _texScratchG;
        private static Texture2D? _texSplatG;
        // Spec 40.8-D v5: the wound over-art VARIANT table. Seed picks one so
        // repeated hits don't all look identical. Index-aligned: _woundGloss[i]
        // is the wet-core gloss for _woundOver[i]. All are BLOOD-ONLY art (no
        // baked skin/flesh) so any tan tint reads right. Extra gash shapes
        // (wound_gash_*) join the two originals (scratch claw + blood splat).
        private static readonly string[] WoundVariantNames =
        {
            "wound_scratch", "blood_splat",
            "wound_gash_slash", "wound_gash_streak", "wound_gash_smear",
            "wound_gash_fork", "wound_gash_torn",
        };
        private static Texture2D?[] _woundOver = System.Array.Empty<Texture2D?>();
        private static Texture2D?[] _woundGloss = System.Array.Empty<Texture2D?>();
        // Decodes the (possibly DXT5nm) authored skin normal into plain RGB
        // before stamps blend on top (NormalDecodeBlit.shader).
        private static Material? _normalDecode;
        // Stamps wound wet-gloss into the map's alpha (WoundGlossStamp.shader:
        // BlendOp Max, ColorMask A — overlaps keep the shiniest value).
        private static Material? _glossStamp;
        // Spec 40.8 v4: composites a droplet's refraction/darkening/rim into
        // the albedo and its smoothness into the gloss map (DropletStamp.shader).
        private static Material? _dropletStamp;
        private static bool _dropletShaderWarned;

        private SkinnedMeshRenderer? _body;
        private BodyBones? _bones;
        private Transform? _bodyRoot;
        private int _npcId;
        private float _height = 1.7f;
        private HashSet<int> _skinSlots = new();
        // Spec 40.8-G: editor-baked placement points — when present, wound/
        // droplet placement is a table lookup and BakeMesh never runs.
        private PaintPointMap? _map;

        private Material[]? _materials;      // per-NPC instances
        private Texture?[] _originalAlbedo = System.Array.Empty<Texture?>();
        private Texture?[] _originalNormal = System.Array.Empty<Texture?>();
        private RenderTexture?[] _slotRt = System.Array.Empty<RenderTexture?>();
        private RenderTexture?[] _slotRtNormal = System.Array.Empty<RenderTexture?>();
        // Spec 40.8 v4: the third painted channel — per-pixel smoothness.
        // Alpha carries ABSOLUTE values (URP Lit multiplies the map by the
        // _Smoothness scalar, so NpcActorView pins the scalar to 1 on slots
        // where this map is live — see SlotHasGlossMap).
        private RenderTexture?[] _slotRtGloss = System.Array.Empty<RenderTexture?>();
        private bool[] _glossLive = System.Array.Empty<bool>();
        // Smoothness of wet-but-undropped skin — the gloss map's base value,
        // fed by NpcActorView from the unified wetness pool.
        private float _wetSmoothness = 0.32f;

        private readonly Dictionary<string, Stamp> _stamps = new();
        private readonly Dictionary<string, float> _alpha = new(); // key -> current fade
        private readonly HashSet<string> _desired = new();
        private readonly List<string> _stale = new();
        private int _lastStateHash;

        // Spec 40.8-G repaint coalescing: Sync only marks the composite
        // dirty; the actual per-slot blit+stamps+mips pass runs at most once
        // per interval (combat reopens wounds every tick — one repaint
        // covers the whole burst, always painting the LATEST state).
        private bool _repaintDirty;
        private float _lastRepaintTime;
        private const float RepaintIntervalSeconds = 0.25f;

        // ---- raycast working set (lazy: built on the FIRST placement) ----
        // Triangle indices + per-triangle slot never change when a pose is
        // baked, so they are cached once; only vertex positions refresh.
        private Mesh? _bakedMesh;
        private readonly List<Vector3> _bakedVerts = new();
        private int[] _skinTriangles = System.Array.Empty<int>();
        private int[] _skinTriangleSlot = System.Array.Empty<int>(); // per tri
        private Vector2[] _uvs = System.Array.Empty<Vector2>();
        private Matrix4x4 _worldToLocal = Matrix4x4.identity;
        private float _localToWorldScale = 1f;

        public void Construct(SkinnedMeshRenderer body, IEnumerable<int> skinSlots,
            BodyBones bones, Transform bodyRoot, int npcId, string actorMesh = "")
        {
            enabled = false; // LateUpdate runs only while a repaint is pending
            _body = body;
            _bones = bones;
            _bodyRoot = bodyRoot;
            _npcId = npcId;
            _height = 1.7f * bodyRoot.lossyScale.y;
            _skinSlots = new HashSet<int>(skinSlots);
            // Spec 40.8-G: baked placement points (falls back to the legacy
            // BakeMesh path — with its combat-frame cost — when missing).
            if (!string.IsNullOrEmpty(actorMesh))
            {
                _map = PaintPointMap.Load($"skin_{actorMesh}",
                    body.sharedMesh != null ? body.sharedMesh.vertexCount : 0);
            }

            _materials = body.materials; // instantiate once, per NPC
            _originalAlbedo = new Texture?[_materials.Length];
            _originalNormal = new Texture?[_materials.Length];
            _slotRt = new RenderTexture?[_materials.Length];
            _slotRtNormal = new RenderTexture?[_materials.Length];
            _slotRtGloss = new RenderTexture?[_materials.Length];
            _glossLive = new bool[_materials.Length];
            for (var i = 0; i < _materials.Length; i++)
            {
                _originalAlbedo[i] = _materials[i] != null && _materials[i].HasProperty("_BaseMap")
                    ? _materials[i].GetTexture("_BaseMap")
                    : null;
                _originalNormal[i] = _materials[i] != null && _materials[i].HasProperty("_BumpMap")
                    ? _materials[i].GetTexture("_BumpMap")
                    : null;
            }
        }

        /// <summary>The renderer whose material slots this painter owns —
        /// NpcActorView matches it against its tint targets.</summary>
        public SkinnedMeshRenderer? Body => _body;

        /// <summary>Spec 40.8-G: load the stamp art behind the loading
        /// curtain. Lazily loading it on the FIRST wound cost a ~2.4 s
        /// File.Read burst mid-combat (slow external disk).</summary>
        public static void Prewarm() => EnsureStampTextures();

        /// <summary>True while a slot's material carries the painted gloss
        /// map. The map's alpha is ABSOLUTE smoothness, so the caller must
        /// pin the _Smoothness scalar/property-block to 1 on these slots
        /// (URP Lit multiplies map × scalar) — and back to its own wetness
        /// lerp everywhere else.</summary>
        public bool SlotHasGlossMap(int slot) =>
            slot >= 0 && slot < _glossLive.Length && _glossLive[slot];

        // Spec 40.8 v4.1: PATCHES per zone (each carries 4-9 shaded drops —
        // full coverage lands ~200 drops body-wide). Counts scale with the
        // wetness pool: a patch or two just past the threshold, the full
        // set near soaked — at which point she reads covered in beads.
        private static int MaxDropletsFor(string zone) => zone switch
        {
            "Head" => FaceDroplets,
            "Torso" => 8,
            "LegL" or "LegR" => 6,
            "Pelvis" => 4,
            _ => 4
        };

        private static int DropletCountFor(string zone, float sweat)
        {
            var max = MaxDropletsFor(zone);
            if (max == 0 || sweat < DropletWetnessThreshold)
            {
                return 0;
            }

            var t = Mathf.Clamp01((sweat - DropletWetnessThreshold) /
                                  (0.9f - DropletWetnessThreshold));
            return Mathf.Max(1, Mathf.RoundToInt(max * t));
        }

        /// <summary>
        /// wounds: (zone, seed, heal01) records; bandaged: zones under a leaf
        /// wrap; sweat01 + uncovered drive the painted water droplets (40.8
        /// v4); wetSmoothness is the caller's current wet-skin gloss — it
        /// becomes the gloss map's base so droplets sit ON the wet sheen.
        /// New wounds raycast-place once; heals repaint with lower alpha;
        /// fully healed marks vanish (composite rebuilt from the original).
        /// </summary>
        public void Sync(List<(string zone, int seed, float heal)> wounds, HashSet<string> bandaged,
            float sweat01 = 0f, HashSet<string>? uncovered = null, float wetSmoothness = 0.32f,
            HashSet<string>? gauzed = null)
        {
            if (_body == null || _materials == null)
            {
                return;
            }

            _wetSmoothness = Mathf.Clamp01(wetSmoothness);
            _desired.Clear();
            var stateHash = 17;
            var needsPlacement = false;

            // Water droplets bead up on EVERY zone once the wetness pool
            // clears the threshold — v4.1 drops the uncovered filter (sweat
            // soaks the whole body; covered zones are occluded by the
            // garment meshes anyway, and skin peeking through rips/gaps
            // should glisten too). Count grows with wetness, relief/gloss
            // fade with the same 0.1 buckets as the wound marks.
            var sweat = Mathf.Clamp01(sweat01);
            _ = uncovered; // kept for signature stability (wounds still use it upstream)
            if (sweat > DropletWetnessThreshold)
            {
                foreach (var zone in Zones.Keys)
                {
                    for (var i = 0; i < DropletCountFor(zone, sweat); i++)
                    {
                        var key = $"sw{zone}#{i}";
                        _desired.Add(key);
                        // Remapped fade: raw wetness left drops at 30-60%
                        // opacity for most of the sweaty range — ghosts. A
                        // drop that EXISTS should read near-full; it still
                        // dissolves through the buckets while drying.
                        _alpha[key] = Mathf.Clamp01((sweat - DropletWetnessThreshold) /
                                                    (0.6f - DropletWetnessThreshold));
                        stateHash = stateHash * 31 + key.GetHashCode();
                        if (!_stamps.ContainsKey(key))
                        {
                            needsPlacement = true;
                        }
                    }
                }

                stateHash = stateHash * 31 + (int)(sweat * 10f);
                // The gloss base tracks the wetness pool — repaint the map
                // when it crosses a bucket even if the drop set is unchanged.
                stateHash = stateHash * 31 + (int)(_wetSmoothness * 20f);
            }

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

            if (wounds.Count > 0)
            {
                // Wound gloss sits on the wet-skin base — repaint when the
                // wetness pool crosses a bucket even with no drops around.
                stateHash = stateHash * 31 + (int)(_wetSmoothness * 20f);
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

            // Spec 44: medkit gauze wraps — same as the leaf wrap but a plain
            // gauze stamp ("g" keys keep them distinct from the "b" leaf wraps).
            if (gauzed != null)
            {
                foreach (var zone in gauzed)
                {
                    var key = $"g{zone}";
                    _desired.Add(key);
                    _alpha[key] = 1f;
                    stateHash = stateHash * 31 + key.GetHashCode();
                    if (!_stamps.ContainsKey(key))
                    {
                        needsPlacement = true;
                    }
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

            // GPU-loss watchdog ("grey vinyl" bug): the painted skin lives in
            // RenderTextures whose contents the GPU can discard mid-session
            // (display sleep, fullscreen/resolution switch, device reset).
            // The materials keep sampling the dead RT, so every painted slot
            // renders as a uniform glossy-grey body until the next heal-bucket
            // repaint — minutes away. IsCreated() flips false on loss, and
            // binding the RT during a repaint re-creates it, so forcing a
            // repaint here fully restores the skin the same frame.
            if (stateHash == _lastStateHash && !needsPlacement && !AnyPaintRtLost())
            {
                return; // nothing changed — no repaint
            }

            _lastStateHash = stateHash;

            if (needsPlacement)
            {
                PlaceNewStamps(wounds, bandaged, sweat, uncovered, gauzed);
            }

            // Coalesced: mark dirty, LateUpdate paints at most once per
            // interval (spec 40.8-G).
            _repaintDirty = true;
            enabled = true;
        }

        private void LateUpdate()
        {
            if (!_repaintDirty)
            {
                enabled = false;
                return;
            }

            if (Time.unscaledTime - _lastRepaintTime < RepaintIntervalSeconds)
            {
                return; // next eligible frame paints the latest state
            }

            _repaintDirty = false;
            _lastRepaintTime = Time.unscaledTime;
            enabled = false;
            RepaintAll();
        }

        // ---- placement: molly's bake-and-raycast, collider-free ----

        private void PlaceNewStamps(List<(string zone, int seed, float heal)> wounds, HashSet<string> bandaged,
            float sweat01, HashSet<string>? uncovered, HashSet<string>? gauzed = null)
        {
            // Spec 40.8-G: with a baked point map every placement is a table
            // lookup — no pose bake, no triangle scans (the legacy path bakes
            // the skinned mesh, which was the top combat-frame CPU cost).
            if (_map == null && !BakePoseForRaycasts())
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

            if (gauzed != null)
            {
                foreach (var zone in gauzed)
                {
                    var key = $"g{zone}";
                    if (!_stamps.ContainsKey(key))
                    {
                        TryPlace(key, zone, zone.GetHashCode(), isBandage: true, isGauze: true);
                    }
                }
            }

            if (sweat01 > DropletWetnessThreshold)
            {
                foreach (var zone in Zones.Keys)
                {
                    for (var i = 0; i < DropletCountFor(zone, sweat01); i++)
                    {
                        var key = $"sw{zone}#{i}";
                        if (!_stamps.ContainsKey(key))
                        {
                            TryPlaceSweat(key, zone, zone.GetHashCode() * 31 + i * 977);
                        }
                    }
                }
            }
        }

        // Spec 40.8 v4.1: a PATCH of small shaded water drops per stamp.
        // (v4.0 tried one large drop per stamp — read as "one lonely drop";
        // v3's dense spray read as pox because its beads were naked relief.
        // These are dense AND fully shaded.) Same seeded surface placement
        // as a wound; the stamp paints THREE channels: dome relief into the
        // normal map, refraction/darkening/rim into the albedo, and near-1
        // smoothness into the gloss map.
        private void TryPlaceSweat(string key, string zoneName, int seed)
        {
            if (!Zones.TryGetValue(zoneName, out var zone))
            {
                PlaceTombstone(key, seed, isBandage: false);
                return;
            }

            // Spec 40.8-G fast path — see TryPlace.
            if (_map != null)
            {
                var mapState = (uint)(_npcId * 19349663 ^ seed) | 1u;
                var mapT = 0.15f + NextRand(ref mapState) * 0.7f;
                var mapAzimuth = NextRand(ref mapState) * Mathf.PI * 2f;
                var points = _map.PointsFor(zoneName);
                var point = _map.PointAt(points, mapT, mapAzimuth);
                if (points.Length == 0 || !point.Valid)
                {
                    PlaceTombstone(key, seed, isBandage: false);
                    return;
                }

                EnsureStampTextures();
                var mapTarget = (DropWorldSizeMin +
                                 NextRand(ref mapState) * (DropWorldSizeMax - DropWorldSizeMin)) *
                                (_height / 1.7f) *
                                (zoneName == "Head" ? HeadPatchScale : 1f);
                SizeFromDensity(point, mapTarget, out var mapSizeU, out var mapSizeV, minUv: 0.004f);
                mapSizeU = Mathf.Min(mapSizeU, MaxDropletUvSize);
                mapSizeV = Mathf.Min(mapSizeV, MaxDropletUvSize);
                var mapCell = PickDropletCell(zoneName, ref mapState);
                _stamps[key] = new Stamp
                {
                    Key = key,
                    Slot = point.Slot,
                    Uv = point.Uv,
                    Seed = seed,
                    UvSizeX = mapSizeU,
                    UvSizeY = mapSizeV,
                    OverNormal = SweatDropletSheet.Normal,
                    Effect = SweatDropletSheet.Effect,
                    CellRect = SweatDropletSheet.CellRect(mapCell),
                    IsDroplet = true,
                    IsBandage = false
                };
                return;
            }

            var boneA = _bones!.GetBone(zone.BoneA);
            if (boneA == null)
            {
                PlaceTombstone(key, seed, isBandage: false);
                return;
            }

            var boneB = string.IsNullOrEmpty(zone.BoneB) ? null : _bones.GetBone(zone.BoneB);
            var state = (uint)(_npcId * 19349663 ^ seed) | 1u;

            var axis = boneB != null
                ? boneB.position - boneA.position
                : _bodyRoot!.up * (_height * 0.1f);
            var axisDir = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.up;
            var side = Vector3.Cross(axisDir, _bodyRoot!.forward).normalized;
            if (side.sqrMagnitude < 0.01f)
            {
                side = Vector3.Cross(axisDir, Vector3.right).normalized;
            }

            var radius = zone.Radius * _height;
            var t = 0.15f + NextRand(ref state) * 0.7f;
            var azimuth = NextRand(ref state) * Mathf.PI * 2f;
            var radial = Quaternion.AngleAxis(azimuth * Mathf.Rad2Deg, axisDir) * side;
            var anchor = boneA.position + axis * t;
            var searchPoint = _worldToLocal.MultiplyPoint3x4(anchor + radial * radius);
            var triangle = ClosestSkinTriangle(searchPoint);
            if (triangle < 0)
            {
                PlaceTombstone(key, seed, isBandage: false);
                return;
            }

            var slot = _skinTriangleSlot[triangle];
            var i0 = _skinTriangles[triangle * 3];
            var i1 = _skinTriangles[triangle * 3 + 1];
            var i2 = _skinTriangles[triangle * 3 + 2];
            var uv = (_uvs[i0] + _uvs[i1] + _uvs[i2]) / 3f;
            uv.x = Mathf.Repeat(uv.x, 1f);
            uv.y = Mathf.Repeat(uv.y, 1f);
            EnsureStampTextures();
            // A PATCH of 4-9 shaded drops, world-true 5-7.5 cm across
            // (drops inside ~10-18 mm — still exaggerated vs real 2-4 mm
            // sweat, or they die in camera distance and mips).
            var targetWorld = (DropWorldSizeMin +
                               NextRand(ref state) * (DropWorldSizeMax - DropWorldSizeMin)) *
                              (_height / 1.7f) *
                              (zoneName == "Head" ? HeadPatchScale : 1f);
            // Droplets bypass the wound floor (0.02 UV would already be 4x a
            // drop on a sparse torso tile) but hard-cap on the dense face tile.
            StampSizeFor(triangle, targetWorld, out var sizeU, out var sizeV, minUv: 0.004f);
            sizeU = Mathf.Min(sizeU, MaxDropletUvSize);
            sizeV = Mathf.Min(sizeV, MaxDropletUvSize);
            var cell = PickDropletCell(zoneName, ref state);
            _stamps[key] = new Stamp
            {
                Key = key,
                Slot = slot,
                Uv = uv,
                Seed = seed,
                UvSizeX = sizeU,
                UvSizeY = sizeV,
                OverNormal = SweatDropletSheet.Normal,
                Effect = SweatDropletSheet.Effect,
                CellRect = SweatDropletSheet.CellRect(cell),
                IsDroplet = true,
                IsBandage = false
            };
        }

        // v4.2: every cell is round beads (run-trail cells were cut — the
        // body animates, texture "down" points anywhere), so the pick is a
        // plain seeded roll.
        private static int PickDropletCell(string zone, ref uint state)
        {
            _ = zone;
            return (int)(NextRand(ref state) * SweatDropletSheet.Cells) % SweatDropletSheet.Cells;
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
            _localToWorldScale = scaleBakedIn ? 1f : Mathf.Max(0.0001f, lossy.y);
            return true;
        }

        // Spec 40.8-G: StampSizeFor's twin for baked points — the map stores
        // the bind-pose UV density (rig-scale metres per UV unit), the actor
        // height scales it to the live world.
        private void SizeFromDensity(in PaintPointMap.Point point, float targetWorld,
            out float sizeU, out float sizeV, float minUv = 0.02f)
        {
            var heightScale = Mathf.Max(0.0001f, _height / 1.7f);
            var worldPerU = Mathf.Max(0.0001f, point.BindPerU * heightScale);
            var worldPerV = Mathf.Max(0.0001f, point.BindPerV * heightScale);
            sizeU = Mathf.Clamp(targetWorld / worldPerU, minUv, 0.95f);
            sizeV = Mathf.Clamp(targetWorld / worldPerV, minUv, 0.95f);
        }

        // World-size-true stamp: measures the triangle's UV density along U
        // and V (world metres per UV unit) and returns per-axis UV sizes so
        // the painted stamp is SQUARE and `targetWorld` metres wide on the
        // body no matter how the tile is unwrapped.
        private void StampSizeFor(int triangle, float targetWorld, out float sizeU, out float sizeV,
            float minUv = 0.02f)
        {
            var i0 = _skinTriangles[triangle * 3];
            var i1 = _skinTriangles[triangle * 3 + 1];
            var i2 = _skinTriangles[triangle * 3 + 2];
            var p1 = _bakedVerts[i1] - _bakedVerts[i0];
            var p2 = _bakedVerts[i2] - _bakedVerts[i0];
            var t1 = _uvs[i1] - _uvs[i0];
            var t2 = _uvs[i2] - _uvs[i0];
            var det = t1.x * t2.y - t1.y * t2.x;
            if (Mathf.Abs(det) < 1e-8f)
            {
                sizeU = sizeV = 0.25f; // degenerate UVs: fall back
                return;
            }

            var inv = 1f / det;
            var dPdu = (p1 * t2.y - p2 * t1.y) * inv;
            var dPdv = (p2 * t1.x - p1 * t2.x) * inv;
            var worldPerU = Mathf.Max(0.0001f, dPdu.magnitude * _localToWorldScale);
            var worldPerV = Mathf.Max(0.0001f, dPdv.magnitude * _localToWorldScale);
            sizeU = Mathf.Clamp(targetWorld / worldPerU, minUv, 0.95f);
            sizeV = Mathf.Clamp(targetWorld / worldPerV, minUv, 0.95f);
        }

        private void TryPlace(string key, string zoneName, int seed, bool isBandage, bool isGauze = false)
        {
            if (!Zones.TryGetValue(zoneName, out var zone))
            {
                PlaceTombstone(key, seed, isBandage, isGauze);
                return;
            }

            // Spec 40.8-G fast path: the seeded (t, azimuth) rolls stay
            // identical to the legacy raycast placement — they just index
            // the baked grid instead of aiming a ray at the live pose.
            if (_map != null)
            {
                var mapState = (uint)(_npcId * 73856093 ^ seed) | 1u;
                var mapT = 0.25f + NextRand(ref mapState) * 0.5f;
                var mapAzimuth = NextRand(ref mapState) * Mathf.PI * 2f;
                var points = _map.PointsFor(zoneName);
                var point = _map.PointAt(points, mapT, mapAzimuth);
                if (points.Length == 0 || !point.Valid)
                {
                    PlaceTombstone(key, seed, isBandage, isGauze);
                    return;
                }

                EnsureStampTextures();
                var mapTarget = (isBandage ? 0.14f : 0.07f + NextRand(ref mapState) * 0.04f)
                                * (_height / 1.7f);
                SizeFromDensity(point, mapTarget, out var mapSizeU, out var mapSizeV);
                var (mover, mgloss) = WoundVariant(seed);
                _stamps[key] = new Stamp
                {
                    Key = key,
                    Slot = point.Slot,
                    Uv = point.Uv,
                    Seed = seed,
                    UvSizeX = mapSizeU,
                    UvSizeY = mapSizeV,
                    Under = isBandage ? null : _texSplash,
                    Over = isGauze ? _texGauze : (isBandage ? _texBandage : mover),
                    OverGloss = isBandage ? null : mgloss,
                    IsBandage = isBandage,
                    IsGauze = isGauze
                };
                return;
            }

            var boneA = _bones!.GetBone(zone.BoneA);
            if (boneA == null)
            {
                // Unresolvable zone: record a dead stamp so the placement
                // isn't retried (and logged) on every sync forever.
                Debug.LogWarning($"[SkinPaint] npc{_npcId} {key}: bone '{zone.BoneA}' not found");
                PlaceTombstone(key, seed, isBandage, isGauze);
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
                PlaceTombstone(key, seed, isBandage, isGauze);
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
            // World-metre targets (at full 1.7 m rig scale): a wound art
            // sheet spans ~9 cm, a bandage wrap ~14 cm.
            var targetWorld = (isBandage ? 0.14f : 0.07f + NextRand(ref state) * 0.04f)
                              * (_height / 1.7f);
            StampSizeFor(triangle, targetWorld, out var sizeU, out var sizeV);
            var (wover, wgloss) = WoundVariant(seed);
            _stamps[key] = new Stamp
            {
                Key = key,
                Slot = slot,
                Uv = uv,
                Seed = seed,
                UvSizeX = sizeU,
                UvSizeY = sizeV,
                Under = isBandage ? null : _texSplash,
                Over = isGauze ? _texGauze : (isBandage ? _texBandage : wover),
                // NO wound relief (spec 40.8-D v5 revision): stamps often
                // straddle a UV seam and the normal discontinuity flared as
                // ugly lit ridges there; the depth gain never justified it.
                // Wound volume = albedo darkness + wet gloss. Droplets keep
                // their dome relief (single small stamp, seams rare).
                OverGloss = isBandage ? null : wgloss,
                IsBandage = isBandage,
                IsGauze = isGauze
            };
        }

        // A dead stamp record: paints nothing (slot -1 never matches) but
        // stops Sync from re-attempting the same placement every frame.
        private void PlaceTombstone(string key, int seed, bool isBandage, bool isGauze = false)
        {
            _stamps[key] = new Stamp { Key = key, Slot = -1, Seed = seed, IsBandage = isBandage, IsGauze = isGauze };
        }

        // Fill in any art a stamp missed because its texture asset wasn't
        // imported yet when the stamp was placed (sweat stamps are
        // normal-only by design — key prefix "sw").
        private static void RefreshStampArt(Stamp stamp)
        {
            if (stamp.Slot < 0)
            {
                return; // tombstone
            }

            EnsureStampTextures();
            if (stamp.Key.StartsWith("sw"))
            {
                // Droplet art is generated, not imported — heal references a
                // domain reload may have severed.
                stamp.OverNormal ??= SweatDropletSheet.Normal;
                stamp.Effect ??= SweatDropletSheet.Effect;
                return;
            }

            if (stamp.IsBandage)
            {
                stamp.Over ??= stamp.IsGauze ? _texGauze : _texBandage;
                return;
            }

            stamp.Under ??= _texSplash;
            var (wover, wgloss) = WoundVariant(stamp.Seed);
            stamp.Over ??= wover;
            stamp.OverGloss ??= wgloss;
        }

        // Deterministic per-seed wound art: the same seed always resolves to
        // the same shape, so a save-replay and a late RefreshStampArt agree.
        // Skips variants whose PNG hasn't imported yet (partial import paints
        // fewer shapes, never crashes); index-aligned over+gloss stay paired.
        private static (Texture? over, Texture? gloss) WoundVariant(int seed)
        {
            var n = _woundOver.Length;
            if (n == 0)
            {
                return (_texScratch, _texScratchG);
            }

            var start = (int)((uint)seed % (uint)n);
            for (var k = 0; k < n; k++)
            {
                var i = (start + k) % n;
                if (_woundOver[i] != null)
                {
                    return (_woundOver[i], _woundGloss[i]);
                }
            }

            return (_texScratch, _texScratchG);
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

        // Spec 44: procedural medkit gauze wrap — a pale off-white cloth pad
        // with a CRISP woven mesh (warp + weft threads with small holes) and
        // two crossed fabric bands (no green, no leaves; this is the pre-made
        // bandage, not gathered plantain). Baked at 1024 so the weave stays
        // sharp when stamped onto the 2048 skin tile. Mirrors SkinDecals'
        // GauzePixel so the paint and projector paths read the same.
        private static Texture2D MakeGauzeTexture()
        {
            const int size = 1024;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 8,
                name = "gauze_wrap_procedural"
            };
            var px = new Color[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size - 0.5f;
                    var v = (y + 0.5f) / size - 0.5f;
                    px[y * size + x] = GauzeSample(u, v);
                }
            }

            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        // Shared crisp-gauze pixel (u,v in -0.5..0.5). Warp/weft thread ridges
        // with sharp gaps read as real woven mesh; the holes drop alpha so a
        // hint of skin shows through, and two crossed wrap bands sit on top.
        // PUBLIC so the editor baker (HexLive ▸ Paint Maps ▸ Bake Gauze PNG)
        // can render the exact same texture into Resources/HexLive/Decals/
        // gauze_wrap.png — the 1024² per-pixel runtime bake was a ~1 s hitch
        // on the first bandage of a session (2026-07-19 deep capture).
        public static Color GauzeSample(float u, float v)
        {
            var r = Mathf.Sqrt(u * u + v * v);
            var pad = Mathf.Clamp01((0.40f - r) / 0.05f);

            const float freq = 30f; // ~30 threads across the pad
            var su = Mathf.Abs(Mathf.Repeat(u * freq, 1f) - 0.5f) * 2f; // 0=thread,1=gap
            var sv = Mathf.Abs(Mathf.Repeat(v * freq, 1f) - 0.5f) * 2f;
            var warp = 1f - Mathf.SmoothStep(0.55f, 0.9f, su); // vertical threads
            var weft = 1f - Mathf.SmoothStep(0.55f, 0.9f, sv); // horizontal threads
            var thread = Mathf.Max(warp, weft);
            var hole = (1f - warp) * (1f - weft); // both in a gap -> mesh hole

            var cream = new Color(0.90f, 0.88f, 0.82f);
            var ridge = new Color(0.98f, 0.97f, 0.93f);
            var gap = new Color(0.72f, 0.70f, 0.64f);
            var col = Color.Lerp(cream, ridge, thread * 0.8f);
            col = Color.Lerp(col, gap, hole * 0.6f);

            // Two crossed wrap bands (sharper edges than the pad).
            var band1 = Mathf.Clamp01((0.05f - Mathf.Abs(u + v * 0.3f)) / 0.012f);
            var band2 = Mathf.Clamp01((0.05f - Mathf.Abs(v - u * 0.3f)) / 0.012f);
            var band = Mathf.Max(band1, band2) * pad;
            col = Color.Lerp(col, new Color(0.80f, 0.76f, 0.68f), band * 0.5f);

            var alpha = pad * Mathf.Lerp(0.97f, 0.6f, hole); // holes let skin peek
            return new Color(col.r, col.g, col.b, alpha);
        }

        // No-domain-reload editor runs keep statics between plays: a load
        // that ran BEFORE an asset was imported would cache null forever
        // (wounds silently lost their relief this way). Reset on every play.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _stampTexturesLoaded = false;
            _texSplash = _texScratch = _texSplat = _texBandage = _texGauze = null;
            _texSplashN = _texScratchN = _texSplatN = _texSweatN = null;
            _texScratchG = _texSplatG = null;
            _woundOver = System.Array.Empty<Texture2D?>();
            _woundGloss = System.Array.Empty<Texture2D?>();
            _normalDecode = null;
            _dropletStamp = null;
            _glossStamp = null;
            _dropletShaderWarned = false;
        }

        private static void EnsureStampTextures()
        {
            // Re-check while anything is missing (asset may import mid-session).
            if (_stampTexturesLoaded && _texScratchN != null && _texSplatN != null &&
                _texSplashN != null && _texSweatN != null && _dropletStamp != null &&
                _texScratchG != null && _texSplatG != null && _glossStamp != null)
            {
                return;
            }

            _stampTexturesLoaded = true;
            _texSplash = Resources.Load<Texture2D>("HexLive/Decals/blood_splash");
            _texScratch = Resources.Load<Texture2D>("HexLive/Decals/wound_scratch");
            _texSplat = Resources.Load<Texture2D>("HexLive/Decals/blood_splat");
            _texBandage = Resources.Load<Texture2D>("HexLive/Decals/bandage_wrap");
            // Spec 44: medkit gauze — prefer a gauze_wrap.png if present, else
            // bake the procedural cloth wrap so the medkit dressing is visible
            // without any imported asset.
            _texGauze = Resources.Load<Texture2D>("HexLive/Decals/gauze_wrap") ?? MakeGauzeTexture();
            _texSplashN = Resources.Load<Texture2D>("HexLive/Decals/blood_splash_n");
            _texScratchN = Resources.Load<Texture2D>("HexLive/Decals/wound_scratch_n");
            _texSplatN = Resources.Load<Texture2D>("HexLive/Decals/blood_splat_n");
            _texSweatN = Resources.Load<Texture2D>("HexLive/Decals/sweat_drops_n");
            _texScratchG = Resources.Load<Texture2D>("HexLive/Decals/wound_scratch_g");
            _texSplatG = Resources.Load<Texture2D>("HexLive/Decals/blood_splat_g");

            // Load the wound over-art variant table (over + matching gloss).
            // A missing variant PNG leaves nulls — WoundVariant() skips them,
            // so a partial import just paints fewer shapes, never crashes.
            _woundOver = new Texture2D?[WoundVariantNames.Length];
            _woundGloss = new Texture2D?[WoundVariantNames.Length];
            for (var i = 0; i < WoundVariantNames.Length; i++)
            {
                _woundOver[i] = Resources.Load<Texture2D>($"HexLive/Decals/{WoundVariantNames[i]}");
                _woundGloss[i] = Resources.Load<Texture2D>($"HexLive/Decals/{WoundVariantNames[i]}_g");
            }

            var decodeShader = Shader.Find("Hidden/HexLive/NormalDecodeBlit");
            _normalDecode = decodeShader != null ? new Material(decodeShader) : null;
            var dropletShader = Shader.Find("Hidden/HexLive/DropletStamp");
            _dropletStamp = dropletShader != null ? new Material(dropletShader) : null;
            var glossShader = Shader.Find("Hidden/HexLive/WoundGlossStamp");
            _glossStamp = glossShader != null ? new Material(glossShader) : null;
        }

        // ---- painting ----

        // True when any live paint target lost its hardware resource (the GPU
        // discarded it) — the cue for Sync's forced-repaint watchdog.
        private bool AnyPaintRtLost()
        {
            foreach (var rt in _slotRt)
            {
                if (rt != null && !rt.IsCreated())
                {
                    return true;
                }
            }

            foreach (var rt in _slotRtNormal)
            {
                if (rt != null && !rt.IsCreated())
                {
                    return true;
                }
            }

            foreach (var rt in _slotRtGloss)
            {
                if (rt != null && !rt.IsCreated())
                {
                    return true;
                }
            }

            return false;
        }

        private void RepaintAll()
        {
            if (_materials == null)
            {
                return;
            }

            // Stamps placed while an art asset hadn't imported yet hold null
            // textures — heal them now instead of painting nothing forever.
            foreach (var stamp in _stamps.Values)
            {
                RefreshStampArt(stamp);
            }

            // Which slots carry stamps now — PER CHANNEL: a fully healed slot
            // restores each original independently. Droplets touch all three
            // channels: refraction/rim bake into the albedo (the price of the
            // lens look — the slot swaps to the paint target), relief into
            // the normal map, near-1 smoothness into the gloss map.
            for (var slot = 0; slot < _materials.Length; slot++)
            {
                var hasAlbedo = false;
                var hasNormal = false;
                var hasDroplet = false;
                var hasGloss = false;
                foreach (var stamp in _stamps.Values)
                {
                    if (stamp.Slot != slot)
                    {
                        continue;
                    }

                    var droplet = stamp.IsDroplet && stamp.Effect != null;
                    hasAlbedo |= stamp.Under != null || stamp.Over != null || droplet;
                    hasNormal |= stamp.UnderNormal != null || stamp.OverNormal != null;
                    hasDroplet |= droplet;
                    // Wounds carry their own wet-gloss stamp (spec 40.8-D v5).
                    hasGloss |= droplet || stamp.OverGloss != null;
                }

                if (hasAlbedo)
                {
                    RepaintSlot(slot);
                }
                else if (_slotRt[slot] != null)
                {
                    _materials[slot].SetTexture("_BaseMap", _originalAlbedo[slot]);
                }

                if (hasNormal)
                {
                    RepaintSlotNormal(slot);
                }
                else if (_slotRtNormal[slot] != null)
                {
                    RestoreSlotNormal(slot);
                }

                if (hasDroplet && _dropletStamp == null && !_dropletShaderWarned)
                {
                    // Droplets without their shader = faint normal bumps only.
                    _dropletShaderWarned = true;
                    Debug.LogWarning($"[SkinPaint] npc{_npcId}: DropletStamp shader missing — " +
                                     "droplet albedo/gloss muted (normal relief only)");
                }

                if ((hasDroplet && _dropletStamp != null) || (hasGloss && _glossStamp != null))
                {
                    RepaintSlotGloss(slot);
                }
                else if (_glossLive[slot])
                {
                    RestoreSlotGloss(slot);
                }
            }
        }

        // Uniforms for one droplet's DrawTexture passes. _SlotRect maps the
        // stamp's footprint back to slot UVs so the shader can offset-sample
        // the ORIGINAL albedo under the drop (the fake refraction).
        private void ConfigureDropletMaterial(Stamp stamp, float fade, int slot)
        {
            var mat = _dropletStamp!;
            mat.SetTexture(UnderTexId, _originalAlbedo[slot]);
            mat.SetVector(SlotRectId, new Vector4(
                stamp.Uv.x - stamp.UvSizeX * 0.5f, stamp.Uv.y - stamp.UvSizeY * 0.5f,
                stamp.UvSizeX, stamp.UvSizeY));
            mat.SetVector(CellRectId, new Vector4(
                stamp.CellRect.x, stamp.CellRect.y, stamp.CellRect.width, stamp.CellRect.height));
            mat.SetFloat(FadeId, fade);
            mat.SetFloat(RefractStrengthId, RefractStrength);
            mat.SetFloat(DarkenId, DropDarken);
            mat.SetFloat(HaloDarkenId, HaloDarken);
            mat.SetFloat(RimBoostId, RimBoost);
            mat.SetFloat(BaseGlossId, _wetSmoothness);
            mat.SetFloat(DropGlossId, DropGloss);
        }

        private Rect DropletRect(Stamp stamp) => new(
            stamp.Uv.x - stamp.UvSizeX * 0.5f, 1f - stamp.Uv.y - stamp.UvSizeY * 0.5f,
            stamp.UvSizeX, stamp.UvSizeY);

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
                    // 0.5: at 0.8 the splash's pale-pink wash read as skin
                    // DISCOLORATION on tanned bodies — a faint halo only.
                    var cx = stamp.Uv.x;
                    var cy = 1f - stamp.Uv.y;
                    if (stamp.Under != null)
                    {
                        var sx = stamp.UvSizeX * 1.6f;
                        var sy = stamp.UvSizeY * 1.6f;
                        Graphics.DrawTexture(new Rect(cx - sx * 0.5f, cy - sy * 0.5f, sx, sy),
                            stamp.Under, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(alpha * 0.5f));
                    }

                    if (stamp.Over != null)
                    {
                        Graphics.DrawTexture(new Rect(cx - stamp.UvSizeX * 0.5f, cy - stamp.UvSizeY * 0.5f,
                                stamp.UvSizeX, stamp.UvSizeY),
                            stamp.Over, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(alpha));
                    }
                }

                // Water droplets land ON TOP of wounds/bandages: the damp
                // halo multiplies whatever is painted (pass 0), the drop
                // interior becomes the refracted original albedo (pass 1).
                if (_dropletStamp != null)
                {
                    foreach (var stamp in _stamps.Values)
                    {
                        if (stamp.Slot != slot || !stamp.IsDroplet || stamp.Effect == null ||
                            !_alpha.TryGetValue(stamp.Key, out var alpha) || alpha <= 0.01f)
                        {
                            continue;
                        }

                        ConfigureDropletMaterial(stamp, alpha, slot);
                        var rect = DropletRect(stamp);
                        Graphics.DrawTexture(rect, stamp.Effect, stamp.CellRect,
                            0, 0, 0, 0, Color.white, _dropletStamp, 0);
                        Graphics.DrawTexture(rect, stamp.Effect, stamp.CellRect,
                            0, 0, 0, 0, Color.white, _dropletStamp, 1);
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

        // Spec 40.8 v4: the gloss map. Cleared to the CURRENT wet-skin base
        // smoothness, droplets overwrite toward DropGloss (BlendOp Max in the
        // shader keeps overlaps additive-safe). Alpha is ABSOLUTE smoothness
        // and R is metallic 0 — URP Lit multiplies alpha by the _Smoothness
        // scalar, which NpcActorView pins to 1 while this map is live.
        private void RepaintSlotGloss(int slot)
        {
            // Either stamp material serves: droplets need _dropletStamp,
            // wound wet-gloss needs _glossStamp — per-stamp guards below.
            if (_materials == null || _materials[slot] == null ||
                (_dropletStamp == null && _glossStamp == null) ||
                !_materials[slot].HasProperty("_MetallicGlossMap"))
            {
                return;
            }

            var rt = _slotRtGloss[slot];
            if (rt == null)
            {
                // Linear: the alpha is smoothness DATA, not colour.
                rt = new RenderTexture(GlossRtSize, GlossRtSize, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear)
                {
                    name = $"SkinPaintG_{_npcId}_{slot}",
                    useMipMap = true,
                    autoGenerateMips = false
                };
                rt.Create();
                _slotRtGloss[slot] = rt;
            }

            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(false, true, new Color(0f, 0f, 0f, _wetSmoothness));
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, 1f, 1f, 0f);

                foreach (var stamp in _stamps.Values)
                {
                    if (stamp.Slot != slot ||
                        !_alpha.TryGetValue(stamp.Key, out var alpha) || alpha <= 0.01f)
                    {
                        continue;
                    }

                    if (stamp.IsDroplet)
                    {
                        if (stamp.Effect == null || _dropletStamp == null)
                        {
                            continue;
                        }

                        ConfigureDropletMaterial(stamp, alpha, slot);
                        Graphics.DrawTexture(DropletRect(stamp), stamp.Effect, stamp.CellRect,
                            0, 0, 0, 0, Color.white, _dropletStamp, 2);
                        continue;
                    }

                    if (stamp.OverGloss == null || _glossStamp == null)
                    {
                        continue;
                    }

                    // Only the detailed over-art glistens — the splash
                    // underlay stays dry; a matte halo around a wet core is
                    // what makes the cut read DEEP. Healing fades the gloss
                    // until it sinks below the base and BlendOp Max drops it.
                    _glossStamp.SetFloat(GlossMaxId, WoundWetGloss);
                    _glossStamp.SetFloat(FadeId, alpha);
                    Graphics.DrawTexture(new Rect(stamp.Uv.x - stamp.UvSizeX * 0.5f,
                            1f - stamp.Uv.y - stamp.UvSizeY * 0.5f, stamp.UvSizeX, stamp.UvSizeY),
                        stamp.OverGloss, new Rect(0f, 0f, 1f, 1f),
                        0, 0, 0, 0, Color.white, _glossStamp);
                }

                GL.PopMatrix();
                RenderTexture.active = previous;
                rt.GenerateMips();
                _materials[slot].SetTexture("_MetallicGlossMap", rt);
                _materials[slot].EnableKeyword("_METALLICSPECGLOSSMAP");
                _glossLive[slot] = true;
            }
            catch (System.Exception e)
            {
                RenderTexture.active = previous;
                RestoreSlotGloss(slot);
                Debug.LogWarning($"[SkinPaint] npc{_npcId} slot={slot}: gloss repaint failed — {e.Message}");
            }
        }

        private void RestoreSlotGloss(int slot)
        {
            if (_materials == null || _materials[slot] == null ||
                !_materials[slot].HasProperty("_MetallicGlossMap"))
            {
                return;
            }

            // Back to the scalar-only path (the authored materials ship with
            // no gloss map at all) — NpcActorView resumes its wetness lerp.
            _materials[slot].SetTexture("_MetallicGlossMap", null);
            _materials[slot].DisableKeyword("_METALLICSPECGLOSSMAP");
            _glossLive[slot] = false;
        }

        // The same composite for the skin NORMAL map: the authored normal is
        // decoded to plain RGB (NormalDecodeBlit handles DXT5nm), then each
        // stamp's relief blends on top by its alpha — scratches carve grooves,
        // blood beads up, and healing fades the relief with the color. The
        // RGB encoding (x in R, y in G, A = 1) survives URP's
        // UnpackNormalmapRGorAG (a·r = x when a = 1).
        private void RepaintSlotNormal(int slot)
        {
            if (_materials == null || _materials[slot] == null ||
                !_materials[slot].HasProperty("_BumpMap"))
            {
                return;
            }

            var source = _originalNormal[slot];
            var rt = _slotRtNormal[slot];
            if (rt == null)
            {
                var w = source != null ? Mathf.Min(source.width, MaxRenderTextureSize) : 1024;
                var h = source != null ? Mathf.Min(source.height, MaxRenderTextureSize) : 1024;
                // Linear: normals are vector data, sRGB conversion would bend
                // them sideways.
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear)
                {
                    name = $"SkinPaintN_{_npcId}_{slot}",
                    useMipMap = true,
                    autoGenerateMips = false
                };
                rt.Create();
                _slotRtNormal[slot] = rt;
            }

            var previous = RenderTexture.active;
            try
            {
                if (source != null && _normalDecode != null)
                {
                    Graphics.Blit(source, rt, _normalDecode);
                }
                else
                {
                    // No authored normal: start from a flat surface.
                    RenderTexture.active = rt;
                    GL.Clear(false, true, new Color(0.5f, 0.5f, 1f, 1f));
                }

                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, 1f, 1f, 0f);

                foreach (var stamp in _stamps.Values)
                {
                    if (stamp.Slot != slot || !_alpha.TryGetValue(stamp.Key, out var alpha) || alpha <= 0.01f)
                    {
                        continue;
                    }

                    var cx = stamp.Uv.x;
                    var cy = 1f - stamp.Uv.y;
                    if (stamp.UnderNormal != null)
                    {
                        var sx = stamp.UvSizeX * 1.6f;
                        var sy = stamp.UvSizeY * 1.6f;
                        Graphics.DrawTexture(new Rect(cx - sx * 0.5f, cy - sy * 0.5f, sx, sy),
                            stamp.UnderNormal, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(alpha * 0.8f));
                    }

                    if (stamp.OverNormal != null)
                    {
                        // CellRect: droplets pick one drop out of the sheet's
                        // atlas; wound art keeps the default full rect.
                        Graphics.DrawTexture(new Rect(cx - stamp.UvSizeX * 0.5f, cy - stamp.UvSizeY * 0.5f,
                                stamp.UvSizeX, stamp.UvSizeY),
                            stamp.OverNormal, stamp.CellRect, 0, 0, 0, 0, StampTint(alpha));
                    }
                }

                GL.PopMatrix();
                RenderTexture.active = previous;
                rt.GenerateMips();
                _materials[slot].SetTexture("_BumpMap", rt);
                // Slots whose material had no normal map need the keyword to
                // start sampling one.
                _materials[slot].EnableKeyword("_NORMALMAP");
            }
            catch (System.Exception e)
            {
                RenderTexture.active = previous;
                RestoreSlotNormal(slot);
                Debug.LogWarning($"[SkinPaint] npc{_npcId} slot={slot}: normal repaint failed — {e.Message}");
            }
        }

        private void RestoreSlotNormal(int slot)
        {
            if (_materials == null || _materials[slot] == null ||
                !_materials[slot].HasProperty("_BumpMap"))
            {
                return;
            }

            _materials[slot].SetTexture("_BumpMap", _originalNormal[slot]);
            if (_originalNormal[slot] == null)
            {
                // We enabled the keyword ourselves — a null bump with
                // _NORMALMAP on samples garbage.
                _materials[slot].DisableKeyword("_NORMALMAP");
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

            foreach (var rt in _slotRtNormal)
            {
                if (rt != null)
                {
                    rt.Release();
                    Destroy(rt);
                }
            }

            foreach (var rt in _slotRtGloss)
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
