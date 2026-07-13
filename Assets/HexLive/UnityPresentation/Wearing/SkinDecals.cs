#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Spec 40.8/40.6: skin decals via URP <see cref="DecalProjector"/> — each
    /// mark is a projector parented to the zone's bone, aimed into the limb, so
    /// the texture is PROJECTED onto the skin mesh and hugs its curvature (the
    /// old camera-facing quads floated off the body). Wound scratches/blood
    /// splatter per hurt zone, dirt smudges as hygiene drops, sweat droplet
    /// clusters in the heat. Placement is deterministic from npcId+zone+slot, so
    /// decals persist frame to frame. SKIN ONLY: they spawn solely on zones the
    /// simulation reports as uncovered; worn garments occlude covered skin.
    /// Requires the Decal renderer feature on the URP renderer (added to
    /// PC_Renderer/Mobile_Renderer).
    /// </summary>
    public sealed class SkinDecals : MonoBehaviour
    {
        private enum DecalType { Scratch, Blood, Dirt, Sweat, Bandage, Gauze }

        private sealed class Zone
        {
            public string BoneA = string.Empty;   // segment start (owner bone)
            public string BoneB = string.Empty;   // segment end ("" = use body up)
            public float Radius;                  // limb radius, fraction of height
        }

        // Genesis3 rig segments per sim body zone; radius ~ real limb girth.
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

        // Dirt coverage order: interleaved across ALL zones from the start (so
        // even light grime dusts legs+arms+torso, not just legs), then repeated
        // rounds — at full filth every zone carries several smudges: legs 5,
        // arms 4+4, torso (chest+belly) 6, pelvis (butt) 4, face 3 = 26 decals.
        // Tuned down twice (52 → 30 → 24; both louder passes read as
        // paint-bombed): mostly limbs/torso, only a couple on the head so
        // the face and hair don't drown in grain. The granular sheet itself
        // is dark brown and translucent (alpha ≤ 0.45) with an early radial
        // fade — full filth reads as a quiet dusty crust with no hard seams
        // at the projector edges.
        private static readonly string[] DirtSpread =
        {
            "LegL", "LegR", "ArmL", "ArmR", "Torso", "Pelvis",
            "LegL", "LegR", "ArmL", "ArmR", "Torso", "Pelvis",
            "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR",
            "Torso", "LegL", "ArmR", "Head",
            "LegR", "Head"
        };

        // Sweat shows where skin glistens first: face, chest, then arms.
        // 8 → 13 patches: the droplet sheet is all small beads now, so full
        // heat covers the body in many little bubbles instead of a few blots.
        private static readonly string[] SweatSpread =
            { "Head", "Head", "Torso", "Torso", "ArmL", "ArmR", "Head", "Torso",
              "Torso", "Pelvis", "ArmL", "ArmR", "Torso" };

        private BodyBones? _bones;
        private Transform? _bodyRoot;
        private int _npcId;
        private float _height = 1.7f;

        // Rendering-layer bit reserved for skin: projectors only affect
        // renderers carrying it, so decals never land on terrain, clothing or
        // props (default mask is bit 0 only). Requires decalLayers on in the
        // URP Decal renderer feature.
        private const uint SkinRenderingLayer = 1u << 1;

        // Rain droplets also land on garments (Wear opts its renderers into
        // this bit); sweat/wounds/dirt stay skin-only.
        private const uint ClothRenderingLayer = Wear.ClothDecalLayer;

        // Rain sprays the whole body top-down — every zone, covered or not
        // (droplets on covered zones land on the garment above the skin).
        private static readonly string[] RainSpread =
            { "Head", "Torso", "ArmL", "ArmR", "LegL", "LegR", "Pelvis", "Torso", "Head", "ArmL", "ArmR", "Torso" };

        private readonly Dictionary<string, GameObject> _decals = new();
        private readonly HashSet<string> _desired = new();
        private readonly List<string> _stale = new();
        // Sweat is 3D droplets (tiny glossy blobs on the skin), not projections.
        private readonly List<DecalProjector> _sweat = new();

        public void Construct(BodyBones bones, Transform bodyRoot, int npcId,
            IReadOnlyList<SkinnedMeshRenderer> skinRenderers)
        {
            _bones = bones;
            _bodyRoot = bodyRoot;
            _npcId = npcId;
            _height = 1.7f * bodyRoot.lossyScale.y;

            // Only the bare-skin renderers opt into receiving skin decals.
            foreach (var renderer in skinRenderers)
            {
                if (renderer != null)
                {
                    renderer.renderingLayerMask |= SkinRenderingLayer;
                }
            }
        }

        /// <summary>
        /// Re-derives the decal set from the tick snapshot. wounds: sim wound
        /// records ("Zone|seed|heal01") — ONE decal each, spot from seed, alpha
        /// fading as it heals; bandaged: zones dressed with a leaf bandage —
        /// their wound decals are REPLACED by one leaf-wrap decal; uncovered:
        /// zones with NO clothing — the only places skin decals may live;
        /// hygiene 1=clean; thermal &gt; 0 = hot.
        /// </summary>
        public void Sync(IReadOnlyList<string> wounds, HashSet<string> uncovered,
            float hygiene, float thermal, float rainWet = 0f,
            HashSet<string> bandaged = null, HashSet<string> gauzed = null)
        {
            if (_bones == null || _bodyRoot == null)
            {
                return;
            }

            _desired.Clear();

            // Spec 44: a dressed zone shows its wrap decal INSTEAD of its wound
            // marks (the shader-paint experiment was reverted — the projector
            // reads better on skin). Herbal dressings show the leaf wrap;
            // pre-made medkit dressings show a plain gauze wrap.
            if (bandaged != null)
            {
                foreach (var zone in bandaged)
                {
                    if (Zones.ContainsKey(zone) && uncovered.Contains(zone))
                    {
                        Want($"bandage.{zone}", DecalType.Bandage, zone, 271);
                    }
                }
            }

            if (gauzed != null)
            {
                foreach (var zone in gauzed)
                {
                    if (Zones.ContainsKey(zone) && uncovered.Contains(zone))
                    {
                        Want($"gauze.{zone}", DecalType.Gauze, zone, 271);
                    }
                }
            }

            // --- rain: droplets over the whole body, skin AND clothes ---
            var rain = Mathf.Clamp01(rainWet);
            var rainCount = rain < 0.1f ? 0 : Mathf.Min(RainSpread.Length, (int)(rain * RainSpread.Length + 0.5f));
            for (var i = 0; i < rainCount; i++)
            {
                Want($"rain.{i}", DecalType.Sweat, RainSpread[i], i * 53 + 5, alsoCloth: true);
            }

            // --- wounds: one decal per sim wound record ("Zone|seed|heal") —
            // NOT derived from zone HP (starving drains HP without wounds).
            // The seed picks the exact spot and the scratch/splat look; the
            // decal fades out as the wound closes.
            if (wounds != null)
            {
                foreach (var entry in wounds)
                {
                    var parts = entry.Split('|');
                    if (parts.Length < 3 || !Zones.ContainsKey(parts[0]) ||
                        !int.TryParse(parts[1], out var woundSeed) ||
                        !float.TryParse(parts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var heal))
                    {
                        continue;
                    }

                    if (!uncovered.Contains(parts[0]))
                    {
                        continue; // covered by clothing — hidden until undressed
                    }

                    if ((bandaged != null && bandaged.Contains(parts[0])) ||
                        (gauzed != null && gauzed.Contains(parts[0])))
                    {
                        continue; // dressed — the wrap (leaf or gauze) replaces the wound marks
                    }

                    var type = (woundSeed & 1) == 0 ? DecalType.Scratch : DecalType.Blood;
                    var key = $"wound.{woundSeed}";
                    Want(key, type, parts[0], woundSeed);

                    // Healing: the mark fades toward invisible as heal → 1.
                    if (_decals.TryGetValue(key, out var go) && go != null)
                    {
                        var projector = go.GetComponent<DecalProjector>();
                        if (projector != null)
                        {
                            projector.fadeFactor = Mathf.Clamp01(1f - heal);
                        }
                    }
                }
            }

            // --- dirt: ground dust climbing the body as hygiene drops ---
            var grime = Mathf.Clamp01(1f - hygiene);
            var dirtCount = Mathf.Min(DirtSpread.Length, (int)(grime * (DirtSpread.Length + 1)));
            for (var i = 0; i < dirtCount; i++)
            {
                if (uncovered.Contains(DirtSpread[i]))
                {
                    Want($"dirt.{i}", DecalType.Dirt, DirtSpread[i], i * 29 + 7);
                }
            }

            // --- sweat: droplet clusters bloom with heat ---
            var heat = Mathf.Clamp01(thermal / 0.6f);
            var sweatCount = heat < 0.15f ? 0 : Mathf.Min(SweatSpread.Length, (int)(heat * SweatSpread.Length + 0.5f));
            for (var i = 0; i < sweatCount; i++)
            {
                if (uncovered.Contains(SweatSpread[i]))
                {
                    Want($"sweat.{i}", DecalType.Sweat, SweatSpread[i], i * 41 + 11);
                }
            }

            // --- despawn what's no longer desired (healed/washed/cooled) ---
            _stale.Clear();
            foreach (var key in _decals.Keys)
            {
                if (!_desired.Contains(key))
                {
                    _stale.Add(key);
                }
            }

            foreach (var key in _stale)
            {
                if (_decals[key] != null)
                {
                    var projector = _decals[key].GetComponent<DecalProjector>();
                    if (projector != null)
                    {
                        _sweat.Remove(projector);
                    }

                    Destroy(_decals[key]);
                }

                _decals.Remove(key);
            }
        }

        private void Update()
        {
            // Sweat sheen breathes softly — reads as wet, not painted on.
            for (var i = 0; i < _sweat.Count; i++)
            {
                var projector = _sweat[i];
                if (projector != null)
                {
                    projector.fadeFactor = 0.85f + 0.15f * Mathf.Sin(Time.time * 2.2f + i * 1.9f);
                }
            }
        }

        private void Want(string key, DecalType type, string zoneName, int salt, bool alsoCloth = false)
        {
            _desired.Add(key);
            if (_decals.TryGetValue(key, out var existing) && existing != null)
            {
                return; // persists — never re-rolled while active
            }

            var zone = Zones[zoneName];
            var boneA = _bones!.GetBone(zone.BoneA);
            if (boneA == null)
            {
                return;
            }

            var boneB = string.IsNullOrEmpty(zone.BoneB) ? null : _bones.GetBone(zone.BoneB);

            // Deterministic placement: same npc + key → same spot, forever.
            var seed = (uint)(_npcId * 73856093 ^ key.GetHashCode() ^ salt * 19349663) | 1u;
            var t = 0.25f + NextRand(ref seed) * 0.5f;               // along the segment
            var azimuth = NextRand(ref seed) * Mathf.PI * 2f;        // around the limb
            var roll = NextRand(ref seed) * 360f;
            var sizeJitter = 0.85f + NextRand(ref seed) * 0.45f;

            var axis = boneB != null
                ? (boneB.position - boneA.position)
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

            // The projector floats just outside the limb, looking inward: the
            // decal is projected onto the skin and hugs the mesh curvature.
            var worldPos = anchor + radial * (radius * 1.6f);
            var worldRot = Quaternion.LookRotation(-radial, axisDir) * Quaternion.Euler(0f, 0f, roll);

            var size = type switch
            {
                DecalType.Scratch => 0.095f,
                DecalType.Blood => 0.075f,
                DecalType.Sweat => 0.070f, // droplet spray patch
                DecalType.Bandage => 0.110f, // leaf wrap covers the wound area
                DecalType.Gauze => 0.110f, // gauze wrap covers the wound area
                _ => 0.110f // dirt (0.150 read too loud on the thighs)
            } * _height * sizeJitter;

            var go = new GameObject($"Decal {key}");
            go.transform.SetParent(boneA, true);
            go.transform.position = worldPos;
            go.transform.rotation = worldRot;

            var projector = go.AddComponent<DecalProjector>();
            projector.material = GetDecalMaterial(type);
            // Depth reaches through the limb's front half so curved skin catches
            // the full image; it does NOT reach the far side (radius margin).
            projector.size = new Vector3(size, size, radius * 2.2f);
            projector.pivot = new Vector3(0f, 0f, radius * 1.1f);
            // Skin-only by default; rain droplets also hit the cloth layer.
            // Never the terrain (default layer bit 0 is excluded either way).
            projector.renderingLayerMask = alsoCloth
                ? SkinRenderingLayer | ClothRenderingLayer
                : SkinRenderingLayer;

            _decals[key] = go;
            if (type == DecalType.Sweat)
            {
                _sweat.Add(projector);
            }
        }

        // ---- materials / textures ----

        private static readonly Dictionary<DecalType, Material> _materials = new();

        // No-domain-reload runs keep this cache between plays — rebuilt so a
        // texture updated on disk (or imported mid-session) is picked up.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticMaterialCache()
        {
            _materials.Clear();
        }

        private static Material GetDecalMaterial(DecalType type)
        {
            if (_materials.TryGetValue(type, out var cached) && cached != null)
            {
                return cached;
            }

            // URP decal shader (needs the Decal renderer feature). If it's ever
            // missing the projector just renders nothing — no pink, no crash.
            var shader = Shader.Find("Shader Graphs/Decal");
            var material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"));

            // Wounds use fal.ai-generated textures (red-only, soft alpha edges —
            // pale baked-in "skin" was filtered out so they sit right on any
            // tan). Fallbacks: molly's blood_splash, then procedural.
            Texture? texture = type switch
            {
                DecalType.Scratch => Resources.Load<Texture2D>("HexLive/Decals/wound_scratch"),
                DecalType.Blood => Resources.Load<Texture2D>("HexLive/Decals/blood_splat"),
                // fal.ai droplets on black, luminance-keyed to alpha: bright
                // specular cores stay, background fully transparent — reads as
                // a glistening spray of sweat right on the skin.
                DecalType.Sweat => Resources.Load<Texture2D>("HexLive/Decals/sweat_drops"),
                // Spec 44: leaf poultice bound with fiber twine (fal.ai).
                DecalType.Bandage => Resources.Load<Texture2D>("HexLive/Decals/bandage_wrap"),
                // Spec 44: plain medkit gauze wrap (procedural fallback until a
                // gauze_wrap.png is dropped in — the loader then prefers it).
                DecalType.Gauze => Resources.Load<Texture2D>("HexLive/Decals/gauze_wrap"),
                // fal.ai granular dust on black, luminance-keyed: powder
                // grains + clumps like the logo's weathered grime — the old
                // procedural blobs read as flat paint.
                DecalType.Dirt => Resources.Load<Texture2D>("HexLive/Decals/dirt_dust"),
                _ => null
            };

            if (texture == null && (type == DecalType.Scratch || type == DecalType.Blood))
            {
                texture = Resources.Load<Texture2D>("HexLive/Decals/blood_splash");
            }

            texture ??= MakeTexture(type);

            // NOTE: the URP Decal shadergraph's texture property reference is
            // "Base_Map" (underscore style) — "_BaseMap" silently no-ops and the
            // decal renders as a plain white square. Set both to stay robust.
            material.SetTexture("Base_Map", texture);
            material.SetTexture("_BaseMap", texture);

            // Volumetric grains: the DBuffer runs Albedo+Normal now. Sweat
            // beads get a full-strength dome normal map (on wet-glossed skin
            // every bead catches its own sun glint); dirt crumbs get a softer
            // one (dry grain relief). Wounds/bandage explicitly blend NO
            // normal — they'd flatten the skin pores.
            var normalMap = type switch
            {
                DecalType.Sweat => Resources.Load<Texture2D>("HexLive/Decals/sweat_drops_n"),
                DecalType.Dirt => Resources.Load<Texture2D>("HexLive/Decals/dirt_dust_n"),
                _ => null
            };
            var normalBlend = normalMap == null ? 0f : type == DecalType.Sweat ? 1f : 0.7f;
            if (normalMap != null)
            {
                material.SetTexture("Normal_Map", normalMap);
                material.SetTexture("_NormalMap", normalMap);
            }

            material.SetFloat("Normal_Blend", normalBlend);
            material.SetFloat("_NormalBlend", normalBlend);
            material.SetFloat("_DecalNormalBlendFactor", normalBlend);

            _materials[type] = material;
            return material;
        }

        private static Texture2D MakeTexture(DecalType type)
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4
            };
            var px = new Color[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size - 0.5f; // -0.5..0.5
                    var v = (y + 0.5f) / size - 0.5f;
                    px[y * size + x] = type switch
                    {
                        DecalType.Scratch => ScratchPixel(u, v),
                        DecalType.Blood => BloodPixel(u, v),
                        DecalType.Dirt => DirtPixel(u, v),
                        DecalType.Bandage => BandagePixel(u, v),
                        DecalType.Gauze => GauzePixel(u, v),
                        _ => SweatPixel(u, v)
                    };
                }
            }

            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        // Claw wound: 3 ragged parallel gashes — near-black dried core, raw red
        // flesh at the lips, and an inflamed pink halo fading into the skin.
        private static Color ScratchPixel(float u, float v)
        {
            var a = 0f;
            var core = 0f;
            for (var i = -1; i <= 1; i++)
            {
                var jag = 0.045f * Mathf.Sin(u * 23f + i * 4f) + 0.02f * Mathf.Sin(u * 57f + i * 9f);
                var lane = v - i * 0.15f + jag;
                // Width varies along the cut — thick center, tapering tips.
                var width = 0.030f * (1f - Mathf.SmoothStep(0.18f, 0.48f, Mathf.Abs(u))) + 0.004f;
                var d = Mathf.Abs(lane) / width;
                var body = Mathf.Clamp01(1.4f - d);
                a = Mathf.Max(a, body);
                core = Mathf.Max(core, Mathf.Clamp01(1f - d * 1.6f));
            }

            // Inflamed halo around all cuts.
            var halo = Mathf.Clamp01(a * 1.6f) * 0.30f;
            var col = Color.Lerp(new Color(0.72f, 0.14f, 0.10f), new Color(0.22f, 0.015f, 0.01f), core);
            var alpha = Mathf.Clamp01(Mathf.Max(a * 0.95f, halo));
            if (a < 0.05f)
            {
                col = new Color(0.85f, 0.30f, 0.24f); // halo tint
            }

            return new Color(col.r, col.g, col.b, alpha);
        }

        // Blood splatter: a ragged main pool with darker dried center + a spray
        // of satellite droplets flung outward.
        private static Color BloodPixel(float u, float v)
        {
            var angle = Mathf.Atan2(v, u);
            var r = Mathf.Sqrt(u * u + v * v);
            var rag = 0.24f + 0.06f * Mathf.Sin(angle * 6f + 1.3f)
                            + 0.035f * Mathf.Sin(angle * 13f + 4.1f);
            var pool = Mathf.Clamp01((rag - r) / 0.05f);

            // Satellite droplets on stable pseudo-random rays.
            var drops = 0f;
            for (var i = 0; i < 9; i++)
            {
                var da = i * 2.399f + 0.7f; // golden-angle spread
                var dr = 0.28f + 0.16f * Frac(Mathf.Sin(i * 12.9898f) * 43758.5453f);
                var dx = u - Mathf.Cos(da) * dr;
                var dy = v - Mathf.Sin(da) * dr;
                var ds = 0.012f + 0.02f * Frac(Mathf.Sin(i * 78.233f) * 12543.123f);
                drops = Mathf.Max(drops, Mathf.Clamp01((ds - Mathf.Sqrt(dx * dx + dy * dy)) / (ds * 0.5f)));
            }

            var alpha = Mathf.Max(pool, drops * 0.9f);
            var heart = Mathf.Clamp01((0.13f - r) / 0.13f);
            var col = Color.Lerp(new Color(0.52f, 0.05f, 0.04f), new Color(0.24f, 0.01f, 0.01f), heart);
            return new Color(col.r, col.g, col.b, alpha * 0.95f);
        }

        // Dirt: layered earthy grime — big soft patch, darker mud speckles,
        // fine dust grain; clearly texture, not a flat tint.
        private static Color DirtPixel(float u, float v)
        {
            var r = Mathf.Sqrt(u * u + v * v);
            var macro = Mathf.PerlinNoise(u * 6f + 11f, v * 6f + 47f);
            var meso = Mathf.PerlinNoise(u * 16f + 3f, v * 16f + 29f);
            var grain = Mathf.PerlinNoise(u * 48f + 7f, v * 48f + 13f);

            var mask = Mathf.Clamp01((0.47f - r) / 0.22f);
            mask *= Mathf.SmoothStep(0.30f, 0.62f, macro * 0.6f + meso * 0.4f);
            var speck = Mathf.SmoothStep(0.62f, 0.82f, meso) * 0.5f;

            // Sandy earth, not soot: dry dust reads light-brown on skin.
            var col = Color.Lerp(new Color(0.62f, 0.53f, 0.38f), new Color(0.44f, 0.36f, 0.25f),
                Mathf.Clamp01(speck + grain * 0.3f));
            var alpha = Mathf.Clamp01(mask * (0.55f + grain * 0.35f) + speck * mask);
            return new Color(col.r, col.g, col.b, alpha * 0.7f);
        }

        // Sweat: a cluster of beads with thin run-down streaks — bright specular
        // cores with a cool translucent film between them.
        private static Color SweatPixel(float u, float v)
        {
            var beads = 0f;
            var highlight = 0f;
            for (var i = 0; i < 7; i++)
            {
                var bx = (Frac(Mathf.Sin(i * 12.9898f) * 43758.5453f) - 0.5f) * 0.62f;
                var by = (Frac(Mathf.Sin(i * 39.346f) * 11631.844f) - 0.5f) * 0.62f;
                var bs = 0.022f + 0.03f * Frac(Mathf.Sin(i * 63.726f) * 28001.83f);
                var dx = u - bx;
                var dy = v - by;

                // Teardrop: slightly stretched downward + a streak trailing up.
                var d = Mathf.Sqrt(dx * dx * 1.7f + dy * dy);
                beads = Mathf.Max(beads, Mathf.Clamp01((bs - d) / (bs * 0.5f)));
                var streak = Mathf.Clamp01((0.006f - Mathf.Abs(dx)) / 0.006f)
                             * Mathf.Clamp01((dy) / 0.16f) * Mathf.Clamp01(1f - dy / 0.22f);
                beads = Mathf.Max(beads, streak * 0.5f);

                var hx = dx - bs * 0.25f;
                var hy = dy - bs * 0.3f;
                highlight = Mathf.Max(highlight,
                    Mathf.Clamp01((bs * 0.35f - Mathf.Sqrt(hx * hx + hy * hy)) / (bs * 0.35f)));
            }

            var col = Color.Lerp(new Color(0.72f, 0.82f, 0.92f), Color.white, highlight);
            var alpha = Mathf.Clamp01(beads * 0.45f + highlight * 0.5f);
            return new Color(col.r, col.g, col.b, alpha);
        }

        // Fallback leaf wrap: overlapping green leaf pads + crossed tan twine.
        private static Color BandagePixel(float u, float v)
        {
            var r = Mathf.Sqrt(u * u + v * v);
            var pad = Mathf.Clamp01((0.40f - r) / 0.08f);
            var leafTone = 0.5f + 0.5f * Mathf.Sin(u * 21f + v * 9f);
            var col = Color.Lerp(new Color(0.30f, 0.52f, 0.26f), new Color(0.42f, 0.64f, 0.32f), leafTone);

            // Crossed fiber twine bands.
            var band1 = Mathf.Clamp01((0.05f - Mathf.Abs(u + v * 0.3f)) / 0.02f);
            var band2 = Mathf.Clamp01((0.05f - Mathf.Abs(v - u * 0.3f)) / 0.02f);
            var twine = Mathf.Max(band1, band2) * pad;
            col = Color.Lerp(col, new Color(0.76f, 0.62f, 0.42f), twine);

            return new Color(col.r, col.g, col.b, pad * 0.95f);
        }

        // Fallback medkit gauze: a pale off-white cloth pad with a CRISP woven
        // mesh (warp + weft threads with small holes) and two crossed fabric
        // bands — no green, no leaves (this is the pre-made bandage, not
        // gathered plantain).
        private static Color GauzePixel(float u, float v)
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

            var band1 = Mathf.Clamp01((0.05f - Mathf.Abs(u + v * 0.3f)) / 0.012f);
            var band2 = Mathf.Clamp01((0.05f - Mathf.Abs(v - u * 0.3f)) / 0.012f);
            var band = Mathf.Max(band1, band2) * pad;
            col = Color.Lerp(col, new Color(0.80f, 0.76f, 0.68f), band * 0.5f);

            var alpha = pad * Mathf.Lerp(0.97f, 0.6f, hole);
            return new Color(col.r, col.g, col.b, alpha);
        }

        private static float Frac(float x) => x - Mathf.Floor(x);

        private static float NextRand(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f;
        }
    }
}
